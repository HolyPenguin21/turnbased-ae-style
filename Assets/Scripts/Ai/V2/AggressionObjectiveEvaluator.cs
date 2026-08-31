using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.HexGrid;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    public enum AggressionObjectiveKind
    {
        Raid,
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
            if (report?.All == null || report.All.Count == 0)
            {
                AiDebugLog.Write("[AI][V2][AggressionObjective] decision=NONE reason=no_known_enemy_or_neutral_army_opportunities");
                return list;
            }

            foreach (CombatOpportunity o in report.All)
            {
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
            foreach (CombatOpportunity o in report.All)
                if (o.HasTarget && o.TargetArmyId == trackedArmyId)
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
            bool needsHero = !readyViable && !report.HeroAvailable;
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

        private static IReadOnlyList<WorthIt.DefenderProfile> DefendersOf(WorldSnapshot snap, int armyId)
        {
            IEnumerable<AiMapMemory.KnownEnemySighting> all =
                (snap.Known?.EnemySightings ?? Enumerable.Empty<AiMapMemory.KnownEnemySighting>())
                .Concat(snap.Known?.NeutralSightings ?? Enumerable.Empty<AiMapMemory.KnownEnemySighting>());
            foreach (AiMapMemory.KnownEnemySighting s in all)
                if (s.ArmyId == armyId)
                    return s.Defenders ?? System.Array.Empty<WorthIt.DefenderProfile>();
            return System.Array.Empty<WorthIt.DefenderProfile>();
        }

        private static int MinDist(IReadOnlyList<HexCoord> hexes, HexCoord to)
        {
            int best = int.MaxValue;
            foreach (HexCoord h in hexes)
            {
                int d = HexGridMath.Distance(h, to);
                if (d < best) best = d;
            }
            return best == int.MaxValue ? 0 : best;
        }

        private static string F(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    }
}
