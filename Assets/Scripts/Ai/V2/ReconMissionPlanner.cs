using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.HexGrid;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  RECON MISSION PLANNER  (Strategy V2 build-order step 4)  — implements MissionLayer
    // ===========================================================================================
    //  One WorldSnapshot + the Recon DesireBreakdown -> up to AiConfigV2.maxConcurrentRecon Scout
    //  MissionProposals. It NEVER re-derives the analysis behind the breakdown — it reads
    //  breakdown.ReconExploration / .ReconSurveillance as given and only decides WHICH concrete
    //  hex each Scout heads for, and how hidden its executor must be.
    //
    //  TWO CANDIDATE KINDS, ONE 0..100 SCALE
    //    Explore  — a MapKnowledge.Frontier hex (already inside the wave band + touching reachable
    //               ground; carries FreshNeighbors, DistanceFromNearestBase, and the enemy-
    //               exposure annotation). Valued on info gain + centrality.
    //    Surveil  — a stale HONEST positioned contact (Source == Honest, Knowledge == LastKnown).
    //               Now that AiReconMemory retains contacts past V1's 2-turn tactical window, the
    //               staleness x ThreatModel-severity term is actually reachable. Valued on
    //               staleness x the severity already attached to that contact.
    //
    //  STEALTH REQUIREMENT (parity with V1's hard exclusion)
    //    !EnemyExposure                        -> None
    //    EnemyExposure, no detector            -> Required, DetectionRisk 0
    //    EnemyExposure, a detector can see here-> Required, DetectionRisk > 0
    //  Required means "the executor must be hidden by the risky leg" — an already-hidden scout
    //  satisfies it for free. A visible, already-activated scout is not a valid executor; that is
    //  ScoutCostModel's eligibility filter, and if nothing qualifies the proposal still forms
    //  (MoverKnown false) and Provisioning resolves or fails it — the mission is never dropped
    //  here for lack of a mover.
    //
    //  INTRINSIC value vs SELECTION
    //    BaseValue      = Lerp(scoutBaseValueMin, scoutBaseValueMax, quality) — the ONLY thing in
    //                     MissionProposal.BaseValue. The allocator packs on this + the radar slices.
    //    SelectionScore = BaseValue * (Explore ? ReconExploration : ReconSurveillance). Used HERE
    //                     only, to rank the pool and pick winners.
    //
    //  DEDUP: identical FocusHex -> never both. Explore+Explore -> at least
    //  scoutTargetMinSeparation apart. Explore+Surveil and Surveil+Surveil -> allowed (different
    //  information tasks; one physical army yields one Surveil contact anyway).
    // ===========================================================================================
    internal static class MissionLayer
    {
        private readonly struct ScoutCandidate
        {
            public readonly ScoutMissionTarget Target;
            public readonly float BaseValue;
            public readonly float SelectionScore;
            public readonly string Explain;

            public ScoutCandidate(ScoutMissionTarget target, float baseValue, float selectionScore, string explain)
            {
                Target = target;
                BaseValue = baseValue;
                SelectionScore = selectionScore;
                Explain = explain;
            }
        }

        public static List<MissionProposal> Propose(WorldSnapshot snap, DesireBreakdown breakdown)
        {
            var proposals = new List<MissionProposal>();
            if (snap?.Self == null || snap.MapKnowledge == null || breakdown == null)
                return proposals;

            var candidates = new List<ScoutCandidate>();
            candidates.AddRange(ExploreCandidates(snap, breakdown.ReconExploration));
            // build-order step 6a is Explore end-to-end ONLY. Surveil has a different spatial
            // contract (FocusHex != ExecutionHex — you observe the target hex from a safe vantage,
            // you do NOT step onto a stale enemy's last-known hex) and needs SurveilVantageSelector
            // in provisioning, which is step 6b. Until then Surveil must not reach the allocator /
            // executor at all — publishing it here with ExecutionHex = FocusHex would march a solo
            // scout straight at the enemy. SurveilCandidates() is kept (and unit-testable) but
            // deliberately not wired in.
            // TODO(6b): candidates.AddRange(SurveilCandidates(snap, breakdown.ReconSurveillance));

            var picked = new List<ScoutCandidate>();
            foreach (ScoutCandidate c in candidates.OrderByDescending(x => x.SelectionScore))
            {
                if (picked.Count >= AiConfigV2.maxConcurrentRecon)
                    break;
                if (c.SelectionScore <= 0f)
                    break; // sorted — nothing better follows
                if (picked.Any(p => Conflicts(p, c)))
                    continue;
                picked.Add(c);
            }

            foreach (ScoutCandidate c in picked)
                proposals.Add(BuildProposal(snap, c));
            return proposals;
        }

        // Identical hex is never allowed. Two Explores must be spread out. An Explore and a
        // Surveil (or two Surveils) on nearby-but-different hexes are genuinely different jobs.
        private static bool Conflicts(ScoutCandidate a, ScoutCandidate b)
        {
            if (a.Target.FocusHex.Equals(b.Target.FocusHex))
                return true;
            bool bothExplore = a.Target.Kind == ScoutTargetKind.Explore && b.Target.Kind == ScoutTargetKind.Explore;
            return bothExplore
                && HexGridMath.Distance(a.Target.FocusHex, b.Target.FocusHex) < AiConfigV2.scoutTargetMinSeparation;
        }

        // --------------------------------------------------------------------------- Explore ----
        private static IEnumerable<ScoutCandidate> ExploreCandidates(WorldSnapshot snap, float reconExploration)
        {
            IReadOnlyList<FrontierHexSnapshot> frontier = snap.MapKnowledge.Frontier;
            if (frontier == null)
                yield break;

            foreach (FrontierHexSnapshot f in frontier)
            {
                float infoGain = Mathf.Clamp01(f.FreshNeighbors / Mathf.Max(0.0001f, AiConfigV2.scoutInfoGainNorm));
                float proximity = Proximity(f.DistanceFromNearestBase);

                float wSum = AiConfigV2.scoutInfoGainWeight + AiConfigV2.scoutStrategicProximityWeight;
                float quality = Mathf.Clamp01(
                    (AiConfigV2.scoutInfoGainWeight * infoGain
                     + AiConfigV2.scoutStrategicProximityWeight * proximity) / Mathf.Max(0.0001f, wSum));
                float baseValue = Mathf.Lerp(AiConfigV2.scoutBaseValueMin, AiConfigV2.scoutBaseValueMax, quality);

                StealthRequirement req = f.EnemyExposure ? StealthRequirement.Required : StealthRequirement.None;
                float risk = f.EnemyExposure
                    ? Mathf.Max(f.StealthDetectionRisk ? 1f / Mathf.Max(0.0001f, AiConfigV2.scoutDetectionRiskNorm) : 0f,
                        CurrentDetectorRisk(snap, f.Hex))
                    : 0f;

                var target = new ScoutMissionTarget
                {
                    FocusHex = f.Hex,
                    Kind = ScoutTargetKind.Explore,
                    Contact = null,
                    Stealth = req,
                    DetectionRisk = risk,
                };
                string explain = $"Explore @{f.Hex.Q},{f.Hex.R} opens {f.FreshNeighbors} "
                    + $"(info {F(infoGain)} prox {F(proximity)} d{f.DistanceFromNearestBase}{StealthTag(req, risk)}) "
                    + $"base {F(baseValue)} x explore {F(reconExploration)}";
                yield return new ScoutCandidate(target, baseValue, Selection(baseValue, reconExploration, risk), explain);
            }
        }

        // --------------------------------------------------------------------------- Surveil ----
        private static IEnumerable<ScoutCandidate> SurveilCandidates(WorldSnapshot snap, float reconSurveillance)
        {
            IReadOnlyList<EnemyContactSnapshot> contacts = snap.Threat?.Contacts;
            if (contacts == null)
                yield break;

            IReadOnlyList<AssetThreatSnapshot> threats = snap.Threat.Threats;
            IReadOnlyList<HexCoord> bases = snap.Self.BaseHexes;

            foreach (EnemyContactSnapshot c in contacts)
            {
                if (c.Source != ContactSource.Honest || c.Knowledge != ContactKnowledge.LastKnown || !c.Position.HasValue)
                    continue;

                HexCoord pos = c.Position.Value;
                int age = c.AgeTurns(snap.TurnNumber);
                float staleness = Curves.Ramp(age, AiConfigV2.scoutSurveilStaleTurnsLo, AiConfigV2.scoutSurveilStaleTurnsHi);

                float maxSeverity = 0f;
                if (threats != null)
                    foreach (AssetThreatSnapshot t in threats)
                        if (ReferenceEquals(t.Contact, c) && t.Severity > maxSeverity)
                            maxSeverity = t.Severity;

                float threatRelevance = Mathf.Clamp01(staleness * maxSeverity);
                float proximity = bases != null && bases.Count > 0
                    ? Proximity(bases.Min(b => HexGridMath.Distance(b, pos)))
                    : 0f;

                float wSum = AiConfigV2.scoutStrategicProximityWeight + AiConfigV2.scoutThreatWeight;
                float quality = Mathf.Clamp01(
                    (AiConfigV2.scoutStrategicProximityWeight * proximity
                     + AiConfigV2.scoutThreatWeight * threatRelevance) / Mathf.Max(0.0001f, wSum));
                float baseValue = Mathf.Lerp(AiConfigV2.scoutBaseValueMin, AiConfigV2.scoutBaseValueMax, quality);

                // A Surveil target IS a (stale) enemy contact — always stealth-Required. Its own
                // last-known hex carries a confidence-scaled base risk (if the army is still there,
                // co-located it can detect stealth); any currently-known detectors add on top.
                StealthRequirement req = StealthRequirement.Required;
                float risk = Mathf.Clamp01(Mathf.Max(
                    c.Confidence * AiConfigV2.scoutSurveilBaseDetectionRisk,
                    CurrentDetectorRisk(snap, pos)));

                var target = new ScoutMissionTarget
                {
                    FocusHex = pos,
                    Kind = ScoutTargetKind.Surveil,
                    Contact = c,
                    Stealth = req,
                    DetectionRisk = risk,
                };
                string explain = $"Surveil @{pos.Q},{pos.R} age {age} "
                    + $"(stale {F(staleness)} sev {F(maxSeverity)} prox {F(proximity)}{StealthTag(req, risk)}) "
                    + $"base {F(baseValue)} x surv {F(reconSurveillance)}";
                yield return new ScoutCandidate(target, baseValue, Selection(baseValue, reconSurveillance, risk), explain);
            }
        }

        // ------------------------------------------------------------------------------ shared ----

        private static float Proximity(int distanceFromNearestBase) =>
            Curves.InvRamp(distanceFromNearestBase, AiConfigV2.scoutProximityRampLo, AiConfigV2.scoutProximityRampHi);

        // [0..1] risk from CURRENTLY known non-neutral forces that could actually roll a stealth
        // challenge on `hex` (KnownEnemySighting.CanDetectStealthAt). Count-based for now;
        // RecceSpotStrength-weighted pressure is a later refinement.
        private static float CurrentDetectorRisk(WorldSnapshot snap, HexCoord hex)
        {
            var sightings = snap.Known?.EnemySightings;
            if (sightings == null) return 0f;
            int r = AiConfigV2.frontierEnemyExposureRadius;
            int detectors = 0;
            foreach (var s in sightings)
                if (HexGridMath.Distance(s.Hex, hex) <= r && s.CanDetectStealthAt(hex))
                    detectors++;
            return Mathf.Clamp01(detectors / Mathf.Max(0.0001f, AiConfigV2.scoutDetectionRiskNorm));
        }

        // SelectionScore = BaseValue * the relevant Recon sub-desire * an execution-risk factor.
        // The risk factor stays OUT of BaseValue (and therefore out of the radar) — it is not
        // intrinsic information value, just a tie-breaker toward the safer of two equal jobs.
        private static float Selection(float baseValue, float subDesire, float detectionRisk) =>
            baseValue * subDesire
            * Mathf.Clamp01(1f - AiConfigV2.scoutDetectionRiskSelectionPenalty * detectionRisk);

        private static string StealthTag(StealthRequirement req, float risk) =>
            req == StealthRequirement.None ? "" : $" stealth={req} risk {F(risk)}";

        private static MissionProposal BuildProposal(WorldSnapshot snap, ScoutCandidate c)
        {
            ScoutCostEstimate est = ScoutCostModel.Estimate(snap, c.Target);
            var req = new MissionRequirements
            {
                MoverKnown = est.MoverKnown,
                ApMinimum = est.ApMinimum,
                ApDesired = est.ApDesired,
                ApMaximum = est.ApMaximum,
                EnergyMinimum = est.ActivationEnergy,
                EnergyDesired = est.ActivationEnergy,
                EnergyMaximum = est.ActivationEnergy,
                EtaTurns = est.EtaTurns,
                EstimatedDistance = est.EstimatedDistance,
            };

            var proposal = new MissionProposal
            {
                Kind = MissionKind.Scout,
                Target = c.Target,
                BaseValue = c.BaseValue,
                Requirements = req,
                SelectionScore = c.SelectionScore,
                Explain = c.Explain,
            };
            proposal.Axes.Value[DesireAxis.Recon] = 1.0f;
            return proposal;
        }

        private static string F(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    }
}
