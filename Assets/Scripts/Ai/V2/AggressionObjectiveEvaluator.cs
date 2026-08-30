using System.Collections.Generic;
using System.Globalization;
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
        Raid,
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

        public float BaseValue;
        public float Confidence;

        // ---- combat feasibility snapshot (kept OUT of BaseValue) ----
        public float ReadyWinChance;
        public float AssemblableWinChance;
        public bool CanCoverAllDefenders;
        public int EstimatedEta;
        public int DefenderCount;
        public float TargetPower;
        public bool GatePassed;

        // These are strategic-cycle hints only. DemandLayer re-checks the CURRENT free capability
        // after continuity claims are known, so a committed raid army cannot masquerade as spare
        // supply and a hero card in hand cannot masquerade as an already-deployed raid leader.
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
                string shortage = !obj.CanCoverAllDefenders ? "coverage"
                    : obj.NeedsCombatPower ? "win_chance_or_power"
                    : obj.NeedsHero ? "hero"
                    : "none";
                AiDebugLog.Write($"[AI][V2][AggressionObjective] decision=ACCEPT targetArmy={obj.TargetArmyId} "
                    + $"hex=({obj.LastKnownHex.Q},{obj.LastKnownHex.R}) base={F(obj.BaseValue)} "
                    + $"readyWin={F(obj.ReadyWinChance)} asmWin={F(obj.AssemblableWinChance)} "
                    + $"cover={(obj.CanCoverAllDefenders ? 1 : 0)} gate={(obj.GatePassed ? 1 : 0)} "
                    + $"needsPower={(obj.NeedsCombatPower ? 1 : 0)} needsHero={(obj.NeedsHero ? 1 : 0)} "
                    + $"powerDeficit={F(obj.CombatPowerDeficit)} shortage={shortage}");
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

            // Coverage is a hard combat precondition. Step 9 originally treated a high raw
            // Ready/Assemblable win chance as "viable" even when no attacker could damage one of
            // the known defenders. That suppresses the very demand that could prepare a better
            // combat body and later sends Provisioning into a permanent assembly failure loop.
            bool readyViable = o.CanCoverAllDefenders
                && o.ReadyWinChance >= AiConfigV2.raidMinViableWinChance;
            bool assemblableViable = o.CanCoverAllDefenders
                && o.AssemblableWinChance >= AiConfigV2.raidMinViableWinChance;
            bool haveViable = o.GatePassed || readyViable || assemblableViable;

            // A ready existing force may raid without a hero. A hero is strategically missing only
            // when we still need to form/strengthen a raid force and the shared strategic analysis
            // says no hero is obtainable at all. Operational field availability is rechecked in
            // DemandLayer after ActorCommitments are known.
            bool needsHero = !readyViable && !report.HeroAvailable;
            bool needsCombatPower = !haveViable;

            float targetPower = AiPower.EffectiveArmyPowerFromProfiles(DefendersOf(snap, o.TargetArmyId));
            float requiredPower = targetPower * AiConfigV2.raidCombatPowerMargin;
            float deficit = needsCombatPower
                ? Mathf.Max(1f, requiredPower - snap.Self.FieldPower)
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
