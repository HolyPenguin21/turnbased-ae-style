using System.Collections.Generic;
using Game.Map;

namespace Game.Ai.V2
{
    public enum MandatoryAviationKind { None, Rebase, Recovery }

    // ===========================================================================================
    //  Which airborne obligation the operational loop settles next, and the per-kind facts the
    //  shared step protocol needs. A multi-turn rebase is resumed before a recovery unless the
    //  recovery actor has the lower army id; both lists come from their existing owners
    //  (AviationRebasePlanner.FindMandatoryContinuations,
    //  ReconAirExecutor.FindMandatoryRecoveryActors; both already exclude stalled wings). This
    //  type only orders their heads and names the kinds. Pure: no world access.
    // ===========================================================================================
    internal static class MandatoryAviationOrder
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
                ? $"aviation rebase #{actorId} could not take a safe step — deferred to next turn, missions continue"
                : $"recovery #{actorId} made no progress — deferred to next turn, missions continue";
    }
}
