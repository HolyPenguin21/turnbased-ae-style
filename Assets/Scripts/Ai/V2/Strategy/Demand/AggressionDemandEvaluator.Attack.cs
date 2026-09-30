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
    //  ATK §41/§43/§75 — ATTACK CAPABILITY SHORTAGE.
    //
    //  A mechanical partial of the existing Aggression demand owner. Two things may create a
    //  demand here:
    //    * a PROVEN structural shortage of a bound, live Attack operation: its primary no longer
    //      cannot reach the current force threshold or cover the known defenders — the same gate the phase machine turns
    //      to Reinforcement on (one owner), so a delivered support is really used — and no
    //      existing free army could fix that by joining it;
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

                // Only a BOUND operation may ask for anything. Without a claimed primary there is
                // no proven obligation yet — the fresh-objective path owns that case.
                if (!ai.PrimaryArmyId.HasValue || commitments == null
                    || !commitments.IsArmyClaimed(ai.PrimaryArmyId.Value))
                    continue;
                int primaryId = ai.PrimaryArmyId.Value;

                AxisDemand attackShortage = BoundPrimaryShortage(snap, inv, commitments, i,
                    MissionKind.Attack, ai.Target.DiagnosticLabel, primaryId, ai.SupportArmyId,
                    ai.ReinforcementRequestedTurn,
                    AttackObjectiveEvaluator.KnownSiteOpposition(snap, ai.Target.Hex),
                    AttackObjectiveEvaluator.KnownSiteDefenceBonus(snap, null, ai.Target.Hex),
                    // Coverage is independent of the dynamic force requirement.
                    GroundCombatAdmissionPolicy.AttackCoverageGate,
                    () => AttackObjectiveEvaluator.ForTrackedTarget(snap, ai.Target)?.TaskScore ?? default,
                    diag, ai.AssaultStarted ? 0f : 0.80f * snap.Self.TotalMilitaryPotential);
                if (attackShortage != null)
                    demands.Add(attackShortage);
            }

            AppendUnboundAttackDemand(snap, activeIntents, commitments, diag, demands);
            AppendHeldBaseGarrisonDemands(snap, diag, demands);
        }

        private static TaskScore BuildHeldBaseGarrisonScore() =>
            new TaskScore(
                strategicRelevance: TaskScoreEvaluator.StrategicRelevance(
                    AiConfigV2.assetValueBase / Mathf.Max(1f, AiConfigV2.assetValueCitadel)));

        // Strike force step 7 — a base our field army holds (the fist that just took it) whose
        // garrison is below its non-hero floor is garrisoned from hand first. If the hand cannot
        // deliver, Housekeeping fills the floor at turn end from the holding army's most wounded,
        // then weakest, body (ArmyReorganizationCandidates, garrison fill).
        private static void AppendHeldBaseGarrisonDemands(WorldSnapshot snap, List<string> diag,
            List<AxisDemand> demands)
        {
            IReadOnlyList<ArmySnapshot> armies = snap.Self.Armies
                ?? (IReadOnlyList<ArmySnapshot>)System.Array.Empty<ArmySnapshot>();
            foreach (HexCoord baseHex in snap.Self.BaseHexes ?? (IReadOnlyList<HexCoord>)System.Array.Empty<HexCoord>())
            {
                ArmySnapshot garrison = armies.FirstOrDefault(a => a != null && a.IsGarrison
                    && a.Hex.Equals(baseHex));
                if (garrison == null
                    || !armies.Any(a => a != null && a.IsStructuralRaidActor && a.Hex.Equals(baseHex)))
                    continue;
                int floor = baseHex.Equals(snap.Self.Citadel)
                    ? AiConfig.secureCitadelMinNonHeroUnits : AiConfig.secureBaseMinNonHeroUnits;
                int missing = floor - (garrison.Members?.Count ?? 0);
                if (missing <= 0)
                    continue;
                float desired = missing * AiConfigV2.combatPowerPerBodyEstimate;
                TaskScore score = BuildHeldBaseGarrisonScore();
                diag.Add($"[AI][V2][Demand][Aggression] decision=CREATE base=({baseHex.Q},{baseHex.R}) "
                    + $"capability=FieldCombatPower shape=Garrison missing={missing} desired={desired:0.#} "
                    + $"task={score.Value:0.##} reason=held_base_garrison_below_floor");
                demands.Add(new AxisDemand
                {
                    RequestingAxis = DesireAxis.Aggression,
                    Capability = CapabilityKind.FieldCombatPower,
                    DeliveryShape = CapabilityDeliveryShape.Garrison,
                    ConsumerMissionKind = MissionKind.Attack,
                    DesiredAmount = desired,
                    RequiredCapabilityPower = desired,
                    RequiredTraits = TraitPreference.None,
                    MinimumFollowupAp = 0f,
                    TargetHex = baseHex,
                    WorldTaskScore = score,
                    Value = score.Value,
                    Explain = $"garrison the held base ({baseHex.Q},{baseHex.R}) from hand: "
                        + $"{missing} body short of its floor {floor}; task={score.Value:0.##}",
                });
            }
        }

        // Strike force step 4 — the Attack objective with no operation yet. Only the best known
        // Base/Citadel (the same TaskScore the mission layer ranks by) and only when no free army,
        // same-hex package nor cross-hex gather can take it above the current force threshold.
        // A card already in hand can strengthen the free fist through Phase A; an undrawn card
        // remains Phase B's Draw responsibility.
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
            if (!ai.PrimaryArmyId.HasValue || commitments == null
                || !commitments.IsPreparationHost(ai.PrimaryArmyId.Value))
            {
                diag.Add($"[AI][V2][Demand][Aggression] decision=SKIP {at}preparation_host_not_claimed");
                return null;
            }
            int hostId = ai.PrimaryArmyId.Value;
            ArmySnapshot host = snap.Self.Armies?.FirstOrDefault(a => a != null && a.ArmyId == hostId);
            float required = 0.80f * snap.Self.TotalMilitaryPotential;
            float have = host?.EffectiveArmyPower ?? 0f;
            if (host == null || have > required)
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
            IReadOnlyList<WorthIt.DefendingArmy> opposition =
                AttackObjectiveEvaluator.KnownSiteOpposition(snap, ai.Target.Hex);
            float hexBonus = AttackObjectiveEvaluator.KnownSiteDefenceBonus(snap, null, ai.Target.Hex);
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
                + $"required>{required:0.#} roster={host.MemberCount}/{host.Capacity} task={score.Value:0.##}");
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
        internal static string PreparationHostCardSource(WorldSnapshot snap, ArmyData host)
        {
            if (snap?.Self == null || host == null || snap.Self.BaseHexes == null
                || !snap.Self.BaseHexes.Contains(host.Hex))
                return null;
            bool Strengthens(Game.Cards.CardDefinition d, Game.Cards.CardDefinition equipped = null) =>
                d != null && !d.isAviation
                && (d.cardType == Game.Cards.CardType.Unit || d.cardType == Game.Cards.CardType.Hero)
                && host.CanFitAdditionalCard(d)
                && MaterializationDeliveryPolicy.StrengthensArmy(host.Members, d,
                    AiPower.EffectiveLine(d, equipped?.equipment));

            foreach (Game.Cards.CardData c in HandFieldCards(snap))
                if (Strengthens(c.Definition, c.Equipment))
                    return $"hand_card:{c.Definition.displayName}";
            DevelopmentReadiness dev = snap.Development;
            if (dev != null)
            {
                foreach (DevelopmentOffering o in dev.Offerings)
                    if (!o.ProducesEquipment && Strengthens(o.Card))
                        return $"generation:{o.Card.displayName}@({o.FacilityHex.Q},{o.FacilityHex.R})"
                            + (DevelopmentInvestmentGate.IsOpenFor(snap.Observer, snap.TurnNumber,
                                o.Card.resourceCost) ? "" : "(window_closed)");
                foreach (Game.Cards.CardDefinition d in dev.StaffedOutputs)
                    if (Strengthens(d))
                        return $"generation:{d.displayName}(stock_short)";
            }
            foreach (Game.Cards.CardDefinition d in snap.Self.Deck
                ?? (IReadOnlyList<Game.Cards.CardDefinition>)System.Array.Empty<Game.Cards.CardDefinition>())
                if (Strengthens(d))
                    return $"undrawn_card:{d.displayName}";
            return null;
        }

        private static void AppendUnboundAttackDemand(WorldSnapshot snap,
            IReadOnlyList<MissionIntent> activeIntents, ActorCommitments commitments,
            List<string> diag, List<AxisDemand> demands)
        {
            // T01 — one fist at a time: a live preparation owns the shortage (its own pinned
            // demand above); a second free-fist request would dilute the force.
            MissionIntent preparing = (activeIntents ?? System.Array.Empty<MissionIntent>())
                .FirstOrDefault(i => i != null && i.Status == IntentStatus.Active
                    && i.Kind == MissionKind.Attack && i.Attack != null && i.Attack.Preparation
                    && i.Attack.Phase == AttackMissionPhase.Gather);
            if (preparing != null)
            {
                diag.Add($"[AI][V2][Demand][Aggression] decision=SKIP intent={preparing.IntentKey} "
                    + "reason=unbound_attack_owned_by_live_preparation");
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
                    MinimumArmyPower = 0.80f * snap.Self.TotalMilitaryPotential,
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
                    && AttackObjectiveEvaluator.ForceReady(a.EffectiveArmyPower,
                        snap.Self.TotalMilitaryPotential));
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
                minimumArmyPower: 0.80f * snap.Self.TotalMilitaryPotential);
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
                    MinimumArmyPower = 0.80f * snap.Self.TotalMilitaryPotential,
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
                bool open = AttackObjectiveEvaluator.MobilizationOpen(snap.Self.DeployedPower,
                    snap.Self.AvailablePower);
                diag.Add($"[AI][V2][Demand][Aggression] decision=DEFER target={objective.Target.DiagnosticLabel} "
                    + "reason=no_free_base_fist_for_direct_card_delivery "
                    + $"mobilization={(open ? "open:preparation_owns_host" : "closed")} "
                    + $"deployed={snap.Self.DeployedPower:0.#} available={snap.Self.AvailablePower:0.#}");
                return;
            }
            float required = 0.80f * snap.Self.TotalMilitaryPotential;
            float have = fist?.EffectiveArmyPower ?? 0f;
            // §11 — a fist that already has the numbers yet misses the gate is an assembly /
            // composition gap: strengthening it by a phantom +1 from hand closes nothing.
            if (have > required)
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
