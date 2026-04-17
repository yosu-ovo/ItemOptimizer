using Barotrauma;

namespace ItemOptimizerMod.World
{
    /// <summary>
    /// Base class for dual-runtime components. A NativeComponent can run on:
    ///   - Vanilla runtime (DirectContext: commands apply immediately on main thread)
    ///   - Native runtime (BufferedContext: commands buffer, parallel tick, flush on main thread)
    ///
    /// Mod authors inherit this instead of ItemComponent to get automatic parallelization.
    /// Existing vanilla ItemComponents continue to work — they just don't get parallel benefits.
    ///
    /// Rules for Tick():
    ///   ALLOWED: read own fields, read ctx.Snapshot, emit commands via ctx.Emit*()
    ///   FORBIDDEN: call other entities' methods, modify world state directly
    /// </summary>
    public abstract class NativeComponent
    {
        /// <summary>The Item this component belongs to. Set by runtime at registration.</summary>
        public Item Host { get; internal set; }

        /// <summary>Current zone. Set by runtime, updated when entity moves between zones.</summary>
        public Zone Zone { get; internal set; }

        /// <summary>
        /// Optional read phase — gather data from the world snapshot before parallel tick.
        /// Called on the main thread before Tick(). Most simple components can skip this.
        /// Use this when you need to query spatial data (nearby entities, hull states).
        /// </summary>
        public virtual void Read(in ReadContext ctx) { }

        /// <summary>
        /// Main tick — read own state + snapshot, produce commands.
        /// May run on a worker thread (Native runtime) or main thread (Vanilla runtime).
        /// Do NOT access other entities directly. Use ctx.Snapshot for reads, ctx.Emit*() for writes.
        /// </summary>
        public abstract void Tick(ref TickContext ctx);

        /// <summary>
        /// LOD gate — runtime calls this before Tick to decide if this component
        /// should tick this frame. Return false to skip (e.g., every Nth frame for Passive tier).
        /// Default: always tick.
        /// </summary>
        public virtual bool ShouldTick(ZoneTier tier, uint frame) => true;

        /// <summary>Called when this component's zone changes tier (e.g., Active → Passive).</summary>
        public virtual void OnTierChanged(ZoneTier oldTier, ZoneTier newTier) { }

        /// <summary>Called when registered with the runtime.</summary>
        public virtual void OnRegistered() { }

        /// <summary>Called when unregistered from the runtime.</summary>
        public virtual void OnUnregistered() { }
    }
}
