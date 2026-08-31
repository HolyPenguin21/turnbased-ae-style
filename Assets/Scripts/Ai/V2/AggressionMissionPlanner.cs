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
    //  Strategic target discovery/value remains frozen. Own-force executability does NOT: Phase A
    //  may have materially changed a raid body, so this layer refreshes only the ready-force combat
    //  projection from the post-Phase-A Self snapshot. That keeps target identity/value stable while
    //  preventing stale readyWin values from competing with Provisioning's live estimator.
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
                return new RaidCandidate(Target, BaseValue, LocalAdmissionScore,
                    Explain + $" [incumbent {tier}; funding protected separately]",
                    true, tier, preferredMover);
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
                fresh.Add(ToCandidate(snap, o, breakdown));

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
                        if (!intent.Raid.OperationStarted)
                        {
                            AiDebugLog.Write($"[AI][V2]   raid mission — DEFER {intent.IntentKey}: target has no fresh opportunity read and operation never started");
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
                        float staleScore = sv * UnityEngine.Mathf.Max(0.01f, breakdown.AggRaidOpportunity);
                        incumbents.Add(new RaidCandidate(stale, sv, staleScore,
                            $"Raid #{intent.Raid.TargetArmyId} (tracking in fog; Hard funding protection is allocator-owned)",
                            true, intent.Funding, intent.PreferredMoverArmyId));
                        AiDebugLog.Write($"[AI][V2]   raid mission — CONTINUE {intent.IntentKey}: target in fog, using last-known hex "
                            + $"({intent.Raid.LastKnownHex.Q},{intent.Raid.LastKnownHex.R}); base {F(sv)}, local {F(staleScore)}, tier {intent.Funding}");
                        continue;
                    }
                    incumbents.Add(ToCandidate(snap, o, breakdown).AsIncumbent(intent.Funding, intent.PreferredMoverArmyId));
                }

            var incumbentKeys = new HashSet<int>(incumbents.Select(c => c.Target.TargetArmyId));
            var picked = new List<RaidCandidate>();

            foreach (RaidCandidate c in incumbents
                .Where(x => x.Tier != CommitmentTier.None)
                .OrderByDescending(x => x.LocalAdmissionScore)
                .ThenBy(x => x.Target.TargetArmyId))
                picked.Add(c);

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
            {
                MissionProposal p = BuildProposal(snap, c);
                if (!c.IsIncumbent
                    && RaidAdmissionRegistry.TryGet(p, out HashSet<int> eligible)
                    && eligible.Count == 0)
                {
                    AiDebugLog.Write($"[AI][V2]   mission suppress — {StableMissionKey.For(p)} "
                        + "reason=no_ready_raid_actor_after_phaseA");
                    continue;
                }

                proposals.Add(p);
                AiDebugLog.Write($"[AI][V2]   raid mission — PROPOSE {StableMissionKey.For(p)}: {p.Explain}; "
                    + $"tier {p.DurableFundingTier}, ap {F(p.Requirements?.ApMinimum ?? 0f)}..{F(p.Requirements?.ApMaximum ?? 0f)}, "
                    + $"readyActors=[{RaidAdmissionRegistry.EligibleIds(p)}]");
            }
            if (proposals.Count == 0)
                AiDebugLog.Write($"[AI][V2]   raid mission — NONE: {objectives.Count} frozen objective(s), no executable candidate survived beam/materialisation");
            return proposals;
        }

        private static RaidCandidate ToCandidate(WorldSnapshot snap, AggressionObjective o, DesireBreakdown bd)
        {
            RaidMissionTarget target = o.ToTarget();
            IReadOnlyList<WorthIt.DefenderProfile> defenders = KnownDefenders(snap, o.TargetArmyId);
            RaidAssemblyPlan live = RaidAssemblyPlanner.Plan(snap, target, defenders, null);

            float readyWin = live.Feasible ? UnityEngine.Mathf.Clamp01(live.ProjectedWinChance) : 0f;
            if (live.Feasible)
            {
                target.ReadyWinChance = readyWin;
                target.CanCoverAllDefenders = live.CoversAllDefenders;
            }

            // Expected-value weighting: probability is no longer merely a weak gate. Squaring the
            // live ready probability makes a 0.75 target materially preferable to a 0.35 target
            // when their intrinsic values are nearly equal, while still preserving BaseValue as
            // the cross-lane strategic merit.
            float p = live.Feasible
                ? readyWin
                : UnityEngine.Mathf.Clamp01(o.AssemblableWinChance) * 0.35f;
            float feasibility = UnityEngine.Mathf.Lerp(
                AiConfigV2.raidLocalFeasibilityFloor, 1f, p * p);

            MissionRequirements req = RaidCostModel.Build(snap, target);
            float ap = UnityEngine.Mathf.Max(0f, req?.ApDesired ?? 0f);
            float apEfficiency = 1f / (1f + 0.12f * UnityEngine.Mathf.Max(0f, ap - 1f));
            float las = o.BaseValue * UnityEngine.Mathf.Max(0.01f, bd.AggRaidOpportunity)
                * feasibility * apEfficiency;

            string explain = $"Raid #{o.TargetArmyId} @{o.LastKnownHex.Q},{o.LastKnownHex.R} "
                + $"val {F(o.BaseValue)} x aggRaid {F(bd.AggRaidOpportunity)} liveFeas {F(feasibility)} "
                + $"apEff {F(apEfficiency)} (readyWin {F(readyWin)} frozenReady {F(o.ReadyWinChance)} "
                + $"asmWin {F(o.AssemblableWinChance)} def {o.DefenderCount} eta {o.EstimatedEta} "
                + $"gate {(o.GatePassed ? 1 : 0)}{(o.NeedsCombatPower ? " NEEDS-POWER" : "")}" 
                + $"{(o.NeedsHero ? " NEEDS-HERO" : "")})";
            return new RaidCandidate(target, o.BaseValue, las, explain);
        }

        private static IReadOnlyList<WorthIt.DefenderProfile> KnownDefenders(WorldSnapshot snap, int armyId)
        {
            if (snap?.Known == null || armyId == 0)
                return System.Array.Empty<WorthIt.DefenderProfile>();
            IEnumerable<AiMapMemory.KnownEnemySighting> all =
                (snap.Known.EnemySightings ?? Enumerable.Empty<AiMapMemory.KnownEnemySighting>())
                .Concat(snap.Known.NeutralSightings ?? Enumerable.Empty<AiMapMemory.KnownEnemySighting>());
            foreach (AiMapMemory.KnownEnemySighting s in all)
                if (s.ArmyId == armyId)
                    return s.Defenders ?? System.Array.Empty<WorthIt.DefenderProfile>();
            return System.Array.Empty<WorthIt.DefenderProfile>();
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
            RaidAdmissionRegistry.Record(proposal, snap);
            return proposal;
        }

        private static string F(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    }
}
