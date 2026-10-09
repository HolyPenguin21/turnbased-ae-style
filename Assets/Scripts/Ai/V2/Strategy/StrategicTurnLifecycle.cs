using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  The moments of the AI turn at which the strategic bank changes the STAGE of what it holds.
    //  The orchestrator reports its own moments (a mission step settled, the ordinary passes
    //  closed, a Phase B round is about to spend, the turn was scanned); WHAT the bank does at each
    //  moment is decided here, by the owners of the spend rules, so a new hold stage is a Strategy
    //  / State change and does not edit the order of the turn.
    //
    //  This type holds no bank state and no formula: it only joins existing owners
    //  (EconomyReservationLifecycle for the Economy stages, OperationContinuationWindow for the
    //  continuation window and the mobilization gate) in the order each moment needs. The order is
    //  the order the orchestrator called them in before; it is fixed here and by
    //  AiStrategicTurnLifecycleTests.
    // ===========================================================================================
    internal static class StrategicTurnLifecycle
    {
        // The turn was scanned: stamp the Attack mobilization gate. The first Attack preparation
        // step's AP hold reads it.
        internal static void ObserveInitialForce(WorldSnapshot snapshot, PlayerSetupData player,
            AiTurnContext ctx)
        {
            bool mobilizationOpen = AttackForceReadiness.MobilizationOpen(snapshot?.Self);
            OperationContinuationWindow.SetMobilizationOpen(player, ctx.TurnNumber, mobilizationOpen);
            if (mobilizationOpen)
                AiDebugLog.Write($"[AI][V2][Attack][Mobilization] {player.Nickname}: gate open, Phase A plays around "
                    + "the AP of the next preparation step (strike-force cards excepted)");
        }

        // A mission step settled: a single atomic move may have consumed the last MP after
        // Provisioning had legitimately reserved this owner's completion AP; settle its stage now.
        internal static void AfterMissionSettlement(PlayerSetupData player, PlayerRoot root,
            AiHandData hand, AiTurnContext ctx)
        {
            EconomyReservationLifecycle.ReconcileEconomyCompletionReservations(player, root, hand, ctx);
        }

        // The ordinary passes are closed (also on bounded / no-progress exits where no further typed
        // admission occurs): Phase B must see the AP no actor can spend on a build; every build still
        // deferred is proven not to complete this turn, so the part of its hold the next income tick
        // covers is released; the Phase-A protection of continuing Hard operations ends.
        internal static void BeforeFirstTempo(PlayerSetupData player, PlayerRoot root, AiHandData hand,
            AiTurnContext ctx)
        {
            EconomyReservationLifecycle.ReconcileEconomyCompletionReservations(player, root, hand, ctx);
            EconomyReservationLifecycle.ReleaseDeferredEconomyIncomeCover(player, ctx);
            OperationContinuationWindow.Settle(player, ctx.TurnNumber);
        }

        // A Phase B round is about to spend: a prior Phase B action may have spent AP or removed a
        // build card, so revalidate each owner's stronger completion claim first.
        internal static void BeforeTempoSpend(PlayerSetupData player, PlayerRoot root, AiHandData hand,
            AiTurnContext ctx)
        {
            EconomyReservationLifecycle.ReconcileEconomyCompletionReservations(player, root, hand, ctx);
        }
    }
}
