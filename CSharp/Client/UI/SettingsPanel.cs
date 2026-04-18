using System;
using System.Collections.Generic;
using Barotrauma;
using ItemOptimizerMod.SignalGraph;
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
        private static bool _showImpactBars = false;

        // ── Impact bar animation state ──
        private struct ImpactBarState
        {
            public GUIProgressBar Bar;
            public GUITextBlock PctLabel;
            public string FeatureKey;
            public float CurrentFraction;
            public float TargetFraction;
        }
        private static readonly List<ImpactBarState> _impactBars = new();
        private static GUITextBlock _totalSavedLabel;
        private static int _tickCounter;
        private static bool _animActive;

        // ── Panel Lifecycle ──

        public static bool IsOpen => frame != null;

        public static void Show()
        {
            if (frame != null) return;

            var pauseMenu = GUI.PauseMenu;
            if (pauseMenu == null) return;

            Localization.Init();

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
            _impactBars.Clear();
            _totalSavedLabel = null;
            _animActive = false;
            _cachedMods = null;
        }

        private static void Rebuild()
        {
            if (frame == null) return;
            float scroll = listBox?.BarScroll ?? 0f;
            ruleRows.Clear();
            _modPanelRefs.Clear();
            _impactBars.Clear();
            foreach (var w in _overlayWidgets)
                frame.RemoveChild(w);
            _overlayWidgets.Clear();
            foreach (var child in new List<GUIComponent>(listBox.Content.Children))
                listBox.Content.RemoveChild(child);
            BuildContent();
            if (listBox != null) listBox.BarScroll = scroll;
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

            // ── Impact Bar toggle ──
            new GUITickBox(
                new RectTransform(new Vector2(1f, 0.05f), content.RectTransform),
                Localization.T("impact_bar_toggle"))
            {
                Selected = _showImpactBars,
                ToolTip = Localization.T("impact_bar_toggle_desc"),
                OnSelected = tb =>
                {
                    _showImpactBars = tb.Selected;
                    _impactBars.Clear();
                    Rebuild();
                    return true;
                }
            };

            var impact = ComputeImpact();

            // ══════════════════════════════════════════════
            //  Section 1: 核心优化 (Core)
            // ══════════════════════════════════════════════
            SectionHeader(content, Localization.T("section_core_opt"));

            StrategyTickBox(content, "strategy_cold_storage", "strategy_cold_storage_desc",
                OptimizerConfig.EnableColdStorageSkip,
                v => { ItemOptimizerPlugin.SetStrategyEnabled("cold_storage", v); if (_showImpactBars) RefreshImpactTargets(); },
                ImpactFrac(impact.ColdMs, impact.TotalMs));

            StrategyTickBoxWithNumber(content, "strategy_ground_item", "strategy_ground_item_desc",
                OptimizerConfig.EnableGroundItemThrottle,
                v => { ItemOptimizerPlugin.SetStrategyEnabled("ground_item", v); if (_showImpactBars) RefreshImpactTargets(); },
                OptimizerConfig.GroundItemSkipFrames,
                v => OptimizerConfig.GroundItemSkipFrames = v,
                ImpactFrac(impact.GroundMs, impact.TotalMs));

            StrategyTickBox(content, "strategy_zone_dispatch", "strategy_zone_dispatch_desc",
                OptimizerConfig.EnableNativeRuntime,
                v => { ItemOptimizerPlugin.SetStrategyEnabled("native_runtime", v); if (_showImpactBars) RefreshImpactTargets(); },
                ImpactFrac(impact.ZoneMs, impact.TotalMs));

            StrategyDropDown(content, "strategy_signal_graph", "strategy_signal_graph_desc",
                new[] { "signal_graph_off", "signal_graph_accel", "signal_graph_aggressive" },
                OptimizerConfig.SignalGraphMode,
                v => { ItemOptimizerPlugin.SetStrategyValue("signal_graph_accel", v); if (_showImpactBars) RefreshImpactTargets(); },
                ImpactFrac(impact.SgMs, impact.TotalMs));

            // ══════════════════════════════════════════════
            //  Section 2: 传感器 (Sensors)
            // ══════════════════════════════════════════════
            SectionHeader(content, Localization.T("section_sensor"));

            // Motion Sensor: rewrite toggle + skip frames
            StrategyTickBoxWithNumber(content, "strategy_motion_sensor", "strategy_motion_sensor_desc",
                OptimizerConfig.EnableMotionSensorRewrite,
                v => { ItemOptimizerPlugin.SetStrategyEnabled("motion_rewrite", v); if (_showImpactBars) RefreshImpactTargets(); },
                OptimizerConfig.MotionSensorSkipFrames,
                v => OptimizerConfig.MotionSensorSkipFrames = v,
                ImpactFrac(impact.MotionMs, impact.TotalMs));

            // Water Detector: rewrite toggle + skip frames
            StrategyTickBoxWithNumber(content, "strategy_water_detector_mode", "strategy_water_detector_mode_desc",
                OptimizerConfig.EnableWaterDetectorRewrite,
                v => { ItemOptimizerPlugin.SetStrategyEnabled("water_det_rewrite", v); if (_showImpactBars) RefreshImpactTargets(); },
                OptimizerConfig.WaterDetectorSkipFrames,
                v => OptimizerConfig.WaterDetectorSkipFrames = v,
                ImpactFrac(impact.WaterMs, impact.TotalMs));

            StrategyTickBox(content, "strategy_hull_spatial", "strategy_hull_spatial_desc",
                OptimizerConfig.EnableHullSpatialIndex,
                v => OptimizerConfig.EnableHullSpatialIndex = v);

            // ══════════════════════════════════════════════
            //  Section 3: 客户端 (Client)
            // ══════════════════════════════════════════════
            SectionHeader(content, Localization.T("section_client_opt"));

            StrategyTickBoxWithNumber(content,
                "strategy_interaction_label", "strategy_interaction_label_desc",
                OptimizerConfig.EnableInteractionLabelOpt,
                v => OptimizerConfig.EnableInteractionLabelOpt = v,
                OptimizerConfig.InteractionLabelMaxCount,
                v => OptimizerConfig.InteractionLabelMaxCount = Math.Clamp(v, 10, 200),
                10, 200, "interaction_label_max_label");

            // ══════════════════════════════════════════════
            //  Section 4: 中等优化 (Medium)
            // ══════════════════════════════════════════════
            SectionHeader(content, Localization.T("section_advanced"));

            StrategyTickBox(content, "strategy_wire_skip", "strategy_wire_skip_desc",
                OptimizerConfig.EnableWireSkip,
                v => { ItemOptimizerPlugin.SetStrategyEnabled("wire_skip", v); if (_showImpactBars) RefreshImpactTargets(); },
                ImpactFrac(impact.WireMs, impact.TotalMs));

            StrategyTickBox(content, "strategy_misc_parallel", "strategy_misc_parallel_desc",
                OptimizerConfig.EnableMiscParallel,
                v => OptimizerConfig.EnableMiscParallel = v);

            StrategyTickBox(content, "strategy_button_terminal_opt", "strategy_button_terminal_opt_desc",
                OptimizerConfig.EnableButtonTerminalOpt,
                v => OptimizerConfig.EnableButtonTerminalOpt = v);

            StrategyTickBox(content, "strategy_pump_opt", "strategy_pump_opt_desc",
                OptimizerConfig.EnablePumpOpt,
                v => OptimizerConfig.EnablePumpOpt = v);

            // ══════════════════════════════════════════════
            //  Section 5: 电路系统（实验性）(Circuit - Experimental)
            // ══════════════════════════════════════════════
            SectionHeader(content, Localization.T("section_circuit"));

            StrategyTickBox(content, "strategy_relay_rewrite", "strategy_relay_rewrite_desc",
                OptimizerConfig.EnableRelayRewrite,
                v => ItemOptimizerPlugin.SetStrategyEnabled("relay_rewrite", v));

            StrategyTickBox(content, "strategy_power_transfer_rewrite", "strategy_power_transfer_rewrite_desc",
                OptimizerConfig.EnablePowerTransferRewrite,
                v => ItemOptimizerPlugin.SetStrategyEnabled("power_transfer_rewrite", v));

            StrategyTickBox(content, "strategy_power_container_rewrite", "strategy_power_container_rewrite_desc",
                OptimizerConfig.EnablePowerContainerRewrite,
                v => ItemOptimizerPlugin.SetStrategyEnabled("power_container_rewrite", v));

            StrategyTickBox(content, "strategy_hst_cache", "strategy_hst_cache_desc",
                OptimizerConfig.EnableHasStatusTagCache,
                v => { ItemOptimizerPlugin.SetStrategyEnabled("has_status_tag_cache", v); if (_showImpactBars) RefreshImpactTargets(); },
                ImpactFrac(impact.HstMs, impact.TotalMs));

            // ══════════════════════════════════════════════
            //  Section 6: 角色 (Character)
            // ══════════════════════════════════════════════
            SectionHeader(content, Localization.T("section_character_opt"));

            StrategyTickBox(content, "strategy_anim_lod", "strategy_anim_lod_desc",
                OptimizerConfig.EnableAnimLOD,
                v => { ItemOptimizerPlugin.SetStrategyEnabled("anim_lod", v); if (_showImpactBars) RefreshImpactTargets(); },
                ImpactFrac(impact.AnimMs, impact.TotalMs));

            StrategyTickBoxWithNumber(content, "strategy_char_stagger", "strategy_char_stagger_desc",
                OptimizerConfig.EnableCharacterStagger,
                v => { ItemOptimizerPlugin.SetStrategyEnabled("char_stagger", v); if (_showImpactBars) RefreshImpactTargets(); },
                OptimizerConfig.CharacterStaggerGroups,
                v => OptimizerConfig.CharacterStaggerGroups = Math.Clamp(v, 2, 8),
                2, 8, "stagger_groups_label",
                ImpactFrac(impact.StaggerMs, impact.TotalMs));

            // ══════════════════════════════════════════════
            //  Section 7: 网络同步 (Network)
            // ══════════════════════════════════════════════
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

            // ══════════════════════════════════════════════
            //  Section 8: 开发者工具 (Developer)
            // ══════════════════════════════════════════════
            SectionHeader(content, Localization.T("section_dev_tools"));

            StrategyTickBox(content, "strategy_spike_detector", "strategy_spike_detector_desc",
                OptimizerConfig.EnableSpikeDetector,
                v => { OptimizerConfig.EnableSpikeDetector = v; SpikeDetector.SetEnabled(v); });

            StrategyTickBox(content, "strategy_allow_sync", "strategy_allow_sync_desc",
                OptimizerConfig.AllowClientSync,
                v => { OptimizerConfig.AllowClientSync = v; OptimizerConfig.AutoSave(); });

            // Server perf overlay toggle
            new GUITickBox(
                new RectTransform(new Vector2(1f, 0.05f), content.RectTransform),
                Localization.T("dev_serverperf"))
            {
                Selected = ServerPerfOverlay.Visible,
                ToolTip = Localization.T("dev_serverperf_desc"),
                OnSelected = tb =>
                {
                    ServerPerfOverlay.Visible = tb.Selected;
                    return true;
                }
            };

            // iorecord button
            DevToolButton(content, "dev_iorecord", "dev_iorecord_desc", () =>
            {
                DebugConsole.ExecuteCommand("iorecord 1200");
                DebugConsole.IsOpen = true;
            });

            // iosgraph button
            DevToolButton(content, "dev_iosgraph", "dev_iosgraph_desc", () =>
            {
                DebugConsole.ExecuteCommand("iosgraph");
                PrintSignalGraphAnalysis();
                DebugConsole.IsOpen = true;
            });

            // ══════════════════════════════════════════════
            //  Section 9-11: Mod Control / Item Rules / Whitelist
            // ══════════════════════════════════════════════

            // Mod Control Panel (always visible)
            BuildModControlSection(content);

            // Item Rules (collapsible)
            BuildItemRulesSection(content);

            // Whitelist (collapsible)
            BuildWhitelistSection(content);

            // ══════════════════════════════════════════════
            //  Diagnostics & Stats
            // ══════════════════════════════════════════════
            SectionHeader(content, Localization.T("section_diagnostics"));

            Spacer(content);

            StatLine(content, "strategy_cold_storage", Stats.AvgColdStorageSkips);
            StatLine(content, "stats_ground_item", Stats.AvgGroundItemSkips);
            StatLine(content, "strategy_motion_sensor", Stats.AvgMotionSensorSkips);
            StatLine(content, "stats_item_rules", Stats.AvgItemRuleSkips);
            StatLine(content, "stats_mod_opt", Stats.AvgModOptSkips);
            StatLine(content, "stats_water_det", Stats.AvgWaterDetectorSkips);
            StatLine(content, "stats_hst_cache", Stats.AvgHasStatusTagCacheHits);
            StatLine(content, "stats_wire_skip", Stats.AvgWireSkips);
            StatLine(content, "stats_signal_graph_skip", Stats.AvgSignalGraphAccelSkips);
            StatLine(content, "stats_signal_graph_tick", Stats.AvgSignalGraphTickMs);

            _totalSavedLabel = new GUITextBlock(
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

        // ── Impact bar computation ──

        private struct ImpactSnapshot
        {
            public float ColdMs, GroundMs, ZoneMs, SgMs, MotionMs, WaterMs,
                         WireMs, HstMs, AnimMs, StaggerMs, TotalMs;
        }

        private static ImpactSnapshot ComputeImpact()
        {
            var s = new ImpactSnapshot();
            s.ColdMs    = OptimizerConfig.EnableColdStorageSkip     ? Stats.AvgColdStorageSkips      * Stats.CostColdStorage    : 0f;
            s.GroundMs  = OptimizerConfig.EnableGroundItemThrottle  ? Stats.AvgGroundItemSkips       * Stats.CostGroundItem     : 0f;
            s.ZoneMs    = OptimizerConfig.EnableNativeRuntime       ? Stats.AvgZoneSkips             * Stats.CostZoneSkip       : 0f;
            s.SgMs      = OptimizerConfig.SignalGraphMode > 0       ? Stats.AvgSignalGraphAccelSkips * Stats.CostSignalGraph    : 0f;
            s.MotionMs  = OptimizerConfig.EnableMotionSensorRewrite ? Stats.AvgMotionSensorSkips     * Stats.CostMotionSensor   : 0f;
            s.WaterMs   = OptimizerConfig.EnableWaterDetectorRewrite? Stats.AvgWaterDetectorSkips    * Stats.CostWaterDetector  : 0f;
            s.WireMs    = OptimizerConfig.EnableWireSkip            ? Stats.AvgWireSkips             * Stats.CostWireSkip       : 0f;
            s.HstMs     = OptimizerConfig.EnableHasStatusTagCache   ? Stats.AvgHasStatusTagCacheHits * Stats.CostHSTCache       : 0f;
            s.AnimMs    = OptimizerConfig.EnableAnimLOD             ? Stats.AvgAnimLODSkipped * Stats.CostAnimLODSkip + Stats.AvgAnimLODHalfRate * Stats.CostAnimLODHalf : 0f;
            s.StaggerMs = OptimizerConfig.EnableCharacterStagger    ? Stats.AvgCharStaggerSkipped    * Stats.CostCharStagger    : 0f;
            s.TotalMs   = s.ColdMs + s.GroundMs + s.ZoneMs + s.SgMs + s.MotionMs + s.WaterMs + s.WireMs + s.HstMs + s.AnimMs + s.StaggerMs;
            return s;
        }

        private static float GetFeatureMs(string key, ImpactSnapshot snap)
        {
            return key switch
            {
                "strategy_cold_storage"        => snap.ColdMs,
                "strategy_ground_item"         => snap.GroundMs,
                "strategy_zone_dispatch"       => snap.ZoneMs,
                "strategy_signal_graph"        => snap.SgMs,
                "strategy_motion_sensor"       => snap.MotionMs,
                "strategy_water_detector_mode" => snap.WaterMs,
                "strategy_wire_skip"           => snap.WireMs,
                "strategy_hst_cache"           => snap.HstMs,
                "strategy_anim_lod"            => snap.AnimMs,
                "strategy_char_stagger"        => snap.StaggerMs,
                _ => 0f
            };
        }

        private static float ImpactFrac(float featureMs, float totalMs)
        {
            if (!_showImpactBars) return -1f;
            if (totalMs <= 0.001f) return 0f;
            return Math.Clamp(featureMs / totalMs, 0f, 1f);
        }

        /// <summary>
        /// Recalculate impact bar targets without rebuilding GUI. Called on toggle changes.
        /// </summary>
        private static void RefreshImpactTargets()
        {
            var snap = ComputeImpact();
            for (int i = 0; i < _impactBars.Count; i++)
            {
                var s = _impactBars[i];
                float featureMs = GetFeatureMs(s.FeatureKey, snap);
                s.TargetFraction = snap.TotalMs > 0.001f
                    ? Math.Clamp(featureMs / snap.TotalMs, 0f, 1f) : 0f;
                _impactBars[i] = s;
            }
            _animActive = true;
            _tickCounter = 0;
        }

        /// <summary>
        /// Per-frame animation driver for impact bars. Called from GuiDrawPostfix.
        /// </summary>
        public static void TickImpactBars()
        {
            if (frame == null || !_showImpactBars || !_animActive) return;
            _tickCounter++;

            // Periodic refresh: re-read EMA stats every ~0.25s
            if (_tickCounter % 15 == 0)
                RefreshImpactTargets();

            const float speed = 0.12f;
            const float eps = 0.002f;
            bool moving = false;

            for (int i = 0; i < _impactBars.Count; i++)
            {
                var s = _impactBars[i];
                if (Math.Abs(s.CurrentFraction - s.TargetFraction) < eps)
                {
                    s.CurrentFraction = s.TargetFraction;
                }
                else
                {
                    s.CurrentFraction += (s.TargetFraction - s.CurrentFraction) * speed;
                    moving = true;
                }

                s.Bar.BarSize = s.CurrentFraction;
                s.Bar.Color = BarColor(s.CurrentFraction);
                s.PctLabel.Text = s.CurrentFraction >= 0.01f
                    ? $"{s.CurrentFraction * 100f:F0}%" : "<1%";
                _impactBars[i] = s;
            }

            if (_totalSavedLabel != null && _tickCounter % 15 == 0)
                _totalSavedLabel.Text = Localization.Format("stats_saved", Stats.EstimatedSavedMs());

            if (!moving && _tickCounter > 60)
                _animActive = false;
        }

        private static void PrintSignalGraphAnalysis()
        {
            bool isCN = Localization.T("btn_close") == "关闭";

            int mode = OptimizerConfig.SignalGraphMode;
            bool compiled = SignalGraph.SignalGraphEvaluator.IsCompiled;
            int nodes = SignalGraph.SignalGraphEvaluator.AcceleratedNodeCount;
            int regs = SignalGraph.SignalGraphEvaluator.RegisterCount;
            float avgMs = SignalGraph.SignalGraphEvaluator.AvgTickMs;

            DebugConsole.NewMessage("", Color.White);
            if (isCN)
            {
                DebugConsole.NewMessage("──── 信号图加速状态分析 ────", Color.Cyan);
                if (mode == 0)
                {
                    DebugConsole.NewMessage("当前模式: 关闭", Color.Yellow);
                    DebugConsole.NewMessage("信号图加速未启用。如需加速电路处理，请在设置中选择\"加速模式\"或\"激进模式\"。", Color.Yellow);
                }
                else if (!compiled)
                {
                    DebugConsole.NewMessage("当前模式: " + (mode == 1 ? "加速" : "激进") + "，但尚未编译", Color.Red);
                    DebugConsole.NewMessage("可能原因: 回合未开始，或潜艇没有可编译的电路组件。", Color.Yellow);
                    DebugConsole.NewMessage("解决方法: 确保已加载含电路的潜艇并开始回合。", Color.Yellow);
                }
                else
                {
                    string modeName = mode == 1 ? "加速模式" : "激进模式";
                    DebugConsole.NewMessage($"当前模式: {modeName} — 编译成功", Color.LimeGreen);
                    DebugConsole.NewMessage($"已编译 {nodes} 个节点，使用 {regs} 个寄存器", Color.LimeGreen);
                    if (avgMs > 0.5f)
                        DebugConsole.NewMessage($"信号图每帧耗时 {avgMs:F2}ms — 偏高，电路可能过于复杂", Color.Yellow);
                    else if (avgMs > 0.1f)
                        DebugConsole.NewMessage($"信号图每帧耗时 {avgMs:F2}ms — 正常", Color.LimeGreen);
                    else
                        DebugConsole.NewMessage($"信号图每帧耗时 {avgMs:F2}ms — 很快", Color.LimeGreen);

                    if (nodes == 0)
                        DebugConsole.NewMessage("未检测到可加速的电路节点。潜艇可能不含逻辑门/传感器等信号组件。", Color.Yellow);
                }
            }
            else
            {
                DebugConsole.NewMessage("──── Signal Graph Analysis ────", Color.Cyan);
                if (mode == 0)
                {
                    DebugConsole.NewMessage("Mode: Off", Color.Yellow);
                    DebugConsole.NewMessage("Signal graph acceleration is disabled. Enable 'Accelerate' or 'Aggressive' mode in settings to speed up circuit processing.", Color.Yellow);
                }
                else if (!compiled)
                {
                    DebugConsole.NewMessage("Mode: " + (mode == 1 ? "Accelerate" : "Aggressive") + " — NOT compiled", Color.Red);
                    DebugConsole.NewMessage("Possible cause: Round not started, or submarine has no compilable circuit components.", Color.Yellow);
                    DebugConsole.NewMessage("Fix: Load a submarine with circuits and start a round.", Color.Yellow);
                }
                else
                {
                    string modeName = mode == 1 ? "Accelerate" : "Aggressive";
                    DebugConsole.NewMessage($"Mode: {modeName} — compiled OK", Color.LimeGreen);
                    DebugConsole.NewMessage($"Compiled {nodes} nodes using {regs} registers", Color.LimeGreen);
                    if (avgMs > 0.5f)
                        DebugConsole.NewMessage($"Per-frame cost: {avgMs:F2}ms — high, circuits may be too complex", Color.Yellow);
                    else if (avgMs > 0.1f)
                        DebugConsole.NewMessage($"Per-frame cost: {avgMs:F2}ms — normal", Color.LimeGreen);
                    else
                        DebugConsole.NewMessage($"Per-frame cost: {avgMs:F2}ms — fast", Color.LimeGreen);

                    if (nodes == 0)
                        DebugConsole.NewMessage("No acceleratable nodes found. Submarine may lack logic gates, sensors, or signal components.", Color.Yellow);
                }
            }
        }
    }
}
