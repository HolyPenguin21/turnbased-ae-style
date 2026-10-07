namespace Game.Ai.V2
{
    // Existing domain result semantics, owned by the same continuity policy.
    internal static partial class MissionContinuityLayer
    {
        internal static bool IsScoutStepObjectiveSatisfiedLive(Game.Players.PlayerSetupData player,
            ProvisionedMission pm) => ScoutObjectiveEvaluator.IsSatisfiedLive(player, pm.ScoutKind, pm.FocusHex);

        internal static void ClassifyScoutStep(ExecutionResult e, MissionTurnOutcome o)
        {
            if (e.StopReason == ExecutionStopReason.TargetInvalidated)
            {
                o.Outcome = ExecutionOutcome.Blocked;
                return;
            }
            MissionStepResultPolicy.ClassifyDefaultExecution(e, o);
        }
    }
}
