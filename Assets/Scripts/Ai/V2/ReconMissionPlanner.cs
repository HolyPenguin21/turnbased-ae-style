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

            // Step 7 — set only when this candidate was re-materialised from a durable intent.
            public readonly bool IsIncumbent;
            public readonly CommitmentTier Tier;
            public readonly int? PreferredMover;

            public ScoutCandidate(ScoutMissionTarget target, float baseValue, float localAdmissionScore, string explain,
                bool isIncumbent = false, CommitmentTier tier = CommitmentTier.None, int? preferredMover = null)
            {
                Target = target;
                BaseValue = baseValue;
                LocalAdmissionScore = localAdmissionScore;
                Explain = explain;
                IsIncumbent = isIncumbent;
                Tier = tier;
                PreferredMover = preferredMover;
            }

            public ScoutCandidate AsIncumbent(CommitmentTier tier, int? preferredMover) =>
                new ScoutCandidate(Target, BaseValue, LocalAdmissionScore, Explain + " [incumbent]", true, tier, preferredMover);
        }

        public static List<MissionProposal> Propose(WorldSnapshot snap, DesireBreakdown breakdown,
            IReadOnlyList<MissionIntent> activeIntents)
        {
            var proposals = new List<MissionProposal>();
            if (snap?.Self == null || snap.MapKnowledge == null || breakdown == null)
                return proposals;

            // Fresh candidates from the current world.
            var fresh = new List<ScoutCandidate>();
            fresh.AddRange(ExploreCandidates(snap, breakdown.ReconExploration));
            fresh.AddRange(SurveilCandidates(snap, breakdown.ReconSurveillance));

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
                proposals.Add(BuildProposal(snap, c));
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

            if (si.Kind == ScoutTargetKind.Explore)
            {
                int fresh = ScoutObjectiveEvaluator.ExploreStillOpen(snap, si.FocusHex);
                if (fresh <= 0)
                    return null;
                int distBase = snap.Self.BaseHexes != null && snap.Self.BaseHexes.Count > 0
                    ? snap.Self.BaseHexes.Min(b => HexGridMath.Distance(b, si.FocusHex))
                    : 0;
                bool exposed = EnemyExposedAt(snap, si.FocusHex);
                bool stealthRisk = exposed && DetectorsAt(snap, si.FocusHex) > 0;
                return MakeExploreCandidate(snap, si.FocusHex, fresh, distBase, exposed, stealthRisk, bd.ReconExploration)
                    .AsIncumbent(intent.Funding, intent.PreferredMoverArmyId);
            }

            EnemyContactSnapshot contact = ScoutObjectiveEvaluator.SurveilContact(snap, si.TrackedArmyId);
            if (contact == null)
                return null;
            return MakeSurveilCandidate(snap, contact, bd.ReconSurveillance)
                .AsIncumbent(intent.Funding, intent.PreferredMoverArmyId);
        }

        // Inline mirrors of the frontier scan's enemy-exposure annotation — a materialised Explore
        // intent's focus hex may have dropped out of MapKnowledge.Frontier (the wave band moved),
        // so the precomputed flag is not available. Same constants, same CanDetectStealthAt call.
        private static bool EnemyExposedAt(WorldSnapshot snap, HexCoord hex)
        {
            IReadOnlyList<AiMapMemory.KnownEnemySighting> sightings = snap.Known?.EnemySightings;
            if (sightings == null) return false;
            int r = AiConfigV2.frontierEnemyExposureRadius;
            foreach (AiMapMemory.KnownEnemySighting e in sightings)
                if (HexGridMath.Distance(e.Hex, hex) <= r) return true;
            return false;
        }

        private static int DetectorsAt(WorldSnapshot snap, HexCoord hex)
        {
            IReadOnlyList<AiMapMemory.KnownEnemySighting> sightings = snap.Known?.EnemySightings;
            if (sightings == null) return 0;
            int r = AiConfigV2.frontierEnemyExposureRadius;
            int n = 0;
            foreach (AiMapMemory.KnownEnemySighting e in sightings)
                if (HexGridMath.Distance(e.Hex, hex) <= r && e.CanDetectStealthAt(hex)) n++;
            return n;
        }

        // --------------------------------------------------------------------------- Explore ----
        private static IEnumerable<ScoutCandidate> ExploreCandidates(WorldSnapshot snap, float reconExploration)
        {
            IReadOnlyList<FrontierHexSnapshot> frontier = snap.MapKnowledge.Frontier;
            if (frontier == null)
                yield break;

            foreach (FrontierHexSnapshot f in frontier)
                yield return MakeExploreCandidate(snap, f.Hex, f.FreshNeighbors, f.DistanceFromNearestBase,
                    f.EnemyExposure, f.StealthDetectionRisk, reconExploration);
        }

        private static ScoutCandidate MakeExploreCandidate(WorldSnapshot snap, HexCoord hex, int freshNeighbors,
            int distFromBase, bool enemyExposure, bool stealthDetectionRisk, float reconExploration)
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
                    CurrentDetectorRisk(snap, hex))
                : 0f;

            var target = new ScoutMissionTarget
            {
                FocusHex = hex,
                Kind = ScoutTargetKind.Explore,
                Contact = null,
                Stealth = req,
                DetectionRisk = risk,
            };
            string explain = $"Explore @{hex.Q},{hex.R} opens {freshNeighbors} "
                + $"(info {F(infoGain)} prox {F(proximity)} d{distFromBase}{StealthTag(req, risk)}) "
                + $"base {F(baseValue)} x explore {F(reconExploration)}";
            return new ScoutCandidate(target, baseValue,
                ComputeLocalAdmissionScore(baseValue, reconExploration, risk), explain);
        }

        // --------------------------------------------------------------------------- Surveil ----
        private static IEnumerable<ScoutCandidate> SurveilCandidates(WorldSnapshot snap, float reconSurveillance)
        {
            IReadOnlyList<EnemyContactSnapshot> contacts = snap.Threat?.Contacts;
            if (contacts == null)
                yield break;

            foreach (EnemyContactSnapshot c in contacts)
            {
                if (c.Source != ContactSource.Honest || c.Knowledge != ContactKnowledge.LastKnown || !c.Position.HasValue)
                    continue;
                yield return MakeSurveilCandidate(snap, c, reconSurveillance);
            }
        }

        private static ScoutCandidate MakeSurveilCandidate(WorldSnapshot snap, EnemyContactSnapshot c, float reconSurveillance)
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
            float proximity = bases != null && bases.Count > 0
                ? Proximity(bases.Min(b => HexGridMath.Distance(b, pos)))
                : 0f;

            float wSum = AiConfigV2.scoutStrategicProximityWeight + AiConfigV2.scoutThreatWeight;
            float quality = Mathf.Clamp01(
                (AiConfigV2.scoutStrategicProximityWeight * proximity
                 + AiConfigV2.scoutThreatWeight * threatRelevance) / Mathf.Max(0.0001f, wSum));
            float baseValue = Mathf.Lerp(AiConfigV2.scoutBaseValueMin, AiConfigV2.scoutBaseValueMax, quality);

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
            return new ScoutCandidate(target, baseValue,
                ComputeLocalAdmissionScore(baseValue, reconSurveillance, risk), explain);
        }

        // ------------------------------------------------------------------------------ shared ----

        private static float Proximity(int distanceFromNearestBase) =>
            Curves.InvRamp(distanceFromNearestBase, AiConfigV2.scoutProximityRampLo, AiConfigV2.scoutProximityRampHi);

        // [0..1] risk from CURRENTLY known non-neutral forces that could actually roll a stealth
        // challenge on `hex`. The implementation is the shared ScoutRiskModel (step 6b) so a
        // Surveil vantage is scored identically — do not re-inline it here.
        private static float CurrentDetectorRisk(WorldSnapshot snap, HexCoord hex) =>
            ScoutRiskModel.DetectorRisk(snap, hex);

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
