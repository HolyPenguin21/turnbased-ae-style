using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  RECON OBJECTIVE EVALUATOR
    // ===========================================================================================
    //  One frozen turn produces three explicit Recon opportunity classes:
    //    Explore  — never/ground-unvisited frontier information.
    //    Refresh  — stale previously-observed information; ground route/terrain witness.
    //    Surveil  — stale enemy contact; observation-vantage semantics in provisioning.
    // ===========================================================================================
    public enum ReconObjectiveKind { Explore, Refresh, Surveil }

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
                ScoutTargetKind sub = Kind == ReconObjectiveKind.Refresh
                    ? ScoutTargetKind.Refresh
                    : ScoutTargetKind.Explore;
                return new MissionIntentKey(MissionKind.Scout, (int)sub, 0, FocusHex.Q, FocusHex.R);
            }
        }

        public ScoutMissionTarget ToTarget() => new ScoutMissionTarget
        {
            FocusHex = FocusHex,
            Kind = Kind == ReconObjectiveKind.Surveil
                ? ScoutTargetKind.Surveil
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

            // Generic Refresh is NOT enemy-contact surveillance. It revisits map information the
            // player genuinely observed in an earlier turn. The frozen sidecar excludes never-seen
            // hexes by construction and current-visible hexes naturally have age 0.
            List<ReconObjective> refresh = BuildRefreshObjectives(snap);
            list.AddRange(refresh);

            IReadOnlyList<EnemyContactSnapshot> contacts = snap.Threat?.Contacts;
            if (contacts != null)
                foreach (EnemyContactSnapshot c in contacts)
                    if (c.Source == ContactSource.Honest && c.Knowledge == ContactKnowledge.LastKnown
                        && c.Position.HasValue && c.Army != null)
                        list.Add(BuildSurveil(snap, c));

            var auditPlayer = snap.Self.Armies?.FirstOrDefault(a => a?.Owner != null)?.Owner;
            if (auditPlayer != null)
            {
                ReconAcceptanceAudit.RecordDirectionBoundary(auditPlayer, snap.TurnNumber,
                    ReconDirectionModel.Build(snap));

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

        public static ReconObjective ExploreAt(WorldSnapshot snap, HexCoord hex)
        {
            if (!ScoutObjectiveEvaluator.IsExploreFocusRunnable(snap, hex))
                return null;
            int fresh = ScoutObjectiveEvaluator.ExploreStillOpen(snap, hex);
            int distBase = snap?.Self?.BaseHexes != null && snap.Self.BaseHexes.Count > 0
                ? MinDist(snap.Self.BaseHexes, hex) : 0;
            bool exposed = EnemyExposedAt(snap, hex);
            bool stealthRisk = exposed && DetectorsAt(snap, hex) > 0;
            return BuildExplore(snap, hex, fresh, distBase, exposed, stealthRisk);
        }

        public static ReconObjective RefreshAt(WorldSnapshot snap, HexCoord hex)
        {
            if (!ReconIntelSnapshotRegistry.TryGetIntelAge(snap, hex, out int age)
                || age < AiConfigV2.scoutSurveilStaleTurnsLo)
                return null;
            if (snap?.MapKnowledge != null && snap.MapKnowledge.IsBlockedForScout(hex, stealthCapable: false))
                return null;
            return BuildRefresh(snap, hex, age);
        }

        public static ReconObjective SurveilOf(WorldSnapshot snap, EnemyContactSnapshot c) =>
            c == null ? null : BuildSurveil(snap, c);

        private static List<ReconObjective> BuildRefreshObjectives(WorldSnapshot snap)
        {
            var candidates = new List<ReconObjective>();
            foreach (KeyValuePair<HexCoord, int> kv in ReconIntelSnapshotRegistry.LastObservedFor(snap))
            {
                int age = Mathf.Max(0, snap.TurnNumber - kv.Value);
                if (age < AiConfigV2.scoutSurveilStaleTurnsLo)
                    continue;
                if (snap.MapKnowledge != null && snap.MapKnowledge.IsBlockedForScout(kv.Key, stealthCapable: false))
                    continue;
                ReconObjective o = BuildRefresh(snap, kv.Key, age);
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

        internal static ReconObjective BuildExplore(WorldSnapshot snap, HexCoord hex, int freshNeighbors,
            int distFromBase, bool enemyExposure, bool stealthDetectionRisk)
        {
            float infoGainRaw = Mathf.Clamp01(
                freshNeighbors / Mathf.Max(0.0001f, AiConfigV2.scoutInfoGainNorm));
            infoGainRaw *= ExploreObservationFreshnessFactor(snap, hex);

            int homeDist = HomeDistance(snap, hex, distFromBase);
            StealthRequirement req = enemyExposure ? StealthRequirement.Required : StealthRequirement.None;
            float riskRaw = enemyExposure
                ? Mathf.Max(stealthDetectionRisk
                        ? 1f / Mathf.Max(0.0001f, AiConfigV2.scoutDetectionRiskNorm) : 0f,
                    ScoutRiskModel.DetectorRisk(snap, hex))
                : 0f;

            var score = new TaskScore(
                infoGain: TaskScoreEvaluator.InfoGain(infoGainRaw),
                ownTerritoryProximity: TaskScoreEvaluator.OwnTerritoryProximity(homeDist),
                detectionRisk: TaskScoreEvaluator.DetectionRisk(riskRaw));
            TaskScoreDiagnostics.Log("ReconExplore", hex, score,
                $"freshNeighbors={freshNeighbors} infoGain={infoGainRaw:0.###} "
                + $"homeDistance={homeDist} detectionRisk={riskRaw:0.###}");

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
                if (onMap != null && !onMap.Contains(n)) continue;
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

        private static ReconObjective BuildRefresh(WorldSnapshot snap, HexCoord hex, int age)
        {
            IReadOnlyList<HexCoord> bases = snap.Self.BaseHexes;
            int distBase = bases != null && bases.Count > 0 ? MinDist(bases, hex) : 0;
            int homeDist = HomeDistance(snap, hex, distBase);
            float staleRaw = Curves.Ramp(age, AiConfigV2.scoutSurveilStaleTurnsLo,
                AiConfigV2.scoutSurveilStaleTurnsHi);

            float strategicRaw = StrategicRefreshRelevance(snap, hex);
            ReconDirectionSnapshot direction = ReconDirectionModel.Build(snap);
            ReconSector sector = ReconDirectionModel.Sector(snap.Self.Citadel, hex);
            float directionalRaw = direction?.EnemyDirectionSectors != null
                && direction.EnemyDirectionSectors.TryGetValue(sector, out float pressure)
                    ? Mathf.Clamp01(pressure)
                    : 0f;
            if (direction?.KnownEnemyCitadelDirection == sector)
                directionalRaw = Mathf.Max(directionalRaw, 0.75f);

            bool exposed = EnemyExposedAt(snap, hex);
            float riskRaw = exposed ? ScoutRiskModel.DetectorRisk(snap, hex) : 0f;
            var score = new TaskScore(
                staleness: TaskScoreEvaluator.PositiveStaleness(staleRaw),
                strategicRelevance: TaskScoreEvaluator.StrategicRelevance(strategicRaw),
                threatDirection: TaskScoreEvaluator.ThreatDirection(directionalRaw),
                ownTerritoryProximity: TaskScoreEvaluator.OwnTerritoryProximity(homeDist),
                detectionRisk: TaskScoreEvaluator.DetectionRisk(riskRaw));
            TaskScoreDiagnostics.Log("ReconRefresh", hex, score,
                $"age={age} stale={staleRaw:0.###} strategic={strategicRaw:0.###} "
                + $"direction={directionalRaw:0.###} homeDistance={homeDist} detectionRisk={riskRaw:0.###}");

            var objective = new ReconObjective
            {
                Kind = ReconObjectiveKind.Refresh,
                FocusHex = hex,
                TaskScore = score,
                BaseValue = score.Value,
                DetectionRisk = riskRaw,
                Stealth = exposed ? StealthRequirement.Required : StealthRequirement.None,
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

        private static ReconObjective BuildSurveil(WorldSnapshot snap, EnemyContactSnapshot c)
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
            int homeDist = HomeDistance(snap, pos, fallbackDistance);
            float riskRaw = Mathf.Clamp01(Mathf.Max(
                c.Confidence * AiConfigV2.scoutSurveilBaseDetectionRisk,
                ScoutRiskModel.DetectorRisk(snap, pos)));

            var score = new TaskScore(
                staleness: TaskScoreEvaluator.PositiveStaleness(stalenessRaw),
                contactRelevance: TaskScoreEvaluator.ContactRelevance(contactRelevanceRaw),
                ownTerritoryProximity: TaskScoreEvaluator.OwnTerritoryProximity(homeDist),
                detectionRisk: TaskScoreEvaluator.DetectionRisk(riskRaw));
            TaskScoreDiagnostics.Log("ReconSurveil", pos, score,
                $"age={age} stale={stalenessRaw:0.###} confidence={c.Confidence:0.###} "
                + $"severity={maxSeverity:0.###} contact={contactRelevanceRaw:0.###} "
                + $"homeDistance={homeDist} detectionRisk={riskRaw:0.###}");

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

        private static float StrategicRefreshRelevance(WorldSnapshot snap, HexCoord hex)
        {
            float relevance = 0f;
            if (snap.Known?.Buildings != null)
                foreach (AiMapMemory.KnownBuilding b in snap.Known.Buildings)
                {
                    int d = HexGridMath.Distance(b.Hex, hex);
                    if (d == 0) relevance = Mathf.Max(relevance, b.IsStartingCitadel ? 1f : 0.85f);
                    else if (d == 1) relevance = Mathf.Max(relevance, 0.50f);
                }

            if (snap.Known?.ResourceHexes != null)
                foreach (Game.Ai.AiMapMemory.KnownResourceHex r in snap.Known.ResourceHexes)
                {
                    int d = HexGridMath.Distance(r.Hex, hex);
                    if (d == 0) relevance = Mathf.Max(relevance, 0.75f);
                    else if (d == 1) relevance = Mathf.Max(relevance, 0.40f);
                }

            if (snap.Known?.EventGuardHexes != null)
                foreach (HexCoord e in snap.Known.EventGuardHexes)
                {
                    int d = HexGridMath.Distance(e, hex);
                    if (d == 0) relevance = Mathf.Max(relevance, 0.80f);
                    else if (d == 1) relevance = Mathf.Max(relevance, 0.45f);
                }
            return relevance;
        }

        // Shared strategic home-distance contract. Keep this method as the stable test seam; the
        // implementation itself is now owned by the common TaskScore evaluator.
        internal static int HomeDistance(WorldSnapshot snap, HexCoord hex, int fallbackDistFromBase) =>
            TaskScoreEvaluator.NearestOwnedHomeDistance(snap, hex, fallbackDistFromBase);

        private static int MinDist(IReadOnlyList<HexCoord> hexes, HexCoord to) => AiV2Util.MinDist(hexes, to);

        private static bool EnemyExposedAt(WorldSnapshot snap, HexCoord hex)
        {
            IReadOnlyList<AiMapMemory.KnownEnemySighting> s = snap?.Known?.EnemySightings;
            if (s == null) return false;
            int r = AiConfigV2.frontierEnemyExposureRadius;
            foreach (AiMapMemory.KnownEnemySighting e in s)
                if (HexGridMath.Distance(e.Hex, hex) <= r) return true;
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
