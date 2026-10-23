using System;
using System.Collections.Generic;

namespace Game.Ai.V2
{
    // Domain composition only. Generic result/lease storage never implements these rules.
    internal static partial class MissionContinuityLayer
    {
        // ONE registration per mission kind: every per-kind continuity hook in one place, so a new
        // kind is wired here once and a missing kind fails loudly instead of being read as Scout.
        // Required hooks: LiveObjective, Resolve, ProvisionFacts. A null optional hook means the
        // kind has no rule of its own there (default classification / no facts / a hard failure).
        private delegate void ActiveResolver(Game.Players.PlayerSetupData player,
            WorldSnapshot snapshot, MissionIntent intent, ActiveResolution pass);
        private sealed class KindHooks
        {
            internal Func<Game.Players.PlayerSetupData, ProvisionedMission, bool> LiveObjective;
            internal ActiveResolver Resolve;
            internal Action<ProvisionedMission, MissionStepResult> ProvisionFacts;
            internal Action<ExecutionResult, MissionStepResult> ClassifyExecution;
            internal Action<ExecutionResult, MissionStepResult> ExecutionFacts;
            internal Func<MissionStepResult, bool> InvalidatedTargetContinues;
        }
        private static readonly IReadOnlyDictionary<MissionKind, KindHooks> Kinds =
            new Dictionary<MissionKind, KindHooks>
            {
                [MissionKind.Scout] = new KindHooks
                {
                    LiveObjective = ReconContinuity.IsScoutStepObjectiveSatisfiedLive,
                    Resolve = ReconContinuity.ResolveScoutOperation,
                    ProvisionFacts = ReconContinuity.CaptureScoutProvisionFacts,
                    ClassifyExecution = ReconContinuity.ClassifyScoutStep,
                    ExecutionFacts = ReconContinuity.CaptureScoutExecutionFacts,
                    InvalidatedTargetContinues = _ => true,
                },
                [MissionKind.Raid] = new KindHooks
                {
                    LiveObjective = RaidContinuity.IsRaidStepObjectiveSatisfiedLive,
                    Resolve = RaidContinuity.ResolveRaidOperation,
                    ProvisionFacts = RaidContinuity.CaptureRaidProvisionFacts,
                    ClassifyExecution = RaidContinuity.ClassifyRaidStep,
                    ExecutionFacts = RaidContinuity.CaptureRaidExecutionFacts,
                    InvalidatedTargetContinues = GroundCombatLegs.IsSupportLeg,
                },
                [MissionKind.Attack] = new KindHooks
                {
                    LiveObjective = AttackContinuity.IsAttackStepObjectiveSatisfiedLive,
                    Resolve = AttackContinuity.ResolveAttackOperation,
                    ProvisionFacts = AttackContinuity.CaptureAttackProvisionFacts,
                    ClassifyExecution = AttackContinuity.ClassifyAttackStep,
                    ExecutionFacts = AttackContinuity.CaptureAttackExecutionFacts,
                    InvalidatedTargetContinues = GroundCombatLegs.IsSupportLeg,
                },
                [MissionKind.ActiveDefence] = new KindHooks
                {
                    LiveObjective = ActiveDefenceContinuity.IsActiveDefenceStepObjectiveSatisfiedLive,
                    Resolve = ActiveDefenceContinuity.ResolveDefenceOperation,
                    ProvisionFacts = ActiveDefenceContinuity.CaptureActiveDefenceProvisionFacts,
                },
                [MissionKind.Economy] = new KindHooks
                {
                    LiveObjective = EconomyContinuity.IsEconomyStepObjectiveSatisfiedLive,
                    Resolve = EconomyContinuity.ResolveEconomyOperation,
                    ProvisionFacts = EconomyContinuity.CaptureEconomyProvisionFacts,
                    ClassifyExecution = EconomyContinuity.ClassifyEconomyStep,
                    ExecutionFacts = EconomyContinuity.CaptureEconomyExecutionFacts,
                },
                [MissionKind.Development] = new KindHooks
                {
                    LiveObjective = DevelopmentContinuity.IsDevelopmentStepObjectiveSatisfiedLive,
                    Resolve = DevelopmentContinuity.ResolveDevelopmentOperation,
                    ProvisionFacts = DevelopmentContinuity.CaptureDevelopmentProvisionFacts,
                    ClassifyExecution = DevelopmentContinuity.ClassifyDevelopmentStep,
                    ExecutionFacts = DevelopmentContinuity.CaptureDevelopmentExecutionFacts,
                },
            };
        internal static bool HasContinuity(MissionKind kind) =>
            Kinds.TryGetValue(kind, out KindHooks h)
            && h.LiveObjective != null && h.Resolve != null && h.ProvisionFacts != null;
        private static KindHooks HooksFor(MissionKind kind) =>
            Kinds.TryGetValue(kind, out KindHooks h) ? h
                : throw new InvalidOperationException($"MissionKind {kind} has no continuity registration");

        internal static bool IsStepObjectiveSatisfiedLive(Game.Players.PlayerSetupData player, ProvisionedMission mission) =>
            HooksFor(mission.Kind).LiveObjective(player, mission);
        internal static void ClassifyDomainExecution(ExecutionResult execution, MissionStepResult outcome)
        {
            var classify = HooksFor(outcome.MissionKind).ClassifyExecution;
            if (classify != null) classify(execution, outcome);
            else MissionStepResultPolicy.ClassifyDefaultExecution(execution, outcome);
        }
        internal static bool TryClassifySatisfiedDomainLeg(MissionStepResult outcome) =>
            outcome.MissionKind == MissionKind.Raid && RaidContinuity.TryClassifySatisfiedRaidLeg(outcome);
        internal static void ClassifyInvalidatedDomainTarget(MissionStepResult outcome)
        {
            var continues = HooksFor(outcome.MissionKind).InvalidatedTargetContinues;
            if (continues != null && continues(outcome))
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
            internal readonly Game.Ai.AiTurnContext Context;
            internal HashSet<int> ActorClaims;
            internal HashSet<int> AttackGatherUnavailable;
            internal ActiveResolution(MissionIntentState state, IReadOnlyList<ReconObjective> objectives,
                Func<Game.HexGrid.HexCoord, Game.HexGrid.HexCoord, int, int> routeCost,
                Game.Ai.AiTurnContext context = null)
            { State = state; ReconObjectives = objectives; SafeRouteCost = routeCost; Context = context; }
        }
        private delegate void ResolutionPreparation(Game.Players.PlayerSetupData player,
            WorldSnapshot snapshot, ActiveResolution pass);
        private static readonly ResolutionPreparation[] ResolutionPreparers =
            { EconomyContinuity.RepairEconomyLoans, EconomyContinuity.RecoverEndedOutboundBuilders,
                AttackContinuity.RetireGatherDonors, ReconContinuity.CollectScoutFoci };
        private static readonly ResolutionPreparation[] ResolutionFinalizers =
            { ReconContinuity.FinalizeReconResolution, GroundCombatContinuity.FinalizeAirSupportResolution };
        private static void ResolveDomainOperation(Game.Players.PlayerSetupData player, WorldSnapshot snapshot,
            MissionIntent intent, ActiveResolution pass) =>
            HooksFor(intent.Kind).Resolve(player, snapshot, intent, pass);

        internal static void CaptureProvisionFacts(ProvisionedMission provisioned, MissionStepResult result) =>
            HooksFor(provisioned.Kind).ProvisionFacts(provisioned, result);
        internal static void CaptureExecutionFacts(ExecutionResult execution, MissionStepResult result)
        {
            GroundCombatContinuity.CaptureGroundHandoffFacts(execution, result);
            HooksFor(result.MissionKind).ExecutionFacts?.Invoke(execution, result);
        }
    }
}
