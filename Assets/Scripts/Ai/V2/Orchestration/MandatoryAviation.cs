using System;
using System.Collections;
using System.Collections.Generic;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    public enum MandatoryAviationKind { None, Rebase, Recovery }

    // ===========================================================================================
    //  Mandatory aviation obligations of the operational loop.
    //  ORDER — which airborne obligation is settled next, and the per-kind facts the shared step
    //  protocol needs. A multi-turn rebase is resumed before a recovery unless the recovery actor
    //  has the lower army id; both lists come from their existing owners
    //  (AviationRebasePlanner.FindMandatoryContinuations,
    //  ReconAirExecutor.FindMandatoryRecoveryActors; both already exclude stalled wings). This
    //  only orders their heads and names the kinds. Pure: no world access.
    //  STEP — adapter from the selected obligation to its existing executor. It owns no policy and
    //  no state: planning and physics stay in AviationRebasePlanner / ReconAirExecutor, and the
    //  shared post-step protocol stays in the pipeline. `actionChanged` reports whether the
    //  executor changed the world (rebase: the wing moved; recovery: the actor step mutated).
    // ===========================================================================================
    internal static class MandatoryAviation
    {
        internal static bool RebaseFirst(int? firstRebaseId, int? firstRecoveryId) =>
            firstRebaseId.HasValue
            && (!firstRecoveryId.HasValue || firstRebaseId.Value <= firstRecoveryId.Value);

        // The next obligation to settle, or (None, null) when nothing is pending. Each list is
        // ordered by army id by its owner; only the heads compete.
        internal static (MandatoryAviationKind Kind, ArmyData Actor) Next(
            IReadOnlyList<ArmyData> rebaseContinuations, IReadOnlyList<ArmyData> recoveries)
        {
            ArmyData rebase = rebaseContinuations != null && rebaseContinuations.Count > 0
                ? rebaseContinuations[0] : null;
            ArmyData recovery = recoveries != null && recoveries.Count > 0 ? recoveries[0] : null;
            if (rebase != null && RebaseFirst(rebase.Id, recovery?.Id))
                return (MandatoryAviationKind.Rebase, rebase);
            if (recovery != null)
                return (MandatoryAviationKind.Recovery, recovery);
            return (MandatoryAviationKind.None, null);
        }

        // Baseline difference, kept deliberately (see StepTriggerSequence for the reasoning): a
        // rebase takes the typed triggers and re-enters once, a recovery twice.
        internal static int TriggerPairs(MandatoryAviationKind kind) =>
            kind == MandatoryAviationKind.Recovery
                ? StepTriggerSequence.StandardPairs : StepTriggerSequence.RebasePairs;

        // Name used in the [Loop] step line and the invariant boundary label.
        internal static string Label(MandatoryAviationKind kind) =>
            kind == MandatoryAviationKind.Rebase ? "aviation-rebase" : "recovery";

        // Recon audit B1 — the obligation is skipped for the rest of the turn; it must not stop
        // every mission's admission with it.
        internal static string StallMessage(MandatoryAviationKind kind, int actorId) =>
            kind == MandatoryAviationKind.Rebase
                ? $"aviation rebase #{actorId} could not take a safe step - deferred to next turn, missions continue"
                : $"recovery #{actorId} made no progress - deferred to next turn, missions continue";
    

        internal static IEnumerator Execute(MandatoryAviationKind kind, ArmyData actor,
            PlayerSetupData player, PlayerRoot root, AiTurnContext ctx, WorldSnapshot snapshot,
            Action<bool> actionChanged)
        {
            if (kind == MandatoryAviationKind.Rebase)
            {
                // Route safety and destination validity are live-rechecked inside the executor.
                yield return AviationRebaseExecutor.ExecuteContinuation(
                    player, root, ctx, actor, actionChanged);
            }
            else if (kind == MandatoryAviationKind.Recovery)
            {
                HexCoord? focus =
                    ReconPatrolStateRegistry.TryGet(player, actor.Id, out ReconPatrolState state)
                        ? state.StrategicAnchor : (HexCoord?)null;
                var result = new AirReconExecutionResult();
                yield return ReconAirExecutor.RunActorStep(player, root, ctx, snapshot, actor,
                    result, root.ActionPoints, focus, perMissionResult: null,
                    control: new ReconAirExecutor.ActorStepControl());
                actionChanged?.Invoke(result.Mutated);
            }
        }
    
    }
}
