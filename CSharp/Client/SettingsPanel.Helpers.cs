using System;
using Barotrauma;
using Microsoft.Xna.Framework;

namespace ItemOptimizerMod
{
    static partial class SettingsPanel
    {
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
                    LuaCsLogger.Log($"[ItemOptimizer] {nameKey} = {tb.Selected}");
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
                    LuaCsLogger.Log($"[ItemOptimizer] {nameKey} = {tb.Selected}");
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
                    LuaCsLogger.Log($"[ItemOptimizer] {nameKey} = {tb.Selected}");
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

        private static void StatLine(GUIComponent parent, string nameKey, float avgValue)
        {
            new GUITextBlock(
                new RectTransform(new Vector2(1f, 0.04f), parent.RectTransform),
                Localization.Format("stats_format", Localization.T(nameKey), avgValue),
                font: GUIStyle.SmallFont);
        }
    }
}
