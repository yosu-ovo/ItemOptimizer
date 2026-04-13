using System;
using Barotrauma;
using ItemOptimizerMod.Patches;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ItemOptimizerMod
{
    static class StatsOverlay
    {
        internal static bool Visible;

        private const int Padding = 10;
        private const int LineSpacing = 4;
        private const int BarHeight = 14;
        private const int BarMaxWidth = 180;
        private const int LabelWidth = 80; // enough for "工作线程1" etc.

        private static readonly Color MainThreadColor = new Color(255, 165, 0);   // Orange
        private static readonly Color WorkerColor = new Color(50, 205, 50);       // LimeGreen

        public static void Draw(SpriteBatch spriteBatch)
        {
            if (!Visible) return;

            var font = GUIStyle.SmallFont;
            string title = Localization.T("mod_name");
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

            // If parallel dispatch active, reserve space for thread bars section
            bool showParallel = UpdateAllTakeover.Enabled && OptimizerConfig.EnableParallelDispatch;
            // Use DisplayThreadCount for stable rendering (no flickering)
            int threadCount = showParallel ? Stats.DisplayThreadCount : 0;
            float parallelSectionHeight = 0;
            string parallelHeader = "";
            string parallelItemsLine = "";
            string parallelSavedLine = "";

            if (showParallel && threadCount > 0)
            {
                parallelHeader = Localization.T("section_threads");
                parallelItemsLine = Localization.Format("parallel_items", Stats.AvgParallelItems, Stats.AvgMainThreadItems);
                parallelSavedLine = Localization.Format("parallel_saved", Stats.ParallelSavedMs());

                float headerH = font.MeasureString(parallelHeader).Y + LineSpacing;
                float barsH = threadCount * (BarHeight + LineSpacing);
                float itemsH = font.MeasureString(parallelItemsLine).Y + LineSpacing;
                float savedH = font.MeasureString(parallelSavedLine).Y + LineSpacing;
                parallelSectionHeight = headerH + barsH + itemsH + savedH;

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

            float panelW = maxWidth + Padding * 2;
            float panelH = totalHeight + parallelSectionHeight + serverSectionHeight + Padding * 2;
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

            // Draw parallel thread bars
            if (showParallel && threadCount > 0)
            {
                // Section header
                GUI.DrawString(spriteBatch,
                    new Vector2(panelX + Padding, y),
                    parallelHeader, Color.Cyan, font: font);
                y += font.MeasureString(parallelHeader).Y + LineSpacing;

                // Find max ms across threads for normalization (use smoothed values)
                float maxMs = 0.01f;
                for (int i = 0; i < threadCount; i++)
                {
                    if (Stats.AvgThreadMs[i] > maxMs) maxMs = Stats.AvgThreadMs[i];
                }

                // Draw per-thread bars
                float barX = panelX + Padding + LabelWidth;
                for (int i = 0; i < threadCount; i++)
                {
                    float ms = Stats.AvgThreadMs[i];
                    int items = Stats.AvgThreadItems[i];
                    bool isMain = (i == 0);
                    string label = isMain
                        ? Localization.T("parallel_main")
                        : $"{Localization.T("parallel_worker")}{i}";
                    Color barColor = isMain ? MainThreadColor : WorkerColor;

                    // Label (left of bar)
                    GUI.DrawString(spriteBatch,
                        new Vector2(panelX + Padding, y + 1),
                        label, barColor, font: font);

                    // Bar background
                    GUI.DrawRectangle(spriteBatch,
                        new Vector2(barX, y),
                        new Vector2(BarMaxWidth, BarHeight),
                        OverlayHelper.BarBgColor, isFilled: true);

                    // Bar fill (proportional to max)
                    float barW = Math.Max(1, (ms / maxMs) * BarMaxWidth);
                    GUI.DrawRectangle(spriteBatch,
                        new Vector2(barX, y),
                        new Vector2(barW, BarHeight),
                        barColor * 0.8f, isFilled: true);

                    // Bar outline
                    GUI.DrawRectangle(spriteBatch,
                        new Vector2(barX, y),
                        new Vector2(BarMaxWidth, BarHeight),
                        barColor * 0.4f, isFilled: false);

                    // Stats text (right of bar)
                    string statsText = $"{ms:F1}ms ({items})";
                    GUI.DrawString(spriteBatch,
                        new Vector2(barX + BarMaxWidth + 6, y + 1),
                        statsText, Color.White, font: font);

                    y += BarHeight + LineSpacing;
                }

                // Items line
                GUI.DrawString(spriteBatch,
                    new Vector2(panelX + Padding, y),
                    parallelItemsLine, Color.White, font: font);
                y += font.MeasureString(parallelItemsLine).Y + LineSpacing;

                // Saved line
                GUI.DrawString(spriteBatch,
                    new Vector2(panelX + Padding, y),
                    parallelSavedLine, Color.LimeGreen, font: font);
                y += font.MeasureString(parallelSavedLine).Y + LineSpacing;
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
            }
        }
    }
}
