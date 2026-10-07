namespace Game.Ai.V2
{
    // Existing domain result semantics, owned by the same continuity policy.
    internal static partial class MissionContinuityLayer
    {
        internal static bool IsDevelopmentStepObjectiveSatisfiedLive(Game.Players.PlayerSetupData player,
            ProvisionedMission pm) => Game.Cards.ResearchProductionSystem.ActorStillQualifies(player,
                pm.DevelopmentTarget.Hero, pm.DevelopmentTarget.FacilityHex, pm.DevelopmentTarget.Mode)
                && Game.Cards.ResearchProductionSystem.IsEligible(player,
                    pm.DevelopmentTarget.FacilityHex, pm.DevelopmentTarget.Mode, out _);

        internal static void ClassifyDevelopmentStep(ExecutionResult e, MissionTurnOutcome o)
        {
            switch (e.StopReason)
            {
                case ExecutionStopReason.StepCompleted:
                case ExecutionStopReason.OutOfMovement:
                    o.Outcome = ExecutionOutcome.ProductiveStop;
                    break;
                case ExecutionStopReason.NoSafeStep:
                case ExecutionStopReason.MoveRejected:
                case ExecutionStopReason.BattleStarted:
                case ExecutionStopReason.HexEventStarted:
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
