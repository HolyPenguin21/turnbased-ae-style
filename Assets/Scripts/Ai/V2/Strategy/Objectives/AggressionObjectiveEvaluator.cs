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
    // owns the deliberate assault on a known hostile structure: a Base/Citadel (captured) or a
    // Facility (destroyed). Enumeration of each family
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

    public sealed class AggressionObjective
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

    public static class AggressionObjectiveEvaluator
    {
        public static List<AggressionObjective> Enumerate(WorldSnapshot snap, CombatOpportunityReport report)
        {
            var list = new List<AggressionObjective>();
            if (snap?.Self == null)
            {
                AiDebugLog.Write("[AI][V2][AggressionObjective] decision=NONE reason=no_self_snapshot");
                return list;
            }
            IReadOnlyList<CombatOpportunity> candidates = report?.NeutralOpportunities
                ?? (IReadOnlyList<CombatOpportunity>)System.Array.Empty<CombatOpportunity>();
            if (candidates.Count == 0)
            {
                AiDebugLog.Write("[AI][V2][AggressionObjective] decision=NONE reason=no_known_neutral_army_opportunities");
                return list;
            }

            foreach (CombatOpportunity o in candidates)
            {
                if (!o.TargetIsNeutral)
                {
                    AiDebugLog.WriteDeduped(o.Target.DiagnosticLabel,
                        $"[AI][V2][AggressionObjective] decision=REJECT target={o.Target.DiagnosticLabel} reason=target_is_not_neutral");
                    continue;
                }
                if (!o.HasTarget || !o.Target.HasValue)
                {
                    AiDebugLog.WriteDeduped("None",
                        "[AI][V2][AggressionObjective] decision=REJECT target=None reason=opportunity_has_no_target");
                    continue;
                }

                AggressionObjective obj = Build(snap, report, o);
                if (obj.BaseValue < AiConfigV2.raidObjectiveMinBaseValue)
                {
                    // Re-evaluated every cycle for every below-threshold candidate — WriteDeduped
                    // keeps the reject reason visible without reprinting an unchanged value each time.
                    AiDebugLog.WriteDeduped(obj.Target.DiagnosticLabel,
                        $"[AI][V2][AggressionObjective] decision=REJECT target={obj.Target.DiagnosticLabel} "
                        + $"reason=task_value_below_threshold value={F(obj.BaseValue)} min={F(AiConfigV2.raidObjectiveMinBaseValue)}");
                    continue;
                }

                list.Add(obj);
                string frozenGap = !obj.CanCoverAllDefenders ? "coverage"
                    : obj.NeedsCombatPower ? "assemblability"
                    : obj.NeedsHero ? "hero_availability" : "none";
                AiDebugLog.WriteDeduped(obj.Target.DiagnosticLabel,
                    $"[AI][V2][AggressionObjective] decision=ACCEPT target={obj.Target.DiagnosticLabel} "
                    + $"hex=({obj.LastKnownHex.Q},{obj.LastKnownHex.R}) task={F(obj.BaseValue)} "
                    + $"readyWin={F(obj.ReadyWinChance)} asmWin={F(obj.AssemblableWinChance)} "
                    + $"cover={(obj.CanCoverAllDefenders ? 1 : 0)} gate={(obj.GatePassed ? 1 : 0)} "
                    + $"defenders={obj.DefenderCount} frozenNeedsPower={(obj.NeedsCombatPower ? 1 : 0)} "
                    + $"frozenNeedsHero={(obj.NeedsHero ? 1 : 0)} frozenPowerDeficit={F(obj.CombatPowerDeficit)} "
                    + $"frozenGap={frozenGap}");
            }

            list.Sort((a, b) =>
            {
                int c = b.BaseValue.CompareTo(a.BaseValue);
                return c != 0 ? c : string.CompareOrdinal(a.Target.DiagnosticLabel, b.Target.DiagnosticLabel);
            });
            return list;
        }

        public static AggressionObjective ForTrackedArmy(WorldSnapshot snap, CombatOpportunityReport report,
            int trackedArmyId) => ForTrackedTarget(snap, report, RaidTargetRef.ForNeutralArmy(trackedArmyId));

        public static AggressionObjective ForTrackedTarget(WorldSnapshot snap, CombatOpportunityReport report,
            RaidTargetRef target)
        {
            if (report?.All == null || !target.HasValue)
                return null;
            foreach (CombatOpportunity o in report.All)
                if (o.HasTarget && o.TargetIsNeutral && o.Target.Equals(target))
                    return Build(snap, report, o);
            return null;
        }

        private static AggressionObjective Build(WorldSnapshot snap, CombatOpportunityReport report,
            CombatOpportunity o)
        {
            // Defender power is a combat-difficulty fact, not an expected resource/card reward.
            // Both neutral-army and guarded-event Raid objectives receive exactly one fixed
            // intrinsic reward. Combat difficulty remains with WorthIt and assembly.
            // Raid targets are stationary neutrals or event guards; older sightings do not move them.
            // Keep shared StaleIntelPenalty for future attacks on mobile player armies.
            int homeDistance = TaskScoreEvaluator.NearestOwnedHomeDistance(snap, o.TargetHex);
            var score = new TaskScore(
                ownTerritoryProximity: TaskScoreEvaluator.OwnTerritoryProximity(homeDistance),
                militaryTargetRelevance: AiConfigV2.RaidReward);

            bool readyViable = o.CanCoverAllDefenders
                && o.ReadyWinChance >= AiConfigV2.raidMinViableWinChance;
            bool assemblableViable = o.CanCoverAllDefenders
                && o.AssemblableWinChance >= AiConfigV2.raidMinViableWinChance;
            bool haveViable = o.GatePassed || readyViable || assemblableViable;
            bool needsHero = !haveViable && !report.HeroAvailable;
            bool needsCombatPower = !haveViable;

            IReadOnlyList<WorthIt.DefenderProfile> knownDefenders = AiV2Util.KnownDefenders(snap, o.Target);
            float targetPower = AiPower.EffectiveArmyPowerFromProfiles(knownDefenders);
            float requiredPower = GroundCombatFeasibility.RequiredPower(knownDefenders,
                AiV2Util.KnownRaidDefenceBonus(snap, o.Target));
            float deficit = needsCombatPower ? Mathf.Max(1f, requiredPower - snap.Self.FieldPower) : 0f;

            return new AggressionObjective
            {
                Kind = AggressionObjectiveKind.Raid,
                Target = o.Target,
                LastKnownHex = o.TargetHex,
                TargetOwner = o.TargetOwner,
                TargetIsNeutral = o.TargetIsNeutral,
                TaskScore = score,
                BaseValue = score.Value,
                Confidence = o.Confidence,
                ReadyWinChance = o.ReadyWinChance,
                AssemblableWinChance = o.AssemblableWinChance,
                CanCoverAllDefenders = o.CanCoverAllDefenders,
                EstimatedEta = o.Eta,
                DefenderCount = o.DefenderCount,
                TargetPower = targetPower,
                GatePassed = o.GatePassed,
                NeedsCombatPower = needsCombatPower,
                NeedsHero = needsHero,
                CombatPowerDeficit = deficit,
            };
        }

        private static string F(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    }
}
