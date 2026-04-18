using Barotrauma;
using Barotrauma.Items.Components;
using ItemOptimizerMod.World.Components;

namespace ItemOptimizerMod.World
{
    /// <summary>
    /// Client-side partial: registers visual NativeComponents (LightNativeComponent etc.)
    /// into the Zone system during OnRoundStart.
    /// </summary>
    internal static partial class NativeRuntimeBridge
    {
        static partial void RegisterClientComponents()
        {
            if (Runtime == null) return;

            int lightCount = 0;
            foreach (var item in Item.ItemList)
            {
                if (item == null || item.Removed) continue;
                if (item.Prefab?.Identifier.Value != "proxy_light") continue;

                var light = item.GetComponent<LightComponent>();
                if (light == null) continue;

                // Match zone: by submarine > by spatial position > skip
                Zone targetZone = null;
                if (item.Submarine != null)
                {
                    foreach (var zone in Runtime.Graph.Zones)
                    {
                        if (zone is SubmarineZone sz && sz.Submarine == item.Submarine)
                        {
                            targetZone = sz;
                            break;
                        }
                    }
                }
                targetZone ??= Runtime.Graph.FindZoneAt(item.WorldPosition);

                if (targetZone == null) continue;

                var native = new LightNativeComponent(light, item);
                Runtime.Register(native, targetZone);
                IsZoneManaged[item.ID] = true;
                lightCount++;
            }

            if (lightCount > 0)
                LuaCsLogger.Log($"[ItemOptimizer] NativeRuntime: {lightCount} proxy_light items registered as LightNativeComponent");
        }
    }
}
