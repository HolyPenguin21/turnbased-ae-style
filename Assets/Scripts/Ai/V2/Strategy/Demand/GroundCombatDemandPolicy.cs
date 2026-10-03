using System.Collections.Generic;
using System.Linq;
using Game.Players;
using UnityEngine;

using Game.Combat;

namespace Game.Ai.V2
{
    internal static class GroundCombatDemandPolicy
    {
        // §41 — the ONE "does a bound ground-combat primary need a NEW support army" chain, shared
        // by Raid and Attack. It is NOT a shortage while the primary still clears its gate, a
        // support is already bound or was requested this turn, or an EXISTING free army could join
        // it (Missions/Provisioning bind that one through the batch solve). Power that exists but
        // cannot physically be delivered as a separate army is a bounded DEFER, never a phantom
        // claim. Otherwise: one IndependentFieldArmy demand sized to the deficit, carrying the
        // objective's canonical TaskScore (no synthetic lifecycle value). Returns null when no
        // demand is due; every decision is written to `diag`.
        // A demand is sized with the SAME hex defence its gate was evaluated with
        // (GroundCombatFeasibility.RequiredPower owns the formula): sized without it, it
        // under-asks exactly on fortified targets, where the estimator rejected the fight.
        internal static float RequiredSitePower(IReadOnlyList<WorthIt.DefendingArmy> opposition,
            float hexBonus) =>
            GroundCombatFeasibility.RequiredPower(WorthIt.UnitsOf(opposition), hexBonus);

        internal static AxisDemand BoundPrimaryShortage(WorldSnapshot snap, CapabilityInventory inv,
            ActorCommitments commitments, MissionIntent intent, MissionKind consumerKind,
            string targetLabel, int primaryId, int? supportArmyId, int reinforcementRequestedTurn,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, float hexBonus, float gate,
            System.Func<TaskScore> objectiveScore, List<string> diag,
            float minimumArmyPower = 0f)
        {
            string at = $"intent={intent.IntentKey} target={targetLabel} primary={primaryId}";
            GroundCombatAssemblyPlan primaryPlan = GroundCombatAssemblyPlanner.PlanForArmyAt(
                snap, opposition, primaryId, gate, hexBonus);
            if (primaryPlan.Feasible && (minimumArmyPower <= 0f
                || primaryPlan.ProjectedPower > minimumArmyPower))
            {
                diag.Add($"[AI][V2][Demand][Aggression] decision=SATISFIED {at} "
                    + $"win={primaryPlan.ProjectedWinChance:0.00} reason=primary_clears_its_gate");
                return null;
            }
            if (supportArmyId.HasValue)
            {
                diag.Add($"[AI][V2][Demand][Aggression] decision=SATISFIED {at} "
                    + $"support={supportArmyId.Value} reason=reinforcement_already_assigned_or_en_route");
                return null;
            }
            // T06 — no support army built from the known pool could ever cover this opposition:
            // asking for one would be an endless target-specific request. The operation keeps its
            // own lifecycle (Continuity's recovery/return rules decide what the primary does).
            if (CombatOpportunityAnalyzer.ProvenUncoverableWithinKnownPool(snap, opposition, hexBonus,
                    out string unreachable))
            {
                diag.Add($"[AI][V2][Demand][Aggression] decision=SKIP {at} "
                    + $"reason=proven_unreachable_within_known_pool detail=\"{unreachable}\"");
                return null;
            }
            if (reinforcementRequestedTurn == snap.TurnNumber)
            {
                diag.Add($"[AI][V2][Demand][Aggression] decision=SATISFIED {at} "
                    + "reason=reinforcement_already_requested_this_turn");
                return null;
            }
            List<int> existing = GroundCombatAssemblyPlanner.ReinforcementSupportCandidates(
                snap, primaryId, opposition, commitments?.ClaimedArmyIdSet, hexBonus,
                allowCommandHandover: consumerKind == MissionKind.Attack);
            if (existing.Count > 0)
            {
                diag.Add($"[AI][V2][Demand][Aggression] decision=SATISFIED {at} "
                    + $"candidates={existing.Count} reason=existing_free_army_available_as_support");
                return null;
            }

            ArmySnapshot primary = snap.Self.Armies?.FirstOrDefault(a => a != null && a.ArmyId == primaryId);
            float have = primary?.EffectiveArmyPower ?? 0f;
            float required = minimumArmyPower > 0f ? minimumArmyPower : RequiredSitePower(opposition, hexBonus);
            // §11 — enough numeric power that still misses the estimator's gate is a composition
            // gap more power cannot close; it never becomes a phantom +1 FieldCombatPower.
            if (minimumArmyPower <= 0f && required - have <= AiConfigV2.allocatorSliceEpsilon)
            {
                diag.Add($"[AI][V2][Demand][Aggression] decision=DEFER {at} "
                    + $"reason=primary_power_suffices_gate_missed required={required:0.#} have={have:0.#} "
                    + $"hexDef={hexBonus:0.#}");
                return null;
            }
            float deficit = minimumArmyPower > 0f
                ? Mathf.Max(AiConfigV2.allocatorSliceEpsilon,
                    required - have + AiConfigV2.allocatorSliceEpsilon)
                : required - have;
            if (!CanDeliverIndependentFieldArmy(snap, inv))
            {
                diag.Add($"[AI][V2][Demand][Aggression] decision=DEFER {at} "
                    + $"reason=no_independent_field_army_deliverable deficit={deficit:0.#} "
                    + $"freePower={inv.RaidAvailableFieldPower:0.#} shells={inv.ReusableEmptyArmies.Count}");
                return null;
            }

            TaskScore score = objectiveScore();
            string lane = consumerKind.ToString().ToLowerInvariant();
            string rendezvous = $"({(primary?.Hex.Q ?? 0)},{(primary?.Hex.R ?? 0)})";
            diag.Add($"[AI][V2][Demand][Aggression] decision=CREATE {at} capability=FieldCombatPower "
                + $"shape=IndependentFieldArmy desired={deficit:0.#} required={required:0.#} "
                + $"have={have:0.#} hexDef={hexBonus:0.#} task={score.Value:0.##} rendezvous={rendezvous} "
                + "reason=bound_primary_needs_separate_support_army");
            return new AxisDemand
            {
                RequestingAxis = DesireAxis.Aggression,
                Capability = CapabilityKind.FieldCombatPower,
                DeliveryShape = CapabilityDeliveryShape.IndependentFieldArmy,
                ConsumerIntentKey = intent.IntentKey,
                ConsumerMissionKind = consumerKind,
                DesiredAmount = deficit,
                RequiredCapabilityPower = deficit,
                RequiredTraits = TraitPreference.None,
                MinimumFollowupAp = 0f,
                TargetHex = primary?.Hex,
                WorldTaskScore = score,
                Value = score.Value,
                Explain = $"{lane} {targetLabel}: primary #{primaryId} no longer clears its gate "
                    + $"({have:0.#} of {required:0.#} needed, hex defence {hexBonus:0.#}); deliver "
                    + $"~{deficit:0.#} field power as a SEPARATE support army to {rendezvous}; "
                    + $"task={score.Value:0.##}",
            };
        }

        // §6 — "is there, in principle, a way to physically field a SEPARATE support army": a free
        // ready field army, a reusable empty shell, a deployable unit/hero card in hand, or (T04)
        // a Research/Production output that mints a ground Unit/Hero this turn — the snapshot's
        // GenerationSource offerings, inside the investment window the real chain is held to.
        // Which of them actually covers the shortage stays MaterializationChainEnumerator's
        // choice. Pure read; it never claims anything. When this is false the caller DEFERS
        // (bounded) instead of inventing a phantom power request nothing could ever satisfy.
        internal static bool CanDeliverIndependentFieldArmy(WorldSnapshot snap, CapabilityInventory inv)
        {
            if (inv != null && inv.RaidAvailableFieldPower > AiConfigV2.allocatorSliceEpsilon)
                return true;
            if (inv != null && inv.ReusableEmptyArmies != null && inv.ReusableEmptyArmies.Count > 0)
                return true;
            if (HandFieldCards(snap).Any())
                return true;
            return GroundGenerationOffers(snap).Any();
        }

        // The held Unit/Hero cards CanDeliverIndependentFieldArmy counts — the only hand input of
        // the Aggression demand, so the Aggression admission fingerprint keys on exactly these.
        internal static IEnumerable<Game.Cards.CardData> HandFieldCards(WorldSnapshot snap)
        {
            foreach (Game.Cards.CardData c in snap?.Self?.Hand
                ?? (IReadOnlyList<Game.Cards.CardData>)System.Array.Empty<Game.Cards.CardData>())
            {
                Game.Cards.CardType? t = c?.Definition?.cardType;
                if (t == Game.Cards.CardType.Unit || t == Game.Cards.CardType.Hero)
                    yield return c;
            }
        }

        // The Research/Production offerings that can mint a ground Unit/Hero now (T04). Read by
        // CanDeliverIndependentFieldArmy and by the Aggression admission fingerprint.
        internal static IEnumerable<DevelopmentOffering> GroundGenerationOffers(WorldSnapshot snap)
        {
            foreach (DevelopmentOffering o in snap?.Development?.Offerings
                ?? (IReadOnlyList<DevelopmentOffering>)System.Array.Empty<DevelopmentOffering>())
            {
                Game.Cards.CardDefinition def = o.Card;
                if (!o.ProducesEquipment && def != null && !def.isAviation
                    && (def.cardType == Game.Cards.CardType.Unit || def.cardType == Game.Cards.CardType.Hero)
                    && DevelopmentInvestmentGate.IsOpenFor(snap.Observer, snap.TurnNumber, def.resourceCost))
                    yield return o;
            }
        }
    }
}
