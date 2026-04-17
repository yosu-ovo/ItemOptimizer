using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace ItemOptimizerMod
{
    enum ProfileMode { Off, Snapshot, Record }

    static class PerfProfiler
    {
        // ── State ──
        private static ProfileMode Mode = ProfileMode.Off;
        private static int _recordFramesLeft;
        private static int _recordFrameIndex;
        private static string _outputPath;
        private static HashSet<string> TargetFilter; // null = all

        // Per-frame accumulation
        private static readonly Dictionary<string, (long ticks, int count)> FrameData = new(512);
        // Per-frame lane tracking: identifier → "main" | "worker" | "skipped"
        private static readonly Dictionary<string, string> FrameLane = new(512);
        // Lock for thread-safe access when parallel dispatch is active during recording
        private static readonly object FrameDataLock = new();
        private static long _loopStartTick;
        private static long _loopTotalTicks;
        [ThreadStatic]
        private static long _currentItemStartTick;

        // Lazy cache: identifier → package name
        private static readonly Dictionary<string, string> IdToPackage = new(512);

        // Lazy cache: identifier → semantic lane (sensor/logic/power/door/pump/wearable/other)
        private static readonly Dictionary<string, string> IdToLane = new(512);

        // Record buffer
        private static StringBuilder _csvBuffer;

        // Known component names for snapshot
        private static HashSet<string> _knownComponentNames;

        private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

        // Harmony references for dynamic patching
        private static Harmony _harmony;
        private static System.Reflection.MethodInfo _itemUpdateMethod;
        private static HarmonyMethod _itemPrefixMethod;
        private static HarmonyMethod _itemPostfixMethod;
        private static bool _itemPatchesActive;

        // ── Patch Registration ──

        public static void RegisterPatches(Harmony harmony)
        {
            _harmony = harmony;
            _itemUpdateMethod = AccessTools.Method(typeof(Item), nameof(Item.Update));
            _itemPrefixMethod = new HarmonyMethod(typeof(PerfProfiler), nameof(ItemUpdatePrefix));
            _itemPostfixMethod = new HarmonyMethod(typeof(PerfProfiler), nameof(ItemUpdatePostfix));

            // MapEntity.UpdateAll timing is now called directly by UpdateAllTakeover,
            // not via Harmony — no patch registration needed here.
            // Item.Update patches are NOT registered here — only when profiling starts
        }

        private static void AttachItemPatches()
        {
            if (_itemPatchesActive || _harmony == null || _itemUpdateMethod == null) return;
            _harmony.Patch(_itemUpdateMethod, prefix: _itemPrefixMethod, postfix: _itemPostfixMethod);
            _itemPatchesActive = true;
        }

        private static void DetachItemPatches()
        {
            if (!_itemPatchesActive || _harmony == null || _itemUpdateMethod == null) return;
            _harmony.Unpatch(_itemUpdateMethod, _itemPrefixMethod.method);
            _harmony.Unpatch(_itemUpdateMethod, _itemPostfixMethod.method);
            _itemPatchesActive = false;
        }

        // ── Start Methods ──

        public static void StartSnapshot(string path)
        {
            _outputPath = ResolvePath(path);
            FrameData.Clear();
            AttachItemPatches();
            Mode = ProfileMode.Snapshot;
        }

        public static void StartRecord(int frames, string path, HashSet<string> targetFilter)
        {
            _outputPath = ResolvePath(path);
            _recordFramesLeft = Math.Clamp(frames, 1, 3600);
            _recordFrameIndex = 0;
            TargetFilter = targetFilter;
            _csvBuffer = new StringBuilder(1024 * 64);
            DiagnosticHeader.WriteTo(_csvBuffer, frames);
            _csvBuffer.AppendLine("frame,identifier,package,time_ms,update_count,lane");
            FrameData.Clear();
            FrameLane.Clear();
            AttachItemPatches();
            Mode = ProfileMode.Record;
        }

        // ── Harmony Callbacks ──

        // Item.Update prefix/postfix — only active during profiling (dynamically attached/detached)
        public static void ItemUpdatePrefix(Item __instance)
        {
            _currentItemStartTick = Stopwatch.GetTimestamp();
        }

        public static void ItemUpdatePostfix(Item __instance)
        {
            long elapsed = Stopwatch.GetTimestamp() - _currentItemStartTick;

            string id = __instance.Prefab?.Identifier.Value ?? "unknown";
            if (TargetFilter != null && !TargetFilter.Contains(id)) return;

            lock (FrameDataLock)
            {
                if (FrameData.TryGetValue(id, out var existing))
                    FrameData[id] = (existing.ticks + elapsed, existing.count + 1);
                else
                    FrameData[id] = (elapsed, 1);

                // Track lane (semantic classification by component type)
                if (!FrameLane.ContainsKey(id))
                    FrameLane[id] = ClassifyLane(__instance);
            }
        }

        public static void MapEntityUpdateAllPrefix()
        {
            SpikeDetector.OnFrameStart();
            if (Mode == ProfileMode.Off) return;
            FrameData.Clear();
            FrameLane.Clear();
            _loopStartTick = Stopwatch.GetTimestamp();
        }

        public static void MapEntityUpdateAllPostfix()
        {
            SpikeDetector.OnFrameEnd();
            if (Mode == ProfileMode.Off) return;
            _loopTotalTicks = Stopwatch.GetTimestamp() - _loopStartTick;
            ProcessFrame();
        }

        // ── Frame Processing ──

        private static void ProcessFrame()
        {
            switch (Mode)
            {
                case ProfileMode.Snapshot:
                    WriteSnapshotJson();
                    Mode = ProfileMode.Off;
                    DetachItemPatches();
                    TargetFilter = null;
                    DebugConsole.NewMessage($"[ItemOptimizer] Snapshot saved to {_outputPath}", Color.LimeGreen);
                    break;

                case ProfileMode.Record:
                    AppendFrameToCsv();
                    _recordFrameIndex++;
                    _recordFramesLeft--;
                    if (_recordFramesLeft <= 0)
                    {
                        FlushCsvToFile();
                        Mode = ProfileMode.Off;
                        DetachItemPatches();
                        TargetFilter = null;
                        DebugConsole.NewMessage($"[ItemOptimizer] Recording saved to {_outputPath} ({_recordFrameIndex} frames)", Color.LimeGreen);
                    }
                    break;
            }
        }

        private static void AppendFrameToCsv()
        {
            int frame = _recordFrameIndex + 1;
            foreach (var kvp in FrameData)
            {
                string pkg = GetPackageName(kvp.Key);
                double ms = kvp.Value.ticks * TicksToMs;
                FrameLane.TryGetValue(kvp.Key, out var lane);
                _csvBuffer.Append(frame).Append(',');
                _csvBuffer.Append(CsvEscape(kvp.Key)).Append(',');
                _csvBuffer.Append(CsvEscape(pkg)).Append(',');
                _csvBuffer.Append(ms.ToString("F4")).Append(',');
                _csvBuffer.Append(kvp.Value.count).Append(',');
                _csvBuffer.AppendLine(lane ?? "-");
            }
        }

        private static void FlushCsvToFile()
        {
            WriteToFile(_outputPath, _csvBuffer.ToString(), "CSV");
            _csvBuffer = null;
        }

        // ── Snapshot JSON ──

        private static void WriteSnapshotJson()
        {
            var sb = new StringBuilder(1024 * 64);
            sb.AppendLine("{");
            sb.Append("  \"timestamp\": \"").Append(DateTime.UtcNow.ToString("o")).AppendLine("\",");
            sb.Append("  \"gameVersion\": \"").Append(GameMain.Version).AppendLine("\",");
            sb.AppendLine("  \"source\": \"ItemOptimizer\",");

            BuildPackageList(sb);
            sb.AppendLine(",");
            BuildPrefabList(sb);
            sb.AppendLine(",");
            BuildInstanceList(sb);
            sb.AppendLine(",");
            BuildFramePerf(sb);
            sb.AppendLine();
            sb.AppendLine("}");

            WriteToFile(_outputPath, sb.ToString(), "snapshot");
        }

        private static void BuildPackageList(StringBuilder sb)
        {
            var prefabCounts = new Dictionary<string, int>(64);
            foreach (var prefab in ItemPrefab.Prefabs)
            {
                string pkg = prefab.ContentPackage?.Name ?? "Unknown";
                prefabCounts.TryGetValue(pkg, out int c);
                prefabCounts[pkg] = c + 1;
            }

            sb.AppendLine("  \"enabledPackages\": [");
            bool first = true;
            foreach (var cp in ContentPackageManager.EnabledPackages.All)
            {
                if (!first) sb.AppendLine(",");
                first = false;

                string workshopId = "0";
                if (cp.UgcId.TryUnwrap(out var ugcId) && ugcId is SteamWorkshopId swid)
                    workshopId = swid.Value.ToString();

                prefabCounts.TryGetValue(cp.Name, out int cnt);

                sb.Append("    { \"name\": \"").Append(JsonEscape(cp.Name)).Append('"');
                sb.Append(", \"workshopId\": \"").Append(workshopId).Append('"');
                sb.Append(", \"itemPrefabCount\": ").Append(cnt);
                sb.Append(" }");
            }
            sb.AppendLine();
            sb.Append("  ]");
        }

        private static void BuildPrefabList(StringBuilder sb)
        {
            var compNames = GetKnownComponentNames();

            sb.AppendLine("  \"itemPrefabs\": [");
            bool first = true;
            foreach (var prefab in ItemPrefab.Prefabs)
            {
                if (!first) sb.AppendLine(",");
                first = false;

                sb.Append("    { \"identifier\": \"").Append(JsonEscape(prefab.Identifier.Value)).Append('"');
                sb.Append(", \"package\": \"").Append(JsonEscape(prefab.ContentPackage?.Name ?? "Unknown")).Append('"');

                // Components
                sb.Append(", \"components\": [");
                bool fc = true;
                int compCount = 0;
                foreach (var el in prefab.ConfigElement.Elements())
                {
                    string elName = el.Name.ToString();
                    if (compNames.Contains(elName))
                    {
                        if (!fc) sb.Append(", ");
                        fc = false;
                        sb.Append('"').Append(elName).Append('"');
                        compCount++;
                    }
                }
                sb.Append(']');
                sb.Append(", \"compCount\": ").Append(compCount);

                // StatusEffect count
                int seCount = 0;
                foreach (var desc in prefab.ConfigElement.Descendants())
                {
                    if (desc.Name.ToString().Equals("StatusEffect", StringComparison.OrdinalIgnoreCase))
                        seCount++;
                }
                sb.Append(", \"seCount\": ").Append(seCount);

                sb.Append(" }");
            }
            sb.AppendLine();
            sb.Append("  ]");
        }

        private static void BuildInstanceList(StringBuilder sb)
        {
            var groups = new Dictionary<string, (string pkg, int total, int active)>(256);
            foreach (var item in Item.ItemList)
            {
                string id = item.Prefab?.Identifier.Value ?? "unknown";
                string pkg = item.Prefab?.ContentPackage?.Name ?? "Unknown";
                if (groups.TryGetValue(id, out var g))
                    groups[id] = (g.pkg, g.total + 1, g.active + (item.IsActive ? 1 : 0));
                else
                    groups[id] = (pkg, 1, item.IsActive ? 1 : 0);
            }

            sb.AppendLine("  \"instantiatedItems\": [");
            bool first = true;
            foreach (var kvp in groups.OrderByDescending(g => g.Value.total))
            {
                if (!first) sb.AppendLine(",");
                first = false;
                sb.Append("    { \"identifier\": \"").Append(JsonEscape(kvp.Key)).Append('"');
                sb.Append(", \"package\": \"").Append(JsonEscape(kvp.Value.pkg)).Append('"');
                sb.Append(", \"total\": ").Append(kvp.Value.total);
                sb.Append(", \"active\": ").Append(kvp.Value.active);
                sb.Append(" }");
            }
            sb.AppendLine();
            sb.Append("  ]");
        }

        private static void BuildFramePerf(StringBuilder sb)
        {
            sb.AppendLine("  \"framePerf\": {");
            sb.Append("    \"loopTotalMs\": ").Append((_loopTotalTicks * TicksToMs).ToString("F4")).AppendLine(",");
            sb.AppendLine("    \"items\": [");

            bool first = true;
            foreach (var kvp in FrameData.OrderByDescending(x => x.Value.ticks))
            {
                if (!first) sb.AppendLine(",");
                first = false;
                double ms = kvp.Value.ticks * TicksToMs;
                string pkg = GetPackageName(kvp.Key);
                sb.Append("      { \"identifier\": \"").Append(JsonEscape(kvp.Key)).Append('"');
                sb.Append(", \"package\": \"").Append(JsonEscape(pkg)).Append('"');
                sb.Append(", \"timeMs\": ").Append(ms.ToString("F4"));
                sb.Append(", \"count\": ").Append(kvp.Value.count);
                sb.Append(" }");
            }
            sb.AppendLine();
            sb.AppendLine("    ]");
            sb.Append("  }");
        }

        // ── Helpers ──

        private static string GetPackageName(string identifier)
        {
            if (IdToPackage.TryGetValue(identifier, out var pkg)) return pkg;
            foreach (var prefab in ItemPrefab.Prefabs)
            {
                if (prefab.Identifier.Value == identifier)
                {
                    pkg = prefab.ContentPackage?.Name ?? "Unknown";
                    IdToPackage[identifier] = pkg;
                    return pkg;
                }
            }
            IdToPackage[identifier] = "Unknown";
            return "Unknown";
        }

        private static string ClassifyLane(Item item)
        {
            string id = item.Prefab?.Identifier.Value ?? "unknown";
            if (IdToLane.TryGetValue(id, out var cached)) return cached;

            string lane = "other";
            foreach (var comp in item.Components)
            {
                string typeName = comp.GetType().Name;
                switch (typeName)
                {
                    case "MotionSensor":
                    case "WaterDetector":
                    case "OxygenDetector":
                        lane = "sensor"; goto done;
                    case "RelayComponent":
                    case "SignalCheckComponent":
                    case "AdderComponent":
                    case "MultiplyComponent":
                    case "DivideComponent":
                    case "SubtractComponent":
                    case "NotComponent":
                    case "AndComponent":
                    case "OrComponent":
                    case "XorComponent":
                    case "EqualsComponent":
                    case "GreaterComponent":
                    case "MemoryComponent":
                    case "OscillatorComponent":
                    case "DelayComponent":
                    case "ConcatComponent":
                    case "RegExFindComponent":
                    case "StringComponent":
                    case "ColorComponent":
                    case "TrigonometricFunctionComponent":
                    case "ModuloComponent":
                    case "AbsComponent":
                    case "SquareRootComponent":
                    case "RoundComponent":
                    case "CeilingComponent":
                    case "FloorComponent":
                    case "WiFiComponent":
                    case "Terminal":
                    case "CustomInterface":
                        lane = "logic"; goto done;
                    case "PowerTransfer":
                    case "PowerContainer":
                    case "Reactor":
                    case "PoweredController":
                        lane = "power"; goto done;
                    case "Door":
                        lane = "door"; goto done;
                    case "Pump":
                        lane = "pump"; goto done;
                    case "Wearable":
                        if (lane != "other") break; // don't override a more specific classification
                        lane = "wearable"; break;
                    case "Wire":
                        if (lane == "other") lane = "wire"; break;
                }
            }
            done:
            IdToLane[id] = lane;
            return lane;
        }

        private static HashSet<string> GetKnownComponentNames()
        {
            if (_knownComponentNames != null) return _knownComponentNames;
            _knownComponentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var type in ReflectionUtils.GetDerivedNonAbstract<ItemComponent>())
                _knownComponentNames.Add(type.Name);
            _knownComponentNames.Add("ItemComponent");
            return _knownComponentNames;
        }

        internal static string ResolvePath(string path)
        {
            if (Path.IsPathRooted(path)) return path;
            return ModPaths.ResolveData(path);
        }

        private static string JsonEscape(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
        }

        private static string CsvEscape(string s)
        {
            if (s == null) return "";
            if (s.Contains(',') || s.Contains('"'))
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        private static void WriteToFile(string path, string content, string label)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, content);
            }
            catch (Exception e)
            {
                DebugConsole.ThrowError($"[ItemOptimizer] Failed to write {label}: {e.Message}");
            }
        }

        internal static void Reset()
        {
            Mode = ProfileMode.Off;
            DetachItemPatches();
            FrameData.Clear();
            FrameLane.Clear();
            IdToPackage.Clear();
            IdToLane.Clear();
            _csvBuffer = null;
            TargetFilter = null;
        }
    }
}
