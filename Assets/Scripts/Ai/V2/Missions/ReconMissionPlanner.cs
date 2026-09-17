using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.HexGrid;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  RECON MISSION PLANNER
    // ===========================================================================================
    // Three explicit Recon sub-kinds share one strategic axis:
    // Explore — new ground information; Refresh — stale map information;
    // Surveil — stale enemy contact. Actor assignment belongs to ReconAssignmentPlanner.
    // ===========================================================================================
    internal static class ReconMissionPlanner
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

            // Recon sub-pressure remains diagnostic/axis policy information. It must not re-score
            // an objective whose intrinsic usefulness is already fully represented by TaskScore.
            var auditPlayer = snap.Self.Armies?.FirstOrDefault(a => a?.Owner != null)?.Owner;
            if (auditPlayer != null)
                ReconAcceptanceAudit.RecordMostlyExploredPressure(auditPlayer, snap.TurnNumber,
                    snap.MapKnowledge.ExplorableUnknownFrac,
                    breakdown.ReconExplorePressure, breakdown.ReconRefreshPressure);

            var fresh = new List<ScoutCandidate>();
            foreach (ReconObjective o in objectives)
                fresh.Add(ToCandidate(snap, o, breakdown));

            var incumbents = new List<ScoutCandidate>();
            if (activeIntents != null)
                foreach (MissionIntent intent in activeIntents)
                {
                    ScoutCandidate? c = TryMaterializeIntent(snap, breakdown, intent);
                    if (c.HasValue)
                        incumbents.Add(c.Value);
                    else
                        AiDebugLog.WriteDeduped(intent.IntentKey.ToString(),
                            $"[AI][V2]   mission — intent {intent.IntentKey} not materialisable this turn");
                }

            var incumbentKeys = new HashSet<MissionIntentKey>();
            foreach (ScoutCandidate c in incumbents)
                incumbentKeys.Add(CandidateKey(c));

            var picked = new List<ScoutCandidate>();
            foreach (ScoutCandidate c in incumbents
                .Where(x => x.Tier != CommitmentTier.None)
                .OrderByDescending(x => x.LocalAdmissionScore)
                .ThenByDescending(x => ReconScoutKinds.IsExplore(x.Target.Kind) ? x.FreshNeighbors : 0)
                .ThenBy(x => CandidateKey(x)))
                picked.Add(c);

            // Continuity has already validated/refocused and concurrency-trimmed these live lanes.
            // A None-funded incumbent used to compete with all fresh jobs for the top-N beam; a
            // cheaper fresh job could push it out while ActorCommitments STILL claimed its mover.
            // After a surplus trim the other scout is intentionally barred for the rest of this
            // turn, so funding only fresh jobs then produced MoverContended for every job despite
            // a valid incumbent still owning the sole permitted lane (Mordak T11/T15).
            // Admit live incumbents into the SAME bounded beam before adding fresh alternatives.
            // Do not grant Hard funding, change intrinsic TaskScore, release an actor, or increase
            // desired concurrency. Allocator still compares scores; Assignment still owns matching.
            var ordinaryIncumbents = incumbents
                .Where(x => x.Tier == CommitmentTier.None)
                .OrderByDescending(x => MissionAdmissionPolicy.AdmissionRank(
                    x.LocalAdmissionScore, x.IsIncumbent, x.Tier))
                .ThenByDescending(x => ReconScoutKinds.IsExplore(x.Target.Kind) ? x.FreshNeighbors : 0)
                .ThenBy(x => CandidateKey(x))
                .ToList();
            picked.AddRange(ordinaryIncumbents);

            IEnumerable<ScoutCandidate> ordinary = fresh
                .Where(f => !incumbentKeys.Contains(CandidateKey(f)))
                .OrderByDescending(x => MissionAdmissionPolicy.AdmissionRank(x.LocalAdmissionScore, x.IsIncumbent, x.Tier))
                .ThenByDescending(x => ReconScoutKinds.IsExplore(x.Target.Kind) ? x.FreshNeighbors : 0)
                .ThenBy(x => CandidateKey(x));
            int ordinaryCount = ordinaryIncumbents.Count;
            foreach (ScoutCandidate c in ordinary)
            {
                if (ordinaryCount >= AiConfigV2.scoutCandidateBeamWidth) break;
                if (c.LocalAdmissionScore <= 0f) continue;
                picked.Add(c);
                ordinaryCount++;
            }

            // Planning publishes the actor against which the pre-funding envelope was priced.
            // This is still only a preference/compatibility witness: ReconAssignmentPlanner owns
            // the final one-actor/one-job binding and may rematch when live route/vantage facts move.
            foreach (ScoutCandidate c in picked)
                proposals.Add(BuildProposal(snap, c));

            return proposals;
        }

        private static MissionIntentKey CandidateKey(ScoutCandidate c) =>
            MissionIntentKey.ForScoutTarget(c.Target);

        private static ScoutCandidate? TryMaterializeIntent(WorldSnapshot snap, DesireBreakdown bd, MissionIntent intent)
        {
            ScoutIntent si = intent?.Scout;
            if (si == null)
                return null;

            ReconObjective o;
            if (ReconScoutKinds.IsExplore(si.Kind))
                o = ReconObjectiveEvaluator.ExploreAt(snap, si.FocusHex);
            else if (ReconScoutKinds.IsRefresh(si.Kind))
                o = ReconObjectiveEvaluator.RefreshAt(snap, si.FocusHex);
            else if (ReconScoutKinds.IsSurveil(si.Kind))
                o = ReconObjectiveEvaluator.SurveilOf(snap,
                    ScoutObjectiveEvaluator.SurveilContact(snap, si.TrackedArmyId));
            else
            {
                AiDebugLog.Write($"[AI][V2][Recon] intent materialize reject — unknown Scout kind {(int)si.Kind}");
                return null;
            }

            if (o == null)
                return null;
            return ToCandidate(snap, o, bd).AsIncumbent(intent.Funding, intent.PreferredMoverArmyId);
        }

        private static ScoutCandidate ToCandidate(WorldSnapshot snap, ReconObjective o, DesireBreakdown bd)
        {
            bool explore = o.Kind == ReconObjectiveKind.Explore;
            bool refresh = o.Kind == ReconObjectiveKind.Refresh;
            bool surveil = o.Kind == ReconObjectiveKind.Surveil;
            float rawSubDesire = explore
                ? bd.ReconExplorePressure
                : refresh
                    ? bd.ReconRefreshPressure
                    : surveil ? bd.ReconSurveillance : 0f;

            float proximity = Curves.InvRamp(o.DistanceFromBase,
                AiConfigV2.scoutProximityRampLo, AiConfigV2.scoutProximityRampHi);
            float infoGain = explore
                ? Mathf.Clamp01(o.FreshNeighbors / Mathf.Max(0.0001f, AiConfigV2.scoutInfoGainNorm))
                : 0f;
            bool infoCapped = explore && o.FreshNeighbors >= AiConfigV2.scoutInfoGainNorm;

            ScoutMissionTarget target = o.ToTarget();
            float admission = ComputeLocalAdmissionScore(o.BaseValue);

            string explain;
            if (explore)
            {
                explain = $"Explore @{o.FocusHex.Q},{o.FocusHex.R} opens {o.FreshNeighbors} d{o.DistanceFromBase} "
                    + $"info {F(infoGain)} prox {F(proximity)} infoCap {(infoCapped ? 1 : 0)}"
                    + $"{StealthTag(o.Stealth, o.DetectionRisk)} task {F(o.BaseValue)} "
                    + $"exploreP {F(rawSubDesire)} LAS {F(admission)}";
            }
            else if (refresh)
            {
                explain = $"Refresh @{o.FocusHex.Q},{o.FocusHex.R} age {o.AgeTurns} "
                    + $"strategic {F(o.StrategicRelevance)} direction {F(o.DirectionPressure)} prox {F(proximity)}"
                    + $"{StealthTag(o.Stealth, o.DetectionRisk)} task {F(o.BaseValue)} "
                    + $"refreshP {F(rawSubDesire)} LAS {F(admission)}";
            }
            else if (surveil)
            {
                explain = $"Surveil @{o.FocusHex.Q},{o.FocusHex.R} age {o.AgeTurns} sev {F(o.Severity)} "
                    + $"prox {F(proximity)}{StealthTag(o.Stealth, o.DetectionRisk)} "
                    + $"task {F(o.BaseValue)} survP {F(rawSubDesire)} LAS {F(admission)}";
            }
            else
            {
                admission = 0f;
                explain = $"UnknownReconObjective kind={(int)o.Kind} suppressed";
            }

            return new ScoutCandidate(target, o.BaseValue, admission, explain,
                freshNeighbors: explore ? o.FreshNeighbors : 0);
        }

        private static float ComputeLocalAdmissionScore(float taskScoreValue) => taskScoreValue;

        private static string StealthTag(StealthRequirement req, float risk) =>
            req == StealthRequirement.None ? "" : $" stealth={req} risk {F(risk)}";

        private static MissionProposal BuildProposal(WorldSnapshot snap, ScoutCandidate c)
        {
            // The estimate must price the SAME durable mover the proposal prefers. Otherwise a
            // cheaper, unrelated scout advertises an AP envelope the incumbent cannot execute.
            // For a fresh mission Estimate selects a concrete cheapest viable ground actor before
            // funding. Carry that actor as a non-binding preference so admission can reason about
            // the exact envelope it is financing; Assignment remains authoritative.
            ScoutCostEstimate est = ScoutCostModel.Estimate(snap, c.Target, c.PreferredMover);
            var req = new MissionRequirements
            {
                MoverKnown = est.MoverKnown,
                ApMinimum = est.ApMinimum,
                ApDesired = est.ApDesired,
                ApMaximum = est.ApMaximum,
                EnergyMinimum = est.EnergyMinimum,
                EnergyDesired = est.EnergyDesired,
                EnergyMaximum = est.EnergyMaximum,
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
                PreferredMoverArmyId = c.PreferredMover ?? est.PreferredMoverArmyId,
            };
            proposal.Axes.Value[DesireAxis.Recon] = 1.0f;
            return proposal;
        }

        private static string F(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    }
}
