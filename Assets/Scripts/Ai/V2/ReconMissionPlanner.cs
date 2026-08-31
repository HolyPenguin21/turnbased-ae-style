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
    //  One WorldSnapshot + the Recon DesireBreakdown -> a CANDIDATE BEAM of up to
    //  AiConfigV2.scoutCandidateBeamWidth Scout MissionProposals. It NEVER re-derives the analysis
    //  behind the breakdown — it reads breakdown.ReconExploration / .ReconSurveillance as given and
    //  only decides WHICH concrete hex each Scout heads for, and how hidden its executor must be.
    //
    //  TWO CANDIDATE KINDS, ONE 0..100 SCALE
    //    Explore  — a MapKnowledge.Frontier hex. Valued on info gain + centrality.
    //    Surveil  — a stale HONEST positioned contact (Source == Honest, Knowledge == LastKnown).
    //               Valued on staleness x the severity already attached to that contact.
    //
    //  STEP-7 CONTINUITY — this is the ONE place that turns a durable MissionIntent back into a
    //  concrete proposal (Intent != Proposal). Every active intent is re-materialised from the
    //  CURRENT snapshot (fresh cost via ScoutCostModel, fresh vantage later in provisioning), so
    //  there is no second proposal-builder inside the continuity layer.
    //
    //  STEP-7.1 — N (beam width) is separated from K (execution capacity). MissionLayer now emits
    //  N alternatives, NOT K winners:
    //    · every valid Soft/Hard incumbent materialises unconditionally, ON TOP of the beam — a
    //      funding-protected commitment cannot vanish because the fresh beam is full;
    //    · None-tier incumbents + fresh candidates compete for the scoutCandidateBeamWidth ordinary
    //      slots, ranked by MissionAdmissionPolicy.AdmissionRank (retarget hysteresis for a
    //      None-tier incumbent — one formula, shared with the allocator's K-cut);
    //    · a fresh candidate with the same strategic identity as an incumbent is dropped (the
    //      incumbent carries PreferredMover / continuity metadata);
    //    · pairwise execution conflicts are NOT applied here — full alternatives must survive to
    //      the allocator so its bounded re-pack can fall through to a backup. Conflicts + K are
    //      MissionAdmissionPolicy's / ResourceAllocator's job.
    //
    //  POST-PHASE-A EXECUTABILITY
    //    The pipeline calls this planner AFTER StrategicManager Phase A. Therefore the current
    //    snapshot already includes every Scout card the AI could afford/materialize for this turn.
    //    If no structural Scout actor exists at this point, emitting Explore/Surveil proposals is
    //    knowingly impossible and only causes NoMoverExists -> re-pack churn. Such proposals are
    //    suppressed here. A spent/claimed Scout is different: it still structurally exists, so
    //    normal contention/continuity semantics remain downstream.
    //
    //  INTRINSIC value vs LOCAL ADMISSION
    //    BaseValue           = Lerp(scoutBaseValueMin, scoutBaseValueMax, quality) — the ONLY thing
    //                          in MissionProposal.BaseValue. Cross-lane ordering + radar slices.
    //    LocalAdmissionScore = BaseValue * (Explore ? ReconExploration : ReconSurveillance) *
    //                          riskFactor. Ranks Recon alternatives against each other — here for
    //                          the beam, and in the allocator for the K-cut. Never cross-lane.
    // ===========================================================================================
    internal static class MissionLayer
    {
        private readonly struct ScoutCandidate
        {
            public readonly ScoutMissionTarget Target;
            public readonly float BaseValue;
            public readonly float LocalAdmissionScore;
            public readonly string Explain;
            // Raw Explore information survives BaseValue saturation and is used only as a secondary
            // local tie-break. This keeps the shared 0..100 scale unchanged while ensuring that an
            // otherwise identical opens=5 objective outranks opens=4 instead of falling to key order.
            public readonly int FreshNeighbors;

            // Step 7 — set only when this candidate was re-materialised from a durable intent.
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

            // Fresh candidates from the turn's FROZEN Recon objectives (ReconObjectiveEvaluator ran
            // once, right after the radar, BEFORE StrategicManager touched own forces). The list is
            // passed in by the pipeline; a bare test / sim that omits it gets a fresh enumeration.
            IReadOnlyList<ReconObjective> objectives = frozenObjectives ?? ReconObjectiveEvaluator.Enumerate(snap);
            var fresh = new List<ScoutCandidate>();
            foreach (ReconObjective o in objectives)
                fresh.Add(ToCandidate(o, breakdown));

            // Incumbent candidates — every still-valid durable intent, re-materialised here.
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

            // Strategic identity of every incumbent — a fresh candidate with the SAME identity is a
            // duplicate the incumbent already covers (and covers better: it carries PreferredMover
            // + continuity metadata), so the fresh copy is dropped before ranking.
            var incumbentKeys = new HashSet<MissionIntentKey>();
            foreach (ScoutCandidate c in incumbents)
                incumbentKeys.Add(CandidateKey(c));

            var picked = new List<ScoutCandidate>();

            // 1. Every valid Soft/Hard incumbent materialises unconditionally — a funding-protected
            //    commitment cannot disappear because the ordinary beam is full. Emitted ON TOP of
            //    the beam (not counted against scoutCandidateBeamWidth). No conflict filtering —
            //    the allocator (MissionAdmissionPolicy) owns execution conflicts + K now. Score
            //    only orders them against each other; the stable key breaks ties.
            foreach (ScoutCandidate c in incumbents
                .Where(x => x.Tier != CommitmentTier.None)
                .OrderByDescending(x => x.LocalAdmissionScore)
                .ThenByDescending(x => x.Target.Kind == ScoutTargetKind.Explore ? x.FreshNeighbors : 0)
                .ThenBy(x => CandidateKey(x)))
                picked.Add(c);

            // 2. The ordinary beam: None-tier incumbents + fresh candidates (minus fresh duplicates
            //    of ANY incumbent), ranked by the SHARED admission rank so the retarget hysteresis
            //    for a None-tier incumbent is the exact same formula the allocator's K-cut uses,
            //    then truncated to scoutCandidateBeamWidth. Pairwise conflicts are deliberately NOT
            //    applied — full alternatives must reach the allocator so its bounded re-pack can
            //    fall through to a backup when a higher pick fails provisioning.
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
                if (!c.IsIncumbent && c.LocalAdmissionScore <= 0f) continue; // fresh needs positive merit; an incumbent may ride at ~0
                picked.Add(c);
                ordinaryCount++;
            }

            foreach (ScoutCandidate c in picked)
            {
                // Phase A has already had the chance to satisfy ScoutCapability demand from the
                // hand/generators using the real AP/resources. At this boundary a structural miss
                // means this is not an executable task this turn. Do not let a target-specific
                // mission key hide that lane-wide fact and burn allocator re-pack attempts.
                if (!ScoutMoverSelector.HasStructuralCandidate(snap, c.Target))
                {
                    AiDebugLog.Write($"[AI][V2]   mission suppress — Scout {CandidateKey(c)} "
                        + "reason=no_materialized_scout_after_phaseA");
                    continue;
                }
                proposals.Add(BuildProposal(snap, c));
            }
            return proposals;
        }

        // Deterministic tie-break for candidate ranking + the dedup key — the SAME strategic
        // identity the intent registry and the allocator use (Surveil keyed by tracked ArmyId, so
        // two Surveils on one hex don't collapse). Keeps beam selection from depending on LINQ's
        // input order (registry iteration order for incumbents, Threat.Contacts order for Surveil).
        private static MissionIntentKey CandidateKey(ScoutCandidate c) =>
            MissionIntentKey.ForScoutTarget(c.Target);

        // --------------------------------------------------------------------- continuity ----

        // Turn one durable intent back into a concrete ScoutCandidate against THIS snapshot, or
        // null if the objective is no longer coherent (focus visited / boxed in, tracked contact
        // gone). MissionContinuityLayer.ResolveActive already purged the plainly-dead ones; this is
        // the same check re-run against the identical snapshot object, plus a fresh cost sizing.
        private static ScoutCandidate? TryMaterializeIntent(WorldSnapshot snap, DesireBreakdown bd, MissionIntent intent)
        {
            ScoutIntent si = intent?.Scout;
            if (si == null)
                return null;

            // Re-materialise the intent from the ONE Recon-objective evaluator — never a second
            // proposal-builder, never a re-scan for new opportunities.
            ReconObjective o = si.Kind == ScoutTargetKind.Explore
                ? ReconObjectiveEvaluator.ExploreAt(snap, si.FocusHex)
                : ReconObjectiveEvaluator.SurveilOf(snap, ScoutObjectiveEvaluator.SurveilContact(snap, si.TrackedArmyId));
            if (o == null)
                return null;
            return ToCandidate(o, bd).AsIncumbent(intent.Funding, intent.PreferredMoverArmyId);
        }

        // --------------------------------------------------------------------------- shared ----

        // One ReconObjective -> one ScoutCandidate. BaseValue / target / risk come straight from
        // the objective; only the mission-planning-specific LocalAdmissionScore (BaseValue x the
        // relevant Recon sub-desire x a risk factor) is applied here. Explain also expands the
        // objective's already-computed raw terms so score saturation is visible in AiDebug.log.
        private static ScoutCandidate ToCandidate(ReconObjective o, DesireBreakdown bd)
        {
            bool explore = o.Kind == ReconObjectiveKind.Explore;
            float subDesire = explore ? bd.ReconExploration : bd.ReconSurveillance;
            float proximity = Curves.InvRamp(o.DistanceFromBase,
                AiConfigV2.scoutProximityRampLo, AiConfigV2.scoutProximityRampHi);
            float infoGain = explore
                ? Mathf.Clamp01(o.FreshNeighbors / Mathf.Max(0.0001f, AiConfigV2.scoutInfoGainNorm))
                : 0f;
            bool infoCapped = explore && o.FreshNeighbors >= AiConfigV2.scoutInfoGainNorm;
            string explain = explore
                ? $"Explore @{o.FocusHex.Q},{o.FocusHex.R} opens {o.FreshNeighbors} d{o.DistanceFromBase} "
                  + $"info {F(infoGain)} prox {F(proximity)} infoCap {(infoCapped ? 1 : 0)}"
                  + $"{StealthTag(o.Stealth, o.DetectionRisk)} base {F(o.BaseValue)} x explore {F(subDesire)}"
                : $"Surveil @{o.FocusHex.Q},{o.FocusHex.R} age {o.AgeTurns} sev {F(o.Severity)} "
                  + $"prox {F(proximity)}{StealthTag(o.Stealth, o.DetectionRisk)} "
                  + $"base {F(o.BaseValue)} x surv {F(subDesire)}";
            return new ScoutCandidate(o.ToTarget(), o.BaseValue,
                ComputeLocalAdmissionScore(o.BaseValue, subDesire, o.DetectionRisk), explain,
                freshNeighbors: explore ? o.FreshNeighbors : 0);
        }

        // LocalAdmissionScore = BaseValue * the relevant Recon sub-desire * an execution-risk
        // factor. The risk factor stays OUT of BaseValue (and therefore out of the radar) — it is
        // not intrinsic information value, just a tie-breaker toward the safer of two equal jobs.
        // Used to rank Recon alternatives against each other (beam here, K-cut in the allocator).
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
