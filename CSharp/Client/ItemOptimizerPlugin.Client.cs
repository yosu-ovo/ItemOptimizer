using System;
using System.Collections.Generic;
using System.Reflection;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;
using ItemOptimizerMod.Patches;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ItemOptimizerMod
{
    public sealed partial class ItemOptimizerPlugin
    {
        private static GUIButton settingsButton;

        partial void InitializeClient()
        {
            // Patch: inject button into ESC pause menu
            harmony.Patch(
                AccessTools.Method(typeof(GUI), nameof(GUI.TogglePauseMenu), new Type[] { }),
                postfix: new HarmonyMethod(AccessTools.Method(
                    typeof(ItemOptimizerPlugin), nameof(TogglePauseMenuPostfix))));

            // Patch: draw HUD overlay
            harmony.Patch(
                AccessTools.Method(typeof(GUI), nameof(GUI.Draw),
                    new[] { typeof(Camera), typeof(SpriteBatch) }),
                postfix: new HarmonyMethod(AccessTools.Method(
                    typeof(ItemOptimizerPlugin), nameof(GuiDrawPostfix))));

            // StatusHUD throttle — always patch, runtime-guarded by config flag
            var statusHudUpdate = AccessTools.Method(typeof(StatusHUD), nameof(StatusHUD.Update));
            var statusHudDrawThermal = AccessTools.Method(typeof(StatusHUD), nameof(StatusHUD.DrawThermalOverlay));
            harmony.Patch(statusHudUpdate,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(StatusHUDPatch), nameof(StatusHUDPatch.Prefix))),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(StatusHUDPatch), nameof(StatusHUDPatch.Postfix))));
            harmony.Patch(statusHudDrawThermal,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(StatusHUDPatch), nameof(StatusHUDPatch.DrawThermalPrefix))));
        }

        partial void DisposeClient()
        {
            SettingsPanel.Close();
            settingsButton = null;
            StatsOverlay.Visible = false;
        }

        // ── Harmony Postfixes ──

        private static void TogglePauseMenuPostfix()
        {
            try
            {
                if (GUI.PauseMenuOpen)
                {
                    var buttonList = FindPauseMenuButtonList();
                    if (buttonList == null) return;

                    settingsButton = new GUIButton(
                        new RectTransform(new Vector2(1f, 0.1f), buttonList.RectTransform),
                        Localization.T("btn_settings"), Alignment.Center, "GUIButtonSmall")
                    {
                        OnClicked = (btn, userData) =>
                        {
                            SettingsPanel.Show();
                            return true;
                        }
                    };
                }
                else
                {
                    SettingsPanel.Close();
                    settingsButton = null;
                }
            }
            catch (Exception e)
            {
                LuaCsLogger.HandleException(e, LuaCsMessageOrigin.CSharpMod);
            }
        }

        private static void GuiDrawPostfix(SpriteBatch spriteBatch)
        {
            StatsOverlay.Draw(spriteBatch);
        }

        // ── Pause menu navigation ──

        private static GUIComponent FindPauseMenuButtonList()
        {
            var pauseMenu = GUI.PauseMenu;
            if (pauseMenu == null) return null;

            // Pause menu structure: Frame > [0]=background, [1]=content panel > [0]=button list
            var children = GetDirectChildren(pauseMenu);
            if (children.Count < 2) return null;

            var innerChildren = GetDirectChildren(children[1]);
            return innerChildren.Count > 0 ? innerChildren[0] : null;
        }

        private static List<GUIComponent> GetDirectChildren(GUIComponent component)
        {
            var children = new List<GUIComponent>();
            if (component == null) return children;
            foreach (var child in component.GetAllChildren())
                children.Add(child);
            return children;
        }
    }
}
