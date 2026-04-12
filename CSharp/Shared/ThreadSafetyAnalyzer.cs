using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Barotrauma;

namespace ItemOptimizerMod
{
    enum ThreadSafetyTier : byte
    {
        Safe        = 0, // Always safe for worker thread
        Conditional = 1, // Safe with runtime checks (body/wires)
        Unsafe      = 2  // Never safe for worker thread
    }

    [Flags]
    enum UnsafeFlags : int
    {
        None               = 0,
        DangerousComponent = 1 << 0,
        ConnectionPanel    = 1 << 1,
        PhysicsBody        = 1 << 2,
        SEDuration         = 1 << 3,  // duration>0 → writes static DurationList
        SEInterval         = 1 << 4,  // (kept for display; interval alone is instance-level safe)
        SEAffliction       = 1 << 5,
        SESpawn            = 1 << 6,
        SEFire             = 1 << 7,
        SEExplosion        = 1 << 8,
        SERemoveItem       = 1 << 9,
        SEExternalTarget   = 1 << 10,
        SEGiveSkill        = 1 << 11,
        SEGiveTalent       = 1 << 12,
        SEDropItem         = 1 << 13,
        SETriggerEvent     = 1 << 14,
        SEUseItem          = 1 << 15,
        ManualOverride     = 1 << 16
    }

    struct PrefabSafetyInfo
    {
        public ThreadSafetyTier Tier;
        public UnsafeFlags Flags;
        public bool IsVanilla;
    }

    static class ThreadSafetyAnalyzer
    {
        // ── Cache version — bump when classification logic changes to invalidate old caches ──
        private const int CacheVersion = 2;

        // ── State ──
        private static Dictionary<string, PrefabSafetyInfo> _cache;
        private static string _cacheHash;
        internal static bool IsScanComplete { get; private set; }

        // ── Manual overrides (identifier → forced tier) ──
        internal static readonly Dictionary<string, ThreadSafetyTier> Overrides
            = new(StringComparer.Ordinal);

        // ── Summary counts ──
        internal static int CountSafe, CountConditional, CountUnsafe, CountVanilla;

        // ── Dangerous component names (XML element tag, case-insensitive) ──
        private static readonly HashSet<string> DangerousComponents
            = new(StringComparer.OrdinalIgnoreCase)
        {
            "Door", "Pump", "Reactor", "PowerTransfer", "Turret",
            "Fabricator", "Deconstructor", "Steering", "Engine",
            "OxygenGenerator", "MiniMap", "Sonar", "DockingPort",
            "ElectricalDischarger", "Controller", "TriggerComponent",
            "Rope", "EntitySpawnerComponent", "StatusMonitor",
            "Wire", "StatusHUD"
        };

        // ── SE self-only targets ──
        private static readonly HashSet<string> SafeTargets
            = new(StringComparer.OrdinalIgnoreCase)
        {
            "This", "Contained", "Limb", "Parent", "LastLimb"
        };

        // ── SE dangerous child element names ──
        private static readonly HashSet<string> DangerousSEChildren
            = new(StringComparer.OrdinalIgnoreCase)
        {
            "Affliction", "Explosion", "Fire", "SpawnItem", "SpawnCharacter",
            "RemoveItem", "RemoveCharacter", "DropItem", "DropContainedItems",
            "GiveSkill", "GiveTalentInfo", "TriggerEvent", "UseItem"
        };

        // ── SE types that fire when the item is idle (no character interaction needed) ──
        // "Always" and "OnNotContained" fire EVERY FRAME for ground items.
        // "OnContaining" fires when item has contained items (e.g. loaded magazine in gun on floor).
        // "InWater"/"NotInWater" fire based on environment.
        // "OnBroken"/"OnDamaged"/"OnFire" can fire from environmental damage.
        // "OnSpawn" fires once on creation — negligible and usually benign.
        private static readonly HashSet<string> IdleFiringTypes
            = new(StringComparer.OrdinalIgnoreCase)
        {
            "Always", "OnNotContained", "OnContaining", "InWater", "NotInWater",
            "OnBroken", "OnDamaged", "OnFire", "OnSpawn"
        };

        // SE types that REQUIRE character interaction — never fire on idle ground items.
        // OnUse, OnSecondaryUse, OnActive, OnWearing, OnPicked, OnSuccess, OnFailure,
        // OnEating, OnSevered, OnAbility, OnDeconstructing, OnDeconstructed,
        // OnContained (requires parentInventory != null; ground items have null),
        // OnImpact (physics collision — only fires on moving items, rarely idle)
        // OnInserted/OnRemoved (one-shot on inventory action)
        // These are safe to IGNORE when evaluating idle-safety.

        // Maps SE child name → UnsafeFlags
        private static readonly Dictionary<string, UnsafeFlags> SEChildToFlag
            = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Affliction"]         = UnsafeFlags.SEAffliction,
            ["Explosion"]          = UnsafeFlags.SEExplosion,
            ["Fire"]               = UnsafeFlags.SEFire,
            ["SpawnItem"]          = UnsafeFlags.SESpawn,
            ["SpawnCharacter"]     = UnsafeFlags.SESpawn,
            ["RemoveItem"]         = UnsafeFlags.SERemoveItem,
            ["RemoveCharacter"]    = UnsafeFlags.SERemoveItem,
            ["DropItem"]           = UnsafeFlags.SEDropItem,
            ["DropContainedItems"] = UnsafeFlags.SEDropItem,
            ["GiveSkill"]          = UnsafeFlags.SEGiveSkill,
            ["GiveTalentInfo"]     = UnsafeFlags.SEGiveTalent,
            ["TriggerEvent"]       = UnsafeFlags.SETriggerEvent,
            ["UseItem"]            = UnsafeFlags.SEUseItem,
        };

        // ── Public API ──

        internal static ThreadSafetyTier GetTier(string identifier)
        {
            if (_cache != null && _cache.TryGetValue(identifier, out var info))
                return info.Tier;
            return ThreadSafetyTier.Unsafe; // fail-safe
        }

        internal static PrefabSafetyInfo GetInfo(string identifier)
        {
            if (_cache != null && _cache.TryGetValue(identifier, out var info))
                return info;
            return new PrefabSafetyInfo { Tier = ThreadSafetyTier.Unsafe, Flags = UnsafeFlags.None, IsVanilla = false };
        }

        // ── RunScan ──

        internal static void RunScan()
        {
            _cache = new Dictionary<string, PrefabSafetyInfo>(32768, StringComparer.Ordinal);
            CountSafe = 0;
            CountConditional = 0;
            CountUnsafe = 0;
            CountVanilla = 0;

            foreach (ItemPrefab prefab in ItemPrefab.Prefabs)
            {
                var info = AnalyzePrefab(prefab);
                _cache[prefab.Identifier.Value] = info;

                if (info.IsVanilla) CountVanilla++;
                switch (info.Tier)
                {
                    case ThreadSafetyTier.Safe: CountSafe++; break;
                    case ThreadSafetyTier.Conditional: CountConditional++; break;
                    case ThreadSafetyTier.Unsafe: CountUnsafe++; break;
                }
            }

            _cacheHash = OptimizerConfig.GetModSetHash();
            IsScanComplete = true;

            LuaCsLogger.Log($"[ItemOptimizer] Thread safety scan complete (v{CacheVersion}): " +
                $"safe={CountSafe}, conditional={CountConditional}, unsafe={CountUnsafe}, " +
                $"vanilla={CountVanilla}, total={_cache.Count}");
        }

        // ── Analyze single prefab ──

        private static PrefabSafetyInfo AnalyzePrefab(ItemPrefab prefab)
        {
            // Vanilla → always Unsafe
            var cp = prefab.ContentPackage;
            if (cp == null || cp == ContentPackageManager.VanillaCorePackage)
                return new PrefabSafetyInfo { Tier = ThreadSafetyTier.Unsafe, Flags = UnsafeFlags.None, IsVanilla = true };

            // Manual override
            if (Overrides.TryGetValue(prefab.Identifier.Value, out var overrideTier))
                return new PrefabSafetyInfo { Tier = overrideTier, Flags = UnsafeFlags.ManualOverride, IsVanilla = false };

            var configEl = prefab.ConfigElement;
            if (configEl == null)
                return new PrefabSafetyInfo { Tier = ThreadSafetyTier.Unsafe, Flags = UnsafeFlags.DangerousComponent, IsVanilla = false };

            UnsafeFlags flags = UnsafeFlags.None;

            // Scan component elements
            foreach (var compEl in configEl.Elements())
            {
                string tag = compEl.Name.ToString();

                // Dangerous component check
                if (DangerousComponents.Contains(tag))
                    flags |= UnsafeFlags.DangerousComponent;

                // ConnectionPanel
                if (tag.Equals("ConnectionPanel", StringComparison.OrdinalIgnoreCase))
                    flags |= UnsafeFlags.ConnectionPanel;

                // Body (physics)
                if (tag.Equals("Body", StringComparison.OrdinalIgnoreCase))
                    flags |= UnsafeFlags.PhysicsBody;

                // Scan StatusEffects within this component
                AnalyzeStatusEffects(compEl, ref flags);
            }

            // Determine tier
            var tier = DetermineTier(flags);
            return new PrefabSafetyInfo { Tier = tier, Flags = flags, IsVanilla = false };
        }

        private static void AnalyzeStatusEffects(ContentXElement parent, ref UnsafeFlags flags)
        {
            foreach (var child in parent.Elements())
            {
                if (!child.Name.ToString().Equals("StatusEffect", StringComparison.OrdinalIgnoreCase)
                 && !child.Name.ToString().Equals("statuseffect", StringComparison.OrdinalIgnoreCase))
                    continue;

                // ── Determine SE type ──
                string seType = child.GetAttributeString("type", "Always");

                // SE types that require character interaction → skip entirely for idle analysis.
                // These SEs will never fire on an idle ground item.
                if (!IdleFiringTypes.Contains(seType))
                    continue;

                // ── This SE CAN fire when idle. Check what it does. ──

                // Duration → writes to static DurationList (always unsafe)
                float duration = child.GetAttributeFloat("duration", 0f);
                if (duration > 0f)
                    flags |= UnsafeFlags.SEDuration;

                // Interval: the intervalTimers dict is per-SE-instance, keyed by Entity.
                // Pure instance-level state, not shared. Only flag it for informational display,
                // but it is NOT counted as a danger signal for tier determination.
                float interval = child.GetAttributeFloat("interval", 0f);
                if (interval > 0f)
                    flags |= UnsafeFlags.SEInterval;

                // Target check: only flag external targets that write shared state
                string targetStr = child.GetAttributeString("target", null)
                                ?? child.GetAttributeString("targettype", "This");
                bool hasExternalTarget = false;
                if (!string.IsNullOrEmpty(targetStr))
                {
                    var targets = targetStr.Split(',');
                    foreach (var t in targets)
                    {
                        string trimmed = t.Trim();
                        if (trimmed.Length > 0 && !SafeTargets.Contains(trimmed))
                        {
                            hasExternalTarget = true;
                            flags |= UnsafeFlags.SEExternalTarget;
                            break;
                        }
                    }
                }

                // Dangerous child elements — only count as danger if they write shared state.
                // For target=This/Contained, children like Affliction actually apply to self
                // which is safe. Only flag if combined with external target.
                foreach (var seChild in child.Elements())
                {
                    string childName = seChild.Name.ToString();
                    if (SEChildToFlag.TryGetValue(childName, out var childFlag))
                    {
                        // SpawnItem/SpawnCharacter/RemoveItem/TriggerEvent are always unsafe
                        // regardless of target — they mutate global entity spawner or event system
                        bool alwaysUnsafe =
                            childFlag == UnsafeFlags.SESpawn ||
                            childFlag == UnsafeFlags.SERemoveItem ||
                            childFlag == UnsafeFlags.SETriggerEvent;

                        if (alwaysUnsafe || hasExternalTarget)
                            flags |= childFlag;
                    }
                }
            }
        }

        private static ThreadSafetyTier DetermineTier(UnsafeFlags flags)
        {
            // Any dangerous component → Unsafe
            if ((flags & UnsafeFlags.DangerousComponent) != 0)
                return ThreadSafetyTier.Unsafe;

            // SE danger flags that indicate true shared-state writes.
            // SEInterval is excluded: intervalTimers is per-SE-instance, not shared.
            // SEExternalTarget alone is not dangerous — it only matters when combined
            // with a dangerous child (which sets its own flag).
            const UnsafeFlags seDangerMask =
                UnsafeFlags.SEDuration | UnsafeFlags.SEAffliction |
                UnsafeFlags.SESpawn | UnsafeFlags.SEFire | UnsafeFlags.SEExplosion |
                UnsafeFlags.SERemoveItem |
                UnsafeFlags.SEGiveSkill | UnsafeFlags.SEGiveTalent |
                UnsafeFlags.SEDropItem | UnsafeFlags.SETriggerEvent | UnsafeFlags.SEUseItem;

            if ((flags & seDangerMask) != 0)
                return ThreadSafetyTier.Unsafe;

            // ConnectionPanel or Body (no other danger) → Conditional
            if ((flags & (UnsafeFlags.ConnectionPanel | UnsafeFlags.PhysicsBody)) != 0)
                return ThreadSafetyTier.Conditional;

            return ThreadSafetyTier.Safe;
        }

        // ── Manual Overrides ──

        internal static void SetOverride(string identifier, ThreadSafetyTier? tier)
        {
            if (tier == null)
                Overrides.Remove(identifier);
            else
                Overrides[identifier] = tier.Value;

            // Re-classify this prefab if scan is active
            if (_cache != null && IsScanComplete)
            {
                if (ItemPrefab.Prefabs.TryGet(new Identifier(identifier), out var prefab))
                {
                    var info = AnalyzePrefab(prefab);
                    _cache[identifier] = info;
                    RecountTiers();
                }
            }
        }

        private static void RecountTiers()
        {
            CountSafe = 0; CountConditional = 0; CountUnsafe = 0; CountVanilla = 0;
            foreach (var info in _cache.Values)
            {
                if (info.IsVanilla) CountVanilla++;
                switch (info.Tier)
                {
                    case ThreadSafetyTier.Safe: CountSafe++; break;
                    case ThreadSafetyTier.Conditional: CountConditional++; break;
                    case ThreadSafetyTier.Unsafe: CountUnsafe++; break;
                }
            }
        }

        // ── Cache IO ──

        internal static void SaveCache()
        {
            try
            {
                if (_cache == null || !IsScanComplete) return;
                var path = OptimizerConfig.GetThreadSafetyCachePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));

                var overridesEl = new XElement("Overrides");
                foreach (var kv in Overrides)
                    overridesEl.Add(new XElement("Item",
                        new XAttribute("id", kv.Key),
                        new XAttribute("tier", (int)kv.Value)));

                var prefabsEl = new XElement("Prefabs");
                foreach (var kv in _cache)
                {
                    if (kv.Value.IsVanilla) continue; // don't store vanilla
                    prefabsEl.Add(new XElement("I",
                        new XAttribute("id", kv.Key),
                        new XAttribute("t", (int)kv.Value.Tier),
                        new XAttribute("r", (int)kv.Value.Flags)));
                }

                var doc = new XDocument(
                    new XElement("ThreadSafetyCache",
                        new XAttribute("hash", _cacheHash ?? ""),
                        new XAttribute("version", CacheVersion),
                        new XElement("Summary",
                            new XAttribute("safe", CountSafe),
                            new XAttribute("conditional", CountConditional),
                            new XAttribute("unsafe", CountUnsafe),
                            new XAttribute("vanilla", CountVanilla)),
                        overridesEl,
                        prefabsEl));
                doc.Save(path);

                LuaCsLogger.Log($"[ItemOptimizer] Thread safety cache saved to {Path.GetFileName(path)}");
            }
            catch (Exception e)
            {
                DebugConsole.ThrowError($"[ItemOptimizer] Failed to save thread safety cache: {e.Message}");
            }
        }

        internal static bool LoadCache()
        {
            try
            {
                var path = OptimizerConfig.GetThreadSafetyCachePath();
                if (!File.Exists(path)) return false;

                var doc = XDocument.Load(path);
                var root = doc.Root;
                if (root == null) return false;

                // Validate hash
                var hash = root.Attribute("hash")?.Value;
                var currentHash = OptimizerConfig.GetModSetHash();
                if (hash != currentHash)
                {
                    LuaCsLogger.Log($"[ItemOptimizer] Thread safety cache hash mismatch ({hash} vs {currentHash}), needs re-scan");
                    return false;
                }

                // Validate version (classification logic may have changed)
                int.TryParse(root.Attribute("version")?.Value, out int version);
                if (version != CacheVersion)
                {
                    LuaCsLogger.Log($"[ItemOptimizer] Thread safety cache version mismatch (v{version} vs v{CacheVersion}), needs re-scan");
                    return false;
                }

                // Load overrides
                Overrides.Clear();
                var overridesEl = root.Element("Overrides");
                if (overridesEl != null)
                {
                    foreach (var el in overridesEl.Elements("Item"))
                    {
                        var id = el.Attribute("id")?.Value;
                        var tierStr = el.Attribute("tier")?.Value;
                        if (!string.IsNullOrEmpty(id) && int.TryParse(tierStr, out int tierVal)
                            && tierVal >= 0 && tierVal <= 2)
                        {
                            Overrides[id] = (ThreadSafetyTier)tierVal;
                        }
                    }
                }

                // Load prefab classifications
                _cache = new Dictionary<string, PrefabSafetyInfo>(32768, StringComparer.Ordinal);
                var prefabsEl = root.Element("Prefabs");
                if (prefabsEl != null)
                {
                    foreach (var el in prefabsEl.Elements("I"))
                    {
                        var id = el.Attribute("id")?.Value;
                        if (string.IsNullOrEmpty(id)) continue;
                        int t = 2, r = 0;
                        int.TryParse(el.Attribute("t")?.Value, out t);
                        int.TryParse(el.Attribute("r")?.Value, out r);
                        _cache[id] = new PrefabSafetyInfo
                        {
                            Tier = (ThreadSafetyTier)Math.Clamp(t, 0, 2),
                            Flags = (UnsafeFlags)r,
                            IsVanilla = false
                        };
                    }
                }

                // Vanilla items not in cache — add them
                foreach (ItemPrefab prefab in ItemPrefab.Prefabs)
                {
                    var cp = prefab.ContentPackage;
                    if (cp != null && cp == ContentPackageManager.VanillaCorePackage)
                    {
                        _cache[prefab.Identifier.Value] = new PrefabSafetyInfo
                        {
                            Tier = ThreadSafetyTier.Unsafe,
                            Flags = UnsafeFlags.None,
                            IsVanilla = true
                        };
                    }
                }

                // Read summary
                var summary = root.Element("Summary");
                if (summary != null)
                {
                    int.TryParse(summary.Attribute("safe")?.Value, out CountSafe);
                    int.TryParse(summary.Attribute("conditional")?.Value, out CountConditional);
                    int.TryParse(summary.Attribute("unsafe")?.Value, out CountUnsafe);
                    int.TryParse(summary.Attribute("vanilla")?.Value, out CountVanilla);
                }

                _cacheHash = currentHash;
                IsScanComplete = true;

                LuaCsLogger.Log($"[ItemOptimizer] Thread safety cache loaded: " +
                    $"safe={CountSafe}, conditional={CountConditional}, unsafe={CountUnsafe}, vanilla={CountVanilla}");
                return true;
            }
            catch (Exception e)
            {
                DebugConsole.ThrowError($"[ItemOptimizer] Failed to load thread safety cache: {e.Message}");
                return false;
            }
        }

        internal static void InvalidateCache()
        {
            _cache = null;
            IsScanComplete = false;
            CountSafe = CountConditional = CountUnsafe = CountVanilla = 0;
        }

        // ── Reason text for UI display ──

        internal static string GetFlagsText(UnsafeFlags flags)
        {
            if (flags == UnsafeFlags.None) return "";
            if ((flags & UnsafeFlags.ManualOverride) != 0) return Localization.T("reason_manual_override");

            var sb = new StringBuilder();
            AppendFlag(sb, flags, UnsafeFlags.DangerousComponent, "reason_dangerous_component");
            AppendFlag(sb, flags, UnsafeFlags.ConnectionPanel, "reason_connection_panel");
            AppendFlag(sb, flags, UnsafeFlags.PhysicsBody, "reason_physics_body");
            AppendFlag(sb, flags, UnsafeFlags.SEDuration, "reason_se_duration");
            AppendFlag(sb, flags, UnsafeFlags.SEInterval, "reason_se_interval");
            AppendFlag(sb, flags, UnsafeFlags.SEAffliction, "reason_se_affliction");
            AppendFlag(sb, flags, UnsafeFlags.SESpawn, "reason_se_spawn");
            AppendFlag(sb, flags, UnsafeFlags.SEFire, "reason_se_fire");
            AppendFlag(sb, flags, UnsafeFlags.SEExplosion, "reason_se_explosion");
            AppendFlag(sb, flags, UnsafeFlags.SERemoveItem, "reason_se_remove_item");
            AppendFlag(sb, flags, UnsafeFlags.SEExternalTarget, "reason_se_external_target");
            AppendFlag(sb, flags, UnsafeFlags.SEGiveSkill, "reason_se_give_skill");
            AppendFlag(sb, flags, UnsafeFlags.SEGiveTalent, "reason_se_give_talent");
            AppendFlag(sb, flags, UnsafeFlags.SEDropItem, "reason_se_drop_item");
            AppendFlag(sb, flags, UnsafeFlags.SETriggerEvent, "reason_se_trigger_event");
            AppendFlag(sb, flags, UnsafeFlags.SEUseItem, "reason_se_use_item");
            return sb.ToString();
        }

        private static void AppendFlag(StringBuilder sb, UnsafeFlags flags, UnsafeFlags flag, string key)
        {
            if ((flags & flag) == 0) return;
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(Localization.T(key));
        }
    }
}
