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
            if (snap.MapContext != null
                && snap.MapContext.ExploredFraction >= 0.80f
                && breakdown.ReconRefreshPressure > breakdown.ReconExplorePressure + 0.01f)
            {
                AiDebugLog.Write($"[AI][V2][Recon][Acceptance] scenario=refresh-dominates-explored status=PASS "
                    + $"explored={snap.MapContext.ExploredFraction:0.00} "
                    + $"exploreP={breakdown.ReconExplorePressure:0.00} refreshP={breakdown.ReconRefreshPressure:0.00}");
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

            if (refresh && snap.Known?.Buildings != null
                && snap.Known.Buildings.Any(b => b.Hex.Equals(o.FocusHex)))
            {
                AiDebugLog.Write($"[AI][V2][Recon][Acceptance] scenario=stale-facility-refresh status=PASS "
                    + $"age={o.AgeTurns} strategic={o.StrategicRelevance:0.00}");
            }

            ScoutMissionTarget target = o.ToTarget();
            float intrinsicAdmission = ComputeLocalAdmissionScore(o.BaseValue, localSubDesire, o.DetectionRisk);
            ScoutRouteCostEvaluator.Assessment route = ScoutRouteCostEvaluator.Evaluate(snap, target);
            bool ground = explore || refresh;
            float routeMultiplier = ground && route.HasRoute ? route.AdmissionMultiplier : 1f;
            float admission = intrinsicAdmission * routeMultiplier;

            string explain;
            if (explore)
            {
                explain = $"Explore @{o.FocusHex.Q},{o.FocusHex.R} opens {o.FreshNeighbors} d{o.DistanceFromBase} "
                    + $"info {F(infoGain)} prox {F(proximity)} infoCap {(infoCapped ? 1 : 0)}"
                    + $"{StealthTag(o.Stealth, o.DetectionRisk)} base {F(o.BaseValue)} x exploreP {F(rawSubDesire)} "
                    + $"localFloor {F(localSubDesire)} intrinsicLAS {F(intrinsicAdmission)}"
                    + RouteExplain(route, routeMultiplier);
            }
            else if (refresh)
            {
                explain = $"Refresh @{o.FocusHex.Q},{o.FocusHex.R} age {o.AgeTurns} "
                    + $"strategic {F(o.StrategicRelevance)} direction {F(o.DirectionPressure)} prox {F(proximity)}"
                    + $"{StealthTag(o.Stealth, o.DetectionRisk)} base {F(o.BaseValue)} x refreshP {F(rawSubDesire)} "
                    + $"intrinsicLAS {F(intrinsicAdmission)}"
                    + RouteExplain(route, routeMultiplier);
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

        private static string RouteExplain(ScoutRouteCostEvaluator.Assessment route, float multiplier) =>
            route.HasRoute
                ? $" routeMP {route.MovementCost} eta {route.EtaTurns} visitsNow {route.ExpectedVisitsThisTurn} "
                  + $"remainMP {route.RemainingMovementAtFocus} routeX {F(multiplier)}"
                : " route unknown";

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
