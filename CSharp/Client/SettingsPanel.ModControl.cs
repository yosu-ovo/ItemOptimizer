using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Microsoft.Xna.Framework;

namespace ItemOptimizerMod
{
    static partial class SettingsPanel
    {
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
            OptimizerConfig.AutoSave();
        }

        private static void ClearAllModRules()
        {
            OptimizerConfig.ModOptSettings.Clear();
            OptimizerConfig.BuildModOptLookup();
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

        // ── Mod Control Section Builder ──

        private static void BuildModControlSection(GUIComponent content)
        {
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
                    new GUIProgressBar(
                        new RectTransform(new Vector2(Math.Max(fraction, 0.05f), 1f), barContainer.RectTransform),
                        1.0f, TierMeta[(int)tier].Color);
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
                        OptimizerConfig.AutoSave();
                        Rebuild();
                        return true;
                    }
                };
            }
        }
    }
}
