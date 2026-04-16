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
            public GUITextBox SkipBox;
            public GUIDropDown ConditionDropDown;
            public GUIListBox SuggestionList;
            public ItemRule Rule;
        }

        private class ModPanelRefs
        {
            public string ModName;
            public GUIScrollBar IntensitySlider;
            public GUITextBlock IntensityPercent;
            public GUITextBlock IntensityPreview;
            public List<(GUIScrollBar Slider, GUITextBox NumBox, ModTierInfo TierInfo)> TierControls = new();
        }
        private static readonly List<ModPanelRefs> _modPanelRefs = new();
        private static readonly List<GUIComponent> _overlayWidgets = new();

        // Collapse state for sections
        private static bool _rulesExpanded = false;
        private static bool _whitelistExpanded = false;

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
            _modPanelRefs.Clear();
            _overlayWidgets.Clear();
            _cachedMods = null;
        }

        private static void Rebuild()
        {
            if (frame == null) return;
            ruleRows.Clear();
            _modPanelRefs.Clear();
            foreach (var w in _overlayWidgets)
                frame.RemoveChild(w);
            _overlayWidgets.Clear();
            foreach (var child in new List<GUIComponent>(listBox.Content.Children))
                listBox.Content.RemoveChild(child);
            BuildContent();
        }

        // ── Build Content ──

        private static void BuildContent()
        {
            var content = listBox.Content;

            // ── HUD Overlay toggle (at top for quick access) ──
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

            // ══════════════════════════════════════════════
            //  Section 1: Core Optimization
            // ══════════════════════════════════════════════
            SectionHeader(content, Localization.T("section_core_opt"));

            // -- Item Update Strategies --
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

            StrategyTickBoxWithNumber(content, "strategy_wearable", "strategy_wearable_desc",
                OptimizerConfig.EnableWearableThrottle,
                v => ItemOptimizerPlugin.SetStrategyEnabled("wearable", v),
                OptimizerConfig.WearableSkipFrames,
                v => OptimizerConfig.WearableSkipFrames = v);

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

            StrategyTickBox(content, "strategy_wire_skip", "strategy_wire_skip_desc",
                OptimizerConfig.EnableWireSkip,
                v => ItemOptimizerPlugin.SetStrategyEnabled("wire_skip", v));

            // -- Rewrites --
            StrategyTickBox(content, "strategy_relay_rewrite", "strategy_relay_rewrite_desc",
                OptimizerConfig.EnableRelayRewrite,
                v => ItemOptimizerPlugin.SetStrategyEnabled("relay_rewrite", v));

            StrategyTickBox(content, "strategy_power_transfer_rewrite", "strategy_power_transfer_rewrite_desc",
                OptimizerConfig.EnablePowerTransferRewrite,
                v => ItemOptimizerPlugin.SetStrategyEnabled("power_transfer_rewrite", v));

            StrategyTickBox(content, "strategy_power_container_rewrite", "strategy_power_container_rewrite_desc",
                OptimizerConfig.EnablePowerContainerRewrite,
                v => ItemOptimizerPlugin.SetStrategyEnabled("power_container_rewrite", v));

            // -- Character Optimization --
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

            // -- Network Sync Fixes --
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

            // -- Advanced --
            SectionHeader(content, Localization.T("section_advanced"));

            StrategyTickBox(content, "strategy_misc_parallel", "strategy_misc_parallel_desc",
                OptimizerConfig.EnableMiscParallel,
                v => OptimizerConfig.EnableMiscParallel = v);

            StrategyTickBox(content, "strategy_proxy", "strategy_proxy_desc",
                OptimizerConfig.EnableProxySystem,
                v => ItemOptimizerPlugin.SetStrategyEnabled("proxy_system", v));

            StrategyDropDown(content, "strategy_signal_graph", "strategy_signal_graph_desc",
                new[] { "signal_graph_off", "signal_graph_accel", "signal_graph_aggressive" },
                OptimizerConfig.SignalGraphMode,
                v => ItemOptimizerPlugin.SetStrategyValue("signal_graph_accel", v));

            // Motion Sensor: Off(0) | Throttle(1) | Rewrite(2)
            int motionMode = OptimizerConfig.EnableMotionSensorRewrite ? 2
                           : OptimizerConfig.EnableMotionSensorThrottle ? 1 : 0;
            StrategyDropDownWithNumber(content, "strategy_motion_sensor", "strategy_motion_sensor_desc",
                new[] { "sensor_off", "sensor_throttle", "sensor_rewrite" },
                motionMode,
                v =>
                {
                    if (v != 2 && OptimizerConfig.EnableMotionSensorRewrite)
                        ItemOptimizerPlugin.SetStrategyEnabled("motion_rewrite", false);
                    if (v != 1 && OptimizerConfig.EnableMotionSensorThrottle)
                        ItemOptimizerPlugin.SetStrategyEnabled("motion", false);
                    if (v == 1) ItemOptimizerPlugin.SetStrategyEnabled("motion", true);
                    if (v == 2) ItemOptimizerPlugin.SetStrategyEnabled("motion_rewrite", true);
                },
                OptimizerConfig.MotionSensorSkipFrames,
                v => OptimizerConfig.MotionSensorSkipFrames = v);

            // Water Detector: Off(0) | Throttle(1) | Rewrite(2)
            int waterMode = OptimizerConfig.EnableWaterDetectorRewrite ? 2
                          : OptimizerConfig.EnableWaterDetectorThrottle ? 1 : 0;
            StrategyDropDownWithNumber(content, "strategy_water_detector_mode", "strategy_water_detector_mode_desc",
                new[] { "sensor_off", "sensor_throttle", "sensor_rewrite" },
                waterMode,
                v =>
                {
                    if (v != 2 && OptimizerConfig.EnableWaterDetectorRewrite)
                        ItemOptimizerPlugin.SetStrategyEnabled("water_det_rewrite", false);
                    if (v != 1 && OptimizerConfig.EnableWaterDetectorThrottle)
                        ItemOptimizerPlugin.SetStrategyEnabled("water_detector", false);
                    if (v == 1) ItemOptimizerPlugin.SetStrategyEnabled("water_detector", true);
                    if (v == 2) ItemOptimizerPlugin.SetStrategyEnabled("water_det_rewrite", true);
                },
                OptimizerConfig.WaterDetectorSkipFrames,
                v => OptimizerConfig.WaterDetectorSkipFrames = v);

            StrategyTickBox(content, "strategy_hull_spatial", "strategy_hull_spatial_desc",
                OptimizerConfig.EnableHullSpatialIndex,
                v => OptimizerConfig.EnableHullSpatialIndex = v);

            StrategyTickBox(content, "strategy_spike_detector", "strategy_spike_detector_desc",
                OptimizerConfig.EnableSpikeDetector,
                v => { OptimizerConfig.EnableSpikeDetector = v; SpikeDetector.SetEnabled(v); });

            // -- Client Optimization --
            SectionHeader(content, Localization.T("section_client_opt"));

            StrategyTickBoxWithNumber(content,
                "strategy_interaction_label", "strategy_interaction_label_desc",
                OptimizerConfig.EnableInteractionLabelOpt,
                v => OptimizerConfig.EnableInteractionLabelOpt = v,
                OptimizerConfig.InteractionLabelMaxCount,
                v => OptimizerConfig.InteractionLabelMaxCount = Math.Clamp(v, 10, 200),
                10, 200, "interaction_label_max_label");

            StrategyTickBox(content, "strategy_relay_opt", "strategy_relay_opt_desc",
                OptimizerConfig.EnableRelayOpt,
                v => OptimizerConfig.EnableRelayOpt = v);

            StrategyTickBox(content, "strategy_motion_sensor_opt", "strategy_motion_sensor_opt_desc",
                OptimizerConfig.EnableMotionSensorOpt,
                v => OptimizerConfig.EnableMotionSensorOpt = v);

            StrategyTickBox(content, "strategy_water_detector_opt", "strategy_water_detector_opt_desc",
                OptimizerConfig.EnableWaterDetectorOpt,
                v => OptimizerConfig.EnableWaterDetectorOpt = v);

            StrategyTickBox(content, "strategy_button_terminal_opt", "strategy_button_terminal_opt_desc",
                OptimizerConfig.EnableButtonTerminalOpt,
                v => OptimizerConfig.EnableButtonTerminalOpt = v);

            StrategyTickBox(content, "strategy_pump_opt", "strategy_pump_opt_desc",
                OptimizerConfig.EnablePumpOpt,
                v => OptimizerConfig.EnablePumpOpt = v);

            // ══════════════════════════════════════════════
            //  Section 2: Mod Item Management
            // ══════════════════════════════════════════════

            // Mod Control Panel (always visible)
            BuildModControlSection(content);

            // Item Rules (collapsible)
            BuildItemRulesSection(content);

            // Whitelist (collapsible)
            BuildWhitelistSection(content);

            // ══════════════════════════════════════════════
            //  Section 3: Diagnostics & Stats
            // ══════════════════════════════════════════════
            SectionHeader(content, Localization.T("section_diagnostics"));

            Spacer(content);

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
            StatLine(content, "stats_wire_skip", Stats.AvgWireSkips);
            StatLine(content, "stats_ladder_fix", Stats.AvgLadderFixCorrections);
            StatLine(content, "stats_platform_fix", Stats.AvgPlatformFixCorrections);
            StatLine(content, "stats_proxy_items", Stats.AvgProxyItems);
            StatLine(content, "proxy_batch", Stats.AvgProxyBatchComputeMs);
            StatLine(content, "proxy_sync", Stats.AvgProxySyncBackMs);
            StatLine(content, "stats_signal_graph_skip", Stats.AvgSignalGraphAccelSkips);
            StatLine(content, "stats_signal_graph_tick", Stats.AvgSignalGraphTickMs);

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
