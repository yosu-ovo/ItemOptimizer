using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Items.Components;
using FarseerPhysics;
using HarmonyLib;
using ItemOptimizerMod.World;
using Microsoft.Xna.Framework;

namespace ItemOptimizerMod.Patches
{
    /// <summary>
    /// Complete replacement for MotionSensor.Update() via Harmony Prefix.
    /// Merges MotionSensorPatch (frame-skip) + MotionSensorOpt (.Any() elimination)
    /// into a single zero-alloc prefix with optimized detection order:
    ///   broad-phase distance check BEFORE type filtering (vanilla does it backwards).
    ///
    /// iter-21: Hull-based spatial pre-filtering (Layer 0 + Layer 1).
    ///   Layer 0: submarine-level culling — skip characters on distant submarines.
    ///   Layer 1: hull partition — only check characters in hulls overlapping sensor range.
    ///   Falls back to full CharacterList scan when EnableHullSpatialIndex is off.
    /// </summary>
    static class MotionSensorRewrite
    {
        // ── FieldRef accessors (zero-reflection, zero-boxing) ──
        private static readonly AccessTools.FieldRef<MotionSensor, float> Ref_rangeX =
            AccessTools.FieldRefAccess<MotionSensor, float>("rangeX");
        private static readonly AccessTools.FieldRef<MotionSensor, float> Ref_rangeY =
            AccessTools.FieldRefAccess<MotionSensor, float>("rangeY");
        private static readonly AccessTools.FieldRef<MotionSensor, float> Ref_updateTimer =
            AccessTools.FieldRefAccess<MotionSensor, float>("updateTimer");
        private static readonly AccessTools.FieldRef<MotionSensor, HashSet<Identifier>> Ref_targetCharacters =
            AccessTools.FieldRefAccess<MotionSensor, HashSet<Identifier>>("targetCharacters");
        private static readonly AccessTools.FieldRef<MotionSensor, bool> Ref_triggerFromHumans =
            AccessTools.FieldRefAccess<MotionSensor, bool>("triggerFromHumans");
        private static readonly AccessTools.FieldRef<MotionSensor, bool> Ref_triggerFromPets =
            AccessTools.FieldRefAccess<MotionSensor, bool>("triggerFromPets");
        private static readonly AccessTools.FieldRef<MotionSensor, bool> Ref_triggerFromMonsters =
            AccessTools.FieldRefAccess<MotionSensor, bool>("triggerFromMonsters");
        private static readonly AccessTools.FieldRef<MotionSensor, Vector2> Ref_detectOffset =
            AccessTools.FieldRefAccess<MotionSensor, Vector2>("detectOffset");

        // ── Per-instance cached Connection (flat array indexed by item.ID) ──
        private static readonly Connection[] CachedStateOut = new Connection[65536];

        // ── Frame counter for throttle (flat array, no CWT) ──
        private static readonly int[] FrameCounters = new int[65536];

        // ── NativeRuntime bypass: sensors managed by NativeRuntime skip Prefix entirely ──
        internal static readonly bool[] IsNativeManaged = new bool[65536];

        // ── Spatial cache per sensor: which hulls does this sensor's broadRange cover? ──
        private struct SensorSpatialCache
        {
            public int[] CoveredHullIds;   // hull entity IDs on sensor's sub that intersect broadRange
            public Submarine SensorSub;     // sensor's submarine (null if not on any sub)
            public bool Resolved;
        }
        private static readonly SensorSpatialCache[] _spatialCache = new SensorSpatialCache[65536];

        private static MethodInfo _originalMethod;
        private static HarmonyMethod _prefixMethod;
        internal static bool IsRegistered { get; private set; }

        /// <summary>Trace mode: when > 0, log detection details for each sensor update. Decremented per frame.</summary>
        internal static int TraceFrames;
        /// <summary>When >= 0, only trace this specific sensor ID. -1 = trace all.</summary>
        internal static int TraceTargetId = -1;

        internal static void Register(Harmony harmony)
        {
            _originalMethod = AccessTools.Method(typeof(MotionSensor), nameof(MotionSensor.Update));
            if (_originalMethod == null)
            {
                LuaCsLogger.LogError("[ItemOptimizer] MotionSensorRewrite: could not find MotionSensor.Update");
                return;
            }

            _prefixMethod = new HarmonyMethod(AccessTools.Method(typeof(MotionSensorRewrite), nameof(Prefix)));
            harmony.Patch(_originalMethod, prefix: _prefixMethod);
            IsRegistered = true;
            LuaCsLogger.Log("[ItemOptimizer] MotionSensorRewrite registered");
        }

        internal static void Unregister(Harmony harmony)
        {
            if (_originalMethod != null && _prefixMethod != null)
                harmony.Unpatch(_originalMethod, _prefixMethod.method);
            IsRegistered = false;
        }

        /// <summary>Reset cached state (called on round end / mod reload).</summary>
        internal static void Reset()
        {
            Array.Clear(CachedStateOut, 0, CachedStateOut.Length);
            Array.Clear(FrameCounters, 0, FrameCounters.Length);
            Array.Clear(_spatialCache, 0, _spatialCache.Length);
            Array.Clear(IsNativeManaged, 0, IsNativeManaged.Length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static Connection GetStateOutConnection(MotionSensor ms)
        {
            int id = ms.item.ID;
            var conn = CachedStateOut[id];
            if (conn != null) return conn;

            var connections = ms.item.Connections;
            if (connections == null) return null;
            foreach (var c in connections)
            {
                if (c.Name == "state_out")
                {
                    CachedStateOut[id] = c;
                    return c;
                }
            }
            return null;
        }

        // ── Spatial cache resolution ──

        /// <summary>
        /// Compute which hulls a sensor's broadRange rectangle overlaps.
        /// Called once per sensor per round (sensor is fixed on the submarine).
        /// Uses local (submarine-relative) coordinates for same-sub hulls so the
        /// cache remains valid as the submarine moves through the world.
        /// </summary>
        private static void ResolveSpatialCache(ref SensorSpatialCache cache, MotionSensor ms)
        {
            var item = ms.item;
            float rangeX = Ref_rangeX(ms);
            float rangeY = Ref_rangeY(ms);
            float broadRangeX = Math.Max(rangeX * 2, 500);
            float broadRangeY = Math.Max(rangeY * 2, 500);

            // Sensor detect center in LOCAL coordinates (relative to its submarine).
            // Must use Rect-based center, NOT item.Position — the latter returns
            // body.Position (world coords) when a physics body exists.
            var r = item.Rect;
            Vector2 localDetectPos = new Vector2(
                r.X + r.Width / 2.0f,
                r.Y - r.Height / 2.0f) + ms.TransformedDetectOffset;

            var sensorSub = item.Submarine;

            // Broad AABB in local space
            float left   = localDetectPos.X - broadRangeX;
            float right  = localDetectPos.X + broadRangeX;
            float bottom = localDetectPos.Y - broadRangeY;
            float top    = localDetectPos.Y + broadRangeY;

            // Find all hulls on the same submarine that intersect this AABB
            var hullIds = new List<int>(16);
            foreach (Hull hull in Hull.HullList)
            {
                // Only match hulls on the same submarine (local coords only valid for same sub)
                if (hull.Submarine != sensorSub) continue;

                // Hull.Rect is in local coordinates (submarine-relative)
                var hr = hull.Rect;
                float hLeft   = hr.X;
                float hRight  = hr.X + hr.Width;
                float hTop    = hr.Y;
                float hBottom = hr.Y - hr.Height;

                // AABB overlap test
                if (hRight < left || hLeft > right || hTop < bottom || hBottom > top)
                    continue;

                hullIds.Add(hull.ID);
            }

            cache.CoveredHullIds = hullIds.ToArray();
            cache.SensorSub = sensorSub;

            cache.Resolved = true;
        }

        // ── Character detection helpers (shared by Prefix and QuickDetect) ──

        /// <summary>
        /// Iterate characters from a candidate list through the full detection pipeline.
        /// Returns true and sets MotionDetected if any character's limb triggers.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool CheckCandidates(
            List<Character> candidates, MotionSensor sensor,
            Vector2 detectPos, float broadRangeX, float broadRangeY,
            Rectangle detectRect, float minVelSq,
            bool trigHumans, bool trigPets, bool trigMonsters,
            HashSet<Identifier> targetChars)
        {
            for (int j = 0; j < candidates.Count; j++)
            {
                Character character = candidates[j];

                // Broad-phase distance check
                if (Math.Abs(character.WorldPosition.X - detectPos.X) > broadRangeX ||
                    Math.Abs(character.WorldPosition.Y - detectPos.Y) > broadRangeY)
                    continue;

                // Spawn time filter
                if (character.SpawnTime > Timing.TotalTime - 1.0) continue;

                // Dead check
                if (sensor.IgnoreDead && character.IsDead) continue;

                // Type filter (inlined TriggersOn)
                if (character.IsPet)
                {
                    if (!trigPets) continue;
                }
                else if (character.IsHuman || CharacterParams.CompareGroup(character.Group, CharacterPrefab.HumanGroup))
                {
                    if (!trigHumans) continue;
                }
                else
                {
                    if (!trigMonsters) continue;
                }

                // Target character filter
                if (targetChars.Count > 0)
                {
                    bool matchFound = false;
                    foreach (Identifier target in targetChars)
                    {
                        if (character.MatchesSpeciesNameOrGroup(target) || character.Params.HasTag(target))
                        {
                            matchFound = true;
                            break;
                        }
                    }
                    if (!matchFound) continue;
                }

                // Per-limb narrow phase
                foreach (Limb limb in character.AnimController.Limbs)
                {
                    if (limb.IsSevered) continue;
                    if (limb.LinearVelocity.LengthSquared() < minVelSq) continue;
                    if (MathUtils.CircleIntersectsRectangle(limb.WorldPosition,
                        ConvertUnits.ToDisplayUnits(limb.body.GetMaxExtent()), detectRect))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Quick-detect version: center-point check with expanded range, no per-limb.
        /// Returns true if any candidate character is moving within range.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool QuickCheckCandidates(
            List<Character> candidates, MotionSensor sensor,
            Vector2 detectPos, float rxExpanded, float ryExpanded, float minVelSq,
            bool trigHumans, bool trigPets, bool trigMonsters,
            HashSet<Identifier> targetChars)
        {
            for (int j = 0; j < candidates.Count; j++)
            {
                Character character = candidates[j];

                float dx = Math.Abs(character.WorldPosition.X - detectPos.X);
                float dy = Math.Abs(character.WorldPosition.Y - detectPos.Y);
                if (dx > rxExpanded || dy > ryExpanded) continue;

                if (sensor.IgnoreDead && character.IsDead) continue;
                if (!character.Enabled) continue;

                // Inline type check
                if (character.IsPet)
                {
                    if (!trigPets) continue;
                }
                else if (character.IsHuman || CharacterParams.CompareGroup(character.Group, CharacterPrefab.HumanGroup))
                {
                    if (!trigHumans) continue;
                }
                else
                {
                    if (!trigMonsters) continue;
                }

                // Target filter
                if (targetChars.Count > 0)
                {
                    bool matchFound = false;
                    foreach (Identifier target in targetChars)
                    {
                        if (character.MatchesSpeciesNameOrGroup(target) || character.Params.HasTag(target))
                        {
                            matchFound = true;
                            break;
                        }
                    }
                    if (!matchFound) continue;
                }

                // Velocity check
                if (character.AnimController?.Collider != null)
                {
                    var vel = character.AnimController.Collider.LinearVelocity;
                    if (vel.LengthSquared() >= minVelSq)
                        return true;
                }
            }
            return false;
        }

        /// <summary>Diagnostic: return spatial cache info for a sensor item ID.</summary>
        internal static (bool resolved, int[] hullIds, string subName) GetSpatialCacheInfo(int itemId)
        {
            if (itemId < 0 || itemId >= _spatialCache.Length)
                return (false, Array.Empty<int>(), "?");
            ref var sc = ref _spatialCache[itemId];
            return (sc.Resolved, sc.CoveredHullIds ?? Array.Empty<int>(), sc.SensorSub?.Info?.Name ?? "null");
        }

        /// <summary>
        /// Complete replacement for MotionSensor.Update().
        /// Returns false to skip the original method.
        /// </summary>
        public static bool Prefix(MotionSensor __instance, float deltaTime)
        {
            // NativeRuntime bypass — when managed by NativeRuntime, skip Prefix entirely.
            // NativeRuntime has its own independent tick hook (postfix on MapEntity.UpdateAll)
            // that ensures Tick() runs even when UpdateAllTakeover is OFF.
            if (IsNativeManaged[__instance.item.ID])
            {
                if (NativeRuntimeBridge.IsEnabled) return false;
                // Runtime disabled mid-round — fall through to rewrite/vanilla
            }

            if (!OptimizerConfig.EnableMotionSensorRewrite)
            {
                if (TraceFrames > 0 && (TraceTargetId < 0 || TraceTargetId == __instance.item.ID))
                {
                    TraceFrames--;
                    DebugConsole.NewMessage($"[TRACE] Rewrite OFF — vanilla will run for sensor ID={__instance.item.ID}", Color.Orange);
                }
                return true;
            }

            var item = __instance.item;
            int id = item.ID;
            bool tracing = TraceFrames > 0 && (TraceTargetId < 0 || TraceTargetId == id);

            // ── 1. Every frame: send last detection result (maintains signal continuity) ──
            string signalOut = __instance.MotionDetected ? __instance.Output : __instance.FalseOutput;
            if (!string.IsNullOrEmpty(signalOut))
            {
                var conn = GetStateOutConnection(__instance);
                if (conn != null)
                    item.SendSignal(new Signal(signalOut, 1), conn);
                else
                    item.SendSignal(new Signal(signalOut, 1), "state_out");
            }

            if (__instance.MotionDetected)
            {
                __instance.ApplyStatusEffects(ActionType.OnUse, deltaTime);
            }

            // ── 2. Timer-gated scan (vanilla updateTimer logic) ──
            ref float updateTimer = ref Ref_updateTimer(__instance);
            updateTimer -= deltaTime;

            if (updateTimer > 0.0f)
            {
                // On skipped timer frames, do lightweight center-point check
                FrameCounters[id]++;
                if (FrameCounters[id] % OptimizerConfig.MotionSensorSkipFrames != 0)
                {
                    if (tracing)
                    {
                        TraceFrames--;
                        DebugConsole.NewMessage(
                            $"[TRACE] ID={id} SKIP timer={updateTimer:F3} dt={deltaTime:F4} detected={__instance.MotionDetected}",
                            Color.Gray);
                    }
                    Stats.MotionSensorSkips++;
                    return false;
                }

                // Quick detect with spatial filtering
                bool quickHit = QuickDetect(__instance);
                if (quickHit)
                    __instance.MotionDetected = true;

                if (tracing)
                {
                    TraceFrames--;
                    DebugConsole.NewMessage(
                        $"[TRACE] ID={id} QUICK timer={updateTimer:F3} dt={deltaTime:F4} quickHit={quickHit} detected={__instance.MotionDetected}",
                        quickHit ? Color.LimeGreen : Color.Yellow);
                }

                Stats.MotionSensorSkips++;
                return false;
            }

            // ── 3. Full scan frame ──
            __instance.MotionDetected = false;
            updateTimer = __instance.UpdateInterval;
            FrameCounters[id] = 0;

            // 3a. Own motion detection (cheap)
            if (item.body != null && item.body.Enabled && __instance.DetectOwnMotion)
            {
                if (Math.Abs(item.body.LinearVelocity.X) > __instance.MinimumVelocity ||
                    Math.Abs(item.body.LinearVelocity.Y) > __instance.MinimumVelocity)
                {
                    __instance.MotionDetected = true;
                    return false;
                }
            }

            float rangeX = Ref_rangeX(__instance);
            float rangeY = Ref_rangeY(__instance);
            Vector2 detectPos = item.WorldPosition + __instance.TransformedDetectOffset;
            Rectangle detectRect = new Rectangle(
                (int)(detectPos.X - rangeX), (int)(detectPos.Y - rangeY),
                (int)(rangeX * 2), (int)(rangeY * 2));
            float broadRangeX = Math.Max(rangeX * 2, 500);
            float broadRangeY = Math.Max(rangeY * 2, 500);

            // 3b. Wall detection (rare path — only for subs with no hull + Wall target)
            if (item.CurrentHull == null && item.Submarine != null && __instance.Target.HasFlag(MotionSensor.TargetType.Wall))
            {
                if (Level.Loaded != null &&
                    (Math.Abs(item.Submarine.Velocity.X) > __instance.MinimumVelocity ||
                     Math.Abs(item.Submarine.Velocity.Y) > __instance.MinimumVelocity))
                {
                    var cells = Level.Loaded.GetCells(item.WorldPosition, 1);
                    foreach (var cell in cells)
                    {
                        if (cell.IsPointInside(item.WorldPosition))
                        {
                            __instance.MotionDetected = true;
                            return false;
                        }
                        foreach (var edge in cell.Edges)
                        {
                            Vector2 e1 = edge.Point1 + cell.Translation;
                            Vector2 e2 = edge.Point2 + cell.Translation;
                            if (MathUtils.LineSegmentsIntersect(e1, e2, new Vector2(detectRect.X, detectRect.Y), new Vector2(detectRect.Right, detectRect.Y)) ||
                                MathUtils.LineSegmentsIntersect(e1, e2, new Vector2(detectRect.X, detectRect.Bottom), new Vector2(detectRect.Right, detectRect.Bottom)) ||
                                MathUtils.LineSegmentsIntersect(e1, e2, new Vector2(detectRect.X, detectRect.Y), new Vector2(detectRect.X, detectRect.Bottom)) ||
                                MathUtils.LineSegmentsIntersect(e1, e2, new Vector2(detectRect.Right, detectRect.Y), new Vector2(detectRect.Right, detectRect.Bottom)))
                            {
                                __instance.MotionDetected = true;
                                return false;
                            }
                        }
                    }
                }
                foreach (Submarine sub in Submarine.Loaded)
                {
                    if (sub == item.Submarine) continue;

                    Vector2 relativeVelocity = item.Submarine.Velocity - sub.Velocity;
                    if (Math.Abs(relativeVelocity.X) < __instance.MinimumVelocity &&
                        Math.Abs(relativeVelocity.Y) < __instance.MinimumVelocity)
                        continue;

                    Rectangle worldBorders = new Rectangle(
                        sub.Borders.X + (int)sub.WorldPosition.X,
                        sub.Borders.Y + (int)sub.WorldPosition.Y - sub.Borders.Height,
                        sub.Borders.Width,
                        sub.Borders.Height);

                    if (worldBorders.Intersects(detectRect))
                    {
                        foreach (Structure wall in Structure.WallList)
                        {
                            if (wall.Submarine == sub && wall.WorldRect.Intersects(detectRect))
                            {
                                __instance.MotionDetected = true;
                                return false;
                            }
                        }
                    }
                }
            }

            // 3c. Character detection
            bool trigHumans = Ref_triggerFromHumans(__instance);
            bool trigPets = Ref_triggerFromPets(__instance);
            bool trigMonsters = Ref_triggerFromMonsters(__instance);
            bool hasTriggers = trigHumans || trigPets || trigMonsters;
            if (!hasTriggers) return false;

            var targetChars = Ref_targetCharacters(__instance);
            float minVelSq = __instance.MinimumVelocity * __instance.MinimumVelocity;

            // ── Hull spatial filtering (Layer 0 + Layer 1) ──
            // HullCharacterTracker.Rebuild() is guaranteed fresh:
            // - When iotakeover ON: dispatch loop calls Rebuild() before item dispatch
            // - When iotakeover OFF + ionative ON: NativeRuntime postfix calls Rebuild()
            // - When iotakeover OFF + ionative OFF: Rewrite runs from vanilla Update,
            //   tracker may be stale — fall back to full scan
            if (OptimizerConfig.EnableHullSpatialIndex
                && (UpdateAllTakeover.Enabled || NativeRuntimeBridge.IsEnabled))
            {
                ref var sc = ref _spatialCache[id];
                if (!sc.Resolved)
                    ResolveSpatialCache(ref sc, __instance);

                // Layer 1: Check characters in same-sub covered hulls
                var coveredIds = sc.CoveredHullIds;
                for (int i = 0; i < coveredIds.Length; i++)
                {
                    var candidates = HullCharacterTracker.GetCharactersInHull(coveredIds[i]);
                    if (candidates.Count > 0 && CheckCandidates(candidates, __instance,
                        detectPos, broadRangeX, broadRangeY, detectRect, minVelSq,
                        trigHumans, trigPets, trigMonsters, targetChars))
                    {
                        __instance.MotionDetected = true;
                        return false;
                    }
                }

                // Layer 0: Check characters NOT on the sensor's submarine
                // (no-hull characters + characters on other subs e.g. docked vessels)
                // Use broadRange distance check to quickly skip distant ones.
                var charList = Character.CharacterList;
                for (int i = 0; i < charList.Count; i++)
                {
                    Character character = charList[i];
                    Hull cHull = character.CurrentHull;

                    // Skip characters already covered by hull-based check above
                    if (cHull != null && cHull.Submarine == sc.SensorSub)
                        continue;

                    // Broad-phase distance check (world coordinates)
                    if (Math.Abs(character.WorldPosition.X - detectPos.X) > broadRangeX ||
                        Math.Abs(character.WorldPosition.Y - detectPos.Y) > broadRangeY)
                        continue;

                    if (character.Removed) continue;
                    if (character.SpawnTime > Timing.TotalTime - 1.0) continue;
                    if (__instance.IgnoreDead && character.IsDead) continue;

                    if (character.IsPet)
                    {
                        if (!trigPets) continue;
                    }
                    else if (character.IsHuman || CharacterParams.CompareGroup(character.Group, CharacterPrefab.HumanGroup))
                    {
                        if (!trigHumans) continue;
                    }
                    else
                    {
                        if (!trigMonsters) continue;
                    }

                    if (targetChars.Count > 0)
                    {
                        bool matchFound = false;
                        foreach (Identifier target in targetChars)
                        {
                            if (character.MatchesSpeciesNameOrGroup(target) || character.Params.HasTag(target))
                            {
                                matchFound = true;
                                break;
                            }
                        }
                        if (!matchFound) continue;
                    }

                    foreach (Limb limb in character.AnimController.Limbs)
                    {
                        if (limb.IsSevered) continue;
                        if (limb.LinearVelocity.LengthSquared() < minVelSq) continue;
                        if (MathUtils.CircleIntersectsRectangle(limb.WorldPosition,
                            ConvertUnits.ToDisplayUnits(limb.body.GetMaxExtent()), detectRect))
                        {
                            __instance.MotionDetected = true;
                            return false;
                        }
                    }
                }
            }
            else
            {
                // Fallback: original full CharacterList scan
                foreach (Character character in Character.CharacterList)
                {
                    if (Math.Abs(character.WorldPosition.X - detectPos.X) > broadRangeX ||
                        Math.Abs(character.WorldPosition.Y - detectPos.Y) > broadRangeY)
                        continue;

                    if (character.SpawnTime > Timing.TotalTime - 1.0) continue;
                    if (__instance.IgnoreDead && character.IsDead) continue;

                    if (character.IsPet)
                    {
                        if (!trigPets) continue;
                    }
                    else if (character.IsHuman || CharacterParams.CompareGroup(character.Group, CharacterPrefab.HumanGroup))
                    {
                        if (!trigHumans) continue;
                    }
                    else
                    {
                        if (!trigMonsters) continue;
                    }

                    if (targetChars.Count > 0)
                    {
                        bool matchFound = false;
                        foreach (Identifier target in targetChars)
                        {
                            if (character.MatchesSpeciesNameOrGroup(target) || character.Params.HasTag(target))
                            {
                                matchFound = true;
                                break;
                            }
                        }
                        if (!matchFound) continue;
                    }

                    foreach (Limb limb in character.AnimController.Limbs)
                    {
                        if (limb.IsSevered) continue;
                        if (limb.LinearVelocity.LengthSquared() < minVelSq) continue;
                        if (MathUtils.CircleIntersectsRectangle(limb.WorldPosition,
                            ConvertUnits.ToDisplayUnits(limb.body.GetMaxExtent()), detectRect))
                        {
                            __instance.MotionDetected = true;
                            return false;
                        }
                    }
                }
            }

            if (tracing)
            {
                TraceFrames--;
                var charList2 = Character.CharacterList;
                int nearbyCount = 0;
                for (int ci = 0; ci < charList2.Count; ci++)
                {
                    float dx = Math.Abs(charList2[ci].WorldPosition.X - (item.WorldPosition.X + __instance.TransformedDetectOffset.X));
                    float dy = Math.Abs(charList2[ci].WorldPosition.Y - (item.WorldPosition.Y + __instance.TransformedDetectOffset.Y));
                    if (dx < 1000 && dy < 1000) nearbyCount++;
                }
                DebugConsole.NewMessage(
                    $"[TRACE] Sensor ID={id} fullScan detected={__instance.MotionDetected} " +
                    $"spatial={OptimizerConfig.EnableHullSpatialIndex} nearby={nearbyCount} " +
                    $"minVel={__instance.MinimumVelocity:F2}",
                    __instance.MotionDetected ? Color.LimeGreen : Color.Red);
            }

            return false;
        }

        /// <summary>
        /// Lightweight center-point check for timer-skipped frames.
        /// Uses expanded range (1.5x) to compensate for not checking limb extents.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool QuickDetect(MotionSensor sensor)
        {
            float rx = Ref_rangeX(sensor);
            float ry = Ref_rangeY(sensor);
            Vector2 detectPos = sensor.item.WorldPosition + sensor.TransformedDetectOffset;
            float minVelSq = sensor.MinimumVelocity * sensor.MinimumVelocity;
            float rxExpanded = rx * 1.5f;
            float ryExpanded = ry * 1.5f;

            bool trigHumans = Ref_triggerFromHumans(sensor);
            bool trigPets = Ref_triggerFromPets(sensor);
            bool trigMonsters = Ref_triggerFromMonsters(sensor);
            var targetChars = Ref_targetCharacters(sensor);

            // ── Hull spatial filtering ──
            // HullCharacterTracker is fresh when iotakeover OR ionative is ON.
            if (OptimizerConfig.EnableHullSpatialIndex
                && (UpdateAllTakeover.Enabled || NativeRuntimeBridge.IsEnabled))
            {
                int id = sensor.item.ID;
                ref var sc = ref _spatialCache[id];
                if (!sc.Resolved)
                    ResolveSpatialCache(ref sc, sensor);

                // Layer 1: same-sub hull characters
                var coveredIds = sc.CoveredHullIds;
                for (int i = 0; i < coveredIds.Length; i++)
                {
                    var candidates = HullCharacterTracker.GetCharactersInHull(coveredIds[i]);
                    if (candidates.Count > 0 && QuickCheckCandidates(candidates, sensor,
                        detectPos, rxExpanded, ryExpanded, minVelSq,
                        trigHumans, trigPets, trigMonsters, targetChars))
                    {
                        return true;
                    }
                }

                // Layer 0: characters NOT on the sensor's submarine
                var charList = Character.CharacterList;
                for (int ci = 0; ci < charList.Count; ci++)
                {
                    Character character = charList[ci];
                    Hull cHull = character.CurrentHull;
                    if (cHull != null && cHull.Submarine == sc.SensorSub)
                        continue;

                    float dx = Math.Abs(character.WorldPosition.X - detectPos.X);
                    float dy = Math.Abs(character.WorldPosition.Y - detectPos.Y);
                    if (dx > rxExpanded || dy > ryExpanded) continue;

                    if (sensor.IgnoreDead && character.IsDead) continue;
                    if (!character.Enabled) continue;

                    if (character.IsPet)
                    {
                        if (!trigPets) continue;
                    }
                    else if (character.IsHuman || CharacterParams.CompareGroup(character.Group, CharacterPrefab.HumanGroup))
                    {
                        if (!trigHumans) continue;
                    }
                    else
                    {
                        if (!trigMonsters) continue;
                    }

                    if (targetChars.Count > 0)
                    {
                        bool matchFound = false;
                        foreach (Identifier target in targetChars)
                        {
                            if (character.MatchesSpeciesNameOrGroup(target) || character.Params.HasTag(target))
                            {
                                matchFound = true;
                                break;
                            }
                        }
                        if (!matchFound) continue;
                    }

                    if (character.AnimController?.Collider != null)
                    {
                        var vel = character.AnimController.Collider.LinearVelocity;
                        if (vel.LengthSquared() >= minVelSq)
                            return true;
                    }
                }

                return false;
            }

            // Fallback: original full scan
            foreach (Character character in Character.CharacterList)
            {
                float dx = Math.Abs(character.WorldPosition.X - detectPos.X);
                float dy = Math.Abs(character.WorldPosition.Y - detectPos.Y);
                if (dx > rxExpanded || dy > ryExpanded) continue;

                if (sensor.IgnoreDead && character.IsDead) continue;
                if (!character.Enabled) continue;

                if (character.IsPet)
                {
                    if (!trigPets) continue;
                }
                else if (character.IsHuman || CharacterParams.CompareGroup(character.Group, CharacterPrefab.HumanGroup))
                {
                    if (!trigHumans) continue;
                }
                else
                {
                    if (!trigMonsters) continue;
                }

                if (targetChars.Count > 0)
                {
                    bool matchFound = false;
                    foreach (Identifier target in targetChars)
                    {
                        if (character.MatchesSpeciesNameOrGroup(target) || character.Params.HasTag(target))
                        {
                            matchFound = true;
                            break;
                        }
                    }
                    if (!matchFound) continue;
                }

                if (character.AnimController?.Collider != null)
                {
                    var vel = character.AnimController.Collider.LinearVelocity;
                    if (vel.LengthSquared() >= minVelSq)
                        return true;
                }
            }

            return false;
        }

        // ════════════════════════════════════════════════════════════════
        //  NativeRuntime entry point — ONLY called by MotionSensorNative.
        //  This is a SEPARATE codepath from Prefix. Prefix is untouched.
        //  Duplicates sections 2+3 of Prefix (timer gate + full scan).
        //  Does NOT send signals or apply status effects — the caller
        //  handles those via TickContext command emission.
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Run detection logic for a sensor managed by NativeRuntime.
        /// Updates sensor.MotionDetected, updateTimer, and FrameCounters.
        /// Does NOT send signals or apply status effects.
        /// </summary>
        internal static void RunDetection(MotionSensor sensor, float deltaTime)
        {
            var item = sensor.item;
            int id = item.ID;

            // ── Timer gate (same logic as Prefix section 2) ──
            ref float updateTimer = ref Ref_updateTimer(sensor);
            updateTimer -= deltaTime;

            if (updateTimer > 0.0f)
            {
                FrameCounters[id]++;
                if (FrameCounters[id] % OptimizerConfig.MotionSensorSkipFrames != 0)
                {
                    Stats.MotionSensorSkips++;
                    return;
                }

                if (QuickDetect(sensor))
                    sensor.MotionDetected = true;
                Stats.MotionSensorSkips++;
                return;
            }

            // ── Full scan (same logic as Prefix section 3) ──
            sensor.MotionDetected = false;
            updateTimer = sensor.UpdateInterval;
            FrameCounters[id] = 0;

            // 3a. Own motion
            if (item.body != null && item.body.Enabled && sensor.DetectOwnMotion)
            {
                if (Math.Abs(item.body.LinearVelocity.X) > sensor.MinimumVelocity ||
                    Math.Abs(item.body.LinearVelocity.Y) > sensor.MinimumVelocity)
                {
                    sensor.MotionDetected = true;
                    return;
                }
            }

            float rangeX = Ref_rangeX(sensor);
            float rangeY = Ref_rangeY(sensor);
            Vector2 detectPos = item.WorldPosition + sensor.TransformedDetectOffset;
            Rectangle detectRect = new Rectangle(
                (int)(detectPos.X - rangeX), (int)(detectPos.Y - rangeY),
                (int)(rangeX * 2), (int)(rangeY * 2));
            float broadRangeX = Math.Max(rangeX * 2, 500);
            float broadRangeY = Math.Max(rangeY * 2, 500);

            // 3b. Wall detection
            if (item.CurrentHull == null && item.Submarine != null &&
                sensor.Target.HasFlag(MotionSensor.TargetType.Wall))
            {
                if (Level.Loaded != null &&
                    (Math.Abs(item.Submarine.Velocity.X) > sensor.MinimumVelocity ||
                     Math.Abs(item.Submarine.Velocity.Y) > sensor.MinimumVelocity))
                {
                    var cells = Level.Loaded.GetCells(item.WorldPosition, 1);
                    foreach (var cell in cells)
                    {
                        if (cell.IsPointInside(item.WorldPosition))
                        {
                            sensor.MotionDetected = true;
                            return;
                        }
                        foreach (var edge in cell.Edges)
                        {
                            Vector2 e1 = edge.Point1 + cell.Translation;
                            Vector2 e2 = edge.Point2 + cell.Translation;
                            if (MathUtils.LineSegmentsIntersect(e1, e2,
                                    new Vector2(detectRect.X, detectRect.Y),
                                    new Vector2(detectRect.Right, detectRect.Y)) ||
                                MathUtils.LineSegmentsIntersect(e1, e2,
                                    new Vector2(detectRect.X, detectRect.Bottom),
                                    new Vector2(detectRect.Right, detectRect.Bottom)) ||
                                MathUtils.LineSegmentsIntersect(e1, e2,
                                    new Vector2(detectRect.X, detectRect.Y),
                                    new Vector2(detectRect.X, detectRect.Bottom)) ||
                                MathUtils.LineSegmentsIntersect(e1, e2,
                                    new Vector2(detectRect.Right, detectRect.Y),
                                    new Vector2(detectRect.Right, detectRect.Bottom)))
                            {
                                sensor.MotionDetected = true;
                                return;
                            }
                        }
                    }
                }
                foreach (Submarine sub in Submarine.Loaded)
                {
                    if (sub == item.Submarine) continue;
                    Vector2 relativeVelocity = item.Submarine.Velocity - sub.Velocity;
                    if (Math.Abs(relativeVelocity.X) < sensor.MinimumVelocity &&
                        Math.Abs(relativeVelocity.Y) < sensor.MinimumVelocity)
                        continue;
                    Rectangle worldBorders = new Rectangle(
                        sub.Borders.X + (int)sub.WorldPosition.X,
                        sub.Borders.Y + (int)sub.WorldPosition.Y - sub.Borders.Height,
                        sub.Borders.Width,
                        sub.Borders.Height);
                    if (worldBorders.Intersects(detectRect))
                    {
                        foreach (Structure wall in Structure.WallList)
                        {
                            if (wall.Submarine == sub && wall.WorldRect.Intersects(detectRect))
                            {
                                sensor.MotionDetected = true;
                                return;
                            }
                        }
                    }
                }
            }

            // 3c. Character detection
            bool trigHumans = Ref_triggerFromHumans(sensor);
            bool trigPets = Ref_triggerFromPets(sensor);
            bool trigMonsters = Ref_triggerFromMonsters(sensor);
            if (!(trigHumans || trigPets || trigMonsters)) return;

            var targetChars = Ref_targetCharacters(sensor);
            float minVelSq = sensor.MinimumVelocity * sensor.MinimumVelocity;

            if (OptimizerConfig.EnableHullSpatialIndex)
            {
                ref var sc = ref _spatialCache[id];
                if (!sc.Resolved)
                    ResolveSpatialCache(ref sc, sensor);

                var coveredIds = sc.CoveredHullIds;
                for (int i = 0; i < coveredIds.Length; i++)
                {
                    var candidates = HullCharacterTracker.GetCharactersInHull(coveredIds[i]);
                    if (candidates.Count > 0 && CheckCandidates(candidates, sensor,
                        detectPos, broadRangeX, broadRangeY, detectRect, minVelSq,
                        trigHumans, trigPets, trigMonsters, targetChars))
                    {
                        sensor.MotionDetected = true;
                        return;
                    }
                }

                var charList = Character.CharacterList;
                for (int i = 0; i < charList.Count; i++)
                {
                    Character character = charList[i];
                    Hull cHull = character.CurrentHull;
                    if (cHull != null && cHull.Submarine == sc.SensorSub) continue;
                    if (Math.Abs(character.WorldPosition.X - detectPos.X) > broadRangeX ||
                        Math.Abs(character.WorldPosition.Y - detectPos.Y) > broadRangeY)
                        continue;
                    if (character.Removed) continue;
                    if (character.SpawnTime > Timing.TotalTime - 1.0) continue;
                    if (sensor.IgnoreDead && character.IsDead) continue;
                    if (character.IsPet) { if (!trigPets) continue; }
                    else if (character.IsHuman || CharacterParams.CompareGroup(
                        character.Group, CharacterPrefab.HumanGroup))
                    { if (!trigHumans) continue; }
                    else { if (!trigMonsters) continue; }
                    if (targetChars.Count > 0)
                    {
                        bool matchFound = false;
                        foreach (Identifier target in targetChars)
                        {
                            if (character.MatchesSpeciesNameOrGroup(target) ||
                                character.Params.HasTag(target))
                            { matchFound = true; break; }
                        }
                        if (!matchFound) continue;
                    }
                    foreach (Limb limb in character.AnimController.Limbs)
                    {
                        if (limb.IsSevered) continue;
                        if (limb.LinearVelocity.LengthSquared() < minVelSq) continue;
                        if (MathUtils.CircleIntersectsRectangle(limb.WorldPosition,
                            ConvertUnits.ToDisplayUnits(limb.body.GetMaxExtent()), detectRect))
                        {
                            sensor.MotionDetected = true;
                            return;
                        }
                    }
                }
            }
            else
            {
                foreach (Character character in Character.CharacterList)
                {
                    if (Math.Abs(character.WorldPosition.X - detectPos.X) > broadRangeX ||
                        Math.Abs(character.WorldPosition.Y - detectPos.Y) > broadRangeY)
                        continue;
                    if (character.SpawnTime > Timing.TotalTime - 1.0) continue;
                    if (sensor.IgnoreDead && character.IsDead) continue;
                    if (character.IsPet) { if (!trigPets) continue; }
                    else if (character.IsHuman || CharacterParams.CompareGroup(
                        character.Group, CharacterPrefab.HumanGroup))
                    { if (!trigHumans) continue; }
                    else { if (!trigMonsters) continue; }
                    if (targetChars.Count > 0)
                    {
                        bool matchFound = false;
                        foreach (Identifier target in targetChars)
                        {
                            if (character.MatchesSpeciesNameOrGroup(target) ||
                                character.Params.HasTag(target))
                            { matchFound = true; break; }
                        }
                        if (!matchFound) continue;
                    }
                    foreach (Limb limb in character.AnimController.Limbs)
                    {
                        if (limb.IsSevered) continue;
                        if (limb.LinearVelocity.LengthSquared() < minVelSq) continue;
                        if (MathUtils.CircleIntersectsRectangle(limb.WorldPosition,
                            ConvertUnits.ToDisplayUnits(limb.body.GetMaxExtent()), detectRect))
                        {
                            sensor.MotionDetected = true;
                            return;
                        }
                    }
                }
            }
        }
    }
}
