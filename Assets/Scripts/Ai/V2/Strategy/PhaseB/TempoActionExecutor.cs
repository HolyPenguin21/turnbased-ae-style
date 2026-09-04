using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Cards;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // spec §P1.7 — one structured result for EVERY tempo action (planning/execution parity,
    // diagnostics, retry-loop protection). Resource-spent fields are measured by the arbiter
    // around the call; the execute paths own the semantic flags.
    internal struct TempoExecutionResult
    {
        public bool Succeeded;
        public bool StateChanged;
        public bool Progressed;
        public bool Interrupt;
        public float ApSpent, HumanSpent, EnergySpent, MaterialsSpent, TechSpent;
        public bool CardPlayed, Drawn, GenerationAttempted, Generated, Attached;
        public string FailReason;
    }

    // ARCH-02 §8 — the Phase-B tempo executor: given a chosen tempo candidate it performs exactly
    // that action through the canonical gameplay paths and reports a structured result. It never
    // re-selects, re-scores or retargets. Extracted verbatim from StrategicManager.
    internal static class TempoActionExecutor
    {
        // Execute one materialization-surplus chain, mirroring the old inline Phase-B path
        // (finalization / residual bookkeeping / capability-changed interrupt). Returns the
        // refreshed snapshot; `exec` drives the arbiter's park / rebuild / stop logic (spec §P1.7).
        internal static WorldSnapshot ExecuteMatSurplus(MatSurplusDecision mat, WorldSnapshot snap,
            PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx,
            ActorCommitments commitments, StrategicPhaseResult result, ref TempoExecutionResult exec)
        {
            MaterializationPlan plan = mat.Plan;
            AxisDemand residual = mat.Residual;
            CapabilityInventory inv = mat.Inv;

            var armyIdsBefore = new HashSet<int>(snap.Self?.Armies?
                .Where(a => a != null).Select(a => a.ArmyId) ?? Enumerable.Empty<int>());
            MaterializationResult play = MaterializationExecutor.Execute(snap, player, root, hand, ctx, plan, commitments);
            result.MaterializationAttempts++;
            if (play.Deployed) result.MaterializationsSucceeded++;
            if (plan.Generation != null)
            {
                result.GeneratedCardAttempts++;
                if (play.Generated) result.GeneratedCardsSucceeded++;
            }
            if (plan.UsesEquipment)
            {
                result.EquipmentAssignmentAttempts++;
                if (play.Attached) result.EquipmentAssignmentsSucceeded++;
            }
            if (plan.Generation != null)
            {
                result.Reservation.RecordGenerationAttempt(plan.Generation, play);
                StrategicTempoBudget.RecordGenerationAttempt(player, ctx.TurnNumber);
                exec.GenerationAttempted = true;
            }
            exec.Generated |= play.Generated;
            exec.Attached |= play.Attached;
            if (play.StateChanged) { exec.StateChanged = true; result.StateChanged = true; }

            if (!play.Deployed)
            {
                exec.FailReason = play.FailReason;
                AiDebugLog.Write($"[AI][V2]   strat.B — {plan.Kind} {AiCardLog.Plan(plan)} "
                    + $"chain did not deploy ({play.FailReason})");
                return play.StateChanged
                    ? WorldAnalysis.RefreshOperationalState(snap, player, root, hand, ctx) : snap;
            }
            result.CardsPlayed++;
            exec.Succeeded = true; exec.Progressed = true; exec.CardPlayed = true;

            snap = WorldAnalysis.RefreshOperationalState(snap, player, root, hand, ctx);
            CapabilityInventory afterInv = CapabilityInventory.Build(snap, player, commitments);
            float delivered = 0f;
            bool operationalResidual = residual != null && CapabilityDeliveryEvaluator.FinalizeOperationalDelivery(
                player, ctx, snap, plan, residual, inv, afterInv, armyIdsBefore, out delivered);
            if (operationalResidual)
            {
                residual.DesiredAmount = Mathf.Max(0f, residual.DesiredAmount - delivered);
                if (residual.DesiredAmount <= AiConfigV2.allocatorSliceEpsilon)
                    result.Reservation.UnresolvedDemands.Remove(residual);
                result.CapabilityDeliveries++;
            }
            AiDebugLog.Write($"[AI][V2]   strat.B — {plan.Kind} {AiCardLog.Plan(plan)} "
                + $"util {F(mat.Utility)} (ap {F(play.ApSpent)}, {plan.Deploy.Kind}, delivered {F(delivered)}, {plan.StableKey})");

            if (operationalResidual)
            {
                StrategicInterruptRegistry.MarkCapabilityChanged(player, ctx.TurnNumber, hand);
                AiDebugLog.Write($"[AI][V2] strategic interrupt — Phase B delivered operational "
                    + $"{residual.Capability}; re-admit missions before further surplus spending");
                exec.Interrupt = true;
            }
            return snap;
        }

        internal static WorldSnapshot ExecuteNonCombatSurplus(NonCombatCardPlayer.NonCombatPlay nc,
            WorldSnapshot snap, PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx,
            StrategicPhaseResult result, ref TempoExecutionResult exec)
        {
            result.MaterializationAttempts++;
            NonCombatCardPlayer.NonCombatExecuteResult ncRes =
                NonCombatCardPlayer.Execute(nc, snap, player, root, hand, ctx);
            if (ncRes.StateChanged) { exec.StateChanged = true; result.StateChanged = true; }
            if (ncRes.GenerationAttempted)
            {
                result.Reservation.RecordGenerationAttempt(nc.Generation, null);
                StrategicTempoBudget.RecordGenerationAttempt(player, ctx.TurnNumber);
                exec.GenerationAttempted = true;
                result.GeneratedCardAttempts++;
                if (ncRes.Generated) result.GeneratedCardsSucceeded++;
            }
            exec.Generated |= ncRes.Generated;
            if (!ncRes.Played)
            {
                exec.FailReason = ncRes.FailReason;
                AiDebugLog.Write($"[AI][V2]   strat.B non-combat — {nc.Kind} {nc.Explain} "
                    + $"did not play ({ncRes.FailReason}{(ncRes.Generated ? "; generated card kept in hand" : "")})");
                return ncRes.StateChanged
                    ? WorldAnalysis.RefreshOperationalState(snap, player, root, hand, ctx) : snap;
            }
            result.MaterializationsSucceeded++;
            result.CardsPlayed++;
            exec.Succeeded = true; exec.Progressed = true; exec.CardPlayed = true;
            if (nc.Kind == NonCombatCardPlayer.PlayKind.Base || nc.Kind == NonCombatCardPlayer.PlayKind.Facility)
            {
                result.InfrastructureAttempts++;
                result.InfrastructureBuilt++;
            }
            else if (nc.Kind == NonCombatCardPlayer.PlayKind.Equipment)
            {
                result.EquipmentAssignmentAttempts++;
                result.EquipmentAssignmentsSucceeded++;
                exec.Attached = true;
            }
            AiDebugLog.Write($"[AI][V2]   strat.B non-combat — played {nc.Kind} {nc.Explain} (ap {F(ncRes.ApSpent)})");
            return WorldAnalysis.RefreshOperationalState(snap, player, root, hand, ctx);
        }

        private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
