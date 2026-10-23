using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  ATK §40 — THE ATTACK MISSION PROPOSALS.
    //
    //  A mechanical partial of AggressionMissionLayer, not a second owner: the Aggression mission
    //  planner stays the one place Aggression proposals are built, and this file only holds the
    //  Attack lane's own semantics (which structure, which defender package, which leg). Every
    //  physical decision below — who can take the fight, what the assembled roster costs, how fast
    //  it travels — is asked of the shared GroundCombat kernel, exactly as Raid and ActiveDefence
    //  already ask it.
    // ===========================================================================================
    internal static partial class AggressionMissionLayer
    {
        internal static void AppendAttack(WorldSnapshot snap,
            IReadOnlyList<MissionIntent> activeIntents, ISet<int> committed,
            List<MissionProposal> proposals, AiTurnContext ctx,
            IDictionary<MissionIntentKey, string> deferredThisPass, IMissionIntentView intentState,
            ICollection<RetreatWitnessRevision> witnessRevisions)
        {
            if (snap?.Self == null || snap.Self.AttackPeak <= 0f)
                return;

            // Enumerated once, before the durable legs: a preparation's walk and the Assault loop
            // below ask the same question of it (AttackIntermediateBasePolicy.SelectForPreparationWalk).
            List<AttackObjective> objectives = AttackObjectiveEvaluator.Enumerate(snap);

            // ---- durable legs that carry their own pinned actor and destination ---------------
            if (activeIntents != null)
                foreach (MissionIntent intent in activeIntents.Where(i => i?.Attack != null
                    && i.Status == IntentStatus.Active))
                {
                    AttackIntent a = intent.Attack;
                    if (a.Phase == AttackMissionPhase.RecoveryReturn)
                        AppendAttackWalkHome(snap, intent, a, AttackMissionPhase.RecoveryReturn,
                            a.PrimaryArmyId, a.RecoveryBaseHex, proposals);
                    else if (a.Phase == AttackMissionPhase.SupportReturn)
                        AppendAttackWalkHome(snap, intent, a, AttackMissionPhase.SupportReturn,
                            a.SupportArmyId, a.SupportReturnHex, proposals);
                    else if (a.Phase == AttackMissionPhase.Reinforcement)
                        AppendAttackReinforcement(snap, intent, a, committed, proposals, ctx,
                            deferredThisPass);
                    else if (a.Phase == AttackMissionPhase.Gather)
                        AppendAttackGather(snap, intent, a, committed, proposals, ctx, intentState, objectives);
                    // Strike force step 5 — donors that already handed over walk home beside
                    // whatever the operation itself does.
                    foreach (AttackGatherReturn r in a.GatherReturns)
                        AppendAttackWalkHome(snap, intent, a, AttackMissionPhase.GatherReturn,
                            r.ArmyId, r.BaseHex, proposals);
                    // The bound support wing flies its sortie beside the operation too.
                    AppendAttackAirSupport(snap, intent, a, proposals);
                }

            // ---- Assault: fresh objectives and incumbents still marching on their target ------
            bool attackProposed = false;
            // 2026-09-30 (user decision) — at most ONE live Attack operation per player: while one
            // is live no fresh objective is offered, and of several fresh candidates only the best
            // reaches the allocator (it could otherwise fund two operations in one pass).
            MissionIntent liveOperation = LiveAttackOperation(activeIntents);
            // 2026-10-21 (user decision) — beside it, at most one surplus-only second operation
            // (AttackSecondaryPolicy); only the best fresh one reaches the allocator.
            MissionIntent liveSecondary = LiveSecondaryAttack(activeIntents);
            var secondaryCandidates = new List<MissionProposal>();
            var freshCandidates = new List<MissionProposal>();
            // Why an incumbent Assault got no proposal this pass (the planner knows it right where
            // it declines); published below as the planner deferral Continuity reads.
            var assaultWhy = new Dictionary<MissionIntentKey, string>();
            foreach (AttackObjective objective in objectives)
            {
                MissionIntent incumbent = activeIntents?.FirstOrDefault(i => i != null
                    && i.Status == IntentStatus.Active && i.Kind == MissionKind.Attack
                    && i.Attack != null && i.Attack.Target.Equals(objective.Target));
                // 2026-10-21 — a preparation host walking to its staging Base may take a Base on
                // the way decisively (its MoveHost step is then not offered: AppendAttackGather).
                AttackObjective prepDetour = incumbent?.Attack?.Phase == AttackMissionPhase.Gather
                    ? AttackIntermediateBasePolicy.SelectForPreparationWalk(snap, incumbent.Attack, objectives)
                    : null;
                // An incumbent already past the Assault leg is being proposed above; do not also
                // offer it a fresh assault against the same target this pass.
                if (incumbent != null && incumbent.Attack.Phase != AttackMissionPhase.Assault
                    && prepDetour == null)
                    continue;
                // A fresh objective while the main operation lives may only be the surplus-only
                // second operation: one at a time, never on a site whose defenders are unknown.
                bool secondary = incumbent == null && liveOperation != null;
                // A Base the main march has taken as its optional local fight is already served.
                bool detourOfMain = secondary && liveOperation.Attack.IntermediateTarget == objective.Target;
                if (secondary && (liveSecondary != null || objective.LocationOnly || detourOfMain))
                {
                    AiDebugLog.WriteDeduped(objective.Target.DiagnosticLabel,
                        $"[AI][V2][Attack][Assembly] decision=HOLD target={objective.Target.DiagnosticLabel} "
                        + $"blocker=operation_live:{liveOperation.IntentKey}"
                        + (liveSecondary != null ? $" secondary_live:{liveSecondary.IntentKey}"
                            : detourOfMain ? " main_detour" : " location_only"));
                    continue;
                }
                secondary |= incumbent?.Attack?.Secondary == true;
                // T01 — sanctioned coordinates are not an observed defender package: a march on a
                // never-observed site is not proposed (Recon observes it; mobilization may prepare).
                // 2026-10-01 (user decision) — a preparation whose host reached the peak bar is
                // already an Assault incumbent (Continuity): it marches and observes on the way.
                if (objective.LocationOnly && incumbent == null)
                {
                    AiDebugLog.WriteDeduped(objective.Target.DiagnosticLabel,
                        $"[AI][V2][Attack][Assembly] decision=HOLD target={objective.Target.DiagnosticLabel} "
                        + "blocker=unknown_defenders knowledge=starting-location-only");
                    continue;
                }

                int? pinnedActor = incumbent?.Attack?.PrimaryArmyId;
                // A started Assault keeps its committed members and drops the fresh power bar; the
                // one fact Missions, admission (ForceCommitted) and provisioning all read.
                bool assaultStarted = incumbent?.Attack?.AssaultStarted == true;
                // The preparation's detour fields the host exactly as it stands (no same-hex
                // reshuffle, no fist bar): the leg is committed like a marching Assault's optional
                // Base. A secondary leg's start bar is AttackSecondaryPolicy.ClearsStart instead.
                bool keepsRoster = assaultStarted || prepDetour != null;
                var excluded = committed == null ? new HashSet<int>() : new HashSet<int>(committed);
                if (pinnedActor.HasValue)
                    excluded.Remove(pinnedActor.Value);

                // A secondary operation takes its one decisive target, no optional Base on the way.
                AttackObjective intermediate = secondary ? null
                    : prepDetour ?? AttackIntermediateBasePolicy.Select(snap, incumbent?.Attack, objectives);

                // 2026-10-08 — the ONE local decision of a marching army, taken here, before
                // funding (AttackTacticalOpportunity.Decide). It is frozen into the leg below and
                // only verified at execution. Priority: relevant unwinnable hostile army (the
                // attack goes home, Continuity moves the phase) > main target reachable now >
                // ActiveDefence objective > intermediate Base > other significant army > march.
                AttackLocalAction local = AttackLocalAction.Continue();
                ArmySnapshot marching = incumbent?.Attack?.Phase == AttackMissionPhase.Assault
                    && incumbent.Attack.OperationStarted && pinnedActor.HasValue
                        ? snap.Self.Armies?.FirstOrDefault(x => x != null && x.ArmyId == pinnedActor.Value)
                        : null;
                if (marching != null && marching.IsStructuralRaidActor)
                {
                    local = AttackTacticalOpportunity.Decide(snap, marching, objective.Target,
                        incumbent.Attack.LastOpportunisticStrikeTurn, intermediate != null,
                        ActiveDefenceObjectiveEvaluator.Enumerate(snap), intentState);
                    if (local.Kind == AttackLocalActionKind.Retreat)
                    {
                        AiDebugLog.WriteDeduped(objective.Target.DiagnosticLabel + "#retreat",
                            $"[AI][V2][Attack][Local] decision=RETREAT target={objective.Target.DiagnosticLabel} "
                            + $"enemy=#{local.EnemyArmyId} at ({local.Hex.Q},{local.Hex.R}) win={F(local.WinChance)} "
                            + $"reason={local.Reason}; no assault step proposed, Continuity withdraws the army");
                        assaultWhy[incumbent.IntentKey] = "attack_retreat_hostile_contact";
                        continue;
                    }
                    // A higher-priority action replaces the optional Base for this pass.
                    if (local.Kind != AttackLocalActionKind.IntermediateBase)
                        intermediate = null;
                }
                AttackObjective assaultObjective = intermediate ?? objective;
                IReadOnlyList<WorthIt.DefendingArmy> opposition = assaultObjective.Opposition;
                // §30 — the honest, knowledge-scoped answer to "what defence does a defender on
                // that hex actually get". Never a live BuildingRegistry read.
                float hexBonus = AiMapMemory.KnownHexDefenseBonus(
                    snap?.Observer, ctx?.Map, assaultObjective.Hex);

                GroundCombatAssemblyPlan plan = GroundCombatAssemblyPlanner.Plan(snap,
                    new GroundCombatAssemblyRequest
                    {
                        Opposition = opposition,
                        WinChanceGate = prepDetour != null
                            ? AttackIntermediateBasePolicy.PreparationWalkWinChanceGate
                            : intermediate != null
                            ? GroundCombatAdmissionPolicy.AttackIntermediateBaseWinChanceGate
                            : assaultStarted ? GroundCombatAdmissionPolicy.AttackLocalWinChanceGate
                            : GroundCombatAdmissionPolicy.AttackWinChanceGate,
                        RequireCoverage = intermediate != null
                            ? GroundCombatAdmissionPolicy.AttackIntermediateBaseRequiresCoverage
                            : (bool?)null,
                        AllowSameHexAssembly = intermediate == null,
                        PreserveDepartureRoster = keepsRoster,
                        MinimumArmyPower = keepsRoster || secondary
                            ? 0f : AttackForceReadiness.RequiredPower(snap.Self.AttackPeak),
                        AllowDecisivePinnedDefender = true,
                        PreferredPrimaryArmyId = pinnedActor,
                        PinToPreferred = pinnedActor.HasValue,
                        ExcludedArmyIds = excluded,
                        DefenderHexDefenseBonus = hexBonus,
                        AcceptPlan = marching != null ? null : candidate =>
                        {
                            bool allowed = AttackSecondaryPolicy.CanStart(snap,
                                new AttackMissionTarget { Target = objective.Target, Secondary = secondary,
                                    ForceCommitted = keepsRoster },
                                candidate, intentState, opposition, out string why);
                            if (!allowed)
                                AiDebugLog.WriteDeduped(objective.Target.DiagnosticLabel + "#" + candidate.BaseArmyId,
                                    $"[AI][V2][Attack][Assembly] decision=REJECT_CANDIDATE "
                                    + $"target={objective.Target.DiagnosticLabel} actor=#{candidate.BaseArmyId} "
                                    + $"reason={why}; checking remaining candidates");
                            return allowed;
                        },
                    });

                if (!plan.Feasible)
                {
                    // Audit F7 — a FRESH objective no single army nor same-hex package can take
                    // may still be formed by free armies spread over several hexes: gather them.
                    if (incumbent == null && !secondary && AttackForceReadiness.MobilizationOpen(snap.Self)
                        && TryAppendFreshAttackGather(snap, objective, opposition,
                            hexBonus, excluded, freshCandidates, intentState))
                    {
                        attackProposed = true;
                        continue;
                    }
                    // 2026-10-21 — no field host can take it: a garrison on an own Base may still,
                    // as the second operation (AttackSecondaryPolicy.PlanGarrisonSortie).
                    if (incumbent == null && liveSecondary == null
                        && TryAppendGarrisonSortie(snap, objective, hexBonus, excluded, secondaryCandidates))
                        continue;
                    // §24 — a started operation whose primary can no longer clear the site is a
                    // REINFORCEMENT decision, not a dead objective. Continuity moves the phase;
                    // the planner only refrains from proposing an impossible assault.
                    AiDebugLog.WriteDeduped(objective.Target.DiagnosticLabel,
                        $"[AI][V2][Attack][Assembly] decision=REJECT target={objective.Target.DiagnosticLabel} "
                        + $"reason={plan.Reason}");
                    if (incumbent != null)
                        assaultWhy[incumbent.IntentKey] = "attack_assault_rejected_plan_infeasible";
                    continue;
                }

                ArmySnapshot actor = snap.Self.Armies?.FirstOrDefault(x => x != null
                    && x.ArmyId == plan.BaseArmyId);
                if (actor == null)
                {
                    if (incumbent != null)
                        assaultWhy[incumbent.IntentKey] = "attack_assault_actor_temporarily_unavailable";
                    continue;
                }

                bool observedDefence = AttackObjectiveEvaluator.HasAssaultObservation(snap,
                    assaultObjective.Target, out string intelWhy);
                if (!observedDefence && HexGridMath.Distance(actor.Hex, assaultObjective.Hex) <= 1
                    && !local.IsLocalFight)
                {
                    if (incumbent != null) assaultWhy[incumbent.IntentKey] = "attack_assault_" + intelWhy;
                    AiDebugLog.WriteDeduped(objective.Target.DiagnosticLabel + "#observation",
                        $"[AI][V2][Attack][Assembly] decision=HOLD target={objective.Target.DiagnosticLabel} "
                        + $"actor=#{actor.ArmyId} blocker={intelWhy}; awaiting Recon before entry");
                    continue;
                }

                // Safety was tested per candidate before selection. Apply only the chosen
                // projected roster's witness revision; rejected candidates do not own this target.
                if (!assaultStarted)
                {
                    var projected = GroundCombatAssemblyPlanner.ProjectedDepartureArmies(snap, plan);
                    ArmySnapshot departure = projected.TryGetValue(actor.ArmyId, out ArmySnapshot assembled)
                        ? assembled : actor;
                    AttackRetreatWitness.Blocks(intentState, snap, objective.Target, departure,
                        out _, out RetreatWitnessRevision revision);
                    if (revision.Kind != RetreatWitnessRevisionKind.None)
                        witnessRevisions?.Add(revision);
                }

                // Price and time the force this plan will ACTUALLY field, through the same
                // projections Raid and ActiveDefence use — never a host-only figure.
                int projectedMove = GroundCombatAssemblyPlanner.ProjectedMaxMovement(snap, plan)
                    ?? actor.MaxMovement;
                int distance = intermediate != null
                    ? AttackIntermediateBasePolicy.Route(snap, actor, actor.Hex, assaultObjective.Hex)?.TotalCost ?? int.MaxValue
                    : AiV2Util.TravelCost(snap, actor, assaultObjective.Hex, maxMovement: projectedMove,
                        terrainOnly: true);
                if (distance == int.MaxValue)
                {
                    if (incumbent != null)
                        assaultWhy[incumbent.IntentKey] = "attack_assault_target_unreachable_this_pass";
                    continue;
                }
                int eta = AiV2Util.CeilDiv(distance,
                    Mathf.Max(AiConfigV2.etaFallbackMoveBudget, projectedMove));
                int? projectedAp = GroundCombatAssemblyPlanner.ProjectedActivationApCost(snap, plan);
                TaskScore score = AttackObjectiveEvaluator.WithResponse(assaultObjective, actor,
                    observedDefence ? plan.ProjectedWinChance : 0f, eta, 0f, projectedAp);

                // Once the assembled actor strictly clears the force threshold, it may march.

                var target = new AttackMissionTarget
                {
                    Phase = AttackMissionPhase.Assault,
                    Target = objective.Target,
                    IntermediateTarget = intermediate?.Target ?? AttackTargetRef.None,
                    PrimaryArmyId = actor.ArmyId,
                    DestinationHex = assaultObjective.Hex,
                    DefenderHexDefenseBonus = hexBonus,
                    DefenderCount = assaultObjective.DefenderCount,
                    ProjectedWinChance = observedDefence ? plan.ProjectedWinChance : 0f,
                    CoversAllDefenders = plan.CoversAllDefenders,
                    ForceCommitted = keepsRoster,
                    EstimatedEta = eta,
                    // §17 — carry the operation's own once-per-turn side-strike marker into the leg
                    // the executor will run. A fresh objective has no incumbent and therefore no
                    // marker, which is exactly right: it has taken no strike yet.
                    OpportunisticStrikeTurn =
                        incumbent?.Attack?.LastOpportunisticStrikeTurn ?? 0,
                    Local = intermediate != null ? default : local,
                    Secondary = secondary,
                };
                float ap = actor.HasActivatedThisTurn ? 0f
                    : projectedAp ?? actor.ActivationApCost;
                var proposal = new MissionProposal
                {
                    Kind = MissionKind.Attack,
                    Target = target,
                    BaseValue = score.Value,
                    Score = score,
                    LocalAdmissionScore = score.Value,
                    PreferredMoverArmyId = actor.ArmyId,
                    FromDurableIntent = incumbent != null,
                    DurableFundingTier = incumbent?.Funding ?? CommitmentTier.None,
                    Requirements = new MissionRequirements
                    {
                        MoverKnown = true,
                        RequiresArmy = true,
                        ApMinimum = ap, ApDesired = ap, ApMaximum = ap,
                        EtaTurns = eta, EstimatedDistance = distance,
                        CombatPowerMinimum = assaultObjective.TargetPower,
                        CombatPowerDesired = assaultObjective.TargetPower,
                    },
                    Explain = $"Attack {objective.Target.DiagnosticLabel} "
                        + (intermediate == null ? "" : $"via {intermediate.Target.DiagnosticLabel} ")
                        + $"task {F(score.Value)} win {(observedDefence ? F(plan.ProjectedWinChance) : "unknown")} "
                        + $"defenders {assaultObjective.DefenderCount} hexDef {F(hexBonus)} eta {eta}",
                };
                proposal.Axes.Value[DesireAxis.Aggression] = 1f;

                GroundCombatAdmissionRegistry.RecordAttack(proposal, snap, opposition, hexBonus, excluded);
                if (!GroundCombatAdmissionRegistry.TryGet(proposal, out HashSet<int> eligible)
                    || eligible.Count == 0)
                {
                    AiDebugLog.WriteDeduped(objective.Target.DiagnosticLabel,
                        $"[AI][V2][Attack][Admission] decision=SUPPRESS target={objective.Target.DiagnosticLabel} "
                        + "reason=no_ready_ground_actor_after_phaseA");
                    if (incumbent != null)
                        assaultWhy[incumbent.IntentKey] = "attack_assault_no_ready_actor_this_pass";
                    continue;
                }

                if (intermediate != null)
                    AiDebugLog.Write($"[AI][V2][Attack][Intermediate] decision=PROPOSE "
                        + $"main={objective.Target.DiagnosticLabel} base={intermediate.Target.DiagnosticLabel} "
                        + $"actor=#{actor.ArmyId} win={F(plan.ProjectedWinChance)} ap={F(ap)} "
                        + $"mainDefenders={(objective.LocationOnly ? "unknown" : "known")} "
                        + "reason=current_host_clears_observed_base_on_route");
                if (incumbent == null && secondary)
                    secondaryCandidates.Add(proposal);
                else
                {
                    (incumbent == null ? freshCandidates : proposals).Add(proposal);
                    attackProposed = true;
                }
                // The army serves an ActiveDefence objective itself: AD proposes no competing
                // ground answer to that contact (air support is a separate execution and stays).
                // Only a Hard incumbent is protected from losing its funding, so only it may
                // displace the independent answer.
                if (incumbent != null)
                    TryServeActiveDefence(snap, proposals, proposal, incumbent, local);
                if (local.IsLocalFight)
                    AiDebugLog.WriteDeduped(objective.Target.DiagnosticLabel + "#local",
                        $"[AI][V2][Attack][Local] decision={local.Kind} target={objective.Target.DiagnosticLabel} "
                        + $"actor=#{actor.ArmyId} enemy=#{local.EnemyArmyId} at ({local.Hex.Q},{local.Hex.R}) "
                        + $"cost={local.ContactCost} win={F(local.WinChance)} cover={(local.CoversAllDefenders ? 1 : 0)} "
                        + $"ad={(local.ServesActiveDefence ? 1 : 0)} reason={local.Reason}");
                AiDebugLog.WriteDeduped(objective.Target.DiagnosticLabel,
                    $"[AI][V2][Attack][Admission] decision=PROPOSE target={objective.Target.DiagnosticLabel} "
                    + $"actor={actor.ArmyId} score={F(score.Value)} eligible=[{GroundCombatAdmissionRegistry.EligibleIds(proposal)}]");
            }

            // freshCandidates were appended in objective order — the internal selection priority
            // (AttackObjectiveEvaluator.CompareSelection: nearer, then less defended), not score.
            MissionProposal bestFresh = freshCandidates.FirstOrDefault();
            if (bestFresh != null)
                proposals.Add(bestFresh);
            MissionProposal bestSecondary = secondaryCandidates.FirstOrDefault();
            if (bestSecondary != null)
            {
                proposals.Add(bestSecondary);
                AiDebugLog.WriteDeduped("secondary#" + snap.Observer?.ColorIndex,
                    $"[AI][V2][Attack][Secondary] decision=PROPOSE "
                    + $"target={((AttackMissionTarget)bestSecondary.Target).Target.DiagnosticLabel} "
                    + $"actor=#{bestSecondary.PreferredMoverArmyId} beside={liveOperation?.IntentKey} "
                    + $"score={F(bestSecondary.BaseValue)} (surplus roster, decisive start)");
            }

            TryAppendAttackPreparation(snap, objectives, activeIntents, committed, proposals, ctx,
                attackProposed, intentState);

            RecordAttackDeferrals(activeIntents, proposals, assaultWhy, deferredThisPass);
        }

        // The second operation materialized from a garrison: one proposal whose executor first moves
        // the pinned surplus bodies into their shell / new container (its own terminal step), after
        // which the intent marches as an ordinary committed secondary Assault.
        private static bool TryAppendGarrisonSortie(WorldSnapshot snap, AttackObjective objective,
            float hexBonus, ISet<int> excluded, List<MissionProposal> candidates)
        {
            GarrisonSortiePlan sortie = AttackSecondaryPolicy.PlanGarrisonSortie(snap, objective, hexBonus, excluded);
            if (sortie == null)
                return false;
            TaskScore score = AttackObjectiveEvaluator.WithResponse(objective, sortie.Detachment,
                sortie.WinChance, 1, 0f, sortie.TotalAp);
            var target = new AttackMissionTarget
            {
                Phase = AttackMissionPhase.Assault,
                Target = objective.Target,
                PrimaryArmyId = sortie.ShellArmyId,
                DestinationHex = objective.Hex,
                DefenderHexDefenseBonus = hexBonus,
                DefenderCount = objective.DefenderCount,
                ProjectedWinChance = sortie.WinChance,
                CoversAllDefenders = sortie.CoversAllDefenders,
                EstimatedEta = 1,
                Secondary = true,
                Sortie = sortie,
            };
            var proposal = new MissionProposal
            {
                Kind = MissionKind.Attack,
                Target = target,
                BaseValue = score.Value,
                Score = score,
                LocalAdmissionScore = score.Value,
                PreferredMoverArmyId = sortie.Detachment.ArmyId,
                Requirements = new MissionRequirements
                {
                    MoverKnown = true, RequiresArmy = true,
                    ApMinimum = sortie.TotalAp, ApDesired = sortie.TotalAp, ApMaximum = sortie.TotalAp,
                    EtaTurns = 1, EstimatedDistance = sortie.RouteCost,
                    CombatPowerMinimum = objective.TargetPower, CombatPowerDesired = objective.TargetPower,
                },
                Explain = $"Attack {objective.Target.DiagnosticLabel} by garrison sortie #{sortie.DonorGarrisonId} "
                    + $"({sortie.UnitRuntimeIds.Count} surplus bodies) win {F(sortie.WinChance)}",
            };
            proposal.Axes.Value[DesireAxis.Aggression] = 1f;
            candidates.Add(proposal);
            AiDebugLog.WriteDeduped(objective.Target.DiagnosticLabel + "#sortie",
                $"[AI][V2][Attack][Secondary] decision=SORTIE target={objective.Target.DiagnosticLabel} "
                + $"garrison=#{sortie.DonorGarrisonId} shell={(sortie.ShellArmyId.HasValue ? "#" + sortie.ShellArmyId : "new")} "
                + $"bodies={sortie.UnitRuntimeIds.Count} power={F(sortie.Detachment.EffectiveArmyPower)} "
                + $"win={F(sortie.WinChance)} route={sortie.RouteCost} ap={sortie.TotalAp}");
            return true;
        }

        // An independent (non-durable) ActiveDefence ground Intercept of `enemyArmyId` is removed
        // from this pass: the marching Attack army answers that contact itself, so the two lanes
        // never send two ground owners at one enemy. The Attack army's own claim is untouched.
        // The displacement is conditional on the Attack step really being fundable: a Hard
        // incumbent only (the allocator protects it), and never while this very step is cooling
        // down after a rejected attempt - then the independent answer stays and the threat is
        // not left without any handler.
        internal static bool TryServeActiveDefence(WorldSnapshot snap, List<MissionProposal> proposals,
            MissionProposal attackProposal, MissionIntent incumbent, AttackLocalAction local)
        {
            if (incumbent == null || local.Kind != AttackLocalActionKind.Intercept || !local.ServesActiveDefence
                || incumbent.Funding != CommitmentTier.Hard || attackProposal == null
                || !attackProposal.PreferredMoverArmyId.HasValue)
                return false;
            AiAllocatorState state = AiAllocatorStateRegistry.Peek(snap?.Observer);
            if (state != null && state.OnCooldown(StableMissionKey.For(attackProposal), snap.TurnNumber))
                return false;
            return WithdrawIndependentActiveDefenceIntercept(proposals, local.EnemyArmyId,
                attackProposal.PreferredMoverArmyId.Value) > 0;
        }

        private static int WithdrawIndependentActiveDefenceIntercept(List<MissionProposal> proposals,
            int enemyArmyId, int attackActorId)
        {
            int removed = proposals.RemoveAll(p => p != null && p.Kind == MissionKind.ActiveDefence
                && !p.FromDurableIntent
                && p.Target is ActiveDefenceMissionTarget t && t.Phase == ActiveDefencePhase.Intercept
                && t.EnemyArmyId == enemyArmyId && t.PrimaryArmyId != attackActorId);
            if (removed > 0)
                AiDebugLog.WriteDeduped($"atk-ad-handler-{enemyArmyId}",
                    $"[AI][V2][Attack][Local] ActiveDefence ground intercept of enemy #{enemyArmyId} "
                    + $"withdrawn ({removed}): served by marching Attack army #{attackActorId}");
            return removed;
        }

        // Missions owns the reason a live Gather / Assault intent has no executable proposal this
        // pass (Continuity must not re-run Attack eligibility to guess it). Only a deliberately
        // absent primary step is recorded; an intent that did get a proposal, or whose lane already
        // named its own deferral (Reinforcement), is left alone.
        private static void RecordAttackDeferrals(IReadOnlyList<MissionIntent> activeIntents,
            List<MissionProposal> proposals, IReadOnlyDictionary<MissionIntentKey, string> assaultWhy,
            IDictionary<MissionIntentKey, string> deferredThisPass)
        {
            if (activeIntents == null || deferredThisPass == null)
                return;
            foreach (MissionIntent intent in activeIntents.Where(i => i?.Attack != null
                && i.Status == IntentStatus.Active))
            {
                AttackMissionPhase phase = intent.Attack.Phase;
                if ((phase != AttackMissionPhase.Gather && phase != AttackMissionPhase.Assault)
                    || deferredThisPass.ContainsKey(intent.IntentKey))
                    continue;
                if (proposals.Any(p => p != null && p.Kind == MissionKind.Attack
                    && MissionIntentKey.For(p).Equals(intent.IntentKey)))
                    continue;
                string reason;
                if (phase == AttackMissionPhase.Gather)
                    reason = intent.Attack.Preparation
                        ? "attack_preparation_no_executable_step_this_pass"
                        : "attack_gather_no_executable_leg_this_pass";
                else if (!assaultWhy.TryGetValue(intent.IntentKey, out reason))
                    reason = "attack_assault_no_executable_step_this_pass";
                deferredThisPass[intent.IntentKey] = reason;
            }
        }

        // The player's one live Attack operation (a Gather — preparation included —, Assault or
        // Reinforcement), or null. Return legs of a finished fight are not an operation. The
        // surplus-only secondary operation (AttackIntent.Secondary) is never "the" operation.
        internal static MissionIntent LiveAttackOperation(IReadOnlyList<MissionIntent> activeIntents) =>
            activeIntents?.FirstOrDefault(i => IsLiveOperation(i) && !i.Attack.Secondary);

        // The live secondary operation, or null (AttackSecondaryPolicy).
        internal static MissionIntent LiveSecondaryAttack(IReadOnlyList<MissionIntent> activeIntents) =>
            activeIntents?.FirstOrDefault(i => IsLiveOperation(i) && i.Attack.Secondary);

        private static bool IsLiveOperation(MissionIntent i) =>
            i != null && i.Status == IntentStatus.Active && i.Kind == MissionKind.Attack && i.Attack != null
            && (i.Attack.Phase == AttackMissionPhase.Gather || i.Attack.Phase == AttackMissionPhase.Assault
                || i.Attack.Phase == AttackMissionPhase.Reinforcement);

        // ---- T01: the mobilization trigger and the first preparation step -----------------------
        //
        // Opens ONE new preparation when the mobilization gate is open (AttackObjectiveEvaluator
        // .MobilizationOpen: the deployed share reaches three quarters, or the field bodies can
        // already form the strike army) and no Attack operation is live. It never admits a march: the prepared
        // fist marches only through the ordinary strict >80% peak + coverage path. The objective is
        // the objective owner's best (Enumerate's TaskScore order), incl. a location-only starting
        // Citadel. The fist is assembled on the own Base nearest to the target
        // (AttackPreparationPolicy.PreparationStagingBase), where cards land in it directly.
        // Host order: an existing free field army (one already on the staging Base first; one
        // elsewhere first walks there — MoveHost) > an empty reusable shell on the staging Base >
        // a container created there (CreateArmyWithMember when a legal same-hex first member
        // exists, else one empty shell while hand/deck can still fill it).
        // The first executed step creates the durable Gather intent (§70); funding stays the one
        // allocator's decision — nothing is spent or claimed here.

        // The support wing's sortie leg (AttackMissionPhase.AirSupport): the wing Continuity bound
        // flies to the site, strikes, and lands. Lifecycle work of a Hard operation, so its
        // intrinsic score stays neutral; requirements are the one air-support leg shape.
        private static void AppendAttackAirSupport(WorldSnapshot snap, MissionIntent intent,
            AttackIntent a, List<MissionProposal> proposals)
        {
            if (!a.AirSupportArmyId.HasValue || !a.AirSupportLandingHex.HasValue)
                return;
            // A formed wing or a stored launch from an own airfield (GroundCombatAirSupport).
            ArmySnapshot wing = GroundCombatAirSupport.SupportSource(snap, a.AirSupportArmyId.Value);
            if (wing == null
                || GroundCombatAirSupport.HoldingThisTurn(snap.Observer, wing.ArmyId, snap.TurnNumber)
                || !GroundCombatAirSupport.SourceLeg(snap, wing, a.Target.Hex, AirStrikePolicy.Standard,
                    out int eta, out MissionRequirements requirements))
                return;
            var target = new AttackMissionTarget
            {
                Phase = AttackMissionPhase.AirSupport,
                Target = a.Target,
                AirSupportArmyId = wing.ArmyId,
                AirSupportLandingHex = a.AirSupportLandingHex,
                DestinationHex = a.Target.Hex,
                DefenderCount = KnownOpposition.DefendersOnHex(snap, a.Target.Hex).Count,
                EstimatedEta = eta,
                OpportunisticStrikeTurn = a.LastOpportunisticStrikeTurn,
            };
            var proposal = new MissionProposal
            {
                Kind = MissionKind.Attack,
                Target = target,
                BaseValue = 0f,
                Score = default(TaskScore),
                LocalAdmissionScore = 0f,
                PreferredMoverArmyId = wing.ArmyId,
                FromDurableIntent = true,
                DurableFundingTier = intent.Funding,
                Requirements = requirements,
                Explain = $"Attack {a.Target.DiagnosticLabel} AirSupport wing #{wing.ArmyId} "
                    + $"-> strike, land ({a.AirSupportLandingHex.Value.Q},{a.AirSupportLandingHex.Value.R})",
            };
            proposal.Axes.Value[DesireAxis.Aggression] = 1f;
            proposals.Add(proposal);
        }

        // §24/§47 — a walking-home leg (RecoveryReturn / SupportReturn). Lifecycle work, not fresh
        // strategic target scoring: its execution priority comes from the durable commitment, so the
        // intrinsic score stays neutral and cannot out-rank unrelated lanes.
        private static void AppendAttackWalkHome(WorldSnapshot snap, MissionIntent intent,
            AttackIntent a, AttackMissionPhase phase, int? moverArmyId, HexCoord? destination,
            List<MissionProposal> proposals)
        {
            if (!moverArmyId.HasValue || !destination.HasValue)
                return;
            ArmySnapshot actor = snap.Self.Armies?.FirstOrDefault(x => x != null
                && x.ArmyId == moverArmyId.Value);
            if (actor == null)
                return;

            MissionRequirements requirements = GroundCombatLegs.PinnedLegRequirements(
                actor, destination.Value, out int eta);
            var target = new AttackMissionTarget
            {
                Phase = phase,
                Target = a.Target,
                // A donor walking home is no part of the operation's force: it never names the
                // primary (which would pin it) and moves as its own support actor.
                PrimaryArmyId = phase == AttackMissionPhase.GatherReturn ? null : a.PrimaryArmyId,
                SupportArmyId = phase == AttackMissionPhase.GatherReturn ? moverArmyId : a.SupportArmyId,
                DestinationHex = destination.Value,
                RecoveryBaseHex = a.RecoveryBaseHex,
                SupportReturnHex = a.SupportReturnHex,
                EstimatedEta = eta,
                OpportunisticStrikeTurn = a.LastOpportunisticStrikeTurn,
            };
            var proposal = new MissionProposal
            {
                Kind = MissionKind.Attack,
                Target = target,
                BaseValue = 0f,
                Score = default(TaskScore),
                LocalAdmissionScore = 0f,
                PreferredMoverArmyId = actor.ArmyId,
                FromDurableIntent = true,
                DurableFundingTier = intent.Funding,
                Requirements = requirements,
                Explain = $"Attack {phase} actor #{actor.ArmyId} -> "
                    + $"({destination.Value.Q},{destination.Value.R})",
            };
            proposal.Axes.Value[DesireAxis.Aggression] = 1f;
            proposals.Add(proposal);
        }

        // §46 — reinforcement uses the shared ground-combat reinforcement mechanics. When a support
        // army is already bound, this is the convoy/handoff leg. When none is bound the planner
        // proposes nothing and holds: asking for a NEW capability is the Demand layer's decision,
        // never the mission planner's (exactly the rule the Raid lane already follows).
        private static void AppendAttackReinforcement(WorldSnapshot snap, MissionIntent intent,
            AttackIntent a, ISet<int> committed, List<MissionProposal> proposals, AiTurnContext ctx,
            IDictionary<MissionIntentKey, string> deferredThisPass)
        {
            if (!a.PrimaryArmyId.HasValue)
                return;
            ArmySnapshot primary = snap.Self.Armies?.FirstOrDefault(x => x != null
                && x.ArmyId == a.PrimaryArmyId.Value);
            if (primary == null)
                return;

            if (!a.SupportArmyId.HasValue)
            {
                // 2026-10-04 — a committed Assault's support is bound by Continuity alone, together
                // with its rendezvous (ResolveCommittedAssault); a support released mid-pass is
                // re-decided there on the next reconciliation, never by the batch solve.
                if (a.AssaultStarted)
                    return;
                // §46 — an EXISTING free army is an actor-contention decision, not a capability
                // request: it belongs in the SAME batch solve the assault legs run through, exactly
                // as the Raid lane's unpinned reinforcement leg already does. Without this leg the
                // operation sat in Reinforcement forever — the demand layer correctly answered
                // "an existing free army can solve this, materialise nothing", and nothing ever
                // proposed the join. Only when no free army exists at all does the planner hold and
                // let Aggression demand ask Production for one.
                AppendAttackUnpinnedReinforcement(snap, intent, a, primary, committed, proposals, ctx,
                    deferredThisPass);
                return;
            }

            ArmySnapshot support = snap.Self.Armies?.FirstOrDefault(x => x != null
                && x.ArmyId == a.SupportArmyId.Value);
            if (support == null)
                return;

            // 2026-10-04 — a committed Assault's Reinforcement meets on the primary's route: the
            // primary keeps walking toward the target up to the rendezvous while the support walks
            // there too; the handoff happens once both stand on it.
            HexCoord? meet = a.RendezvousHex;
            bool primaryArrived = !meet.HasValue || primary.Hex.Equals(meet.Value);
            if (!primaryArrived)
                AppendAttackPrimaryToRendezvous(intent, a, primary, meet.Value, proposals);
            // A support already waiting on the rendezvous has nothing to do until the primary
            // arrives.
            if (!primaryArrived && support.Hex.Equals(meet.Value))
                return;
            HexCoord supportDestination = meet ?? primary.Hex;

            MissionRequirements requirements = GroundCombatLegs.PinnedLegRequirements(
                support, supportDestination, out int eta);
            var target = new AttackMissionTarget
            {
                Phase = AttackMissionPhase.Reinforcement,
                Target = a.Target,
                PrimaryArmyId = a.PrimaryArmyId,
                SupportArmyId = a.SupportArmyId,
                DestinationHex = supportDestination,
                RendezvousHex = meet,
                EstimatedEta = eta,
                OpportunisticStrikeTurn = a.LastOpportunisticStrikeTurn,
            };
            var proposal = new MissionProposal
            {
                Kind = MissionKind.Attack,
                Target = target,
                BaseValue = 0f,
                Score = default(TaskScore),
                LocalAdmissionScore = 0f,
                PreferredMoverArmyId = support.ArmyId,
                FromDurableIntent = true,
                DurableFundingTier = intent.Funding,
                Requirements = requirements,
                Explain = $"Attack Reinforcement support #{support.ArmyId} -> primary "
                    + $"#{a.PrimaryArmyId} at ({supportDestination.Q},{supportDestination.R})",
            };
            proposal.Axes.Value[DesireAxis.Aggression] = 1f;
            proposals.Add(proposal);
        }

        // The primary's half of a committed Reinforcement: one more step along its route toward
        // the target, ending on the rendezvous Continuity chose (never behind the primary). A
        // lifecycle leg of the Hard operation, so its intrinsic score stays neutral.
        private static void AppendAttackPrimaryToRendezvous(MissionIntent intent, AttackIntent a,
            ArmySnapshot primary, HexCoord rendezvous, List<MissionProposal> proposals)
        {
            MissionRequirements requirements = GroundCombatLegs.PinnedLegRequirements(
                primary, rendezvous, out int eta);
            var target = new AttackMissionTarget
            {
                Phase = AttackMissionPhase.Reinforcement,
                PrimaryRendezvousLeg = true,
                Target = a.Target,
                PrimaryArmyId = a.PrimaryArmyId,
                SupportArmyId = a.SupportArmyId,
                DestinationHex = rendezvous,
                RendezvousHex = rendezvous,
                EstimatedEta = eta,
                OpportunisticStrikeTurn = a.LastOpportunisticStrikeTurn,
            };
            var proposal = new MissionProposal
            {
                Kind = MissionKind.Attack,
                Target = target,
                BaseValue = 0f,
                Score = default(TaskScore),
                LocalAdmissionScore = 0f,
                PreferredMoverArmyId = primary.ArmyId,
                FromDurableIntent = true,
                DurableFundingTier = intent.Funding,
                Requirements = requirements,
                Explain = $"Attack Reinforcement primary #{primary.ArmyId} -> rendezvous "
                    + $"({rendezvous.Q},{rendezvous.R}) on its route to {a.Target.DiagnosticLabel}",
            };
            proposal.Axes.Value[DesireAxis.Aggression] = 1f;
            proposals.Add(proposal);
        }

        // §46 — the UNPINNED reinforcement leg: "some existing free army should join this primary",
        // with the actor left to the one batch solve (PrepareGroundCombatAssignments) exactly as the
        // Raid lane leaves it. Nothing is picked here; the eligible set is published through the one
        // admission registry, and the AP envelope is priced off the candidate that same solve
        // prefers first (cheapest activation, then weakest, then lowest id) so funding matches the
        // actor it is most likely to bind.
        private static void AppendAttackUnpinnedReinforcement(WorldSnapshot snap,
            MissionIntent intent, AttackIntent a, ArmySnapshot primary, ISet<int> committed,
            List<MissionProposal> proposals, AiTurnContext ctx,
            IDictionary<MissionIntentKey, string> deferredThisPass)
        {
            IReadOnlyList<WorthIt.DefendingArmy> opposition =
                KnownOpposition.OnHex(snap, a.Target.Hex);
            float hexBonus = AiMapMemory.KnownHexDefenseBonus(snap?.Observer, ctx?.Map, a.Target.Hex);
            List<int> candidates = GroundCombatAssemblyPlanner.ReinforcementSupportCandidates(
                snap, a.PrimaryArmyId.Value, opposition, committed, hexBonus,
                allowCommandHandover: true);
            if (candidates.Count == 0)
            {
                if (deferredThisPass != null)
                    deferredThisPass[intent.IntentKey] = "attack_reinforcement_waiting_for_new_power";
                AiDebugLog.WriteDeduped(intent.IntentKey.ToString(),
                    $"[AI][V2][Attack] decision=HOLD {intent.IntentKey}: primary #{a.PrimaryArmyId} "
                    + "waits; no existing free army improves the assault (Aggression demand owns "
                    + "the request for a new one)");
                return;
            }

            ArmySnapshot priced = snap.Self.Armies?
                .Where(x => x != null && candidates.Contains(x.ArmyId))
                .OrderBy(x => x.HasActivatedThisTurn ? 0 : x.ActivationApCost)
                .ThenBy(x => x.EffectiveArmyPower)
                .ThenBy(x => x.ArmyId)
                .FirstOrDefault();
            float ap = priced != null && !priced.HasActivatedThisTurn ? priced.ActivationApCost : 0f;
            int distance = priced == null ? 0 : AiV2Util.TravelCost(snap, priced, primary.Hex);
            if (distance == int.MaxValue) return;
            int eta = priced == null ? 1
                : AiV2Util.CeilDiv(distance, Mathf.Max(1, priced.MaxMovement));

            var target = new AttackMissionTarget
            {
                Phase = AttackMissionPhase.Reinforcement,
                Target = a.Target,
                PrimaryArmyId = a.PrimaryArmyId,
                SupportArmyId = null,
                DestinationHex = primary.Hex,
                DefenderHexDefenseBonus = hexBonus,
                DefenderCount = WorthIt.UnitsOf(opposition).Count,
                EstimatedEta = eta,
                OpportunisticStrikeTurn = a.LastOpportunisticStrikeTurn,
            };
            var proposal = new MissionProposal
            {
                Kind = MissionKind.Attack,
                Target = target,
                BaseValue = 0f,
                Score = default(TaskScore),
                LocalAdmissionScore = 0f,
                PreferredMoverArmyId = priced?.ArmyId,
                FromDurableIntent = true,
                DurableFundingTier = intent.Funding,
                Requirements = new MissionRequirements
                {
                    MoverKnown = priced != null, RequiresArmy = true,
                    ApMinimum = ap, ApDesired = ap, ApMaximum = ap,
                    EtaTurns = Mathf.Max(1, eta), EstimatedDistance = distance,
                },
                Explain = $"Attack {a.Target.DiagnosticLabel} Reinforcement: select an existing free "
                    + $"support for primary #{a.PrimaryArmyId} at ({primary.Hex.Q},{primary.Hex.R}); "
                    + $"{candidates.Count} candidate(s); Hard funding protection is allocator-owned",
            };
            proposal.Axes.Value[DesireAxis.Aggression] = 1f;
            GroundCombatAdmissionRegistry.RecordReinforcement(proposal, snap, committed);
            proposals.Add(proposal);
            AiDebugLog.WriteDeduped(intent.IntentKey.ToString(),
                $"[AI][V2][Attack][Admission] decision=REINFORCE-SELECT {intent.IntentKey} "
                + $"primary={a.PrimaryArmyId} candidates={candidates.Count} "
                + $"eligible=[{GroundCombatAdmissionRegistry.EligibleIds(proposal)}]");
        }
    }
}



