using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using Barotrauma;

namespace ItemOptimizerMod
{
    public enum ItemRuleAction
    {
        Skip,
        Throttle
    }

    public class ItemRule
    {
        public string Identifier = "";
        public ItemRuleAction Action = ItemRuleAction.Skip;
        public int SkipFrames = 3;
        public string Condition = "always"; // "always", "coldStorage", "notInActiveUse"
    }

    static class OptimizerConfig
    {
        public static bool EnableColdStorageSkip = true;
        public static bool EnableGroundItemThrottle = true;
        public static int GroundItemSkipFrames = 3;
        public static bool EnableCustomInterfaceThrottle = true;
        public static bool EnableMotionSensorThrottle = true;
        public static bool EnableWearableThrottle = true;
        public static bool EnableWaterDetectorThrottle = true;
        public static bool EnableDoorThrottle = true;
        public static bool EnableHasStatusTagCache = true;
        public static bool EnableStatusHUDThrottle = true;
        public static bool EnableAfflictionDedup = true;
        public static int MotionSensorSkipFrames = 3;
        public static int WearableSkipFrames = 3;
        public static int WaterDetectorSkipFrames = 3;
        public static int DoorSkipFrames = 2;
        public static float StatusHUDScanInterval = 1.5f;
        public static int StatusHUDDrawSkipFrames = 3;

        // Parallel dispatch
        public static bool EnableParallelDispatch = false;
        public static int ParallelWorkerCount = 2; // 1-6 worker threads

        // Spike detector (off by default — adds ~1-2ms overhead when enabled)
        public static bool EnableSpikeDetector = false;
        public static float SpikeThresholdMs = 30f;

        // Per-item rules (manual, user-defined)
        public static List<ItemRule> ItemRules = new();

        // Pre-compiled lookup tables for fast per-frame checks
        public static readonly Dictionary<string, ItemRule> RuleLookup = new();

        // ── Mod Optimization (tier-based, separate from manual rules) ──
        // Persistence: packageName → int[4] { criticalSkip, activeSkip, moderateSkip, staticSkip }
        public static readonly Dictionary<string, int[]> ModOptSettings = new(StringComparer.Ordinal);
        // Runtime flat lookup: identifier → skipFrames (built from ModOptSettings + prefab classification)
        public static readonly Dictionary<string, int> ModOptLookup = new(StringComparer.Ordinal);

        // ── Thread safety manual overrides (identifier → tier: 0=Safe, 1=Conditional, 2=Unsafe) ──
        public static readonly Dictionary<string, int> ThreadSafetyOverrides = new(StringComparer.Ordinal);

        /// <summary>
        /// Classify an ItemPrefab into activity tier (0=Critical,1=Active,2=Moderate,3=Static)
        /// using XML pattern detection. Shared between SettingsPanel and BuildModOptLookup.
        /// </summary>
        public static int ClassifyItemPrefab(ItemPrefab prefab)
        {
            var configEl = prefab.ConfigElement;
            if (configEl == null) return 3; // Static

            bool hasStatusHUD = false;
            bool hasAffliction = false;
            bool hasConditional = false;
            int statusEffectCount = 0;

            foreach (var compEl in configEl.Elements())
            {
                if (compEl.Name.ToString().Equals("StatusHUD", StringComparison.OrdinalIgnoreCase))
                    hasStatusHUD = true;

                foreach (var subEl in compEl.Elements())
                {
                    var subName = subEl.Name.ToString();
                    if (subName.Equals("statuseffect", StringComparison.OrdinalIgnoreCase))
                    {
                        statusEffectCount++;
                        var typeAttr = subEl.GetAttributeString("type", "OnActive");
                        if (typeAttr.Equals("OnActive", StringComparison.OrdinalIgnoreCase)
                            || typeAttr.Equals("Always", StringComparison.OrdinalIgnoreCase))
                        {
                            foreach (var seChild in subEl.Elements())
                            {
                                if (seChild.Name.ToString().Equals("Affliction", StringComparison.OrdinalIgnoreCase))
                                {
                                    hasAffliction = true;
                                    break;
                                }
                            }
                        }
                    }
                    else if (subName.Equals("activeconditional", StringComparison.OrdinalIgnoreCase)
                          || subName.Equals("isactiveconditional", StringComparison.OrdinalIgnoreCase)
                          || subName.Equals("isactive", StringComparison.OrdinalIgnoreCase))
                    {
                        hasConditional = true;
                    }
                }
            }

            bool hasMultiSE = statusEffectCount > 5;

            if (hasStatusHUD) return 0; // Critical
            if (hasMultiSE || (hasAffliction && hasConditional)) return 1; // Active
            if (hasAffliction || hasConditional || statusEffectCount > 2) return 2; // Moderate
            return 3; // Static
        }

        /// <summary>
        /// Rebuild ModOptLookup from ModOptSettings by scanning all non-vanilla prefabs.
        /// Only items whose package is in ModOptSettings get an entry.
        /// Items with skipFrames=1 are excluded (no throttle needed).
        /// </summary>
        public static void BuildModOptLookup()
        {
            ModOptLookup.Clear();
            if (ModOptSettings.Count == 0) return;

            foreach (ItemPrefab prefab in ItemPrefab.Prefabs)
            {
                var pkg = prefab.ContentPackage;
                if (pkg == null || pkg == ContentPackageManager.VanillaCorePackage) continue;

                if (!ModOptSettings.TryGetValue(pkg.Name, out var tierSkips)) continue;

                int tier = ClassifyItemPrefab(prefab);
                int skip = tierSkips[tier];
                if (skip <= 1) continue; // no throttle

                ModOptLookup[prefab.Identifier.Value] = skip;
            }
        }

        // ── Profile System (per-mod-set persistence) ──

        private static string _profileHash;

        internal static string GetModSetHash()
        {
            if (_profileHash != null) return _profileHash;
            var names = new List<string>();
            foreach (var pkg in ContentPackageManager.EnabledPackages.All)
            {
                if (pkg == ContentPackageManager.VanillaCorePackage) continue;
                names.Add(pkg.Name);
            }
            names.Sort(StringComparer.Ordinal);
            uint hash = 2166136261;
            foreach (var name in names)
                foreach (char c in name)
                    hash = (hash ^ c) * 16777619;
            _profileHash = hash.ToString("x8");
            return _profileHash;
        }

        private static string GetProfileDir()
        {
            var modDir = Path.GetDirectoryName(typeof(OptimizerConfig).Assembly.Location);
            return Path.Combine(modDir ?? ".", "Optimizerlist");
        }

        internal static string GetProfilePath()
        {
            return Path.Combine(GetProfileDir(), $"profile_{GetModSetHash()}.xml");
        }

        internal static string GetThreadSafetyCachePath()
        {
            return Path.Combine(GetProfileDir(), $"thread_safety_{GetModSetHash()}.xml");
        }

        public static void SaveProfile()
        {
            try
            {
                var dir = GetProfileDir();
                Directory.CreateDirectory(dir);

                var modOptElement = new XElement("ModOptimization");
                foreach (var kv in ModOptSettings)
                {
                    modOptElement.Add(new XElement("Mod",
                        new XAttribute("name", kv.Key),
                        new XAttribute("critical", kv.Value[0]),
                        new XAttribute("active", kv.Value[1]),
                        new XAttribute("moderate", kv.Value[2]),
                        new XAttribute("static", kv.Value[3])));
                }

                var rulesElement = new XElement("ItemRules");
                foreach (var rule in ItemRules)
                {
                    if (string.IsNullOrWhiteSpace(rule.Identifier)) continue;
                    rulesElement.Add(new XElement("Rule",
                        new XAttribute("identifier", rule.Identifier),
                        new XAttribute("action", rule.Action.ToString()),
                        new XAttribute("skipFrames", rule.SkipFrames),
                        new XAttribute("condition", rule.Condition)));
                }

                var modsElement = new XElement("EnabledMods");
                foreach (var pkg in ContentPackageManager.EnabledPackages.All)
                {
                    if (pkg == ContentPackageManager.VanillaCorePackage) continue;
                    modsElement.Add(new XElement("Mod", new XAttribute("name", pkg.Name)));
                }

                var overridesElement = new XElement("ThreadSafetyOverrides");
                foreach (var kv in ThreadSafetyOverrides)
                    overridesElement.Add(new XElement("Item",
                        new XAttribute("id", kv.Key),
                        new XAttribute("tier", kv.Value)));

                var doc = new XDocument(
                    new XElement("OptimizerProfile",
                        new XAttribute("hash", GetModSetHash()),
                        modsElement,
                        modOptElement,
                        rulesElement,
                        overridesElement));
                doc.Save(GetProfilePath());
            }
            catch (Exception e)
            {
                Barotrauma.DebugConsole.ThrowError($"[ItemOptimizer] Failed to save profile: {e.Message}");
            }
        }

        public static void LoadProfile()
        {
            try
            {
                var path = GetProfilePath();
                if (!File.Exists(path)) return;

                var doc = XDocument.Load(path);
                var root = doc.Root;
                if (root == null) return;

                ModOptSettings.Clear();
                var modOptElement = root.Element("ModOptimization");
                if (modOptElement != null)
                {
                    foreach (var modEl in modOptElement.Elements("Mod"))
                    {
                        var name = modEl.Attribute("name")?.Value;
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        ModOptSettings[name] = new int[]
                        {
                            ParseInt(modEl.Attribute("critical")?.Value, 1, 1, 15),
                            ParseInt(modEl.Attribute("active")?.Value, 3, 1, 15),
                            ParseInt(modEl.Attribute("moderate")?.Value, 5, 1, 15),
                            ParseInt(modEl.Attribute("static")?.Value, 8, 1, 15)
                        };
                    }
                }

                ItemRules.Clear();
                var rulesElement = root.Element("ItemRules");
                if (rulesElement != null)
                {
                    foreach (var ruleEl in rulesElement.Elements("Rule"))
                    {
                        var rule = new ItemRule
                        {
                            Identifier = ruleEl.Attribute("identifier")?.Value ?? "",
                            SkipFrames = ParseInt(ruleEl.Attribute("skipFrames")?.Value, 3, 1, 30),
                            Condition = ruleEl.Attribute("condition")?.Value ?? "always"
                        };
                        var actionStr = ruleEl.Attribute("action")?.Value ?? "Skip";
                        rule.Action = actionStr.Equals("Throttle", StringComparison.OrdinalIgnoreCase)
                            ? ItemRuleAction.Throttle
                            : ItemRuleAction.Skip;
                        if (!string.IsNullOrWhiteSpace(rule.Identifier))
                            ItemRules.Add(rule);
                    }
                }

                BuildLookupTables();
                BuildModOptLookup();

                // Load thread safety overrides
                ThreadSafetyOverrides.Clear();
                var overridesEl = root.Element("ThreadSafetyOverrides");
                if (overridesEl != null)
                {
                    foreach (var el in overridesEl.Elements("Item"))
                    {
                        var id = el.Attribute("id")?.Value;
                        var tierStr = el.Attribute("tier")?.Value;
                        if (!string.IsNullOrWhiteSpace(id) && int.TryParse(tierStr, out int tierVal)
                            && tierVal >= 0 && tierVal <= 2)
                        {
                            ThreadSafetyOverrides[id] = tierVal;
                        }
                    }
                }
                // Sync overrides to analyzer
                ThreadSafetyAnalyzer.Overrides.Clear();
                foreach (var kv in ThreadSafetyOverrides)
                    ThreadSafetyAnalyzer.Overrides[kv.Key] = (ThreadSafetyTier)kv.Value;
            }
            catch (Exception e)
            {
                Barotrauma.DebugConsole.ThrowError($"[ItemOptimizer] Failed to load profile: {e.Message}");
            }
        }

        /// <summary>Auto-save: called after any optimization change.</summary>
        public static void AutoSave()
        {
            Save();
            SaveProfile();
        }

        private static string _configPath;

        private static string GetConfigPath()
        {
            if (_configPath != null) return _configPath;
            var modDir = Path.GetDirectoryName(
                typeof(OptimizerConfig).Assembly.Location);
            if (string.IsNullOrEmpty(modDir))
                modDir = "LocalMods/ItemOptimizer";
            _configPath = Path.Combine(modDir, "ItemOptimizer_config.xml");
            return _configPath;
        }

        public static void Load()
        {
            var path = GetConfigPath();
            if (!File.Exists(path))
            {
                Save();
                return;
            }

            try
            {
                var doc = XDocument.Load(path);
                var root = doc.Root;
                if (root == null) return;

                var cs = root.Element("ColdStorageSkip");
                if (cs != null)
                    EnableColdStorageSkip = ParseBool(cs.Attribute("enabled")?.Value, true);

                var gi = root.Element("GroundItemThrottle");
                if (gi != null)
                {
                    EnableGroundItemThrottle = ParseBool(gi.Attribute("enabled")?.Value, true);
                    GroundItemSkipFrames = ParseInt(gi.Attribute("skipFrames")?.Value, 3, 1, 30);
                }

                var ci = root.Element("CustomInterfaceThrottle");
                if (ci != null)
                    EnableCustomInterfaceThrottle = ParseBool(ci.Attribute("enabled")?.Value, true);

                var ms = root.Element("MotionSensorThrottle");
                if (ms != null)
                {
                    EnableMotionSensorThrottle = ParseBool(ms.Attribute("enabled")?.Value, true);
                    MotionSensorSkipFrames = ParseInt(ms.Attribute("skipFrames")?.Value, 3, 1, 30);
                }

                var we = root.Element("WearableThrottle");
                if (we != null)
                {
                    EnableWearableThrottle = ParseBool(we.Attribute("enabled")?.Value, true);
                    WearableSkipFrames = ParseInt(we.Attribute("skipFrames")?.Value, 3, 1, 30);
                }

                var wd = root.Element("WaterDetectorThrottle");
                if (wd != null)
                {
                    EnableWaterDetectorThrottle = ParseBool(wd.Attribute("enabled")?.Value, true);
                    WaterDetectorSkipFrames = ParseInt(wd.Attribute("skipFrames")?.Value, 3, 1, 30);
                }

                var dr = root.Element("DoorThrottle");
                if (dr != null)
                {
                    EnableDoorThrottle = ParseBool(dr.Attribute("enabled")?.Value, true);
                    DoorSkipFrames = ParseInt(dr.Attribute("skipFrames")?.Value, 2, 1, 30);
                }

                var hst = root.Element("HasStatusTagCache");
                if (hst != null)
                    EnableHasStatusTagCache = ParseBool(hst.Attribute("enabled")?.Value, true);

                var shud = root.Element("StatusHUDThrottle");
                if (shud != null)
                {
                    EnableStatusHUDThrottle = ParseBool(shud.Attribute("enabled")?.Value, true);
                    StatusHUDScanInterval = ParseFloat(shud.Attribute("scanInterval")?.Value, 1.5f, 0.5f, 5.0f);
                    StatusHUDDrawSkipFrames = ParseInt(shud.Attribute("drawSkipFrames")?.Value, 3, 1, 10);
                }

                var afd = root.Element("AfflictionDedup");
                if (afd != null)
                    EnableAfflictionDedup = ParseBool(afd.Attribute("enabled")?.Value, true);

                var spike = root.Element("SpikeDetector");
                if (spike != null)
                {
                    EnableSpikeDetector = ParseBool(spike.Attribute("enabled")?.Value, true);
                    SpikeThresholdMs = ParseFloat(spike.Attribute("thresholdMs")?.Value, 30f, 5f, 1000f);
                }

                var pd = root.Element("ParallelDispatch");
                if (pd != null)
                {
                    EnableParallelDispatch = ParseBool(pd.Attribute("enabled")?.Value, false);
                    ParallelWorkerCount = ParseInt(pd.Attribute("workers")?.Value, 2, 1, 6);
                }

                // Load item rules
                ItemRules.Clear();
                var rulesElement = root.Element("ItemRules");
                if (rulesElement != null)
                {
                    foreach (var ruleEl in rulesElement.Elements("Rule"))
                    {
                        var rule = new ItemRule
                        {
                            Identifier = ruleEl.Attribute("identifier")?.Value ?? "",
                            SkipFrames = ParseInt(ruleEl.Attribute("skipFrames")?.Value, 3, 1, 30),
                            Condition = ruleEl.Attribute("condition")?.Value ?? "always"
                        };

                        var actionStr = ruleEl.Attribute("action")?.Value ?? "Skip";
                        rule.Action = actionStr.Equals("Throttle", StringComparison.OrdinalIgnoreCase)
                            ? ItemRuleAction.Throttle
                            : ItemRuleAction.Skip;

                        if (!string.IsNullOrWhiteSpace(rule.Identifier))
                            ItemRules.Add(rule);
                    }
                }

                // Load mod optimization settings
                ModOptSettings.Clear();
                var modOptElement = root.Element("ModOptimization");
                if (modOptElement != null)
                {
                    foreach (var modEl in modOptElement.Elements("Mod"))
                    {
                        var name = modEl.Attribute("name")?.Value;
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        var skips = new int[]
                        {
                            ParseInt(modEl.Attribute("critical")?.Value, 1, 1, 15),
                            ParseInt(modEl.Attribute("active")?.Value, 3, 1, 15),
                            ParseInt(modEl.Attribute("moderate")?.Value, 5, 1, 15),
                            ParseInt(modEl.Attribute("static")?.Value, 8, 1, 15)
                        };
                        ModOptSettings[name] = skips;
                    }
                }

                BuildLookupTables();
                BuildModOptLookup();
                // Load profile (overrides ItemRules + ModOpt if profile exists)
                LoadProfile();
                // Attempt to load thread safety cache
                ThreadSafetyAnalyzer.LoadCache();
            }
            catch (Exception e)
            {
                Barotrauma.DebugConsole.ThrowError($"[ItemOptimizer] Failed to load config: {e.Message}");
            }
        }

        public static void Save()
        {
            try
            {
                var rulesElement = new XElement("ItemRules");
                foreach (var rule in ItemRules)
                {
                    if (string.IsNullOrWhiteSpace(rule.Identifier)) continue;
                    rulesElement.Add(new XElement("Rule",
                        new XAttribute("identifier", rule.Identifier),
                        new XAttribute("action", rule.Action.ToString()),
                        new XAttribute("skipFrames", rule.SkipFrames),
                        new XAttribute("condition", rule.Condition)));
                }

                var modOptElement = new XElement("ModOptimization");
                foreach (var kv in ModOptSettings)
                {
                    modOptElement.Add(new XElement("Mod",
                        new XAttribute("name", kv.Key),
                        new XAttribute("critical", kv.Value[0]),
                        new XAttribute("active", kv.Value[1]),
                        new XAttribute("moderate", kv.Value[2]),
                        new XAttribute("static", kv.Value[3])));
                }

                var doc = new XDocument(
                    new XElement("ItemOptimizerConfig",
                        new XElement("ColdStorageSkip",
                            new XAttribute("enabled", EnableColdStorageSkip)),
                        new XElement("GroundItemThrottle",
                            new XAttribute("enabled", EnableGroundItemThrottle),
                            new XAttribute("skipFrames", GroundItemSkipFrames)),
                        new XElement("CustomInterfaceThrottle",
                            new XAttribute("enabled", EnableCustomInterfaceThrottle)),
                        new XElement("MotionSensorThrottle",
                            new XAttribute("enabled", EnableMotionSensorThrottle),
                            new XAttribute("skipFrames", MotionSensorSkipFrames)),
                        new XElement("WearableThrottle",
                            new XAttribute("enabled", EnableWearableThrottle),
                            new XAttribute("skipFrames", WearableSkipFrames)),
                        new XElement("WaterDetectorThrottle",
                            new XAttribute("enabled", EnableWaterDetectorThrottle),
                            new XAttribute("skipFrames", WaterDetectorSkipFrames)),
                        new XElement("DoorThrottle",
                            new XAttribute("enabled", EnableDoorThrottle),
                            new XAttribute("skipFrames", DoorSkipFrames)),
                        new XElement("HasStatusTagCache",
                            new XAttribute("enabled", EnableHasStatusTagCache)),
                        new XElement("StatusHUDThrottle",
                            new XAttribute("enabled", EnableStatusHUDThrottle),
                            new XAttribute("scanInterval", StatusHUDScanInterval),
                            new XAttribute("drawSkipFrames", StatusHUDDrawSkipFrames)),
                        new XElement("AfflictionDedup",
                            new XAttribute("enabled", EnableAfflictionDedup)),
                        new XElement("SpikeDetector",
                            new XAttribute("enabled", EnableSpikeDetector),
                            new XAttribute("thresholdMs", SpikeThresholdMs)),
                        new XElement("ParallelDispatch",
                            new XAttribute("enabled", EnableParallelDispatch),
                            new XAttribute("workers", ParallelWorkerCount)),
                        rulesElement,
                        modOptElement
                    ));
                doc.Save(GetConfigPath());
            }
            catch (Exception e)
            {
                Barotrauma.DebugConsole.ThrowError($"[ItemOptimizer] Failed to save config: {e.Message}");
            }
        }

        public static void BuildLookupTables()
        {
            RuleLookup.Clear();
            foreach (var rule in ItemRules)
            {
                if (!string.IsNullOrWhiteSpace(rule.Identifier))
                    RuleLookup[rule.Identifier] = rule;
            }
        }

        private static bool ParseBool(string val, bool def)
        {
            if (string.IsNullOrEmpty(val)) return def;
            return bool.TryParse(val, out var result) ? result : def;
        }

        private static int ParseInt(string val, int def, int min, int max)
        {
            if (string.IsNullOrEmpty(val)) return def;
            if (!int.TryParse(val, out var result)) return def;
            return Math.Clamp(result, min, max);
        }

        private static float ParseFloat(string val, float def, float min, float max)
        {
            if (string.IsNullOrEmpty(val)) return def;
            if (!float.TryParse(val, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var result)) return def;
            return Math.Clamp(result, min, max);
        }
    }
}
