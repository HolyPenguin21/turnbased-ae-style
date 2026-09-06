using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Aviation;
using Game.Economy;
using Game.Map;
using Game.Players;
using UnityEngine;

using Game.Cards;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  AIR RECON ENERGY-PRESSURE MEASUREMENTS  (spec §40–44)
    // ===========================================================================================
    //  A library of generic, name-free reads of "how much Energy is spoken for" that the single
    //  canonical sortie-reservation decision (AviationSortieReservationEvaluator, reached from
    //  ProvisioningManager.AirSortieReservationAdmission) consumes:
    //    · CommittedAirActivationEnergy   — Energy other in-flight air wings still owe this turn
    //    · ProtectedHandEnergy            — Energy a currently-PLAYABLE high-value hand card needs
    //                                       (§41.2: largest such card in full + a fraction of the rest)
    //    · ProtectedNearTermDrawEnergy    — low-weight allowance for the turn's likely next draw (§44)
    //
    //  This type no longer makes an admission decision of its own — the retired `Evaluate()` used to,
    //  and that was a second strategic authority parallel to the evaluator. It reads Energy the same
    //  way every other V2 resource read does (PlayerRoot.GetResource) and card costs through
    //  CardCostRules, never a parallel model.
    // ===========================================================================================
    internal static class ReconAirEnergyPolicy
    {
        private static readonly ResourceType[] NonEnergyTypes =
        {
            ResourceType.Human, ResourceType.Materials, ResourceType.Tech,
        };

        // NOTE — this policy no longer owns an admission decision (`Evaluate()` is retired). It is a
        // library of generic, name-free Energy-pressure MEASUREMENTS only; the single strategic
        // "is this sortie worth its Energy" decision lives in AviationSortieReservationEvaluator
        // (reached from ProvisioningManager.AirSortieReservationAdmission), which calls the three
        // helpers below.

        // Energy that OTHER already-airborne air wings still owe on their own first activation this
        // turn — both AirRecon and AirStrike sorties. V2 pays activation for real on the wing's
        // first MoveArmy step, so an already-activated wing owes nothing; one still sitting
        // un-activated after launch does, and a later spend must not eat it (spec §41.1
        // "already committed/funded actions").
        // Exposed (AviationSortieReservationEvaluator) — no hardcoded card names live here or in the
        // caller; this stays the single source of "Energy other in-flight air wings still owe".
        internal static int CommittedAirActivationEnergy(PlayerSetupData player, int excludeArmyId)
        {
            int total = 0;
            foreach (ArmyData army in ArmyRegistry.AllForOwner(player))
            {
                if (army == null || army.Id == excludeArmyId || army.HasActivatedThisTurn)
                    continue;
                if (!AviationRules.IsValidAirArmy(army))
                    continue;
                bool inFlightSortie = ReconPatrolStateRegistry.TryGet(player, army.Id, out _)
                    || AirSortieRegistry.ForArmy(player, army) != null;
                if (!inFlightSortie)
                    continue;
                total += Mathf.Max(0, army.ActivationEnergyCost);
            }
            return total;
        }

        // §41.2 / §44 — Energy a currently-PLAYABLE, HIGH-VALUE hand card would need. "Playable" =
        // every non-Energy resource cost and the play-time AP cost are already satisfiable from the
        // live stock; a card blocked only by Energy still counts (that Energy is exactly what we
        // protect). "High value" is proxied by an Energy cost of at least
        // reconAirEnergyHighValueMinCost — a cheap trick that costs 0-1 Energy is not the
        // strategically significant unit/hero/aviation card §41.2 means. Weighting: the single
        // largest such card in full, plus reconAirEnergyExtraHandFraction of the rest — never the
        // whole deck (§44), never a card already played, never one that needs resources the AI
        // does not have.
        // Exposed (AviationSortieReservationEvaluator §2 Hand Energy Pressure) — generic over
        // whatever cards happen to be in hand, no name/type hardcoding.
        internal static int ProtectedHandEnergy(PlayerRoot root, PlayerSetupData player)
        {
            AiHandData hand = AiHandRegistry.Peek(player);
            if (hand == null || hand.Hand.Count == 0)
                return 0;

            var energyCosts = new List<int>();
            foreach (Game.Cards.CardData card in hand.Hand)
            {
                if (card == null)
                    continue;
                int energy = CardCostRules.PlayResource(card, ResourceType.Energy);
                if (energy < AiConfigV2.reconAirEnergyHighValueMinCost)
                    continue;
                if (!root.CanSpendActionPoints(CardCostRules.PlayAp(card)))
                    continue;
                bool nonEnergyAffordable = NonEnergyTypes.All(t =>
                    CardCostRules.PlayResource(card, t) <= root.GetResource(t));
                if (!nonEnergyAffordable)
                    continue;
                energyCosts.Add(energy);
            }

            if (energyCosts.Count == 0)
                return 0;

            int largest = energyCosts.Max();
            int rest = energyCosts.Sum() - largest;
            return largest + Mathf.RoundToInt(AiConfigV2.reconAirEnergyExtraHandFraction * rest);
        }

        // §44 — a LOW-weight allowance for the Energy the turn's likely next draw would need. This
        // is the expected Energy of one random still-drawable deck card, scaled by
        // reconAirEnergyDeckDrawFraction — deliberately NOT the whole remaining deck's appetite
        // (§44 "не складывать Energy cost всей колоды"). Zero once the deck is empty or the hand is
        // full (no draw is coming).
        //
        // §41.4 Research/Production opportunity is intentionally not added here: under ReconOnly no
        // such action exists, and when Full V2 returns its own funded Develop actions already claim
        // their Energy through the pipeline before AirRecon is evaluated.
        // Exposed (AviationSortieReservationEvaluator §2 Deck Energy Pressure) — probability-weighted
        // (mean of the remaining deck), not "deck contains an Energy card => hoard forever".
        internal static int ProtectedNearTermDrawEnergy(PlayerSetupData player)
        {
            AiHandData hand = AiHandRegistry.Peek(player);
            if (hand == null || !hand.HasFreeSlot || !hand.HasCardsLeftToDraw)
                return 0;

            int count = 0;
            long sum = 0;
            foreach (Game.Cards.CardDefinition def in hand.RemainingDeck)
            {
                if (def == null)
                    continue;
                count++;
                sum += Mathf.Max(0, def.resourceCost != null ? def.resourceCost.Get(ResourceType.Energy) : 0);
            }
            if (count == 0)
                return 0;

            float expectedNextDrawEnergy = (float)sum / count;
            return Mathf.RoundToInt(AiConfigV2.reconAirEnergyDeckDrawFraction * expectedNextDrawEnergy);
        }
    }
}
