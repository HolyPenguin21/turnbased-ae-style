using System.Collections.Generic;
using System.Linq;
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
            IDictionary<MissionIntentKey, string> deferredThisPass)
        {
            if (snap?.Self == null || snap.Self.AttackPeak <= 0f)
                return;

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
                        AppendAttackGather(snap, intent, a, committed, proposals, ctx);
                    // Strike force step 5 — donors that already handed over walk home beside
                    // whatever the operation itself does.
                    foreach (AttackGatherReturn r in a.GatherReturns)
                        AppendAttackWalkHome(snap, intent, a, AttackMissionPhase.GatherReturn,
                            r.ArmyId, r.BaseHex, proposals);
                    // The bound support wing flies its sortie beside the operation too.
                    AppendAttackAirSupport(snap, intent, a, proposals);
                }

            // ---- Assault: fresh objectives and incumbents still marching on their target ------
            List<AttackObjective> objectives = AttackObjectiveEvaluator.Enumerate(snap);
            bool attackProposed = false;
            // 2026-09-30 (user decision) — at most ONE live Attack operation per player: while one
            // is live no fresh objective is offered, and of several fresh candidates only the best
            // reaches the allocator (it could otherwise fund two operations in one pass).
            MissionIntent liveOperation = LiveAttackOperation(activeIntents);
            var freshCandidates = new List<MissionProposal>();
            // Why an incumbent Assault got no proposal this pass (the planner knows it right where
            // it declines); published below as the planner deferral Continuity reads.
            var assaultWhy = new Dictionary<MissionIntentKey, string>();
            foreach (AttackObjective objective in objectives)
            {
                MissionIntent incumbent = activeIntents?.FirstOrDefault(i => i != null
                    && i.Status == IntentStatus.Active && i.Kind == MissionKind.Attack
                    && i.Attack != null && i.Attack.Target.Equals(objective.Target));
                // An incumbent already past the Assault leg is being proposed above; do not also
                // offer it a fresh assault against the same target this pass.
                if (incumbent != null && incumbent.Attack.Phase != AttackMissionPhase.Assault)
                    continue;
                if (incumbent == null && !AttackForceReadiness.MobilizationOpen(snap.Self))
                    continue;
                if (incumbent == null && liveOperation != null)
                {
                    AiDebugLog.WriteDeduped(objective.Target.DiagnosticLabel,
                        $"[AI][V2][Attack][Assembly] decision=HOLD target={objective.Target.DiagnosticLabel} "
                        + $"blocker=operation_live:{liveOperation.IntentKey}");
                    continue;
                }
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
                var excluded = committed == null ? new HashSet<int>() : new HashSet<int>(committed);
                if (pinnedActor.HasValue)
                    excluded.Remove(pinnedActor.Value);

                AttackObjective intermediate = AttackIntermediateBasePolicy.Select(snap, incumbent?.Attack, objectives);
                AttackObjective assaultObjective = intermediate ?? objective;
                IReadOnlyList<WorthIt.DefendingArmy> opposition = assaultObjective.Opposition;
                // §30 — the honest, knowledge-scoped answer to "what defence does a defender on
                // that hex actually get". Never a live BuildingRegistry read.
                float hexBonus = AttackObjectiveEvaluator.KnownSiteDefenceBonus(
                    snap, ctx?.Map, assaultObjective.Hex);

                GroundCombatAssemblyPlan plan = GroundCombatAssemblyPlanner.Plan(snap,
                    new GroundCombatAssemblyRequest
                    {
                        Opposition = opposition,
                        WinChanceGate = intermediate != null
                            ? GroundCombatAdmissionPolicy.FreshStartWinChanceGate
                            : GroundCombatAdmissionPolicy.AttackCoverageGate,
                        AllowSameHexAssembly = intermediate == null,
                        MinimumArmyPower = incumbent?.Attack?.AssaultStarted == true
                            ? 0f : AttackForceReadiness.RequiredPower(snap.Self.AttackPeak),
                        PreferredPrimaryArmyId = pinnedActor,
                        PinToPreferred = pinnedActor.HasValue,
                        ExcludedArmyIds = excluded,
                        DefenderHexDefenseBonus = hexBonus,
                    });

                if (!plan.Feasible)
                {
                    // Audit F7 — a FRESH objective no single army nor same-hex package can take
                    // may still be formed by free armies spread over several hexes: gather them.
                    if (incumbent == null && TryAppendFreshAttackGather(snap, objective, opposition,
                            hexBonus, excluded, freshCandidates))
                    {
                        attackProposed = true;
                        continue;
                    }
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

                // Price and time the force this plan will ACTUALLY field, through the same
                // projections Raid and ActiveDefence use — never a host-only figure.
                int projectedMove = GroundCombatAssemblyPlanner.ProjectedMaxMovement(snap, plan)
                    ?? actor.MaxMovement;
                int distance = intermediate != null
                    ? AttackIntermediateBasePolicy.Route(snap, actor, actor.Hex, assaultObjective.Hex)?.TotalCost ?? int.MaxValue
                    : AiV2Util.TravelCost(snap, actor, assaultObjective.Hex, maxMovement: projectedMove);
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
                    plan.ProjectedWinChance, eta, 0f, projectedAp);

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
                    ProjectedWinChance = plan.ProjectedWinChance,
                    CoversAllDefenders = plan.CoversAllDefenders,
                    ForceCommitted = incumbent?.Attack?.AssaultStarted == true,
                    EstimatedEta = eta,
                    // §17 — carry the operation's own once-per-turn side-strike marker into the leg
                    // the executor will run. A fresh objective has no incumbent and therefore no
                    // marker, which is exactly right: it has taken no strike yet.
                    OpportunisticStrikeTurn =
                        incumbent?.Attack?.LastOpportunisticStrikeTurn ?? 0,
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
                        + $"task {F(score.Value)} win {F(plan.ProjectedWinChance)} "
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
                (incumbent == null ? freshCandidates : proposals).Add(proposal);
                attackProposed = true;
                AiDebugLog.WriteDeduped(objective.Target.DiagnosticLabel,
                    $"[AI][V2][Attack][Admission] decision=PROPOSE target={objective.Target.DiagnosticLabel} "
                    + $"actor={actor.ArmyId} score={F(score.Value)} eligible=[{GroundCombatAdmissionRegistry.EligibleIds(proposal)}]");
            }

            // freshCandidates were appended in objective order — the internal selection priority
            // (AttackObjectiveEvaluator.CompareSelection: nearer, then less defended), not score.
            MissionProposal bestFresh = freshCandidates.FirstOrDefault();
            if (bestFresh != null)
                proposals.Add(bestFresh);

            TryAppendAttackPreparation(snap, objectives, activeIntents, committed, proposals, ctx,
                attackProposed);

            RecordAttackDeferrals(activeIntents, proposals, assaultWhy, deferredThisPass);
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
        // Reinforcement), or null. Return legs of a finished fight are not an operation.
        internal static MissionIntent LiveAttackOperation(IReadOnlyList<MissionIntent> activeIntents) =>
            activeIntents?.FirstOrDefault(i => i != null
                && i.Status == IntentStatus.Active && i.Kind == MissionKind.Attack && i.Attack != null
                && (i.Attack.Phase == AttackMissionPhase.Gather || i.Attack.Phase == AttackMissionPhase.Assault
                    || i.Attack.Phase == AttackMissionPhase.Reinforcement));

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
            ArmySnapshot wing = snap.Self.Armies?.FirstOrDefault(x => x != null
                && x.ArmyId == a.AirSupportArmyId.Value && x.IsAir && !x.IsAirfield);
            if (wing == null
                || GroundCombatAirSupport.HoldingThisTurn(snap.Observer, wing.ArmyId, snap.TurnNumber))
                return;
            int eta = GroundCombatAirSupport.SortieEta(wing, a.Target.Hex);
            var target = new AttackMissionTarget
            {
                Phase = AttackMissionPhase.AirSupport,
                Target = a.Target,
                AirSupportArmyId = wing.ArmyId,
                AirSupportLandingHex = a.AirSupportLandingHex,
                DestinationHex = a.Target.Hex,
                DefenderCount = AttackObjectiveEvaluator.KnownSiteDefenders(snap, a.Target.Hex).Count,
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
                Requirements = GroundCombatAirSupport.LegRequirements(wing, a.Target.Hex, eta),
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
                AttackObjectiveEvaluator.KnownSiteOpposition(snap, a.Target.Hex);
            float hexBonus = AttackObjectiveEvaluator.KnownSiteDefenceBonus(snap, ctx?.Map, a.Target.Hex);
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


