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
    // and HousekeepingManager call, plus the named moments of the turn (the orchestrator reports
    // them, StrategicTurnLifecycle decides what the bank does). No logic lives here.
    public static class StrategicManager
    {
        public static StrategicPhaseResult FulfillDemands(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, PhaseAApBudget apBudget,
            IReadOnlyList<AxisDemand> demands, ActorCommitments commitments,
            IReadOnlyList<MissionIntent> activeIntents = null,
            IReadOnlyList<ReconObjective> reconObjectives = null,
            MaterializationReservation carriedReservation = null,
            bool economyAxisAuthoritative = true, Radar radar = null,
            bool deferFreshZeroRadar = false)
            => StrategicPhaseA.FulfillDemands(snap, player, root, hand, ctx, apBudget, demands, commitments,
                activeIntents, reconObjectives, carriedReservation, economyAxisAuthoritative, radar,
                deferFreshZeroRadar);

        public static IEnumerator UseSurplus(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, ActorCommitments commitments,
            MaterializationReservation carriedReservation, StrategicPhaseResult result,
            IReadOnlyList<ReconObjective> reconObjectives = null)
            => StrategicPhaseB.UseSurplus(snap, player, root, hand, ctx, commitments,
                carriedReservation, result, reconObjectives);

        // ---- Moments of the turn (see StrategicTurnLifecycle) ----

        internal static void ObserveInitialForce(WorldSnapshot snapshot, PlayerSetupData player,
            AiTurnContext ctx)
            => StrategicTurnLifecycle.ObserveInitialForce(snapshot, player, ctx);

        internal static void AfterMissionSettlement(PlayerSetupData player, PlayerRoot root,
            AiHandData hand, AiTurnContext ctx)
            => StrategicTurnLifecycle.AfterMissionSettlement(player, root, hand, ctx);

        internal static void BeforeFirstTempo(PlayerSetupData player, PlayerRoot root,
            AiHandData hand, AiTurnContext ctx)
            => StrategicTurnLifecycle.BeforeFirstTempo(player, root, hand, ctx);

        internal static void BeforeTempoSpend(PlayerSetupData player, PlayerRoot root,
            AiHandData hand, AiTurnContext ctx)
            => StrategicTurnLifecycle.BeforeTempoSpend(player, root, hand, ctx);
    }
}
