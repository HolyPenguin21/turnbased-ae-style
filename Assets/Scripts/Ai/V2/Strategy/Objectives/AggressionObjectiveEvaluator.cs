using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.HexGrid;
using Game.Players;
using UnityEngine;

using Game.Combat;

namespace Game.Ai.V2
{
    public enum AggressionObjectiveKind
    {
        Raid,
    }

    // AGG-RAID §4 — the EXECUTION PHASE of one Raid operation, deliberately separate from the
    // objective TYPE. These are three states of a single durable Raid, not three managers:
    //   Assault       — the primary army is moving on / fighting the current neutral target.
    //   Reinforcement — the primary holds position; a separate support army is en route to it.
    //   Return        — no neutral targets remain; the primary walks home to the chosen base.
    public enum RaidMissionPhase
    {
        Assault,
        Reinforcement,
        Return,
    }

    public sealed class AggressionObjective
    {
        public AggressionObjectiveKind Kind;
        public int TargetArmyId;
        public HexCoord LastKnownHex;
        public PlayerSetupData TargetOwner;
        public bool TargetIsNeutral;
        public float BaseValue;
        public float Confidence;

        // FROZEN strategic projection captured before StrategicManager/continuity ownership changes.
        // These describe strategic assemblability only. They are deliberately NOT the authoritative
        // operational shortage flags; RaidOperationalReadiness owns that later question.
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

        public string ObjectiveId => $"Raid#{TargetArmyId}";
        public MissionIntentKey IntentKey =>
            new MissionIntentKey(MissionKind.Raid, (int)Kind, TargetArmyId, 0, 0);

        public RaidMissionTarget ToTarget() => new RaidMissionTarget
        {
            TargetArmyId = TargetArmyId,
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
        // AGG-RAID §8 — which leg of the operation this proposal is. Assault (default) attacks the
        // neutral; Reinforcement moves the SUPPORT army to the primary; Return walks the primary
        // home. One target type, three phases — not three mission kinds.
        public RaidMissionPhase Phase;
        public int PrimaryArmyId;
        public int SupportArmyId;
        // Rendezvous hex (Reinforcement) or chosen base hex (Return). Unused for Assault.
        public HexCoord DestinationHex;

        public int TargetArmyId;
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
            // AGG-RAID §4 — Raid targets ONLY known neutral armies. An ordinary enemy army belongs
            // to the future Active Defence / strategic-offensive lane and must never produce a Raid
            // objective here. NeutralOpportunities is the analyzer's own filtered view of the same
            // facts; the raw `All` list stays available to every other consumer.
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
                    AiDebugLog.Write($"[AI][V2][AggressionObjective] decision=REJECT targetArmy={o.TargetArmyId} "
                        + "reason=target_is_not_neutral");
                    continue;
                }
                if (!o.HasTarget)
                {
                    AiDebugLog.Write("[AI][V2][AggressionObjective] decision=REJECT targetArmy=0 reason=opportunity_has_no_target");
                    continue;
                }
                if (o.TargetArmyId == 0)
                {
                    AiDebugLog.Write($"[AI][V2][AggressionObjective] decision=REJECT hex=({o.TargetHex.Q},{o.TargetHex.R}) reason=missing_stable_target_army_id");
                    continue;
                }
                if (o.DefenderCount > AiConfigV2.raidTargetMaxDefenders)
                {
                    AiDebugLog.Write($"[AI][V2][AggressionObjective] decision=REJECT targetArmy={o.TargetArmyId} "
                        + $"reason=too_many_defenders defenders={o.DefenderCount} max={AiConfigV2.raidTargetMaxDefenders}");
                    continue;
                }

                AggressionObjective obj = Build(snap, report, o);
                if (obj.BaseValue < AiConfigV2.raidObjectiveMinBaseValue)
                {
                    AiDebugLog.Write($"[AI][V2][AggressionObjective] decision=REJECT targetArmy={obj.TargetArmyId} "
                        + $"reason=base_value_below_threshold base={F(obj.BaseValue)} min={F(AiConfigV2.raidObjectiveMinBaseValue)}");
                    continue;
                }

                list.Add(obj);
                string frozenGap = !obj.CanCoverAllDefenders ? "coverage"
                    : obj.NeedsCombatPower ? "assemblability"
                    : obj.NeedsHero ? "hero_availability"
                    : "none";
                AiDebugLog.Write($"[AI][V2][AggressionObjective] decision=ACCEPT targetArmy={obj.TargetArmyId} "
                    + $"hex=({obj.LastKnownHex.Q},{obj.LastKnownHex.R}) base={F(obj.BaseValue)} "
                    + $"readyWin={F(obj.ReadyWinChance)} asmWin={F(obj.AssemblableWinChance)} "
                    + $"cover={(obj.CanCoverAllDefenders ? 1 : 0)} gate={(obj.GatePassed ? 1 : 0)} "
                    + $"frozenNeedsPower={(obj.NeedsCombatPower ? 1 : 0)} frozenNeedsHero={(obj.NeedsHero ? 1 : 0)} "
                    + $"frozenPowerDeficit={F(obj.CombatPowerDeficit)} frozenGap={frozenGap}");
            }

            list.Sort((a, b) =>
            {
                int c = b.BaseValue.CompareTo(a.BaseValue);
                return c != 0 ? c : a.TargetArmyId.CompareTo(b.TargetArmyId);
            });
            return list;
        }

        public static AggressionObjective ForTrackedArmy(WorldSnapshot snap, CombatOpportunityReport report, int trackedArmyId)
        {
            if (report?.All == null || trackedArmyId == 0)
                return null;
            // Identity is the TargetArmyId, never a coordinate — a moving neutral stays the same
            // objective. Neutrality is still required: a target that stopped being neutral is no
            // longer a Raid objective.
            foreach (CombatOpportunity o in report.All)
                if (o.HasTarget && o.TargetIsNeutral && o.TargetArmyId == trackedArmyId)
                    return Build(snap, report, o);
            return null;
        }

        private static AggressionObjective Build(WorldSnapshot snap, CombatOpportunityReport report, CombatOpportunity o)
        {
            float valueTerm = Mathf.Clamp01(o.TargetValue / Mathf.Max(0.0001f, AiConfigV2.opportunityValueNorm));
            IReadOnlyList<HexCoord> bases = snap.Self.BaseHexes;
            int distBase = bases != null && bases.Count > 0 ? MinDist(bases, o.TargetHex) : 0;
            float proximityTerm = Curves.InvRamp(distBase, AiConfigV2.raidProximityRampLo, AiConfigV2.raidProximityRampHi);
            float wSum = AiConfigV2.raidValueWeight + AiConfigV2.raidProximityWeight;
            float quality = Mathf.Clamp01(
                (AiConfigV2.raidValueWeight * valueTerm + AiConfigV2.raidProximityWeight * proximityTerm)
                / Mathf.Max(0.0001f, wSum));
            float baseValue = Mathf.Lerp(AiConfigV2.raidBaseValueMin, AiConfigV2.raidBaseValueMax, quality);

            bool readyViable = o.CanCoverAllDefenders
                && o.ReadyWinChance >= AiConfigV2.raidMinViableWinChance;
            bool assemblableViable = o.CanCoverAllDefenders
                && o.AssemblableWinChance >= AiConfigV2.raidMinViableWinChance;
            bool haveViable = o.GatePassed || readyViable || assemblableViable;
            // A missing hero is only a projected shortage when no legal heroless assemblable force
            // already clears the target. Hero is a capacity option, not a Raid prerequisite.
            bool needsHero = !haveViable && !report.HeroAvailable;
            bool needsCombatPower = !haveViable;

            float targetPower = AiPower.EffectiveArmyPowerFromProfiles(DefendersOf(snap, o.TargetArmyId));
            float requiredPower = targetPower * AiConfigV2.raidCombatPowerMargin;
            float deficit = needsCombatPower ? Mathf.Max(1f, requiredPower - snap.Self.FieldPower) : 0f;

            return new AggressionObjective
            {
                Kind = AggressionObjectiveKind.Raid,
                TargetArmyId = o.TargetArmyId,
                LastKnownHex = o.TargetHex,
                TargetOwner = o.TargetOwner,
                TargetIsNeutral = o.TargetIsNeutral,
                BaseValue = baseValue,
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

        private static IReadOnlyList<WorthIt.DefenderProfile> DefendersOf(WorldSnapshot snap, int armyId) =>
            AiV2Util.KnownDefenders(snap, armyId);

        private static int MinDist(IReadOnlyList<HexCoord> hexes, HexCoord to) => AiV2Util.MinDist(hexes, to);

        private static string F(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    }
}
