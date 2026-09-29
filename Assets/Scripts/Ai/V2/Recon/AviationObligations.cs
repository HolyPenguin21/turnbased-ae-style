using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
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
        // Initiative purchases happen after round income but before this player's first aviation
        // obligation can execute. Protect exactly the NEXT activation Energy of every air army that
        // ended the previous turn away from an owned airfield. The reservation is recomputed every
        // round from live armies; nothing is stored cross-turn.
        internal static int NextActivationEnergyCommitment(PlayerSetupData player)
        {
            if (player == null)
                return 0;
            return ArmyRegistry.AllForOwner(player)
                .Where(a => AviationRules.IsValidAirArmy(a)
                    && !AviationRules.IsOwnedAirfieldAt(a.Hex, player))
                .Sum(a => System.Math.Max(0, a.ActivationEnergyCost));
        }

        internal static bool Pending(PlayerSetupData player, AiTurnContext ctx) =>
            player != null && ctx != null
            && (AviationRebasePlanner.FindMandatoryContinuations(player, ctx.TurnNumber).Count > 0
                || ReconAirExecutor.FindMandatoryRecoveryActors(player, ctx).Count > 0);
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
