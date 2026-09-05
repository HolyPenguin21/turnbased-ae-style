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
    //  Three explicit Recon sub-kinds share one strategic axis:
    //    Explore — new ground information; ground route/terrain witness.
    //    Refresh — stale previously-observed information; ground route/terrain witness.
    //    Surveil — stale enemy contact; observation-vantage semantics in provisioning.
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

            // Acceptance is about the STRATEGIC lane pressures, not whichever single objective has
            // the highest BaseValue. MissionLayer is the first place where the frozen objectives
            // and the corresponding DesireBreakdown meet, so record the authoritative comparison
            // here and keep ReconObjectiveEvaluator focused on objective facts.
            var auditPlayer = snap.Self.Armies?.FirstOrDefault(a => a?.Owner != null)?.Owner;
            if (auditPlayer != null)
            {
                ReconAcceptanceAudit.RecordMostlyExploredPressure(auditPlayer, snap.TurnNumber,
                    snap.MapKnowledge.ExplorableUnknownFrac,
                    breakdown.ReconExplorePressure, breakdown.ReconRefreshPressure);
            }

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
                        AiDebugLog.Write($"[AI][V2]   mission — intent {intent.IntentKey} not materialisable this turn");
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

            IEnumerable<ScoutCandidate> ordinary = incumbents
                .Where(x => x.Tier == CommitmentTier.None)
                .Concat(fresh.Where(f => !incumbentKeys.Contains(CandidateKey(f))))
                .OrderByDescending(x => MissionAdmissionPolicy.AdmissionRank(x.LocalAdmissionScore, x.IsIncumbent, x.Tier))
                .ThenByDescending(x => ReconScoutKinds.IsExplore(x.Target.Kind) ? x.FreshNeighbors : 0)
                .ThenBy(x => CandidateKey(x));
            int ordinaryCount = 0;
            foreach (ScoutCandidate c in ordinary)
            {
                if (ordinaryCount >= AiConfigV2.scoutCandidateBeamWidth) break;
                if (!c.IsIncumbent && c.LocalAdmissionScore <= 0f) continue;
                picked.Add(c);
                ordinaryCount++;
            }

            // §8 — Mission does not decide concrete actor availability; whether an actor exists to
            // execute this proposal is Assignment's question (ReconAssignmentPlanner /
            // ProvisioningManager, which report NoMoverExists / MoverContended if none does). Mission
            // pricing (ScoutCostModel.Estimate) is actor-agnostic by construction, so there is no
            // actor-pair matching pass here any more (review finding 1) — ReconAssignmentPlanner
            // binds the real actor at Assignment time, and ProvisioningManager's envelope check +
            // ResourceAllocator's repack loop already reconcile any funded-vs-real-cost gap.
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

            // Global Recon intensity is already owned by Radar. Here only the sub-driver orders
            // concrete alternatives inside each lane. Explore retains a local floor while a real
            // frontier objective exists; generic Refresh follows frozen IntelAge pressure while
            // contact-specific Surveil keeps its own stale-contact surveillance pressure.
            float localSubDesire = explore
                ? Mathf.Lerp(0.25f, 1f, Mathf.Clamp01(rawSubDesire))
                : Mathf.Clamp01(rawSubDesire);
            float proximity = Curves.InvRamp(o.DistanceFromBase,
                AiConfigV2.scoutProximityRampLo, AiConfigV2.scoutProximityRampHi);
            float infoGain = explore
                ? Mathf.Clamp01(o.FreshNeighbors / Mathf.Max(0.0001f, AiConfigV2.scoutInfoGainNorm))
                : 0f;
            bool infoCapped = explore && o.FreshNeighbors >= AiConfigV2.scoutInfoGainNorm;

            ScoutMissionTarget target = o.ToTarget();
            // §8 — Mission ranking reflects the strategic objective only. Actor-specific route
            // executability (which mover, whether IT can currently path there) is Assignment's
            // question, not Mission's — a generic per-actor route scan here would let mover
            // availability quietly bias which objective gets proposed at all.
            float admission = ComputeLocalAdmissionScore(o.BaseValue, localSubDesire, o.DetectionRisk);

            string explain;
            if (explore)
            {
                explain = $"Explore @{o.FocusHex.Q},{o.FocusHex.R} opens {o.FreshNeighbors} d{o.DistanceFromBase} "
                    + $"info {F(infoGain)} prox {F(proximity)} infoCap {(infoCapped ? 1 : 0)}"
                    + $"{StealthTag(o.Stealth, o.DetectionRisk)} base {F(o.BaseValue)} x exploreP {F(rawSubDesire)} "
                    + $"localFloor {F(localSubDesire)} LAS {F(admission)}";
            }
            else if (refresh)
            {
                explain = $"Refresh @{o.FocusHex.Q},{o.FocusHex.R} age {o.AgeTurns} "
                    + $"strategic {F(o.StrategicRelevance)} direction {F(o.DirectionPressure)} prox {F(proximity)}"
                    + $"{StealthTag(o.Stealth, o.DetectionRisk)} base {F(o.BaseValue)} x refreshP {F(rawSubDesire)} "
                    + $"LAS {F(admission)}";
            }
            else if (surveil)
            {
                explain = $"Surveil @{o.FocusHex.Q},{o.FocusHex.R} age {o.AgeTurns} sev {F(o.Severity)} "
                    + $"prox {F(proximity)}{StealthTag(o.Stealth, o.DetectionRisk)} "
                    + $"base {F(o.BaseValue)} x surv {F(rawSubDesire)}";
            }
            else
            {
                // ReconObjectiveKind is an internal closed enum, but keep the planner fail-closed if
                // another value is ever added without materialization semantics here.
                admission = 0f;
                explain = $"UnknownReconObjective kind={(int)o.Kind} suppressed";
            }

            return new ScoutCandidate(target, o.BaseValue, admission, explain,
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
