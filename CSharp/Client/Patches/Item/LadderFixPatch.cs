using System;
using System.Reflection;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;

namespace ItemOptimizerMod.Patches
{
    /// <summary>
    /// Fixes vanilla networking desyncs in character reconciliation:
    ///
    /// Patch A) Ladder climbing state fix — when reconciliation changes SelectedSecondaryItem
    ///   to/from a ladder, ensure AnimController.StartClimbing()/StopClimbing() is called.
    ///   (Climbing animation is NOT directly synced — only SelectedSecondaryItem is.)
    ///
    /// Patch B) Anti-rubber-banding — detect repeated ladder corrections within a short window
    ///   and force-accept the server's state to break the feedback loop.
    ///
    /// Patch C) Platform drop-through desync fix — vanilla Path 2 (controlled character)
    ///   never syncs IgnorePlatforms from server MemState, causing rubber-banding when
    ///   pressing S on stairs/platforms. We sync it in the postfix, mirroring what Path 1
    ///   already does for other players.
    ///
    /// Client-only: only affects client-side reconciliation.
    /// </summary>
    static class LadderFixPatch
    {
        private static MethodInfo _originalMethod;

        // Reflection accessor for Ragdoll.character (protected readonly) — lazy-initialized in Register()
        private static AccessTools.FieldRef<Ragdoll, Character> Ref_character;

        // Anti-rubber-banding state (Patch B)
        private static int _correctionCount;
        private static float _correctionWindowStart;
        private const float CorrectionWindowSec = 2.0f;
        private const int CorrectionThreshold = 3;

        // Track the controlled character's state before reconciliation
        private static Item _preReconcileSecondaryItem;
        private static bool _preReconcileIgnorePlatforms;
        private static bool _serverIgnorePlatforms;
        private static bool _hasServerState;
        private static Character _trackedCharacter;

        public static void Register(Harmony harmony)
        {
            // Initialize field accessor — must succeed for the patch to work
            try
            {
                Ref_character = AccessTools.FieldRefAccess<Ragdoll, Character>("character");
            }
            catch (Exception e)
            {
                LuaCsLogger.LogError($"[LadderFix] Could not access Ragdoll.character field: {e.Message}");
                return;
            }

            // Target the client-side partial method on Ragdoll
            _originalMethod = AccessTools.Method(
                typeof(Ragdoll), "UpdateNetPlayerPositionProjSpecific",
                new[] { typeof(float), typeof(float) });

            if (_originalMethod == null)
            {
                LuaCsLogger.LogError("[LadderFix] Could not find Ragdoll.UpdateNetPlayerPositionProjSpecific");
                return;
            }

            harmony.Patch(_originalMethod,
                prefix: new HarmonyMethod(typeof(LadderFixPatch), nameof(Prefix)),
                postfix: new HarmonyMethod(typeof(LadderFixPatch), nameof(Postfix)));

            LuaCsLogger.Log("[LadderFix] Patch registered");
        }

        public static void Unregister(Harmony harmony)
        {
            if (_originalMethod == null) return;
            harmony.Unpatch(_originalMethod, AccessTools.Method(typeof(LadderFixPatch), nameof(Prefix)));
            harmony.Unpatch(_originalMethod, AccessTools.Method(typeof(LadderFixPatch), nameof(Postfix)));
            _correctionCount = 0;
        }

        /// <summary>
        /// Prefix: capture state before reconciliation.
        /// For controlled character: capture SelectedSecondaryItem (ladder fix) and
        /// server's IgnorePlatforms from MemState (platform fix) before the method clears them.
        /// </summary>
        static void Prefix(Ragdoll __instance)
        {
            if (Ref_character == null) return;
            bool ladderEnabled = OptimizerConfig.EnableLadderFix;
            bool platformEnabled = OptimizerConfig.EnablePlatformFix;
            if (!ladderEnabled && !platformEnabled) return;

            var character = Ref_character(__instance);
            if (character == null) return;

            // Only track the locally controlled character (Path 2 in reconciliation)
            if (character != Character.Controlled) return;

            // Reset state if controlled character changed (death, spectator switch, etc.)
            if (_trackedCharacter != character)
            {
                _trackedCharacter = character;
                _preReconcileSecondaryItem = null;
                _preReconcileIgnorePlatforms = false;
                _correctionCount = 0;
            }

            if (ladderEnabled)
                _preReconcileSecondaryItem = character.SelectedSecondaryItem;

            if (platformEnabled)
            {
                _preReconcileIgnorePlatforms = character.AnimController.IgnorePlatforms;

                // Capture the server's desired IgnorePlatforms from MemState before reconciliation
                // clears it (vanilla Path 2 does character.MemState.Clear() at the end).
                _hasServerState = false;
                var memState = character.MemState;
                if (memState != null && memState.Count > 0)
                {
                    // Path 2 uses MemState.Last() as the authoritative server position
                    _serverIgnorePlatforms = memState[memState.Count - 1].IgnorePlatforms;
                    _hasServerState = true;
                }
            }
        }

        /// <summary>
        /// Postfix: after reconciliation, fix climbing state and platform state mismatches.
        /// </summary>
        static void Postfix(Ragdoll __instance)
        {
            if (Ref_character == null) return;
            bool ladderEnabled = OptimizerConfig.EnableLadderFix;
            bool platformEnabled = OptimizerConfig.EnablePlatformFix;
            if (!ladderEnabled && !platformEnabled) return;

            var character = Ref_character(__instance);
            if (character == null) return;

            // ── Patch A: Fix climbing state for OTHER players ──
            if (character != Character.Controlled)
            {
                if (ladderEnabled)
                    FixRemoteCharacterClimbing(character);
                return;
            }

            // ── Patch A + B: Fix controlled character climbing ──
            if (ladderEnabled)
                FixControlledCharacterClimbing(character);

            // ── Patch C: Fix IgnorePlatforms desync ──
            if (platformEnabled)
                FixControlledCharacterPlatforms(character);
        }

        /// <summary>
        /// For remote characters: if SelectedSecondaryItem is a ladder but not climbing, start.
        /// If SelectedSecondaryItem is null but still climbing, stop.
        /// This is mostly already handled by vanilla's ic.Select() call, but we add a safety net.
        /// </summary>
        private static void FixRemoteCharacterClimbing(Character character)
        {
            var secondaryItem = character.SelectedSecondaryItem;

            if (secondaryItem != null && !secondaryItem.Removed)
            {
                // Check if item has a Ladder component and character should be climbing
                if (!character.AnimController.IsClimbing && HasLadderComponent(secondaryItem))
                {
                    character.AnimController.StartClimbing();
                    Stats.LadderFixCorrections++;
                }
            }
            else
            {
                // No secondary item but still climbing — force stop
                if (character.AnimController.IsClimbing)
                {
                    character.AnimController.StopClimbing();
                    Stats.LadderFixCorrections++;
                }
            }
        }

        /// <summary>
        /// For the controlled character: detect state changes from reconciliation and fix climbing.
        /// Also implements anti-rubber-banding (Patch B).
        /// </summary>
        private static void FixControlledCharacterClimbing(Character character)
        {
            var postItem = character.SelectedSecondaryItem;
            var preItem = _preReconcileSecondaryItem;

            // No change from reconciliation — nothing to fix
            if (preItem == postItem) return;

            float now = (float)Timing.TotalTime;

            // Case 1: Server set SelectedSecondaryItem to a ladder (we weren't on one before)
            if (postItem != null && !postItem.Removed && HasLadderComponent(postItem))
            {
                if (!character.AnimController.IsClimbing)
                {
                    character.AnimController.StartClimbing();
                    Stats.LadderFixCorrections++;
                }

                // ── Patch B: Anti-rubber-banding ──
                // Server keeps putting us back on a ladder we keep trying to leave
                if (now - _correctionWindowStart > CorrectionWindowSec)
                {
                    _correctionWindowStart = now;
                    _correctionCount = 0;
                }
                _correctionCount++;

                if (_correctionCount >= CorrectionThreshold)
                {
                    // Break the loop: accept the server's state and stop fighting it.
                    // We do this by NOT releasing — instead we fully commit to the ladder.
                    // The player can manually release by pressing the interact key.
                    // This prevents the endless snap-back cycle.
                    _correctionCount = 0;
                    _correctionWindowStart = now;

                    // Force re-select to ensure all components are properly initialized
                    foreach (var ic in postItem.Components)
                    {
                        if (ic.CanBeSelected)
                            ic.Select(character);
                    }
                }
            }
            // Case 2: Server cleared SelectedSecondaryItem (we were on a ladder)
            else if (postItem == null || postItem.Removed)
            {
                if (character.AnimController.IsClimbing)
                {
                    character.AnimController.StopClimbing();
                    Stats.LadderFixCorrections++;
                }

                // Reset rubber-banding tracker
                _correctionCount = 0;
            }
        }

        /// <summary>
        /// Patch C: Fix IgnorePlatforms desync for the controlled character.
        ///
        /// Root cause: Vanilla's controlled character reconciliation (Path 2 in
        /// UpdateNetPlayerPositionProjSpecific) syncs SelectedItem, SelectedSecondaryItem,
        /// and Animation from the server, but NEVER syncs IgnorePlatforms — even though
        /// the server sends it in every position update and it's stored in MemState entries.
        /// Compare: Path 1 (other players) does set IgnorePlatforms at line 92.
        ///
        /// Additionally, SetPosition corrections hardcode ignorePlatforms: false (line 284),
        /// which can fight the actual server state when the server says platforms should be ignored.
        ///
        /// Fix: After reconciliation, if we captured a server MemState entry, apply the server's
        /// IgnorePlatforms to the AnimController — mirroring what Path 1 already does.
        /// </summary>
        private static void FixControlledCharacterPlatforms(Character character)
        {
            if (!_hasServerState) return;

            bool clientState = character.AnimController.IgnorePlatforms;

            // If the server says we should be ignoring platforms but the client disagrees
            // (or vice versa), sync it. This is the exact same thing vanilla Path 1 does
            // for other players at line 92: character.AnimController.IgnorePlatforms = memState[0].IgnorePlatforms
            if (clientState != _serverIgnorePlatforms)
            {
                character.AnimController.IgnorePlatforms = _serverIgnorePlatforms;
                Stats.PlatformFixCorrections++;
            }
        }

        private static bool HasLadderComponent(Item item)
        {
            if (item == null || item.Removed) return false;
            foreach (var ic in item.Components)
            {
                if (ic is Ladder) return true;
            }
            return false;
        }
    }
}
