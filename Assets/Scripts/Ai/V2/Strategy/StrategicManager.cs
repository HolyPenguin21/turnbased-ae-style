using System.Collections;
using System.Collections.Generic;
using Game.Cards;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // ARCH-02 §8/§49 — StrategicManager is now a THIN FACADE. Everything it used to own moved to a
    // single-responsibility owner:
    //   Phase A orchestration ............ StrategicPhaseA.FulfillDemands
    //   Phase B tempo arbiter loop ....... StrategicPhaseB.UseSurplus
    //   jointly-feasible portfolio ....... MaterializationPortfolioSolver
    //   delivered capability + lease ..... CapabilityDeliveryEvaluator
    //   tempo candidate construction ..... TempoCandidateProvider
    //   tempo action execution ........... TempoActionExecutor
    //   persistent-resource hold policy .. HoldEvaluator
    //   strategic spendability ........... StrategicSpendability
    // This forwarder just keeps the two stable entry points the orchestrator, StrategicReactionPass
    // and HousekeepingManager call. No logic lives here.
    public static class StrategicManager
    {
        public static StrategicPhaseResult FulfillDemands(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, AxisBudgetLedger ledger,
            IReadOnlyList<AxisDemand> demands, ActorCommitments commitments)
            => StrategicPhaseA.FulfillDemands(snap, player, root, hand, ctx, ledger, demands, commitments);

        public static IEnumerator UseSurplus(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, ActorCommitments commitments,
            MaterializationReservation carriedReservation, StrategicPhaseResult result,
            IReadOnlyList<ReconObjective> reconObjectives = null)
            => StrategicPhaseB.UseSurplus(snap, player, root, hand, ctx, commitments,
                carriedReservation, result, reconObjectives);
    }
}
