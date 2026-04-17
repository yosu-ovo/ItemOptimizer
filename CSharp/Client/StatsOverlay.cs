using System;
using System.Collections.Generic;
using Barotrauma;
using ItemOptimizerMod.Patches;
using ItemOptimizerMod.Proxy;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ItemOptimizerMod
{
    static class StatsOverlay
    {
        internal static bool Visible;

        internal const string Version = DiagnosticHeader.ModVersion;

        private const int Padding = 10;
        private const int LineSpacing = 4;
        private const int BarHeight = 14;
        private const int BarMaxWidth = 180;
        private const int LabelWidth = 80;

        private static readonly Color MainThreadColor = new Color(255, 165, 0);   // Orange
        private static readonly Color ProxyColor = new Color(0, 206, 209);        // DarkTurquoise

        public static void Draw(SpriteBatch spriteBatch)
        {
            if (!Visible) return;

            var font = GUIStyle.SmallFont;
            string title = $"{Localization.T("mod_name")} {Version}";
            string sep = OverlayHelper.Separator;

            string lineCold = $"{Localization.T("strategy_cold_storage")}: ~{Stats.AvgColdStorageSkips:F0}/frame";
            string lineGnd  = $"{Localization.T("strategy_ground_item")}: ~{Stats.AvgGroundItemSkips:F0}/frame";
            string lineCi   = $"{Localization.T("strategy_ci_throttle")}: ~{Stats.AvgCustomInterfaceSkips:F0}/frame";
            string lineMot  = $"{Localization.T("strategy_motion")}: ~{Stats.AvgMotionSensorSkips:F0}/frame";
            string lineWear = $"{Localization.T("strategy_wearable")}: ~{Stats.AvgWearableSkips:F0}/frame";
            string lineRule = $"{Localization.T("stats_item_rules")}: ~{Stats.AvgItemRuleSkips:F0}/frame";
            string lineModOpt = $"{Localization.T("stats_mod_opt")}: ~{Stats.AvgModOptSkips:F0}/frame";
            string lineWd   = $"{Localization.T("stats_water_det")}: ~{Stats.AvgWaterDetectorSkips:F0}/frame";
            string lineDoor = $"{Localization.T("stats_door")}: ~{Stats.AvgDoorSkips:F0}/frame";
            string lineHst  = $"{Localization.T("stats_hst_cache")}: ~{Stats.AvgHasStatusTagCacheHits:F0}/frame";
            string lineShud = $"{Localization.T("stats_statushud")}: ~{Stats.AvgStatusHUDSkips:F0}/frame";
            string lineAffl = $"{Localization.T("stats_affliction")}: ~{Stats.AvgAfflictionDedupSkips:F0}/frame";
            string lineAnimLod = $"{Localization.T("stats_anim_lod")}: ~{Stats.AvgAnimLODSkipped + Stats.AvgAnimLODHalfRate:F0}/frame";
            string lineCharStagger = $"{Localization.T("stats_char_stagger")}: ~{Stats.AvgCharStaggerSkipped:F0}/frame";
            string lineLadderFix = $"{Localization.T("stats_ladder_fix")}: ~{Stats.AvgLadderFixCorrections:F1}/frame";
            string lineSave = Localization.Format("stats_saved", Stats.EstimatedSavedMs());
            string lineMiscP = $"{Localization.T("strategy_misc_parallel")}: {(OptimizerConfig.EnableMiscParallel ? "ON" : "OFF")}";

            string[] lines = { title, sep, lineCold, lineGnd, lineCi, lineMot, lineWear, lineRule, lineModOpt, lineWd, lineDoor, lineHst, lineShud, lineAffl, lineAnimLod, lineCharStagger, lineLadderFix, sep, lineSave, lineMiscP };

            // Measure panel size
            float maxWidth = 0;
            float totalHeight = 0;
            foreach (var line in lines)
            {
                Vector2 size = font.MeasureString(line);
                if (size.X > maxWidth) maxWidth = size.X;
                totalHeight += size.Y + LineSpacing;
            }
            totalHeight -= LineSpacing;

            // If takeover active, reserve space for dispatch bars section
            bool showDispatch = UpdateAllTakeover.Enabled;
            float dispatchSectionHeight = 0;
            string dispatchHeader = "";

            string dispatchTotalLine = "";
            string phaseBreakdown = "";
            string subPhaseB = "";

            if (showDispatch)
            {
                dispatchHeader = Localization.T("section_threads");

                float mainThreadMs = Stats.AvgPhaseBMainLoopMs;
                float vanillaMs = Stats.AvgPhaseAMs + Stats.AvgPhaseCMs + Stats.AvgPhaseDMs;
                float trackedMs = mainThreadMs + vanillaMs
                    + Stats.AvgProxyBatchComputeMs + Stats.AvgProxySyncBackMs + Stats.AvgProxyPhysicsMs;
                float overheadMs = Math.Max(0, Stats.AvgTotalDispatchMs - trackedMs);
                dispatchTotalLine = string.Format(Localization.T("dispatch_total"),
                    Stats.AvgTotalDispatchMs, overheadMs);

                phaseBreakdown = $"  A:{Stats.AvgPhaseAMs:F1} Proxy:{Stats.AvgPhaseProxyMs:F1} B:{Stats.AvgPhaseBMs:F1} C:{Stats.AvgPhaseCMs:F1} D:{Stats.AvgPhaseDMs:F1}";
                subPhaseB = $"  B=HST:{Stats.AvgPhaseBPreBuildMs:F2} Cls:{Stats.AvgPhaseBClassifyMs:F1} Loop:{Stats.AvgPhaseBMainLoopMs:F1}";

                float headerH = font.MeasureString(dispatchHeader).Y + LineSpacing;
                float barsH = BarHeight + LineSpacing; // single main thread bar
                float summaryH = font.MeasureString(dispatchTotalLine).Y + LineSpacing;
                summaryH += font.MeasureString(phaseBreakdown).Y + LineSpacing;
                summaryH += font.MeasureString(subPhaseB).Y + LineSpacing;
                dispatchSectionHeight = headerH + barsH + summaryH;

                float barSectionWidth = LabelWidth + BarMaxWidth + 100;
                if (barSectionWidth > maxWidth) maxWidth = barSectionWidth;
            }

            // If proxy system active, reserve space for proxy section
            bool showProxy = ProxyRegistry.HasHandlers;
            float proxySectionHeight = 0;
            string proxyHeader = "";
            string proxyItemsLine = "";

            if (showProxy)
            {
                proxyHeader = Localization.T("section_proxy");
                proxyItemsLine = string.Format(Localization.T("proxy_items_count"),
                    Stats.AvgProxyItems, Barotrauma.Item.ItemList.Count);

                float headerH = font.MeasureString(proxyHeader).Y + LineSpacing;
                float barsH = 3 * (BarHeight + LineSpacing); // BatchCompute + SyncBack + PhysMaint
                float itemsH = font.MeasureString(proxyItemsLine).Y + LineSpacing;
                proxySectionHeight = headerH + barsH + itemsH;

                // Ensure width accommodates label + bar + stats text
                float barSectionWidth = LabelWidth + BarMaxWidth + 100;
                if (barSectionWidth > maxWidth) maxWidth = barSectionWidth;
            }

            // If server data available, reserve space for server section
            bool showServer = ServerMetrics.HasServerData;
            float serverSectionHeight = 0;
            string serverHeader = "";
            string serverTickLine = "";
            string serverClientsLine = "";
            string serverQueuesLine = "";
            string serverSkippedLine = "";

            if (showServer)
            {
                string healthLabel = OverlayHelper.GetHealthLabel(ServerMetrics.Health);

                serverHeader = $"{Localization.T("section_server")}: {healthLabel} ({ServerMetrics.HealthScore})";
                serverTickLine = $"  Tick: {ServerMetrics.AvgTickMs:F1}ms ({ServerMetrics.TickRate}Hz)";
                serverClientsLine = Localization.Format("server_clients_entities",
                    ServerMetrics.ClientCount, ServerMetrics.EntityCount);
                serverQueuesLine = Localization.Format("server_queues",
                    ServerMetrics.AvgPendingPos, ServerMetrics.AvgEventQueue);
                serverSkippedLine = Localization.Format("server_skipped", ServerMetrics.SkippedItems);

                float lineH = font.MeasureString(serverHeader).Y + LineSpacing;
                serverSectionHeight = lineH * 6; // separator + header + tick + clients + queues + skipped

                // Check width
                float[] serverWidths = {
                    font.MeasureString(serverHeader).X,
                    font.MeasureString(serverTickLine).X,
                    font.MeasureString(serverClientsLine).X,
                    font.MeasureString(serverQueuesLine).X,
                    font.MeasureString(serverSkippedLine).X
                };
                foreach (float w in serverWidths)
                    if (w > maxWidth) maxWidth = w;
            }

            // ── Held item info section ──
            bool showHeldItem = false;
            bool isWhitelisted = false;
            string heldId = "";
            string heldHeader = "";
            string heldIdLine = "";
            string heldNameLine = "";
            string heldModLine = "";
            string heldStatusLine = "";
            string heldBtnText = "";
            float heldSectionHeight = 0;
            Item heldItem = null;

            var controlled = Character.Controlled;
            if (controlled != null)
            {
                heldItem = controlled.SelectedItem;
                if (heldItem == null)
                {
                    foreach (var item in controlled.HeldItems)
                    {
                        heldItem = item;
                        break;
                    }
                }
            }

            if (heldItem?.Prefab != null)
            {
                showHeldItem = true;
                heldId = heldItem.Prefab.Identifier.Value;
                string name = heldItem.Prefab.Name?.Value ?? heldId;
                string pkg = heldItem.Prefab.ContentPackage?.Name ?? "Vanilla";
                isWhitelisted = OptimizerConfig.WhitelistLookup.Contains(heldId);

                heldHeader = Localization.T("hud_held_item");
                heldIdLine = $"  {Localization.T("hud_item_id")}: {heldId}";
                heldNameLine = $"  {Localization.T("hud_item_name")}: {name}";
                heldModLine = $"  {Localization.T("hud_item_mod")}: {pkg}";

                var statusParts = new List<string>();
                if (isWhitelisted)
                    statusParts.Add(Localization.T("hud_whitelisted"));
                if (ColdStorageDetector.IsInColdStorage(heldItem))
                    statusParts.Add(Localization.T("hud_cold_storage"));
                if (OptimizerConfig.RuleLookup.TryGetValue(heldId, out var rule))
                    statusParts.Add($"Rule: {rule.Action}/{rule.SkipFrames}f");
                if (OptimizerConfig.ModOptLookup.TryGetValue(heldId, out var modSkip))
                    statusParts.Add($"ModOpt: {modSkip}f");
                if (statusParts.Count == 0)
                    statusParts.Add(Localization.T("hud_no_opt"));

                heldStatusLine = $"  {Localization.T("hud_item_status")}: {string.Join(", ", statusParts)}";
                heldBtnText = Localization.T("btn_hud_whitelist");

                float lineH = font.MeasureString(heldHeader).Y + LineSpacing;
                // sep + header + id + name + mod + status + (button if not whitelisted)
                heldSectionHeight = lineH * 6;
                if (!isWhitelisted) heldSectionHeight += lineH;

                float[] heldWidths = {
                    font.MeasureString(heldHeader).X,
                    font.MeasureString(heldIdLine).X,
                    font.MeasureString(heldNameLine).X,
                    font.MeasureString(heldModLine).X,
                    font.MeasureString(heldStatusLine).X
                };
                foreach (float w in heldWidths)
                    if (w > maxWidth) maxWidth = w;
            }

            float panelW = maxWidth + Padding * 2;
            float panelH = totalHeight + dispatchSectionHeight + proxySectionHeight + serverSectionHeight + heldSectionHeight + Padding * 2;
            float panelX = GameMain.GraphicsWidth - panelW - Padding;
            float panelY = Padding;

            // Draw background
            GUI.DrawRectangle(spriteBatch,
                new Vector2(panelX, panelY),
                new Vector2(panelW, panelH),
                Color.Black * 0.6f, isFilled: true);

            // Draw text lines
            float y = panelY + Padding;
            foreach (var line in lines)
            {
                GUI.DrawString(spriteBatch,
                    new Vector2(panelX + Padding, y),
                    line, Color.White, font: font);
                y += font.MeasureString(line).Y + LineSpacing;
            }

            // Draw dispatch bar
            if (showDispatch)
            {
                // Section header
                GUI.DrawString(spriteBatch,
                    new Vector2(panelX + Padding, y),
                    dispatchHeader, Color.Cyan, font: font);
                y += font.MeasureString(dispatchHeader).Y + LineSpacing;

                // Main thread bar
                float mainMs = Stats.AvgPhaseBMainLoopMs;
                float maxMs = Math.Max(0.01f, mainMs);

                float barX = panelX + Padding + LabelWidth;
                string label = Localization.T("parallel_main");

                // Label
                GUI.DrawString(spriteBatch,
                    new Vector2(panelX + Padding, y + 1),
                    label, MainThreadColor, font: font);

                // Bar background
                GUI.DrawRectangle(spriteBatch,
                    new Vector2(barX, y),
                    new Vector2(BarMaxWidth, BarHeight),
                    OverlayHelper.BarBgColor, isFilled: true);

                // Bar fill
                float barW = Math.Max(1, (mainMs / maxMs) * BarMaxWidth);
                GUI.DrawRectangle(spriteBatch,
                    new Vector2(barX, y),
                    new Vector2(barW, BarHeight),
                    MainThreadColor * 0.8f, isFilled: true);

                // Bar outline
                GUI.DrawRectangle(spriteBatch,
                    new Vector2(barX, y),
                    new Vector2(BarMaxWidth, BarHeight),
                    MainThreadColor * 0.4f, isFilled: false);

                // Stats text
                string statsText = $"{mainMs:F1}ms";
                GUI.DrawString(spriteBatch,
                    new Vector2(barX + BarMaxWidth + 6, y + 1),
                    statsText, Color.White, font: font);

                y += BarHeight + LineSpacing;

                // Total dispatch + overhead
                GUI.DrawString(spriteBatch,
                    new Vector2(panelX + Padding, y),
                    dispatchTotalLine, Color.Gray, font: font);
                y += font.MeasureString(dispatchTotalLine).Y + LineSpacing;

                // Phase breakdown diagnostic
                GUI.DrawString(spriteBatch,
                    new Vector2(panelX + Padding, y),
                    phaseBreakdown, Color.DarkGray, font: font);
                y += font.MeasureString(phaseBreakdown).Y + LineSpacing;

                // Sub-phase B breakdown
                GUI.DrawString(spriteBatch,
                    new Vector2(panelX + Padding, y),
                    subPhaseB, Color.DarkGray, font: font);
                y += font.MeasureString(subPhaseB).Y + LineSpacing;
            }

            // ── Proxy system section ──
            if (showProxy)
            {
                // Section header
                GUI.DrawString(spriteBatch,
                    new Vector2(panelX + Padding, y),
                    proxyHeader, ProxyColor, font: font);
                y += font.MeasureString(proxyHeader).Y + LineSpacing;

                // Find max ms across proxy bars for normalization
                float maxMs = Math.Max(0.01f,
                    Math.Max(Stats.AvgProxyBatchComputeMs,
                    Math.Max(Stats.AvgProxySyncBackMs, Stats.AvgProxyPhysicsMs)));

                float barX = panelX + Padding + LabelWidth;

                // BatchCompute bar
                {
                    string label = Localization.T("proxy_batch");
                    float ms = Stats.AvgProxyBatchComputeMs;

                    // Label
                    GUI.DrawString(spriteBatch,
                        new Vector2(panelX + Padding, y + 1),
                        label, ProxyColor, font: font);

                    // Bar background
                    GUI.DrawRectangle(spriteBatch,
                        new Vector2(barX, y),
                        new Vector2(BarMaxWidth, BarHeight),
                        OverlayHelper.BarBgColor, isFilled: true);

                    // Bar fill
                    float barW = Math.Max(1, (ms / maxMs) * BarMaxWidth);
                    GUI.DrawRectangle(spriteBatch,
                        new Vector2(barX, y),
                        new Vector2(barW, BarHeight),
                        ProxyColor * 0.8f, isFilled: true);

                    // Bar outline
                    GUI.DrawRectangle(spriteBatch,
                        new Vector2(barX, y),
                        new Vector2(BarMaxWidth, BarHeight),
                        ProxyColor * 0.4f, isFilled: false);

                    // Stats text
                    string statsText = $"{ms:F1}ms";
                    GUI.DrawString(spriteBatch,
                        new Vector2(barX + BarMaxWidth + 6, y + 1),
                        statsText, Color.White, font: font);

                    y += BarHeight + LineSpacing;
                }

                // SyncBack bar
                {
                    string label = Localization.T("proxy_sync");
                    float ms = Stats.AvgProxySyncBackMs;

                    // Label
                    GUI.DrawString(spriteBatch,
                        new Vector2(panelX + Padding, y + 1),
                        label, ProxyColor, font: font);

                    // Bar background
                    GUI.DrawRectangle(spriteBatch,
                        new Vector2(barX, y),
                        new Vector2(BarMaxWidth, BarHeight),
                        OverlayHelper.BarBgColor, isFilled: true);

                    // Bar fill
                    float barW = Math.Max(1, (ms / maxMs) * BarMaxWidth);
                    GUI.DrawRectangle(spriteBatch,
                        new Vector2(barX, y),
                        new Vector2(barW, BarHeight),
                        ProxyColor * 0.8f, isFilled: true);

                    // Bar outline
                    GUI.DrawRectangle(spriteBatch,
                        new Vector2(barX, y),
                        new Vector2(BarMaxWidth, BarHeight),
                        ProxyColor * 0.4f, isFilled: false);

                    // Stats text
                    string statsText = $"{ms:F1}ms";
                    GUI.DrawString(spriteBatch,
                        new Vector2(barX + BarMaxWidth + 6, y + 1),
                        statsText, Color.White, font: font);

                    y += BarHeight + LineSpacing;
                }

                // PhysMaint bar
                {
                    string label = Localization.T("proxy_physics");
                    float ms = Stats.AvgProxyPhysicsMs;

                    // Label
                    GUI.DrawString(spriteBatch,
                        new Vector2(panelX + Padding, y + 1),
                        label, ProxyColor, font: font);

                    // Bar background
                    GUI.DrawRectangle(spriteBatch,
                        new Vector2(barX, y),
                        new Vector2(BarMaxWidth, BarHeight),
                        OverlayHelper.BarBgColor, isFilled: true);

                    // Bar fill
                    float barW = Math.Max(1, (ms / maxMs) * BarMaxWidth);
                    GUI.DrawRectangle(spriteBatch,
                        new Vector2(barX, y),
                        new Vector2(barW, BarHeight),
                        ProxyColor * 0.8f, isFilled: true);

                    // Bar outline
                    GUI.DrawRectangle(spriteBatch,
                        new Vector2(barX, y),
                        new Vector2(BarMaxWidth, BarHeight),
                        ProxyColor * 0.4f, isFilled: false);

                    // Stats text
                    string statsText = $"{ms:F1}ms";
                    GUI.DrawString(spriteBatch,
                        new Vector2(barX + BarMaxWidth + 6, y + 1),
                        statsText, Color.White, font: font);

                    y += BarHeight + LineSpacing;
                }

                // Items count line
                GUI.DrawString(spriteBatch,
                    new Vector2(panelX + Padding, y),
                    proxyItemsLine, Color.White, font: font);
                y += font.MeasureString(proxyItemsLine).Y + LineSpacing;
            }

            // ── Server health section ──
            if (showServer)
            {
                Color healthColor = OverlayHelper.GetHealthColor(ServerMetrics.Health);

                // Separator
                GUI.DrawString(spriteBatch,
                    new Vector2(panelX + Padding, y),
                    OverlayHelper.Separator, Color.White, font: font);
                y += font.MeasureString(OverlayHelper.Separator).Y + LineSpacing;

                // Header with health color
                GUI.DrawString(spriteBatch,
                    new Vector2(panelX + Padding, y),
                    serverHeader, healthColor, font: font);
                y += font.MeasureString(serverHeader).Y + LineSpacing;

                // Tick
                GUI.DrawString(spriteBatch,
                    new Vector2(panelX + Padding, y),
                    serverTickLine, Color.White, font: font);
                y += font.MeasureString(serverTickLine).Y + LineSpacing;

                // Clients + entities
                GUI.DrawString(spriteBatch,
                    new Vector2(panelX + Padding, y),
                    serverClientsLine, Color.White, font: font);
                y += font.MeasureString(serverClientsLine).Y + LineSpacing;

                // Queues
                GUI.DrawString(spriteBatch,
                    new Vector2(panelX + Padding, y),
                    serverQueuesLine, Color.White, font: font);
                y += font.MeasureString(serverQueuesLine).Y + LineSpacing;

                // Skipped items
                GUI.DrawString(spriteBatch,
                    new Vector2(panelX + Padding, y),
                    serverSkippedLine, Color.White, font: font);
                y += font.MeasureString(serverSkippedLine).Y + LineSpacing;
            }

            // ── Held item section ──
            if (showHeldItem)
            {
                // Separator
                GUI.DrawString(spriteBatch,
                    new Vector2(panelX + Padding, y),
                    sep, Color.White, font: font);
                y += font.MeasureString(sep).Y + LineSpacing;

                // Header
                GUI.DrawString(spriteBatch,
                    new Vector2(panelX + Padding, y),
                    heldHeader, Color.Gold, font: font);
                y += font.MeasureString(heldHeader).Y + LineSpacing;

                // ID
                GUI.DrawString(spriteBatch,
                    new Vector2(panelX + Padding, y),
                    heldIdLine, Color.White, font: font);
                y += font.MeasureString(heldIdLine).Y + LineSpacing;

                // Name
                GUI.DrawString(spriteBatch,
                    new Vector2(panelX + Padding, y),
                    heldNameLine, Color.White, font: font);
                y += font.MeasureString(heldNameLine).Y + LineSpacing;

                // Mod
                GUI.DrawString(spriteBatch,
                    new Vector2(panelX + Padding, y),
                    heldModLine, Color.Gray, font: font);
                y += font.MeasureString(heldModLine).Y + LineSpacing;

                // Status
                Color statusColor = isWhitelisted ? Color.Gold : Color.LimeGreen;
                GUI.DrawString(spriteBatch,
                    new Vector2(panelX + Padding, y),
                    heldStatusLine, statusColor, font: font);
                y += font.MeasureString(heldStatusLine).Y + LineSpacing;

                // Whitelist button (only if not already whitelisted)
                if (!isWhitelisted)
                {
                    Vector2 btnTextSize = font.MeasureString(heldBtnText);
                    int btnW = (int)btnTextSize.X + 16;
                    int btnH = (int)btnTextSize.Y + 8;
                    int btnX = (int)(panelX + Padding);
                    int btnY = (int)y + 2;

                    var btnRect = new Rectangle(btnX, btnY, btnW, btnH);
                    bool hovered = btnRect.Contains(PlayerInput.MousePosition.ToPoint());

                    Color btnBg = hovered ? new Color(80, 100, 60, 200) : new Color(50, 60, 40, 180);
                    GUI.DrawRectangle(spriteBatch,
                        new Vector2(btnX, btnY), new Vector2(btnW, btnH),
                        btnBg, isFilled: true);

                    GUI.DrawRectangle(spriteBatch,
                        new Vector2(btnX, btnY), new Vector2(btnW, btnH),
                        Color.Gold * 0.6f, isFilled: false);

                    GUI.DrawString(spriteBatch,
                        new Vector2(btnX + 8, btnY + 4),
                        heldBtnText, Color.Gold, font: font);

                    if (hovered && PlayerInput.PrimaryMouseButtonClicked())
                    {
                        if (!string.IsNullOrEmpty(heldId) && !OptimizerConfig.Whitelist.Contains(heldId))
                        {
                            OptimizerConfig.Whitelist.Add(heldId);
                            OptimizerConfig.RebuildWhitelistLookup();
                            OptimizerConfig.AutoSave();
                        }
                    }
                }
            }
        }
    }
}
