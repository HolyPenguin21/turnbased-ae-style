using System;
using System.Collections.Generic;

namespace Game.Ai.V2
{
    // Domain composition only. Generic result/lease storage never implements these rules.
    internal static partial class MissionContinuityLayer
    {
        private static readonly IReadOnlyDictionary<MissionKind, Action<ExecutionResult, MissionTurnOutcome>>
            ExecutionClassifiers = new Dictionary<MissionKind, Action<ExecutionResult, MissionTurnOutcome>>
            {
                [MissionKind.Scout] = ClassifyScoutStep,
                [MissionKind.Raid] = ClassifyRaidStep,
                [MissionKind.Attack] = ClassifyAttackStep,
                [MissionKind.Economy] = ClassifyEconomyStep,
                [MissionKind.Development] = ClassifyDevelopmentStep,
            };
        private static readonly IReadOnlyDictionary<MissionKind, Func<Game.Players.PlayerSetupData, ProvisionedMission, bool>>
            LiveObjectiveReaders = new Dictionary<MissionKind, Func<Game.Players.PlayerSetupData, ProvisionedMission, bool>>
            {
                [MissionKind.Scout] = IsScoutStepObjectiveSatisfiedLive,
                [MissionKind.Raid] = IsRaidStepObjectiveSatisfiedLive,
                [MissionKind.Attack] = IsAttackStepObjectiveSatisfiedLive,
                [MissionKind.ActiveDefence] = IsActiveDefenceStepObjectiveSatisfiedLive,
                [MissionKind.Economy] = IsEconomyStepObjectiveSatisfiedLive,
                [MissionKind.Development] = IsDevelopmentStepObjectiveSatisfiedLive,
            };
        internal static bool IsStepObjectiveSatisfiedLive(Game.Players.PlayerSetupData player, ProvisionedMission mission) =>
            LiveObjectiveReaders.TryGetValue(mission.Kind, out var read)
                ? read(player, mission) : IsScoutStepObjectiveSatisfiedLive(player, mission);
        private static readonly IReadOnlyDictionary<MissionKind, Func<MissionTurnOutcome, bool>>
            InvalidatedTargetContinues = new Dictionary<MissionKind, Func<MissionTurnOutcome, bool>>
            {
                [MissionKind.Scout] = _ => true,
                [MissionKind.Raid] = GroundCombatLegs.IsSupportLeg,
                [MissionKind.Attack] = GroundCombatLegs.IsSupportLeg,
            };
        internal static void ClassifyDomainExecution(ExecutionResult execution, MissionTurnOutcome outcome)
        {
            if (ExecutionClassifiers.TryGetValue(outcome.MissionKind, out var classify)) classify(execution, outcome);
            else MissionStepResultPolicy.ClassifyDefaultExecution(execution, outcome);
        }
        internal static bool TryClassifySatisfiedDomainLeg(MissionTurnOutcome outcome) =>
            outcome.MissionKind == MissionKind.Raid && TryClassifySatisfiedRaidLeg(outcome);
        internal static void ClassifyInvalidatedDomainTarget(MissionTurnOutcome outcome) =>
            outcome.Outcome = InvalidatedTargetContinues.TryGetValue(outcome.MissionKind, out var continues)
                && continues(outcome) ? ExecutionOutcome.Blocked : ExecutionOutcome.Failed;
    }
}
