using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Microsoft.Xna.Framework;

namespace ItemOptimizerMod
{
    static class SettingsPanel
    {
        private static GUIFrame frame;
        private static GUIListBox listBox;
        private static readonly List<RuleRow> ruleRows = new();

        private class RuleRow
        {
            public GUIFrame Container;
            public GUITextBox IdentifierBox;
            public GUIDropDown ActionDropDown;
            public GUINumberInput SkipInput;
            public GUIDropDown ConditionDropDown;
            public ItemRule Rule;
        }

        // ── Activity Tier System ──

        private enum ActivityTier { Critical, Active, Moderate, Static }

        private static readonly (Color Color, string LabelKey, int RecommendedSkip)[] TierMeta =
        {
            (new Color(220, 50, 50),  "tier_critical", 1),  // Critical
            (new Color(255, 165, 0),  "tier_active",   3),  // Active
            (new Color(220, 220, 50), "tier_moderate", 5),  // Moderate
            (new Color(50, 200, 50),  "tier_static",   8),  // Static
        };

        private class ModTierInfo
        {
            public ActivityTier Tier;
            public List<ModItemInfo> Items = new();
            public int RecommendedSkip;
            public int CurrentSkip;
        }

        // ── Mod Data Model ──

        private class ModInfo
        {
            public ContentPackage Package;
            public string Name;
            public List<ModItemInfo> Items = new();
            public bool IsExpanded;
            public bool IsDetailExpanded;
            public Dictionary<ActivityTier, ModTierInfo> Tiers = new();
        }

        private class ModItemInfo
        {
            public ItemPrefab Prefab;
            public string Identifier;
            public List<string> DetectedPatterns = new();
            public int StatusEffectCount;
            public ActivityTier Tier;
        }

        private static List<ModInfo> _cachedMods;

        private static ActivityTier ClassifyItem(ModItemInfo item)
        {
            bool hasStatusHUD   = item.DetectedPatterns.Contains("StatusHUD");
            bool hasMultiSE     = item.DetectedPatterns.Contains("MultiSE");
            bool hasAffliction  = item.DetectedPatterns.Contains("Affliction");
            bool hasConditional = item.DetectedPatterns.Contains("Conditional");

            if (hasStatusHUD) return ActivityTier.Critical;
            if (hasMultiSE || (hasAffliction && hasConditional)) return ActivityTier.Active;
            if (hasAffliction || hasConditional || item.StatusEffectCount > 2) return ActivityTier.Moderate;
            return ActivityTier.Static;
        }

        private static List<ModInfo> ScanMods()
        {
            if (_cachedMods != null) return _cachedMods;

            var modMap = new Dictionary<ContentPackage, ModInfo>();

            foreach (ItemPrefab prefab in ItemPrefab.Prefabs)
            {
                var pkg = prefab.ContentPackage;
                if (pkg == null) continue;
                if (pkg == ContentPackageManager.VanillaCorePackage) continue;

                if (!modMap.TryGetValue(pkg, out var modInfo))
                {
                    modInfo = new ModInfo { Package = pkg, Name = pkg.Name };
                    modMap[pkg] = modInfo;
                }

                var itemInfo = new ModItemInfo
                {
                    Prefab = prefab,
                    Identifier = prefab.Identifier.Value
                };

                // Detect expensive patterns by scanning the XML ConfigElement
                var configEl = prefab.ConfigElement;
                if (configEl != null)
                {
                    int statusEffectCount = 0;

                    foreach (var compEl in configEl.Elements())
                    {
                        var compName = compEl.Name.ToString();

                        if (compName.Equals("StatusHUD", StringComparison.OrdinalIgnoreCase))
                            itemInfo.DetectedPatterns.Add("StatusHUD");

                        foreach (var subEl in compEl.Elements())
                        {
                            var subName = subEl.Name.ToString();

                            if (subName.Equals("statuseffect", StringComparison.OrdinalIgnoreCase))
                            {
                                statusEffectCount++;
                                var typeAttr = subEl.GetAttributeString("type", "OnActive");
                                bool isOnActiveOrAlways = typeAttr.Equals("OnActive", StringComparison.OrdinalIgnoreCase)
                                    || typeAttr.Equals("Always", StringComparison.OrdinalIgnoreCase);

                                if (isOnActiveOrAlways)
                                {
                                    foreach (var seChild in subEl.Elements())
                                    {
                                        if (seChild.Name.ToString().Equals("Affliction", StringComparison.OrdinalIgnoreCase))
                                        {
                                            if (!itemInfo.DetectedPatterns.Contains("Affliction"))
                                                itemInfo.DetectedPatterns.Add("Affliction");
                                            break;
                                        }
                                    }
                                }
                            }
                            else if (subName.Equals("activeconditional", StringComparison.OrdinalIgnoreCase)
                                  || subName.Equals("isactiveconditional", StringComparison.OrdinalIgnoreCase)
                                  || subName.Equals("isactive", StringComparison.OrdinalIgnoreCase))
                            {
                                if (!itemInfo.DetectedPatterns.Contains("Conditional"))
                                    itemInfo.DetectedPatterns.Add("Conditional");
                            }
                        }
                    }

                    if (statusEffectCount > 5 && !itemInfo.DetectedPatterns.Contains("MultiSE"))
                        itemInfo.DetectedPatterns.Add("MultiSE");

                    itemInfo.StatusEffectCount = statusEffectCount;
                }

                itemInfo.Tier = ClassifyItem(itemInfo);
                modInfo.Items.Add(itemInfo);
            }

            // Build tier aggregation for each mod
            foreach (var mod in modMap.Values)
            {
                // Check if we have saved settings for this mod
                OptimizerConfig.ModOptSettings.TryGetValue(mod.Name, out var savedSkips);

                foreach (ActivityTier tier in Enum.GetValues(typeof(ActivityTier)))
                {
                    var meta = TierMeta[(int)tier];
                    int currentSkip = savedSkips != null ? savedSkips[(int)tier] : meta.RecommendedSkip;
                    mod.Tiers[tier] = new ModTierInfo
                    {
                        Tier = tier,
                        RecommendedSkip = meta.RecommendedSkip,
                        CurrentSkip = currentSkip,
                        Items = new List<ModItemInfo>()
                    };
                }
                foreach (var item in mod.Items)
                    mod.Tiers[item.Tier].Items.Add(item);
            }

            _cachedMods = modMap.Values
                .Where(m => m.Items.Count > 0)
                .OrderByDescending(m => m.Items.Count)
                .ToList();

            return _cachedMods;
        }

        // ── Rule Application Methods (ModOpt system) ──

        private static void ApplyTierRules(ModInfo mod, ActivityTier tier)
        {
            var tierInfo = mod.Tiers[tier];
            int skip = tierInfo.CurrentSkip;

            // Get or create the tier skips array for this package
            if (!OptimizerConfig.ModOptSettings.TryGetValue(mod.Name, out var tierSkips))
            {
                tierSkips = new int[] { 1, 1, 1, 1 }; // default: no throttle
                OptimizerConfig.ModOptSettings[mod.Name] = tierSkips;
            }

            tierSkips[(int)tier] = skip;

            // If all tiers are 1 (no throttle), remove the entry entirely
            if (tierSkips[0] <= 1 && tierSkips[1] <= 1 && tierSkips[2] <= 1 && tierSkips[3] <= 1)
                OptimizerConfig.ModOptSettings.Remove(mod.Name);

            OptimizerConfig.BuildModOptLookup();
            ItemOptimizerPlugin.SyncItemUpdatePatch();
        }

        private static void ApplyModRecommended(ModInfo mod)
        {
            var tierSkips = new int[4];
            foreach (ActivityTier tier in Enum.GetValues(typeof(ActivityTier)))
            {
                var tierInfo = mod.Tiers[tier];
                tierInfo.CurrentSkip = tierInfo.RecommendedSkip;
                tierSkips[(int)tier] = tierInfo.RecommendedSkip;
            }

            // Only store if at least one tier throttles
            if (tierSkips[0] > 1 || tierSkips[1] > 1 || tierSkips[2] > 1 || tierSkips[3] > 1)
                OptimizerConfig.ModOptSettings[mod.Name] = tierSkips;
            else
                OptimizerConfig.ModOptSettings.Remove(mod.Name);

            OptimizerConfig.BuildModOptLookup();
            ItemOptimizerPlugin.SyncItemUpdatePatch();
        }

        private static void ApplyAllRecommended()
        {
            var mods = ScanMods();
            foreach (var mod in mods)
                ApplyModRecommended(mod);
            OptimizerConfig.AutoSave();
            Rebuild();
        }

        private static void ClearModRules(ModInfo mod)
        {
            OptimizerConfig.ModOptSettings.Remove(mod.Name);
            OptimizerConfig.BuildModOptLookup();
            ItemOptimizerPlugin.SyncItemUpdatePatch();
            OptimizerConfig.AutoSave();
        }

        private static void ClearAllModRules()
        {
            OptimizerConfig.ModOptSettings.Clear();
            OptimizerConfig.BuildModOptLookup();
            ItemOptimizerPlugin.SyncItemUpdatePatch();
            OptimizerConfig.AutoSave();
            Rebuild();
        }

        private static int CountConfiguredInTier(ModTierInfo tierInfo)
        {
            int count = 0;
            foreach (var item in tierInfo.Items)
                if (OptimizerConfig.ModOptLookup.ContainsKey(item.Identifier))
                    count++;
            return count;
        }

        private static int CountConfiguredInMod(ModInfo mod)
        {
            int count = 0;
            foreach (var item in mod.Items)
                if (OptimizerConfig.ModOptLookup.ContainsKey(item.Identifier))
                    count++;
            return count;
        }

        // ── Panel Lifecycle ──

        public static bool IsOpen => frame != null;

        public static void Show()
        {
            if (frame != null) return;

            var pauseMenu = GUI.PauseMenu;
            if (pauseMenu == null) return;

            frame = new GUIFrame(
                new RectTransform(new Vector2(0.55f, 0.90f), pauseMenu.RectTransform, Anchor.Center));

            var mainLayout = new GUILayoutGroup(
                new RectTransform(new Vector2(0.95f, 0.95f), frame.RectTransform, Anchor.Center))
            {
                Stretch = true,
                RelativeSpacing = 0.01f
            };

            new GUITextBlock(
                new RectTransform(new Vector2(1f, 0.05f), mainLayout.RectTransform),
                Localization.T("panel_title"),
                textAlignment: Alignment.Center,
                font: GUIStyle.SubHeadingFont);

            listBox = new GUIListBox(
                new RectTransform(new Vector2(1f, 0.84f), mainLayout.RectTransform))
            {
                ScrollBarVisible = true
            };

            BuildContent();

            var bottomRow = new GUILayoutGroup(
                new RectTransform(new Vector2(1f, 0.06f), mainLayout.RectTransform),
                isHorizontal: true)
            {
                RelativeSpacing = 0.02f,
                Stretch = true
            };

            new GUIButton(
                new RectTransform(new Vector2(0.3f, 1f), bottomRow.RectTransform),
                Localization.T("btn_save"), Alignment.Center, "GUIButtonSmall")
            {
                OnClicked = (btn, ud) =>
                {
                    ApplyRulesToConfig();
                    OptimizerConfig.Save();
                    OptimizerConfig.SaveProfile();
                    DebugConsole.NewMessage($"[ItemOptimizer] {Localization.T("config_saved")}", Color.LimeGreen);
                    return true;
                }
            };

            new GUIButton(
                new RectTransform(new Vector2(0.3f, 1f), bottomRow.RectTransform),
                Localization.T("btn_close"), Alignment.Center, "GUIButtonSmall")
            {
                OnClicked = (btn, ud) =>
                {
                    Close();
                    return true;
                }
            };

            frame.AddToGUIUpdateList();
        }

        public static void Close()
        {
            if (frame == null) return;
            ApplyRulesToConfig();

            if (frame.Parent != null)
                frame.Parent.RemoveChild(frame);
            frame = null;
            listBox = null;
            ruleRows.Clear();
            _cachedMods = null;
        }

        private static void Rebuild()
        {
            if (frame == null) return;
            ruleRows.Clear();
            foreach (var child in new List<GUIComponent>(listBox.Content.Children))
                listBox.Content.RemoveChild(child);
            BuildContent();
        }

        // ── Build Content ──

        private static void BuildContent()
        {
            var content = listBox.Content;

            // ── Strategies Section ──
            SectionHeader(content, Localization.T("section_strategies"));

            StrategyTickBox(content, "strategy_cold_storage", "strategy_cold_storage_desc",
                OptimizerConfig.EnableColdStorageSkip,
                v => ItemOptimizerPlugin.SetStrategyEnabled("cold_storage", v));

            StrategyTickBoxWithNumber(content, "strategy_ground_item", "strategy_ground_item_desc",
                OptimizerConfig.EnableGroundItemThrottle,
                v => ItemOptimizerPlugin.SetStrategyEnabled("ground_item", v),
                OptimizerConfig.GroundItemSkipFrames,
                v => OptimizerConfig.GroundItemSkipFrames = v);

            StrategyTickBox(content, "strategy_ci_throttle", "strategy_ci_throttle_desc",
                OptimizerConfig.EnableCustomInterfaceThrottle,
                v => ItemOptimizerPlugin.SetStrategyEnabled("ci_throttle", v));

            StrategyTickBoxWithNumber(content, "strategy_motion", "strategy_motion_desc",
                OptimizerConfig.EnableMotionSensorThrottle,
                v => ItemOptimizerPlugin.SetStrategyEnabled("motion", v),
                OptimizerConfig.MotionSensorSkipFrames,
                v => OptimizerConfig.MotionSensorSkipFrames = v);

            StrategyTickBoxWithNumber(content, "strategy_wearable", "strategy_wearable_desc",
                OptimizerConfig.EnableWearableThrottle,
                v => ItemOptimizerPlugin.SetStrategyEnabled("wearable", v),
                OptimizerConfig.WearableSkipFrames,
                v => OptimizerConfig.WearableSkipFrames = v);

            StrategyTickBoxWithNumber(content, "strategy_water_det", "strategy_water_det_desc",
                OptimizerConfig.EnableWaterDetectorThrottle,
                v => ItemOptimizerPlugin.SetStrategyEnabled("water_detector", v),
                OptimizerConfig.WaterDetectorSkipFrames,
                v => OptimizerConfig.WaterDetectorSkipFrames = v);

            StrategyTickBoxWithNumber(content, "strategy_door", "strategy_door_desc",
                OptimizerConfig.EnableDoorThrottle,
                v => ItemOptimizerPlugin.SetStrategyEnabled("door", v),
                OptimizerConfig.DoorSkipFrames,
                v => OptimizerConfig.DoorSkipFrames = v);

            StrategyTickBox(content, "strategy_hst_cache", "strategy_hst_cache_desc",
                OptimizerConfig.EnableHasStatusTagCache,
                v => ItemOptimizerPlugin.SetStrategyEnabled("has_status_tag_cache", v));

            StrategyTickBox(content, "strategy_statushud", "strategy_statushud_desc",
                OptimizerConfig.EnableStatusHUDThrottle,
                v => OptimizerConfig.EnableStatusHUDThrottle = v);

            StrategyTickBox(content, "strategy_affliction", "strategy_affliction_desc",
                OptimizerConfig.EnableAfflictionDedup,
                v => ItemOptimizerPlugin.SetStrategyEnabled("affliction_dedup", v));

            StrategyTickBoxWithNumber(content, "strategy_parallel", "strategy_parallel_desc",
                OptimizerConfig.EnableParallelDispatch,
                v => ItemOptimizerPlugin.SetStrategyEnabled("parallel_dispatch", v),
                OptimizerConfig.ParallelWorkerCount,
                v => OptimizerConfig.ParallelWorkerCount = Math.Clamp(v, 1, 6),
                1, 6, "parallel_workers_label");

            // ── Thread Safety Analysis Section ──
            SectionHeader(content, Localization.T("section_thread_safety"));

            // Scan button + status row
            var scanRow = new GUILayoutGroup(
                new RectTransform(new Vector2(1f, 0.05f), content.RectTransform),
                isHorizontal: true)
            {
                Stretch = true,
                RelativeSpacing = 0.01f
            };

            new GUIButton(
                new RectTransform(new Vector2(0.25f, 1f), scanRow.RectTransform),
                Localization.T(ThreadSafetyAnalyzer.IsScanComplete ? "btn_rescan" : "btn_run_scan"),
                Alignment.Center, "GUIButtonSmall")
            {
                OnClicked = (btn, ud) =>
                {
                    ThreadSafetyAnalyzer.RunScan();
                    ThreadSafetyAnalyzer.SaveCache();
                    // Sync overrides back to config
                    OptimizerConfig.ThreadSafetyOverrides.Clear();
                    foreach (var kv in ThreadSafetyAnalyzer.Overrides)
                        OptimizerConfig.ThreadSafetyOverrides[kv.Key] = (int)kv.Value;
                    OptimizerConfig.AutoSave();
                    Rebuild();
                    return true;
                }
            };

            if (ThreadSafetyAnalyzer.IsScanComplete)
            {
                // Summary: Safe / Conditional / Unsafe
                var summaryText = Localization.Format("scan_summary",
                    ThreadSafetyAnalyzer.CountSafe,
                    ThreadSafetyAnalyzer.CountConditional,
                    ThreadSafetyAnalyzer.CountUnsafe);
                new GUITextBlock(
                    new RectTransform(new Vector2(0.55f, 1f), scanRow.RectTransform),
                    summaryText,
                    font: GUIStyle.SmallFont,
                    textColor: Color.LimeGreen);
            }
            else
            {
                new GUITextBlock(
                    new RectTransform(new Vector2(0.55f, 1f), scanRow.RectTransform),
                    Localization.T("scan_status_none"),
                    font: GUIStyle.SmallFont,
                    textColor: Color.Gray);
            }

            // HUD Overlay toggle
            Spacer(content);
            new GUITickBox(
                new RectTransform(new Vector2(1f, 0.05f), content.RectTransform),
                Localization.T("overlay_toggle"))
            {
                Selected = StatsOverlay.Visible,
                OnSelected = tb =>
                {
                    StatsOverlay.Visible = tb.Selected;
                    return true;
                }
            };

            // ── Item Rules Section ──
            SectionHeader(content, Localization.T("section_item_rules"));

            ruleRows.Clear();
            foreach (var rule in OptimizerConfig.ItemRules)
                AddRuleRow(content, rule);

            new GUIButton(
                new RectTransform(new Vector2(0.4f, 0.05f), content.RectTransform),
                Localization.T("rule_add"), Alignment.Center, "GUIButtonSmall")
            {
                OnClicked = (btn, ud) =>
                {
                    OptimizerConfig.ItemRules.Add(new ItemRule());
                    Rebuild();
                    return true;
                }
            };

            // ── Mod Control Panel ──
            SectionHeader(content, Localization.T("section_mod_control"));

            var mods = ScanMods();
            int totalMods = mods.Count;
            int optimizedMods = mods.Count(m => CountConfiguredInMod(m) > 0);

            // Global action row
            var globalRow = new GUILayoutGroup(
                new RectTransform(new Vector2(1f, 0.05f), content.RectTransform),
                isHorizontal: true)
            {
                Stretch = true,
                RelativeSpacing = 0.01f
            };

            new GUIButton(
                new RectTransform(new Vector2(0.32f, 1f), globalRow.RectTransform),
                Localization.T("btn_optimize_all"), Alignment.Center, "GUIButtonSmall")
            {
                OnClicked = (btn, ud) =>
                {
                    ApplyAllRecommended();
                    return true;
                }
            };

            new GUIButton(
                new RectTransform(new Vector2(0.30f, 1f), globalRow.RectTransform),
                Localization.T("btn_clear_all_mod"), Alignment.Center, "GUIButtonSmall")
            {
                OnClicked = (btn, ud) =>
                {
                    ClearAllModRules();
                    return true;
                }
            };

            new GUITextBlock(
                new RectTransform(new Vector2(0.35f, 1f), globalRow.RectTransform),
                Localization.Format("mods_optimized_summary", optimizedMods, totalMods),
                font: GUIStyle.SmallFont,
                textAlignment: Alignment.CenterRight);

            // Per-mod panels
            foreach (var mod in mods)
            {
                BuildModPanel(content, mod);
            }

            // ── Stats Section ──
            SectionHeader(content, Localization.T("section_stats"));

            StatLine(content, "strategy_cold_storage", Stats.AvgColdStorageSkips);
            StatLine(content, "stats_ground_item", Stats.AvgGroundItemSkips);
            StatLine(content, "strategy_ci_throttle", Stats.AvgCustomInterfaceSkips);
            StatLine(content, "strategy_motion", Stats.AvgMotionSensorSkips);
            StatLine(content, "strategy_wearable", Stats.AvgWearableSkips);
            StatLine(content, "stats_item_rules", Stats.AvgItemRuleSkips);
            StatLine(content, "stats_mod_opt", Stats.AvgModOptSkips);
            StatLine(content, "stats_water_det", Stats.AvgWaterDetectorSkips);
            StatLine(content, "stats_door", Stats.AvgDoorSkips);
            StatLine(content, "stats_hst_cache", Stats.AvgHasStatusTagCacheHits);
            StatLine(content, "stats_statushud", Stats.AvgStatusHUDSkips);
            StatLine(content, "stats_affliction", Stats.AvgAfflictionDedupSkips);

            new GUITextBlock(
                new RectTransform(new Vector2(1f, 0.04f), content.RectTransform),
                Localization.Format("stats_saved", Stats.EstimatedSavedMs()),
                textColor: Color.LimeGreen,
                font: GUIStyle.SmallFont);
        }

        // ── Mod Panel Builder ──

        private static void BuildModPanel(GUIComponent content, ModInfo mod)
        {
            var capturedMod = mod;
            int configured = CountConfiguredInMod(mod);

            // Header row
            var headerFrame = new GUIFrame(
                new RectTransform(new Vector2(1f, 0.05f), content.RectTransform),
                style: null);
            var headerRow = new GUILayoutGroup(
                new RectTransform(Vector2.One, headerFrame.RectTransform),
                isHorizontal: true)
            {
                Stretch = true,
                RelativeSpacing = 0.005f
            };

            // Expand button
            new GUIButton(
                new RectTransform(new Vector2(0.04f, 1f), headerRow.RectTransform),
                mod.IsExpanded ? "\u25bc" : ">", Alignment.Center, "GUIButtonSmall")
            {
                OnClicked = (btn, ud) =>
                {
                    capturedMod.IsExpanded = !capturedMod.IsExpanded;
                    if (!capturedMod.IsExpanded) capturedMod.IsDetailExpanded = false;
                    Rebuild();
                    return true;
                }
            };

            // Mod name
            new GUITextBlock(
                new RectTransform(new Vector2(0.28f, 1f), headerRow.RectTransform),
                $"{mod.Name} ({mod.Items.Count})",
                font: GUIStyle.SmallFont);

            // Tier distribution mini-bars
            var barContainer = new GUILayoutGroup(
                new RectTransform(new Vector2(0.25f, 0.7f), headerRow.RectTransform),
                isHorizontal: true)
            {
                Stretch = true,
                RelativeSpacing = 0.01f
            };
            foreach (ActivityTier tier in Enum.GetValues(typeof(ActivityTier)))
            {
                var tierInfo = mod.Tiers[tier];
                float fraction = mod.Items.Count > 0 ? (float)tierInfo.Items.Count / mod.Items.Count : 0f;
                if (fraction > 0f)
                {
                    var bar = new GUIProgressBar(
                        new RectTransform(new Vector2(Math.Max(fraction, 0.05f), 1f), barContainer.RectTransform),
                        fraction, TierMeta[(int)tier].Color);
                }
            }

            // Status text
            string statusText = configured > 0
                ? Localization.Format("tier_status", configured, mod.Items.Count)
                : Localization.T("mod_not_optimized");
            new GUITextBlock(
                new RectTransform(new Vector2(0.18f, 1f), headerRow.RectTransform),
                statusText,
                font: GUIStyle.SmallFont,
                textAlignment: Alignment.Center,
                textColor: configured > 0 ? Color.LimeGreen : Color.Gray);

            // Per-mod optimize button
            new GUIButton(
                new RectTransform(new Vector2(0.18f, 1f), headerRow.RectTransform),
                Localization.T("btn_optimize_mod"), Alignment.Center, "GUIButtonSmall")
            {
                OnClicked = (btn, ud) =>
                {
                    ApplyModRecommended(capturedMod);
                    OptimizerConfig.AutoSave();
                    Rebuild();
                    return true;
                }
            };

            // Expanded: tier rows + detail
            if (!mod.IsExpanded) return;

            foreach (ActivityTier tier in Enum.GetValues(typeof(ActivityTier)))
            {
                var tierInfo = mod.Tiers[tier];
                if (tierInfo.Items.Count == 0) continue;
                BuildTierRow(content, capturedMod, tierInfo);
            }

            // Detail toggle button
            var detailFrame = new GUIFrame(
                new RectTransform(new Vector2(1f, 0.04f), content.RectTransform),
                style: null);
            var detailRow = new GUILayoutGroup(
                new RectTransform(Vector2.One, detailFrame.RectTransform),
                isHorizontal: true);

            new GUIFrame(
                new RectTransform(new Vector2(0.04f, 1f), detailRow.RectTransform),
                style: null);

            new GUIButton(
                new RectTransform(new Vector2(0.25f, 1f), detailRow.RectTransform),
                Localization.T(mod.IsDetailExpanded ? "btn_hide_detail" : "btn_show_detail"),
                Alignment.Center, "GUIButtonSmall")
            {
                OnClicked = (btn, ud) =>
                {
                    capturedMod.IsDetailExpanded = !capturedMod.IsDetailExpanded;
                    Rebuild();
                    return true;
                }
            };

            // Detail view: items grouped by tier
            if (mod.IsDetailExpanded)
            {
                foreach (ActivityTier tier in Enum.GetValues(typeof(ActivityTier)))
                {
                    var tierInfo = mod.Tiers[tier];
                    if (tierInfo.Items.Count == 0) continue;

                    var meta = TierMeta[(int)tier];

                    // Tier sub-header
                    var subHeaderFrame = new GUIFrame(
                        new RectTransform(new Vector2(1f, 0.035f), content.RectTransform),
                        style: null);
                    var subHeaderRow = new GUILayoutGroup(
                        new RectTransform(Vector2.One, subHeaderFrame.RectTransform),
                        isHorizontal: true);
                    new GUIFrame(
                        new RectTransform(new Vector2(0.06f, 1f), subHeaderRow.RectTransform),
                        style: null);
                    new GUITextBlock(
                        new RectTransform(new Vector2(0.90f, 1f), subHeaderRow.RectTransform),
                        $"── {Localization.T(meta.LabelKey)} ({tierInfo.Items.Count}) ──",
                        font: GUIStyle.SmallFont,
                        textColor: meta.Color);

                    // Individual items
                    foreach (var item in tierInfo.Items)
                    {
                        BuildItemDetailRow(content, item);
                    }
                }
            }
        }

        private static void BuildTierRow(GUIComponent content, ModInfo mod, ModTierInfo tierInfo)
        {
            var meta = TierMeta[(int)tierInfo.Tier];
            var capturedMod = mod;
            var capturedTier = tierInfo;
            int configured = CountConfiguredInTier(tierInfo);

            var tierFrame = new GUIFrame(
                new RectTransform(new Vector2(1f, 0.045f), content.RectTransform),
                style: null);
            var tierRow = new GUILayoutGroup(
                new RectTransform(Vector2.One, tierFrame.RectTransform),
                isHorizontal: true)
            {
                Stretch = true,
                RelativeSpacing = 0.005f
            };

            // Indent
            new GUIFrame(
                new RectTransform(new Vector2(0.03f, 1f), tierRow.RectTransform),
                style: null);

            // Tier label (colored)
            new GUITextBlock(
                new RectTransform(new Vector2(0.12f, 1f), tierRow.RectTransform),
                $"\u25a0 {Localization.T(meta.LabelKey)}",
                font: GUIStyle.SmallFont,
                textColor: meta.Color);

            // Item count
            new GUITextBlock(
                new RectTransform(new Vector2(0.08f, 1f), tierRow.RectTransform),
                $"{tierInfo.Items.Count}",
                font: GUIStyle.SmallFont,
                textAlignment: Alignment.Center);

            // "跳帧:" label
            new GUITextBlock(
                new RectTransform(new Vector2(0.06f, 1f), tierRow.RectTransform),
                Localization.T("tier_skip_label"),
                font: GUIStyle.SmallFont,
                textAlignment: Alignment.CenterRight);

            // Slider
            var slider = new GUIScrollBar(
                new RectTransform(new Vector2(0.22f, 0.8f), tierRow.RectTransform),
                barSize: 0.07f, style: "GUISlider")
            {
                Range = new Vector2(1, 15),
                BarScrollValue = tierInfo.CurrentSkip,
                StepValue = 1
            };

            // Number input
            var numInput = new GUINumberInput(
                new RectTransform(new Vector2(0.07f, 1f), tierRow.RectTransform),
                NumberType.Int)
            {
                IntValue = tierInfo.CurrentSkip,
                MinValueInt = 1,
                MaxValueInt = 15
            };

            // Bidirectional sync
            slider.OnMoved = (sb, val) =>
            {
                int v = (int)Math.Round(sb.BarScrollValue);
                capturedTier.CurrentSkip = v;
                numInput.IntValue = v;
                return true;
            };
            numInput.OnValueChanged = ni =>
            {
                capturedTier.CurrentSkip = ni.IntValue;
                slider.BarScrollValue = ni.IntValue;
            };

            // Status
            new GUITextBlock(
                new RectTransform(new Vector2(0.10f, 1f), tierRow.RectTransform),
                Localization.Format("tier_status", configured, tierInfo.Items.Count),
                font: GUIStyle.SmallFont,
                textAlignment: Alignment.Center,
                textColor: configured == tierInfo.Items.Count ? Color.LimeGreen :
                           configured > 0 ? Color.Yellow : Color.Gray);

            // Apply button
            new GUIButton(
                new RectTransform(new Vector2(0.10f, 1f), tierRow.RectTransform),
                Localization.T("btn_apply_tier"), Alignment.Center, "GUIButtonSmall")
            {
                OnClicked = (btn, ud) =>
                {
                    ApplyTierRules(capturedMod, capturedTier.Tier);
                    OptimizerConfig.AutoSave();
                    Rebuild();
                    return true;
                }
            };
        }

        private static void BuildItemDetailRow(GUIComponent content, ModItemInfo item)
        {
            var itemFrame = new GUIFrame(
                new RectTransform(new Vector2(1f, 0.035f), content.RectTransform),
                style: null);
            var itemRow = new GUILayoutGroup(
                new RectTransform(Vector2.One, itemFrame.RectTransform),
                isHorizontal: true)
            {
                Stretch = true,
                RelativeSpacing = 0.005f
            };

            // Indent
            new GUIFrame(
                new RectTransform(new Vector2(0.06f, 1f), itemRow.RectTransform),
                style: null);

            // Display name (localized)
            string displayName = item.Prefab?.Name?.Value ?? item.Identifier;
            new GUITextBlock(
                new RectTransform(new Vector2(0.20f, 1f), itemRow.RectTransform),
                displayName,
                font: GUIStyle.SmallFont,
                textColor: Color.White);

            // Identifier
            new GUITextBlock(
                new RectTransform(new Vector2(0.15f, 1f), itemRow.RectTransform),
                item.Identifier,
                font: GUIStyle.SmallFont,
                textColor: Color.Gray);

            // Thread safety tier (from analyzer)
            if (ThreadSafetyAnalyzer.IsScanComplete)
            {
                var safetyInfo = ThreadSafetyAnalyzer.GetInfo(item.Identifier);
                string tierLabel;
                Color tierColor;
                switch (safetyInfo.Tier)
                {
                    case ThreadSafetyTier.Safe:
                        tierLabel = Localization.T("safety_safe");
                        tierColor = Color.LimeGreen;
                        break;
                    case ThreadSafetyTier.Conditional:
                        tierLabel = Localization.T("safety_conditional");
                        tierColor = Color.Yellow;
                        break;
                    default:
                        tierLabel = Localization.T("safety_unsafe");
                        tierColor = new Color(220, 50, 50);
                        break;
                }
                new GUITextBlock(
                    new RectTransform(new Vector2(0.07f, 1f), itemRow.RectTransform),
                    tierLabel,
                    font: GUIStyle.SmallFont,
                    textAlignment: Alignment.Center,
                    textColor: tierColor)
                {
                    ToolTip = ThreadSafetyAnalyzer.GetFlagsText(safetyInfo.Flags)
                };
            }

            // Patterns
            string patternText = item.DetectedPatterns.Count > 0
                ? string.Join(", ", item.DetectedPatterns)
                : "-";
            new GUITextBlock(
                new RectTransform(new Vector2(0.16f, 1f), itemRow.RectTransform),
                patternText,
                font: GUIStyle.SmallFont,
                textColor: item.DetectedPatterns.Count > 0 ? Color.Yellow : Color.Gray);

            // ModOpt status
            bool hasModOpt = OptimizerConfig.ModOptLookup.TryGetValue(item.Identifier, out var skipFrames);
            bool hasManualRule = OptimizerConfig.RuleLookup.ContainsKey(item.Identifier);
            string ruleText;
            Color ruleColor;
            if (hasManualRule)
            {
                ruleText = "Manual";
                ruleColor = Color.Cyan;
            }
            else if (hasModOpt)
            {
                ruleText = $"Throttle/{skipFrames}";
                ruleColor = Color.LimeGreen;
            }
            else
            {
                ruleText = Localization.T("mod_no_rule");
                ruleColor = Color.Gray;
            }
            new GUITextBlock(
                new RectTransform(new Vector2(0.13f, 1f), itemRow.RectTransform),
                ruleText,
                font: GUIStyle.SmallFont,
                textColor: ruleColor);

            // Add/Remove manual rule button
            var capturedItem = item;
            if (hasManualRule)
            {
                var removeBtn = new GUIButton(
                    new RectTransform(new Vector2(0.10f, 1f), itemRow.RectTransform),
                    Localization.T("btn_remove_rule"), Alignment.Center, "GUIButtonSmall")
                {
                    OnClicked = (btn, ud) =>
                    {
                        OptimizerConfig.ItemRules.RemoveAll(r => r.Identifier == capturedItem.Identifier);
                        OptimizerConfig.BuildLookupTables();
                        ItemOptimizerPlugin.SyncItemUpdatePatch();
                        OptimizerConfig.AutoSave();
                        Rebuild();
                        return true;
                    }
                };
            }
            else
            {
                var addBtn = new GUIButton(
                    new RectTransform(new Vector2(0.10f, 1f), itemRow.RectTransform),
                    "+ " + Localization.T("mod_add_rule"), Alignment.Center, "GUIButtonSmall")
                {
                    OnClicked = (btn, ud) =>
                    {
                        var rule = new ItemRule
                        {
                            Identifier = capturedItem.Identifier,
                            Action = ItemRuleAction.Throttle,
                            SkipFrames = TierMeta[(int)capturedItem.Tier].RecommendedSkip,
                            Condition = "notInActiveUse"
                        };
                        OptimizerConfig.ItemRules.Add(rule);
                        OptimizerConfig.BuildLookupTables();
                        ItemOptimizerPlugin.SyncItemUpdatePatch();
                        OptimizerConfig.AutoSave();
                        Rebuild();
                        return true;
                    }
                };
            }
        }

        // ── GUI Helpers ──

        private static void SectionHeader(GUIComponent parent, string text)
        {
            Spacer(parent);
            new GUITextBlock(
                new RectTransform(new Vector2(1f, 0.04f), parent.RectTransform),
                text,
                textAlignment: Alignment.Center,
                textColor: Color.Cyan,
                font: GUIStyle.SmallFont);
        }

        private static void Spacer(GUIComponent parent)
        {
            new GUIFrame(
                new RectTransform(new Vector2(1f, 0.015f), parent.RectTransform),
                style: null);
        }

        private static void StrategyTickBox(GUIComponent parent, string nameKey, string descKey,
            bool currentValue, Action<bool> setter)
        {
            var row = new GUILayoutGroup(
                new RectTransform(new Vector2(1f, 0.05f), parent.RectTransform),
                isHorizontal: true);

            new GUITickBox(
                new RectTransform(new Vector2(0.7f, 1f), row.RectTransform),
                Localization.T(nameKey))
            {
                Selected = currentValue,
                ToolTip = Localization.T(descKey),
                OnSelected = tb =>
                {
                    setter(tb.Selected);
                    return true;
                }
            };
        }

        private static void StrategyTickBoxWithNumber(GUIComponent parent, string nameKey, string descKey,
            bool currentEnabled, Action<bool> enableSetter,
            int currentSkip, Action<int> skipSetter)
        {
            var row = new GUILayoutGroup(
                new RectTransform(new Vector2(1f, 0.05f), parent.RectTransform),
                isHorizontal: true)
            {
                RelativeSpacing = 0.01f
            };

            new GUITickBox(
                new RectTransform(new Vector2(0.55f, 1f), row.RectTransform),
                Localization.T(nameKey))
            {
                Selected = currentEnabled,
                ToolTip = Localization.T(descKey),
                OnSelected = tb =>
                {
                    enableSetter(tb.Selected);
                    return true;
                }
            };

            new GUITextBlock(
                new RectTransform(new Vector2(0.15f, 1f), row.RectTransform),
                Localization.T("skip_frames_label"),
                textAlignment: Alignment.CenterRight,
                font: GUIStyle.SmallFont);

            new GUINumberInput(
                new RectTransform(new Vector2(0.2f, 1f), row.RectTransform),
                NumberType.Int)
            {
                IntValue = currentSkip,
                MinValueInt = 1,
                MaxValueInt = 30,
                OnValueChanged = ni => skipSetter(ni.IntValue)
            };
        }

        private static void StrategyTickBoxWithNumber(GUIComponent parent, string nameKey, string descKey,
            bool currentEnabled, Action<bool> enableSetter,
            int currentValue, Action<int> valueSetter,
            int minValue, int maxValue, string labelKey)
        {
            var row = new GUILayoutGroup(
                new RectTransform(new Vector2(1f, 0.05f), parent.RectTransform),
                isHorizontal: true)
            {
                RelativeSpacing = 0.01f
            };

            new GUITickBox(
                new RectTransform(new Vector2(0.55f, 1f), row.RectTransform),
                Localization.T(nameKey))
            {
                Selected = currentEnabled,
                ToolTip = Localization.T(descKey),
                OnSelected = tb =>
                {
                    enableSetter(tb.Selected);
                    return true;
                }
            };

            new GUITextBlock(
                new RectTransform(new Vector2(0.15f, 1f), row.RectTransform),
                Localization.T(labelKey),
                textAlignment: Alignment.CenterRight,
                font: GUIStyle.SmallFont);

            new GUINumberInput(
                new RectTransform(new Vector2(0.2f, 1f), row.RectTransform),
                NumberType.Int)
            {
                IntValue = currentValue,
                MinValueInt = minValue,
                MaxValueInt = maxValue,
                OnValueChanged = ni => valueSetter(ni.IntValue)
            };
        }

        private static void AddRuleRow(GUIComponent parent, ItemRule rule)
        {
            var rowFrame = new GUIFrame(
                new RectTransform(new Vector2(1f, 0.07f), parent.RectTransform),
                style: null);

            var row = new GUILayoutGroup(
                new RectTransform(Vector2.One, rowFrame.RectTransform),
                isHorizontal: true)
            {
                RelativeSpacing = 0.01f,
                Stretch = true
            };

            // Display name (resolved from prefab)
            string displayName = "-";
            if (!string.IsNullOrWhiteSpace(rule.Identifier))
            {
                foreach (var p in ItemPrefab.Prefabs)
                {
                    if (p.Identifier.Value == rule.Identifier)
                    {
                        displayName = p.Name?.Value ?? rule.Identifier;
                        break;
                    }
                }
            }
            new GUITextBlock(
                new RectTransform(new Vector2(0.18f, 1f), row.RectTransform),
                displayName,
                font: GUIStyle.SmallFont,
                textColor: Color.LightGray);

            var identBox = new GUITextBox(
                new RectTransform(new Vector2(0.20f, 1f), row.RectTransform))
            {
                Text = rule.Identifier,
                ToolTip = Localization.T("rule_identifier")
            };
            identBox.OnTextChanged += (tb, text) =>
            {
                rule.Identifier = text ?? "";
                return true;
            };

            var actionDd = new GUIDropDown(
                new RectTransform(new Vector2(0.18f, 1f), row.RectTransform));
            actionDd.AddItem(Localization.T("action_skip"), ItemRuleAction.Skip);
            actionDd.AddItem(Localization.T("action_throttle"), ItemRuleAction.Throttle);
            actionDd.SelectItem(rule.Action);
            actionDd.OnSelected += (component, obj) =>
            {
                if (obj is ItemRuleAction action)
                    rule.Action = action;
                return true;
            };

            var skipInput = new GUINumberInput(
                new RectTransform(new Vector2(0.10f, 1f), row.RectTransform),
                NumberType.Int)
            {
                IntValue = rule.SkipFrames,
                MinValueInt = 1,
                MaxValueInt = 30,
                OnValueChanged = ni => rule.SkipFrames = ni.IntValue
            };

            var condDd = new GUIDropDown(
                new RectTransform(new Vector2(0.22f, 1f), row.RectTransform));
            condDd.AddItem(Localization.T("cond_always"), "always");
            condDd.AddItem(Localization.T("cond_cold_storage"), "coldStorage");
            condDd.AddItem(Localization.T("cond_not_active_use"), "notInActiveUse");
            condDd.SelectItem(rule.Condition);
            condDd.OnSelected += (component, obj) =>
            {
                if (obj is string cond)
                    rule.Condition = cond;
                return true;
            };

            new GUIButton(
                new RectTransform(new Vector2(0.08f, 1f), row.RectTransform),
                Localization.T("rule_remove"), Alignment.Center, "GUIButtonSmall")
            {
                OnClicked = (btn, ud) =>
                {
                    OptimizerConfig.ItemRules.Remove(rule);
                    Rebuild();
                    return true;
                }
            };

            ruleRows.Add(new RuleRow
            {
                Container = rowFrame,
                IdentifierBox = identBox,
                ActionDropDown = actionDd,
                SkipInput = skipInput,
                ConditionDropDown = condDd,
                Rule = rule
            });
        }

        private static void StatLine(GUIComponent parent, string nameKey, float avgValue)
        {
            new GUITextBlock(
                new RectTransform(new Vector2(1f, 0.04f), parent.RectTransform),
                Localization.Format("stats_format", Localization.T(nameKey), avgValue),
                font: GUIStyle.SmallFont);
        }

        private static void ApplyRulesToConfig()
        {
            OptimizerConfig.BuildLookupTables();
            OptimizerConfig.AutoSave();
        }
    }
}
