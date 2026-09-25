using System.Collections.Generic;
using System.Linq;
using Game.Combat;
using Game.HexGrid;
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
    //      clears Attack's floor against the target site — the same gate the phase machine turns
    //      to Reinforcement on (one owner), so a delivered support is really used — and no
    //      existing free army could fix that by joining it;
    //    * strike force step 4 — the best known Base/Citadel objective that no army can take even
    //      at Attack's floor, while cards in hand could still strengthen the fist
    //      (AppendUnboundAttackDemand). The target is a real, known structure, so this names a
    //      real objective, not an invented war (§43).
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

                IReadOnlyList<WorthIt.DefendingArmy> opposition =
                    AttackObjectiveEvaluator.KnownSiteOpposition(snap, ai.Target.Hex);
                List<WorthIt.DefenderProfile> defenders = WorthIt.UnitsOf(opposition);
                float hexBonus = AttackObjectiveEvaluator.KnownSiteDefenceBonus(
                    snap, null, ai.Target.Hex);

                GroundCombatAssemblyPlan primaryPlan = GroundCombatAssemblyPlanner.PlanForArmyAt(
                    snap, opposition, primaryId, GroundCombatAdmissionPolicy.AttackWinChanceFloor,
                    hexBonus);
                if (primaryPlan.Feasible)
                {
                    diag.Add($"[AI][V2][Demand][Aggression] decision=SATISFIED intent={i.IntentKey} "
                        + $"target={ai.Target.DiagnosticLabel} primary={primaryId} "
                        + $"win={primaryPlan.ProjectedWinChance:0.00} "
                        + "reason=primary_clears_worthit_against_attack_site");
                    continue;
                }

                if (ai.SupportArmyId.HasValue)
                {
                    diag.Add($"[AI][V2][Demand][Aggression] decision=SATISFIED intent={i.IntentKey} "
                        + $"target={ai.Target.DiagnosticLabel} primary={primaryId} "
                        + $"support={ai.SupportArmyId.Value} "
                        + "reason=reinforcement_already_assigned_or_en_route");
                    continue;
                }

                if (ai.ReinforcementRequestedTurn == snap.TurnNumber)
                {
                    diag.Add($"[AI][V2][Demand][Aggression] decision=SATISFIED intent={i.IntentKey} "
                        + "reason=reinforcement_already_requested_this_turn");
                    continue;
                }

                // §41 — an EXISTING free army may already fix this. Nothing needs materialising.
                List<int> existing = GroundCombatAssemblyPlanner.ReinforcementSupportCandidates(
                    snap, primaryId, opposition, commitments.ClaimedArmyIdSet, hexBonus);
                if (existing.Count > 0)
                {
                    diag.Add($"[AI][V2][Demand][Aggression] decision=SATISFIED intent={i.IntentKey} "
                        + $"target={ai.Target.DiagnosticLabel} primary={primaryId} "
                        + $"candidates={existing.Count} "
                        + "reason=existing_free_army_available_as_support");
                    continue;
                }

                ArmySnapshot primary = snap.Self.Armies?.FirstOrDefault(a => a != null
                    && a.ArmyId == primaryId);
                float sitePower = AiPower.EffectiveArmyPowerFromProfiles(defenders);
                float required = Mathf.Max(1f, sitePower * AiConfigV2.raidCombatPowerMargin);
                float deficit = Mathf.Max(1f, required - (primary?.EffectiveArmyPower ?? 0f));

                // If the power exists numerically but cannot PHYSICALLY be delivered as a separate
                // army, that is a bounded DEFER, never a phantom capability claim.
                if (!CanDeliverIndependentFieldArmy(snap, inv))
                {
                    diag.Add($"[AI][V2][Demand][Aggression] decision=DEFER intent={i.IntentKey} "
                        + $"target={ai.Target.DiagnosticLabel} primary={primaryId} "
                        + $"reason=no_independent_field_army_deliverable deficit={deficit:0.#}");
                    continue;
                }

                AttackObjective objective = AttackObjectiveEvaluator.ForTrackedTarget(snap, ai.Target);
                TaskScore score = objective?.TaskScore ?? default;

                diag.Add($"[AI][V2][Demand][Aggression] decision=CREATE intent={i.IntentKey} "
                    + $"target={ai.Target.DiagnosticLabel} capability=FieldCombatPower "
                    + $"shape=IndependentFieldArmy desired={deficit:0.#} primary={primaryId} "
                    + $"required={required:0.#} have={(primary?.EffectiveArmyPower ?? 0f):0.#} "
                    + $"hexDef={hexBonus:0.#} task={score.Value:0.##} "
                    + "reason=attack_primary_cannot_clear_known_site_defence");
                demands.Add(new AxisDemand
                {
                    RequestingAxis = DesireAxis.Aggression,
                    Capability = CapabilityKind.FieldCombatPower,
                    DeliveryShape = CapabilityDeliveryShape.IndependentFieldArmy,
                    ConsumerIntentKey = i.IntentKey,
                    ConsumerMissionKind = MissionKind.Attack,
                    DesiredAmount = deficit,
                    RequiredCapabilityPower = deficit,
                    RequiredTraits = TraitPreference.None,
                    MinimumFollowupAp = 0f,
                    TargetHex = primary?.Hex,
                    // The same world objective, so the same canonical intrinsic TaskScore. No
                    // synthetic lifecycle value is injected into this transport.
                    WorldTaskScore = score,
                    Value = score.Value,
                    Explain = $"attack {ai.Target.DiagnosticLabel}: primary #{primaryId} no longer clears "
                        + $"WorthIt against the site ({(primary?.EffectiveArmyPower ?? 0f):0.#} of "
                        + $"{required:0.#} needed, hex defence {hexBonus:0.#}); deliver ~{deficit:0.#} "
                        + $"field power as a SEPARATE support army; task={score.Value:0.##}",
                });
            }

            AppendUnboundAttackDemand(snap, activeIntents, commitments, diag, demands);
            AppendHeldBaseGarrisonDemands(snap, diag, demands);
        }

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
                TaskScore score = new TaskScore(strategicRelevance: TaskScoreEvaluator.StrategicRelevance(
                    AiConfigV2.assetValueBase / Mathf.Max(1f, AiConfigV2.assetValueCitadel)));
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
        // same-hex package nor cross-hex gather can take it even at Attack's floor. The fist (the strongest free
        // field army) is what the hand should strengthen, and only a hand that can actually add
        // to the best stack (BestStackPotential > FieldPotential) is asked.
        private static void AppendUnboundAttackDemand(WorldSnapshot snap,
            IReadOnlyList<MissionIntent> activeIntents, ActorCommitments commitments,
            List<string> diag, List<AxisDemand> demands)
        {
            AttackObjective objective = AttackObjectiveEvaluator.Enumerate(snap)
                .FirstOrDefault(o => o.Target.Kind != AttackTargetKind.Facility
                    && !(activeIntents ?? System.Array.Empty<MissionIntent>()).Any(i => i != null
                        && i.Status == IntentStatus.Active && i.Kind == MissionKind.Attack
                        && i.Attack != null && i.Attack.Target.Equals(o.Target)));
            if (objective == null)
                return;
            if (snap.Self.BestStackPotential <= snap.Self.FieldPotential + AiConfigV2.allocatorSliceEpsilon)
            {
                diag.Add($"[AI][V2][Demand][Aggression] decision=SKIP target={objective.Target.DiagnosticLabel} "
                    + "reason=unbound_attack_hand_adds_nothing_to_the_fist");
                return;
            }

            ISet<int> claimed = commitments?.ClaimedArmyIdSet;
            float hexBonus = AttackObjectiveEvaluator.KnownSiteDefenceBonus(snap, null, objective.Hex);
            GroundCombatAssemblyPlan plan = GroundCombatAssemblyPlanner.Plan(snap,
                new GroundCombatAssemblyRequest
                {
                    Opposition = objective.Opposition,
                    WinChanceGate = GroundCombatAdmissionPolicy.AttackWinChanceFloor,
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
            GroundCombatGatherPlan gather = GroundCombatAssemblyPlanner.PlanGather(snap,
                objective.Opposition, hexBonus, objective.Hex, claimed,
                GroundCombatAdmissionPolicy.AttackWinChanceFloor,
                donorApPrices: GroundCombatDonorPolicy.BorrowableDonorApPrices(activeIntents));
            if (gather.Feasible)
            {
                diag.Add($"[AI][V2][Demand][Aggression] decision=SATISFIED target={objective.Target.DiagnosticLabel} "
                    + $"host={gather.HostArmyId} win={gather.ProjectedWinChance:0.00} "
                    + "reason=unbound_attack_takeable_by_a_gather");
                return;
            }

            ArmySnapshot fist = snap.Self.Armies?
                .Where(a => a != null && a.IsStructuralRaidActor
                    && (claimed == null || !claimed.Contains(a.ArmyId)))
                .OrderByDescending(a => a.EffectiveArmyPower).ThenBy(a => a.ArmyId)
                .FirstOrDefault();
            float required = Mathf.Max(1f, objective.TargetPower * AiConfigV2.raidCombatPowerMargin);
            float deficit = Mathf.Max(1f, required - (fist?.EffectiveArmyPower ?? 0f));
            TaskScore score = objective.TaskScore;
            diag.Add($"[AI][V2][Demand][Aggression] decision=CREATE target={objective.Target.DiagnosticLabel} "
                + $"capability=FieldCombatPower shape=Any desired={deficit:0.#} fist={(fist?.ArmyId ?? 0)} "
                + $"required={required:0.#} have={(fist?.EffectiveArmyPower ?? 0f):0.#} "
                + $"task={score.Value:0.##} reason=unbound_attack_no_force_clears_the_floor");
            demands.Add(new AxisDemand
            {
                RequestingAxis = DesireAxis.Aggression,
                Capability = CapabilityKind.FieldCombatPower,
                DeliveryShape = CapabilityDeliveryShape.Any,
                ConsumerMissionKind = MissionKind.Attack,
                DesiredAmount = deficit,
                RequiredCapabilityPower = deficit,
                RequiredTraits = TraitPreference.None,
                MinimumFollowupAp = 0f,
                TargetHex = fist?.Hex,
                WorldTaskScore = score,
                Value = score.Value,
                Explain = $"attack {objective.Target.DiagnosticLabel}: no force clears the Attack floor "
                    + $"({(fist?.EffectiveArmyPower ?? 0f):0.#} of {required:0.#}); strengthen the fist "
                    + $"#{(fist?.ArmyId ?? 0)} from hand; task={score.Value:0.##}",
            });
        }
    }
}
