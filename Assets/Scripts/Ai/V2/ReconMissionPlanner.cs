using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.HexGrid;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  RECON MISSION PLANNER  (Strategy V2 build-order step 4, + step 7 continuity, + step 7.1 beam)
    // ===========================================================================================
    //  Fresh target value is local (frontier information / contact staleness). The global Recon
    //  sub-driver still orders Explore vs Surveil, but Explore keeps a bounded local floor: a
    //  discontinuous global explorable fraction must not turn a concrete 5-neighbour frontier job
    //  into LAS~=0 while the objective itself still exists. Radar remains the global AP owner.
    // ===========================================================================================
    internal static class MissionLayer
    {
        private readonly struct ScoutCandidate
        {
            public readonly ScoutMissionTarget Target;
            public readonly float BaseValue;
            public readonly float LocalAdmissionScore;
            public readonly string Explain;
            public readonly int FreshNeighbors;
            public readonly bool IsIncumbent;
            public readonly CommitmentTier Tier;
            public readonly int? PreferredMover;

            public ScoutCandidate(ScoutMissionTarget target, float baseValue, float localAdmissionScore, string explain,
                bool isIncumbent = false, CommitmentTier tier = CommitmentTier.None, int? preferredMover = null,
                int freshNeighbors = 0)
            {
                Target = target;
                BaseValue = baseValue;
                LocalAdmissionScore = localAdmissionScore;
                Explain = explain;
                FreshNeighbors = freshNeighbors;
                IsIncumbent = isIncumbent;
                Tier = tier;
                PreferredMover = preferredMover;
            }

            public ScoutCandidate AsIncumbent(CommitmentTier tier, int? preferredMover) =>
                new ScoutCandidate(Target, BaseValue, LocalAdmissionScore, Explain + " [incumbent]", true, tier,
                    preferredMover, FreshNeighbors);
        }

        public static List<MissionProposal> Propose(WorldSnapshot snap, DesireBreakdown breakdown,
            IReadOnlyList<MissionIntent> activeIntents,
            IReadOnlyList<ReconObjective> frozenObjectives = null)
        {
            var proposals = new List<MissionProposal>();
            if (snap?.Self == null || snap.MapKnowledge == null || breakdown == null)
                return proposals;

            IReadOnlyList<ReconObjective> objectives = frozenObjectives ?? ReconObjectiveEvaluator.Enumerate(snap);
            var fresh = new List<ScoutCandidate>();
            foreach (ReconObjective o in objectives)
                fresh.Add(ToCandidate(o, breakdown));

            var incumbents = new List<ScoutCandidate>();
            if (activeIntents != null)
                foreach (MissionIntent intent in activeIntents)
                {
                    ScoutCandidate? c = TryMaterializeIntent(snap, breakdown, intent);
                    if (c.HasValue)
                        incumbents.Add(c.Value);
                    else
                        AiDebugLog.Write($"[AI][V2]   mission — intent {intent.IntentKey} not materialisable this turn");
                }

            var incumbentKeys = new HashSet<MissionIntentKey>();
            foreach (ScoutCandidate c in incumbents)
                incumbentKeys.Add(CandidateKey(c));

            var picked = new List<ScoutCandidate>();
            foreach (ScoutCandidate c in incumbents
                .Where(x => x.Tier != CommitmentTier.None)
                .OrderByDescending(x => x.LocalAdmissionScore)
                .ThenByDescending(x => x.Target.Kind == ScoutTargetKind.Explore ? x.FreshNeighbors : 0)
                .ThenBy(x => CandidateKey(x)))
                picked.Add(c);

            IEnumerable<ScoutCandidate> ordinary = incumbents
                .Where(x => x.Tier == CommitmentTier.None)
                .Concat(fresh.Where(f => !incumbentKeys.Contains(CandidateKey(f))))
                .OrderByDescending(x => MissionAdmissionPolicy.AdmissionRank(x.LocalAdmissionScore, x.IsIncumbent, x.Tier))
                .ThenByDescending(x => x.Target.Kind == ScoutTargetKind.Explore ? x.FreshNeighbors : 0)
                .ThenBy(x => CandidateKey(x));
            int ordinaryCount = 0;
            foreach (ScoutCandidate c in ordinary)
            {
                if (ordinaryCount >= AiConfigV2.scoutCandidateBeamWidth) break;
                if (!c.IsIncumbent && c.LocalAdmissionScore <= 0f) continue;
                picked.Add(c);
                ordinaryCount++;
            }

            foreach (ScoutCandidate c in picked)
            {
                if (!ScoutMoverSelector.HasStructuralCandidate(snap, c.Target))
                {
                    AiDebugLog.Write($"[AI][V2]   mission suppress — Scout {CandidateKey(c)} "
                        + "reason=no_materialized_scout_after_phaseA");
                    continue;
                }
                proposals.Add(BuildProposal(snap, c));
            }

            ScoutPricingWitness.Apply(snap, proposals);
            return proposals;
        }

        private static MissionIntentKey CandidateKey(ScoutCandidate c) =>
            MissionIntentKey.ForScoutTarget(c.Target);

        private static ScoutCandidate? TryMaterializeIntent(WorldSnapshot snap, DesireBreakdown bd, MissionIntent intent)
        {
            ScoutIntent si = intent?.Scout;
            if (si == null)
                return null;

            ReconObjective o = si.Kind == ScoutTargetKind.Explore
                ? ReconObjectiveEvaluator.ExploreAt(snap, si.FocusHex)
                : ReconObjectiveEvaluator.SurveilOf(snap, ScoutObjectiveEvaluator.SurveilContact(snap, si.TrackedArmyId));
            if (o == null)
                return null;
            return ToCandidate(o, bd).AsIncumbent(intent.Funding, intent.PreferredMoverArmyId);
        }

        private static ScoutCandidate ToCandidate(ReconObjective o, DesireBreakdown bd)
        {
            bool explore = o.Kind == ReconObjectiveKind.Explore;
            float rawSubDesire = explore ? bd.ReconExploration : bd.ReconSurveillance;
            // The radar already carries the global Recon intensity. Here the sub-driver is only an
            // Explore-vs-Surveil local preference. Preserve a quarter-strength local Explore signal
            // while a concrete frontier objective exists, so reachability/flood discontinuities do
            // not collapse a valid mission to zero and continuity does not cling to a 0-LAS ghost.
            float localSubDesire = explore
                ? Mathf.Lerp(0.25f, 1f, Mathf.Clamp01(rawSubDesire))
                : Mathf.Clamp01(rawSubDesire);
            float proximity = Curves.InvRamp(o.DistanceFromBase,
                AiConfigV2.scoutProximityRampLo, AiConfigV2.scoutProximityRampHi);
            float infoGain = explore
                ? Mathf.Clamp01(o.FreshNeighbors / Mathf.Max(0.0001f, AiConfigV2.scoutInfoGainNorm))
                : 0f;
            bool infoCapped = explore && o.FreshNeighbors >= AiConfigV2.scoutInfoGainNorm;
            string explain = explore
                ? $"Explore @{o.FocusHex.Q},{o.FocusHex.R} opens {o.FreshNeighbors} d{o.DistanceFromBase} "
                  + $"info {F(infoGain)} prox {F(proximity)} infoCap {(infoCapped ? 1 : 0)}"
                  + $"{StealthTag(o.Stealth, o.DetectionRisk)} base {F(o.BaseValue)} x explore {F(rawSubDesire)} "
                  + $"localFloor {F(localSubDesire)}"
                : $"Surveil @{o.FocusHex.Q},{o.FocusHex.R} age {o.AgeTurns} sev {F(o.Severity)} "
                  + $"prox {F(proximity)}{StealthTag(o.Stealth, o.DetectionRisk)} "
                  + $"base {F(o.BaseValue)} x surv {F(rawSubDesire)}";
            return new ScoutCandidate(o.ToTarget(), o.BaseValue,
                ComputeLocalAdmissionScore(o.BaseValue, localSubDesire, o.DetectionRisk), explain,
                freshNeighbors: explore ? o.FreshNeighbors : 0);
        }

        private static float ComputeLocalAdmissionScore(float baseValue, float subDesire, float detectionRisk) =>
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
                LocalAdmissionScore = c.LocalAdmissionScore,
                FromDurableIntent = c.IsIncumbent,
                DurableFundingTier = c.Tier,
                Explain = c.Explain,
                PreferredMoverArmyId = c.PreferredMover,
            };
            proposal.Axes.Value[DesireAxis.Recon] = 1.0f;
            return proposal;
        }

        private static string F(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    }
}
