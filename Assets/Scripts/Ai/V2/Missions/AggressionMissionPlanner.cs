using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.HexGrid;
using Game.Combat;
using Game.Aviation;

namespace Game.Ai.V2
{
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
            // One actor identity supplies the combat projection, price and travel distance.
            // Provisioning remains the authority on whether that actor can execute this turn.
            public readonly int? CostedMover;

            public RaidCandidate(RaidMissionTarget target, float baseValue, float localAdmissionScore,
                string explain, bool isIncumbent = false, CommitmentTier tier = CommitmentTier.None,
                int? preferredMover = null, int? costedMover = null)
            {
                Target = target;
                BaseValue = baseValue;
                LocalAdmissionScore = localAdmissionScore;
                Explain = explain;
                IsIncumbent = isIncumbent;
                Tier = tier;
                PreferredMover = preferredMover;
                CostedMover = costedMover;
            }

            public RaidCandidate AsIncumbent(CommitmentTier tier, int? preferredMover)
            {
                // ToCandidate already pinned the assembly projection and price before folding.
                // Never substitute a different mover AFTER scoring: that previously preserved the
                // cheap army's score but funded the durable primary's more expensive route.
                return new RaidCandidate(Target, BaseValue, LocalAdmissionScore,
                    Explain + $" [incumbent {tier}; funding protected separately]",
                    true, tier, preferredMover, CostedMover);
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

            // Fresh Raid scoring must use the same actor-ownership view Provisioning enforces.
            // Otherwise a busy Economy/Recon/Raid actor can make a fresh Raid look executable and cheap,
            // only to be rejected later by the batch assignment. Incumbents are allowed to keep their
            // own pinned mover below; every other durable actor remains excluded from host/donor selection.
            // Only Active intents contribute a real claim — a retired/cancelled intent's mover is free.
            ISet<int> committed = ActorCommitments.FromIntents(
                activeIntents?.Where(i => i != null && i.Status == IntentStatus.Active),
                snap, null).ClaimedArmyIdSet;
            var fresh = new List<RaidCandidate>();
            foreach (AggressionObjective o in objectives)
                fresh.Add(ToCandidate(snap, o, breakdown, unavailableArmyIds: committed));

            var incumbents = new List<RaidCandidate>();
            if (activeIntents != null)
                foreach (MissionIntent intent in activeIntents)
                {
                    if (intent.Kind != MissionKind.Raid || intent.Raid == null)
                        continue;

                    if (intent.Raid.Phase == RaidMissionPhase.Return)
                    {
                        RaidCandidate? ret = ReturnCandidate(intent, RaidMissionPhase.Return,
                            intent.Raid.PrimaryArmyId, intent.Raid.ReturnHex);
                        if (ret.HasValue) incumbents.Add(ret.Value);
                        continue;
                    }
                    if (intent.Raid.Phase == RaidMissionPhase.SupportReturn)
                    {
                        RaidCandidate? sret = ReturnCandidate(intent, RaidMissionPhase.SupportReturn,
                            intent.Raid.SupportArmyId, intent.Raid.SupportReturnHex);
                        if (sret.HasValue) incumbents.Add(sret.Value);
                        continue;
                    }
                    if (intent.Raid.Phase == RaidMissionPhase.AirSupport
                        || intent.Raid.Phase == RaidMissionPhase.Reinforcement)
                    {
                        RaidCandidate? air = AirSupportCandidate(snap, intent, committed);
                        if (air.HasValue)
                        {
                            incumbents.Add(air.Value);
                            continue;
                        }
                    }
                    if (intent.Raid.Phase == RaidMissionPhase.Reinforcement)
                    {
                        RaidCandidate? sup = ReinforcementCandidate(snap, intent);
                        if (sup.HasValue) incumbents.Add(sup.Value);
                        else
                            AiDebugLog.WriteDeduped(intent.IntentKey.ToString(),
                                $"[AI][V2]   raid mission — HOLD {intent.IntentKey}: primary "
                                + $"#{intent.Raid.PrimaryArmyId} waits in place; no support army assigned yet "
                                + "(Aggression demand owns the request)");
                        continue;
                    }

                    AggressionObjective o = AggressionObjectiveEvaluator.ForTrackedTarget(
                        snap, breakdown.OpportunityReport, intent.Raid.Target);
                    if (o == null)
                    {
                        if (!intent.Raid.OperationStarted)
                        {
                            AiDebugLog.Write($"[AI][V2]   raid mission — DEFER {intent.IntentKey}: target has no fresh opportunity read and operation never started");
                            continue;
                        }
                        var stale = new RaidMissionTarget
                        {
                            Target = intent.Raid.Target,
                            LastKnownHex = intent.Raid.LastKnownHex,
                            TargetIsNeutral = intent.Raid.TargetIsNeutral,
                            AssemblableWinChance = AiConfigV2.raidMinViableWinChance,
                            EstimatedEta = 1,
                        };
                        RaidCostEstimate staleEstimate = RaidCostModel.Estimate(snap, stale,
                            intent.PreferredMoverArmyId);
                        MissionRequirements staleCost = staleEstimate.Requirements;
                        // Stationary neutral/event target: loss of a fresh opportunity read
                        // does not make its last-known position less valuable.
                        int homeDistance = TaskScoreEvaluator.NearestOwnedHomeDistance(
                            snap, intent.Raid.LastKnownHex);
                        var staleTask = new TaskScore(
                            ownTerritoryProximity: TaskScoreEvaluator.OwnTerritoryProximity(homeDistance),
                            cardPrice: staleCost.ApDesired * AiConfigV2.taskScoreReactivationApWeight,
                            delivery: TaskScoreEvaluator.DeliveryFromEta(staleEstimate.RecurringActivationAp,
                                staleCost.EtaTurns, AiConfigV2.taskScoreReactivationApWeight));
                        float staleValue = staleTask.Value;
                        TaskScoreDiagnostics.Log("Raid", intent.Raid.LastKnownHex, staleTask,
                            $"continuation=tracking_in_fog confidence=unknown stale=0 homeDistance={homeDistance} actor="
                            + (intent.PreferredMoverArmyId.HasValue
                                ? intent.PreferredMoverArmyId.Value.ToString() : "none")
                            + $" recurringAp={staleEstimate.RecurringActivationAp:0.###}");
                        incumbents.Add(new RaidCandidate(stale, staleValue, staleValue,
                            $"Raid {intent.Raid.Target.DiagnosticLabel} (tracking in fog; intrinsic={F(staleValue)}; Hard funding protection is allocator-owned)",
                            true, intent.Funding, intent.PreferredMoverArmyId, intent.PreferredMoverArmyId));
                        AiDebugLog.Write($"[AI][V2]   raid mission — CONTINUE {intent.IntentKey}: target in fog, using last-known hex "
                            + $"({intent.Raid.LastKnownHex.Q},{intent.Raid.LastKnownHex.R}); intrinsic {F(staleValue)}, tier {intent.Funding}");
                        continue;
                    }
                    // An incumbent is an already-owned operation, not a second opportunity to pick
                    // whichever fresh army happens to be cheaper. Pin the existing primary BEFORE
                    // assembly and Fold, using the existing continuation gate only if started.
                    incumbents.Add(ToCandidate(snap, o, breakdown,
                            intent.PreferredMoverArmyId, intent.Raid.OperationStarted,
                            unavailableArmyIds: committed)
                        .AsIncumbent(intent.Funding, intent.PreferredMoverArmyId));
                }

            var incumbentKeys = new HashSet<RaidTargetRef>(incumbents
                .Where(c => c.Target.Target.HasValue).Select(c => c.Target.Target));
            var picked = new List<RaidCandidate>();

            foreach (RaidCandidate c in incumbents
                .Where(x => x.Tier != CommitmentTier.None)
                .OrderByDescending(x => x.LocalAdmissionScore)
                .ThenBy(x => x.Target.Target.DiagnosticLabel))
                picked.Add(c);

            IEnumerable<RaidCandidate> ordinary = incumbents
                .Where(x => x.Tier == CommitmentTier.None)
                .Concat(fresh.Where(f => !incumbentKeys.Contains(f.Target.Target)))
                .OrderByDescending(x => MissionAdmissionPolicy.AdmissionRank(
                    x.LocalAdmissionScore, x.IsIncumbent, x.Tier))
                .ThenBy(x => x.Target.Target.DiagnosticLabel);
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
                MissionProposal p = BuildProposal(snap, c, committed);
                if (!c.IsIncumbent
                    && GroundCombatAdmissionRegistry.TryGet(p, out HashSet<int> eligible)
                    && eligible.Count == 0)
                {
                    string suppressKey = StableMissionKey.For(p).ToString();
                    AiDebugLog.WriteDeduped(suppressKey,
                        $"[AI][V2]   mission suppress — {suppressKey} reason=no_ready_raid_actor_after_phaseA");
                    continue;
                }

                proposals.Add(p);
                string missionKey = StableMissionKey.For(p).ToString();
                AiDebugLog.WriteDeduped(missionKey,
                    $"[AI][V2]   raid mission — PROPOSE {missionKey}: {p.Explain}; "
                    + $"tier {p.DurableFundingTier}, ap {F(p.Requirements?.ApMinimum ?? 0f)}..{F(p.Requirements?.ApMaximum ?? 0f)}, "
                    + $"readyActors=[{GroundCombatAdmissionRegistry.EligibleIds(p)}]");
            }
            if (proposals.Count == 0)
                AiDebugLog.WriteDeduped("none",
                    $"[AI][V2]   raid mission — NONE: {objectives.Count} frozen objective(s), no executable candidate survived beam/materialisation");
            return proposals;
        }

        // Return/support-return are lifecycle/continuity legs, not fresh strategic target scoring.
        // Their execution priority is owned by the durable Hard commitment; intrinsic TaskScore
        // stays neutral so lifecycle work cannot out-rank unrelated lanes through a legacy scale.
        private static RaidCandidate? ReturnCandidate(MissionIntent intent, RaidMissionPhase phase,
            int? moverArmyId, HexCoord? homeHex)
        {
            RaidIntent ri = intent.Raid;
            if (!moverArmyId.HasValue || !homeHex.HasValue)
                return null;
            var target = new RaidMissionTarget
            {
                Phase = phase,
                PrimaryArmyId = ri.PrimaryArmyId,
                SupportArmyId = ri.SupportArmyId,
                DestinationHex = homeHex.Value,
                Target = ri.Target,
                LastKnownHex = ri.LastKnownHex,
                TargetIsNeutral = ri.TargetIsNeutral,
                EstimatedEta = 1,
                AssemblableWinChance = 1f,
                CanCoverAllDefenders = true,
            };
            float value = default(TaskScore).Value;
            string label = phase == RaidMissionPhase.SupportReturn ? "SUPPORT-RETURN" : "RETURN";
            string role = phase == RaidMissionPhase.SupportReturn ? "support" : "primary";
            AiDebugLog.Write($"[AI][V2]   raid mission — {label} {intent.IntentKey}: {role} "
                + $"#{moverArmyId.Value} -> ({homeHex.Value.Q},{homeHex.Value.R})");
            return new RaidCandidate(target, value, value,
                $"Raid {ri.Target.DiagnosticLabel} {phase}: {role} #{moverArmyId.Value} to base "
                + $"({homeHex.Value.Q},{homeHex.Value.R}); intrinsic={F(value)}; Hard funding protection is allocator-owned",
                true, intent.Funding, moverArmyId, moverArmyId);
        }

        // Reinforcement is a durable continuation leg. Target discovery/value is not recomputed here;
        // its Hard commitment, not a synthetic strategic value, owns continuation priority.
        private static RaidCandidate? ReinforcementCandidate(WorldSnapshot snap, MissionIntent intent)
        {
            RaidIntent ri = intent.Raid;
            if (!ri.PrimaryArmyId.HasValue)
                return null;
            int primaryId = ri.PrimaryArmyId.Value;
            ArmySnapshot primary = snap.Self?.Armies?
                .FirstOrDefault(a => a != null && a.ArmyId == primaryId);
            if (primary == null)
                return null;

            if (!ri.SupportArmyId.HasValue)
            {
                IReadOnlyList<WorthIt.DefenderProfile> defenders = AiV2Util.KnownDefenders(snap, ri.Target);
                List<int> candidates = GroundCombatAssemblyPlanner.ReinforcementSupportCandidates(
                    snap, primaryId, defenders, null);
                if (candidates.Count == 0)
                    return null;

                var unpinned = new RaidMissionTarget
                {
                    Phase = RaidMissionPhase.Reinforcement,
                    PrimaryArmyId = primaryId,
                    SupportArmyId = null,
                    DestinationHex = primary.Hex,
                    Target = ri.Target,
                    LastKnownHex = ri.LastKnownHex,
                    TargetIsNeutral = ri.TargetIsNeutral,
                    EstimatedEta = 1,
                    AssemblableWinChance = 1f,
                    CanCoverAllDefenders = true,
                };
                float unpinnedValue = default(TaskScore).Value;
                AiDebugLog.Write($"[AI][V2]   raid mission — REINFORCE-SELECT {intent.IntentKey}: "
                    + $"{candidates.Count} existing free candidate(s) for primary #{primaryId} at ({primary.Hex.Q},{primary.Hex.R})");
                return new RaidCandidate(unpinned, unpinnedValue, unpinnedValue,
                    $"Raid {ri.Target.DiagnosticLabel} Reinforcement: select an existing free support for primary #{primaryId} at ({primary.Hex.Q},{primary.Hex.R}); intrinsic={F(unpinnedValue)}; Hard funding protection is allocator-owned",
                    true, intent.Funding, null, null);
            }

            var target = new RaidMissionTarget
            {
                Phase = RaidMissionPhase.Reinforcement,
                PrimaryArmyId = primaryId,
                SupportArmyId = ri.SupportArmyId,
                DestinationHex = primary.Hex,
                Target = ri.Target,
                LastKnownHex = ri.LastKnownHex,
                TargetIsNeutral = ri.TargetIsNeutral,
                EstimatedEta = 1,
                AssemblableWinChance = 1f,
                CanCoverAllDefenders = true,
            };
            float value = default(TaskScore).Value;
            AiDebugLog.Write($"[AI][V2]   raid mission — REINFORCE {intent.IntentKey}: support "
                + $"#{ri.SupportArmyId.Value} -> primary #{primaryId} at ({primary.Hex.Q},{primary.Hex.R})");
            return new RaidCandidate(target, value, value,
                $"Raid {ri.Target.DiagnosticLabel} Reinforcement: support #{ri.SupportArmyId.Value} joins primary #{primaryId} at ({primary.Hex.Q},{primary.Hex.R}); intrinsic={F(value)}; Hard funding protection is allocator-owned",
                true, intent.Funding, ri.SupportArmyId, ri.SupportArmyId);
        }

        private static RaidCandidate? AirSupportCandidate(WorldSnapshot snap, MissionIntent intent,
            ISet<int> committed)
        {
            RaidIntent ri = intent?.Raid;
            bool continuing = ri?.Phase == RaidMissionPhase.AirSupport;
            if (ri == null || ri.Target.Kind != RaidTargetKind.NeutralArmy
                || !ri.PrimaryArmyId.HasValue
                || (!continuing && ri.SupportArmyId.HasValue))
                return null;
            AiMapMemory.KnownEnemySighting? sighting = (snap?.Known?.NeutralSightings
                    ?? System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                .Where(s => s.ArmyId == ri.Target.ArmyId)
                .Select(s => (AiMapMemory.KnownEnemySighting?)s).FirstOrDefault();
            IReadOnlyList<WorthIt.DefenderProfile> defenders = AiV2Util.KnownDefenders(snap, ri.Target);
            if (!continuing && (!sighting.HasValue || sighting.Value.SeenTurn != snap.TurnNumber
                    || defenders.Count <= 1 || ri.AirSupportAttemptedTurn == snap.TurnNumber))
                return null;

            int? airId = continuing ? ri.AirSupportArmyId : null;
            if (!airId.HasValue)
            {
                airId = (snap.Self.Armies ?? System.Array.Empty<ArmySnapshot>())
                    .Where(a => a != null && a.IsAir && !a.IsAirfield && !a.IsPrison
                        && a.MemberCount > 0 && a.CurrentMovement > 0
                        && (committed == null || !committed.Contains(a.ArmyId)))
                    .OrderBy(a => HexGridMath.Distance(a.Hex, ri.LastKnownHex))
                    .ThenBy(a => a.ArmyId)
                    .Select(a => (int?)a.ArmyId).FirstOrDefault();
            }
            if (!airId.HasValue)
                return null;
            ArmySnapshot wing = snap.Self.Armies.FirstOrDefault(a => a != null
                && a.ArmyId == airId.Value && a.IsAir && !a.IsAirfield && !a.IsPrison);
            if (wing == null)
                return null;
            HexCoord landing = ri.AirSupportLandingHex
                ?? (snap.Self.BaseHexes ?? System.Array.Empty<HexCoord>())
                    .OrderBy(h => HexGridMath.Distance(h, ri.LastKnownHex))
                    .ThenBy(h => h.Q).ThenBy(h => h.R).FirstOrDefault();
            var target = new RaidMissionTarget
            {
                Phase = RaidMissionPhase.AirSupport,
                PrimaryArmyId = ri.PrimaryArmyId,
                AirSupportArmyId = airId,
                AirSupportLandingHex = landing,
                DestinationHex = ri.LastKnownHex,
                Target = ri.Target,
                LastKnownHex = ri.LastKnownHex,
                TargetIsNeutral = true,
                DefenderCount = defenders.Count,
                EstimatedEta = 1,
                AirSupportAttemptedTurn = ri.AirSupportAttemptedTurn,
                AirSupportStrikeSucceeded = ri.AirSupportStrikeSucceeded,
            };
            float value = default(TaskScore).Value;
            return new RaidCandidate(target, value, value,
                $"Raid {ri.Target.DiagnosticLabel} AirSupport: wing #{airId.Value} softens exact target nonlethally",
                true, intent.Funding, airId, airId);
        }

        private static RaidCandidate ToCandidate(WorldSnapshot snap, AggressionObjective o,
            DesireBreakdown bd, int? pinnedPrimaryArmyId = null, bool operationStarted = false,
            ISet<int> unavailableArmyIds = null)
        {
            // An incumbent may retain ITS OWN claimed primary, never any other claimant; every
            // other durable host/donor stays excluded from this Raid's assembly.
            ISet<int> excluded = unavailableArmyIds;
            if (pinnedPrimaryArmyId.HasValue && unavailableArmyIds != null)
            {
                var ownExcluded = new HashSet<int>(unavailableArmyIds);
                ownExcluded.Remove(pinnedPrimaryArmyId.Value);
                excluded = ownExcluded;
            }
            RaidMissionTarget target = o.ToTarget();
            IReadOnlyList<WorthIt.DefenderProfile> defenders = AiV2Util.KnownDefenders(snap, o.Target);

            GroundCombatAssemblyPlan live = pinnedPrimaryArmyId.HasValue
                ? GroundCombatAssemblyPlanner.Plan(snap, new GroundCombatAssemblyRequest
                {
                    Defenders = defenders,
                    PreferredPrimaryArmyId = pinnedPrimaryArmyId,
                    PinToPreferred = true,
                    ExcludedArmyIds = excluded,
                    WinChanceGate = operationStarted
                        ? RaidAdmissionPolicy.ContinuationWinChanceFloor
                        : RaidAdmissionPolicy.FreshStartWinChanceGate,
                })
                : GroundCombatAssemblyPlanner.Plan(snap, target, defenders, excluded);

            float readyWin = live.Feasible
                ? UnityEngine.Mathf.Clamp01(live.ProjectedWinChance) : 0f;
            // If the pinned actor cannot currently assemble, its cost remains pinned and its
            // combat contribution is zero; neither metric silently borrows another army.
            int? costedMover = pinnedPrimaryArmyId
                ?? (live.Feasible ? live.BaseArmyId : (int?)null);
            if (live.Feasible)
            {
                target.ReadyWinChance = readyWin;
                target.CanCoverAllDefenders = live.CoversAllDefenders;
            }

            RaidCostEstimate estimate = RaidCostModel.Estimate(snap, target, costedMover);
            MissionRequirements req = estimate.Requirements;
            float currentActivationAp = UnityEngine.Mathf.Max(0f, req?.ApDesired ?? 0f);
            float recurringActivationAp = UnityEngine.Mathf.Max(0f, estimate.RecurringActivationAp);
            float etaTurns = UnityEngine.Mathf.Max(0f, req?.EtaTurns ?? 0f);
            var score = new TaskScore(
                staleness: o.TaskScore.Staleness,
                ownTerritoryProximity: o.TaskScore.OwnTerritoryProximity,
                militaryTargetRelevance: o.TaskScore.MilitaryTargetRelevance,
                winChance: TaskScoreEvaluator.WinChance(readyWin),
                cardPrice: currentActivationAp * AiConfigV2.taskScoreReactivationApWeight,
                delivery: TaskScoreEvaluator.DeliveryFromEta(recurringActivationAp, etaTurns,
                    AiConfigV2.taskScoreReactivationApWeight),
                moverOpportunityCost: 0f);
            float las = score.Value;
            TaskScoreDiagnostics.Log("Raid", o.LastKnownHex, score,
                $"target={o.Target.DiagnosticLabel} confidence={o.Confidence:0.###} "
                + $"readyWin={readyWin:0.###} coversAll={(live.CoversAllDefenders ? 1 : 0)} "
                + $"selectedMover={(estimate.PlannedMoverArmyId.HasValue ? estimate.PlannedMoverArmyId.Value.ToString() : "none")} "
                + $"currentActivationAp={currentActivationAp:0.###} recurringActivationAp={recurringActivationAp:0.###} etaTurns={etaTurns:0.###}");

            string explain = $"Raid {o.Target.DiagnosticLabel} @{o.LastKnownHex.Q},{o.LastKnownHex.R} "
                + $"task {F(score.Value)} readyWin {F(readyWin)} frozenReady {F(o.ReadyWinChance)} "
                + $"asmWin {F(o.AssemblableWinChance)} def {o.DefenderCount} eta {req?.EtaTurns ?? o.EstimatedEta} "
                + $"aggRaidPolicy {F(bd.AggRaidOpportunity)} gate {(o.GatePassed ? 1 : 0)}"
                + $"{(o.NeedsCombatPower ? " NEEDS-POWER" : "")}{(o.NeedsHero ? " NEEDS-HERO" : "")}";
            return new RaidCandidate(target, score.Value, las, explain,
                costedMover: estimate.PlannedMoverArmyId ?? costedMover);
        }

        private static MissionProposal BuildProposal(WorldSnapshot snap, RaidCandidate c,
            ISet<int> unavailableArmyIds)
        {
            var excluded = unavailableArmyIds == null
                ? new HashSet<int>() : new HashSet<int>(unavailableArmyIds);
            if (c.IsIncumbent && c.PreferredMover.HasValue)
                excluded.Remove(c.PreferredMover.Value);
            int? pricedMover = c.PreferredMover ?? c.CostedMover;
            RaidCostEstimate estimate = RaidCostModel.Estimate(snap, c.Target, pricedMover);
            MissionRequirements req = estimate.Requirements;
            if (c.Target.Phase == RaidMissionPhase.AirSupport)
            {
                ArmySnapshot wing = snap.Self?.Armies?.FirstOrDefault(a => a != null
                    && c.Target.AirSupportArmyId.HasValue
                    && a.ArmyId == c.Target.AirSupportArmyId.Value);
                float activation = wing != null && !wing.HasActivatedThisTurn
                    ? wing.ActivationApCost : 0f;
                float energy = wing != null && !wing.HasActivatedThisTurn
                    ? wing.ActivationEnergyCost : 0f;
                req = new MissionRequirements
                {
                    RequiresArmy = true, RequiresHero = false, MoverKnown = wing != null,
                    ApMinimum = activation, ApDesired = activation, ApMaximum = activation,
                    EnergyMinimum = energy, EnergyDesired = energy, EnergyMaximum = energy,
                    EstimatedDistance = wing == null ? 0
                        : HexGridMath.Distance(wing.Hex, c.Target.DestinationHex),
                    EtaTurns = 1,
                };
            }
            int? plannedMover = c.PreferredMover ?? estimate.PlannedMoverArmyId ?? c.CostedMover;

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
                // Proposal-side witness: this is the same actor whose real route/AP envelope was
                // priced before ResourceAllocator.Pack. Provisioning still owns final live binding.
                PreferredMoverArmyId = plannedMover,
            };
            proposal.Axes.Value[DesireAxis.Aggression] = 1.0f;
            if (c.Target.Phase == RaidMissionPhase.Assault)
                GroundCombatAdmissionRegistry.Record(proposal, snap, excluded);
            else if (c.Target.Phase == RaidMissionPhase.Reinforcement
                && !c.Target.SupportArmyId.HasValue)
                GroundCombatAdmissionRegistry.RecordReinforcement(proposal, snap);
            return proposal;
        }

        private static string F(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    }
}
