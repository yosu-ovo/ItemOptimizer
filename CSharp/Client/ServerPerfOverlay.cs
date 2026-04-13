using System;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ItemOptimizerMod
{
    /// <summary>
    /// Client-side: renders server performance breakdown as a bar chart overlay.
    /// Toggled via "showserverperf" console command.
    /// </summary>
    static class ServerPerfOverlay
    {
        internal static bool Visible;

        private const int Padding = 10;
        private const int LineSpacing = 3;
        private const int BarHeight = 16;
        private const int BarMaxWidth = 200;
        private const int LabelWidth = 110;

        private static readonly Color BarBgColor = OverlayHelper.BarBgColor;

        // Per-system colors (matching client showperf aesthetic)
        private static readonly Color ColGameSession  = new Color(100, 180, 255);  // Blue
        private static readonly Color ColCharacter     = new Color(255, 180, 80);   // Orange
        private static readonly Color ColStatusEffect  = new Color(255, 100, 100);  // Red
        private static readonly Color ColMapEntity     = new Color(100, 255, 100);  // Green
        private static readonly Color ColRagdoll       = new Color(200, 140, 255);  // Purple
        private static readonly Color ColPhysics       = new Color(255, 255, 100);  // Yellow
        private static readonly Color ColNetworking    = new Color(100, 255, 255);  // Cyan

        public static void Draw(SpriteBatch spriteBatch)
        {
            if (!Visible) return;
            if (!ServerMetrics.HasServerData) return;

            var font = GUIStyle.SmallFont;

            // ── Build lines ──
            string healthLabel = OverlayHelper.GetHealthLabel(ServerMetrics.Health);

            Color healthColor = OverlayHelper.GetHealthColor(ServerMetrics.Health);

            string title = $"{Localization.T("section_server")} Perf: {healthLabel} ({ServerMetrics.HealthScore})";
            string tickLine = $"Tick: {ServerMetrics.AvgTickMs:F1}ms @ {ServerMetrics.TickRate}Hz";
            string infoLine = Localization.Format("server_clients_entities",
                ServerMetrics.ClientCount, ServerMetrics.EntityCount);
            string queueLine = Localization.Format("server_queues",
                ServerMetrics.AvgPendingPos, ServerMetrics.AvgEventQueue);

            // ── System breakdown ──
            var systems = new (string label, float ms, Color color)[]
            {
                ("GameSession",   ServerMetrics.PerfGameSession,  ColGameSession),
                ("Character",     ServerMetrics.PerfCharacter,     ColCharacter),
                ("StatusEffect",  ServerMetrics.PerfStatusEffect,  ColStatusEffect),
                ("MapEntity",     ServerMetrics.PerfMapEntity,     ColMapEntity),
                ("Ragdoll",       ServerMetrics.PerfRagdoll,       ColRagdoll),
                ("Physics",       ServerMetrics.PerfPhysics,       ColPhysics),
                ("Networking",    ServerMetrics.PerfNetworking,    ColNetworking),
            };

            // ── Measure panel ──
            float maxWidth = LabelWidth + BarMaxWidth + 80; // label + bar + "12.3ms"
            float titleW = font.MeasureString(title).X;
            if (titleW > maxWidth) maxWidth = titleW;
            float tickW = font.MeasureString(tickLine).X;
            if (tickW > maxWidth) maxWidth = tickW;

            float lineH = font.MeasureString("X").Y + LineSpacing;
            // title + tick + info + queues + separator + 7 bars + total
            float panelH = lineH * 4 + LineSpacing + systems.Length * (BarHeight + LineSpacing) + lineH + Padding * 2;
            float panelW = maxWidth + Padding * 2;

            // Position: left side (to not overlap StatsOverlay on right)
            float panelX = Padding;
            float panelY = Padding;

            // ── Draw background ──
            GUI.DrawRectangle(spriteBatch,
                new Vector2(panelX, panelY),
                new Vector2(panelW, panelH),
                Color.Black * 0.7f, isFilled: true);

            float y = panelY + Padding;

            // ── Title ──
            GUI.DrawString(spriteBatch, new Vector2(panelX + Padding, y), title, healthColor, font: font);
            y += lineH;

            // ── Tick line ──
            GUI.DrawString(spriteBatch, new Vector2(panelX + Padding, y), tickLine, Color.White, font: font);
            y += lineH;

            // ── Info line ──
            GUI.DrawString(spriteBatch, new Vector2(panelX + Padding, y), infoLine, Color.White, font: font);
            y += lineH;

            // ── Queue line ──
            GUI.DrawString(spriteBatch, new Vector2(panelX + Padding, y), queueLine, Color.White, font: font);
            y += lineH;

            if (!ServerMetrics.HasPerfData)
            {
                GUI.DrawString(spriteBatch, new Vector2(panelX + Padding, y),
                    Localization.T("overlay_waiting"), Color.Gray, font: font);
                return;
            }

            // ── Separator ──
            GUI.DrawString(spriteBatch, new Vector2(panelX + Padding, y),
                OverlayHelper.Separator, Color.White * 0.5f, font: font);
            y += lineH;

            // ── Find max ms for bar normalization ──
            float maxMs = 1f;
            foreach (var (_, ms, _) in systems)
                if (ms > maxMs) maxMs = ms;

            float totalMs = 0;
            float barX = panelX + Padding + LabelWidth;

            // ── Per-system bars ──
            foreach (var (label, ms, color) in systems)
            {
                totalMs += ms;

                // Label
                GUI.DrawString(spriteBatch, new Vector2(panelX + Padding, y + 1), label, color, font: font);

                // Bar background
                GUI.DrawRectangle(spriteBatch,
                    new Vector2(barX, y),
                    new Vector2(BarMaxWidth, BarHeight),
                    BarBgColor, isFilled: true);

                // Bar fill
                float barW = Math.Max(1, (ms / maxMs) * BarMaxWidth);
                GUI.DrawRectangle(spriteBatch,
                    new Vector2(barX, y),
                    new Vector2(barW, BarHeight),
                    color * 0.8f, isFilled: true);

                // Bar outline
                GUI.DrawRectangle(spriteBatch,
                    new Vector2(barX, y),
                    new Vector2(BarMaxWidth, BarHeight),
                    color * 0.3f, isFilled: false);

                // ms text
                string msText = $"{ms:F1}ms";
                GUI.DrawString(spriteBatch,
                    new Vector2(barX + BarMaxWidth + 6, y + 1),
                    msText, Color.White, font: font);

                y += BarHeight + LineSpacing;
            }

            // ── Total line ──
            string totalText = $"Total: {totalMs:F1}ms";
            GUI.DrawString(spriteBatch, new Vector2(panelX + Padding, y), totalText, Color.White, font: font);
        }
    }
}
