using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;

namespace ItemOptimizerMod.Proxy
{
    /// <summary>
    /// Proxy handler for proxy_light items.
    /// BatchCompute: calculates breathing brightness + color hue rotation (worker-safe).
    /// SyncBack: pushes results to LightComponent/LightSource (main thread).
    /// </summary>
    public class ProxyLightHandler : IProxyHandler
    {
        // ── Per-item state (SOA layout for cache efficiency) ──
        private readonly List<Item> _items = new();
        private readonly List<float> _timers = new();

        // ── Computed outputs (written by BatchCompute, read by SyncBack) ──
        private Color[] _outColors = Array.Empty<Color>();

        // ── Cached references for SyncBack (avoid GetComponent per frame) ──
        private readonly List<LightComponent> _lights = new();

        public int Count => _items.Count;
        public ProxySkipLevel SkipLevel => ProxySkipLevel.Lightweight;

        public void Attach(Item item)
        {
            if (_items.Contains(item)) return;

            var light = item.GetComponent<LightComponent>();
            if (light == null)
            {
                LuaCsLogger.LogError($"[ItemOptimizer:Proxy] proxy_light '{item.Name}' has no LightComponent");
                return;
            }

            _items.Add(item);
            _timers.Add(0f);
            _lights.Add(light);
            EnsureOutputCapacity();

            light.IsOn = true;
        }

        public void Detach(Item item)
        {
            int idx = _items.IndexOf(item);
            if (idx < 0) return;

            _items.RemoveAt(idx);
            _timers.RemoveAt(idx);
            _lights.RemoveAt(idx);
        }

        /// <summary>
        /// Compute phase — safe for worker threads.
        /// Reads item positions (approximate, may have torn Vector2 but cosmetically harmless).
        /// Writes only to handler-local arrays.
        /// </summary>
        public void BatchCompute(float deltaTime)
        {
            int count = _items.Count;
            if (count == 0) return;

            for (int i = 0; i < count; i++)
            {
                float t = _timers[i] + deltaTime;
                _timers[i] = t;

                // Breathing brightness: 0.3 ~ 1.0
                float brightness = 0.65f + 0.35f * MathF.Sin(t * 2f);

                // Hue rotation: full cycle every 10 seconds
                float hue = (t * 0.1f) % 1f;
                _outColors[i] = HsvToRgb(hue, 0.6f, brightness);
            }
        }

        /// <summary>
        /// Sync phase — main thread only.
        /// Pushes computed color to LightComponent and LightSource directly.
        /// We set ls.Color directly because LightComponent.Update is skipped (Lightweight mode).
        /// </summary>
        public void SyncBack()
        {
            int count = _items.Count;
            if (count == 0) return;

            for (int i = 0; i < count; i++)
            {
                var item = _items[i];
                if (item.Removed) continue;

                var light = _lights[i];
                if (light == null) continue;

                light.LightColor = _outColors[i];

#if CLIENT
                var ls = light.Light;
                if (ls != null)
                {
                    ls.Color = _outColors[i];
                    ls.Enabled = true;
                }
#endif
            }
        }

        private void EnsureOutputCapacity()
        {
            int needed = _items.Count;
            if (_outColors.Length < needed)
            {
                int newSize = Math.Max(needed, _outColors.Length * 2);
                newSize = Math.Max(newSize, 8);
                Array.Resize(ref _outColors, newSize);
            }
        }

        /// <summary>HSV to RGB conversion. H in [0,1], S in [0,1], V in [0,1].</summary>
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
