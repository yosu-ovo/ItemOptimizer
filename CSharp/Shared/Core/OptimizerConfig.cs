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

    /// <summary>
    /// Per-mod optimization profile: tier base skip frames + intensity slider.
    /// Intensity 0→use tier bases as-is, 1→all tiers converge to MaxSkip.
    /// </summary>
    public class ModOptProfile
    {
        public int[] TierBases = { 1, 3, 5, 8 };
        public float Intensity = 0f;  // 0~1

        public const int MaxSkip = 15;

        public int GetEffectiveSkip(int tier)
        {
            int baseVal = TierBases[tier];
            return baseVal + (int)Math.Round((MaxSkip - baseVal) * Intensity);
        }
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
        public static bool EnableMotionSensorRewrite = true;   // Replaces MotionSensorThrottle + MotionSensorOpt
        public static bool EnableWaterDetectorRewrite = true;  // Replaces WaterDetectorThrottle + WaterDetectorOpt
        public static bool EnableRelayRewrite = true;           // Replaces RelayOpt
        public static bool EnablePowerTransferRewrite = true;   // JunctionBox etc.
        public static bool EnablePowerContainerRewrite = true;  // Battery/Supercapacitor
        public static bool EnableWireSkip = true;
        public static bool EnableHasStatusTagCache = true;
        public static bool EnableHullSpatialIndex = true;   // Hull-based spatial pre-filtering for MotionSensor
        public static bool EnableStatusHUDThrottle = true;
        public static bool EnableAfflictionDedup = true;

        // ── Character optimization ──
        public static bool EnableAnimLOD = true;
        public static bool EnableCharacterStagger = true;
        public static int CharacterStaggerGroups = 4;
        public static bool EnableLadderFix = true;          // fix ladder climbing desync (client-only)
        public static bool EnablePlatformFix = true;        // fix IgnorePlatforms desync (client-only)

        public static int MotionSensorSkipFrames = 9;
        public static int WearableSkipFrames = 3;
        public static int WaterDetectorSkipFrames = 9;
        public static int DoorSkipFrames = 2;
        public static float StatusHUDScanInterval = 1.5f;
        public static int StatusHUDDrawSkipFrames = 3;

        // Proxy item system (batch compute + sync architecture)
        public static bool EnableProxySystem = true;

        // ── Client optimization ──
        public static bool EnableInteractionLabelOpt = true;
        public static int InteractionLabelMaxCount = 50; // 10-200
        public static bool EnableRelayOpt = true;
        public static bool EnableMotionSensorOpt = true;
        public static bool EnableWaterDetectorOpt = true;
        public static bool EnableButtonTerminalOpt = true;
        public static bool EnablePumpOpt = true;

        // Misc entity parallelism (Hull/Structure/Gap/Power — safe, no side effects)
        public static bool EnableMiscParallel = true;

        // Signal graph accelerator (0=Off, 1=Accelerate, 2=Aggressive)
        public static int SignalGraphMode = 2;

        // NativeComponent runtime (experimental — default off)
        public static bool EnableNativeRuntime = false;

        // Spike detector (off by default — adds ~1-2ms overhead when enabled)
        public static bool EnableSpikeDetector = false;
        public static float SpikeThresholdMs = 30f;

        // ── Server-side optimizations ──
        public static bool EnableServerHashSetDedup = true;
        public static float MetricSendInterval = 0.5f;
        public static bool AllowClientSync = false;

        // Per-item rules (manual, user-defined)
        public static List<ItemRule> ItemRules = new();

        // Pre-compiled lookup tables for fast per-frame checks
        public static readonly Dictionary<string, ItemRule> RuleLookup = new();

        // ── Whitelist (items that should never be throttled by ModOpt) ──
        public static List<string> Whitelist = new();
        public static HashSet<string> WhitelistLookup = new(StringComparer.Ordinal);

        public static void RebuildWhitelistLookup()
        {
            WhitelistLookup = new HashSet<string>(Whitelist, StringComparer.Ordinal);
        }

        // ── Mod Optimization (tier-based, separate from manual rules) ──
        // Persistence: packageName → ModOptProfile { tierBases[4], intensity }
        public static readonly Dictionary<string, ModOptProfile> ModOptProfiles = new(StringComparer.Ordinal);
        // Runtime flat lookup: identifier → skipFrames (built from ModOptProfiles + prefab classification)
        public static volatile Dictionary<string, int> ModOptLookup = new(StringComparer.Ordinal);

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
        /// Rebuild ModOptLookup from ModOptProfiles by scanning all non-vanilla prefabs.
        /// Only items whose package is in ModOptProfiles get an entry.
        /// Items with effective skipFrames &lt;= 1 are excluded (no throttle needed).
        /// </summary>
        public static void BuildModOptLookup()
        {
            var newLookup = new Dictionary<string, int>(StringComparer.Ordinal);
            if (ModOptProfiles.Count == 0)
            {
                ModOptLookup = newLookup;
                return;
            }

            foreach (ItemPrefab prefab in ItemPrefab.Prefabs)
            {
                var pkg = prefab.ContentPackage;
                if (pkg == null || pkg == ContentPackageManager.VanillaCorePackage) continue;

                if (!ModOptProfiles.TryGetValue(pkg.Name, out var profile)) continue;

                int tier = ClassifyItemPrefab(prefab);
                int skip = profile.GetEffectiveSkip(tier);
                if (skip <= 1) continue; // no throttle

                newLookup[prefab.Identifier.Value] = skip;
            }

            ModOptLookup = newLookup; // atomic reference swap
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
            return Path.Combine(ModPaths.ModDir, "Optimizerlist");
        }

        internal static string GetProfilePath()
        {
            return Path.Combine(GetProfileDir(), $"profile_{GetModSetHash()}.xml");
        }


        public static void SaveProfile()
        {
            try
            {
                var dir = GetProfileDir();
                Directory.CreateDirectory(dir);

                var modOptElement = SerializeModOpt();
                var rulesElement = SerializeItemRules();
                var whitelistElement = SerializeWhitelist();

                var modsElement = new XElement("EnabledMods");
                foreach (var pkg in ContentPackageManager.EnabledPackages.All)
                {
                    if (pkg == ContentPackageManager.VanillaCorePackage) continue;
                    modsElement.Add(new XElement("Mod", new XAttribute("name", pkg.Name)));
                }

                var doc = new XDocument(
                    new XElement("OptimizerProfile",
                        new XAttribute("hash", GetModSetHash()),
                        modsElement,
                        modOptElement,
                        rulesElement,
                        whitelistElement));
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

                DeserializeModOpt(root);
                DeserializeItemRules(root);
                DeserializeWhitelist(root);

                BuildLookupTables();
                BuildModOptLookup();
                RebuildWhitelistLookup();
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
            _configPath = ModPaths.Resolve("ItemOptimizer_config.xml");
            return _configPath;
        }

        public static void Load()
        {
            try
            {
                var path = GetConfigPath();
                if (!File.Exists(path))
                {
                    Save();
                    return;
                }

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

                var hsi = root.Element("HullSpatialIndex");
                if (hsi != null)
                    EnableHullSpatialIndex = ParseBool(hsi.Attribute("enabled")?.Value, true);

                var wsk = root.Element("WireSkip");
                if (wsk != null)
                    EnableWireSkip = ParseBool(wsk.Attribute("enabled")?.Value, false);

                var msRw = root.Element("MotionSensorRewrite");
                if (msRw != null)
                    EnableMotionSensorRewrite = ParseBool(msRw.Attribute("enabled")?.Value, true);

                var wdRw = root.Element("WaterDetectorRewrite");
                if (wdRw != null)
                    EnableWaterDetectorRewrite = ParseBool(wdRw.Attribute("enabled")?.Value, true);

                var relayRw = root.Element("RelayRewrite");
                if (relayRw != null)
                    EnableRelayRewrite = ParseBool(relayRw.Attribute("enabled")?.Value, true);

                var ptRw = root.Element("PowerTransferRewrite");
                if (ptRw != null)
                    EnablePowerTransferRewrite = ParseBool(ptRw.Attribute("enabled")?.Value, true);

                var pcRw = root.Element("PowerContainerRewrite");
                if (pcRw != null)
                    EnablePowerContainerRewrite = ParseBool(pcRw.Attribute("enabled")?.Value, true);

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

                var animLod = root.Element("AnimLOD");
                if (animLod != null)
                    EnableAnimLOD = ParseBool(animLod.Attribute("enabled")?.Value, true);

                var charStagger = root.Element("CharacterStagger");
                if (charStagger != null)
                {
                    EnableCharacterStagger = ParseBool(charStagger.Attribute("enabled")?.Value, false);
                    CharacterStaggerGroups = ParseInt(charStagger.Attribute("groups")?.Value, 3, 2, 8);
                }

                var ladderFix = root.Element("LadderFix");
                if (ladderFix != null)
                    EnableLadderFix = ParseBool(ladderFix.Attribute("enabled")?.Value, true);

                var platformFix = root.Element("PlatformFix");
                if (platformFix != null)
                    EnablePlatformFix = ParseBool(platformFix.Attribute("enabled")?.Value, true);

                var spike = root.Element("SpikeDetector");
                if (spike != null)
                {
                    EnableSpikeDetector = ParseBool(spike.Attribute("enabled")?.Value, false);
                    SpikeThresholdMs = ParseFloat(spike.Attribute("thresholdMs")?.Value, 30f, 5f, 1000f);
                }

                var sga = root.Element("SignalGraphAccel");
                if (sga != null)
                    SignalGraphMode = ParseInt(sga.Attribute("mode")?.Value, 0, 0, 2);

                var nrt = root.Element("NativeRuntime");
                if (nrt != null)
                    EnableNativeRuntime = ParseBool(nrt.Attribute("enabled")?.Value, false);

                var proxy = root.Element("ProxySystem");
                if (proxy != null)
                    EnableProxySystem = bool.TryParse(proxy.Attribute("enabled")?.Value, out var v) ? v : true;

                var intLabel = root.Element("InteractionLabel");
                if (intLabel != null)
                {
                    EnableInteractionLabelOpt = ParseBool(intLabel.Attribute("enabled")?.Value, true);
                    InteractionLabelMaxCount = ParseInt(intLabel.Attribute("maxCount")?.Value, 50, 10, 200);
                }

                var relayOpt = root.Element("RelayOpt");
                if (relayOpt != null)
                    EnableRelayOpt = ParseBool(relayOpt.Attribute("enabled")?.Value, true);
                var motionOpt = root.Element("MotionSensorOpt");
                if (motionOpt != null)
                    EnableMotionSensorOpt = ParseBool(motionOpt.Attribute("enabled")?.Value, true);
                var waterDetOpt = root.Element("WaterDetectorOpt");
                if (waterDetOpt != null)
                    EnableWaterDetectorOpt = ParseBool(waterDetOpt.Attribute("enabled")?.Value, true);
                var btnTermOpt = root.Element("ButtonTerminalOpt");
                if (btnTermOpt != null)
                    EnableButtonTerminalOpt = ParseBool(btnTermOpt.Attribute("enabled")?.Value, true);
                var pumpOpt = root.Element("PumpOpt");
                if (pumpOpt != null)
                    EnablePumpOpt = ParseBool(pumpOpt.Attribute("enabled")?.Value, true);

                var mp = root.Element("MiscParallel");
                if (mp != null)
                {
                    EnableMiscParallel = ParseBool(mp.Attribute("enabled")?.Value, true);
                }

                var srv = root.Element("ServerOptimization");
                if (srv != null)
                {
                    EnableServerHashSetDedup = ParseBool(srv.Attribute("enabled")?.Value, true);
                    MetricSendInterval = ParseFloat(srv.Attribute("metricInterval")?.Value, 0.5f, 0.1f, 5f);
                    AllowClientSync = ParseBool(srv.Attribute("allowClientSync")?.Value, false);
                }

                DeserializeItemRules(root);
                DeserializeModOpt(root);
                DeserializeWhitelist(root);

                // Load profile (overrides ItemRules + ModOpt + Whitelist if profile exists)
                LoadProfile();
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
                var rulesElement = SerializeItemRules();
                var modOptElement = SerializeModOpt();
                var whitelistElement = SerializeWhitelist();

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
                        new XElement("HullSpatialIndex",
                            new XAttribute("enabled", EnableHullSpatialIndex)),
                        new XElement("StatusHUDThrottle",
                            new XAttribute("enabled", EnableStatusHUDThrottle),
                            new XAttribute("scanInterval", StatusHUDScanInterval),
                            new XAttribute("drawSkipFrames", StatusHUDDrawSkipFrames)),
                        new XElement("AfflictionDedup",
                            new XAttribute("enabled", EnableAfflictionDedup)),
                        new XElement("WireSkip",
                            new XAttribute("enabled", EnableWireSkip)),
                        new XElement("MotionSensorRewrite",
                            new XAttribute("enabled", EnableMotionSensorRewrite)),
                        new XElement("WaterDetectorRewrite",
                            new XAttribute("enabled", EnableWaterDetectorRewrite)),
                        new XElement("RelayRewrite",
                            new XAttribute("enabled", EnableRelayRewrite)),
                        new XElement("PowerTransferRewrite",
                            new XAttribute("enabled", EnablePowerTransferRewrite)),
                        new XElement("PowerContainerRewrite",
                            new XAttribute("enabled", EnablePowerContainerRewrite)),
                        new XElement("AnimLOD",
                            new XAttribute("enabled", EnableAnimLOD)),
                        new XElement("CharacterStagger",
                            new XAttribute("enabled", EnableCharacterStagger),
                            new XAttribute("groups", CharacterStaggerGroups)),
                        new XElement("LadderFix",
                            new XAttribute("enabled", EnableLadderFix)),
                        new XElement("PlatformFix",
                            new XAttribute("enabled", EnablePlatformFix)),
                        new XElement("SpikeDetector",
                            new XAttribute("enabled", EnableSpikeDetector),
                            new XAttribute("thresholdMs", SpikeThresholdMs)),
                        new XElement("SignalGraphAccel",
                            new XAttribute("mode", SignalGraphMode)),
                        new XElement("NativeRuntime",
                            new XAttribute("enabled", EnableNativeRuntime)),
                        new XElement("ProxySystem",
                            new XAttribute("enabled", EnableProxySystem)),
                        new XElement("InteractionLabel",
                            new XAttribute("enabled", EnableInteractionLabelOpt),
                            new XAttribute("maxCount", InteractionLabelMaxCount)),
                        new XElement("RelayOpt",
                            new XAttribute("enabled", EnableRelayOpt)),
                        new XElement("MotionSensorOpt",
                            new XAttribute("enabled", EnableMotionSensorOpt)),
                        new XElement("WaterDetectorOpt",
                            new XAttribute("enabled", EnableWaterDetectorOpt)),
                        new XElement("ButtonTerminalOpt",
                            new XAttribute("enabled", EnableButtonTerminalOpt)),
                        new XElement("PumpOpt",
                            new XAttribute("enabled", EnablePumpOpt)),
                        new XElement("MiscParallel",
                            new XAttribute("enabled", EnableMiscParallel)),
                        new XElement("ServerOptimization",
                            new XAttribute("enabled", EnableServerHashSetDedup),
                            new XAttribute("metricInterval", MetricSendInterval),
                            new XAttribute("allowClientSync", AllowClientSync)),
                        rulesElement,
                        modOptElement,
                        whitelistElement
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

        // ── Serialization Helpers ──

        private static XElement SerializeItemRules()
        {
            var el = new XElement("ItemRules");
            foreach (var rule in ItemRules)
            {
                if (string.IsNullOrWhiteSpace(rule.Identifier)) continue;
                el.Add(new XElement("Rule",
                    new XAttribute("identifier", rule.Identifier),
                    new XAttribute("action", rule.Action.ToString()),
                    new XAttribute("skipFrames", rule.SkipFrames),
                    new XAttribute("condition", rule.Condition)));
            }
            return el;
        }

        private static XElement SerializeModOpt()
        {
            var el = new XElement("ModOptimization");
            foreach (var kv in ModOptProfiles)
            {
                var profile = kv.Value;
                el.Add(new XElement("Mod",
                    new XAttribute("name", kv.Key),
                    new XAttribute("critical", profile.TierBases[0]),
                    new XAttribute("active", profile.TierBases[1]),
                    new XAttribute("moderate", profile.TierBases[2]),
                    new XAttribute("static", profile.TierBases[3]),
                    new XAttribute("intensity", profile.Intensity.ToString("F2",
                        System.Globalization.CultureInfo.InvariantCulture))));
            }
            return el;
        }

        private static void DeserializeItemRules(XElement root)
        {
            ItemRules.Clear();
            var rulesElement = root.Element("ItemRules");
            if (rulesElement == null) return;
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

        private static void DeserializeModOpt(XElement root)
        {
            ModOptProfiles.Clear();
            var modOptElement = root.Element("ModOptimization");
            if (modOptElement == null) return;
            foreach (var modEl in modOptElement.Elements("Mod"))
            {
                var name = modEl.Attribute("name")?.Value;
                if (string.IsNullOrWhiteSpace(name)) continue;
                var profile = new ModOptProfile
                {
                    TierBases = new int[]
                    {
                        ParseInt(modEl.Attribute("critical")?.Value, 1, 1, 15),
                        ParseInt(modEl.Attribute("active")?.Value, 3, 1, 15),
                        ParseInt(modEl.Attribute("moderate")?.Value, 5, 1, 15),
                        ParseInt(modEl.Attribute("static")?.Value, 8, 1, 15)
                    },
                    // Backward compat: old configs have no intensity attribute → defaults to 0
                    Intensity = ParseFloat(modEl.Attribute("intensity")?.Value, 0f, 0f, 1f)
                };
                ModOptProfiles[name] = profile;
            }
        }

        private static XElement SerializeWhitelist()
        {
            var el = new XElement("Whitelist");
            foreach (var id in Whitelist)
            {
                if (string.IsNullOrWhiteSpace(id)) continue;
                el.Add(new XElement("Item", new XAttribute("identifier", id)));
            }
            return el;
        }

        private static void DeserializeWhitelist(XElement root)
        {
            Whitelist.Clear();
            var whitelistElement = root.Element("Whitelist");
            if (whitelistElement == null) return;
            foreach (var itemEl in whitelistElement.Elements("Item"))
            {
                var id = itemEl.Attribute("identifier")?.Value;
                if (!string.IsNullOrWhiteSpace(id))
                    Whitelist.Add(id);
            }
            RebuildWhitelistLookup();
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
