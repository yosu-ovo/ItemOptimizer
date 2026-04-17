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
            // Collapsible header
            var headerFrame = new GUIFrame(
                new RectTransform(new Vector2(1f, 0.05f), content.RectTransform),
                style: null);
            var headerRow = new GUILayoutGroup(
                new RectTransform(Vector2.One, headerFrame.RectTransform),
                isHorizontal: true)
            {
                Stretch = true
            };

            new GUIButton(
                new RectTransform(new Vector2(0.05f, 1f), headerRow.RectTransform),
                _rulesExpanded ? "\u25bc" : "\u25b6", Alignment.Center, "GUIButtonSmall")
            {
                OnClicked = (btn, ud) =>
                {
                    _rulesExpanded = !_rulesExpanded;
                    Rebuild();
                    return true;
                }
            };

            new GUITextBlock(
                new RectTransform(new Vector2(0.90f, 1f), headerRow.RectTransform),
                Localization.T("section_item_rules"),
                textColor: Color.Cyan,
                font: GUIStyle.SmallFont);

            if (!_rulesExpanded) return;

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

            // Display name in a framed box
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
            // Display name as read-only textbox (shows standard textbox border)
            var nameBox = new GUITextBox(
                new RectTransform(new Vector2(0.18f, 1f), row.RectTransform))
            {
                Text = displayName,
                Readonly = true,
                OverflowClip = true
            };

            var identBox = new GUITextBox(
                new RectTransform(new Vector2(0.20f, 1f), row.RectTransform))
            {
                Text = rule.Identifier,
                ToolTip = Localization.T("rule_identifier"),
                OverflowClip = true
            };

            // ── Suggestion list (autocomplete overlay, parented to frame to avoid clipping) ──
            int sugLineH = (int)(rowFrame.Rect.Height * 0.85f);
            int sugMaxItems = 5;
            var sugList = new GUIListBox(
                new RectTransform(
                    new Point(identBox.Rect.Width, sugLineH * sugMaxItems),
                    frame.RectTransform,
                    Anchor.TopLeft),
                style: null)
            {
                Visible = false,
                PlaySoundOnSelect = true
            };
            var sugBgColor = new Color(20, 25, 30, 230);
            var sugOutlineColor = new Color(100, 120, 140, 255);
            sugList.Color = sugBgColor;
            sugList.Content.Color = sugBgColor;
            if (sugList.ContentBackground != null)
            {
                sugList.ContentBackground.Color = sugBgColor;
                sugList.ContentBackground.OutlineColor = sugOutlineColor;
            }
            _overlayWidgets.Add(sugList);

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

                foreach (var p in ItemPrefab.Prefabs)
                {
                    if (matches.Count >= sugMaxItems) break;
                    string id = p.Identifier.Value;
                    if (id.StartsWith(filter, StringComparison.OrdinalIgnoreCase))
                        matches.Add((id, p.Name?.Value ?? id));
                }

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

                // Position below the identifier textbox (in frame coordinates)
                var boxRect = identBox.Rect;
                var frameRect = frame.Rect;
                sugList.RectTransform.NonScaledSize = new Point(boxRect.Width, sugLineH * matches.Count);
                sugList.RectTransform.AbsoluteOffset = new Point(
                    boxRect.X - frameRect.X,
                    boxRect.Bottom - frameRect.Y + 4);
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
                    Rebuild();
                }
                return true;
            };

            identBox.OnDeselected += (sender, key) =>
            {
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

            // Skip frames as plain text box (no up/down arrows)
            var skipBox = new GUITextBox(
                new RectTransform(new Vector2(0.10f, 1f), row.RectTransform))
            {
                Text = rule.SkipFrames.ToString(),
                OverflowClip = true
            };
            skipBox.OnDeselected += (sender, key) =>
            {
                if (int.TryParse(skipBox.Text, out int v))
                {
                    int clamped = Math.Clamp(v, 1, 30);
                    rule.SkipFrames = clamped;
                    skipBox.Text = clamped.ToString();
                }
                else
                    skipBox.Text = rule.SkipFrames.ToString();
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
                SkipBox = skipBox,
                ConditionDropDown = condDd,
                SuggestionList = sugList,
                Rule = rule
            });
        }
    }
}
