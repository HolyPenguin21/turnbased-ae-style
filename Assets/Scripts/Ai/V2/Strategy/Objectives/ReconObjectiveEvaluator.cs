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
    // Surveil — stale enemy contact; observation-vantage semantics in provisioning;
    // AirSweep — aviation-only observation pass toward the strategic sweep anchor.
    // ===========================================================================================
    public enum ReconObjectiveKind { Explore, Refresh, Surveil, AirSweep }

    public sealed class ReconObjective
    {
        public ReconObjectiveKind Kind;
        public HexCoord FocusHex;
        public int ContactArmyId;              // Surveil only
        public EnemyContactSnapshot Contact;   // Surveil only

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
                if (Kind == ReconObjectiveKind.Surveil)
                    return new MissionIntentKey(MissionKind.Scout, (int)ScoutTargetKind.Surveil,
                        ContactArmyId, 0, 0);
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

        public ScoutMissionTarget ToTarget() => new ScoutMissionTarget
        {
            FocusHex = FocusHex,
            Kind = Kind == ReconObjectiveKind.Surveil ? ScoutTargetKind.Surveil
                : Kind == ReconObjectiveKind.AirSweep ? ScoutTargetKind.AirSweep
                : Kind == ReconObjectiveKind.Refresh ? ScoutTargetKind.Refresh : ScoutTargetKind.Explore,
            Contact = Kind == ReconObjectiveKind.Surveil ? Contact : null,
            Stealth = Stealth,
            DetectionRisk = DetectionRisk,
        };
    }

    public static class ReconObjectiveEvaluator
    {
        public static List<ReconObjective> Enumerate(WorldSnapshot snap)
        {
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

            // Generic Refresh is NOT enemy-contact surveillance. It revisits map information the
            // player genuinely observed in an earlier turn. The frozen sidecar excludes never-seen
            // hexes by construction and current-visible hexes naturally have age 0.
            ReconDirectionSnapshot direction = null;
            List<ReconObjective> refresh = BuildRefreshObjectives(snap, ref direction);
            list.AddRange(refresh);

            IReadOnlyList<EnemyContactSnapshot> contacts = snap.Threat?.Contacts;
            if (contacts != null)
                foreach (EnemyContactSnapshot c in contacts)
                    if (c.Source == ContactSource.Honest && c.Knowledge == ContactKnowledge.LastKnown
                        && c.Position.HasValue && c.Army != null)
                        list.Add(BuildSurveil(snap, c));

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
            return list;
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
            if (!ReconIntelSnapshotRegistry.TryGetIntelAge(snap, hex, out int age)
                || age < AiConfigV2.scoutSurveilStaleTurnsLo)
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
        // (StrategicRelevance) and that it is where the enemy is (ThreatDirection). AP/Energy of
        // the actual wing are priced by allocation/provisioning like every air sortie.
        public static ReconObjective AirSweepOf(WorldSnapshot snap)
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
                    staleWeighted += w;
                    continue;
                }
                staleWeighted += w * Curves.Ramp(age, AiConfigV2.scoutSurveilStaleTurnsLo,
                    AiConfigV2.scoutSurveilStaleTurnsHi);
            }
            if (samples == 0)
                return null;

            float anchorRelevance = Mathf.Max(armyConcentration ? 1f : 0.85f,
                ReconIntelSnapshotRegistry.RefreshRelevance(snap, anchor));
            var score = new TaskScore(
                infoGain: TaskScoreEvaluator.InfoGain(neverObserved / (float)samples),
                staleness: TaskScoreEvaluator.PositiveStaleness(weight > 0f ? staleWeighted / weight : 0f),
                strategicRelevance: TaskScoreEvaluator.StrategicRelevance(anchorRelevance),
                threatDirection: TaskScoreEvaluator.ThreatDirection(armyConcentration ? 1f : 0.75f));

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
                DirectionPressure = armyConcentration ? 1f : 0.75f,
            };
        }

        public static ReconObjective SurveilOf(WorldSnapshot snap, EnemyContactSnapshot c,
            int? preferredMoverArmyId = null) =>
            c == null ? null : BuildSurveil(snap, c, preferredMoverArmyId);

        private static List<ReconObjective> BuildRefreshObjectives(WorldSnapshot snap,
            ref ReconDirectionSnapshot direction)
        {
            var candidates = new List<ReconObjective>();
            foreach (KeyValuePair<HexCoord, int> kv in ReconIntelSnapshotRegistry.LastObservedFor(snap))
            {
                int age = Mathf.Max(0, snap.TurnNumber - kv.Value);
                if (age < AiConfigV2.scoutSurveilStaleTurnsLo)
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

        // Task 5 (Problem A) — cost.ApDesired mixes the mover's THIS-TURN re-activation fee (the
        // same real AP Economy/Raid price at taskScoreReactivationApWeight) with, only when
        // stealth must be entered this turn, a genuine one-time ability spend (correctly priced
        // like any other played card at taskScoreCardPriceApWeight — see ScoutCostEstimate.
        // ActivationApNow). Splitting here — instead of flattening the whole RequiredAp through a
        // single rate — keeps this Recon-scoped fold a caller of the shared TaskScoreEvaluator
        // conversions, not a second Fold() owner.
        private static float ThisTurnCardPrice(ScoutCostEstimate cost)
        {
            float activationNow = Mathf.Max(0f, cost.ActivationApNow);
            float stealthEntryNow = Mathf.Max(0f, cost.ApDesired - activationNow);
            return activationNow * AiConfigV2.taskScoreReactivationApWeight
                + TaskScoreEvaluator.CardPrice(stealthEntryNow, 0f);
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

            var score = new TaskScore(
                economicHexBenefit: RuinsEventBenefit(snap, hex),
                infoGain: TaskScoreEvaluator.InfoGain(infoGainRaw),
                ownTerritoryProximity: TaskScoreEvaluator.OwnTerritoryProximity(homeDist),
                cardPrice: ThisTurnCardPrice(cost),
                // ApDesired is THIS TURN only (zero when the actor was already activated).
                // Later turns reactivate at RecurringActivationAp; never reserve those future AP.
                delivery: TaskScoreEvaluator.DeliveryFromEta(cost.RecurringActivationAp, cost.EtaTurns,
                    AiConfigV2.taskScoreReactivationApWeight),
                detectionRisk: TaskScoreEvaluator.DetectionRisk(riskRaw));

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

        // A presumed Hex Event on an unvisited City-ruins focus pays resources (EventCatalog). It is
        // priced in the SAME slot Economy prices a resource hex with, at resource-hex parity:
        // reconRuinsEventIncomeEquivalent income-equivalent units spread evenly over the resource
        // types (which type the event pays is unknown until visited), each at its canonical
        // ResourcePriority — so a starving economy values ruins more, a saturated one less.
        private static float RuinsEventBenefit(WorldSnapshot snap, HexCoord hex)
        {
            ISet<HexCoord> ruins = snap?.MapKnowledge?.UnvisitedRuinsHexes;
            IReadOnlyList<EconomyResourceStanding> perType = snap?.Economy?.PerType;
            if (ruins == null || !ruins.Contains(hex) || perType == null || perType.Count == 0)
                return 0f;
            float share = AiConfigV2.reconRuinsEventIncomeEquivalent / perType.Count;
            var perResource = new List<(float Gain, float Priority)>(perType.Count);
            foreach (EconomyResourceStanding standing in perType)
                perResource.Add((share, TaskScoreEvaluator.ResourcePriority(standing)));
            return TaskScoreEvaluator.EconomicHexBenefit(perResource);
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
            return Mathf.Lerp(floor, 1f, Curves.Ramp(age,
                AiConfigV2.scoutSurveilStaleTurnsLo, AiConfigV2.scoutSurveilStaleTurnsHi));
        }

        private static ReconObjective BuildRefresh(WorldSnapshot snap, HexCoord hex, int age,
            int? preferredMoverArmyId = null, ReconDirectionSnapshot direction = null)
        {
            IReadOnlyList<HexCoord> bases = snap.Self.BaseHexes;
            int distBase = bases != null && bases.Count > 0 ? MinDist(bases, hex) : 0;
            int homeDist = TaskScoreEvaluator.NearestOwnedHomeDistance(snap, hex, distBase);
            float staleRaw = Curves.Ramp(age, AiConfigV2.scoutSurveilStaleTurnsLo,
                AiConfigV2.scoutSurveilStaleTurnsHi);

            float strategicRaw = ReconIntelSnapshotRegistry.RefreshRelevance(snap, hex);
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
            var score = new TaskScore(
                staleness: TaskScoreEvaluator.PositiveStaleness(staleRaw),
                strategicRelevance: TaskScoreEvaluator.StrategicRelevance(strategicRaw),
                threatDirection: TaskScoreEvaluator.ThreatDirection(directionalRaw),
                ownTerritoryProximity: TaskScoreEvaluator.OwnTerritoryProximity(homeDist),
                cardPrice: ThisTurnCardPrice(cost),
                delivery: TaskScoreEvaluator.DeliveryFromEta(cost.RecurringActivationAp, cost.EtaTurns,
                    AiConfigV2.taskScoreReactivationApWeight),
                detectionRisk: TaskScoreEvaluator.DetectionRisk(riskRaw));

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

        private static ReconObjective BuildSurveil(WorldSnapshot snap, EnemyContactSnapshot c,
            int? preferredMoverArmyId = null)
        {
            IReadOnlyList<AssetThreatSnapshot> threats = snap.Threat?.Threats;
            IReadOnlyList<HexCoord> bases = snap.Self.BaseHexes;

            HexCoord pos = c.Position.Value;
            int age = c.AgeTurns(snap.TurnNumber);
            float stalenessRaw = Curves.Ramp(age, AiConfigV2.scoutSurveilStaleTurnsLo,
                AiConfigV2.scoutSurveilStaleTurnsHi);

            float maxSeverity = 0f;
            if (threats != null)
                foreach (AssetThreatSnapshot t in threats)
                    if (ReferenceEquals(t.Contact, c) && t.Severity > maxSeverity)
                        maxSeverity = t.Severity;

            float contactRelevanceRaw = Mathf.Clamp01(stalenessRaw * maxSeverity);
            int fallbackDistance = bases != null && bases.Count > 0 ? MinDist(bases, pos) : 0;
            int homeDist = TaskScoreEvaluator.NearestOwnedHomeDistance(snap, pos, fallbackDistance);
            float riskRaw = Mathf.Clamp01(Mathf.Max(
                c.Confidence * AiConfigV2.scoutSurveilBaseDetectionRisk,
                ScoutRiskModel.DetectorRisk(snap, pos)));
            ScoutCostEstimate cost = MissionCost(snap, pos, ScoutTargetKind.Surveil,
                StealthRequirement.Required, riskRaw, preferredMoverArmyId);

            var score = new TaskScore(
                staleness: TaskScoreEvaluator.PositiveStaleness(stalenessRaw),
                contactRelevance: TaskScoreEvaluator.ContactRelevance(contactRelevanceRaw),
                ownTerritoryProximity: TaskScoreEvaluator.OwnTerritoryProximity(homeDist),
                cardPrice: ThisTurnCardPrice(cost),
                delivery: TaskScoreEvaluator.DeliveryFromEta(cost.RecurringActivationAp, cost.EtaTurns,
                    AiConfigV2.taskScoreReactivationApWeight),
                detectionRisk: TaskScoreEvaluator.DetectionRisk(riskRaw));

            return new ReconObjective
            {
                Kind = ReconObjectiveKind.Surveil,
                FocusHex = pos,
                ContactArmyId = c.Army?.ArmyId ?? 0,
                Contact = c,
                TaskScore = score,
                BaseValue = score.Value,
                DetectionRisk = riskRaw,
                Stealth = StealthRequirement.Required,
                AgeTurns = age,
                Severity = maxSeverity,
                DistanceFromBase = fallbackDistance,
            };
        }

        private static int MinDist(IReadOnlyList<HexCoord> hexes, HexCoord to) => AiV2Util.MinDist(hexes, to);

        private static bool EnemyExposedAt(WorldSnapshot snap, HexCoord hex)
        {
            IReadOnlyList<AiMapMemory.KnownEnemySighting> s = snap?.Known?.EnemySightings;
            if (s == null) return false;
            int r = AiConfigV2.frontierEnemyExposureRadius;
            // Same exposure rule as WorldAnalysis.Knowledge: a garrison cannot engage (audit F1);
            // its detection is counted by DetectorsAt.
            foreach (AiMapMemory.KnownEnemySighting e in s)
                if (!e.IsGarrison && HexGridMath.Distance(e.Hex, hex) <= r) return true;
            return false;
        }

        private static int DetectorsAt(WorldSnapshot snap, HexCoord hex)
        {
            IReadOnlyList<AiMapMemory.KnownEnemySighting> s = snap?.Known?.EnemySightings;
            if (s == null) return 0;
            int r = AiConfigV2.frontierEnemyExposureRadius, n = 0;
            foreach (AiMapMemory.KnownEnemySighting e in s)
                if (HexGridMath.Distance(e.Hex, hex) <= r && e.CanDetectStealthAt(hex)) n++;
            return n;
        }
    }
}
