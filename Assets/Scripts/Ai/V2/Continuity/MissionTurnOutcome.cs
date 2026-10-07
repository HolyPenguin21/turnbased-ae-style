using Game.HexGrid;

namespace Game.Ai.V2
{
    // Legacy result API: domain fields are projections into typed payloads, never copies.
    public enum ExecutionOutcome { Completed, ProductiveStop, Blocked, Failed }

    public sealed class MissionTurnOutcome : MissionStepResult
    {
        public MissionTurnOutcome() { }
        private MissionTurnOutcome(MissionStepResult source) : base(source) { }
        // Temporary domain-policy adapter: shares common facts and typed payloads by reference.
        internal static MissionTurnOutcome View(MissionStepResult result) =>
            result as MissionTurnOutcome ?? new MissionTurnOutcome(result);

        public ExecutionOutcome Outcome
        {
            get => Disposition == MissionStepDisposition.Completed ? ExecutionOutcome.Completed
                : Disposition == MissionStepDisposition.Progress ? ExecutionOutcome.ProductiveStop
                : Disposition == MissionStepDisposition.Waiting || Disposition == MissionStepDisposition.Replan
                    ? ExecutionOutcome.Blocked : ExecutionOutcome.Failed;
            set => Disposition = value == ExecutionOutcome.Completed ? MissionStepDisposition.Completed
                : value == ExecutionOutcome.ProductiveStop ? MissionStepDisposition.Progress
                : value == ExecutionOutcome.Blocked ? MissionStepDisposition.Waiting
                : Disposition == MissionStepDisposition.PermanentFailure
                    ? MissionStepDisposition.PermanentFailure : MissionStepDisposition.Invalidated;
        }
        public bool StructuralFailure
        {
            get => Disposition == MissionStepDisposition.PermanentFailure;
            set
            {
                if (value) Disposition = MissionStepDisposition.PermanentFailure;
                else if (Disposition == MissionStepDisposition.PermanentFailure)
                    Disposition = MissionStepDisposition.Invalidated;
            }
        }
        public ScoutTargetKind ScoutKind
        {
            get => GetPayload<ReconStepPayload>()?.ScoutKind ?? default(ScoutTargetKind);
            set => PayloadForWrite<ReconStepPayload>().ScoutKind = value;
        }
        public bool ScoutRequiresStealth
        {
            get => GetPayload<ReconStepPayload>()?.ScoutRequiresStealth ?? default(bool);
            set => PayloadForWrite<ReconStepPayload>().ScoutRequiresStealth = value;
        }   // AI-RECON-02 — provisioned Scout requirement was a stealth one
        public HexCoord FocusHex
        {
            get => GetPayload<ReconStepPayload>()?.FocusHex ?? default(HexCoord);
            set => PayloadForWrite<ReconStepPayload>().FocusHex = value;
        }
        public bool HasScoutPayload
        {
            get => GetPayload<ReconStepPayload>()?.HasScoutPayload ?? default(bool);
            set => PayloadForWrite<ReconStepPayload>().HasScoutPayload = value;
        }
        public bool HasRaidPayload
        {
            get => GetPayload<RaidStepPayload>()?.HasRaidPayload ?? default(bool);
            set => PayloadForWrite<RaidStepPayload>().HasRaidPayload = value;
        }
        // Single source of truth for the target of this outcome. RaidTargetArmyId below is a
        // read-only projection for existing non-Raid/logging readers — never a second settable copy.
        public RaidTargetRef RaidTarget
        {
            get => GetPayload<RaidStepPayload>()?.RaidTarget ?? default(RaidTargetRef);
            set => PayloadForWrite<RaidStepPayload>().RaidTarget = value;
        }
        public int RaidTargetArmyId => RaidTarget.Kind == RaidTargetKind.NeutralArmy ? RaidTarget.ArmyId : 0;
        public HexCoord RaidLastKnownHex
        {
            get => GetPayload<RaidStepPayload>()?.RaidLastKnownHex ?? default(HexCoord);
            set => PayloadForWrite<RaidStepPayload>().RaidLastKnownHex = value;
        }
        public bool RaidTargetIsNeutral
        {
            get => GetPayload<RaidStepPayload>()?.RaidTargetIsNeutral ?? default(bool);
            set => PayloadForWrite<RaidStepPayload>().RaidTargetIsNeutral = value;
        }
        public bool OperationStarted
        {
            get => GetPayload<GroundCombatStepPayload>()?.OperationStarted ?? default(bool);
            set => PayloadForWrite<GroundCombatStepPayload>().OperationStarted = value;
        }
        // Exact provisioned leg/actors plus the execution-time handoff boundary. Continuity uses
        // these immutable facts instead of inspecting RaidIntent.Phase after Execution may already
        // have advanced it.
        public RaidMissionPhase RaidPhase
        {
            get => GetPayload<RaidStepPayload>()?.RaidPhase ?? default(RaidMissionPhase);
            set => PayloadForWrite<RaidStepPayload>().RaidPhase = value;
        }
        public int? RaidPrimaryArmyId
        {
            get => GetPayload<RaidStepPayload>()?.RaidPrimaryArmyId ?? default(int?);
            set => PayloadForWrite<RaidStepPayload>().RaidPrimaryArmyId = value;
        }
        public int? RaidSupportArmyId
        {
            get => GetPayload<RaidStepPayload>()?.RaidSupportArmyId ?? default(int?);
            set => PayloadForWrite<RaidStepPayload>().RaidSupportArmyId = value;
        }
        public int? RaidAirSupportArmyId
        {
            get => GetPayload<RaidStepPayload>()?.RaidAirSupportArmyId ?? default(int?);
            set => PayloadForWrite<RaidStepPayload>().RaidAirSupportArmyId = value;
        }
        public HexCoord? RaidAirSupportLandingHex
        {
            get => GetPayload<RaidStepPayload>()?.RaidAirSupportLandingHex ?? default(HexCoord?);
            set => PayloadForWrite<RaidStepPayload>().RaidAirSupportLandingHex = value;
        }
        public bool RaidAirSupportStrikeSucceeded
        {
            get => GetPayload<RaidStepPayload>()?.RaidAirSupportStrikeSucceeded ?? default(bool);
            set => PayloadForWrite<RaidStepPayload>().RaidAirSupportStrikeSucceeded = value;
        }
        public bool ReinforcementHandoffAttempted
        {
            get => GetPayload<GroundCombatStepPayload>()?.ReinforcementHandoffAttempted ?? default(bool);
            set => PayloadForWrite<GroundCombatStepPayload>().ReinforcementHandoffAttempted = value;
        }
        public RaidRefitAction RaidRefitAction
        {
            get => GetPayload<RaidStepPayload>()?.RaidRefitAction ?? default(RaidRefitAction);
            set => PayloadForWrite<RaidStepPayload>().RaidRefitAction = value;
        }
        public bool RaidRefitSucceeded
        {
            get => GetPayload<RaidStepPayload>()?.RaidRefitSucceeded ?? default(bool);
            set => PayloadForWrite<RaidStepPayload>().RaidRefitSucceeded = value;
        }
        public ResourceVector RaidResourcesSpent
        {
            get => GetPayload<RaidStepPayload>()?.RaidResourcesSpent ?? default(ResourceVector);
            set => PayloadForWrite<RaidStepPayload>().RaidResourcesSpent = value;
        }
        public bool HasAttackPayload
        {
            get => GetPayload<AttackStepPayload>()?.HasAttackPayload ?? default(bool);
            set => PayloadForWrite<AttackStepPayload>().HasAttackPayload = value;
        }
        // ATK §69 — the whole provisioned Attack leg, carried as ONE frozen object so Continuity
        // reads immutable execution facts instead of inspecting an intent Execution may already
        // have advanced.
        public AttackMissionTarget AttackTarget
        {
            get => GetPayload<AttackStepPayload>()?.AttackTarget ?? default(AttackMissionTarget);
            set => PayloadForWrite<AttackStepPayload>().AttackTarget = value;
        }
        // ATK §17 — this Attack actually spent its one opportunistic side strike this step.
        public bool AttackOpportunisticStrike
        {
            get => GetPayload<AttackStepPayload>()?.AttackOpportunisticStrike ?? default(bool);
            set => PayloadForWrite<AttackStepPayload>().AttackOpportunisticStrike = value;
        }
        public bool AttackIntermediateCaptured
        {
            get => GetPayload<AttackStepPayload>()?.AttackIntermediateCaptured ?? default(bool);
            set => PayloadForWrite<AttackStepPayload>().AttackIntermediateCaptured = value;
        }
        public bool AttackCaptureHadBattle
        {
            get => GetPayload<AttackStepPayload>()?.AttackCaptureHadBattle ?? default(bool);
            set => PayloadForWrite<AttackStepPayload>().AttackCaptureHadBattle = value;
        }
        public bool HasActiveDefencePayload
        {
            get => GetPayload<ActiveDefenceStepPayload>()?.HasActiveDefencePayload ?? default(bool);
            set => PayloadForWrite<ActiveDefenceStepPayload>().HasActiveDefencePayload = value;
        }
        public ActiveDefenceMissionTarget ActiveDefenceTarget
        {
            get => GetPayload<ActiveDefenceStepPayload>()?.ActiveDefenceTarget ?? default(ActiveDefenceMissionTarget);
            set => PayloadForWrite<ActiveDefenceStepPayload>().ActiveDefenceTarget = value;
        }
        public bool HasEconomyPayload
        {
            get => GetPayload<EconomyStepPayload>()?.HasEconomyPayload ?? default(bool);
            set => PayloadForWrite<EconomyStepPayload>().HasEconomyPayload = value;
        }
        public EconomyMissionTarget EconomyTarget
        {
            get => GetPayload<EconomyStepPayload>()?.EconomyTarget ?? default(EconomyMissionTarget);
            set => PayloadForWrite<EconomyStepPayload>().EconomyTarget = value;
        }
        public bool HasDevelopmentPayload
        {
            get => GetPayload<DevelopmentStepPayload>()?.HasDevelopmentPayload ?? default(bool);
            set => PayloadForWrite<DevelopmentStepPayload>().HasDevelopmentPayload = value;
        }
        public DevelopmentMissionTarget DevelopmentTarget
        {
            get => GetPayload<DevelopmentStepPayload>()?.DevelopmentTarget ?? default(DevelopmentMissionTarget);
            set => PayloadForWrite<DevelopmentStepPayload>().DevelopmentTarget = value;
        }
        public bool EconomyBuildCompleted
        {
            get => GetPayload<EconomyStepPayload>()?.EconomyBuildCompleted ?? default(bool);
            set => PayloadForWrite<EconomyStepPayload>().EconomyBuildCompleted = value;
        }
        public MissionIntentKey? EconomyLoanSource
        {
            get => GetPayload<EconomyStepPayload>()?.EconomyLoanSource ?? default(MissionIntentKey?);
            set => PayloadForWrite<EconomyStepPayload>().EconomyLoanSource = value;
        }
    }

}
