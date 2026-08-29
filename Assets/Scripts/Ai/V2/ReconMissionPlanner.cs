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
    //  MissionProposals. It NEVER re-derives the analysis behind the breakdown (that drift is
    //  risk 1 in the V2 design record) — it reads breakdown.ReconExploration / .ReconSurveillance
    //  as given and only decides WHICH concrete hex each Scout heads for.
    //
    //  TWO CANDIDATE KINDS, ONE 0..100 SCALE
    //  --------------------------------------------------------------------------------------------
    //    Explore  — a MapKnowledge.Frontier hex (already safe + reachable + inside the wave band;
    //               carries FreshNeighbors + DistanceFromNearestBase). Valued on info gain and how
    //               central it is.
    //    Surveil  — a stale HONEST contact's last-known hex (Source == Honest, Knowledge ==
    //               LastKnown, has a Position). Valued on staleness x the ThreatModel Severity
    //               already attached to that contact — Recon leans on the exact threat picture
    //               Defence will, no parallel model.
    //  A Cheat Region/Unknown contact is deliberately NOT a Surveil candidate: it has no hex a
    //  Scout could visit (type invariant on EnemyContactSnapshot). That uncertainty is
    //  enemyBlindness's concern, answered by AirRecon in a later step, not here.
    //
    //  INTRINSIC value vs SELECTION
    //  --------------------------------------------------------------------------------------------
    //    BaseValue      = Lerp(scoutBaseValueMin, scoutBaseValueMax, quality) — the mission's merit
    //                     on its own, and the ONLY thing written to MissionProposal.BaseValue. The
    //                     allocator packs on this + the radar slices; it must not already contain
    //                     the radar's Recon pull.
    //    SelectionScore = BaseValue * (Explore ? ReconExploration : ReconSurveillance). Used HERE,
    //                     once, to rank the pool and pick the winners. When exploration has
    //                     decayed to ~0 the last frontier hex still has a real BaseValue but loses
    //                     the slot to a surveil target the AI currently needs more.
    //
    //  Two winners must be at least scoutTargetMinSeparation apart — adjacent hexes are the same
    //  frontier, not two missions. Explore self-decays (ExplorableUnknownFrac), so two Explore
    //  winners on an opening map is fine as long as they are genuinely apart.
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

            // Rank by selection score, greedily take winners that are spread out on the map.
            var picked = new List<HexCoord>();
            foreach (ScoutCandidate c in candidates.OrderByDescending(x => x.SelectionScore))
            {
                if (proposals.Count >= AiConfigV2.maxConcurrentRecon)
                    break;
                if (c.SelectionScore <= 0f)
                    break; // list is sorted — nothing better follows
                if (picked.Any(h => HexGridMath.Distance(h, c.Target.FocusHex) < AiConfigV2.scoutTargetMinSeparation))
                    continue;

                proposals.Add(BuildProposal(snap, c));
                picked.Add(c.Target.FocusHex);
            }
            return proposals;
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

                var target = new ScoutMissionTarget
                {
                    FocusHex = f.Hex,
                    Kind = ScoutTargetKind.Explore,
                    Contact = null,
                };
                string explain = $"Explore @{f.Hex.Q},{f.Hex.R} opens {f.FreshNeighbors} "
                    + $"(info {F(infoGain)} prox {F(proximity)} d{f.DistanceFromNearestBase}) "
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

                var target = new ScoutMissionTarget
                {
                    FocusHex = pos,
                    Kind = ScoutTargetKind.Surveil,
                    Contact = c,
                };
                string explain = $"Surveil @{pos.Q},{pos.R} age {age} "
                    + $"(stale {F(staleness)} sev {F(maxSeverity)} prox {F(proximity)}) "
                    + $"base {F(baseValue)} x surv {F(reconSurveillance)}";
                yield return new ScoutCandidate(target, baseValue, baseValue * reconSurveillance, explain);
            }
        }

        // ------------------------------------------------------------------------------ shared ----

        // Base-distance -> [0..1], 1 close, 0 far. Distance to our OWN territory only — the cost of
        // actually getting a mover there is ScoutCostModel's job, never folded in here.
        private static float Proximity(int distanceFromNearestBase) =>
            Curves.InvRamp(distanceFromNearestBase, AiConfigV2.scoutProximityRampLo, AiConfigV2.scoutProximityRampHi);

        private static MissionProposal BuildProposal(WorldSnapshot snap, ScoutCandidate c)
        {
            ScoutCostEstimate est = ScoutCostModel.Estimate(snap, c.Target);
            var req = new MissionRequirements
            {
                MoverKnown = est.MoverKnown,
                ApMinimum = est.ActivationAp,
                ApDesired = est.ActivationAp,
                ApMaximum = est.ActivationAp + est.OptionalStealthAp,
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
