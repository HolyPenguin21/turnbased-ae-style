using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  AGGRESSION OBJECTIVE EVALUATOR  (Strategy V2 build-order step 9 — the Aggression enumeration)
    // ===========================================================================================
    //  The Aggression-axis counterpart of ReconObjectiveEvaluator. Turns the turn's FROZEN shared
    //  CombatOpportunityReport (computed once in StrategyLayer.Evaluate, carried on
    //  DesireBreakdown.OpportunityReport) into AggressionObjective[] — one per known, viable-scoped
    //  enemy / neutral ARMY target. Both DemandLayer (sizing combat capability) and
    //  AggressionMissionPlanner (building Raid proposals) read THIS; neither re-scans the world or
    //  re-derives a target's merit (spec §4 "Objective discovery must not repeat", AC #5/#6).
    //
    //  FROZEN FOR THE TURN. Computed BEFORE StrategicManager mutates own forces. Strategic Manager
    //  changes which FORCE can execute a Raid, never which strategic targets exist — so the list is
    //  NOT recomputed after the operational-state refresh (spec §8).
    //
    //  MERIT vs FEASIBILITY (spec §10 / §11). BaseValue is the target's INTRINSIC strategic merit
    //  on the shared 0..100 scale — target value + closeness only. It does NOT fold in the radar
    //  Aggression weight, the AggRaidOpportunity sub-driver, commitment bonuses or lane hysteresis.
    //  Feasibility (ready / assemblable win chance, coverage, ETA, capability deficit) is stored
    //  SEPARATELY. An objective exists even when no force can currently execute it — that is how
    //  the Demand layer learns which combat capability is missing (spec §11, AC #28).
    //
    //  SCOPE (spec §12). Only targets the snapshot-tier CombatOpportunityAnalyzer already carries:
    //  known enemy army sightings + known neutral army sightings. NOT enemy buildings, event
    //  guards, cheat-region contacts or unknown targets — those need a first-class snapshot
    //  representation before they can be frozen strategic objectives.
    // ===========================================================================================
    public enum AggressionObjectiveKind
    {
        Raid,   // the first (and only) Aggression Objective type — see spec §5
    }

    public sealed class AggressionObjective
    {
        public AggressionObjectiveKind Kind;

        // STABLE identity — the tracked target army, NOT its hex (spec §7). LastKnownHex is only
        // the current/last-known position; a moving target stays the same objective.
        public int TargetArmyId;
        public HexCoord LastKnownHex;
        public PlayerSetupData TargetOwner;
        public bool TargetIsNeutral;

        public float BaseValue;                 // 0..100 intrinsic merit — becomes MissionProposal.BaseValue
        public float Confidence;               // knowledge-tier confidence of the sighting

        // ---- combat feasibility snapshot (kept OUT of BaseValue) ----
        public float ReadyWinChance;           // strongest existing single stack vs this target
        public float AssemblableWinChance;     // strongest roster we could realistically gather this cycle
        public bool CanCoverAllDefenders;
        public int EstimatedEta;
        public int DefenderCount;
        public float TargetPower;              // EffectiveArmyPower of the target's defenders
        public bool GatePassed;                // hero obtainable AND coverage AND win >= min (feasibility signal only)

        // Capability the Aggression axis is short of, if any (spec §11 / §13). Sized so the Demand
        // layer can ask StrategicManager for the missing amount.
        public bool NeedsCombatPower;          // no ready/assemblable force clears the target
        public bool NeedsHero;                 // no obtainable hero to lead a fresh raid
        public float CombatPowerDeficit;       // projected AiPower shortfall (0 when NeedsCombatPower is false)

        // Deterministic total order for tie-breaks — target army id.
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

    // The typed Raid payload carried by MissionProposal.Target (spec §23). NOT a bare HexCoord —
    // it carries the stable objective identity and the frozen combat projection the executor and
    // the "why" log need.
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
        // Every in-scope Aggression opportunity in this snapshot's frozen CombatOpportunityReport.
        public static List<AggressionObjective> Enumerate(WorldSnapshot snap, CombatOpportunityReport report)
        {
            var list = new List<AggressionObjective>();
            if (snap?.Self == null || report?.All == null)
                return list;

            foreach (CombatOpportunity o in report.All)
            {
                if (!o.HasTarget || o.TargetArmyId == 0)
                    continue;
                // An army-vs-army fight is not a raid (spec §51 AC #24 kin; parity with V1
                // AiConfig.raidTargetMaxDefenders).
                if (o.DefenderCount > AiConfigV2.raidTargetMaxDefenders)
                    continue;

                AggressionObjective obj = Build(snap, report, o);
                if (obj.BaseValue >= AiConfigV2.raidObjectiveMinBaseValue)
                    list.Add(obj);
            }

            list.Sort((a, b) =>
            {
                int c = b.BaseValue.CompareTo(a.BaseValue);
                return c != 0 ? c : a.TargetArmyId.CompareTo(b.TargetArmyId);
            });
            return list;
        }

        // Re-materialise ONE Raid objective for a durable RaidIntent whose target may have moved —
        // the incumbent-intent path, NOT a re-scan for new objectives (spec §22).
        public static AggressionObjective ForTrackedArmy(WorldSnapshot snap, CombatOpportunityReport report, int trackedArmyId)
        {
            if (report?.All == null || trackedArmyId == 0)
                return null;
            foreach (CombatOpportunity o in report.All)
                if (o.HasTarget && o.TargetArmyId == trackedArmyId)
                    return Build(snap, report, o);
            return null;
        }

        // --------------------------------------------------------------------------------------

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

            bool haveViable = o.GatePassed
                || o.ReadyWinChance >= AiConfigV2.raidMinViableWinChance
                || o.AssemblableWinChance >= AiConfigV2.raidMinViableWinChance;
            bool needsHero = !report.HeroAvailable
                && !snap.Self.Armies.Any(a => a != null && a.HasHero && !a.IsPrison);
            bool needsCombatPower = !haveViable;
            float deficit = needsCombatPower
                ? Mathf.Max(0f, o.TargetValue * AiConfigV2.assetValueArmyPowerDivisor * AiConfigV2.raidCombatPowerMargin
                    - snap.Self.FieldPower)
                : 0f;

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
                TargetPower = AiPower.EffectiveArmyPowerFromProfiles(DefendersOf(snap, o.TargetArmyId)),
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
    }
}
