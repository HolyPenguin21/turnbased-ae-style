namespace Game.Ai.V2
{
    // Existing domain result semantics, owned by the same continuity policy.
    internal static partial class MissionContinuityLayer
    {
        internal static bool IsEconomyStepObjectiveSatisfiedLive(Game.Players.PlayerSetupData player,
            ProvisionedMission pm) => EconomyLifecycleState.ObjectiveSatisfied(player, pm.EconomyTarget);

        internal static void ClassifyEconomyStep(ExecutionResult e, MissionTurnOutcome o)
        {
            // A committed roster mutation remains progress if its pinned tail became stale.
            if (e.EconomyPrepared && e.StopReason == ExecutionStopReason.TargetInvalidated)
            {
                o.Outcome = ExecutionOutcome.ProductiveStop;
                return;
            }
            switch (e.StopReason)
            {
                case ExecutionStopReason.StepCompleted:
                case ExecutionStopReason.OutOfMovement:
                    o.Outcome = ExecutionOutcome.ProductiveStop;
                    break;
                case ExecutionStopReason.NoSafeStep:
                case ExecutionStopReason.MoveRejected:
                // Economy audit B2 — every execution-side TargetInvalidated of an Economy step
                // is transient (stale plan, unaffordable activation, AP/resources of a pinned
                // preparation gone this pass), never proof the durable build is invalid:
                // Continuity re-validates the objective itself (ResolveActive).
                case ExecutionStopReason.TargetInvalidated:
                    o.Outcome = ExecutionOutcome.Blocked;
                    break;
                default:
                    o.Outcome = ExecutionOutcome.Failed;
                    break;
            }
            return;
        }
    }
}
