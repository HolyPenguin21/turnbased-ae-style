using System.Collections.Generic;
using Game.HexGrid;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  RECON OBJECTIVE EVALUATOR  (Strategy V2 — the single Recon-opportunity enumeration)
    // ===========================================================================================
    //  "One estimator, many stages" applied to Recon opportunities. Frontier hexes + stale honest
    //  contacts are turned into ReconObjective once per AI turn, right after the radar. Both
    //  DemandLayer (sizing Scout capacity) and MissionLayer (building Scout proposals) read THIS —
    //  neither re-scans the frontier / contact list or re-derives an objective's value.
    //
    //  FROZEN FOR THE TURN. Computed BEFORE StrategicManager mutates own forces. Strategic Manager
    //  changes which SCOUT can execute an objective, never which objectives exist — so the list is
    //  NOT recomputed after the operational-state refresh. The value math here is the exact math
    //  MissionLayer used inline before this split (byte-for-byte); MissionLayer keeps only the
    //  mission-planning-specific LocalAdmissionScore (BaseValue x Recon sub-desire x risk).
    // ===========================================================================================
    public enum ReconObjectiveKind { Explore, Surveil }

    public sealed class ReconObjective
    {
        public ReconObjectiveKind Kind;
        public HexCoord FocusHex;
        public int ContactArmyId;              // Surveil only (0 for Explore)
        public EnemyContactSnapshot Contact;   // Surveil only

        public float BaseValue;                // 0..100 intrinsic merit — becomes MissionProposal.BaseValue
        public float DetectionRisk;            // [0..1]
        public StealthRequirement Stealth;

        // raw inputs kept for the "why" log / downstream tie-breaks
        public int FreshNeighbors;
        public int DistanceFromBase;
        public int AgeTurns;
        public float Severity;

        public MissionIntentKey IntentKey =>
            Kind == ReconObjectiveKind.Surveil
                ? new MissionIntentKey(MissionKind.Scout, (int)ScoutTargetKind.Surveil, ContactArmyId, 0, 0)
                : new MissionIntentKey(MissionKind.Scout, (int)ScoutTargetKind.Explore, 0, FocusHex.Q, FocusHex.R);

        public ScoutMissionTarget ToTarget() => new ScoutMissionTarget
        {
            FocusHex = FocusHex,
            Kind = Kind == ReconObjectiveKind.Surveil ? ScoutTargetKind.Surveil : ScoutTargetKind.Explore,
            Contact = Kind == ReconObjectiveKind.Surveil ? Contact : null,
            Stealth = Stealth,
            DetectionRisk = DetectionRisk,
        };
    }

    public static class ReconObjectiveEvaluator
    {
        // Every strategic Recon opportunity in this snapshot: one per frontier hex, one per stale
        // honest positioned contact.
        public static List<ReconObjective> Enumerate(WorldSnapshot snap)
        {
            var list = new List<ReconObjective>();
            if (snap?.Self == null || snap.MapKnowledge == null)
                return list;

            IReadOnlyList<FrontierHexSnapshot> frontier = snap.MapKnowledge.Frontier;
            if (frontier != null)
                foreach (FrontierHexSnapshot f in frontier)
                {
                    // §6 — the SAME Explore validity contract the continuity layer uses, so a
                    // focus can never be simultaneously "runnable fresh objective" here and
                    // "objective no longer valid" in MissionContinuity against one snapshot.
                    if (!ScoutObjectiveEvaluator.IsExploreFocusRunnable(snap, f.Hex))
                        continue;
                    list.Add(BuildExplore(snap, f.Hex, f.FreshNeighbors, f.DistanceFromNearestBase,
                        f.EnemyExposure, f.StealthDetectionRisk));
                }

            IReadOnlyList<EnemyContactSnapshot> contacts = snap.Threat?.Contacts;
            if (contacts != null)
                foreach (EnemyContactSnapshot c in contacts)
                    if (c.Source == ContactSource.Honest && c.Knowledge == ContactKnowledge.LastKnown
                        && c.Position.HasValue)
                        list.Add(BuildSurveil(snap, c));
            return list;
        }

        // Re-materialise ONE Explore objective whose hex may have left the frontier (wave band
        // moved) — the incumbent-intent path, NOT a re-scan for new objectives.
        public static ReconObjective ExploreAt(WorldSnapshot snap, HexCoord hex)
        {
            // §6 — validity is the shared runnable contract, NOT the fresh-neighbour count. An
            // unvisited, unblocked frontier focus is re-materialisable even with 0 fresh
            // neighbours; `fresh` then only feeds the objective's value/scoring.
            if (!ScoutObjectiveEvaluator.IsExploreFocusRunnable(snap, hex))
                return null;
            int fresh = ScoutObjectiveEvaluator.ExploreStillOpen(snap, hex);
            int distBase = snap?.Self?.BaseHexes != null && snap.Self.BaseHexes.Count > 0
                ? MinDist(snap.Self.BaseHexes, hex) : 0;
            bool exposed = EnemyExposedAt(snap, hex);
            bool stealthRisk = exposed && DetectorsAt(snap, hex) > 0;
            return BuildExplore(snap, hex, fresh, distBase, exposed, stealthRisk);
        }

        public static ReconObjective SurveilOf(WorldSnapshot snap, EnemyContactSnapshot c) =>
            c == null ? null : BuildSurveil(snap, c);

        // --------------------------------------------------------------------------------------

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

        // Inline mirrors of the frontier scan's enemy-exposure annotation, for a materialised
        // Explore intent whose focus hex dropped out of MapKnowledge.Frontier. Same constants.
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
