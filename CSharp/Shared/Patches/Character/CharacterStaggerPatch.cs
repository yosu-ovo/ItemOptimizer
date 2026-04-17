using System.Reflection;
using Barotrauma;
using HarmonyLib;

namespace ItemOptimizerMod.Patches
{
    /// <summary>
    /// Staggers enemy AI updates across frames to spread CPU load.
    ///
    /// Hooks EnemyAIController.Update — the expensive AI decision-making.
    /// Characters are divided into N groups by ID; each frame only one group
    /// runs the full AI update. The others skip AI but still run Character.Update
    /// (health, status effects, oxygen) and physics/animation normally.
    ///
    /// HumanAIController (NPC crew) is NOT hooked — only enemy AI.
    /// </summary>
    static class CharacterStaggerPatch
    {
        private static int _frameCounter;
        private static MethodInfo _originalMethod;

        public static void Register(Harmony harmony)
        {
            _originalMethod = AccessTools.Method(typeof(EnemyAIController), "Update",
                new[] { typeof(float) });

            if (_originalMethod == null)
            {
                LuaCsLogger.LogError("[CharStagger] Could not find EnemyAIController.Update");
                return;
            }

            harmony.Patch(_originalMethod,
                prefix: new HarmonyMethod(typeof(CharacterStaggerPatch), nameof(Prefix)));

            LuaCsLogger.Log("[CharStagger] Patch registered");
        }

        public static void Unregister(Harmony harmony)
        {
            if (_originalMethod != null)
                harmony.Unpatch(_originalMethod,
                    AccessTools.Method(typeof(CharacterStaggerPatch), nameof(Prefix)));
        }

        public static void IncrementFrame()
        {
            _frameCounter++;
        }

        /// <summary>
        /// Prefix on EnemyAIController.Update.
        /// Returns false to skip AI computation on off-frames.
        /// Character.Update (health/status/oxygen) and physics/animation are unaffected.
        /// </summary>
        static bool Prefix(EnemyAIController __instance)
        {
            if (!OptimizerConfig.EnableCharacterStagger)
                return true;

            var character = __instance.Character;
            if (character == null || !character.Enabled)
                return true;

            // Never stagger the controlled character
            if (character == Character.Controlled)
                return true;

            int groups = OptimizerConfig.CharacterStaggerGroups;
            int myGroup = (int)((uint)character.ID % (uint)groups);

            if (_frameCounter % groups != myGroup)
            {
                // Off-frame: skip AI decision-making.
                // Character still moves (physics), plays animations, takes damage, etc.
                // AI just doesn't re-evaluate targets or change steering this frame.
                Stats.CharStaggerSkipped++;
                return false;
            }

            return true;
        }
    }
}
