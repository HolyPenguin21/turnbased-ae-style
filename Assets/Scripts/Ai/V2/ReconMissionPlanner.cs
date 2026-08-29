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
            candidates.AddRange(SurveilCandidates(snap, breakdown.ReconSurveillance));

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

                (StealthRequirement req, float risk) = StealthFor(snap, f.Hex, f.EnemyExposure, f.StealthDetectionRisk);

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
                yield return new ScoutCandidate(target, baseValue, baseValue * reconExploration, explain);
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

                (StealthRequirement req, float risk) = StealthFor(snap, pos, null, null);

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
                yield return new ScoutCandidate(target, baseValue, baseValue * reconSurveillance, explain);
            }
        }

        // ------------------------------------------------------------------------------ shared ----

        private static float Proximity(int distanceFromNearestBase) =>
            Curves.InvRamp(distanceFromNearestBase, AiConfigV2.scoutProximityRampLo, AiConfigV2.scoutProximityRampHi);

        // Enemy exposure -> Required, always (a visible scout is not a valid executor near a known
        // non-neutral — parity with V1). DetectionRisk is non-zero only where a known force could
        // actually roll a stealth challenge on this hex. `exposureHint` / `detectHint` are the
        // frontier scan's pre-computed bools for an Explore hex; null for a Surveil hex, computed
        // here from the same current honest sightings.
        private static (StealthRequirement, float) StealthFor(WorldSnapshot snap, HexCoord hex,
            bool? exposureHint, bool? detectHint)
        {
            var sightings = snap.Known?.EnemySightings;
            int r = AiConfigV2.frontierEnemyExposureRadius;

            bool exposed = exposureHint ?? (sightings != null
                && sightings.Any(s => HexGridMath.Distance(s.Hex, hex) <= r));
            if (!exposed)
                return (StealthRequirement.None, 0f);

            int detectors = 0;
            if (sightings != null)
                foreach (var s in sightings)
                    if (HexGridMath.Distance(s.Hex, hex) <= r && s.CanDetectStealthAt(hex))
                        detectors++;
            // detectHint (true) means the scan already found >=1; keep at least that.
            if ((detectHint ?? false) && detectors == 0)
                detectors = 1;

            float risk = Mathf.Clamp01(detectors / Mathf.Max(0.0001f, AiConfigV2.scoutDetectionRiskNorm));
            return (StealthRequirement.Required, risk);
        }

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
