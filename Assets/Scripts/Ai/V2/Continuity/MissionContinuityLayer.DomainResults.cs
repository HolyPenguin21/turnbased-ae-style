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
        // Ordered compatibility bindings retain original side-leg/completion/payload precedence.
        // A handled completed leg may keep a durable operation; only domain retirement removes it.
        private delegate bool OutcomeTransition(MissionIntentState state, AiAllocatorState allocator,
            MissionIntent intent, MissionTurnOutcome result, int turn);
        private static readonly OutcomeTransition[] SideLegTransitions =
            { TryHandleAttackSideLeg, TryHandleInvalidGroundSupport };
        private static readonly OutcomeTransition[] CompletionTransitions =
            { TryCompleteRaidTarget, TryCompleteAttackLeg, TryContinueScoutWaypoint };
        private static readonly OutcomeTransition[] RecoveryTransitions = { TryKeepEconomyRecovery };
        private static readonly OutcomeTransition[] RetirementTransitions = { TryRetireEconomyOutcome };
        private static readonly OutcomeTransition[] NoProgressTransitions = { TryHandleEconomyNoProgress };
        private static readonly OutcomeTransition[] CreationTransitions =
            { TryCreateScoutStep, TryCreateRaidStep, TryCreateAttackStep, TryCreateActiveDefenceStep,
                TryCreateEconomyStep, TryCreateDevelopmentStep };
        private static readonly Action<MissionIntentState, MissionTurnOutcome, int>[] ProgressRecorders =
            { RecordEconomyStepProgress };
        private static void RecordDomainStepProgress(MissionIntentState state, MissionTurnOutcome result, int turn)
        {
            foreach (var record in ProgressRecorders) record(state, result, turn);
        }
        private static bool TryDomainTransition(IEnumerable<OutcomeTransition> handlers, MissionIntentState state,
            AiAllocatorState allocator, MissionIntent intent, MissionTurnOutcome result, int turn)
        {
            foreach (var handle in handlers)
                if (handle(state, allocator, intent, result, turn)) return true;
            return false;
        }
        private delegate void OutcomeObservation(MissionIntentState state, AiAllocatorState allocator,
            MissionIntent intent, MissionTurnOutcome result, int turn);
        private static readonly OutcomeObservation[] BeforeMoverObservers = { ObserveReconMover };
        private static readonly OutcomeTransition[] MoverTransitions =
            { TryApplyRaidMoverFacts, TryPreserveAttackSupportMover, TryPreservePinnedDomainMover };
        private static readonly OutcomeObservation[] IntentFactObservers =
            { ApplyScoutStepFacts, ApplyAttackStepFacts, ApplyRaidStepFacts, ApplyEconomyStepFacts };
        private static readonly OutcomeTransition[] CapabilityFailureTransitions =
            { TryHandleEconomyCapabilityFailure };

        // Existing durable pinning rule, composed here; generic lease storage knows no mission kind.
        private static bool TryPreservePinnedDomainMover(MissionIntentState state, AiAllocatorState allocator,
            MissionIntent intent, MissionTurnOutcome result, int turn) =>
            (intent.Kind == MissionKind.Economy || intent.Kind == MissionKind.Development)
                && intent.PreferredMoverArmyId.HasValue
                && intent.PreferredMoverArmyId.Value != result.MoverArmyId.Value;

        // Same capability-failure exceptions as the former two expressions; Raid ages only for reap.
        private static bool DomainAgesCapabilityFailure(MissionIntent intent, bool forReaping) =>
            intent.Kind == MissionKind.Development || forReaping && intent.Kind == MissionKind.Raid
                || IsMoverlessScoutRole(intent) || IsCollectorEconomyIntent(intent);
    }
}
