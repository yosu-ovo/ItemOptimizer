using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Microsoft.Xna.Framework;

namespace ItemOptimizerMod
{
    static partial class SettingsPanel
    {
        // ── Activity Tier System ──

        private enum ActivityTier { Critical, Active, Moderate, Static }

        private static readonly (Color Color, string LabelKey, int RecommendedSkip)[] TierMeta =
        {
            (new Color(220, 50, 50),  "tier_critical", 1),  // Critical
            (new Color(255, 165, 0),  "tier_active",   3),  // Active
            (new Color(220, 220, 50), "tier_moderate", 5),  // Moderate
            (new Color(50, 200, 50),  "tier_static",   8),  // Static
        };

        private class ModTierInfo
        {
            public ActivityTier Tier;
            public List<ModItemInfo> Items = new();
            public int RecommendedSkip;
            public int CurrentSkip;
        }

        // ── Mod Data Model ──

        private class ModInfo
        {
            public ContentPackage Package;
            public string Name;
            public List<ModItemInfo> Items = new();
            public bool IsExpanded;
            public bool IsDetailExpanded;
            public Dictionary<ActivityTier, ModTierInfo> Tiers = new();
        }

        private class ModItemInfo
        {
            public ItemPrefab Prefab;
            public string Identifier;
            public List<string> DetectedPatterns = new();
            public int StatusEffectCount;
            public ActivityTier Tier;
        }

        private static List<ModInfo> _cachedMods;

        private static ActivityTier ClassifyItem(ModItemInfo item)
        {
            bool hasStatusHUD   = item.DetectedPatterns.Contains("StatusHUD");
            bool hasMultiSE     = item.DetectedPatterns.Contains("MultiSE");
            bool hasAffliction  = item.DetectedPatterns.Contains("Affliction");
            bool hasConditional = item.DetectedPatterns.Contains("Conditional");

            if (hasStatusHUD) return ActivityTier.Critical;
            if (hasMultiSE || (hasAffliction && hasConditional)) return ActivityTier.Active;
            if (hasAffliction || hasConditional || item.StatusEffectCount > 2) return ActivityTier.Moderate;
            return ActivityTier.Static;
        }

        private static List<ModInfo> ScanMods()
        {
            if (_cachedMods != null) return _cachedMods;

            var modMap = new Dictionary<ContentPackage, ModInfo>();

            foreach (ItemPrefab prefab in ItemPrefab.Prefabs)
            {
                var pkg = prefab.ContentPackage;
                if (pkg == null) continue;
                if (pkg == ContentPackageManager.VanillaCorePackage) continue;

                if (!modMap.TryGetValue(pkg, out var modInfo))
                {
                    modInfo = new ModInfo { Package = pkg, Name = pkg.Name };
                    modMap[pkg] = modInfo;
                }

                var itemInfo = new ModItemInfo
                {
                    Prefab = prefab,
                    Identifier = prefab.Identifier.Value
                };

                // Detect expensive patterns by scanning the XML ConfigElement
                var configEl = prefab.ConfigElement;
                if (configEl != null)
                {
                    int statusEffectCount = 0;

                    foreach (var compEl in configEl.Elements())
                    {
                        var compName = compEl.Name.ToString();

                        if (compName.Equals("StatusHUD", StringComparison.OrdinalIgnoreCase))
                            itemInfo.DetectedPatterns.Add("StatusHUD");

                        foreach (var subEl in compEl.Elements())
                        {
                            var subName = subEl.Name.ToString();

                            if (subName.Equals("statuseffect", StringComparison.OrdinalIgnoreCase))
                            {
                                statusEffectCount++;
                                var typeAttr = subEl.GetAttributeString("type", "OnActive");
                                bool isOnActiveOrAlways = typeAttr.Equals("OnActive", StringComparison.OrdinalIgnoreCase)
                                    || typeAttr.Equals("Always", StringComparison.OrdinalIgnoreCase);

                                if (isOnActiveOrAlways)
                                {
                                    foreach (var seChild in subEl.Elements())
                                    {
                                        if (seChild.Name.ToString().Equals("Affliction", StringComparison.OrdinalIgnoreCase))
                                        {
                                            if (!itemInfo.DetectedPatterns.Contains("Affliction"))
                                                itemInfo.DetectedPatterns.Add("Affliction");
                                            break;
                                        }
                                    }
                                }
                            }
                            else if (subName.Equals("activeconditional", StringComparison.OrdinalIgnoreCase)
                                  || subName.Equals("isactiveconditional", StringComparison.OrdinalIgnoreCase)
                                  || subName.Equals("isactive", StringComparison.OrdinalIgnoreCase))
                            {
                                if (!itemInfo.DetectedPatterns.Contains("Conditional"))
                                    itemInfo.DetectedPatterns.Add("Conditional");
                            }
                        }
                    }

                    if (statusEffectCount > 5 && !itemInfo.DetectedPatterns.Contains("MultiSE"))
                        itemInfo.DetectedPatterns.Add("MultiSE");

                    itemInfo.StatusEffectCount = statusEffectCount;
                }

                itemInfo.Tier = ClassifyItem(itemInfo);
                modInfo.Items.Add(itemInfo);
            }

            // Build tier aggregation for each mod
            foreach (var mod in modMap.Values)
            {
                // Check if we have saved settings for this mod
                OptimizerConfig.ModOptSettings.TryGetValue(mod.Name, out var savedSkips);

                foreach (ActivityTier tier in Enum.GetValues(typeof(ActivityTier)))
                {
                    var meta = TierMeta[(int)tier];
                    int currentSkip = savedSkips != null ? savedSkips[(int)tier] : meta.RecommendedSkip;
                    mod.Tiers[tier] = new ModTierInfo
                    {
                        Tier = tier,
                        RecommendedSkip = meta.RecommendedSkip,
                        CurrentSkip = currentSkip,
                        Items = new List<ModItemInfo>()
                    };
                }
                foreach (var item in mod.Items)
                    mod.Tiers[item.Tier].Items.Add(item);
            }

            _cachedMods = modMap.Values
                .Where(m => m.Items.Count > 0)
                .OrderByDescending(m => m.Items.Count)
                .ToList();

            return _cachedMods;
        }
    }
}
