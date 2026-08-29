using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.HexGrid;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  RECON MISSION PLANNER  (Strategy V2 build-order step 4, + step 7 continuity)
    // ===========================================================================================
    //  One WorldSnapshot + the Recon DesireBreakdown -> up to AiConfigV2.maxConcurrentRecon Scout
    //  MissionProposals. It NEVER re-derives the analysis behind the breakdown — it reads
    //  breakdown.ReconExploration / .ReconSurveillance as given and only decides WHICH concrete
    //  hex each Scout heads for, and how hidden its executor must be.
    //
    //  TWO CANDIDATE KINDS, ONE 0..100 SCALE
    //    Explore  — a MapKnowledge.Frontier hex. Valued on info gain + centrality.
    //    Surveil  — a stale HONEST positioned contact (Source == Honest, Knowledge == LastKnown).
    //               Valued on staleness x the severity already attached to that contact.
    //
    //  STEP-7 CONTINUITY — this is the ONE place that turns a durable MissionIntent back into a
    //  concrete proposal (Intent != Proposal). Every active intent is re-materialised from the
    //  CURRENT snapshot (fresh cost via ScoutCostModel, fresh vantage later in provisioning), so
    //  there is no second proposal-builder inside the continuity layer. Retarget hysteresis: an
    //  in-flight ("incumbent") heading only yields to a fresh candidate that beats it by
    //  AiConfigV2.commitmentRetargetMargin. Soft/Hard incumbents also hold a recon slot
    //  unconditionally; a None-tier (Explore) incumbent can lose its slot to materially better work.
    //
    //  INTRINSIC value vs SELECTION
    //    BaseValue      = Lerp(scoutBaseValueMin, scoutBaseValueMax, quality) — the ONLY thing in
    //                     MissionProposal.BaseValue. The allocator packs on this + the radar slices.
    //    SelectionScore = BaseValue * (Explore ? ReconExploration : ReconSurveillance) * riskFactor.
    //                     Used HERE only, to rank the pool and pick winners.
    // ===========================================================================================
    internal static class MissionLayer
    {
        private readonly struct ScoutCandidate
        {
            public readonly ScoutMissionTarget Target;
            public readonly float BaseValue;
            public readonly float SelectionScore;
            public readonly string Explain;

            // Step 7 — set only when this candidate was re-materialised from a durable intent.
            public readonly bool IsIncumbent;
            public readonly CommitmentTier Tier;
            public readonly int? PreferredMover;

            public ScoutCandidate(ScoutMissionTarget target, float baseValue, float selectionScore, string explain,
                bool isIncumbent = false, CommitmentTier tier = CommitmentTier.None, int? preferredMover = null)
            {
                Target = target;
                BaseValue = baseValue;
                SelectionScore = selectionScore;
                Explain = explain;
                IsIncumbent = isIncumbent;
                Tier = tier;
                PreferredMover = preferredMover;
            }

            public ScoutCandidate AsIncumbent(CommitmentTier tier, int? preferredMover) =>
                new ScoutCandidate(Target, BaseValue, SelectionScore, Explain + " [incumbent]", true, tier, preferredMover);
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

            var picked = new List<ScoutCandidate>();

            // 1. Soft/Hard incumbents hold a slot unconditionally (funding-protected — see
            //    MissionContinuityLayer). Score only orders them against each other; the stable
            //    key breaks ties so the pick never depends on registry iteration order.
            foreach (ScoutCandidate c in incumbents
                .Where(x => x.Tier != CommitmentTier.None)
                .OrderByDescending(x => x.SelectionScore)
                .ThenBy(x => CandidateKey(x)))
            {
                if (picked.Count >= AiConfigV2.maxConcurrentRecon) break;
                if (picked.Any(p => Conflicts(p, c))) continue;
                picked.Add(c);
            }

            // 2. None-tier incumbents + fresh candidates compete for the rest. The retarget margin
            //    is applied as an incumbent-only multiplier on the ranking key — mathematically the
            //    same as "a fresh candidate replaces an incumbent only if fresh > incumbent *
            //    (1 + margin)". One margin, no separate bonus (that stacked into a hard lock).
            float mult = 1f + AiConfigV2.commitmentRetargetMargin;
            IEnumerable<ScoutCandidate> contenders = incumbents
                .Where(x => x.Tier == CommitmentTier.None)
                .Concat(fresh)
                .OrderByDescending(x => x.SelectionScore * (x.IsIncumbent ? mult : 1f))
                .ThenBy(x => CandidateKey(x));
            foreach (ScoutCandidate c in contenders)
            {
                if (picked.Count >= AiConfigV2.maxConcurrentRecon) break;
                if (!c.IsIncumbent && c.SelectionScore <= 0f) continue; // fresh needs positive merit; an incumbent may ride at ~0
                if (picked.Any(p => Conflicts(p, c))) continue;
                picked.Add(c);
            }

            foreach (ScoutCandidate c in picked)
                proposals.Add(BuildProposal(snap, c));
            return proposals;
        }

        // Deterministic tie-break for candidate ranking — the SAME strategic identity the intent
        // registry and the allocator use (Surveil keyed by tracked ArmyId, so two Surveils on one
        // hex don't collapse). Keeps a slot pick from depending on LINQ's input order (which for
        // incumbents is registry iteration order, for Surveil is Threat.Contacts order).
        private static MissionIntentKey CandidateKey(ScoutCandidate c) =>
            MissionIntentKey.ForScoutTarget(c.Target);

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
            return new ScoutCandidate(target, baseValue, Selection(baseValue, reconExploration, risk), explain);
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
            return new ScoutCandidate(target, baseValue, Selection(baseValue, reconSurveillance, risk), explain);
        }

        // ------------------------------------------------------------------------------ shared ----

        private static float Proximity(int distanceFromNearestBase) =>
            Curves.InvRamp(distanceFromNearestBase, AiConfigV2.scoutProximityRampLo, AiConfigV2.scoutProximityRampHi);

        // [0..1] risk from CURRENTLY known non-neutral forces that could actually roll a stealth
        // challenge on `hex`. The implementation is the shared ScoutRiskModel (step 6b) so a
        // Surveil vantage is scored identically — do not re-inline it here.
        private static float CurrentDetectorRisk(WorldSnapshot snap, HexCoord hex) =>
            ScoutRiskModel.DetectorRisk(snap, hex);

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
                PreferredMoverArmyId = c.PreferredMover,
            };
            proposal.Axes.Value[DesireAxis.Recon] = 1.0f;
            return proposal;
        }

        private static string F(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    }
}
