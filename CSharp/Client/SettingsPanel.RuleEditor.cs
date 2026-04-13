using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Microsoft.Xna.Framework;

namespace ItemOptimizerMod
{
    static partial class SettingsPanel
    {
        // ── Item Rules Section Builder ──

        private static void BuildItemRulesSection(GUIComponent content)
        {
            SectionHeader(content, Localization.T("section_item_rules"));

            ruleRows.Clear();
            foreach (var rule in OptimizerConfig.ItemRules)
                AddRuleRow(content, rule);

            new GUIButton(
                new RectTransform(new Vector2(0.4f, 0.05f), content.RectTransform),
                Localization.T("rule_add"), Alignment.Center, "GUIButtonSmall")
            {
                OnClicked = (btn, ud) =>
                {
                    OptimizerConfig.ItemRules.Add(new ItemRule());
                    Rebuild();
                    return true;
                }
            };
        }

        // ── Rule Row with Autocomplete ──

        private static void AddRuleRow(GUIComponent parent, ItemRule rule)
        {
            var rowFrame = new GUIFrame(
                new RectTransform(new Vector2(1f, 0.07f), parent.RectTransform),
                style: null);

            var row = new GUILayoutGroup(
                new RectTransform(Vector2.One, rowFrame.RectTransform),
                isHorizontal: true)
            {
                RelativeSpacing = 0.01f,
                Stretch = true
            };

            // Display name (resolved from prefab)
            string displayName = "-";
            if (!string.IsNullOrWhiteSpace(rule.Identifier))
            {
                foreach (var p in ItemPrefab.Prefabs)
                {
                    if (p.Identifier.Value == rule.Identifier)
                    {
                        displayName = p.Name?.Value ?? rule.Identifier;
                        break;
                    }
                }
            }
            new GUITextBlock(
                new RectTransform(new Vector2(0.18f, 1f), row.RectTransform),
                displayName,
                font: GUIStyle.SmallFont,
                textColor: Color.LightGray);

            var identBox = new GUITextBox(
                new RectTransform(new Vector2(0.20f, 1f), row.RectTransform))
            {
                Text = rule.Identifier,
                ToolTip = Localization.T("rule_identifier"),
                OverflowClip = true
            };

            // ── Suggestion list (autocomplete overlay) ──
            // Parent to rowFrame so it overlays below the row, outside the layout group
            int sugLineH = (int)(rowFrame.Rect.Height * 0.85f);
            int sugMaxItems = 5;
            var sugList = new GUIListBox(
                new RectTransform(
                    new Point(identBox.Rect.Width, sugLineH * sugMaxItems),
                    rowFrame.RectTransform,
                    Anchor.BottomLeft)
                {
                    // Position: align left edge with identBox, push below the row
                    RelativeOffset = new Vector2(0.18f, 0f),  // skip the display name column (0.18)
                    AbsoluteOffset = new Point(0, 4),          // small gap below the row
                },
                style: null)
            {
                Visible = false,
                PlaySoundOnSelect = true
            };
            // Semi-transparent background + border for contrast without blocking text behind
            var sugBgColor = new Color(20, 25, 30, 180);
            var sugOutlineColor = new Color(80, 90, 100, 200);
            sugList.Color = sugBgColor;
            sugList.Content.Color = sugBgColor;
            if (sugList.ContentBackground != null)
            {
                sugList.ContentBackground.Color = sugBgColor;
                sugList.ContentBackground.OutlineColor = sugOutlineColor;
            }

            // Match and populate suggestions
            void UpdateSuggestions(string text)
            {
                sugList.Content.ClearChildren();

                if (string.IsNullOrWhiteSpace(text))
                {
                    sugList.Visible = false;
                    return;
                }

                string filter = text.Trim();
                var matches = new List<(string id, string name)>();

                // Pass 1: prefix match on identifier
                foreach (var p in ItemPrefab.Prefabs)
                {
                    if (matches.Count >= sugMaxItems) break;
                    string id = p.Identifier.Value;
                    if (id.StartsWith(filter, StringComparison.OrdinalIgnoreCase))
                        matches.Add((id, p.Name?.Value ?? id));
                }

                // Pass 2: contains match on identifier (exclude already found)
                if (matches.Count < sugMaxItems)
                {
                    var found = new HashSet<string>(matches.Select(m => m.id));
                    foreach (var p in ItemPrefab.Prefabs)
                    {
                        if (matches.Count >= sugMaxItems) break;
                        string id = p.Identifier.Value;
                        if (!found.Contains(id) && id.Contains(filter, StringComparison.OrdinalIgnoreCase))
                            matches.Add((id, p.Name?.Value ?? id));
                    }
                }

                // Pass 3: contains match on display name
                if (matches.Count < sugMaxItems)
                {
                    var found = new HashSet<string>(matches.Select(m => m.id));
                    foreach (var p in ItemPrefab.Prefabs)
                    {
                        if (matches.Count >= sugMaxItems) break;
                        string id = p.Identifier.Value;
                        string name = p.Name?.Value ?? "";
                        if (!found.Contains(id) && name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                            matches.Add((id, name));
                    }
                }

                if (matches.Count == 0)
                {
                    sugList.Visible = false;
                    return;
                }

                foreach (var (id, name) in matches)
                {
                    var itemFrame = new GUIFrame(
                        new RectTransform(new Vector2(1f, 1f / sugMaxItems), sugList.Content.RectTransform),
                        style: null)
                    {
                        UserData = id,
                        Color = Color.Transparent,
                        HoverColor = new Color(60, 80, 100, 200),
                        SelectedColor = new Color(80, 100, 120, 200),
                        PressedColor = new Color(80, 100, 120, 200)
                    };
                    new GUITextBlock(
                        new RectTransform(Vector2.One, itemFrame.RectTransform),
                        $"{id}  ({name})",
                        font: GUIStyle.SmallFont,
                        textColor: Color.LightGray)
                    {
                        OverflowClip = true
                    };
                }

                sugList.Visible = true;
            }

            identBox.OnTextChanged += (tb, text) =>
            {
                rule.Identifier = text ?? "";
                UpdateSuggestions(text ?? "");
                return true;
            };

            sugList.OnSelected += (component, userData) =>
            {
                if (userData is string selectedId)
                {
                    rule.Identifier = selectedId;
                    identBox.Text = selectedId;
                    sugList.Visible = false;
                    // Rebuild to update display name
                    Rebuild();
                }
                return true;
            };

            identBox.OnDeselected += (sender, key) =>
            {
                // Delay hide so click on suggestion can register first
                CoroutineManager.Invoke(() => { sugList.Visible = false; }, delay: 0.15f);
            };

            identBox.OnSelected += (sender, key) =>
            {
                if (!string.IsNullOrWhiteSpace(identBox.Text))
                    UpdateSuggestions(identBox.Text);
            };

            var actionDd = new GUIDropDown(
                new RectTransform(new Vector2(0.18f, 1f), row.RectTransform));
            actionDd.AddItem(Localization.T("action_skip"), ItemRuleAction.Skip);
            actionDd.AddItem(Localization.T("action_throttle"), ItemRuleAction.Throttle);
            actionDd.SelectItem(rule.Action);
            actionDd.OnSelected += (component, obj) =>
            {
                if (obj is ItemRuleAction action)
                    rule.Action = action;
                return true;
            };

            var skipInput = new GUINumberInput(
                new RectTransform(new Vector2(0.10f, 1f), row.RectTransform),
                NumberType.Int)
            {
                IntValue = rule.SkipFrames,
                MinValueInt = 1,
                MaxValueInt = 30,
                OnValueChanged = ni => rule.SkipFrames = ni.IntValue
            };

            var condDd = new GUIDropDown(
                new RectTransform(new Vector2(0.22f, 1f), row.RectTransform));
            condDd.AddItem(Localization.T("cond_always"), "always");
            condDd.AddItem(Localization.T("cond_cold_storage"), "coldStorage");
            condDd.AddItem(Localization.T("cond_not_active_use"), "notInActiveUse");
            condDd.SelectItem(rule.Condition);
            condDd.OnSelected += (component, obj) =>
            {
                if (obj is string cond)
                    rule.Condition = cond;
                return true;
            };

            new GUIButton(
                new RectTransform(new Vector2(0.08f, 1f), row.RectTransform),
                Localization.T("rule_remove"), Alignment.Center, "GUIButtonSmall")
            {
                OnClicked = (btn, ud) =>
                {
                    OptimizerConfig.ItemRules.Remove(rule);
                    Rebuild();
                    return true;
                }
            };

            ruleRows.Add(new RuleRow
            {
                Container = rowFrame,
                IdentifierBox = identBox,
                ActionDropDown = actionDd,
                SkipInput = skipInput,
                ConditionDropDown = condDd,
                SuggestionList = sugList,
                Rule = rule
            });
        }
    }
}
