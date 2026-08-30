using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  AGGRESSION MISSION PLANNER  (Strategy V2 build-order step 9 — the Raid candidate beam)
    // ===========================================================================================
    //  The Aggression-lane counterpart of MissionLayer (ReconMissionPlanner). One post-Phase-A
    //  operational snapshot + the FROZEN AggressionObjective[] + the Aggression DesireBreakdown ->
    //  a CANDIDATE BEAM of up to AiConfigV2.raidCandidateBeamWidth Raid MissionProposals.
    //
    //  It does NOT (spec §22): scan the world, call an enemy scan, play cards, pick a concrete
    //  army, or mutate game state. It DOES: re-materialise active Raid intents, build fresh Raid
    //  candidates from the frozen objectives, size MissionRequirements (RaidCostModel), set
    //  AxisContribution (Aggression = 1.0, spec §24), set LocalAdmissionScore, and emit proposals.
    //
    //  SCORING SPLIT (spec §10): BaseValue is the objective's intrinsic merit (cross-lane ordering
    //  + radar slices). LocalAdmissionScore = BaseValue * AggRaidOpportunity sub-driver * a
    //  feasibility factor — orders Raid alternatives WITHIN the Aggression lane only, never
    //  cross-lane, and never re-multiplies the whole Aggression radar weight.
    // ===========================================================================================
    internal static class AggressionMissionLayer
    {
        private readonly struct RaidCandidate
        {
            public readonly RaidMissionTarget Target;
            public readonly float BaseValue;
            public readonly float LocalAdmissionScore;
            public readonly string Explain;
            public readonly bool IsIncumbent;
            public readonly CommitmentTier Tier;
            public readonly int? PreferredMover;

            public RaidCandidate(RaidMissionTarget target, float baseValue, float localAdmissionScore, string explain,
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

            public RaidCandidate AsIncumbent(CommitmentTier tier, int? preferredMover)
            {
                // A committed + ready Hard raid gets a sunk-cost bump so a small Radar wobble does
                // not drop it for routine recon (spec §27 / §61-bonus). Soft/None ride as-is.
                float las = tier == CommitmentTier.Hard
                    ? LocalAdmissionScore + AiConfigV2.raidHardCommitmentBonus
                    : LocalAdmissionScore;
                return new RaidCandidate(Target, BaseValue, las, Explain + " [incumbent]", true, tier, preferredMover);
            }
        }

        public static List<MissionProposal> Propose(WorldSnapshot snap, DesireBreakdown breakdown,
            IReadOnlyList<MissionIntent> activeIntents,
            IReadOnlyList<AggressionObjective> frozenObjectives)
        {
            var proposals = new List<MissionProposal>();
            if (snap?.Self == null || breakdown == null)
                return proposals;

            IReadOnlyList<AggressionObjective> objectives = frozenObjectives
                ?? AggressionObjectiveEvaluator.Enumerate(snap, breakdown.OpportunityReport);

            var fresh = new List<RaidCandidate>();
            foreach (AggressionObjective o in objectives)
                fresh.Add(ToCandidate(o, breakdown));

            var incumbents = new List<RaidCandidate>();
            if (activeIntents != null)
                foreach (MissionIntent intent in activeIntents)
                {
                    if (intent.Kind != MissionKind.Raid || intent.Raid == null)
                        continue;
                    AggressionObjective o = AggressionObjectiveEvaluator.ForTrackedArmy(
                        snap, breakdown.OpportunityReport, intent.Raid.TargetArmyId);
                    if (o == null)
                    {
                        // No fresh opportunity read this turn (target in fog) — a started Hard raid
                        // still re-materialises from the last-known intent facts so its commitment
                        // does not evaporate; MissionContinuityLayer's stall/age caps reap a raid
                        // that never re-acquires.
                        if (!intent.Raid.OperationStarted)
                        {
                            AiDebugLog.Write($"[AI][V2]   raid — intent {intent.IntentKey} not materialisable this turn");
                            continue;
                        }
                        var stale = new RaidMissionTarget
                        {
                            TargetArmyId = intent.Raid.TargetArmyId,
                            LastKnownHex = intent.Raid.LastKnownHex,
                            TargetIsNeutral = intent.Raid.TargetIsNeutral,
                            AssemblableWinChance = AiConfigV2.raidMinViableWinChance,
                            EstimatedEta = 1,
                        };
                        float sv = AiConfigV2.raidBaseValueMin;
                        incumbents.Add(new RaidCandidate(stale, sv,
                            sv * UnityEngine.Mathf.Max(0.01f, breakdown.AggRaidOpportunity) + AiConfigV2.raidHardCommitmentBonus,
                            $"Raid #{intent.Raid.TargetArmyId} (tracking, target in fog) [incumbent]",
                            true, intent.Funding, intent.PreferredMoverArmyId));
                        continue;
                    }
                    incumbents.Add(ToCandidate(o, breakdown).AsIncumbent(intent.Funding, intent.PreferredMoverArmyId));
                }

            var incumbentKeys = new HashSet<int>(incumbents.Select(c => c.Target.TargetArmyId));
            var picked = new List<RaidCandidate>();

            // 1. Every valid Soft/Hard incumbent materialises unconditionally, ON TOP of the beam
            //    (a funding-protected raid cannot vanish because the fresh beam is full).
            foreach (RaidCandidate c in incumbents
                .Where(x => x.Tier != CommitmentTier.None)
                .OrderByDescending(x => x.LocalAdmissionScore)
                .ThenBy(x => x.Target.TargetArmyId))
                picked.Add(c);

            // 2. The ordinary beam: None-tier incumbents + fresh (minus fresh duplicates of any
            //    incumbent), ranked by the shared admission rank, truncated to the beam width.
            IEnumerable<RaidCandidate> ordinary = incumbents
                .Where(x => x.Tier == CommitmentTier.None)
                .Concat(fresh.Where(f => !incumbentKeys.Contains(f.Target.TargetArmyId)))
                .OrderByDescending(x => MissionAdmissionPolicy.AdmissionRank(x.LocalAdmissionScore, x.IsIncumbent, x.Tier))
                .ThenBy(x => x.Target.TargetArmyId);
            int count = 0;
            foreach (RaidCandidate c in ordinary)
            {
                if (count >= AiConfigV2.raidCandidateBeamWidth) break;
                if (!c.IsIncumbent && c.LocalAdmissionScore <= 0f) continue;
                picked.Add(c);
                count++;
            }

            foreach (RaidCandidate c in picked)
                proposals.Add(BuildProposal(snap, c));
            return proposals;
        }

        private static RaidCandidate ToCandidate(AggressionObjective o, DesireBreakdown bd)
        {
            float feasibility = UnityEngine.Mathf.Lerp(
                AiConfigV2.raidLocalFeasibilityFloor, 1f,
                UnityEngine.Mathf.Clamp01(UnityEngine.Mathf.Max(o.ReadyWinChance, o.AssemblableWinChance)));
            float las = o.BaseValue * UnityEngine.Mathf.Max(0.01f, bd.AggRaidOpportunity) * feasibility;
            string explain = $"Raid #{o.TargetArmyId} @{o.LastKnownHex.Q},{o.LastKnownHex.R} "
                + $"val {F(o.BaseValue)} x aggRaid {F(bd.AggRaidOpportunity)} feas {F(feasibility)} "
                + $"(readyWin {F(o.ReadyWinChance)} asmWin {F(o.AssemblableWinChance)} def {o.DefenderCount} "
                + $"eta {o.EstimatedEta} gate {(o.GatePassed ? 1 : 0)}"
                + $"{(o.NeedsCombatPower ? " NEEDS-POWER" : "")}{(o.NeedsHero ? " NEEDS-HERO" : "")})";
            return new RaidCandidate(o.ToTarget(), o.BaseValue, las, explain);
        }

        private static MissionProposal BuildProposal(WorldSnapshot snap, RaidCandidate c)
        {
            MissionRequirements req = RaidCostModel.Build(snap, c.Target);

            var proposal = new MissionProposal
            {
                Kind = MissionKind.Raid,
                Target = c.Target,
                BaseValue = c.BaseValue,
                Requirements = req,
                LocalAdmissionScore = c.LocalAdmissionScore,
                FromDurableIntent = c.IsIncumbent,
                DurableFundingTier = c.Tier,
                Explain = c.Explain,
                PreferredMoverArmyId = c.PreferredMover,
            };
            proposal.Axes.Value[DesireAxis.Aggression] = 1.0f;
            return proposal;
        }

        private static string F(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    }
}
