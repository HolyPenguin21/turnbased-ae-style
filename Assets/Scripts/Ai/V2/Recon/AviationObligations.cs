using System.Collections.Generic;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  AVIATION OBLIGATIONS FIRST
    // ===========================================================================================
    //  Airborne wings that must return (ReconAirExecutor.FindMandatoryRecoveryActors) and committed
    //  rebases (AviationRebasePlanner.FindMandatoryContinuations) are settled BEFORE any card play
    //  of the turn. The pipeline defers the initial Phase A and every strategic re-admission while
    //  one is pending, and admits the collected axes once in a single pass when they are done. The
    //  order itself protects their activation AP / Energy, so no reservation holds it.
    //
    //  An obligation that cannot progress is stalled for the turn (AviationObligationStallRegistry)
    //  and stops counting, so an unaffordable or blocked wing never freezes card play.
    // ===========================================================================================
    internal static class AviationObligations
    {
        internal static bool Pending(PlayerSetupData player, AiTurnContext ctx) =>
            player != null && ctx != null
            && (AviationRebasePlanner.FindMandatoryContinuations(player, ctx.TurnNumber).Count > 0
                || ReconAirExecutor.FindMandatoryRecoveryActors(player, ctx).Count > 0);

        // The activation AP the pending obligations will spend this turn. The order above protects
        // it from card play; a spend made before that order (the turn-start hand refill) must
        // leave it untouched itself.
        internal static int ActivationAp(PlayerSetupData player, AiTurnContext ctx)
        {
            if (player == null || ctx == null)
                return 0;
            var wings = new HashSet<ArmyData>(AviationRebasePlanner.FindMandatoryContinuations(player, ctx.TurnNumber));
            wings.UnionWith(ReconAirExecutor.FindMandatoryRecoveryActors(player, ctx));
            int ap = 0;
            foreach (ArmyData wing in wings)
                if (wing != null && !wing.HasActivatedThisTurn)
                    ap += System.Math.Max(0, wing.ActivationApCost);
            return ap;
        }
    }

    // The strategic axes whose re-admission waited for aviation obligations. Pure bookkeeping so
    // the pipeline's defer / flush rule is testable on its own.
    internal sealed class DeferredStrategicAdmission
    {
        private readonly HashSet<DesireAxis> _axes = new HashSet<DesireAxis>();

        internal bool HasAxes => _axes.Count > 0;
        internal IReadOnlyCollection<DesireAxis> Axes => _axes;

        internal void Defer(IEnumerable<DesireAxis> axes)
        {
            if (axes != null)
                _axes.UnionWith(axes);
        }

        // The axes to admit now: the caller's own plus every deferred one. Clears the deferral.
        internal HashSet<DesireAxis> TakeWith(IEnumerable<DesireAxis> axes)
        {
            var result = new HashSet<DesireAxis>(_axes);
            if (axes != null)
                result.UnionWith(axes);
            _axes.Clear();
            return result;
        }
    }
}
