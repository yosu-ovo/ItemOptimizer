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
            bool currentValue, Action<bool> setter, float impactFraction = -1f)
        {
            bool showBar = _showImpactBars && impactFraction >= 0f;
            float tickW = showBar ? 0.55f : 0.7f;

            var row = new GUILayoutGroup(
                new RectTransform(new Vector2(1f, 0.05f), parent.RectTransform),
                isHorizontal: true);

            new GUITickBox(
                new RectTransform(new Vector2(tickW, 1f), row.RectTransform),
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

            if (showBar)
            {
                var (bar, pct) = AppendImpactBar(row, 0.32f, 0.12f, impactFraction);
                _impactBars.Add(new ImpactBarState
                {
                    Bar = bar, PctLabel = pct, FeatureKey = nameKey,
                    CurrentFraction = impactFraction, TargetFraction = impactFraction
                });
            }
        }

        private static void StrategyTickBoxWithNumber(GUIComponent parent, string nameKey, string descKey,
            bool currentEnabled, Action<bool> enableSetter,
            int currentSkip, Action<int> skipSetter,
            float impactFraction = -1f)
        {
            bool showBar = _showImpactBars && impactFraction >= 0f;

            var row = new GUILayoutGroup(
                new RectTransform(new Vector2(1f, 0.05f), parent.RectTransform),
                isHorizontal: true)
            {
                RelativeSpacing = 0.01f
            };

            new GUITickBox(
                new RectTransform(new Vector2(showBar ? 0.40f : 0.55f, 1f), row.RectTransform),
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
                new RectTransform(new Vector2(showBar ? 0.10f : 0.15f, 1f), row.RectTransform),
                Localization.T("skip_frames_label"),
                textAlignment: Alignment.CenterRight,
                font: GUIStyle.SmallFont);

            new GUINumberInput(
                new RectTransform(new Vector2(showBar ? 0.15f : 0.2f, 1f), row.RectTransform),
                NumberType.Int)
            {
                IntValue = currentSkip,
                MinValueInt = 1,
                MaxValueInt = 30,
                OnValueChanged = ni => skipSetter(ni.IntValue)
            };

            if (showBar)
            {
                var (bar, pct) = AppendImpactBar(row, 0.22f, 0.10f, impactFraction);
                _impactBars.Add(new ImpactBarState
                {
                    Bar = bar, PctLabel = pct, FeatureKey = nameKey,
                    CurrentFraction = impactFraction, TargetFraction = impactFraction
                });
            }
        }

        private static void StrategyTickBoxWithNumber(GUIComponent parent, string nameKey, string descKey,
            bool currentEnabled, Action<bool> enableSetter,
            int currentValue, Action<int> valueSetter,
            int minValue, int maxValue, string labelKey,
            float impactFraction = -1f)
        {
            bool showBar = _showImpactBars && impactFraction >= 0f;

            var row = new GUILayoutGroup(
                new RectTransform(new Vector2(1f, 0.05f), parent.RectTransform),
                isHorizontal: true)
            {
                RelativeSpacing = 0.01f
            };

            new GUITickBox(
                new RectTransform(new Vector2(showBar ? 0.40f : 0.55f, 1f), row.RectTransform),
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
                new RectTransform(new Vector2(showBar ? 0.10f : 0.15f, 1f), row.RectTransform),
                Localization.T(labelKey),
                textAlignment: Alignment.CenterRight,
                font: GUIStyle.SmallFont);

            new GUINumberInput(
                new RectTransform(new Vector2(showBar ? 0.15f : 0.2f, 1f), row.RectTransform),
                NumberType.Int)
            {
                IntValue = currentValue,
                MinValueInt = minValue,
                MaxValueInt = maxValue,
                OnValueChanged = ni => valueSetter(ni.IntValue)
            };

            if (showBar)
            {
                var (bar, pct) = AppendImpactBar(row, 0.22f, 0.10f, impactFraction);
                _impactBars.Add(new ImpactBarState
                {
                    Bar = bar, PctLabel = pct, FeatureKey = nameKey,
                    CurrentFraction = impactFraction, TargetFraction = impactFraction
                });
            }
        }

        private static void StatLine(GUIComponent parent, string nameKey, float avgValue)
        {
            new GUITextBlock(
                new RectTransform(new Vector2(1f, 0.04f), parent.RectTransform),
                Localization.Format("stats_format", Localization.T(nameKey), avgValue),
                font: GUIStyle.SmallFont);
        }

        /// <summary>
        /// Label + DropDown for multi-level strategy (e.g. signal graph: Off/Accel/Aggressive).
        /// </summary>
        private static void StrategyDropDown(GUIComponent parent, string nameKey, string descKey,
            string[] optionKeys, int currentIndex, Action<int> setter,
            float impactFraction = -1f)
        {
            bool showBar = _showImpactBars && impactFraction >= 0f;

            var row = new GUILayoutGroup(
                new RectTransform(new Vector2(1f, 0.05f), parent.RectTransform),
                isHorizontal: true)
            {
                RelativeSpacing = 0.01f
            };

            new GUITextBlock(
                new RectTransform(new Vector2(showBar ? 0.35f : 0.45f, 1f), row.RectTransform),
                Localization.T(nameKey),
                font: GUIStyle.SmallFont)
            {
                ToolTip = Localization.T(descKey)
            };

            var dropdown = new GUIDropDown(
                new RectTransform(new Vector2(showBar ? 0.30f : 0.45f, 1f), row.RectTransform));

            for (int i = 0; i < optionKeys.Length; i++)
                dropdown.AddItem(Localization.T(optionKeys[i]), i);

            dropdown.SelectItem(currentIndex);
            dropdown.OnSelected = (component, obj) =>
            {
                int val = (int)obj;
                setter(val);
                LuaCsLogger.Log($"[ItemOptimizer] {nameKey} = {val}");
                return true;
            };

            if (showBar)
            {
                var (bar, pct) = AppendImpactBar(row, 0.22f, 0.10f, impactFraction);
                _impactBars.Add(new ImpactBarState
                {
                    Bar = bar, PctLabel = pct, FeatureKey = nameKey,
                    CurrentFraction = impactFraction, TargetFraction = impactFraction
                });
            }
        }

        /// <summary>
        /// Label + DropDown + skip frames text input for sensor mode strategies.
        /// </summary>
        private static void StrategyDropDownWithNumber(GUIComponent parent, string nameKey, string descKey,
            string[] optionKeys, int currentIndex, Action<int> modeSetter,
            int currentSkip, Action<int> skipSetter)
        {
            var row = new GUILayoutGroup(
                new RectTransform(new Vector2(1f, 0.05f), parent.RectTransform),
                isHorizontal: true)
            {
                RelativeSpacing = 0.01f
            };

            new GUITextBlock(
                new RectTransform(new Vector2(0.30f, 1f), row.RectTransform),
                Localization.T(nameKey),
                font: GUIStyle.SmallFont)
            {
                ToolTip = Localization.T(descKey)
            };

            var dropdown = new GUIDropDown(
                new RectTransform(new Vector2(0.25f, 1f), row.RectTransform));

            for (int i = 0; i < optionKeys.Length; i++)
                dropdown.AddItem(Localization.T(optionKeys[i]), i);

            dropdown.SelectItem(currentIndex);
            dropdown.OnSelected = (component, obj) =>
            {
                int val = (int)obj;
                modeSetter(val);
                LuaCsLogger.Log($"[ItemOptimizer] {nameKey} = {val}");
                return true;
            };

            new GUITextBlock(
                new RectTransform(new Vector2(0.15f, 1f), row.RectTransform),
                Localization.T("skip_frames_label"),
                textAlignment: Alignment.CenterRight,
                font: GUIStyle.SmallFont);

            var skipBox = new GUITextBox(
                new RectTransform(new Vector2(0.15f, 1f), row.RectTransform))
            {
                Text = currentSkip.ToString(),
                OverflowClip = true
            };
            skipBox.OnDeselected += (sender, key) =>
            {
                if (int.TryParse(skipBox.Text, out int v))
                {
                    int clamped = Math.Clamp(v, 1, 30);
                    skipSetter(clamped);
                    skipBox.Text = clamped.ToString();
                }
                else
                    skipBox.Text = currentSkip.ToString();
            };
        }

        private static void DevToolButton(GUIComponent parent, string nameKey, string descKey, Action onClick)
        {
            var row = new GUILayoutGroup(
                new RectTransform(new Vector2(1f, 0.05f), parent.RectTransform),
                isHorizontal: true)
            {
                RelativeSpacing = 0.01f
            };

            new GUIButton(
                new RectTransform(new Vector2(0.4f, 1f), row.RectTransform),
                Localization.T(nameKey), Alignment.Center, "GUIButtonSmall")
            {
                ToolTip = Localization.T(descKey),
                OnClicked = (btn, ud) =>
                {
                    onClick();
                    return true;
                }
            };
        }

        /// <summary>
        /// Appends a GUIProgressBar + percentage label to an existing horizontal row.
        /// Returns references for retained-mode animation updates.
        /// </summary>
        private static (GUIProgressBar bar, GUITextBlock pct) AppendImpactBar(
            GUILayoutGroup row, float barWidth, float pctWidth, float fraction)
        {
            fraction = Math.Clamp(fraction, 0f, 1f);

            var barColor = BarColor(fraction);

            var bar = new GUIProgressBar(
                new RectTransform(new Vector2(barWidth, 0.6f), row.RectTransform),
                fraction, barColor);

            string pctText = fraction >= 0.01f ? $"{fraction * 100f:F0}%" : "<1%";
            var pct = new GUITextBlock(
                new RectTransform(new Vector2(pctWidth, 1f), row.RectTransform),
                pctText,
                textAlignment: Alignment.CenterLeft,
                textColor: new Color(180, 180, 180),
                font: GUIStyle.SmallFont);

            return (bar, pct);
        }

        private static Color BarColor(float fraction)
        {
            return fraction > 0.3f ? new Color(80, 220, 80) :
                   fraction > 0.1f ? new Color(120, 200, 80) :
                                      new Color(80, 160, 80);
        }
    }
}
