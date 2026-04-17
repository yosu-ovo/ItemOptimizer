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

            // Get or create the profile for this package
            if (!OptimizerConfig.ModOptProfiles.TryGetValue(mod.Name, out var profile))
            {
                profile = new ModOptProfile();
                OptimizerConfig.ModOptProfiles[mod.Name] = profile;
            }

            profile.TierBases[(int)tier] = skip;

            // If all tiers are 1 (no throttle) and no intensity, remove the entry entirely
            if (profile.TierBases[0] <= 1 && profile.TierBases[1] <= 1
                && profile.TierBases[2] <= 1 && profile.TierBases[3] <= 1
                && profile.Intensity <= 0f)
                OptimizerConfig.ModOptProfiles.Remove(mod.Name);

            OptimizerConfig.BuildModOptLookup();
        }

        private static void ApplyModRecommended(ModInfo mod)
        {
            var profile = new ModOptProfile();
            foreach (ActivityTier tier in Enum.GetValues(typeof(ActivityTier)))
            {
                var tierInfo = mod.Tiers[tier];
                tierInfo.CurrentSkip = tierInfo.RecommendedSkip;
                profile.TierBases[(int)tier] = tierInfo.RecommendedSkip;
            }

            // Only store if at least one tier throttles
            if (profile.TierBases[0] > 1 || profile.TierBases[1] > 1
                || profile.TierBases[2] > 1 || profile.TierBases[3] > 1)
                OptimizerConfig.ModOptProfiles[mod.Name] = profile;
            else
                OptimizerConfig.ModOptProfiles.Remove(mod.Name);

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
            OptimizerConfig.ModOptProfiles.Remove(mod.Name);
            OptimizerConfig.BuildModOptLookup();
            OptimizerConfig.AutoSave();
        }

        private static void ClearAllModRules()
        {
            OptimizerConfig.ModOptProfiles.Clear();
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
            _modPanelRefs.Clear();

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

            // ── Global intensity slider ──
            float avgIntensity = 0f;
            int profileCount = 0;
            foreach (var kvp in OptimizerConfig.ModOptProfiles)
            {
                avgIntensity += kvp.Value.Intensity;
                profileCount++;
            }
            if (profileCount > 0) avgIntensity /= profileCount;

            var globalIntFrame = new GUIFrame(
                new RectTransform(new Vector2(1f, 0.045f), content.RectTransform),
                style: null);
            var globalIntRow = new GUILayoutGroup(
                new RectTransform(Vector2.One, globalIntFrame.RectTransform),
                isHorizontal: true)
            {
                Stretch = true,
                RelativeSpacing = 0.005f
            };

            new GUITextBlock(
                new RectTransform(new Vector2(0.15f, 1f), globalIntRow.RectTransform),
                Localization.T("global_intensity_label"),
                font: GUIStyle.SmallFont,
                textColor: Color.Gold);

            var globalIntSlider = new GUIScrollBar(
                new RectTransform(new Vector2(0.35f, 0.8f), globalIntRow.RectTransform),
                barSize: 0.05f, style: "GUISlider")
            {
                Range = new Vector2(0, 1),
                BarScrollValue = avgIntensity,
                StepValue = 0.05f
            };

            var globalIntPercent = new GUITextBlock(
                new RectTransform(new Vector2(0.08f, 1f), globalIntRow.RectTransform),
                $"{(int)(avgIntensity * 100)}%",
                font: GUIStyle.SmallFont,
                textAlignment: Alignment.Center);

            var globalPreviewProfile = new ModOptProfile { Intensity = avgIntensity };
            var globalIntPreview = new GUITextBlock(
                new RectTransform(new Vector2(0.40f, 1f), globalIntRow.RectTransform),
                FormatIntensityPreview(globalPreviewProfile),
                font: GUIStyle.SmallFont,
                textColor: Color.Gray);

            globalIntSlider.OnMoved = (sb, val) =>
            {
                float intensity = (float)Math.Round(sb.BarScrollValue, 2);
                globalIntPercent.Text = $"{(int)(intensity * 100)}%";
                globalPreviewProfile.Intensity = intensity;
                globalIntPreview.Text = FormatIntensityPreview(globalPreviewProfile);

                foreach (var kvp in OptimizerConfig.ModOptProfiles)
                    kvp.Value.Intensity = intensity;

                foreach (var refs in _modPanelRefs)
                {
                    if (refs.IntensitySlider != null)
                    {
                        refs.IntensitySlider.BarScrollValue = intensity;
                        refs.IntensityPercent.Text = $"{(int)(intensity * 100)}%";
                    }
                    if (OptimizerConfig.ModOptProfiles.TryGetValue(refs.ModName, out var prof))
                    {
                        if (refs.IntensityPreview != null)
                            refs.IntensityPreview.Text = FormatIntensityPreview(prof);
                        foreach (var (tierSlider, tierBox, tierInfo) in refs.TierControls)
                        {
                            int effective = prof.GetEffectiveSkip((int)tierInfo.Tier);
                            tierSlider.BarScrollValue = effective;
                            tierBox.Text = effective.ToString();
                        }
                    }
                }
                OptimizerConfig.BuildModOptLookup();
                return true;
            };

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

            // Expanded: intensity slider + tier rows + detail
            if (!mod.IsExpanded) return;

            // ── Intensity slider ──
            OptimizerConfig.ModOptProfiles.TryGetValue(mod.Name, out var currentProfile);
            float currentIntensity = currentProfile?.Intensity ?? 0f;

            var intensityFrame = new GUIFrame(
                new RectTransform(new Vector2(1f, 0.045f), content.RectTransform),
                style: null);
            var intensityRow = new GUILayoutGroup(
                new RectTransform(Vector2.One, intensityFrame.RectTransform),
                isHorizontal: true)
            {
                Stretch = true,
                RelativeSpacing = 0.005f
            };

            new GUIFrame(
                new RectTransform(new Vector2(0.03f, 1f), intensityRow.RectTransform),
                style: null);

            new GUITextBlock(
                new RectTransform(new Vector2(0.10f, 1f), intensityRow.RectTransform),
                Localization.T("intensity_label"),
                font: GUIStyle.SmallFont,
                textColor: Color.Cyan);

            var intensitySlider = new GUIScrollBar(
                new RectTransform(new Vector2(0.35f, 0.8f), intensityRow.RectTransform),
                barSize: 0.05f, style: "GUISlider")
            {
                Range = new Vector2(0, 1),
                BarScrollValue = currentIntensity,
                StepValue = 0.05f
            };

            var intensityPercent = new GUITextBlock(
                new RectTransform(new Vector2(0.08f, 1f), intensityRow.RectTransform),
                $"{(int)(currentIntensity * 100)}%",
                font: GUIStyle.SmallFont,
                textAlignment: Alignment.Center);

            // Preview text
            var previewProfile = currentProfile ?? new ModOptProfile();
            var previewText = new GUITextBlock(
                new RectTransform(new Vector2(0.40f, 1f), intensityRow.RectTransform),
                FormatIntensityPreview(previewProfile),
                font: GUIStyle.SmallFont,
                textColor: Color.Gray);

            // Tier rows - collect control references
            var tierControls = new List<(GUIScrollBar Slider, GUITextBox NumBox, ModTierInfo TierInfo)>();
            foreach (ActivityTier tier in Enum.GetValues(typeof(ActivityTier)))
            {
                var tierInfo = mod.Tiers[tier];
                if (tierInfo.Items.Count == 0) continue;
                var controls = BuildTierRow(content, capturedMod, tierInfo);
                tierControls.Add(controls);
            }

            // Connect intensity slider to tier controls
            intensitySlider.OnMoved = (sb, val) =>
            {
                float intensity = (float)Math.Round(sb.BarScrollValue, 2);
                intensityPercent.Text = $"{(int)(intensity * 100)}%";

                if (!OptimizerConfig.ModOptProfiles.TryGetValue(capturedMod.Name, out var prof))
                {
                    prof = new ModOptProfile();
                    foreach (ActivityTier t in Enum.GetValues(typeof(ActivityTier)))
                        prof.TierBases[(int)t] = capturedMod.Tiers[t].CurrentSkip;
                    OptimizerConfig.ModOptProfiles[capturedMod.Name] = prof;
                }
                prof.Intensity = intensity;
                previewText.Text = FormatIntensityPreview(prof);

                foreach (var (tierSlider, tierBox, tierInfo) in tierControls)
                {
                    int effective = prof.GetEffectiveSkip((int)tierInfo.Tier);
                    tierSlider.BarScrollValue = effective;
                    tierBox.Text = effective.ToString();
                }

                OptimizerConfig.BuildModOptLookup();
                return true;
            };

            // Store refs for global slider
            _modPanelRefs.Add(new ModPanelRefs
            {
                ModName = mod.Name,
                IntensitySlider = intensitySlider,
                IntensityPercent = intensityPercent,
                IntensityPreview = previewText,
                TierControls = tierControls
            });

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

        private static string FormatIntensityPreview(ModOptProfile profile)
        {
            return Localization.Format("intensity_preview",
                profile.GetEffectiveSkip(0),
                profile.GetEffectiveSkip(1),
                profile.GetEffectiveSkip(2),
                profile.GetEffectiveSkip(3));
        }

        private static (GUIScrollBar Slider, GUITextBox NumBox, ModTierInfo TierInfo) BuildTierRow(GUIComponent content, ModInfo mod, ModTierInfo tierInfo)
        {
            var meta = TierMeta[(int)tierInfo.Tier];
            var capturedMod = mod;
            var capturedTier = tierInfo;
            int configured = CountConfiguredInTier(tierInfo);

            // Determine display value (effective if profile with intensity exists)
            int displaySkip = tierInfo.CurrentSkip;
            if (OptimizerConfig.ModOptProfiles.TryGetValue(mod.Name, out var existingProfile) && existingProfile.Intensity > 0)
                displaySkip = existingProfile.GetEffectiveSkip((int)tierInfo.Tier);

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

            // "间隔:" label
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
                BarScrollValue = displaySkip,
                StepValue = 1
            };

            // Number box (GUITextBox instead of GUINumberInput for consistent layout)
            var numBox = new GUITextBox(
                new RectTransform(new Vector2(0.07f, 1f), tierRow.RectTransform))
            {
                Text = displaySkip.ToString(),
                OverflowClip = true
            };

            // Slider → sets base, shows effective in numBox
            slider.OnMoved = (sb, val) =>
            {
                int v = (int)Math.Round(sb.BarScrollValue);
                capturedTier.CurrentSkip = v;
                if (OptimizerConfig.ModOptProfiles.TryGetValue(capturedMod.Name, out var prof))
                {
                    prof.TierBases[(int)capturedTier.Tier] = v;
                    int effective = prof.GetEffectiveSkip((int)capturedTier.Tier);
                    numBox.Text = effective.ToString();
                }
                else
                {
                    numBox.Text = v.ToString();
                }
                return true;
            };

            // TextBox → sets base on deselect
            numBox.OnDeselected += (sender, key) =>
            {
                if (int.TryParse(numBox.Text, out int v))
                {
                    v = Math.Clamp(v, 1, 15);
                    capturedTier.CurrentSkip = v;
                    if (OptimizerConfig.ModOptProfiles.TryGetValue(capturedMod.Name, out var prof))
                    {
                        prof.TierBases[(int)capturedTier.Tier] = v;
                        int effective = prof.GetEffectiveSkip((int)capturedTier.Tier);
                        numBox.Text = effective.ToString();
                        slider.BarScrollValue = effective;
                    }
                    else
                    {
                        numBox.Text = v.ToString();
                        slider.BarScrollValue = v;
                    }
                }
                else
                {
                    numBox.Text = displaySkip.ToString();
                }
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

            return (slider, numBox, tierInfo);
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
                new RectTransform(new Vector2(0.22f, 1f), itemRow.RectTransform),
                displayName,
                font: GUIStyle.SmallFont,
                textColor: Color.White);

            // Identifier
            new GUITextBlock(
                new RectTransform(new Vector2(0.18f, 1f), itemRow.RectTransform),
                item.Identifier,
                font: GUIStyle.SmallFont,
                textColor: Color.Gray);

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
            bool isWhitelisted = OptimizerConfig.WhitelistLookup.Contains(item.Identifier);
            string ruleText;
            Color ruleColor;
            if (isWhitelisted)
            {
                ruleText = "\u2605 Whitelist";
                ruleColor = Color.Gold;
            }
            else if (hasManualRule)
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
                new RectTransform(new Vector2(0.15f, 1f), itemRow.RectTransform),
                ruleText,
                font: GUIStyle.SmallFont,
                textColor: ruleColor);

            // Add/Remove manual rule button
            var capturedItem = item;
            if (hasManualRule)
            {
                new GUIButton(
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
                new GUIButton(
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
