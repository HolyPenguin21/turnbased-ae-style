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
    //    Refresh  — previously observed map information whose IntelAge is stale again.
    //    Surveil  — stale last-known enemy contact that requires an observation vantage.
    // ===========================================================================================
    public enum ReconObjectiveKind { Explore, Refresh, Surveil }

    public sealed class ReconObjective
    {
        public ReconObjectiveKind Kind;
        public HexCoord FocusHex;
        public int ContactArmyId;              // Surveil only
        public EnemyContactSnapshot Contact;   // Surveil only

        public float BaseValue;
        public float DetectionRisk;
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
                        && c.Position.HasValue)
                        list.Add(BuildSurveil(snap, c));

            // Objective-level acceptance is limited to facts this layer owns: the sanitized
            // direction boundary and whether direction pressure enters the best Refresh candidate.
            // Explore-vs-Refresh strategic pressure is audited in MissionLayer where the matching
            // frozen DesireBreakdown is available.
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

            // Bound enumeration before MissionLayer's ordinary cross-objective beam. Keep a wider
            // pool than execution capacity so several scouts can spread, but do not hand hundreds
            // of stale hexes to the allocator on a late-game map. Use the effective policy cap so
            // ReconOnly's 3-scout acceptance mode is represented here too.
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

        private static ReconObjective BuildExplore(WorldSnapshot snap, HexCoord hex, int freshNeighbors,
            int distFromBase, bool enemyExposure, bool stealthDetectionRisk)
        {
            float infoGain = Mathf.Clamp01(freshNeighbors / Mathf.Max(0.0001f, AiConfigV2.scoutInfoGainNorm));
            float proximity = Proximity(distFromBase);

            float wSum = AiConfigV2.scoutInfoGainWeight + AiConfigV2.scoutStrategicProximityWeight;
            float quality = Mathf.Clamp01(
                (AiConfigV2.scoutInfoGainWeight * infoGain
                 + AiConfigV2.scoutStrategicProximityWeight * proximity) / Mathf.Max(0.0001f, wSum));
            float baseValue = Mathf.Lerp(AiConfigV2.scoutBaseValueMin, AiConfigV2.scoutBaseValueMax, quality);

            StealthRequirement req = enemyExposure ? StealthRequirement.Required : StealthRequirement.None;
            float risk = enemyExposure
                ? Mathf.Max(stealthDetectionRisk ? 1f / Mathf.Max(0.0001f, AiConfigV2.scoutDetectionRiskNorm) : 0f,
                    ScoutRiskModel.DetectorRisk(snap, hex))
                : 0f;

            return new ReconObjective
            {
                Kind = ReconObjectiveKind.Explore,
                FocusHex = hex,
                BaseValue = baseValue,
                DetectionRisk = risk,
                Stealth = req,
                FreshNeighbors = freshNeighbors,
                DistanceFromBase = distFromBase,
            };
        }

        private static ReconObjective BuildRefresh(WorldSnapshot snap, HexCoord hex, int age)
        {
            IReadOnlyList<HexCoord> bases = snap.Self.BaseHexes;
            int distBase = bases != null && bases.Count > 0 ? MinDist(bases, hex) : 0;
            float stale = Curves.Ramp(age, AiConfigV2.scoutSurveilStaleTurnsLo,
                AiConfigV2.scoutSurveilStaleTurnsHi);
            float proximity = Proximity(distBase);

            float strategic = StrategicRefreshRelevance(snap, hex);
            ReconDirectionSnapshot direction = ReconDirectionModel.Build(snap);
            ReconSector sector = ReconDirectionModel.Sector(snap.Self.Citadel, hex);
            float directional = direction?.EnemyDirectionSectors != null
                && direction.EnemyDirectionSectors.TryGetValue(sector, out float pressure)
                    ? Mathf.Clamp01(pressure)
                    : 0f;
            if (direction?.KnownEnemyCitadelDirection == sector)
                directional = Mathf.Max(directional, 0.75f);

            // Refresh is information maintenance: age is primary, then known strategic content and
            // the sanitized six-sector enemy pressure; proximity keeps it from sending a ground
            // scout across the entire map for an equally stale low-value cell.
            const float staleW = 0.45f;
            const float strategicW = 0.30f;
            const float directionW = 0.15f;
            const float proximityW = 0.10f;
            float quality = Mathf.Clamp01(staleW * stale + strategicW * strategic
                + directionW * directional + proximityW * proximity);
            float baseValue = Mathf.Lerp(AiConfigV2.scoutBaseValueMin,
                AiConfigV2.scoutBaseValueMax, quality);

            bool exposed = EnemyExposedAt(snap, hex);
            float risk = exposed ? ScoutRiskModel.DetectorRisk(snap, hex) : 0f;
            var objective = new ReconObjective
            {
                Kind = ReconObjectiveKind.Refresh,
                FocusHex = hex,
                BaseValue = baseValue,
                DetectionRisk = risk,
                Stealth = exposed ? StealthRequirement.Required : StealthRequirement.None,
                DistanceFromBase = distBase,
                AgeTurns = age,
                StrategicRelevance = strategic,
                DirectionPressure = directional,
            };

            if (strategic > 0f)
            {
                var auditPlayer = snap.Self.Armies?.FirstOrDefault(a => a?.Owner != null)?.Owner;
                if (auditPlayer != null)
                    ReconAcceptanceAudit.RecordStaleStrategicRefresh(auditPlayer, snap.TurnNumber,
                        hex, age, strategic);
            }
            return objective;
        }

        private static ReconObjective BuildSurveil(WorldSnapshot snap, EnemyContactSnapshot c)
        {
            IReadOnlyList<AssetThreatSnapshot> threats = snap.Threat?.Threats;
            IReadOnlyList<HexCoord> bases = snap.Self.BaseHexes;

            HexCoord pos = c.Position.Value;
            int age = c.AgeTurns(snap.TurnNumber);
            float staleness = Curves.Ramp(age, AiConfigV2.scoutSurveilStaleTurnsLo, AiConfigV2.scoutSurveilStaleTurnsHi);

            float maxSeverity = 0f;
            if (threats != null)
                foreach (AssetThreatSnapshot t in threats)
                    if (ReferenceEquals(t.Contact, c) && t.Severity > maxSeverity)
                        maxSeverity = t.Severity;

            float threatRelevance = Mathf.Clamp01(staleness * maxSeverity);
            float proximity = bases != null && bases.Count > 0 ? Proximity(MinDist(bases, pos)) : 0f;

            float wSum = AiConfigV2.scoutStrategicProximityWeight + AiConfigV2.scoutThreatWeight;
            float quality = Mathf.Clamp01(
                (AiConfigV2.scoutStrategicProximityWeight * proximity
                 + AiConfigV2.scoutThreatWeight * threatRelevance) / Mathf.Max(0.0001f, wSum));
            float baseValue = Mathf.Lerp(AiConfigV2.scoutBaseValueMin, AiConfigV2.scoutBaseValueMax, quality);

            float risk = Mathf.Clamp01(Mathf.Max(
                c.Confidence * AiConfigV2.scoutSurveilBaseDetectionRisk, ScoutRiskModel.DetectorRisk(snap, pos)));

            return new ReconObjective
            {
                Kind = ReconObjectiveKind.Surveil,
                FocusHex = pos,
                ContactArmyId = c.Army?.ArmyId ?? 0,
                Contact = c,
                BaseValue = baseValue,
                DetectionRisk = risk,
                Stealth = StealthRequirement.Required,
                AgeTurns = age,
                Severity = maxSeverity,
                DistanceFromBase = bases != null && bases.Count > 0 ? MinDist(bases, pos) : 0,
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
                foreach (KeyValuePair<HexCoord, Game.Economy.ResourceType> r in snap.Known.ResourceHexes)
                {
                    int d = HexGridMath.Distance(r.Key, hex);
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

        private static float Proximity(int distanceFromNearestBase) =>
            Curves.InvRamp(distanceFromNearestBase, AiConfigV2.scoutProximityRampLo, AiConfigV2.scoutProximityRampHi);

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
