using System;
using System.Collections.Generic;
using Barotrauma;
using Microsoft.Xna.Framework;

namespace ItemOptimizerMod
{
    static partial class SettingsPanel
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
            public GUIListBox SuggestionList;
            public ItemRule Rule;
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

            // ── Section 1: Item Update Strategies ──
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

            // ── Section 2: Character Optimization ──
            SectionHeader(content, Localization.T("section_character_opt"));

            StrategyTickBox(content, "strategy_anim_lod", "strategy_anim_lod_desc",
                OptimizerConfig.EnableAnimLOD,
                v => ItemOptimizerPlugin.SetStrategyEnabled("anim_lod", v));

            StrategyTickBoxWithNumber(content, "strategy_char_stagger", "strategy_char_stagger_desc",
                OptimizerConfig.EnableCharacterStagger,
                v => ItemOptimizerPlugin.SetStrategyEnabled("char_stagger", v),
                OptimizerConfig.CharacterStaggerGroups,
                v => OptimizerConfig.CharacterStaggerGroups = Math.Clamp(v, 2, 8),
                2, 8, "stagger_groups_label");

            // ── Section 3: Network Sync Fixes ──
            SectionHeader(content, Localization.T("section_network_sync"));

            StrategyTickBox(content, "strategy_ladder_fix", "strategy_ladder_fix_desc",
                OptimizerConfig.EnableLadderFix,
                v => ItemOptimizerPlugin.SetStrategyEnabled("ladder_fix", v));

            StrategyTickBox(content, "strategy_platform_fix", "strategy_platform_fix_desc",
                OptimizerConfig.EnablePlatformFix,
                v => ItemOptimizerPlugin.SetStrategyEnabled("platform_fix", v));

            StrategyTickBox(content, "strategy_server_dedup", "strategy_server_dedup_desc",
                OptimizerConfig.EnableServerHashSetDedup,
                v => ItemOptimizerPlugin.SetStrategyEnabled("server_hashset_dedup", v));

            // ── Section 4: Parallel Processing ──
            SectionHeader(content, Localization.T("section_parallel"));

            StrategyTickBox(content, "strategy_misc_parallel", "strategy_misc_parallel_desc",
                OptimizerConfig.EnableMiscParallel,
                v => OptimizerConfig.EnableMiscParallel = v);

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
            BuildItemRulesSection(content);

            // ── Mod Control Panel ──
            BuildModControlSection(content);

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
            StatLine(content, "stats_ladder_fix", Stats.AvgLadderFixCorrections);
            StatLine(content, "stats_platform_fix", Stats.AvgPlatformFixCorrections);

            new GUITextBlock(
                new RectTransform(new Vector2(1f, 0.04f), content.RectTransform),
                Localization.Format("stats_saved", Stats.EstimatedSavedMs()),
                textColor: Color.LimeGreen,
                font: GUIStyle.SmallFont);
        }

        private static void ApplyRulesToConfig()
        {
            OptimizerConfig.BuildLookupTables();
            OptimizerConfig.AutoSave();
        }
    }
}
