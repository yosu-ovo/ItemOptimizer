using Barotrauma;
using Microsoft.Xna.Framework;

namespace ItemOptimizerMod
{
    /// <summary>
    /// Shared constants and helpers for StatsOverlay and ServerPerfOverlay.
    /// </summary>
    static class OverlayHelper
    {
        internal static readonly Color HealthGoodColor = new Color(50, 205, 50);
        internal static readonly Color HealthWarnColor = new Color(255, 200, 0);
        internal static readonly Color HealthCritColor = new Color(255, 60, 60);
        internal static readonly Color BarBgColor = new Color(40, 40, 40, 180);
        internal const string Separator = "───────────────";

        internal static Color GetHealthColor(HealthLevel h)
        {
            if (h == HealthLevel.Good) return HealthGoodColor;
            if (h == HealthLevel.Warning) return HealthWarnColor;
            return HealthCritColor;
        }

        internal static string GetHealthLabel(HealthLevel h)
        {
            if (h == HealthLevel.Good) return Localization.T("server_good");
            if (h == HealthLevel.Warning) return Localization.T("server_warning");
            return Localization.T("server_critical");
        }
    }
}
