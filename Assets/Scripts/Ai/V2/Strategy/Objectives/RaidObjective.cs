using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.HexGrid;
using Game.Players;
using UnityEngine;
using Game.Combat;

namespace Game.Ai.V2
{
    // ATK §39 — the three objective families inside the ONE Aggression axis. Raid owns neutral
    // armies / event guards, ActiveDefence owns an enemy army threatening our own asset, Attack
    // owns the deliberate assault on a known hostile Base/Citadel (captured). Enumeration of each family
    // lives in its own evaluator at this same level; this enum is the shared vocabulary.
    public enum AggressionObjectiveKind { Raid, ActiveDefence, Attack }

    public enum RaidMissionPhase
    {
        Assault = 0,
        Reinforcement = 1,
        SupportReturn = 2,
        Return = 3,
        AirSupport = 4,
        RecoveryReturn = 5,
    }

    public enum RaidRefitActionKind
    {
        None = 0,
        RepairUnit = 1,
        TransferUnit = 2,
        SwapUnit = 3,
    }

    // Frozen, snapshot-derived instruction for exactly one bounded Refit mutation. Execution must
    // re-resolve every runtime identity and may never silently substitute a different candidate.
    public struct RaidRefitAction
    {
        public RaidRefitActionKind Kind;
        public int PrimaryArmyId;
        public int? DonorArmyId;
        public int UnitRuntimeId;
        public int DisplacedUnitRuntimeId;
        public HexCoord BaseHex;
        public int ApCost;
        public ResourceVector ResourceCost;
        public float WinChanceBefore;
        public float WinChanceAfter;

        public bool HasValue => Kind != RaidRefitActionKind.None && UnitRuntimeId > 0;
    }

    public sealed class RaidObjective
    {
        public AggressionObjectiveKind Kind;
        public RaidTargetRef Target;
        public HexCoord LastKnownHex;
        public PlayerSetupData TargetOwner;
        public bool TargetIsNeutral;
        // Legacy transport during migration. Intrinsic objective value is TaskScore.Value.
        public float BaseValue;
        public TaskScore TaskScore;
        public float Confidence;

        public int TargetArmyId => Target.Kind == RaidTargetKind.NeutralArmy ? Target.ArmyId : 0;

        public float ReadyWinChance;
        public float AssemblableWinChance;
        public bool CanCoverAllDefenders;
        public int EstimatedEta;
        public int DefenderCount;
        public float TargetPower;
        public bool GatePassed;
        public bool NeedsCombatPower;
        public bool NeedsHero;
        public float CombatPowerDeficit;

        public string ObjectiveId => $"Raid#{Target.DiagnosticLabel}";
        public MissionIntentKey IntentKey => MissionIntentKey.ForRaid(Target);

        public RaidMissionTarget ToTarget() => new RaidMissionTarget
        {
            Target = Target,
            LastKnownHex = LastKnownHex,
            TargetOwner = TargetOwner,
            TargetIsNeutral = TargetIsNeutral,
            Confidence = Confidence,
            ReadyWinChance = ReadyWinChance,
            AssemblableWinChance = AssemblableWinChance,
            CanCoverAllDefenders = CanCoverAllDefenders,
            DefenderCount = DefenderCount,
            TargetPower = TargetPower,
            EstimatedEta = EstimatedEta,
        };
    }

    public struct RaidMissionTarget
    {
        public RaidMissionPhase Phase;
        public int? PrimaryArmyId;
        public int? SupportArmyId;
        public int? AirSupportArmyId;
        public HexCoord? AirSupportLandingHex;
        public HexCoord DestinationHex;
        public RaidTargetRef Target;
        public HexCoord LastKnownHex;
        public PlayerSetupData TargetOwner;
        public bool TargetIsNeutral;
        public float Confidence;
        public float ReadyWinChance;
        public float AssemblableWinChance;
        public bool CanCoverAllDefenders;
        public int DefenderCount;
        public float TargetPower;
        public int EstimatedEta;
        public int AirSupportAttemptedTurn;
        public bool AirSupportStrikeSucceeded;
        public RaidRefitAction RefitAction;

        public int TargetArmyId => Target.Kind == RaidTargetKind.NeutralArmy ? Target.ArmyId : 0;
    }

}
