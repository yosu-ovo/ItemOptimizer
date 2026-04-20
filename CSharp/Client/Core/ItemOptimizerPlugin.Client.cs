using System;
using System.Collections.Generic;
using System.Reflection;
using Barotrauma;
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

            // Register client-side metric receiver for server HUD overlay
            MetricRelayReceiver.Register();

            // Animation LOD — client-only (uses camera position)
            if (OptimizerConfig.EnableAnimLOD)
                AnimLODPatch.Register(harmony);

            // Ladder + platform desync fix — client-only (reconciliation patch)
            // Always register; Prefix/Postfix check EnableLadderFix/EnablePlatformFix at runtime
            LadderFixPatch.Register(harmony);

            // Interaction label optimization — cap ALT labels, runtime-guarded
            InteractionLabelPatch.Register(harmony);

            // Register client-side sync tracking receiver
            SyncRelayReceiver.Register();
        }

        partial void DisposeClient()
        {
            SettingsPanel.Close();
            settingsButton = null;
            StatsOverlay.Visible = false;
            AnimLODPatch.Unregister(harmony);
            LadderFixPatch.Unregister(harmony);
            MetricRelayReceiver.Reset();
            SyncRelayReceiver.Reset();
            SyncTracker.Reset();
        }

        partial void RegisterProxyHandlers()
        {
            // Built-in proxy_light handler migrated to LightNativeComponent (Zone scheduling).
            // External handlers registered via ProxyRegistry.RegisterDynamic still work.
        }

        // ── Harmony Postfixes ──

        private static void TogglePauseMenuPostfix()
        {
            try
            {
                if (GUI.PauseMenuOpen)
                {
                    Localization.Init();

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
                SafeLogger.HandleException(e, LuaCsMessageOrigin.CSharpMod);
            }
        }

        private static void GuiDrawPostfix(SpriteBatch spriteBatch)
        {
            SettingsPanel.TickImpactBars();
            StatsOverlay.Draw(spriteBatch);
            ServerPerfOverlay.Draw(spriteBatch);
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
            foreach (var child in component.Children)
                children.Add(child);
            return children;
        }
    }
}
