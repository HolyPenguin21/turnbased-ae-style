using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  RECON OBJECTIVE EVALUATOR
    // ===========================================================================================
    // One frozen turn produces three explicit Recon opportunity classes:
    // Explore — never/ground-unvisited frontier information;
    // Refresh — stale previously-observed information;
    // AirSweep — aviation-only observation pass toward the strategic sweep anchor.
    // ===========================================================================================
    public enum ReconObjectiveKind { Explore = 0, Refresh = 1, AirSweep = 3, CaptureStructure = 4 }

    public sealed class ReconObjective
    {
        public ReconObjectiveKind Kind;
        public HexCoord FocusHex;

        // Legacy transport kept during migration. Intrinsic value is now owned exclusively by
        // TaskScore; every migrated consumer must observe BaseValue == TaskScore.Value.
        public float BaseValue;
        public TaskScore TaskScore;
        public float DetectionRisk;             // raw fact, not the converted TaskScore contribution
        public StealthRequirement Stealth;

        public int FreshNeighbors;
        public int DistanceFromBase;
        public int AgeTurns;
        public float Severity;
        public float StrategicRelevance;
        public float DirectionPressure;

        public MissionIntentKey IntentKey
        {
            get
            {
                // One durable sweep identity: the anchor follows the enemy, the operation does not
                // become a new intent every time the concentration moves.
                if (Kind == ReconObjectiveKind.AirSweep)
                    return new MissionIntentKey(MissionKind.Scout, (int)ScoutTargetKind.AirSweep, 0, 0, 0);
                ScoutTargetKind sub = Kind == ReconObjectiveKind.Refresh
                    ? ScoutTargetKind.Refresh
                    : ScoutTargetKind.Explore;
                return new MissionIntentKey(MissionKind.Scout, (int)sub, 0, FocusHex.Q, FocusHex.R);
            }
        }

        public bool NeedsStealth => ReconScoutKinds.NeedsStealth(Stealth, DetectionRisk);

        public ScoutMissionTarget ToTarget() => new ScoutMissionTarget
        {
            FocusHex = FocusHex,
            Kind = Kind == ReconObjectiveKind.CaptureStructure ? ScoutTargetKind.CaptureStructure
                : Kind == ReconObjectiveKind.AirSweep ? ScoutTargetKind.AirSweep
                : Kind == ReconObjectiveKind.Refresh ? ScoutTargetKind.Refresh : ScoutTargetKind.Explore,
            Stealth = Stealth,
            DetectionRisk = DetectionRisk,
        };
    }

    public static class ReconObjectiveEvaluator
    {
        public static List<ReconObjective> Enumerate(WorldSnapshot snap)
        {
            using var __profile = new Game.Core.ProfileScope("AI/Objectives.Recon");
            var list = new List<ReconObjective>();
            if (snap?.Self == null || snap.MapKnowledge == null)
                return list;

            IReadOnlyList<FrontierHexSnapshot> frontier = snap.MapKnowledge.Frontier;
            if (frontier != null)
                foreach (FrontierHexSnapshot f in frontier)
                {
                    if (!ScoutObjectiveEvaluator.IsExploreFocusRunnable(snap, f.Hex))
                        continue;
                    list.Add(BuildExplore(snap, f.Hex, f.FreshNeighbors, f.DistanceFromNearestBase,
                        f.EnemyExposure, f.StealthDetectionRisk));
                }

            // An unvisited City-ruins hex is a known event-reward site, so it is an Explore focus
            // worth walking to even before it becomes part of the frontier wave band. Same
            // Explore shape and validity rule as a frontier focus; distance is priced by the
            // ordinary Delivery / OwnTerritoryProximity slots, never filtered here.
            ISet<HexCoord> ruins = snap.MapKnowledge.UnvisitedRuinsHexes;
            if (ruins != null && ruins.Count > 0)
            {
                var listed = new HashSet<HexCoord>(list.Select(o => o.FocusHex));
                foreach (HexCoord r in ruins.OrderBy(h => h.Q).ThenBy(h => h.R))
                {
                    if (listed.Contains(r))
                        continue;
                    ReconObjective o = ExploreAt(snap, r);
                    if (o != null)
                        list.Add(o);
                }
            }

            // Generic Refresh revisits map information the
            // player genuinely observed in an earlier turn. The frozen sidecar excludes never-seen
            // hexes by construction and current-visible hexes naturally have age 0.
            ReconDirectionSnapshot direction = null;
            List<ReconObjective> refresh = BuildRefreshObjectives(snap, ref direction);
            // Strike force step 6 — Attack's observation needs join as Refresh objectives of the
            // same identity, replacing a generic Refresh of the same hex.
            // A need never observed at all (the enemy citadel's cheat anchor, step 7) is an
            // ordinary Explore focus instead.
            foreach (HexCoord need in AttackObjectiveEvaluator.ObservationNeeds(snap).Distinct())
            {
                ReconObjective o = AttackNeedRefresh(snap, need, null, ref direction);
                if (o != null)
                {
                    refresh.RemoveAll(r => r.FocusHex.Equals(need));
                    refresh.Add(o);
                    continue;
                }
                if (!ReconIntelSnapshotRegistry.TryGetIntelAge(snap, need, out _)
                    && !list.Any(x => x.Kind == ReconObjectiveKind.Explore && x.FocusHex.Equals(need)))
                {
                    o = ExploreAt(snap, need);
                    if (o != null)
                        list.Add(o);
                }
            }
            list.AddRange(refresh);

            ReconObjective sweep = AirSweepOf(snap);
            if (sweep != null)
                list.Add(sweep);

            var auditPlayer = snap.Self.Armies?.FirstOrDefault(a => a?.Owner != null)?.Owner;
            if (auditPlayer != null)
            {
                if (direction == null)
                    direction = ReconDirectionModel.Build(snap);
                ReconAcceptanceAudit.RecordDirectionBoundary(auditPlayer, snap.TurnNumber,
                    direction);

                ReconObjective topRefresh = refresh
                    .Where(o => o != null)
                    .OrderByDescending(o => o.BaseValue)
                    .FirstOrDefault();
                if (topRefresh != null)
                    ReconAcceptanceAudit.RecordDirectionInfluence(auditPlayer, snap.TurnNumber,
                        topRefresh.FocusHex, topRefresh.DirectionPressure, topRefresh.BaseValue);
            }
            foreach (HexCoord hex in (snap.Self?.ReconCaptureOpportunities
                ?? System.Array.Empty<(int ArmyId, HexCoord Hex)>()).Select(x => x.Hex).Distinct())
            {
                ReconObjective capture = CaptureAt(snap, hex);
                if (capture != null) list.Add(capture);
            }
            return list;
        }

        internal static ReconObjective CaptureAt(WorldSnapshot snap, HexCoord hex)
        {
            if (snap?.Self?.ReconCaptureOpportunities?.Any(x => x.Hex.Equals(hex)) != true) return null;
            AiMapMemory.KnownBuilding building = snap.Known.Buildings.First(b => b.Hex.Equals(hex));
            var target = new ScoutMissionTarget { Kind = ScoutTargetKind.CaptureStructure, FocusHex = hex };
            ScoutCostEstimate cost = ScoutCostModel.Estimate(snap, target);
            if (!cost.MoverKnown) return null;
            float relevance = building.IsStartingCitadel ? 1f : building.IsBase ? 0.85f : 0.5f;
            TaskScore score = new TaskScore(
                strategicRelevance: TaskScoreEvaluator.StrategicRelevance(relevance),
                cardPrice: TaskScoreEvaluator.Price(cost.ActivationApNow),
                ownTerritoryProximity: TaskScoreEvaluator.OwnTerritoryProximity(
                    TaskScoreEvaluator.NearestOwnedHomeDistance(snap, hex)));
            return new ReconObjective { Kind = ReconObjectiveKind.CaptureStructure, FocusHex = hex,
                TaskScore = score, BaseValue = score.Value, StrategicRelevance = relevance };
        }

        // Task 5 (Problem B) — preferredMoverArmyId lets a caller re-materialising a durable
        // incumbent intent (ReconMissionPlanner.TryMaterializeIntent) price BaseValue against the
        // SAME actor Requirements will later be priced against in BuildProposal(), instead of
        // BaseValue silently reflecting whichever ground actor happens to be cheapest right now.
        // The fresh-objective path (Enumerate) always omits it — a brand-new candidate has no
        // actor to prefer yet.
        public static ReconObjective ExploreAt(WorldSnapshot snap, HexCoord hex,
            int? preferredMoverArmyId = null)
        {
            if (!ScoutObjectiveEvaluator.IsExploreFocusRunnable(snap, hex))
                return null;
            int fresh = ScoutObjectiveEvaluator.ExploreStillOpen(snap, hex);
            int distBase = snap?.Self?.BaseHexes != null && snap.Self.BaseHexes.Count > 0
                ? MinDist(snap.Self.BaseHexes, hex) : 0;
            bool exposed = EnemyExposedAt(snap, hex);
            bool stealthRisk = exposed && DetectorsAt(snap, hex) > 0;
            return BuildExplore(snap, hex, fresh, distBase, exposed, stealthRisk, preferredMoverArmyId);
        }

        public static ReconObjective RefreshAt(WorldSnapshot snap, HexCoord hex,
            int? preferredMoverArmyId = null)
        {
            if (AttackObjectiveEvaluator.ObservationNeeds(snap).Contains(hex))
            {
                ReconDirectionSnapshot direction = null;
                return AttackNeedRefresh(snap, hex, preferredMoverArmyId, ref direction);
            }
            if (!ReconIntelSnapshotRegistry.TryGetIntelAge(snap, hex, out int age)
                || !ReconIntelSnapshotRegistry.IsStaleAge(age))
                return null;
            if (snap?.MapKnowledge != null && snap.MapKnowledge.IsBlockedForScout(hex, stealthCapable: false))
                return null;
            return BuildRefresh(snap, hex, age, preferredMoverArmyId);
        }

        // The aviation-only observation pass (ScoutTargetKind.AirSweep). FocusHex = the sweep
        // anchor (WorldAnalysis.TryAirSweepAnchor: true enemy army concentration, else enemy
        // citadel). Its intrinsic value is what one deep pass along the corridor from our nearest
        // base toward the anchor would observe, on the existing Recon TaskScore slots: never-
        // observed hexes (InfoGain), relevance-weighted staleness (Staleness), what the anchor is
        // (StrategicRelevance) and that it is where the enemy is (ThreatDirection). Like every
        // other Recon task it also carries its own price: one sortie (activation AP + launch
        // Energy) from ScoutCostModel — the incumbent wing's own figure when known, else notional.
        public static ReconObjective AirSweepOf(WorldSnapshot snap, int? preferredMoverArmyId = null)
        {
            if (!WorldAnalysis.TryAirSweepAnchor(snap, out HexCoord anchor, out bool armyConcentration))
                return null;
            IReadOnlyList<HexCoord> bases = snap.Self.BaseHexes;
            HexCoord origin = bases != null && bases.Count > 0
                ? bases.OrderBy(b => HexGridMath.Distance(b, anchor)).ThenBy(b => b.Q).ThenBy(b => b.R).First()
                : snap.Self.Citadel;

            int samples = 0, neverObserved = 0;
            float staleWeighted = 0f, weight = 0f;
            HexCoord cur = origin;
            for (int i = 0; i < AiConfigV2.airSweepValueDepth && !cur.Equals(anchor); i++)
            {
                cur = ReconAirCapacityPolicy.SweepEndpoint(cur, anchor, 1);
                samples++;
                float w = AiConfigV2.reconRefreshPressureFloorWeight
                    + ReconIntelSnapshotRegistry.RefreshRelevance(snap, cur);
                weight += w;
                if (!ReconIntelSnapshotRegistry.TryGetIntelAge(snap, cur, out int age))
                {
                    neverObserved++;
                    // Never observed belongs to InfoGain; there is no old intel to refresh.
                    continue;
                }
                staleWeighted += w * ReconIntelSnapshotRegistry.Staleness(age);
            }
            if (samples == 0)
                return null;

            float anchorRelevance = Mathf.Max(armyConcentration ? 1f : 0.85f,
                ReconIntelSnapshotRegistry.RefreshRelevance(snap, anchor));
            float infoGainRaw = neverObserved / (float)samples;
            float stalenessRaw = weight > 0f ? staleWeighted / weight : 0f;
            float threatDirectionRaw = armyConcentration ? 1f : 0.75f;
            ScoutCostEstimate cost = MissionCost(snap, anchor, ScoutTargetKind.AirSweep,
                StealthRequirement.None, 0f, preferredMoverArmyId);
            TaskScore score = BuildAirSweepScore(snap, infoGainRaw, stalenessRaw,
                anchorRelevance, threatDirectionRaw, cost);

            return new ReconObjective
            {
                Kind = ReconObjectiveKind.AirSweep,
                FocusHex = anchor,
                TaskScore = score,
                BaseValue = score.Value,
                DetectionRisk = 0f,
                Stealth = StealthRequirement.None,
                DistanceFromBase = HexGridMath.Distance(origin, anchor),
                StrategicRelevance = anchorRelevance,
                DirectionPressure = threatDirectionRaw,
            };
        }

        // THE objective a durable Scout intent stands for right now, re-materialised from the
        // snapshot (null when it no longer exists, or for an unknown kind). Mission planning prices
        // it against the incumbent's own actor; ActorCommitments reads its stealth requirement.
        public static ReconObjective ForIntent(WorldSnapshot snap, ScoutIntent si,
            int? preferredMoverArmyId = null)
        {
            if (si == null)
                return null;
            switch (si.Kind)
            {
                case ScoutTargetKind.CaptureStructure: return CaptureAt(snap, si.FocusHex);
                case ScoutTargetKind.Explore: return ExploreAt(snap, si.FocusHex, preferredMoverArmyId);
                case ScoutTargetKind.Refresh: return RefreshAt(snap, si.FocusHex, preferredMoverArmyId);
                case ScoutTargetKind.AirSweep: return AirSweepOf(snap, preferredMoverArmyId);
                default: return null;
            }
        }

        // Strike force step 6 — the one rule for an Attack observation need: a site last seen more
        // than attackIntelMaxAgeTurns ago is refreshed as fully stale and maximally relevant.
        private static ReconObjective AttackNeedRefresh(WorldSnapshot snap, HexCoord hex,
            int? preferredMoverArmyId, ref ReconDirectionSnapshot direction)
        {
            if (!ReconIntelSnapshotRegistry.TryGetIntelAge(snap, hex, out int age)
                || age <= AiConfigV2.attackIntelMaxAgeTurns)
                return null;
            // Recon audit B2 — only a HARD block (off-map / scout-danger zone) stops the look. A known
            // hostile site is always a visible-arrival block (its garrison would fight, an
            // undefended one would be taken over), so testing that block here dropped EVERY Attack
            // need; the job is observed from a vantage instead (ObservationVantageSelector.UsesVantage).
            if (!ScoutObjectiveEvaluator.IsAttackObservationFocusRunnable(snap, hex))
                return null;
            if (direction == null)
                direction = ReconDirectionModel.Build(snap);
            return BuildRefresh(snap, hex, age, preferredMoverArmyId, direction, attackNeed: true);
        }

        private static List<ReconObjective> BuildRefreshObjectives(WorldSnapshot snap,
            ref ReconDirectionSnapshot direction)
        {
            var candidates = new List<ReconObjective>();
            foreach (KeyValuePair<HexCoord, int> kv in ReconIntelSnapshotRegistry.LastObservedFor(snap))
            {
                int age = Mathf.Max(0, snap.TurnNumber - kv.Value);
                if (!ReconIntelSnapshotRegistry.IsStaleAge(age))
                    continue;
                if (snap.MapKnowledge != null && snap.MapKnowledge.IsBlockedForScout(kv.Key, stealthCapable: false))
                    continue;
                if (direction == null)
                    direction = ReconDirectionModel.Build(snap);
                ReconObjective o = BuildRefresh(snap, kv.Key, age, direction: direction);
                if (o != null)
                    candidates.Add(o);
            }

            int cap = Mathf.Max(AiConfigV2.scoutCandidateBeamWidth * 3,
                ReconConcurrencyPolicy.HardCap * 3);
            return candidates
                .OrderByDescending(o => o.BaseValue)
                .ThenByDescending(o => o.AgeTurns)
                .ThenBy(o => o.FocusHex.Q)
                .ThenBy(o => o.FocusHex.R)
                .Take(cap)
                .ToList();
        }

        private static ScoutCostEstimate MissionCost(WorldSnapshot snap, HexCoord hex,
            ScoutTargetKind kind, StealthRequirement stealth, float detectionRisk,
            int? preferredMoverArmyId = null) =>
            ScoutCostModel.Estimate(snap, new ScoutMissionTarget
            {
                Kind = kind,
                FocusHex = hex,
                Stealth = stealth,
                DetectionRisk = detectionRisk,
            }, preferredMoverArmyId);

        // Each Recon task has one visible external-score assembly point. Helpers above/below provide
        // raw world/route facts only; conversion to score units happens here through TaskScoreEvaluator.
        // The value is one pass (airSweepValueDepth hexes), so the price is one sortie: the
        // activation AP it spends now plus its launch Energy — no multi-turn Delivery.
        private static TaskScore BuildAirSweepScore(WorldSnapshot snap, float infoGainRaw,
            float stalenessRaw, float strategicRelevanceRaw, float threatDirectionRaw,
            ScoutCostEstimate cost)
        {
            float launchEnergy = Mathf.Max(0f, cost.EnergyDesired);
            return new TaskScore(
                infoGain: TaskScoreEvaluator.InfoGain(infoGainRaw),
                staleness: TaskScoreEvaluator.PositiveStaleness(stalenessRaw),
                strategicRelevance: TaskScoreEvaluator.StrategicRelevance(strategicRelevanceRaw),
                threatDirection: TaskScoreEvaluator.ThreatDirection(threatDirectionRaw),
                cardPrice: TaskScoreEvaluator.Price(Mathf.Max(0f, cost.ActivationApNow)
                    + ActionPrice.Resources(t => t == Game.Economy.ResourceType.Energy
                        ? launchEnergy : 0f, snap)));
        }

        private static TaskScore BuildExploreScore(WorldSnapshot snap, HexCoord hex,
            float infoGainRaw, int homeDistance, ScoutCostEstimate cost, float detectionRiskRaw)
        {
            float activationNow = Mathf.Max(0f, cost.ActivationApNow);
            float stealthEntryNow = Mathf.Max(0f, cost.ApDesired - activationNow);
            return new TaskScore(
                strategicRelevance: TaskScoreEvaluator.StrategicRelevance(RuinsRelevance(snap, hex)),
                infoGain: TaskScoreEvaluator.InfoGain(infoGainRaw),
                ownTerritoryProximity: TaskScoreEvaluator.OwnTerritoryProximity(homeDistance),
                cardPrice: TaskScoreEvaluator.Price(activationNow + stealthEntryNow),
                delivery: TaskScoreEvaluator.Price(ActionPrice.RecurringAp(
                    cost.RecurringActivationAp, cost.EtaTurns)),
                detectionRisk: TaskScoreEvaluator.DetectionRisk(detectionRiskRaw));
        }

        private static TaskScore BuildRefreshScore(float stalenessRaw, float strategicRelevanceRaw,
            float threatDirectionRaw, int homeDistance, ScoutCostEstimate cost,
            float detectionRiskRaw)
        {
            float activationNow = Mathf.Max(0f, cost.ActivationApNow);
            float stealthEntryNow = Mathf.Max(0f, cost.ApDesired - activationNow);
            return new TaskScore(
                staleness: TaskScoreEvaluator.PositiveStaleness(stalenessRaw),
                strategicRelevance: TaskScoreEvaluator.StrategicRelevance(strategicRelevanceRaw),
                threatDirection: TaskScoreEvaluator.ThreatDirection(threatDirectionRaw),
                ownTerritoryProximity: TaskScoreEvaluator.OwnTerritoryProximity(homeDistance),
                cardPrice: TaskScoreEvaluator.Price(activationNow + stealthEntryNow),
                delivery: TaskScoreEvaluator.Price(ActionPrice.RecurringAp(
                    cost.RecurringActivationAp, cost.EtaTurns)),
                detectionRisk: TaskScoreEvaluator.DetectionRisk(detectionRiskRaw));
        }

        internal static ReconObjective BuildExplore(WorldSnapshot snap, HexCoord hex, int freshNeighbors,
            int distFromBase, bool enemyExposure, bool stealthDetectionRisk,
            int? preferredMoverArmyId = null)
        {
            float infoGainRaw = Mathf.Clamp01(
                freshNeighbors / Mathf.Max(0.0001f, AiConfigV2.scoutInfoGainNorm));
            infoGainRaw *= ExploreObservationFreshnessFactor(snap, hex);

            int homeDist = TaskScoreEvaluator.NearestOwnedHomeDistance(snap, hex, distFromBase);
            StealthRequirement req = enemyExposure ? StealthRequirement.Required : StealthRequirement.None;
            float riskRaw = enemyExposure
                ? Mathf.Max(stealthDetectionRisk
                        ? 1f / Mathf.Max(0.0001f, AiConfigV2.scoutDetectionRiskNorm) : 0f,
                    ScoutRiskModel.DetectorRisk(snap, hex))
                : 0f;
            ScoutCostEstimate cost = MissionCost(snap, hex, ScoutTargetKind.Explore, req, riskRaw,
                preferredMoverArmyId);

            TaskScore score = BuildExploreScore(snap, hex, infoGainRaw, homeDist, cost, riskRaw);

            return new ReconObjective
            {
                Kind = ReconObjectiveKind.Explore,
                FocusHex = hex,
                TaskScore = score,
                BaseValue = score.Value,
                DetectionRisk = riskRaw,
                Stealth = req,
                FreshNeighbors = freshNeighbors,
                DistanceFromBase = distFromBase,
            };
        }

        // An unvisited City-ruins focus always holds a Hex Event (a public map rule). What a scout
        // gains there is KNOWLEDGE of that site (its guard, its reward), not the reward itself: a
        // scout declines the guard fight, and the reward is the Raid's (RaidReward) once the guard
        // is known. So the ruins weight rides StrategicRelevance ("how much knowing this target
        // matters"), never an Economy income slot.
        private static float RuinsRelevance(WorldSnapshot snap, HexCoord hex)
        {
            ISet<HexCoord> ruins = snap?.MapKnowledge?.UnvisitedRuinsHexes;
            return ruins != null && ruins.Contains(hex) ? AiConfigV2.reconRuinsRelevance : 0f;
        }

        // Average [floor..1] information-retention factor over the Explore focus and the unvisited,
        // on-map, non-blocked neighbours that make up its FreshNeighbors count.
        private static float ExploreObservationFreshnessFactor(WorldSnapshot snap, HexCoord focus)
        {
            float floor = Mathf.Clamp01(AiConfigV2.scoutExploreObservedInfoDiscountFloor);
            float sum = HexObservationRetention(snap, focus, floor);
            int count = 1;

            MapKnowledgeSnapshot mk = snap?.MapKnowledge;
            var onMap = mk?.AllHexes as HashSet<HexCoord>
                ?? (mk?.AllHexes != null ? new HashSet<HexCoord>(mk.AllHexes) : null);
            foreach (HexCoord n in HexGridMath.Neighbors(focus))
            {
                if (onMap != null && onMap.Contains(n) == false) continue;
                if (mk?.VisitedHexSet != null && mk.VisitedHexSet.Contains(n)) continue;
                if (mk != null && mk.IsBlockedForScout(n, stealthCapable: false)) continue;
                sum += HexObservationRetention(snap, n, floor);
                count++;
            }
            return count > 0 ? sum / count : 1f;
        }

        private static float HexObservationRetention(WorldSnapshot snap, HexCoord hex, float floor)
        {
            if (!ReconIntelSnapshotRegistry.TryGetIntelAge(snap, hex, out int age))
                return 1f;
            return Mathf.Lerp(floor, 1f, ReconIntelSnapshotRegistry.Staleness(age));
        }

        // `attackNeed` — a live Attack operation's target (AttackNeedRefresh): stale and relevant
        // for its purpose regardless of the generic staleness ramp.
        private static ReconObjective BuildRefresh(WorldSnapshot snap, HexCoord hex, int age,
            int? preferredMoverArmyId = null, ReconDirectionSnapshot direction = null,
            bool attackNeed = false)
        {
            IReadOnlyList<HexCoord> bases = snap.Self.BaseHexes;
            int distBase = bases != null && bases.Count > 0 ? MinDist(bases, hex) : 0;
            int homeDist = TaskScoreEvaluator.NearestOwnedHomeDistance(snap, hex, distBase);
            float staleRaw = attackNeed ? 1f : ReconIntelSnapshotRegistry.Staleness(age);

            float strategicRaw = attackNeed ? 1f : ReconIntelSnapshotRegistry.RefreshRelevance(snap, hex);
            direction = direction ?? ReconDirectionModel.Build(snap);
            ReconSector sector = ReconDirectionModel.Sector(snap.Self.Citadel, hex);
            float directionalRaw = direction?.EnemyDirectionSectors != null
                && direction.EnemyDirectionSectors.TryGetValue(sector, out float pressure)
                    ? Mathf.Clamp01(pressure)
                    : 0f;
            if (direction?.KnownEnemyCitadelDirection == sector)
                directionalRaw = Mathf.Max(directionalRaw, 0.75f);

            bool exposed = EnemyExposedAt(snap, hex);
            float riskRaw = exposed ? ScoutRiskModel.DetectorRisk(snap, hex) : 0f;
            StealthRequirement req = exposed ? StealthRequirement.Required : StealthRequirement.None;
            ScoutCostEstimate cost = MissionCost(snap, hex, ScoutTargetKind.Refresh, req, riskRaw,
                preferredMoverArmyId);
            TaskScore score = BuildRefreshScore(staleRaw, strategicRaw, directionalRaw,
                homeDist, cost, riskRaw);

            var objective = new ReconObjective
            {
                Kind = ReconObjectiveKind.Refresh,
                FocusHex = hex,
                TaskScore = score,
                BaseValue = score.Value,
                DetectionRisk = riskRaw,
                Stealth = req,
                DistanceFromBase = distBase,
                AgeTurns = age,
                StrategicRelevance = strategicRaw,
                DirectionPressure = directionalRaw,
            };

            if (strategicRaw > 0f)
            {
                var auditPlayer = snap.Self.Armies?.FirstOrDefault(a => a?.Owner != null)?.Owner;
                if (auditPlayer != null)
                    ReconAcceptanceAudit.RecordStaleStrategicRefresh(auditPlayer, snap.TurnNumber,
                        hex, age, strategicRaw);
            }
            return objective;
        }

        private static int MinDist(IReadOnlyList<HexCoord> hexes, HexCoord to) => AiV2Util.MinDist(hexes, to);

        private static bool EnemyExposedAt(WorldSnapshot snap, HexCoord hex) =>
            ScoutRiskModel.IsExposed(snap?.Known?.EnemySightings, hex);

        private static int DetectorsAt(WorldSnapshot snap, HexCoord hex) =>
            ScoutRiskModel.CountDetectors(snap?.Known?.EnemySightings, hex);
    }
}
