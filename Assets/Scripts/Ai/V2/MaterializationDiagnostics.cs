using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // Diagnostics-only probe. It never chooses or executes a plan and therefore cannot diverge
    // strategic policy; it reports which broad gate prevented Phase A from finding any chain.
    internal static class MaterializationDiagnostics
    {
        public static string ExplainNoChain(WorldSnapshot snap, PlayerSetupData player, PlayerRoot root,
            AiHandData hand, AiTurnContext ctx, AxisDemand demand, AxisBudgetLedger ledger,
            ActorCommitments commitments, float reservedFollowup)
        {
            if (demand == null || hand == null)
                return "diag unavailable";

            int matching = 0, traitMatching = 0, placements = 0, preflight = 0;
            var failures = new Dictionary<string, int>();
            bool solo = demand.Capability == CapabilityKind.ScoutCapability;
            foreach (CardData card in hand.Hand.Where(c => c?.Definition != null))
            {
                CardDefinition def = card.Definition;
                bool recce = AbilityParams.AbilitiesHaveAnyRecce(def.grantedAbilities);
                bool cap = demand.Capability == CapabilityKind.ScoutCapability ? recce
                    : demand.Capability == CapabilityKind.Hero ? def.cardType == CardType.Hero && !recce
                    : !recce && (def.cardType == CardType.Unit || def.cardType == CardType.Hero);
                if (!cap || def.isAviation)
                    continue;
                matching++;

                bool needsStealth = (demand.RequiredTraits & TraitPreference.Stealth) != 0;
                bool hasStealth = AbilityParams.AbilitiesHaveAnyStealth(def.grantedAbilities);
                if (!hasStealth && card.Equipment?.equipment != null)
                    hasStealth = AbilityParams.AbilitiesHaveAnyStealth(
                        EquipmentSystem.EffectiveAbilities(def.grantedAbilities, card.Equipment.equipment));
                if (needsStealth && !hasStealth)
                    continue;
                traitMatching++;

                foreach (PlacementOption opt in PlacementSelector.BuildOptions(snap, player, def, commitments, solo))
                {
                    placements++;
                    if (CardPlayExecutor.Preflight(player, root, hand, ctx, opt.Bind(card), out string reason))
                    {
                        preflight++;
                        continue;
                    }

                    string detailed = DetailFailure(root, player, card, reason);
                    failures.TryGetValue(detailed, out int count);
                    failures[detailed] = count + 1;
                }
            }

            float axis = ledger != null ? ledger.Balance(demand.RequestingAxis) : 0f;
            int ap = root != null ? root.ActionPoints : 0;
            string failText = failures.Count == 0
                ? "-"
                : string.Join(" | ", failures.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
                    .Select(kv => kv.Value > 1 ? $"{kv.Key} x{kv.Value}" : kv.Key));
            return $"diag hand={hand.Hand.Count} {AiCardLog.Hand(hand)} freeSlot={(hand.HasFreeSlot ? 1 : 0)} "
                + $"match={matching} trait={traitMatching} placements={placements} preflight={preflight} "
                + $"fails=[{failText}] axis={axis:0.##} ap={ap} followupReserved={reservedFollowup:0.##}";
        }

        private static string DetailFailure(PlayerRoot root, PlayerSetupData player, CardData card, string reason)
        {
            string cardName = card?.Definition?.displayName ?? "?";
            if (root == null || player == null || card == null)
                return $"{cardName}: {reason ?? "preflight rejected"}";

            ResourceCost cost = Game.Ai.AiCardCost.PlayResources(card);
            if (cost != null && !Game.Ai.AiCardCost.CanAffordPlayResources(root, player, card))
            {
                return $"{cardName}: resources need H/E/M/T={cost.human}/{cost.energy}/{cost.materials}/{cost.tech} "
                    + $"have={Available(ResourceType.Human)}/{Available(ResourceType.Energy)}/"
                    + $"{Available(ResourceType.Materials)}/{Available(ResourceType.Tech)}";
            }

            return $"{cardName}: {reason ?? "preflight rejected"}";

            int Available(ResourceType type) =>
                UnityEngine.Mathf.FloorToInt(Game.Ai.AiResourceReservation.Available(root, player, type));
        }
    }
}
