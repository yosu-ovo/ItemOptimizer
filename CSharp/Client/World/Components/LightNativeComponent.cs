using System;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;

namespace ItemOptimizerMod.World.Components
{
    /// <summary>
    /// NativeComponent for proxy_light items — decorative breathing light effect.
    /// Migrated from ProxyLightHandler to run under Zone scheduling.
    ///
    /// ShouldTick LOD:
    ///   Active/Nearby: every frame (smooth animation)
    ///   Passive: half frequency (still visually acceptable)
    ///   Dormant: skip (not visible)
    ///
    /// Tick is pure math (sin/HSV). LightSource mutation goes through DeferToMainThread.
    /// </summary>
    internal sealed class LightNativeComponent : NativeComponent
    {
        private readonly LightComponent _light;
        private float _timer;
        private Color _pendingColor;
        private readonly Action _applyColorAction;

        public LightNativeComponent(LightComponent light, Item host)
        {
            _light = light;
            Host = host;
            // Cache the delegate once — reads _pendingColor from field, zero per-tick allocation
            _applyColorAction = ApplyColor;
        }

        private void ApplyColor()
        {
            if (_light == null) return;
            _light.LightColor = _pendingColor;
            var ls = _light.Light;
            if (ls != null)
            {
                ls.Color = _pendingColor;
                ls.Enabled = true;
            }
        }

        public override bool ShouldTick(ZoneTier tier, uint frame)
        {
            if (tier >= ZoneTier.Dormant) return false;
            // Passive: half frequency, staggered by item ID
            if (tier >= ZoneTier.Passive) return ((frame + (uint)Host.ID) & 1) == 0;
            return true;
        }

        public override void Tick(ref TickContext ctx)
        {
            _timer += ctx.DeltaTime;

            // Breathing brightness: 0.3 ~ 1.0
            float brightness = 0.65f + 0.35f * MathF.Sin(_timer * 2f);

            // Hue rotation: full cycle every 10 seconds
            float hue = (_timer * 0.1f) % 1f;
            _pendingColor = HsvToRgb(hue, 0.6f, brightness);

            // LightSource must be modified on main thread — use cached delegate (zero alloc)
            ctx.DeferToMainThread(_applyColorAction);
        }

        public override void OnRegistered()
        {
            if (_light != null) _light.IsOn = true;
        }

        /// <summary>HSV to RGB. H/S/V in [0,1].</summary>
        private static Color HsvToRgb(float h, float s, float v)
        {
            float r, g, b;

            if (s <= 0f)
            {
                r = g = b = v;
            }
            else
            {
                float hSector = h * 6f;
                if (hSector >= 6f) hSector = 0f;
                int sector = (int)hSector;
                float frac = hSector - sector;

                float p = v * (1f - s);
                float q = v * (1f - s * frac);
                float t = v * (1f - s * (1f - frac));

                switch (sector)
                {
                    case 0:  r = v; g = t; b = p; break;
                    case 1:  r = q; g = v; b = p; break;
                    case 2:  r = p; g = v; b = t; break;
                    case 3:  r = p; g = q; b = v; break;
                    case 4:  r = t; g = p; b = v; break;
                    default: r = v; g = p; b = q; break;
                }
            }

            return new Color(r, g, b, 1f);
        }
    }
}
