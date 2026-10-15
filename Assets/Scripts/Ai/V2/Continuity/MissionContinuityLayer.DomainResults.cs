using System;
using System.Collections.Generic;

namespace Game.Ai.V2
{
    // Domain composition only. Generic result/lease storage never implements these rules.
    internal static partial class MissionContinuityLayer
    {
        private static readonly IReadOnlyDictionary<MissionKind, Action<ExecutionResult, MissionStepResult>>
            ExecutionClassifiers = new Dictionary<MissionKind, Action<ExecutionResult, MissionStepResult>>
            {
                [MissionKind.Scout] = ReconContinuity.ClassifyScoutStep,
                [MissionKind.Raid] = RaidContinuity.ClassifyRaidStep,
                [MissionKind.Attack] = AttackContinuity.ClassifyAttackStep,
                [MissionKind.Economy] = EconomyContinuity.ClassifyEconomyStep,
                [MissionKind.Development] = DevelopmentContinuity.ClassifyDevelopmentStep,
            };
        private static readonly IReadOnlyDictionary<MissionKind, Func<Game.Players.PlayerSetupData, ProvisionedMission, bool>>
            LiveObjectiveReaders = new Dictionary<MissionKind, Func<Game.Players.PlayerSetupData, ProvisionedMission, bool>>
            {
                [MissionKind.Scout] = ReconContinuity.IsScoutStepObjectiveSatisfiedLive,
                [MissionKind.Raid] = RaidContinuity.IsRaidStepObjectiveSatisfiedLive,
                [MissionKind.Attack] = AttackContinuity.IsAttackStepObjectiveSatisfiedLive,
                [MissionKind.ActiveDefence] = ActiveDefenceContinuity.IsActiveDefenceStepObjectiveSatisfiedLive,
                [MissionKind.Economy] = EconomyContinuity.IsEconomyStepObjectiveSatisfiedLive,
                [MissionKind.Development] = DevelopmentContinuity.IsDevelopmentStepObjectiveSatisfiedLive,
            };
        internal static bool IsStepObjectiveSatisfiedLive(Game.Players.PlayerSetupData player, ProvisionedMission mission) =>
            LiveObjectiveReaders.TryGetValue(mission.Kind, out var read)
                ? read(player, mission) : ReconContinuity.IsScoutStepObjectiveSatisfiedLive(player, mission);
        private static readonly IReadOnlyDictionary<MissionKind, Func<MissionStepResult, bool>>
            InvalidatedTargetContinues = new Dictionary<MissionKind, Func<MissionStepResult, bool>>
            {
                [MissionKind.Scout] = _ => true,
                [MissionKind.Raid] = GroundCombatLegs.IsSupportLeg,
                [MissionKind.Attack] = GroundCombatLegs.IsSupportLeg,
            };
        internal static void ClassifyDomainExecution(ExecutionResult execution, MissionStepResult outcome)
        {
            if (ExecutionClassifiers.TryGetValue(outcome.MissionKind, out var classify)) classify(execution, outcome);
            else MissionStepResultPolicy.ClassifyDefaultExecution(execution, outcome);
        }
        internal static bool TryClassifySatisfiedDomainLeg(MissionStepResult outcome) =>
            outcome.MissionKind == MissionKind.Raid && RaidContinuity.TryClassifySatisfiedRaidLeg(outcome);
        internal static void ClassifyInvalidatedDomainTarget(MissionStepResult outcome)
        {
            if (InvalidatedTargetContinues.TryGetValue(outcome.MissionKind, out var continues)
                && continues(outcome))
                outcome.Disposition = MissionStepDisposition.Waiting;
            else
                outcome.Fail();
        }
        // Ordered compatibility bindings retain original side-leg/completion/payload precedence.
        // A handled completed leg may keep a durable operation; only domain retirement removes it.
        private delegate bool OutcomeTransition(MissionIntentState state, AiAllocatorState allocator,
            MissionIntent intent, MissionStepResult result, int turn);
        private static readonly OutcomeTransition[] SideLegTransitions =
            { AttackContinuity.TryHandleAttackSideLeg, GroundCombatContinuity.TryHandleInvalidGroundSupport };
        private static readonly OutcomeTransition[] CompletionTransitions =
            { RaidContinuity.TryCompleteRaidTarget, AttackContinuity.TryCompleteAttackLeg, ReconContinuity.TryContinueScoutWaypoint };
        private static readonly OutcomeTransition[] RecoveryTransitions = { EconomyContinuity.TryKeepEconomyRecovery };
        private static readonly OutcomeObservation[] RetirementPreparers = { EconomyContinuity.PrepareEconomyRetirement };
        private static readonly OutcomeTransition[] NoProgressTransitions = { EconomyContinuity.TryHandleEconomyNoProgress };
        private static readonly OutcomeTransition[] CreationTransitions =
            { ReconContinuity.TryCreateScoutStep, RaidContinuity.TryCreateRaidStep, AttackContinuity.TryCreateAttackStep, ActiveDefenceContinuity.TryCreateActiveDefenceStep,
                EconomyContinuity.TryCreateEconomyStep, DevelopmentContinuity.TryCreateDevelopmentStep };
        private static readonly Action<MissionIntentState, MissionStepResult, int>[] ProgressRecorders =
            { EconomyContinuity.RecordEconomyStepProgress };
        private static void RecordDomainStepProgress(MissionIntentState state, MissionStepResult result, int turn)
        {
            foreach (var record in ProgressRecorders) record(state, result, turn);
        }
        private static bool TryDomainTransition(IEnumerable<OutcomeTransition> handlers, MissionIntentState state,
            AiAllocatorState allocator, MissionIntent intent, MissionStepResult result, int turn)
        {
            foreach (var handle in handlers)
                if (handle(state, allocator, intent, result, turn)) return true;
            return false;
        }
        private delegate void OutcomeObservation(MissionIntentState state, AiAllocatorState allocator,
            MissionIntent intent, MissionStepResult result, int turn);
        private static readonly OutcomeObservation[] BeforeMoverObservers = { ReconContinuity.ObserveReconMover };
        private static readonly OutcomeTransition[] MoverTransitions =
            { RaidContinuity.TryApplyRaidMoverFacts, AttackContinuity.TryPreserveAttackSupportMover, TryPreservePinnedDomainMover };
        private static readonly OutcomeObservation[] IntentFactObservers =
            { ReconContinuity.ApplyScoutStepFacts, AttackContinuity.ApplyAttackStepFacts, RaidContinuity.ApplyRaidStepFacts, EconomyContinuity.ApplyEconomyStepFacts };
        private static readonly OutcomeTransition[] CapabilityFailureTransitions =
            { EconomyContinuity.TryHandleEconomyCapabilityFailure };

        // Existing durable pinning rule, composed here; generic lease storage knows no mission kind.
        private static bool TryPreservePinnedDomainMover(MissionIntentState state, AiAllocatorState allocator,
            MissionIntent intent, MissionStepResult result, int turn)
        {
            if ((intent.Kind != MissionKind.Economy && intent.Kind != MissionKind.Development)
                || !intent.PreferredMoverArmyId.HasValue
                || intent.PreferredMoverArmyId == result.MoverArmyId)
                return false;
            // The pinned garrison was a materialization source, not the departing builder.
            // Only this operation's successful extraction may replace it. A failed/stale step,
            // an ordinary composition change or a different source cannot steal a pinned actor.
            EconomyStepPayload facts = result.GetPayload<EconomyStepPayload>();
            bool extractedBuilder = intent.Kind == MissionKind.Economy && result.MadeProgress
                && facts?.HasEconomyPayload == true
                && facts.MaterializationSourceArmyId == intent.PreferredMoverArmyId
                && result.IntentKey.Equals(intent.IntentKey)
                && intent.Economy != null
                && facts.EconomyTarget.Kind == intent.Economy.Kind
                && facts.EconomyTarget.TargetHex.Equals(intent.Economy.TargetHex);
            return !extractedBuilder;
        }

        // Same capability-failure exceptions as the former two expressions; Raid ages only for reap.
        private static bool DomainAgesCapabilityFailure(MissionIntent intent, bool forReaping) =>
            intent.Kind == MissionKind.Development || forReaping && intent.Kind == MissionKind.Raid
                || ReconContinuity.IsMoverlessScoutRole(intent) || EconomyContinuity.IsCollectorEconomyIntent(intent);

        // One derived, pass-scoped workspace. Domain handlers mutate the existing durable owner;
        // this object owns only deferred retire/rekey ordering and normalized actor availability.
        internal sealed class ActiveResolution
        {
            internal readonly MissionIntentState State;
            internal readonly List<MissionIntent> Active = new List<MissionIntent>();
            internal readonly List<MissionIntentKey> Dead = new List<MissionIntentKey>();
            internal readonly List<(MissionIntentKey Old, MissionIntent Intent)> Rekeys =
                new List<(MissionIntentKey, MissionIntent)>();
            internal readonly HashSet<MissionIntentKey> DonatedOperations = new HashSet<MissionIntentKey>();
            internal readonly HashSet<Game.HexGrid.HexCoord> ScoutFoci = new HashSet<Game.HexGrid.HexCoord>();
            internal readonly IReadOnlyList<ReconObjective> ReconObjectives;
            internal readonly Func<Game.HexGrid.HexCoord, Game.HexGrid.HexCoord, int, int> SafeRouteCost;
            internal HashSet<int> ActorClaims;
            internal HashSet<int> AttackGatherUnavailable;
            internal ActiveResolution(MissionIntentState state, IReadOnlyList<ReconObjective> objectives,
                Func<Game.HexGrid.HexCoord, Game.HexGrid.HexCoord, int, int> routeCost)
            { State = state; ReconObjectives = objectives; SafeRouteCost = routeCost; }
        }
        private delegate void ResolutionPreparation(Game.Players.PlayerSetupData player,
            WorldSnapshot snapshot, ActiveResolution pass);
        private delegate void ActiveResolver(Game.Players.PlayerSetupData player,
            WorldSnapshot snapshot, MissionIntent intent, ActiveResolution pass);
        private static readonly ResolutionPreparation[] ResolutionPreparers =
            { EconomyContinuity.RepairEconomyLoans, AttackContinuity.RetireGatherDonors, ReconContinuity.CollectScoutFoci };
        private static readonly ResolutionPreparation[] ResolutionFinalizers =
            { ReconContinuity.FinalizeReconResolution, GroundCombatContinuity.FinalizeAirSupportResolution };
        private static readonly IReadOnlyDictionary<MissionKind, ActiveResolver> ActiveResolvers =
            new Dictionary<MissionKind, ActiveResolver>
            {
                [MissionKind.Development] = DevelopmentContinuity.ResolveDevelopmentOperation,
                [MissionKind.Economy] = EconomyContinuity.ResolveEconomyOperation,
                [MissionKind.ActiveDefence] = ActiveDefenceContinuity.ResolveDefenceOperation,
                [MissionKind.Attack] = AttackContinuity.ResolveAttackOperation,
                [MissionKind.Raid] = RaidContinuity.ResolveRaidOperation,
            };

        private static readonly IReadOnlyDictionary<MissionKind, Action<ProvisionedMission, MissionStepResult>>
            ProvisionFactReaders = new Dictionary<MissionKind, Action<ProvisionedMission, MissionStepResult>>
            {
                [MissionKind.Raid] = RaidContinuity.CaptureRaidProvisionFacts,
                [MissionKind.Attack] = AttackContinuity.CaptureAttackProvisionFacts,
                [MissionKind.ActiveDefence] = ActiveDefenceContinuity.CaptureActiveDefenceProvisionFacts,
                [MissionKind.Economy] = EconomyContinuity.CaptureEconomyProvisionFacts,
                [MissionKind.Development] = DevelopmentContinuity.CaptureDevelopmentProvisionFacts,
            };
        private static readonly IReadOnlyDictionary<MissionKind, Action<ExecutionResult, MissionStepResult>>
            ExecutionFactReaders = new Dictionary<MissionKind, Action<ExecutionResult, MissionStepResult>>
            {
                [MissionKind.Scout] = ReconContinuity.CaptureScoutExecutionFacts,
                [MissionKind.Raid] = RaidContinuity.CaptureRaidExecutionFacts,
                [MissionKind.Attack] = AttackContinuity.CaptureAttackExecutionFacts,
                [MissionKind.Economy] = EconomyContinuity.CaptureEconomyExecutionFacts,
                [MissionKind.Development] = DevelopmentContinuity.CaptureDevelopmentExecutionFacts,
            };
        internal static void CaptureProvisionFacts(ProvisionedMission provisioned, MissionStepResult result)
        {
            if (ProvisionFactReaders.TryGetValue(provisioned.Kind, out var capture)) capture(provisioned, result);
            else ReconContinuity.CaptureScoutProvisionFacts(provisioned, result);
        }
        internal static void CaptureExecutionFacts(ExecutionResult execution, MissionStepResult result)
        {
            GroundCombatContinuity.CaptureGroundHandoffFacts(execution, result);
            if (ExecutionFactReaders.TryGetValue(result.MissionKind, out var capture)) capture(execution, result);
        }
    }
}
