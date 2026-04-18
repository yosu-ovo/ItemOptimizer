using System;
using System.Collections.Generic;
using System.Text;
using Barotrauma;
using ItemOptimizerMod.Patches;
using ItemOptimizerMod.SignalGraph;

namespace ItemOptimizerMod
{
    /// <summary>
    /// Generates a self-contained diagnostic header for io_record CSV files.
    /// All lines are prefixed with "# " so CSV parsers can skip them
    /// (pandas: read_csv(comment='#'), manual: skip lines starting with '#').
    /// </summary>
    static class DiagnosticHeader
    {
        public const string ModVersion = "iter-21-7";

        /// <summary>
        /// Write diagnostic comment block into a StringBuilder, before the CSV column header.
        /// </summary>
        public static void WriteTo(StringBuilder sb, int recordingFrames)
        {
            // ── Identity ──
            sb.AppendLine($"# ItemOptimizer io_record — {ModVersion}");
            sb.AppendLine($"# timestamp: {DateTime.UtcNow:o}");
            sb.AppendLine($"# game_version: {GameMain.Version}");
            sb.AppendLine($"# mod_hash: {OptimizerConfig.GetModSetHash()}");

            // ── Environment ──
            string subName = Submarine.MainSub?.Info?.Name ?? "unknown";
            int itemsTotal = Item.ItemList.Count;
            int itemsActive = 0;
            foreach (var item in Item.ItemList)
                if (item.IsActive) itemsActive++;
            float roundDuration = GameMain.GameSession?.RoundDuration ?? 0f;

            sb.AppendLine($"# submarine: {subName}");
            sb.AppendLine($"# items_total: {itemsTotal}");
            sb.AppendLine($"# items_active: {itemsActive}");
            sb.AppendLine($"# round_duration: {roundDuration:F1}s");
            sb.AppendLine($"# recording_frames: {recordingFrames}");

            // Network mode
            string network;
            if (GameMain.NetworkMember == null)
                network = "singleplayer";
            else if (GameMain.NetworkMember.IsClient)
                network = "client";
            else
                network = "server";
            sb.AppendLine($"# network: {network}");

            sb.AppendLine("#");

            // ── Config State ──
            sb.AppendLine("# ── Config State ──");
            AppendConfigState(sb);
            sb.AppendLine("#");

            // ── Harmony Patch Verification ──
            sb.AppendLine("# ── Harmony Patch Verification ──");
            AppendPatchVerification(sb);
            sb.AppendLine("#");

            // ── Runtime Diagnostics ──
            sb.AppendLine("# ── Runtime Diagnostics ──");
            AppendRuntimeVerify(sb);
            sb.AppendLine("#");
        }

        private static void AppendConfigState(StringBuilder sb)
        {
            // Row 1: item-level throttles
            sb.Append("# ColdStorageSkip=").Append(B(OptimizerConfig.EnableColdStorageSkip));
            sb.Append(" GroundItemThrottle=").Append(B(OptimizerConfig.EnableGroundItemThrottle));
            if (OptimizerConfig.EnableGroundItemThrottle)
                sb.Append("(skip=").Append(OptimizerConfig.GroundItemSkipFrames).Append(')');
            sb.Append(" CI=").Append(B(OptimizerConfig.EnableCustomInterfaceThrottle));
            sb.AppendLine();

            // Row 2: sensor throttles + rewrites
            sb.Append("# MotionThrottle=").Append(B(OptimizerConfig.EnableMotionSensorThrottle));
            sb.Append("(skip=").Append(OptimizerConfig.MotionSensorSkipFrames).Append(')');
            sb.Append(" MotionRewrite=").Append(B(OptimizerConfig.EnableMotionSensorRewrite));
            sb.Append(" WaterDetThrottle=").Append(B(OptimizerConfig.EnableWaterDetectorThrottle));
            sb.Append("(skip=").Append(OptimizerConfig.WaterDetectorSkipFrames).Append(')');
            sb.Append(" WaterDetRewrite=").Append(B(OptimizerConfig.EnableWaterDetectorRewrite));
            sb.AppendLine();

            // Row 3: power rewrites
            sb.Append("# RelayRewrite=").Append(B(OptimizerConfig.EnableRelayRewrite));
            sb.Append(" PowerTransferRewrite=").Append(B(OptimizerConfig.EnablePowerTransferRewrite));
            sb.Append(" PowerContainerRewrite=").Append(B(OptimizerConfig.EnablePowerContainerRewrite));
            sb.AppendLine();

            // Row 4: misc toggles
            sb.Append("# WireSkip=").Append(B(OptimizerConfig.EnableWireSkip));
            sb.Append(" DoorThrottle=").Append(B(OptimizerConfig.EnableDoorThrottle));
            if (OptimizerConfig.EnableDoorThrottle)
                sb.Append("(skip=").Append(OptimizerConfig.DoorSkipFrames).Append(')');
            sb.Append(" WearableThrottle=").Append(B(OptimizerConfig.EnableWearableThrottle));
            sb.Append(" HST=").Append(B(OptimizerConfig.EnableHasStatusTagCache));
            sb.Append(" HullSpatial=").Append(B(OptimizerConfig.EnableHullSpatialIndex));
            sb.Append(" AfflictionDedup=").Append(B(OptimizerConfig.EnableAfflictionDedup));
            sb.AppendLine();

            // Row 5: character + advanced
            sb.Append("# AnimLOD=").Append(B(OptimizerConfig.EnableAnimLOD));
            sb.Append(" CharStagger=").Append(B(OptimizerConfig.EnableCharacterStagger));
            if (OptimizerConfig.EnableCharacterStagger)
                sb.Append("(groups=").Append(OptimizerConfig.CharacterStaggerGroups).Append(')');
            sb.Append(" SignalGraph=").Append(OptimizerConfig.SignalGraphMode);
            sb.Append(" MiscParallel=").Append(B(OptimizerConfig.EnableMiscParallel));
            sb.AppendLine();

            // Row 6: client opts
            sb.Append("# RelayOpt=").Append(B(OptimizerConfig.EnableRelayOpt));
            sb.Append(" MotionSensorOpt=").Append(B(OptimizerConfig.EnableMotionSensorOpt));
            sb.Append(" WaterDetectorOpt=").Append(B(OptimizerConfig.EnableWaterDetectorOpt));
            sb.Append(" ButtonTerminalOpt=").Append(B(OptimizerConfig.EnableButtonTerminalOpt));
            sb.Append(" PumpOpt=").Append(B(OptimizerConfig.EnablePumpOpt));
            sb.AppendLine();
        }

        private static void AppendPatchVerification(StringBuilder sb)
        {
            sb.Append("# MotionSensorRewrite.Registered=").AppendLine(B(MotionSensorRewrite.IsRegistered));
            sb.Append("# WaterDetectorRewrite.Registered=").AppendLine(B(WaterDetectorRewrite.IsRegistered));
            sb.Append("# RelayRewrite.Registered=").AppendLine(B(RelayRewrite.IsRegistered));
            sb.Append("# PowerTransferRewrite.Registered=").AppendLine(B(PowerTransferRewrite.IsRegistered));
            sb.Append("# PowerContainerRewrite.Registered=").AppendLine(B(PowerContainerRewrite.IsRegistered));
        }

        private static string B(bool v) => v ? "true" : "false";

        // ── Runtime diagnostics: config says ON → is it actually working? ──

        private static void AppendRuntimeVerify(StringBuilder sb)
        {
            var warns = new List<string>();

            // UpdateAllTakeover
            if (!UpdateAllTakeover.Enabled)
                warns.Add("UpdateAllTakeover disabled — all optimizations inactive");

            // SignalGraph
            if (OptimizerConfig.SignalGraphMode > 0)
            {
                if (!SignalGraphEvaluator.IsCompiled)
                    warns.Add($"SignalGraph mode={OptimizerConfig.SignalGraphMode} but not compiled");
                else
                    sb.AppendLine($"# SignalGraph: compiled, {SignalGraphEvaluator.AcceleratedNodeCount} nodes, {SignalGraphEvaluator.RegisterCount} regs");
            }

            // Rewrites: config enabled → must be registered
            if (OptimizerConfig.EnableMotionSensorRewrite && !MotionSensorRewrite.IsRegistered)
                warns.Add("MotionRewrite enabled but not registered");
            if (OptimizerConfig.EnableWaterDetectorRewrite && !WaterDetectorRewrite.IsRegistered)
                warns.Add("WaterDetRewrite enabled but not registered");
            if (OptimizerConfig.EnableRelayRewrite && !RelayRewrite.IsRegistered)
                warns.Add("RelayRewrite enabled but not registered");
            if (OptimizerConfig.EnablePowerTransferRewrite && !PowerTransferRewrite.IsRegistered)
                warns.Add("PowerTransferRewrite enabled but not registered");
            if (OptimizerConfig.EnablePowerContainerRewrite && !PowerContainerRewrite.IsRegistered)
                warns.Add("PowerContainerRewrite enabled but not registered");

            // Output
            if (warns.Count == 0)
            {
                sb.AppendLine("# ALL_OK");
            }
            else
            {
                foreach (var w in warns)
                {
                    sb.Append("# WARN: ").AppendLine(w);
                    LuaCsLogger.LogError($"[ItemOptimizer] Diagnostic WARN: {w}");
                }
            }
        }
    }
}
