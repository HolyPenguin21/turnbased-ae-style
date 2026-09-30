using System.Collections.Generic;
using System.Linq;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
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
            if (snap?.Self == null || snap.Self.TotalMilitaryPotential <= 0f)
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
            foreach (AttackObjective objective in objectives)
            {
                MissionIntent incumbent = activeIntents?.FirstOrDefault(i => i != null
                    && i.Status == IntentStatus.Active && i.Kind == MissionKind.Attack
                    && i.Attack != null && i.Attack.Target.Equals(objective.Target));
                // An incumbent already past the Assault leg is being proposed above; do not also
                // offer it a fresh assault against the same target this pass.
                if (incumbent != null && incumbent.Attack.Phase != AttackMissionPhase.Assault)
                    continue;
                // T01 — sanctioned coordinates are not an observed defender package: a march on a
                // never-observed site is not proposed (Recon observes it; mobilization may prepare).
                if (objective.LocationOnly)
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

                IReadOnlyList<WorthIt.DefendingArmy> opposition = objective.Opposition;
                // §30 — the honest, knowledge-scoped answer to "what defence does a defender on
                // that hex actually get". Never a live BuildingRegistry read.
                float hexBonus = AttackObjectiveEvaluator.KnownSiteDefenceBonus(
                    snap, ctx?.Map, objective.Hex);

                GroundCombatAssemblyPlan plan = GroundCombatAssemblyPlanner.Plan(snap,
                    new GroundCombatAssemblyRequest
                    {
                        Opposition = opposition,
                        WinChanceGate = GroundCombatAdmissionPolicy.AttackCoverageGate,
                        MinimumArmyPower = incumbent?.Attack?.AssaultStarted == true
                            ? 0f : 0.80f * snap.Self.TotalMilitaryPotential,
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
                            hexBonus, excluded, proposals))
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
                    continue;
                }

                ArmySnapshot actor = snap.Self.Armies?.FirstOrDefault(x => x != null
                    && x.ArmyId == plan.BaseArmyId);
                if (actor == null)
                    continue;

                int distance = HexGridMath.Distance(actor.Hex, objective.Hex);
                // Price and time the force this plan will ACTUALLY field, through the same
                // projections Raid and ActiveDefence use — never a host-only figure.
                int projectedMove = GroundCombatAssemblyPlanner.ProjectedMaxMovement(snap, plan)
                    ?? actor.MaxMovement;
                int eta = AiV2Util.CeilDiv(distance,
                    Mathf.Max(AiConfigV2.etaFallbackMoveBudget, projectedMove));
                int? projectedAp = GroundCombatAssemblyPlanner.ProjectedActivationApCost(snap, plan);
                TaskScore score = AttackObjectiveEvaluator.WithResponse(objective, actor,
                    plan.ProjectedWinChance, eta, 0f, projectedAp);

                // Once the assembled actor strictly clears the force threshold, it may march.

                var target = new AttackMissionTarget
                {
                    Phase = AttackMissionPhase.Assault,
                    Target = objective.Target,
                    PrimaryArmyId = actor.ArmyId,
                    DestinationHex = objective.Hex,
                    DefenderHexDefenseBonus = hexBonus,
                    DefenderCount = objective.DefenderCount,
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
                        CombatPowerMinimum = objective.TargetPower,
                        CombatPowerDesired = objective.TargetPower,
                    },
                    Explain = $"Attack {objective.Target.DiagnosticLabel} "
                        + $"task {F(score.Value)} win {F(plan.ProjectedWinChance)} "
                        + $"defenders {objective.DefenderCount} hexDef {F(hexBonus)} eta {eta}",
                };
                proposal.Axes.Value[DesireAxis.Aggression] = 1f;

                GroundCombatAdmissionRegistry.RecordAttack(proposal, snap, opposition, hexBonus, excluded);
                if (!GroundCombatAdmissionRegistry.TryGet(proposal, out HashSet<int> eligible)
                    || eligible.Count == 0)
                {
                    AiDebugLog.WriteDeduped(objective.Target.DiagnosticLabel,
                        $"[AI][V2][Attack][Admission] decision=SUPPRESS target={objective.Target.DiagnosticLabel} "
                        + "reason=no_ready_ground_actor_after_phaseA");
                    continue;
                }

                proposals.Add(proposal);
                attackProposed = true;
                AiDebugLog.WriteDeduped(objective.Target.DiagnosticLabel,
                    $"[AI][V2][Attack][Admission] decision=PROPOSE target={objective.Target.DiagnosticLabel} "
                    + $"actor={actor.ArmyId} score={F(score.Value)} eligible=[{GroundCombatAdmissionRegistry.EligibleIds(proposal)}]");
            }

            TryAppendAttackPreparation(snap, objectives, activeIntents, committed, proposals, ctx,
                attackProposed);
        }

        // ---- T01: the mobilization trigger and the first preparation step -----------------------
        //
        // Opens ONE new preparation when the additive share of the force already on the map reaches
        // four fifths (AttackObjectiveEvaluator.MobilizationOpen over SelfSnapshot.DeployedPower /
        // AvailablePower) and no Attack operation is live. It never admits a march: the prepared
        // fist marches only through the ordinary strict >80% peak + coverage path. The objective is
        // the objective owner's best (Enumerate's TaskScore order), incl. a location-only starting
        // Citadel. The fist is assembled on the own Base nearest to the target
        // (AttackObjectiveEvaluator.PreparationStagingBase), where cards land in it directly.
        // Host order: an existing free field army (one already on the staging Base first; one
        // elsewhere first walks there — MoveHost) > an empty reusable shell on the staging Base >
        // a container created there (CreateArmyWithMember when a legal same-hex first member
        // exists, else one empty shell while hand/deck can still fill it).
        // The first executed step creates the durable Gather intent (§70); funding stays the one
        // allocator's decision — nothing is spent or claimed here.
        private static void TryAppendAttackPreparation(WorldSnapshot snap,
            List<AttackObjective> objectives, IReadOnlyList<MissionIntent> activeIntents,
            ISet<int> committed, List<MissionProposal> proposals, AiTurnContext ctx,
            bool attackProposed)
        {
            SelfSnapshot self = snap.Self;
            bool open = AttackObjectiveEvaluator.MobilizationOpen(self.DeployedPower, self.AvailablePower);
            string share = $"deployed={F(self.DeployedPower)} available={F(self.AvailablePower)} "
                + $"share={(self.AvailablePower > 0f ? 100f * self.DeployedPower / self.AvailablePower : 0f):0.00}% "
                + $"gate>=80% open={(open ? 1 : 0)}";
            MissionIntent live = activeIntents?.FirstOrDefault(i => i != null
                && i.Status == IntentStatus.Active && i.Kind == MissionKind.Attack && i.Attack != null
                && (i.Attack.Phase == AttackMissionPhase.Gather || i.Attack.Phase == AttackMissionPhase.Assault
                    || i.Attack.Phase == AttackMissionPhase.Reinforcement));
            string skip = !open ? "trigger_closed"
                : live != null ? $"operation_live:{live.IntentKey}"
                : attackProposed ? "direct_assault_or_gather_proposed"
                : objectives.Count == 0 ? "no_attack_objective"
                : !AttackObjectiveEvaluator.PreparationStagingBase(snap, objectives[0].Hex).HasValue
                    ? "no_own_base" : null;
            string logKey = $"mobilization#{snap.Observer?.ColorIndex}";
            if (skip != null)
            {
                AiDebugLog.WriteDeduped(logKey,
                    $"[AI][V2][Attack][Mobilization] decision=NONE {share} reason={skip}");
                return;
            }

            AttackObjective objective = objectives[0];
            HexCoord citadel = AttackObjectiveEvaluator.PreparationStagingBase(snap, objective.Hex).Value;
            float required = 0.80f * self.TotalMilitaryPotential;
            float hexBonus = AttackObjectiveEvaluator.KnownSiteDefenceBonus(snap, ctx?.Map, objective.Hex);
            IReadOnlyList<WorthIt.DefendingArmy> opposition = objective.Opposition;
            var excluded = committed == null ? new HashSet<int>() : new HashSet<int>(committed);
            string head = $"[AI][V2][Attack][Mobilization] target={objective.Target.DiagnosticLabel} "
                + $"knowledge={(objective.LocationOnly ? "starting-location-only" : "observed")} {share} "
                + $"ideal={F(self.TotalMilitaryPotential)} required>{F(required)} "
                + $"staging=({citadel.Q},{citadel.R})";

            // 1) an existing free field army hosts the fist wherever it stands.
            ArmySnapshot fieldHost = GroundCombatActorEligibility.EligibleArmies(snap, excluded,
                    requireMovementNow: false)
                .OrderByDescending(a => a.EffectiveArmyPower)
                .ThenByDescending(a => a.Hex.Equals(citadel))
                .ThenBy(a => a.ArmyId)
                .FirstOrDefault();
            PlayerSetupData player = snap.Observer;
            HexCoord? hostStaging = fieldHost == null ? (HexCoord?)null
                : AttackObjectiveEvaluator.PreparationStagingBase(snap, objective.Hex, fieldHost);
            if (fieldHost != null && !hostStaging.HasValue)
            {
                AiDebugLog.WriteDeduped(logKey,
                    $"{head} decision=HOLD host=#{fieldHost.ArmyId} reason=no_own_base_reachable_by_host");
                return;
            }
            if (fieldHost != null)
                citadel = hostStaging.Value;
            if (fieldHost != null && !fieldHost.Hex.Equals(citadel))
            {
                // The host walks to the staging Base first; supports gather there afterwards.
                if (fieldHost.CurrentMovement <= 0)
                {
                    AiDebugLog.WriteDeduped(logKey,
                        $"{head} decision=HOLD host=#{fieldHost.ArmyId} reason=host_has_no_movement_to_reach_staging");
                    return;
                }
                AppendPreparationStep(snap, objective, fieldHost.ArmyId, citadel,
                    AttackPreparationStep.MoveHost,
                    fieldHost.HasActivatedThisTurn ? 0f : fieldHost.ActivationApCost, null, hexBonus,
                    proposals, head);
                return;
            }
            if (fieldHost != null)
            {
                Dictionary<int, float> donorValues = GroundCombatDonorPolicy.BorrowableDonorValues(
                    player == null ? null : MissionIntentRegistry.GetOrCreate(player).All);
                GroundCombatGatherPlan gather = GroundCombatAssemblyPlanner.PlanGather(snap, opposition,
                    hexBonus, objective.Hex, excluded, GroundCombatAdmissionPolicy.AttackCoverageGate,
                    fieldHost.ArmyId, donorValues, minimumArmyPower: required, allowPartial: true);
                // The lead leg is a FREE army (a bought donor stays with its operation until the
                // intent this step creates retires it), exactly as the fresh gather's lead.
                ArmySnapshot lead = gather.Feasible
                    ? gather.SupportArmyIds
                        .Where(id => !donorValues.ContainsKey(id))
                        .Select(id => self.Armies?.FirstOrDefault(x => x != null && x.ArmyId == id))
                        .FirstOrDefault(s => s != null && (s.Hex.Equals(fieldHost.Hex) || s.CurrentMovement > 0))
                    : null;
                if (lead != null)
                {
                    int eta = Mathf.Max(1, gather.TotalEta);
                    TaskScore score = TaskScoreEvaluator.WithResponse(objective.TaskScore,
                        PreparationWin(objective, gather.ProjectedWinChance), gather.CurrentTurnAp,
                        AiV2Util.CeilDiv(gather.FutureAp, eta), eta,
                        moverOpportunityCost: gather.DisplacedValue);
                    MissionProposal proposal = BuildAttackGatherLeg(objective.Target, fieldHost, lead,
                        gather.SupportArmyIds, hexBonus, objective.DefenderCount,
                        gather.ProjectedWinChance, gather.CoversAllDefenders, 0, score, null);
                    MarkPreparation(proposal, AttackPreparationStep.None);
                    proposals.Add(proposal);
                    AiDebugLog.WriteDeduped(logKey,
                        $"{head} decision=PROPOSE step=support_gather host=#{fieldHost.ArmyId} "
                        + $"fist={F(fieldHost.EffectiveArmyPower)} lead=#{lead.ArmyId} "
                        + $"supports=[{string.Join(",", gather.SupportArmyIds)}] projected={F(gather.ProjectedPower)} "
                        + $"reachesThreshold={gather.ReachesThreshold} ap={gather.TotalAp} score={F(score.Value)}");
                    return;
                }
                ArmyData liveField = player == null ? null : AiV2Util.ResolveArmy(player, fieldHost.ArmyId);
                GroundCombatAssemblyPlan fieldStep = GroundCombatAssemblyPlanner.PlanPreparationAssembly(
                    snap, liveField, excluded, opposition, hexBonus);
                if (fieldStep.Feasible)
                {
                    AppendPreparationStep(snap, objective, fieldHost.ArmyId, fieldHost.Hex,
                        AttackPreparationStep.Assemble, 0f, fieldStep, hexBonus, proposals, head);
                    return;
                }
                AiDebugLog.WriteDeduped(logKey,
                    $"{head} decision=HOLD host=#{fieldHost.ArmyId} fist={F(fieldHost.EffectiveArmyPower)} "
                    + $"reason=no_support_or_same_hex_body_raises_host gather={gather.Reason ?? "no_movable_lead"} "
                    + "(a card for this fist is the unbound Attack demand's request)");
                return;
            }

            // 2) an empty reusable shell already standing on the own starting Citadel.
            ArmyData shell = player == null ? null : ReusableArmySelector.FindReusableAt(player, citadel, null);
            if (shell != null && excluded.Contains(shell.Id))
                shell = null;
            // 3) otherwise a container created on the Citadel, seeded when a legal member is there.
            ArmyData host = shell;
            if (host == null && player != null)
            {
                host = ArmyData.CreateVisualSnapshot();
                host.Hex = citadel;
                host.Owner = player;
                host.IsGarrison = false;
            }
            GroundCombatAssemblyPlan seed = GroundCombatAssemblyPlanner.PlanPreparationAssembly(
                snap, host, excluded, opposition, hexBonus);
            // ATK-F02 — a container is created or reused only for a concrete source: a same-hex
            // member or a card that would strengthen it (never a bare positive Reserve).
            string cardSource = seed.Feasible || host == null ? null
                : AggressionDemandEvaluator.PreparationHostCardSource(snap, host);
            if (!seed.Feasible && cardSource == null)
            {
                AiDebugLog.WriteDeduped(logKey,
                    $"{head} decision=HOLD blocker=no_legal_source reason=no_same_hex_member_and_no_card_strengthens_host "
                    + $"({seed.Reason})");
                return;
            }
            if (shell != null)
            {
                AppendPreparationStep(snap, objective, shell.Id, citadel,
                    seed.Feasible ? AttackPreparationStep.Assemble : AttackPreparationStep.CreateHost,
                    0f, seed.Feasible ? seed : null, hexBonus, proposals, head);
                return;
            }
            AppendPreparationStep(snap, objective, null, citadel, AttackPreparationStep.CreateHost,
                ArmyActions.CreateArmyApCost, seed.Feasible ? seed : null, hexBonus, proposals, head);
        }

        // Location-only knowledge is no evidence that the fight is winnable: its WinChance slot
        // stays empty (never "observed empty" = 1). An observed site uses the estimator's answer.
        private static float PreparationWin(AttackObjective objective, float projectedWin) =>
            objective.LocationOnly ? 0f : projectedWin;

        private static void MarkPreparation(MissionProposal proposal, AttackPreparationStep step)
        {
            AttackMissionTarget t = (AttackMissionTarget)proposal.Target;
            t.Preparation = true;
            t.PreparationStep = step;
            proposal.Target = t;
        }

        // A host-side preparation step (CreateHost / Assemble): no walking actor. `hostId` null =
        // the container does not exist yet. The allocator funds exactly `ap` (2 AP for a creation,
        // 0 for a reuse / same-hex join); Provisioning re-validates, Execution performs it.
        private static void AppendPreparationStep(WorldSnapshot snap, AttackObjective objective,
            int? hostId, HexCoord hostHex, AttackPreparationStep step, float ap,
            GroundCombatAssemblyPlan assembly, float hexBonus, List<MissionProposal> proposals,
            string head, MissionIntent intent = null, int[] supports = null,
            TaskScore? pricedScore = null)
        {
            float win = assembly?.ProjectedWinChance ?? 0f;
            TaskScore score = pricedScore ?? (intent != null ? default(TaskScore)
                : TaskScoreEvaluator.WithResponse(objective.TaskScore,
                    PreparationWin(objective, win), ap, 0f, 1f));
            var target = new AttackMissionTarget
            {
                Phase = AttackMissionPhase.Gather,
                Target = objective.Target,
                PrimaryArmyId = hostId,
                GatherSupportArmyIds = supports ?? System.Array.Empty<int>(),
                DestinationHex = hostHex,
                DefenderHexDefenseBonus = hexBonus,
                DefenderCount = objective.DefenderCount,
                ProjectedWinChance = win,
                CoversAllDefenders = assembly?.CoversAllDefenders ?? false,
                EstimatedEta = 1,
                Preparation = true,
                PreparationStep = step,
            };
            var proposal = new MissionProposal
            {
                Kind = MissionKind.Attack,
                Target = target,
                BaseValue = score.Value,
                Score = score,
                LocalAdmissionScore = score.Value,
                PreferredMoverArmyId = hostId,
                FromDurableIntent = intent != null,
                DurableFundingTier = intent?.Funding ?? CommitmentTier.None,
                // No existing mover is required for a creation: the step makes the container.
                Requirements = new MissionRequirements
                {
                    MoverKnown = hostId.HasValue,
                    RequiresArmy = hostId.HasValue,
                    ApMinimum = ap, ApDesired = ap, ApMaximum = ap,
                    EtaTurns = 1, EstimatedDistance = 0,
                },
                Explain = $"Attack {objective.Target.DiagnosticLabel} preparation {step} host "
                    + $"{(hostId.HasValue ? $"#{hostId.Value}" : "new")} at ({hostHex.Q},{hostHex.R}) "
                    + $"ap {F(ap)} task {F(score.Value)}",
            };
            proposal.Axes.Value[DesireAxis.Aggression] = 1f;
            proposals.Add(proposal);
            string transfers = assembly == null ? "none"
                : string.Join(",", assembly.Transfers.Select(x => $"{x.Unit?.Name}<-#{x.DonorArmyId}"));
            AiDebugLog.WriteDeduped(intent != null ? intent.IntentKey + "#prep"
                    : $"mobilization#{snap.Observer?.ColorIndex}",
                $"{head} decision=PROPOSE step={step} host={(hostId.HasValue ? $"#{hostId.Value}" : "new")} "
                + $"hex=({hostHex.Q},{hostHex.R}) ap={F(ap)} transfers=[{transfers}] "
                + $"projected={F(assembly?.ProjectedPower ?? 0f)} score={F(score.Value)}");
        }

        // Audit F7 — the first leg of a fresh cross-hex gather. The whole operation is priced here
        // (win of the assembled force, gather + assault ETA, total AP spread over that ETA) so it
        // competes honestly with every other lane; its first executed step creates the Hard
        // intent (§70) carrying the frozen plan, after which the remaining legs are proposed as
        // durable lifecycle work by AppendAttackGather. The lead leg is the critical path: the
        // support with the longest walk that can act this turn.
        private static bool TryAppendFreshAttackGather(WorldSnapshot snap, AttackObjective objective,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, float hexBonus, ISet<int> excluded,
            List<MissionProposal> proposals)
        {
            Dictionary<int, float> donorValues = GroundCombatDonorPolicy.BorrowableDonorValues(
                snap.Observer == null ? null : MissionIntentRegistry.GetOrCreate(snap.Observer).All);
            GroundCombatGatherPlan gather = GroundCombatAssemblyPlanner.PlanGather(snap, opposition,
                hexBonus, objective.Hex, excluded, GroundCombatAdmissionPolicy.AttackCoverageGate,
                donorValues: donorValues,
                minimumArmyPower: 0.80f * snap.Self.TotalMilitaryPotential);
            if (!gather.Feasible || gather.SupportArmyIds.Count == 0)
            {
                AiDebugLog.WriteDeduped(objective.Target.DiagnosticLabel + "#gather",
                    $"[AI][V2][Attack][Gather] decision=REJECT target={objective.Target.DiagnosticLabel} "
                    + $"reason={gather.Reason ?? "host_already_clears"}");
                return false;
            }
            ArmySnapshot host = snap.Self.Armies?.FirstOrDefault(x => x != null
                && x.ArmyId == gather.HostArmyId);
            // The lead leg must be a FREE army: a bought donor is still held by its operation until
            // the Attack intent this lead step creates makes Continuity retire that operation.
            ArmySnapshot lead = gather.SupportArmyIds
                .Where(id => !donorValues.ContainsKey(id))
                .Select(id => snap.Self.Armies?.FirstOrDefault(x => x != null && x.ArmyId == id))
                .FirstOrDefault(s => s != null && (s.Hex.Equals(gather.HostHex) || s.CurrentMovement > 0));
            if (host == null || lead == null)
            {
                AiDebugLog.WriteDeduped(objective.Target.DiagnosticLabel + "#gather",
                    $"[AI][V2][Attack][Gather] decision=HOLD target={objective.Target.DiagnosticLabel} "
                    + $"host={gather.HostArmyId} reason=no_free_planned_support_can_act_this_turn");
                return false;
            }

            int eta = Mathf.Max(1, gather.TotalEta);
            // Priced off the plan's own AP split, never off the host's activation state: the
            // supports' legs are what this turn pays, the rest is spread over the operation.
            TaskScore score = TaskScoreEvaluator.WithResponse(objective.TaskScore,
                gather.ProjectedWinChance, gather.CurrentTurnAp,
                AiV2Util.CeilDiv(gather.FutureAp, eta), eta,
                moverOpportunityCost: gather.DisplacedValue);
            MissionProposal proposal = BuildAttackGatherLeg(objective.Target, host, lead,
                gather.SupportArmyIds, hexBonus, objective.DefenderCount, gather.ProjectedWinChance,
                gather.CoversAllDefenders, 0, score, null);
            proposal.Explain = $"Attack {objective.Target.DiagnosticLabel} Gather (fresh) task "
                + $"{F(score.Value)} host #{host.ArmyId} supports [{string.Join(",", gather.SupportArmyIds)}] "
                + $"win {F(gather.ProjectedWinChance)} gatherTurns {gather.GatherTurns} "
                + $"assaultEta {gather.AssaultEta} ap {gather.TotalAp}";
            proposals.Add(proposal);
            AiDebugLog.WriteDeduped(objective.Target.DiagnosticLabel + "#gather",
                $"[AI][V2][Attack][Gather] decision=PROPOSE target={objective.Target.DiagnosticLabel} "
                + $"host={host.ArmyId} lead={lead.ArmyId} supports=[{string.Join(",", gather.SupportArmyIds)}] "
                + $"win={F(gather.ProjectedWinChance)} gatherTurns={gather.GatherTurns} "
                + $"assaultEta={gather.AssaultEta} ap={gather.TotalAp} score={F(score.Value)}");
            return true;
        }

        // Audit F7 — the durable Gather legs: every planned support still expected at the host
        // walks to it (or hands over once there) in parallel. Lifecycle work of a Hard operation,
        // so, like the Reinforcement convoy, the intrinsic score stays neutral.
        private static void AppendAttackGather(WorldSnapshot snap, MissionIntent intent,
            AttackIntent a, ISet<int> committed, List<MissionProposal> proposals, AiTurnContext ctx)
        {
            if (!a.PrimaryArmyId.HasValue)
                return;
            ArmySnapshot host = snap.Self.Armies?.FirstOrDefault(x => x != null
                && x.ArmyId == a.PrimaryArmyId.Value);
            if (host == null)
                return;
            float hexBonus = AttackObjectiveEvaluator.KnownSiteDefenceBonus(snap, ctx?.Map, a.Target.Hex);
            int defenderCount = AttackObjectiveEvaluator.KnownSiteDefenders(snap, a.Target.Hex).Count;
            HexCoord? staging = a.Preparation
                ? AttackObjectiveEvaluator.PreparationStagingBase(snap, a.Target.Hex, host) : null;
            if (a.Preparation && staging.HasValue && !host.Hex.Equals(staging.Value)
                && host.MemberCount > 0)
            {
                // The durable host walks on to the staging Base (lifecycle leg, neutral score).
                if (host.CurrentMovement > 0)
                {
                    AttackObjective tracked = AttackObjectiveEvaluator.ForTrackedTarget(snap, a.Target)
                        ?? new AttackObjective { Target = a.Target };
                    AppendPreparationStep(snap, tracked, host.ArmyId, staging.Value,
                        AttackPreparationStep.MoveHost,
                        host.HasActivatedThisTurn ? 0f : host.ActivationApCost, null, hexBonus,
                        proposals, $"[AI][V2][Attack][Mobilization] {intent.IntentKey}", intent);
                }
            }
            else if (a.Preparation)
            {
                AppendPreparationAssembly(snap, intent, a, host, hexBonus, committed, proposals);
                if (a.GatherSupportArmyIds.Count == 0)
                    AppendPreparationRecruit(snap, intent, a, host, hexBonus, committed, proposals);
            }
            foreach (int supportId in a.GatherSupportArmyIds.ToList())
            {
                ArmySnapshot support = snap.Self.Armies?.FirstOrDefault(x => x != null
                    && x.ArmyId == supportId);
                // Nothing to do this turn: an idle proposal would only fail NoExecutableStep.
                if (support == null || (!support.Hex.Equals(host.Hex) && support.CurrentMovement <= 0))
                    continue;
                MissionProposal leg = BuildAttackGatherLeg(a.Target, host, support, a.GatherSupportArmyIds,
                    hexBonus, defenderCount, a.ProjectedWinChance, a.CoversAllDefenders,
                    a.LastOpportunisticStrikeTurn, default(TaskScore), intent);
                if (a.Preparation)
                    MarkPreparation(leg, AttackPreparationStep.None);
                proposals.Add(leg);
            }
            AiDebugLog.WriteDeduped(intent.IntentKey.ToString(),
                $"[AI][V2][Attack][Gather] decision=CONTINUE {intent.IntentKey} host={host.ArmyId} "
                + $"supports=[{string.Join(",", a.GatherSupportArmyIds)}]");
        }

        // T01 — the durable same-hex step of a live preparation: legal same-hex bodies that raise
        // the host join it (garrison floors / operators / claims respected by the one planner).
        // Lifecycle work of the Hard operation: neutral intrinsic score, 0 AP.
        private static void AppendPreparationAssembly(WorldSnapshot snap, MissionIntent intent,
            AttackIntent a, ArmySnapshot host, float hexBonus, ISet<int> committed,
            List<MissionProposal> proposals)
        {
            PlayerSetupData player = snap.Observer;
            ArmyData live = player == null ? null : AiV2Util.ResolveArmy(player, host.ArmyId);
            if (live == null)
                return;
            var unavailable = committed == null ? new HashSet<int>() : new HashSet<int>(committed);
            unavailable.Remove(host.ArmyId);
            IReadOnlyList<WorthIt.DefendingArmy> opposition =
                AttackObjectiveEvaluator.KnownSiteOpposition(snap, a.Target.Hex);
            GroundCombatAssemblyPlan step = GroundCombatAssemblyPlanner.PlanPreparationAssembly(
                snap, live, unavailable, opposition, hexBonus);
            if (!step.Feasible)
                return;
            AttackObjective objective = AttackObjectiveEvaluator.ForTrackedTarget(snap, a.Target)
                ?? new AttackObjective { Target = a.Target };
            AppendPreparationStep(snap, objective, host.ArmyId, host.Hex,
                AttackPreparationStep.Assemble, 0f, step, hexBonus, proposals,
                $"[AI][V2][Attack][Mobilization] {intent.IntentKey}", intent);
        }

        // ATK-F05 — a live preparation buys supports another operation holds only through the
        // allocator, like a fresh gather: Continuity re-plans with free armies alone, and this
        // FRESH proposal (not the Hard intent's lifecycle, no default score) prices the whole
        // re-plan with what the lenders lose (MoverOpportunityCost = their DisplacementValue).
        // Until it is funded and executed the donors stay with their operations.
        private static void AppendPreparationRecruit(WorldSnapshot snap, MissionIntent intent,
            AttackIntent a, ArmySnapshot host, float hexBonus, ISet<int> committed,
            List<MissionProposal> proposals)
        {
            if (host.MemberCount == 0 || snap.Observer == null)
                return;
            Dictionary<int, float> donorValues = GroundCombatDonorPolicy.BorrowableDonorValues(
                MissionIntentRegistry.GetOrCreate(snap.Observer).All);
            if (donorValues.Count == 0)
                return;
            var unavailable = committed == null ? new HashSet<int>() : new HashSet<int>(committed);
            unavailable.Remove(host.ArmyId);
            float required = 0.80f * snap.Self.TotalMilitaryPotential;
            GroundCombatGatherPlan plan = GroundCombatAssemblyPlanner.PlanGather(snap,
                AttackObjectiveEvaluator.KnownSiteOpposition(snap, a.Target.Hex), hexBonus,
                a.Target.Hex, unavailable, GroundCombatAdmissionPolicy.AttackCoverageGate,
                host.ArmyId, donorValues, minimumArmyPower: required, allowPartial: true);
            List<int> bought = plan.Feasible
                ? plan.SupportArmyIds.Where(donorValues.ContainsKey).ToList() : new List<int>();
            if (bought.Count == 0)
            {
                AiDebugLog.WriteDeduped(intent.IntentKey + "#recruit",
                    $"[AI][V2][Attack][Mobilization] {intent.IntentKey} decision=NONE step=RecruitDonors "
                    + $"host=#{host.ArmyId} reason={(plan.Feasible ? "plan_needs_no_bought_donor" : plan.Reason)}");
                return;
            }
            AttackObjective objective = AttackObjectiveEvaluator.ForTrackedTarget(snap, a.Target)
                ?? new AttackObjective { Target = a.Target };
            int eta = Mathf.Max(1, plan.TotalEta);
            TaskScore score = TaskScoreEvaluator.WithResponse(objective.TaskScore,
                PreparationWin(objective, plan.ProjectedWinChance), plan.CurrentTurnAp,
                AiV2Util.CeilDiv(plan.FutureAp, eta), eta, moverOpportunityCost: plan.DisplacedValue);
            AppendPreparationStep(snap, objective, host.ArmyId, host.Hex,
                AttackPreparationStep.RecruitDonors, 0f, null, hexBonus, proposals,
                $"[AI][V2][Attack][Mobilization] {intent.IntentKey} donors=[{string.Join(",", bought.Select(id => $"#{id}:{F(donorValues[id])}"))}] "
                + $"supports=[{string.Join(",", plan.SupportArmyIds)}] projected={F(plan.ProjectedPower)} "
                + $"reachesThreshold={plan.ReachesThreshold}",
                supports: plan.SupportArmyIds.ToArray(), pricedScore: score);
        }

        private static MissionProposal BuildAttackGatherLeg(AttackTargetRef targetRef,
            ArmySnapshot host, ArmySnapshot support, IEnumerable<int> gatherSupportIds,
            float hexBonus, int defenderCount, float win, bool cover, int opportunisticTurn,
            TaskScore score, MissionIntent intent)
        {
            MissionRequirements requirements = GroundCombatLegs.PinnedLegRequirements(
                support, host.Hex, out int eta);
            var target = new AttackMissionTarget
            {
                Phase = AttackMissionPhase.Gather,
                Target = targetRef,
                PrimaryArmyId = host.ArmyId,
                SupportArmyId = support.ArmyId,
                GatherSupportArmyIds = gatherSupportIds.ToArray(),
                DestinationHex = host.Hex,
                DefenderHexDefenseBonus = hexBonus,
                DefenderCount = defenderCount,
                ProjectedWinChance = win,
                CoversAllDefenders = cover,
                EstimatedEta = eta,
                OpportunisticStrikeTurn = opportunisticTurn,
            };
            var proposal = new MissionProposal
            {
                Kind = MissionKind.Attack,
                Target = target,
                BaseValue = score.Value,
                Score = score,
                LocalAdmissionScore = score.Value,
                PreferredMoverArmyId = support.ArmyId,
                FromDurableIntent = intent != null,
                DurableFundingTier = intent?.Funding ?? CommitmentTier.None,
                Requirements = requirements,
                Explain = $"Attack {targetRef.DiagnosticLabel} Gather support #{support.ArmyId} -> host "
                    + $"#{host.ArmyId} at ({host.Hex.Q},{host.Hex.R})",
            };
            proposal.Axes.Value[DesireAxis.Aggression] = 1f;
            return proposal;
        }

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
            if (wing == null)
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

            MissionRequirements requirements = GroundCombatLegs.PinnedLegRequirements(
                support, primary.Hex, out int eta);
            var target = new AttackMissionTarget
            {
                Phase = AttackMissionPhase.Reinforcement,
                Target = a.Target,
                PrimaryArmyId = a.PrimaryArmyId,
                SupportArmyId = a.SupportArmyId,
                DestinationHex = primary.Hex,
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
                    + $"#{a.PrimaryArmyId} at ({primary.Hex.Q},{primary.Hex.R})",
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
            int distance = priced == null ? 0 : HexGridMath.Distance(priced.Hex, primary.Hex);
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
