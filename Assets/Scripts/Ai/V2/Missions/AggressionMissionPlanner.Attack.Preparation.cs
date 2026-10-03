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
    internal static partial class AggressionMissionLayer
    {
        // ---- T01: the mobilization trigger and the first preparation step -----------------------
        //
        // Opens ONE new preparation when the mobilization gate is open (AttackObjectiveEvaluator
        // .MobilizationOpen: the deployed share reaches three quarters, or the field bodies can
        // already form the strike army) and no Attack operation is live. It never admits a march: the prepared
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
            bool open = AttackForceReadiness.MobilizationOpen(self);
            bool byShare = AttackForceReadiness.MobilizationOpen(self.DeployedPower, self.AvailablePower);
            bool byField = AttackForceReadiness.FieldStrikeForceReady(self.FieldStrikePotential,
                self.AttackPeak);
            string share = $"deployed={F(self.DeployedPower)} available={F(self.AvailablePower)} "
                + $"share={(self.AvailablePower > 0f ? 100f * self.DeployedPower / self.AvailablePower : 0f):0.00}% "
                + $"gate>=75% fieldStrike={F(self.FieldStrikePotential)} gate>{F(AttackForceReadiness.RequiredPower(self.AttackPeak))} "
                + $"open={(open ? (byShare && byField ? "share+field" : byShare ? "share" : byField ? "field" : "held") : "0")}";
            MissionIntent live = LiveAttackOperation(activeIntents);
            string skip = !open ? "trigger_closed"
                : live != null ? $"operation_live:{live.IntentKey}"
                : attackProposed ? "direct_assault_or_gather_proposed"
                : objectives.Count == 0 ? "no_attack_objective"
                : !AttackPreparationPolicy.PreparationStagingBase(snap, objectives[0].Hex).HasValue
                    ? "no_own_base" : null;
            string logKey = $"mobilization#{snap.Observer?.ColorIndex}";
            if (skip != null)
            {
                AiDebugLog.WriteDeduped(logKey,
                    $"[AI][V2][Attack][Mobilization] decision=NONE {share} reason={skip}");
                return;
            }

            // A preparation is about to be proposed from a genuinely open gate: keep the gate open
            // for the next two turns so a one-pass flicker cannot cost the start of the operation.
            if (byShare || byField)
                OperationContinuationWindow.HoldMobilization(snap.Observer, snap.TurnNumber);

            AttackObjective objective = objectives[0];
            HexCoord citadel = AttackPreparationPolicy.PreparationStagingBase(snap, objective.Hex).Value;
            float required = AttackForceReadiness.RequiredPower(self.AttackPeak);
            float hexBonus = AttackObjectiveEvaluator.KnownSiteDefenceBonus(snap, ctx?.Map, objective.Hex);
            IReadOnlyList<WorthIt.DefendingArmy> opposition = objective.Opposition;
            var excluded = committed == null ? new HashSet<int>() : new HashSet<int>(committed);
            string head = $"[AI][V2][Attack][Mobilization] target={objective.Target.DiagnosticLabel} "
                + $"knowledge={(objective.LocationOnly ? "starting-location-only" : "observed")} {share} "
                + $"ideal={F(self.AttackPeak)} required>{F(required)} "
                + $"staging=({citadel.Q},{citadel.R})";

            // 1) an existing free field army hosts the fist wherever it stands.
            ArmySnapshot fieldHost = PreparationFieldHost(snap, excluded, citadel);
            PlayerSetupData player = snap.Observer;
            HexCoord? hostStaging = fieldHost == null ? (HexCoord?)null
                : AttackPreparationPolicy.PreparationStagingBase(snap, objective.Hex, fieldHost);
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
                    // The step is priced for the gather stage only (walks + handoffs); the later
                    // assault march is paid turn by turn when it is taken, not up front here.
                    int eta = Mathf.Max(1, gather.GatherTurns);
                    TaskScore score = TaskScoreEvaluator.WithResponse(objective.TaskScore,
                        PreparationWin(objective, gather.ProjectedWinChance), gather.CurrentTurnAp,
                        AiV2Util.CeilDiv(gather.GatherFutureAp, eta), eta,
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

            // 2) a free lone-hero army on the staging Base (its hero already gives the capacity,
            //    0 AP), else an empty reusable shell standing there.
            ArmyData shell = player == null ? null : LoneHeroHostAt(player, citadel, excluded)
                ?? ReusableArmySelector.FindReusableAt(player, citadel, null);
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

        // The free field army a preparation hosts its fist in: the strongest, the one already on
        // the staging Base first among equals. Also read by ActiveDefence, which pays the
        // preparation's value to take this army before the preparation claims it.
        internal static ArmySnapshot PreparationFieldHost(WorldSnapshot snap, ISet<int> excluded,
            HexCoord? staging) =>
            GroundCombatActorEligibility.EligibleArmies(snap, excluded, requireMovementNow: false)
                .OrderByDescending(a => a.EffectiveArmyPower)
                .ThenByDescending(a => staging.HasValue && a.Hex.Equals(staging.Value))
                .ThenBy(a => a.ArmyId)
                .FirstOrDefault();

        // 2026-09-30 (user decision) — the army a preparation about to open would host in, and
        // what taking it elsewhere costs (the best Attack objective's TaskScore): the mobilization
        // gate is open, no Attack operation is live, and an objective with a staging Base exists.
        // (-1, 0) otherwise. A live preparation's host is claimed and never offered at all.
        internal static (int armyId, float value) PendingPreparationHost(WorldSnapshot snap,
            IReadOnlyList<MissionIntent> activeIntents, ISet<int> committed)
        {
            if (snap?.Self == null || !AttackForceReadiness.MobilizationOpen(snap.Self)
                || LiveAttackOperation(activeIntents) != null)
                return (-1, 0f);
            AttackObjective objective = AttackObjectiveEvaluator.Enumerate(snap).FirstOrDefault();
            HexCoord? staging = objective == null ? (HexCoord?)null
                : AttackPreparationPolicy.PreparationStagingBase(snap, objective.Hex);
            if (!staging.HasValue)
                return (-1, 0f);
            var excluded = committed == null ? new HashSet<int>() : new HashSet<int>(committed);
            ArmySnapshot host = PreparationFieldHost(snap, excluded, staging);
            return host == null ? (-1, 0f) : (host.ArmyId, Mathf.Max(0f, objective.TaskScore.Value));
        }

        // 2026-09-30 — a free field army holding one hero and nothing else on the staging Base:
        // the widest command first. A garrison hero or a facility operator is never a host.
        private static ArmyData LoneHeroHostAt(PlayerSetupData player, HexCoord hex, ISet<int> excluded) =>
            ArmyRegistry.AllForOwner(player)
                .Where(a => a != null && a.Hex.Equals(hex) && !a.IsGarrison && !a.IsPrison
                    && !a.IsAirfield && !a.IsAirArmy && a.Members.Count == 1
                    && a.Members[0] != null && a.Members[0].IsHero
                    && !AiArmyRoles.IsGarrisonHero(a.Members[0])
                    && !AiArmyRoles.IsFacilityOperator(player, hex, a.Members[0])
                    && (excluded == null || !excluded.Contains(a.Id)))
                .OrderByDescending(a => a.Members[0].CommandRating)
                .ThenBy(a => a.Id)
                .FirstOrDefault();

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
            TaskScore? pricedScore = null, int? commanderDonorId = null, int commanderUnitId = 0)
        {
            float win = assembly?.ProjectedWinChance ?? 0f;
            TaskScore score = pricedScore ?? (intent != null ? default(TaskScore)
                : TaskScoreEvaluator.WithResponse(objective.TaskScore,
                    PreparationWin(objective, win), ap, 0f, 1f,
                    moverOpportunityCost: ActionPrice.GarrisonHeroFallback(
                        assembly?.UsesGarrisonHero == true)));
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
                CommanderDonorArmyId = commanderDonorId,
                CommanderUnitId = commanderUnitId,
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
            AttackPreparationAssessment readiness = AttackPreparationReadiness.Assess(host,
                snap.Self.AttackPeak, AttackObjectiveEvaluator.KnownSiteOpposition(snap, a.Target.Hex),
                hexBonus);
            GroundCombatAssemblyPlan step = GroundCombatAssemblyPlanner.PlanPreparationAssembly(
                snap, live, unavailable, opposition, hexBonus);
            if (!step.Feasible)
                return;
            AttackObjective objective = AttackObjectiveEvaluator.ForTrackedTarget(snap, a.Target)
                ?? new AttackObjective { Target = a.Target };
            AppendPreparationStep(snap, objective, host.ArmyId, host.Hex,
                AttackPreparationStep.Assemble, 0f, step, hexBonus, proposals,
                $"[AI][V2][Attack][Mobilization] {intent.IntentKey} readiness={readiness.Reason} "
                + $"power={F(readiness.CurrentPower)} required>{F(readiness.RequiredPower)}", intent);
        }

        // 2026-10-01 (user decision, variant B) — the host's own commander caps the roster
        // (StrikeRoster.ComposeUnder). When that roster cannot clear the march bar (> 80% of the
        // peak) and an own garrison ELSEWHERE holds a hero with a larger Command whose roster would
        // be stronger — never a garrison hero (Support tag), never a facility operator, only one
        // the garrison may spare — the hero leaves the garrison as a lone-hero container (2 AP,
        // lifecycle of the Hard operation) and then walks to the host (CommanderLeg).
        private static void AppendPreparationCommanderFetch(WorldSnapshot snap, MissionIntent intent,
            AttackIntent a, ArmySnapshot host, float hexBonus, List<MissionProposal> proposals)
        {
            var best = AttackPreparationPolicy.FindCommanderUpgrade(snap, host,
                out float currentPower, out int currentCommand, out bool assessable);
            if (!assessable)
                return;
            float required = AttackForceReadiness.RequiredPower(snap.Self.AttackPeak);
            if (AttackForceReadiness.ForceReady(currentPower, snap.Self.AttackPeak))
                return;
            if (best.hero == null)
            {
                AiDebugLog.WriteDeduped(intent.IntentKey + "#commander",
                    $"[AI][V2][Attack][Mobilization] {intent.IntentKey} decision=NONE step=FetchCommander "
                    + $"host=#{host.ArmyId} command={currentCommand} roster={F(currentPower)} required>{F(required)} "
                    + "reason=no_stronger_spareable_commander_in_an_own_garrison");
                return;
            }
            AttackObjective objective = AttackObjectiveEvaluator.ForTrackedTarget(snap, a.Target)
                ?? new AttackObjective { Target = a.Target };
            AppendPreparationStep(snap, objective, host.ArmyId, best.garrison.Hex,
                AttackPreparationStep.FetchCommander, ArmyActions.CreateArmyApCost, null, hexBonus,
                proposals,
                $"[AI][V2][Attack][Mobilization] {intent.IntentKey} commander={best.hero.Name}"
                + $"(command {best.hero.CommandRating}) from garrison #{best.garrison.Id} roster "
                + $"{F(currentPower)}->{F(best.power)} required>{F(required)}",
                intent, commanderDonorId: best.garrison.Id, commanderUnitId: best.hero.RuntimeId);
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
            float required = AttackForceReadiness.RequiredPower(snap.Self.AttackPeak);
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
            // Gather stage only, as every other gather step (the assault march is paid when taken).
            int eta = Mathf.Max(1, plan.GatherTurns);
            TaskScore score = TaskScoreEvaluator.WithResponse(objective.TaskScore,
                PreparationWin(objective, plan.ProjectedWinChance), plan.CurrentTurnAp,
                AiV2Util.CeilDiv(plan.GatherFutureAp, eta), eta, moverOpportunityCost: plan.DisplacedValue);
            AppendPreparationStep(snap, objective, host.ArmyId, host.Hex,
                AttackPreparationStep.RecruitDonors, 0f, null, hexBonus, proposals,
                $"[AI][V2][Attack][Mobilization] {intent.IntentKey} donors=[{string.Join(",", bought.Select(id => $"#{id}:{F(donorValues[id])}"))}] "
                + $"supports=[{string.Join(",", plan.SupportArmyIds)}] projected={F(plan.ProjectedPower)} "
                + $"reachesThreshold={plan.ReachesThreshold}",
                supports: plan.SupportArmyIds.ToArray(), pricedScore: score);
        }
    }
}
