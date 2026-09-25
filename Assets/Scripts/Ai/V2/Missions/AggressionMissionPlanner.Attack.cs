using System.Collections.Generic;
using System.Linq;
using Game.Combat;
using Game.HexGrid;
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
            List<MissionProposal> proposals, AiTurnContext ctx)
        {
            if (snap?.Self == null)
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
                        AppendAttackReinforcement(snap, intent, a, committed, proposals, ctx);
                    else if (a.Phase == AttackMissionPhase.Gather)
                        AppendAttackGather(snap, intent, a, proposals, ctx);
                    // Strike force step 5 — donors that already handed over walk home beside
                    // whatever the operation itself does.
                    foreach (AttackGatherReturn r in a.GatherReturns)
                        AppendAttackWalkHome(snap, intent, a, AttackMissionPhase.GatherReturn,
                            r.ArmyId, r.BaseHex, proposals);
                    // The bound support wing flies its sortie beside the operation too.
                    AppendAttackAirSupport(snap, intent, a, proposals);
                }

            // ---- Assault: fresh objectives and incumbents still marching on their target ------
            foreach (AttackObjective objective in AttackObjectiveEvaluator.Enumerate(snap))
            {
                MissionIntent incumbent = activeIntents?.FirstOrDefault(i => i != null
                    && i.Status == IntentStatus.Active && i.Kind == MissionKind.Attack
                    && i.Attack != null && i.Attack.Target.Equals(objective.Target));
                // An incumbent already past the Assault leg is being proposed above; do not also
                // offer it a fresh assault against the same target this pass.
                if (incumbent != null && incumbent.Attack.Phase != AttackMissionPhase.Assault)
                    continue;

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
                        WinChanceGate = GroundCombatAdmissionPolicy.AttackWinChanceFloor,
                        PreferredPrimaryArmyId = pinnedActor,
                        PinToPreferred = pinnedActor.HasValue,
                        ExcludedArmyIds = excluded,
                        DefenderHexDefenseBonus = hexBonus,
                    });

                if (!plan.Feasible)
                {
                    // Audit F7 — a FRESH objective no single army nor same-hex package can take
                    // may still be taken by free armies spread over several hexes: gather them.
                    if (incumbent == null && TryAppendFreshAttackGather(snap, objective, opposition,
                            hexBonus, excluded, proposals))
                        continue;
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

                // Strike force step 5 — a fresh operation may instead gather the fist to its peak
                // first: that gather competes with this direct assault on the same TaskScore.
                if (incumbent == null && TryAppendFreshAttackGather(snap, objective, opposition,
                        hexBonus, excluded, proposals, score.Value))
                    continue;

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
                AiDebugLog.WriteDeduped(objective.Target.DiagnosticLabel,
                    $"[AI][V2][Attack][Admission] decision=PROPOSE target={objective.Target.DiagnosticLabel} "
                    + $"actor={actor.ArmyId} score={F(score.Value)} eligible=[{GroundCombatAdmissionRegistry.EligibleIds(proposal)}]");
            }
        }

        // Audit F7 — the first leg of a fresh cross-hex gather. The whole operation is priced here
        // (win of the assembled force, gather + assault ETA, total AP spread over that ETA) so it
        // competes honestly with every other lane; its first executed step creates the Hard
        // intent (§70) carrying the frozen plan, after which the remaining legs are proposed as
        // durable lifecycle work by AppendAttackGather. The lead leg is the critical path: the
        // support with the longest walk that can act this turn.
        // `mustBeat` — the score of a direct assault the gather must out-score (null: none exists).
        private static bool TryAppendFreshAttackGather(WorldSnapshot snap, AttackObjective objective,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, float hexBonus, ISet<int> excluded,
            List<MissionProposal> proposals, float? mustBeat = null)
        {
            GroundCombatGatherPlan gather = GroundCombatAssemblyPlanner.PlanGather(snap, opposition,
                hexBonus, objective.Hex, excluded, GroundCombatAdmissionPolicy.AttackWinChanceFloor);
            if (!gather.Feasible || gather.SupportArmyIds.Count == 0)
            {
                AiDebugLog.WriteDeduped(objective.Target.DiagnosticLabel + "#gather",
                    $"[AI][V2][Attack][Gather] decision=REJECT target={objective.Target.DiagnosticLabel} "
                    + $"reason={gather.Reason ?? "host_already_clears"}");
                return false;
            }
            ArmySnapshot host = snap.Self.Armies?.FirstOrDefault(x => x != null
                && x.ArmyId == gather.HostArmyId);
            ArmySnapshot lead = gather.SupportArmyIds
                .Select(id => snap.Self.Armies?.FirstOrDefault(x => x != null && x.ArmyId == id))
                .FirstOrDefault(s => s != null && (s.Hex.Equals(gather.HostHex) || s.CurrentMovement > 0));
            if (host == null || lead == null)
            {
                AiDebugLog.WriteDeduped(objective.Target.DiagnosticLabel + "#gather",
                    $"[AI][V2][Attack][Gather] decision=HOLD target={objective.Target.DiagnosticLabel} "
                    + $"host={gather.HostArmyId} reason=no_planned_support_can_act_this_turn");
                return false;
            }

            int eta = Mathf.Max(1, gather.TotalEta);
            int perTurnAp = AiV2Util.CeilDiv(gather.TotalAp, eta);
            TaskScore score = AttackObjectiveEvaluator.WithResponse(objective, host,
                gather.ProjectedWinChance, eta, 0f, perTurnAp);
            if (mustBeat.HasValue && score.Value <= mustBeat.Value)
            {
                AiDebugLog.WriteDeduped(objective.Target.DiagnosticLabel + "#gather",
                    $"[AI][V2][Attack][Gather] decision=SKIP target={objective.Target.DiagnosticLabel} "
                    + $"host={host.ArmyId} win={F(gather.ProjectedWinChance)} score={F(score.Value)} "
                    + $"reason=direct_assault_scores_higher({F(mustBeat.Value)})");
                return false;
            }
            MissionProposal proposal = BuildAttackGatherLeg(objective.Target, host, lead,
                gather.SupportArmyIds, hexBonus, objective.DefenderCount, gather.ProjectedWinChance,
                gather.CoversAllDefenders, 0, score.Value, null);
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
            AttackIntent a, List<MissionProposal> proposals, AiTurnContext ctx)
        {
            if (!a.PrimaryArmyId.HasValue)
                return;
            ArmySnapshot host = snap.Self.Armies?.FirstOrDefault(x => x != null
                && x.ArmyId == a.PrimaryArmyId.Value);
            if (host == null)
                return;
            float hexBonus = AttackObjectiveEvaluator.KnownSiteDefenceBonus(snap, ctx?.Map, a.Target.Hex);
            int defenderCount = AttackObjectiveEvaluator.KnownSiteDefenders(snap, a.Target.Hex).Count;
            foreach (int supportId in a.GatherSupportArmyIds.ToList())
            {
                ArmySnapshot support = snap.Self.Armies?.FirstOrDefault(x => x != null
                    && x.ArmyId == supportId);
                // Nothing to do this turn: an idle proposal would only fail NoExecutableStep.
                if (support == null || (!support.Hex.Equals(host.Hex) && support.CurrentMovement <= 0))
                    continue;
                proposals.Add(BuildAttackGatherLeg(a.Target, host, support, a.GatherSupportArmyIds,
                    hexBonus, defenderCount, a.ProjectedWinChance, a.CoversAllDefenders,
                    a.LastOpportunisticStrikeTurn, 0f, intent));
            }
            AiDebugLog.WriteDeduped(intent.IntentKey.ToString(),
                $"[AI][V2][Attack][Gather] decision=CONTINUE {intent.IntentKey} host={host.ArmyId} "
                + $"supports=[{string.Join(",", a.GatherSupportArmyIds)}]");
        }

        private static MissionProposal BuildAttackGatherLeg(AttackTargetRef targetRef,
            ArmySnapshot host, ArmySnapshot support, IEnumerable<int> gatherSupportIds,
            float hexBonus, int defenderCount, float win, bool cover, int opportunisticTurn,
            float value, MissionIntent intent)
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
                BaseValue = value,
                LocalAdmissionScore = value,
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
            AttackIntent a, ISet<int> committed, List<MissionProposal> proposals, AiTurnContext ctx)
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
                AppendAttackUnpinnedReinforcement(snap, intent, a, primary, committed, proposals, ctx);
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
            List<MissionProposal> proposals, AiTurnContext ctx)
        {
            IReadOnlyList<WorthIt.DefendingArmy> opposition =
                AttackObjectiveEvaluator.KnownSiteOpposition(snap, a.Target.Hex);
            float hexBonus = AttackObjectiveEvaluator.KnownSiteDefenceBonus(snap, ctx?.Map, a.Target.Hex);
            List<int> candidates = GroundCombatAssemblyPlanner.ReinforcementSupportCandidates(
                snap, a.PrimaryArmyId.Value, opposition, committed, hexBonus);
            if (candidates.Count == 0)
            {
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
            GroundCombatAdmissionRegistry.RecordReinforcement(proposal, snap);
            proposals.Add(proposal);
            AiDebugLog.WriteDeduped(intent.IntentKey.ToString(),
                $"[AI][V2][Attack][Admission] decision=REINFORCE-SELECT {intent.IntentKey} "
                + $"primary={a.PrimaryArmyId} candidates={candidates.Count} "
                + $"eligible=[{GroundCombatAdmissionRegistry.EligibleIds(proposal)}]");
        }
    }
}
