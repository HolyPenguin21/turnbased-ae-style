using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.HexGrid;
using Game.Combat;
using Game.Aviation;

namespace Game.Ai.V2
{
    internal static partial class AggressionMissionLayer
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
            // AI-01 — the full activation AP of the roster the Assault assembly plan would really
            // produce (host + planned transfers), carried from ToCandidate's assembly projection
            // to BuildProposal so the proposal's envelope is priced off the SAME projection the
            // score was folded from, instead of re-deriving a host-only cost a second time.
            public readonly int? ProjectedActivationAp;
            public readonly bool IsCompletedTargetFallback;

            public RaidCandidate(RaidMissionTarget target, float baseValue, float localAdmissionScore,
                string explain, bool isIncumbent = false, CommitmentTier tier = CommitmentTier.None,
                int? preferredMover = null, int? costedMover = null,
                int? projectedActivationAp = null, bool isCompletedTargetFallback = false)
            {
                Target = target;
                BaseValue = baseValue;
                LocalAdmissionScore = localAdmissionScore;
                Explain = explain;
                IsIncumbent = isIncumbent;
                Tier = tier;
                PreferredMover = preferredMover;
                CostedMover = costedMover;
                ProjectedActivationAp = projectedActivationAp;
                IsCompletedTargetFallback = isCompletedTargetFallback;
            }

            public RaidCandidate AsIncumbent(CommitmentTier tier, int? preferredMover)
            {
                // ToCandidate already pinned the assembly projection and price before folding.
                // Never substitute a different mover AFTER scoring: that previously preserved the
                // cheap army's score but funded the durable primary's more expensive route.
                return new RaidCandidate(Target, BaseValue, LocalAdmissionScore,
                    Explain + $" [incumbent {tier}; funding protected separately]",
                    true, tier, preferredMover, CostedMover, ProjectedActivationAp);
            }
        }

        public static List<MissionProposal> Propose(WorldSnapshot snap, DesireBreakdown breakdown,
            IReadOnlyList<MissionIntent> activeIntents,
            IReadOnlyList<AggressionObjective> frozenObjectives, AiTurnContext ctx = null)
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
            {
                // ATK §50/§51 — a target standing on a known hostile Base/Citadel belongs to that
                // structure's defender package, which only the Attack lane may take on. The same
                // DEFER the ActiveDefence lane already issues for this exact situation. Without it
                // the raid kept being proposed and kept being refused at the movement-authority
                // gate (its terminal step is Combat, never CombatAndCapture), one stalled step per
                // turn, instead of the Attack owner picking the site up.
                if (RaidTargetSitsOnHostileStructure(snap, o))
                {
                    AiDebugLog.WriteDeduped(o.Target.DiagnosticLabel,
                        $"[AI][V2][Raid][Admission] decision=DEFER target={o.Target.DiagnosticLabel} "
                        + "reason=target_on_known_foreign_structure attack_owner_required");
                    continue;
                }
                fresh.Add(ToCandidate(snap, o, breakdown, unavailableArmyIds: committed));
            }

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
                    if (intent.Raid.Phase == RaidMissionPhase.RecoveryReturn)
                    {
                        RaidCandidate? recovery = ReturnCandidate(intent,
                            RaidMissionPhase.RecoveryReturn, intent.Raid.PrimaryArmyId,
                            intent.Raid.RecoveryBaseHex);
                        if (recovery.HasValue) incumbents.Add(recovery.Value);
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
                        RaidCandidate? sup = ReinforcementCandidate(snap, intent, committed);
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

            foreach (RaidCandidate c in incumbents.Where(x => x.IsCompletedTargetFallback))
                picked.Add(c);

            IEnumerable<RaidCandidate> ordinary = incumbents
                .Where(x => x.Tier == CommitmentTier.None && !x.IsCompletedTargetFallback)
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

            AppendActiveDefence(snap, activeIntents, committed, proposals, ctx);
            // ATK §40 — Attack is a peer lane of the same Aggression planner, appended through the
            // same one entry point; it is never orchestrated separately (§81).
            AppendAttack(snap, activeIntents, committed, proposals, ctx);
            return proposals;
        }

        private static void AppendActiveDefence(WorldSnapshot snap,
            IReadOnlyList<MissionIntent> activeIntents, ISet<int> committed,
            List<MissionProposal> proposals, AiTurnContext ctx)
        {
            if (activeIntents != null)
                foreach (MissionIntent intent in activeIntents.Where(i => i?.ActiveDefence != null
                    && i.Status == IntentStatus.Active
                    && i.ActiveDefence.Phase == ActiveDefencePhase.Return
                    && i.ActiveDefence.PrimaryArmyId.HasValue
                    && i.ActiveDefence.ReturnHex.HasValue))
                {
                    ActiveDefenceIntent d = intent.ActiveDefence;
                    ArmySnapshot actor = snap.Self?.Armies?.FirstOrDefault(a => a != null
                        && a.ArmyId == d.PrimaryArmyId.Value);
                    if (actor == null) continue;
                    MissionRequirements requirements = GroundCombatLegs.PinnedLegRequirements(
                        actor, d.ReturnHex.Value, out int eta);
                    var target = new ActiveDefenceMissionTarget
                    {
                        Phase = ActiveDefencePhase.Return, EnemyArmyId = d.EnemyArmyId,
                        LastKnownHex = d.LastKnownHex, LastObservedTurn = d.LastObservedTurn,
                        Confidence = d.Confidence, ProtectedAssetHex = d.ProtectedAssetHex,
                        ProtectedAssetKind = d.ProtectedAssetKind,
                        ProtectedAssetValue = d.ProtectedAssetValue,
                        ThreatSeverity = d.ThreatSeverity, PrimaryArmyId = d.PrimaryArmyId,
                        ReturnHex = d.ReturnHex, EstimatedEta = eta,
                    };
                    var proposal = new MissionProposal
                    {
                        Kind = MissionKind.ActiveDefence, Target = target,
                        BaseValue = 0f, LocalAdmissionScore = 0f,
                        PreferredMoverArmyId = actor.ArmyId,
                        FromDurableIntent = true, DurableFundingTier = intent.Funding,
                        Requirements = requirements,
                        Explain = $"ActiveDefence Return actor #{actor.ArmyId} -> {d.ReturnHex.Value.Q},{d.ReturnHex.Value.R}",
                    };
                    proposal.Axes.Value[DesireAxis.Aggression] = 1f;
                    proposals.Add(proposal);
                }
            foreach (ActiveDefenceObjective objective in ActiveDefenceObjectiveEvaluator.Enumerate(snap))
            {
                MissionIntent incumbent = activeIntents?.FirstOrDefault(i => i != null
                    && i.Status == IntentStatus.Active && i.Kind == MissionKind.ActiveDefence
                    && i.ActiveDefence?.EnemyArmyId == objective.Target.EnemyArmyId);
                int? pinnedActor = incumbent?.ActiveDefence?.PrimaryArmyId;
                var excluded = committed == null
                    ? new HashSet<int>() : new HashSet<int>(committed);
                if (pinnedActor.HasValue) excluded.Remove(pinnedActor.Value);
                EnemyContactSnapshot contact = snap.Threat?.Contacts?.FirstOrDefault(c =>
                    c?.Army != null && c.Army.ArmyId == objective.Target.EnemyArmyId
                    && c.Source == ContactSource.Honest && c.Position.HasValue);
                if (contact == null) continue;
                IReadOnlyList<WorthIt.DefendingArmy> opposition = new[]
                    { new WorthIt.DefendingArmy(contact.Army.Members, contact.Army.Commander) };

                GroundCombatAssemblyPlan plan = GroundCombatAssemblyPlanner.Plan(snap,
                    new GroundCombatAssemblyRequest
                    {
                        Opposition = opposition,
                        WinChanceGate = pinnedActor.HasValue
                            ? GroundCombatAdmissionPolicy.ContinuationWinChanceFloor
                            : GroundCombatAdmissionPolicy.FreshStartWinChanceGate,
                        PreferredPrimaryArmyId = pinnedActor,
                        PinToPreferred = pinnedActor.HasValue,
                        ExcludedArmyIds = excluded,
                    });
                float moverOpportunityCost = 0f;
                MissionIntent borrowedOffensive = null;
                if (!plan.Feasible && incumbent == null && ctx?.Map != null && activeIntents != null)
                {
                    // ATK §49/§73 — EVERY offensive ground-combat operation currently marching on
                    // its objective is a preemption candidate, not Raid alone. The suspend/resume
                    // machinery below and in Continuity was already lane-neutral
                    // (SuspendedOffensiveIntentKey); this enumeration was the last Raid-only link,
                    // which meant an urgent threat could never borrow an Attack primary however
                    // close it stood.
                    foreach (MissionIntent offensiveIntent in activeIntents)
                    {
                        if (!MissionContinuityLayer.TryOffensiveAssaultOperation(offensiveIntent,
                                out int offensivePrimaryId, out HexCoord offensiveHex))
                            continue;
                        ArmySnapshot candidate = snap.Self.Armies.FirstOrDefault(a => a != null
                            && a.ArmyId == offensivePrimaryId);
                        if (candidate == null) continue;
                        int direct = SafeStepPathing.FindSafePathCost(ctx.Map, candidate.Owner,
                            candidate.Hex, offensiveHex, candidate.MaxMovement);
                        int first = SafeStepPathing.FindSafePathCost(ctx.Map, candidate.Owner,
                            candidate.Hex, objective.Target.LastKnownHex, candidate.MaxMovement);
                        int second = SafeStepPathing.FindSafePathCost(ctx.Map, candidate.Owner,
                            objective.Target.LastKnownHex, offensiveHex,
                            candidate.MaxMovement);
                        if (direct == int.MaxValue || first == int.MaxValue || second == int.MaxValue)
                            continue;
                        int move = UnityEngine.Mathf.Max(1, candidate.MaxMovement);
                        int detour = AiV2Util.CeilDiv(first + second, move)
                            - AiV2Util.CeilDiv(direct, move);
                        if (detour > AiConfigV2.activeDefenceRaidMaxDetourTurns) continue;
                        var borrowExcluded = new HashSet<int>(committed);
                        borrowExcluded.Remove(candidate.ArmyId);
                        GroundCombatAssemblyPlan borrowed = GroundCombatAssemblyPlanner.Plan(snap,
                            new GroundCombatAssemblyRequest
                            {
                                Opposition = opposition,
                                WinChanceGate = GroundCombatAdmissionPolicy.FreshStartWinChanceGate,
                                PreferredPrimaryArmyId = candidate.ArmyId,
                                PinToPreferred = true,
                                ExcludedArmyIds = borrowExcluded,
                            });
                        if (!borrowed.Feasible) continue;
                        plan = borrowed;
                        excluded = borrowExcluded;
                        borrowedOffensive = offensiveIntent;
                        moverOpportunityCost = UnityEngine.Mathf.Max(0, detour)
                            * candidate.ActivationApCost
                            * AiConfigV2.taskScoreReactivationApWeight;
                        break;
                    }
                }
                if (!plan.Feasible)
                {
                    AiDebugLog.WriteDeduped(objective.Target.EnemyArmyId.ToString(),
                        $"[AI][V2][ActiveDefence][Assembly] decision=REJECT enemy={objective.Target.EnemyArmyId} reason={plan.Reason}");
                    continue;
                }

                ArmySnapshot actor = snap.Self.Armies.FirstOrDefault(a => a != null
                    && a.ArmyId == plan.BaseArmyId);
                if (actor == null) continue;
                int distance = HexGridMath.Distance(actor.Hex, objective.Target.LastKnownHex);
                // Price and time the force this plan will ACTUALLY field, exactly as the Raid lane
                // does. ActiveDefence shares GroundCombatAssemblyPlanner
                // with Raid, so its plan may recruit same-hex bodies too; costing the untouched
                // host systematically underprices the intercept (the AP the allocator then funds)
                // and over-states its speed (a slower recruit drags the whole formation down).
                // Null means the projection does not resolve live — fall back to the snapshot
                // figure rather than invent a second cost model.
                int projectedMove = GroundCombatAssemblyPlanner.ProjectedMaxMovement(snap, plan)
                    ?? actor.MaxMovement;
                int eta = AiV2Util.CeilDiv(distance,
                    UnityEngine.Mathf.Max(AiConfigV2.etaFallbackMoveBudget, projectedMove));
                TaskScore actorScore = ActiveDefenceObjectiveEvaluator.WithResponse(objective,
                    actor, plan.ProjectedWinChance, eta, moverOpportunityCost,
                    GroundCombatAssemblyPlanner.ProjectedActivationApCost(snap, plan));
                ActiveDefenceMissionTarget target = objective.Target;
                target.PrimaryArmyId = actor.ArmyId;
                target.ProjectedWinChance = plan.ProjectedWinChance;
                target.CoversAllDefenders = plan.CoversAllDefenders;
                target.EstimatedEta = eta;
                target.SuspendedOffensiveIntentKey = borrowedOffensive?.IntentKey;
                target.ReturnHex = actor.ReachableOwnBaseHexes?
                    .OrderBy(h => HexGridMath.Distance(actor.Hex, h))
                    .ThenBy(h => h.Q).ThenBy(h => h.R)
                    .Select(h => (HexCoord?)h).FirstOrDefault()
                    ?? snap.Self.BaseHexes.OrderBy(h => HexGridMath.Distance(actor.Hex, h))
                        .ThenBy(h => h.Q).ThenBy(h => h.R)
                        .Select(h => (HexCoord?)h).FirstOrDefault();
                float ap = actor.HasActivatedThisTurn ? 0f
                    : GroundCombatAssemblyPlanner.ProjectedActivationApCost(snap, plan)
                        ?? actor.ActivationApCost;
                var proposal = new MissionProposal
                {
                    Kind = MissionKind.ActiveDefence,
                    Target = target,
                    BaseValue = actorScore.Value,
                    LocalAdmissionScore = actorScore.Value,
                    PreferredMoverArmyId = actor.ArmyId,
                    FromDurableIntent = incumbent != null,
                    DurableFundingTier = incumbent?.Funding ?? CommitmentTier.None,
                    Requirements = new MissionRequirements
                    {
                        MoverKnown = true, RequiresArmy = true,
                        ApMinimum = ap, ApDesired = ap, ApMaximum = ap,
                        EtaTurns = eta, EstimatedDistance = distance,
                        CombatPowerMinimum = contact.Army.EffectiveArmyPower,
                        CombatPowerDesired = contact.Army.EffectiveArmyPower,
                    },
                    Explain = $"ActiveDefence enemy #{target.EnemyArmyId} -> asset "
                        + $"{target.ProtectedAssetKind}@{target.ProtectedAssetHex.Q},{target.ProtectedAssetHex.R} "
                        + $"task {actorScore.Value:0.00} win {plan.ProjectedWinChance:0.00} eta {eta}",
                };
                proposal.Axes.Value[DesireAxis.Aggression] = 1f;
                GroundCombatAdmissionRegistry.RecordActiveDefence(proposal, snap, opposition, excluded);
                if (GroundCombatAdmissionRegistry.TryGet(proposal, out HashSet<int> eligible)
                    && eligible.Count > 0)
                {
                    proposals.Add(proposal);
                    AiDebugLog.WriteDeduped(target.EnemyArmyId.ToString(),
                        $"[AI][V2][ActiveDefence][Admission] decision=PROPOSE enemy={target.EnemyArmyId} actor={actor.ArmyId} score={actorScore.Value:0.00}");
                }
            }
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
            string label = phase == RaidMissionPhase.SupportReturn ? "SUPPORT-RETURN"
                : phase == RaidMissionPhase.RecoveryReturn ? "RECOVERY-RETURN" : "RETURN";
            string role = phase == RaidMissionPhase.SupportReturn ? "support" : "primary";
            AiDebugLog.Write($"[AI][V2]   raid mission — {label} {intent.IntentKey}: {role} "
                + $"#{moverArmyId.Value} -> ({homeHex.Value.Q},{homeHex.Value.R})");
            string protection = ri.CompletedTargetAwaitingFreshDecision
                ? "fresh-decision fallback; no commitment protection"
                : "Hard funding protection is allocator-owned";
            return new RaidCandidate(target, value, value,
                $"Raid {ri.Target.DiagnosticLabel} {phase}: {role} #{moverArmyId.Value} to base "
                + $"({homeHex.Value.Q},{homeHex.Value.R}); intrinsic={F(value)}; {protection}",
                true, intent.Funding, moverArmyId, moverArmyId,
                isCompletedTargetFallback: ri.CompletedTargetAwaitingFreshDecision);
        }

        // Reinforcement is a durable continuation leg. Target discovery/value is not recomputed here;
        // its Hard commitment, not a synthetic strategic value, owns continuation priority.
        // `committed` — armies claimed by other operations; the same exclusion Demand and
        // Provisioning apply, so an unpinned leg is proposed only while a bindable support exists.
        private static RaidCandidate? ReinforcementCandidate(WorldSnapshot snap, MissionIntent intent,
            ISet<int> committed)
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
                IReadOnlyList<WorthIt.DefendingArmy> opposition = AiV2Util.KnownOpposition(snap, ri.Target);
                List<int> candidates = GroundCombatAssemblyPlanner.ReinforcementSupportCandidates(
                    snap, primaryId, opposition, committed);
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
                RaidRecoveryProjection unpinnedProjection =
                    RaidRecoveryPlanner.ProjectFieldForSupport(snap, ri);
                float unpinnedValue = unpinnedProjection.Viable
                    ? unpinnedProjection.Score.Value : default(TaskScore).Value;
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
            RaidRecoveryProjection projection =
                RaidRecoveryPlanner.ProjectFieldForSupport(snap, ri, ri.SupportArmyId.Value);
            float value = projection.Viable
                ? projection.Score.Value : default(TaskScore).Value;
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
            if (ri == null || ri.Target.Kind != RaidTargetKind.NeutralArmy
                || !ri.PrimaryArmyId.HasValue
                || (ri.Phase != RaidMissionPhase.AirSupport
                    && ri.Phase != RaidMissionPhase.Reinforcement))
                return null;

            RaidRecoveryProjection scored;
            int airId;
            HexCoord landing;
            if (ri.Phase == RaidMissionPhase.AirSupport)
            {
                if (!ri.AirSupportArmyId.HasValue)
                    return null;
                airId = ri.AirSupportArmyId.Value;
                scored = RaidRecoveryPlanner.ProjectAirSupportForWing(snap, ri, airId);
                landing = ri.AirSupportLandingHex
                    ?? (snap.Self.BaseHexes ?? System.Array.Empty<HexCoord>())
                        .OrderBy(h => HexGridMath.Distance(h, ri.LastKnownHex))
                        .ThenBy(h => h.Q).ThenBy(h => h.R).FirstOrDefault();
            }
            else
            {
                // A not-yet-started weak Raid can still request aviation, but wing choice and
                // importance now come only from the same canonical comparison as field/base
                // recovery. Missions never applies a second nearest-wing heuristic.
                scored = RaidRecoveryPlanner.Choose(snap, ri, committed);
                if (!scored.Viable || scored.Phase != RaidMissionPhase.AirSupport
                    || !scored.AirSupportArmyId.HasValue
                    || !scored.AirSupportLandingHex.HasValue)
                    return null;
                airId = scored.AirSupportArmyId.Value;
                landing = scored.AirSupportLandingHex.Value;
            }

            ArmySnapshot wing = snap.Self?.Armies?.FirstOrDefault(a => a != null
                && a.ArmyId == airId && a.IsAir && !a.IsAirfield && !a.IsPrison);
            if (wing == null)
                return null;
            int eta = GroundCombatAirSupport.SortieEta(wing, ri.LastKnownHex);
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
                DefenderCount = AiV2Util.KnownDefenders(snap, ri.Target).Count,
                EstimatedEta = eta,
                AirSupportAttemptedTurn = ri.AirSupportAttemptedTurn,
                AirSupportStrikeSucceeded = ri.AirSupportStrikeSucceeded,
            };
            float value = scored.Viable ? scored.Score.Value : default(TaskScore).Value;
            return new RaidCandidate(target, value, value,
                $"Raid {ri.Target.DiagnosticLabel} AirSupport: execute scored wing #{airId}; "
                    + $"intrinsic={F(value)}",
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
            IReadOnlyList<WorthIt.DefendingArmy> opposition = AiV2Util.KnownOpposition(snap, o.Target);

            GroundCombatAssemblyPlan live = pinnedPrimaryArmyId.HasValue
                ? GroundCombatAssemblyPlanner.Plan(snap, new GroundCombatAssemblyRequest
                {
                    Opposition = opposition,
                    PreferredPrimaryArmyId = pinnedPrimaryArmyId,
                    PinToPreferred = true,
                    ExcludedArmyIds = excluded,
                    WinChanceGate = operationStarted
                        ? GroundCombatAdmissionPolicy.ContinuationWinChanceFloor
                        : GroundCombatAdmissionPolicy.FreshStartWinChanceGate,
                })
                : GroundCombatAssemblyPlanner.Plan(snap, target, opposition, excluded);

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

            // AI-01 — price the roster the assembly will actually field. Only when the projection's
            // host IS the actor being costed; otherwise the AP of one army would be attached to
            // another army's route, the exact confusion RaidCostModel already guards against.
            int? projectedActivationAp =
                target.Phase == RaidMissionPhase.Assault && live.Feasible
                && costedMover.HasValue && live.BaseArmyId == costedMover.Value
                    ? GroundCombatAssemblyPlanner.ProjectedActivationApCost(snap, live)
                    : null;

            RaidCostEstimate estimate = RaidCostModel.Estimate(snap, target, costedMover,
                projectedActivationAp);
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

            string explain = $"Raid {o.Target.DiagnosticLabel} @{o.LastKnownHex.Q},{o.LastKnownHex.R} "
                + $"task {F(score.Value)} readyWin {F(readyWin)} frozenReady {F(o.ReadyWinChance)} "
                + $"asmWin {F(o.AssemblableWinChance)} def {o.DefenderCount} eta {req?.EtaTurns ?? o.EstimatedEta} "
                + $"aggRaidPolicy {F(bd.AggRaidOpportunity)} gate {(o.GatePassed ? 1 : 0)}"
                + $"{(o.NeedsCombatPower ? " NEEDS-POWER" : "")}{(o.NeedsHero ? " NEEDS-HERO" : "")}";
            return new RaidCandidate(target, score.Value, las, explain,
                costedMover: estimate.PlannedMoverArmyId ?? costedMover,
                projectedActivationAp: projectedActivationAp);
        }

        private static MissionProposal BuildProposal(WorldSnapshot snap, RaidCandidate c,
            ISet<int> unavailableArmyIds)
        {
            var excluded = unavailableArmyIds == null
                ? new HashSet<int>() : new HashSet<int>(unavailableArmyIds);
            if (c.IsIncumbent && c.PreferredMover.HasValue)
                excluded.Remove(c.PreferredMover.Value);
            int? pricedMover = c.PreferredMover ?? c.CostedMover;
            // AI-01 — reuse ToCandidate's assembly projection instead of re-deriving a host-only
            // cost here; the proposal's envelope and the folded score must describe the same force.
            // Only valid while the mover priced here is still the one that projection was built on.
            int? projectedActivationAp = c.ProjectedActivationAp.HasValue
                && pricedMover.HasValue && c.CostedMover.HasValue
                && pricedMover.Value == c.CostedMover.Value
                    ? c.ProjectedActivationAp : null;
            RaidCostEstimate estimate = RaidCostModel.Estimate(snap, c.Target, pricedMover,
                projectedActivationAp);
            MissionRequirements req = estimate.Requirements;
            if (c.Target.Phase == RaidMissionPhase.AirSupport)
            {
                ArmySnapshot wing = snap.Self?.Armies?.FirstOrDefault(a => a != null
                    && c.Target.AirSupportArmyId.HasValue
                    && a.ArmyId == c.Target.AirSupportArmyId.Value);
                req = GroundCombatAirSupport.LegRequirements(wing, c.Target.DestinationHex,
                    c.Target.EstimatedEta);
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
                GroundCombatAdmissionRegistry.RecordReinforcement(proposal, snap, excluded);
            return proposal;
        }

        // §50/§51 — honest knowledge only, through the one hostile-structure predicate the Attack
        // objective evaluator owns. A NEUTRAL structure (the ordinary guarded-event or neutral-base
        // raid) is deliberately not covered: taking that over after the fight is existing, intended
        // gameplay and stays exactly as it was.
        private static bool RaidTargetSitsOnHostileStructure(WorldSnapshot snap, AggressionObjective o)
        {
            IReadOnlyList<AiMapMemory.KnownBuilding> buildings = snap?.Known?.Buildings;
            if (buildings == null || o == null)
                return false;
            HexCoord hex = o.LastKnownHex;
            for (int i = 0; i < buildings.Count; i++)
                if (buildings[i].Hex.Equals(hex)
                    && AttackObjectiveEvaluator.IsHostileStrategicStructure(buildings[i], snap.Observer))
                    return true;
            return false;
        }

        private static string F(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    }
}
