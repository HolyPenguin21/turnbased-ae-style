using System.Collections.Generic;
using System.Linq;
using Game.Combat;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  ATK §41/§43/§75 — ATTACK CAPABILITY SHORTAGE.
    //
    //  A mechanical partial of the existing Aggression demand owner. Two things may create a
    //  demand here:
    //    * a PROVEN structural shortage of a bound, live Attack operation that has NOT yet begun
    //      its assault march: its primary cannot reach the current force threshold or cover the
    //      known defenders — the same gate the phase machine turns to Reinforcement on (one
    //      owner), so a delivered support is really used — and no existing free army could fix
    //      that by joining it. A committed Assault never asks (existing support only);
    //    * strike force step 4 — the best known Base/Citadel objective that no army can take even
    //      at the current force threshold, pinned to the free fist on an own Base
    //      (AppendUnboundAttackDemand). The target is a real, known structure, so this names a
    //      real objective, not an invented war (§43). Which card / generated output covers it is
    //      Materialization's choice (T04: no hand-only prerequisite here);
    //    * T01 — a live mobilization preparation whose host is below the current 80% peak once
    //      every existing troop that could join it is used (PreparationHostShortage).
    //
    //  Explicitly NOT shortages (§41):
    //    * an army is already committed elsewhere      -> actor contention, the allocator's problem
    //    * the primary has 0 MP this turn              -> a timing fact, not a capability gap
    //    * there is no AP left this turn               -> a budget fact, not a capability gap
    //    * an existing free army could reinforce       -> use it; nothing needs to be materialised
    //    * a side enemy on the route is too strong     -> ignore it (§14); it is not this operation
    //
    //  Production never invents a war (§43): every demand below names an EXISTING intent whose
    //  physical deficit was measured against a REAL known defender package.
    // ===========================================================================================
    public static partial class AggressionDemandEvaluator
    {
        internal static void AppendAttackDemands(WorldSnapshot snap,
            IReadOnlyList<MissionIntent> activeIntents, ActorCommitments commitments,
            CapabilityInventory inv, List<string> diag, List<AxisDemand> demands)
        {
            if (snap?.Self == null || activeIntents == null)
                return;

            foreach (MissionIntent i in activeIntents)
            {
                AttackIntent ai = i?.Kind == MissionKind.Attack ? i.Attack : null;
                if (ai == null || i.Status != IntentStatus.Active || !ai.Target.HasValue)
                    continue;

                // A walking-home leg needs no combat capability at all.
                if (ai.Phase == AttackMissionPhase.RecoveryReturn
                    || ai.Phase == AttackMissionPhase.SupportReturn)
                {
                    diag.Add($"[AI][V2][Demand][Aggression] decision=SATISFIED intent={i.IntentKey} "
                        + $"reason=attack_in_{ai.Phase.ToString().ToLowerInvariant()}_phase");
                    continue;
                }

                // T01 — a preparation host asks for NEW power only after the troops already on the
                // map are exhausted (supports, free armies, same-hex bodies).
                if (ai.Phase == AttackMissionPhase.Gather && ai.Preparation)
                {
                    AxisDemand prep = PreparationHostShortage(snap, commitments, i, ai, diag);
                    if (prep != null)
                        demands.Add(prep);
                    continue;
                }

                // Audit F7 — a planned gather already has its capability on the map; Production
                // is only asked if the gather falls apart (Continuity then moves to Reinforcement).
                if (ai.Phase == AttackMissionPhase.Gather)
                {
                    diag.Add($"[AI][V2][Demand][Aggression] decision=SATISFIED intent={i.IntentKey} "
                        + $"target={ai.Target.DiagnosticLabel} host={ai.PrimaryArmyId} "
                        + $"supports=[{string.Join(",", ai.GatherSupportArmyIds)}] "
                        + "reason=attack_gather_in_progress");
                    continue;
                }

                // 2026-10-04 — a committed Assault (the fist already marches on the target) is
                // reinforced only by armies already on the map (Continuity binds one when it can
                // meet the primary on its route); it never asks Production for a new support army,
                // and worse defender news never waits for one.
                if (ai.AssaultStarted)
                {
                    if (AttackBaseRefitPolicy.WindowOpen(snap, ai)
                        && snap.Self.Hand?.Any(c => c?.Definition != null && !c.Definition.isAviation
                            && (c.Definition.cardType == CardType.Unit || c.Definition.cardType == CardType.Hero)) == true)
                    {
                        TaskScore score = AttackObjectiveEvaluator.ForTrackedTarget(snap, ai.Target)?.TaskScore ?? default;
                        demands.Add(new AxisDemand { RequestingAxis = DesireAxis.Aggression,
                            Capability = CapabilityKind.FieldCombatPower, DeliveryShape = CapabilityDeliveryShape.Any,
                            AttackLocalRefit = true, AttackFistArmyId = ai.PrimaryArmyId,
                            ConsumerMissionKind = MissionKind.Attack, ConsumerIntentKey = i.IntentKey,
                            TargetHex = ai.RefitBaseHex, DesiredAmount = Mathf.Max(1f, snap.Self.AttackPeak),
                            RequiredCapabilityPower = 0f, WorldTaskScore = score, Value = score.Value,
                            Explain = $"Attack local refit #{ai.PrimaryArmyId} at {ai.RefitBaseHex}: available hand only" });
                    }
                    diag.Add($"[AI][V2][Demand][Aggression] decision=SKIP intent={i.IntentKey} "
                        + $"target={ai.Target.DiagnosticLabel} primary={ai.PrimaryArmyId} "
                        + $"phase={ai.Phase} reason=committed_assault_no_remote_reinforcement_request");
                    continue;
                }

                // Only a BOUND operation may ask for anything. Without a claimed primary there is
                // no proven obligation yet — the fresh-objective path owns that case.
                if (!ai.PrimaryArmyId.HasValue || commitments == null
                    || !commitments.IsArmyClaimed(ai.PrimaryArmyId.Value))
                    continue;
                int primaryId = ai.PrimaryArmyId.Value;

                AxisDemand attackShortage = GroundCombatDemandPolicy.BoundPrimaryShortage(snap, inv, commitments, i,
                    MissionKind.Attack, ai.Target.DiagnosticLabel, primaryId, ai.SupportArmyId,
                    ai.ReinforcementRequestedTurn,
                    AttackObjectiveEvaluator.KnownSiteOpposition(snap, ai.Target.Hex),
                    AttackObjectiveEvaluator.KnownSiteDefenceBonus(snap, null, ai.Target.Hex),
                    // Coverage is independent of the dynamic force requirement.
                    GroundCombatAdmissionPolicy.AttackCoverageGate,
                    () => AttackObjectiveEvaluator.ForTrackedTarget(snap, ai.Target)?.TaskScore ?? default,
                    diag, AttackForceReadiness.RequiredPower(snap.Self.AttackPeak));
                if (attackShortage != null)
                    demands.Add(attackShortage);
            }

            AppendUnboundAttackDemand(snap, activeIntents, commitments, diag, demands);

        }

        // T01 — the measured shortage of a live preparation host: the current 80% peak minus the
        // host's own power, pinned to that exact container (a stronger unrelated army never
        // closes it). Existing troops are gathered first; a host that no card can reach (not on
        // an own Base) waits for walking supports. The chain (hand card, generated output or
        // none) is MaterializationChainEnumerator's choice, never decided here.
        private static AxisDemand PreparationHostShortage(WorldSnapshot snap,
            ActorCommitments commitments, MissionIntent intent, AttackIntent ai, List<string> diag)
        {
            string at = $"intent={intent.IntentKey} target={ai.Target.DiagnosticLabel} "
                + $"host={ai.PrimaryArmyId} reason=";
            ArmySnapshot host = ai.PrimaryArmyId.HasValue
                ? snap.Self.Armies?.FirstOrDefault(a => a != null && a.ArmyId == ai.PrimaryArmyId.Value)
                : null;
            float required = AttackForceReadiness.RequiredPower(snap.Self.AttackPeak);
            float have = host?.EffectiveArmyPower ?? 0f;
            if (host == null)
            {
                diag.Add($"[AI][V2][Demand][Aggression] decision=DEFER {at}preparation_host_missing "
                    + $"have={have:0.#} required>{required:0.#}");
                return null;
            }
            if (commitments == null || !commitments.IsPreparationHost(host.ArmyId))
            {
                diag.Add($"[AI][V2][Demand][Aggression] decision=SKIP {at}preparation_host_not_claimed");
                return null;
            }
            int hostId = host.ArmyId;
            IReadOnlyList<WorthIt.DefendingArmy> opposition =
                AttackObjectiveEvaluator.KnownSiteOpposition(snap, ai.Target.Hex);
            float hexBonus = AttackObjectiveEvaluator.KnownSiteDefenceBonus(snap, null, ai.Target.Hex);
            // The march gate is power AND coverage: GroundCombatFeasibility.Clears stays the one
            // owner of coverage, so a host above the power bar is only satisfied when it also
            // damages every known defender (an unobserved site has none and is vacuously covered).
            AttackPreparationAssessment readiness = AttackPreparationReadiness.Assess(
                host, snap.Self.AttackPeak, opposition, hexBonus);
            bool coverageGap = readiness.PowerReady && !readiness.CombatFeasible;
            if (readiness.CapabilityReady)
            {
                diag.Add($"[AI][V2][Demand][Aggression] decision=SATISFIED {at}preparation_host_clears_power "
                    + $"have={have:0.#} required>{required:0.#}");
                return null;
            }
            if (ai.GatherSupportArmyIds.Count > 0)
            {
                diag.Add($"[AI][V2][Demand][Aggression] decision=SATISFIED {at}existing_supports_en_route "
                    + $"supports=[{string.Join(",", ai.GatherSupportArmyIds)}]");
                return null;
            }
            HashSet<int> claimed = commitments.ClaimedArmyIdSet;
            claimed.Remove(hostId);
            // §41 — the SAME partial gather the preparation plans with, as a capability question:
            // a free army that could raise the host (even with its MP spent this turn) is timing,
            // and one held by another accepted operation is contention — neither is production.
            if (host.MemberCount > 0)
            {
                Dictionary<int, float> donors = GroundCombatDonorPolicy.BorrowableDonorValues(
                    snap.Observer == null ? null : MissionIntentRegistry.GetOrCreate(snap.Observer).All);
                GroundCombatGatherPlan free = GroundCombatAssemblyPlanner.PlanGather(snap, opposition,
                    hexBonus, ai.Target.Hex, claimed, GroundCombatAdmissionPolicy.AttackCoverageGate,
                    hostId, donors, requireMovementNow: false, minimumArmyPower: required,
                    allowPartial: true);
                if (free.Feasible && free.SupportArmyIds.Count > 0)
                {
                    diag.Add($"[AI][V2][Demand][Aggression] decision=SATISFIED {at}existing_army_can_join_host "
                        + $"supports=[{string.Join(",", free.SupportArmyIds)}] (movement/timing, not a shortage)");
                    return null;
                }
                GroundCombatGatherPlan contended = GroundCombatAssemblyPlanner.PlanGather(snap, opposition,
                    hexBonus, ai.Target.Hex, new HashSet<int>(), GroundCombatAdmissionPolicy.AttackCoverageGate,
                    hostId, null, requireMovementNow: false, minimumArmyPower: required, allowPartial: true);
                // Contention only when the busy armies would really complete the fist; a partial
                // lift held elsewhere does not excuse a measured shortage.
                if (contended.Feasible && contended.ReachesThreshold && contended.SupportArmyIds.Count > 0)
                {
                    diag.Add($"[AI][V2][Demand][Aggression] decision=SKIP {at}preparation_supports_contended "
                        + $"physical=[{string.Join(",", contended.SupportArmyIds)}]");
                    return null;
                }
            }
            ArmyData live = snap.Observer == null ? null : AiV2Util.ResolveArmy(snap.Observer, hostId);
            if (live != null && GroundCombatAssemblyPlanner.PlanPreparationAssembly(snap, live, claimed,
                    opposition, hexBonus).Feasible)
            {
                diag.Add($"[AI][V2][Demand][Aggression] decision=SATISFIED {at}same_hex_assembly_pending");
                return null;
            }
            if (snap.Self.BaseHexes == null || !snap.Self.BaseHexes.Contains(host.Hex))
            {
                diag.Add($"[AI][V2][Demand][Aggression] decision=DEFER {at}preparation_host_not_on_own_base "
                    + $"hex=({host.Hex.Q},{host.Hex.R})");
                return null;
            }
            float deficit = Mathf.Max(AiConfigV2.allocatorSliceEpsilon,
                required - have + AiConfigV2.allocatorSliceEpsilon);
            TaskScore score = AttackObjectiveEvaluator.ForTrackedTarget(snap, ai.Target)?.TaskScore ?? default;
            diag.Add($"[AI][V2][Demand][Aggression] decision=CREATE {at}preparation_host_below_threshold "
                + $"capability=FieldCombatPower shape=Any desired={deficit:0.#} have={have:0.#} "
                + $"required>{required:0.#} coverageGap={coverageGap} roster={host.MemberCount}/{host.Capacity} task={score.Value:0.##}");
            return new AxisDemand
            {
                RequestingAxis = DesireAxis.Aggression,
                Capability = CapabilityKind.FieldCombatPower,
                DeliveryShape = CapabilityDeliveryShape.Any,
                ConsumerIntentKey = intent.IntentKey,
                ConsumerMissionKind = MissionKind.Attack,
                DesiredAmount = deficit,
                RequiredCapabilityPower = deficit,
                AttackFistArmyId = hostId,
                AttackFistIsPreparationHost = true,
                AttackCoverageGap = coverageGap,
                AttackCoverageTargetHex = coverageGap ? ai.Target.Hex : (HexCoord?)null,
                RequiredTraits = TraitPreference.None,
                MinimumFollowupAp = 0f,
                TargetHex = host.Hex,
                WorldTaskScore = score,
                Value = score.Value,
                Explain = $"attack {ai.Target.DiagnosticLabel}: preparation host #{hostId} "
                    + $"({have:0.#} of >{required:0.#}) — no existing troop can join it; strengthen "
                    + $"this exact host; task={score.Value:0.##}",
            };
        }

        // ATK-F02 — the card-borne ways the exact preparation host can still gain power, each named
        // by its witness, on the delivery policy's own rule (a legal slot and a real power gain,
        // MaterializationDeliveryPolicy.StrengthensArmy): a held Unit card (Phase A), a Research/
        // Production output of a staffed own facility (a closed investment window or short stock is
        // timing, never impossibility), an undrawn Unit card (Phase B's Draw). Null when the host
        // stands off an own Base (no card lands there) or no card strengthens it: a positive
        // Reserve alone is no delivery. Which chain actually runs stays Materialization's choice.
        internal static string PreparationHostCardSource(WorldSnapshot snap, ArmyData host,
            IReadOnlyList<StrikeRosterSlot> targetRoster = null, HexCoord? coverageTarget = null)
        {
            if (snap?.Self == null || host == null || snap.Self.BaseHexes == null
                || !snap.Self.BaseHexes.Contains(host.Hex))
                return null;
            // The same capability gate the chain enumeration applies to the preparation host's
            // FieldCombatPower demand (MaterializationChainMatching, recceMayFight): a card Phase A
            // never plays into this host is no witness.
            // 2026-10-01 (user decision) — and only a source of the strike roster: a card that
            // fills a missing position of the target roster (the preparation's frozen one, else
            // the snapshot's), or an equivalent at least as strong as the weakest missing body.
            // A body the roster does not need is never waited for, whatever slot it would take.
            IReadOnlyList<StrikeRosterSlot> target = targetRoster ?? snap.Self.StrikeRoster;
            List<StrikeRosterSlot> missing = target == null || target.Count == 0 ? null
                : StrikeRoster.Missing(target, host.Members);
            // A full host still takes a card of an exactly missing position once Housekeeping
            // releases one of its non-roster bodies (ReorgViability.PreparationRosterWaste counts
            // exactly such a held card as a pending source): that slot counts as room. An
            // equivalent card frees no slot, so for it only a real free slot counts.
            bool releasableSlot = missing != null
                && StrikeRoster.NonTargetBodies(target, host.Members).Count > 0;
            // A host that already clears the power bar but cannot damage every known defender of the
            // target is served by a card that closes target coverage — not only by one filling a
            // missing slot of the strongest-power roster.
            bool coverageGap = coverageTarget.HasValue
                && GroundCombatAdmissionPolicy.RequiresCoverage(GroundCombatAdmissionPolicy.AttackCoverageGate)
                && MaterializationDeliveryPolicy.UncoveredDefenderCount(snap, host.Members, null,
                    coverageTarget.Value) > 0;
            bool ExactlyMissing(Game.Cards.CardDefinition d) =>
                missing != null && missing.Any(m => !m.IsHero && m.Key == StrikeRoster.CardKey(d));
            bool Strengthens(Game.Cards.CardDefinition d, Game.Cards.CardDefinition equipped = null, Game.Cards.CardDefinition mutator = null) =>
                d != null && !d.isAviation
                && MaterializationChainMatching.MatchesCapabilityDef(d, CapabilityKind.FieldCombatPower)
                && MaterializationChainMatching.AbilitiesSatisfyCapability(
                    MaterializationChainMatching.EffectiveAbilities(d, equipped, mutator), d.cardType,
                    CapabilityKind.FieldCombatPower, recceMayFight: true)
                && (host.CanFitAdditionalCard(d)
                    || releasableSlot && d.cardType == Game.Cards.CardType.Unit && ExactlyMissing(d))
                && MaterializationDeliveryPolicy.StrengthensArmy(host.Members, d,
                    AiPower.EffectiveLine(d, equipped?.equipment, mutator?.equipment))
                && (coverageGap
                    ? MaterializationDeliveryPolicy.ClosesTargetCoverage(snap, host.Members, d,
                        coverageTarget.Value)
                    : StrikeRoster.FillsMissing(missing, d,
                        AiPower.EffectiveLine(d, equipped?.equipment, mutator?.equipment).BasePower));

            // A held card Phase A already failed to chain into this exact host (its pinned demand,
            // this turn or the last) is no witness: the two stages answer with one truth.
            foreach (Game.Cards.CardData c in GroundCombatDemandPolicy.HandFieldCards(snap))
                if (Strengthens(c.Definition, c.Equipment, c.Mutator)
                    && !PreparationDeliveryMemory.NoChainRecently(snap.Observer, host.Id,
                        snap.TurnNumber, StrikeRoster.CardKey(c.Definition)))
                    return $"hand_card:{c.Definition.displayName}";
            DevelopmentReadiness dev = snap.Development;
            if (dev != null)
            {
                foreach (DevelopmentOffering o in dev.Offerings)
                    if (!o.ProducesEquipment && Strengthens(o.Card))
                        return $"generation:{o.Card.displayName}@({o.FacilityHex.Q},{o.FacilityHex.R})"
                            + (DevelopmentInvestmentGate.IsOpenFor(snap.Observer, snap.TurnNumber,
                                o.Card.resourceCost) ? "" : "(window_closed)");
                // Short stock is timing only while the Challenge fits today's spendable stock plus
                // devChainFundingHorizonTurns of income (the Development PREPARE rule); a resource
                // the player does not earn keeps it out of reach, so it is no WAIT witness.
                foreach (Game.Cards.CardDefinition d in dev.StaffedOutputs)
                    if (Strengthens(d) && FundableWithinHorizon(snap, d.resourceCost))
                        return $"generation:{d.displayName}(stock_short)";
            }
            foreach (Game.Cards.CardDefinition d in snap.Self.Deck
                ?? (IReadOnlyList<Game.Cards.CardDefinition>)System.Array.Empty<Game.Cards.CardDefinition>())
                if (Strengthens(d))
                    return $"undrawn_card:{d.displayName}";
            return null;
        }

        private static bool FundableWithinHorizon(WorldSnapshot snap, Game.Cards.ResourceCost cost)
        {
            if (cost == null)
                return true;
            // The owner-aware spendable stock Analysis already netted (never the raw stockpile);
            // no Economy snapshot counts as nothing spendable.
            ResourceBundle spendable = snap.Economy?.SpendableStockpile ?? default;
            foreach (Game.Economy.ResourceType t in ResourceBundle.All)
                if (cost.Get(t) > spendable.Get(t) + AiConfigV2.devChainFundingHorizonTurns
                        * Mathf.Max(0f, snap.Self.PerTurnIncome.Get(t)) + AiConfigV2.allocatorSliceEpsilon)
                    return false;
            return true;
        }

        private static void AppendUnboundAttackDemand(WorldSnapshot snap,
            IReadOnlyList<MissionIntent> activeIntents, ActorCommitments commitments,
            List<string> diag, List<AxisDemand> demands)
        {
            // The planner owns the one-operation policy. Bound shortages/refit above remain
            // legal, but a spent mover or a different objective never authorizes a second fist.
            MissionIntent live = AggressionMissionLayer.LiveAttackOperation(activeIntents);
            if (live != null)
            {
                diag.Add($"[AI][V2][Demand][Aggression] decision=SKIP intent={live.IntentKey} "
                    + "reason=unbound_attack_owned_by_live_operation");
                return;
            }
            // Target knowledge may accumulate while closed; no operation-specific shortage
            // exists until mobilization authorizes selecting an objective. Bound work is above.
            if (!AttackForceReadiness.MobilizationOpen(snap.Self))
            {
                diag.Add("[AI][V2][Demand][Aggression] decision=HOLD reason=mobilization_closed");
                return;
            }
            AttackObjective objective = AttackObjectiveEvaluator.Enumerate(snap)
                .FirstOrDefault(o => !(activeIntents ?? System.Array.Empty<MissionIntent>()).Any(i => i != null
                        && i.Status == IntentStatus.Active && i.Kind == MissionKind.Attack
                        && i.Attack != null && i.Attack.Target.Equals(o.Target)));
            if (objective == null)
                return;
            ISet<int> claimed = commitments?.ClaimedArmyIdSet;
            float hexBonus = AttackObjectiveEvaluator.KnownSiteDefenceBonus(snap, null, objective.Hex);
            GroundCombatAssemblyPlan plan = GroundCombatAssemblyPlanner.Plan(snap,
                new GroundCombatAssemblyRequest
                {
                    Opposition = objective.Opposition,
                    WinChanceGate = GroundCombatAdmissionPolicy.AttackCoverageGate,
                    MinimumArmyPower = AttackForceReadiness.RequiredPower(snap.Self.AttackPeak),
                    ExcludedArmyIds = claimed,
                    DefenderHexDefenseBonus = hexBonus,
                });
            if (plan.Feasible)
            {
                diag.Add($"[AI][V2][Demand][Aggression] decision=SATISFIED target={objective.Target.DiagnosticLabel} "
                    + $"actor={plan.BaseArmyId} win={plan.ProjectedWinChance:0.00} "
                    + "reason=unbound_attack_takeable_by_existing_force");
                return;
            }
            // This is a capability question, not a nomination for a move this turn. A
            // sufficient field army with spent MP is available again next turn.
            ArmySnapshot futureActor = GroundCombatActorEligibility.EligibleArmies(snap, claimed,
                    requireMovementNow: false)
                .Where(a => a.CurrentMovement <= 0)
                .FirstOrDefault(a => GroundCombatAssemblyPlanner.PlanForArmyAtThreshold(snap,
                    objective.Opposition, a.ArmyId,
                    GroundCombatAdmissionPolicy.AttackCoverageGate, hexBonus).Feasible
                    && AttackForceReadiness.ForceReady(a.EffectiveArmyPower,
                        snap.Self.AttackPeak));
            if (futureActor != null)
            {
                diag.Add($"[AI][V2][Demand][Aggression] decision=SATISFIED target={objective.Target.DiagnosticLabel} "
                    + $"actor={futureActor.ArmyId} reason=unbound_attack_existing_force_waits_for_movement");
                return;
            }
            GroundCombatGatherPlan gather = GroundCombatAssemblyPlanner.PlanGather(snap,
                objective.Opposition, hexBonus, objective.Hex, claimed,
                GroundCombatAdmissionPolicy.AttackCoverageGate,
                donorValues: GroundCombatDonorPolicy.BorrowableDonorValues(activeIntents),
                requireMovementNow: false,
                minimumArmyPower: AttackForceReadiness.RequiredPower(snap.Self.AttackPeak));
            if (gather.Feasible)
            {
                diag.Add($"[AI][V2][Demand][Aggression] decision=SATISFIED target={objective.Target.DiagnosticLabel} "
                    + $"host={gather.HostArmyId} win={gather.ProjectedWinChance:0.00} "
                    + "reason=unbound_attack_takeable_by_a_gather");
                return;
            }

            // §41 — an army committed elsewhere that could take the site is actor contention, the
            // allocator's problem, never a capability shortage for Production to buy.
            GroundCombatAssemblyPlan contended = GroundCombatAssemblyPlanner.Plan(snap,
                new GroundCombatAssemblyRequest
                {
                    Opposition = objective.Opposition,
                    WinChanceGate = GroundCombatAdmissionPolicy.AttackCoverageGate,
                    MinimumArmyPower = AttackForceReadiness.RequiredPower(snap.Self.AttackPeak),
                    ExcludedArmyIds = new HashSet<int>(),
                    DefenderHexDefenseBonus = hexBonus,
                });
            if (contended.Feasible)
            {
                diag.Add($"[AI][V2][Demand][Aggression] decision=SKIP target={objective.Target.DiagnosticLabel} "
                    + $"physicalActor=#{contended.BaseArmyId} reason=unbound_attack_mover_contended");
                return;
            }

            // T04 — the measured shortage is published whatever the hand holds: which source can
            // cover it (a held card, an allowed generated output, or no legal chain) is
            // MaterializationChainEnumerator's decision in Phase A. A card still in the remaining
            // deck stays Phase B's Draw work; Draw never stands in for a generated output.

            ArmySnapshot fist = snap.Self.Armies?
                .Where(a => a != null && a.IsStructuralRaidActor
                    && snap.Self.BaseHexes.Contains(a.Hex)
                    && (claimed == null || !claimed.Contains(a.ArmyId)))
                .OrderByDescending(a => a.EffectiveArmyPower).ThenBy(a => a.ArmyId)
                .FirstOrDefault();
            if (fist == null)
            {
                // T01 — with no free fist on an own Base the mobilization preparation is the path
                // (it creates/reuses the host on the Citadel); this demand names nobody.
                bool open = AttackForceReadiness.MobilizationOpen(snap.Self);
                diag.Add($"[AI][V2][Demand][Aggression] decision=DEFER target={objective.Target.DiagnosticLabel} "
                    + "reason=no_free_base_fist_for_direct_card_delivery "
                    + $"mobilization={(open ? "open:preparation_owns_host" : "closed")} "
                    + $"deployed={snap.Self.DeployedPower:0.#} available={snap.Self.AvailablePower:0.#}");
                return;
            }
            float required = AttackForceReadiness.RequiredPower(snap.Self.AttackPeak);
            float have = fist?.EffectiveArmyPower ?? 0f;
            // §11 — a fist that already has the numbers yet misses the gate is an assembly /
            // composition gap: strengthening it by a phantom +1 from hand closes nothing.
            if (AttackForceReadiness.ForceReady(have, snap.Self.AttackPeak))
            {
                diag.Add($"[AI][V2][Demand][Aggression] decision=SKIP target={objective.Target.DiagnosticLabel} "
                    + $"fist={(fist?.ArmyId ?? 0)} required={required:0.#} have={have:0.#} hexDef={hexBonus:0.#} "
                    + "reason=unbound_attack_power_suffices_gate_missed");
                return;
            }
            float deficit = Mathf.Max(AiConfigV2.allocatorSliceEpsilon, required - have
                + AiConfigV2.allocatorSliceEpsilon);
            TaskScore score = objective.TaskScore;
            diag.Add($"[AI][V2][Demand][Aggression] decision=CREATE target={objective.Target.DiagnosticLabel} "
                + $"capability=FieldCombatPower shape=Any desired={deficit:0.#} fist={(fist?.ArmyId ?? 0)} "
                + $"required={required:0.#} have={have:0.#} hexDef={hexBonus:0.#} "
                + $"task={score.Value:0.##} reason=unbound_attack_force_below_threshold");
            demands.Add(new AxisDemand
            {
                RequestingAxis = DesireAxis.Aggression,
                Capability = CapabilityKind.FieldCombatPower,
                DeliveryShape = CapabilityDeliveryShape.Any,
                ConsumerMissionKind = MissionKind.Attack,
                DesiredAmount = deficit,
                RequiredCapabilityPower = deficit,
                AttackFistArmyId = fist.ArmyId,
                RequiredTraits = TraitPreference.None,
                MinimumFollowupAp = 0f,
                TargetHex = fist?.Hex,
                WorldTaskScore = score,
                Value = score.Value,
                Explain = $"attack {objective.Target.DiagnosticLabel}: no legal force exceeds the current 80% threshold "
                    + $"({have:0.#} of {required:0.#}, hex defence {hexBonus:0.#}); strengthen the fist "
                    + $"#{(fist?.ArmyId ?? 0)} from hand; task={score.Value:0.##}",
            });
        }
    }
}

