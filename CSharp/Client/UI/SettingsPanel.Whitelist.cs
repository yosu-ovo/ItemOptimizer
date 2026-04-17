using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Microsoft.Xna.Framework;

namespace ItemOptimizerMod
{
    static partial class SettingsPanel
    {
        // ── Whitelist Section Builder ──

        private static void BuildWhitelistSection(GUIComponent content)
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
                _whitelistExpanded ? "\u25bc" : "\u25b6", Alignment.Center, "GUIButtonSmall")
            {
                OnClicked = (btn, ud) =>
                {
                    _whitelistExpanded = !_whitelistExpanded;
                    Rebuild();
                    return true;
                }
            };

            new GUITextBlock(
                new RectTransform(new Vector2(0.90f, 1f), headerRow.RectTransform),
                Localization.T("section_whitelist"),
                textColor: Color.Cyan,
                font: GUIStyle.SmallFont);

            if (!_whitelistExpanded) return;

            new GUITextBlock(
                new RectTransform(new Vector2(1f, 0.04f), content.RectTransform),
                Localization.T("whitelist_desc"),
                font: GUIStyle.SmallFont,
                textColor: Color.LightGray,
                wrap: true);

            Spacer(content);

            // ── Existing whitelist entries ──
            if (OptimizerConfig.Whitelist.Count == 0)
            {
                new GUITextBlock(
                    new RectTransform(new Vector2(1f, 0.04f), content.RectTransform),
                    Localization.T("whitelist_empty"),
                    font: GUIStyle.SmallFont,
                    textColor: new Color(150, 150, 150));
            }
            else
            {
                foreach (var identifier in new List<string>(OptimizerConfig.Whitelist))
                {
                    AddWhitelistRow(content, identifier);
                }
            }

            Spacer(content);

            // ── Add new item row ──
            var addRowFrame = new GUIFrame(
                new RectTransform(new Vector2(1f, 0.06f), content.RectTransform),
                style: null);

            var addRow = new GUILayoutGroup(
                new RectTransform(Vector2.One, addRowFrame.RectTransform),
                isHorizontal: true)
            {
                RelativeSpacing = 0.01f,
                Stretch = true
            };

            var addBox = new GUITextBox(
                new RectTransform(new Vector2(0.6f, 1f), addRow.RectTransform))
            {
                ToolTip = Localization.T("whitelist_add"),
                OverflowClip = true
            };

            // ── Suggestion list (autocomplete overlay, parented to frame to avoid clipping) ──
            int sugLineH = (int)(addRowFrame.Rect.Height * 0.85f);
            int sugMaxItems = 5;
            var sugList = new GUIListBox(
                new RectTransform(
                    new Point(addBox.Rect.Width, sugLineH * sugMaxItems),
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

                // Pass 1: prefix match on identifier
                foreach (var p in ItemPrefab.Prefabs)
                {
                    if (matches.Count >= sugMaxItems) break;
                    string id = p.Identifier.Value;
                    if (id.StartsWith(filter, StringComparison.OrdinalIgnoreCase))
                        matches.Add((id, p.Name?.Value ?? id));
                }

                // Pass 2: contains match on identifier
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

                // Position below the add textbox (in frame coordinates)
                var boxRect = addBox.Rect;
                var frameRect = frame.Rect;
                sugList.RectTransform.NonScaledSize = new Point(boxRect.Width, sugLineH * matches.Count);
                sugList.RectTransform.AbsoluteOffset = new Point(
                    boxRect.X - frameRect.X,
                    boxRect.Bottom - frameRect.Y + 4);
            }

            addBox.OnTextChanged += (tb, text) =>
            {
                UpdateSuggestions(text ?? "");
                return true;
            };

            sugList.OnSelected += (component, userData) =>
            {
                if (userData is string selectedId)
                {
                    addBox.Text = selectedId;
                    sugList.Visible = false;
                }
                return true;
            };

            addBox.OnDeselected += (sender, key) =>
            {
                CoroutineManager.Invoke(() => { sugList.Visible = false; }, delay: 0.15f);
            };

            addBox.OnSelected += (sender, key) =>
            {
                if (!string.IsNullOrWhiteSpace(addBox.Text))
                    UpdateSuggestions(addBox.Text);
            };

            new GUIButton(
                new RectTransform(new Vector2(0.2f, 1f), addRow.RectTransform),
                Localization.T("whitelist_add_btn"), Alignment.Center, "GUIButtonSmall")
            {
                OnClicked = (btn, ud) =>
                {
                    string id = addBox.Text?.Trim();
                    if (!string.IsNullOrEmpty(id) && !OptimizerConfig.Whitelist.Contains(id))
                    {
                        OptimizerConfig.Whitelist.Add(id);
                        OptimizerConfig.RebuildWhitelistLookup();
                        addBox.Text = "";
                        Rebuild();
                    }
                    return true;
                }
            };
        }

        private static void AddWhitelistRow(GUIComponent parent, string identifier)
        {
            var rowFrame = new GUIFrame(
                new RectTransform(new Vector2(1f, 0.05f), parent.RectTransform),
                style: null);

            var row = new GUILayoutGroup(
                new RectTransform(Vector2.One, rowFrame.RectTransform),
                isHorizontal: true)
            {
                RelativeSpacing = 0.01f,
                Stretch = true
            };

            // Resolve display name
            string displayName = identifier;
            foreach (var p in ItemPrefab.Prefabs)
            {
                if (p.Identifier.Value == identifier)
                {
                    displayName = p.Name?.Value ?? identifier;
                    break;
                }
            }

            new GUITextBlock(
                new RectTransform(new Vector2(0.35f, 1f), row.RectTransform),
                displayName,
                font: GUIStyle.SmallFont,
                textColor: Color.White);

            new GUITextBlock(
                new RectTransform(new Vector2(0.35f, 1f), row.RectTransform),
                identifier,
                font: GUIStyle.SmallFont,
                textColor: Color.Gray);

            // Whitelist indicator
            new GUITextBlock(
                new RectTransform(new Vector2(0.15f, 1f), row.RectTransform),
                "\u2605",  // ★ star
                font: GUIStyle.SmallFont,
                textColor: Color.Gold,
                textAlignment: Alignment.Center);

            new GUIButton(
                new RectTransform(new Vector2(0.10f, 1f), row.RectTransform),
                Localization.T("whitelist_remove"), Alignment.Center, "GUIButtonSmall")
            {
                OnClicked = (btn, ud) =>
                {
                    OptimizerConfig.Whitelist.Remove(identifier);
                    OptimizerConfig.RebuildWhitelistLookup();
                    Rebuild();
                    return true;
                }
            };
        }
    }
}
