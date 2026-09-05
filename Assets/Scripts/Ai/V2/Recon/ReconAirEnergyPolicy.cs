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
    //  AIR RECON ENERGY OPPORTUNITY COST  (spec §40–44)
    // ===========================================================================================
    //  V1's air recon only ever asked "is this one sortie affordable right now" — so a routine
    //  refresh flight could chew through the whole Energy stock one individually-cheap launch at a
    //  time, starving high-value hand cards / research of the Energy they needed.
    //
    //  This policy splits the stock into:
    //    committed      — Energy other in-flight AirRecon activations still owe this turn
    //    protectedHand  — Energy a currently-PLAYABLE high-value hand card would need (§41.2, §44):
    //                     the single largest such card in full, plus a fraction of the rest. The
    //                     whole remaining deck is deliberately NOT summed (§44).
    //    spendable      — max(0, stock − committed − protectedHand)
    //
    //  First pass (§42): protected Energy is a HARD reserve — a launch that would dip below it is
    //  refused outright. A soft opportunity term then trims marginal launches when spendable Energy
    //  is thin relative to income (§41.5 / §43).
    //
    //  It reads Energy the same way every other V2 resource read does (PlayerRoot.GetResource — see
    //  AiResourceReservation.Available's own comment on why V2 treats the physical stockpile as
    //  authoritative) and card costs through AiCardCost, never a parallel model.
    // ===========================================================================================
    internal readonly struct ReconAirEnergyDecision
    {
        public readonly bool Allowed;
        public readonly int Stock;
        public readonly int LaunchCost;
        public readonly int Committed;
        public readonly int ProtectedHand;
        public readonly int ProtectedDeck;
        public readonly int Spendable;
        public readonly float InformationValue;
        public readonly float OpportunityCost;
        public readonly float FinalUtility;
        public readonly string Reason;

        public ReconAirEnergyDecision(bool allowed, int stock, int launchCost, int committed,
            int protectedHand, int protectedDeck, int spendable, float informationValue,
            float opportunityCost, float finalUtility, string reason)
        {
            Allowed = allowed;
            Stock = stock;
            LaunchCost = launchCost;
            Committed = committed;
            ProtectedHand = protectedHand;
            ProtectedDeck = protectedDeck;
            Spendable = spendable;
            InformationValue = informationValue;
            OpportunityCost = opportunityCost;
            FinalUtility = finalUtility;
            Reason = reason ?? string.Empty;
        }

        // Spec §60 — one line per launch/no-launch decision so a playtester can see exactly why
        // the aircraft did or did not fly.
        public string ToLog(string actorLabel) =>
            $"[AI][V2][Recon][Air][Energy] {actorLabel} stock={Stock} cost={LaunchCost} "
            + $"committed={Committed} protectedHand={ProtectedHand} protectedDeck={ProtectedDeck} spendable={Spendable} "
            + $"informationValue={InformationValue:0.00} oppCost={OpportunityCost:0.00} "
            + $"finalUtility={FinalUtility:0.00} decision={(Allowed ? "LAUNCH" : "NO_LAUNCH")} reason={Reason}";
    }

    internal static class ReconAirEnergyPolicy
    {
        private static readonly ResourceType[] NonEnergyTypes =
        {
            ResourceType.Human, ResourceType.Materials, ResourceType.Tech,
        };

        // launchEnergyCost — this sortie's own first-activation Energy (ArmyData.ActivationEnergyCost
        // for an already-formed wing, Σ UnitData.LaunchEnergyCost for a still-stored group).
        // excludeArmyId — the actor being evaluated, so an airborne wing re-checking its own first
        // step does not count itself in `committed`; pass a negative value for a storage launch that
        // has no ArmyData yet.
        // extraCommittedEnergy — Energy already committed by EARLIER sorties reserved in the same
        // planning pass this turn (AI-RECON-01 ReconAirReservationPrepass evaluates several
        // candidate launches against the ONE stockpile; without this each would see the full stock
        // and the prepass would over-promise guaranteed lanes the executor cannot all fly).
        public static ReconAirEnergyDecision Evaluate(PlayerSetupData player, PlayerRoot root, HexMap map,
            int launchEnergyCost, float informationValue, int excludeArmyId, int extraCommittedEnergy = 0)
        {
            if (player == null || root == null)
                return new ReconAirEnergyDecision(false, 0, launchEnergyCost, 0, 0, 0, 0,
                    informationValue, 0f, 0f, "missing player/root");

            int stock = Mathf.Max(0, root.GetResource(ResourceType.Energy));
            int committed = CommittedAirActivationEnergy(player, excludeArmyId) + Mathf.Max(0, extraCommittedEnergy);
            int protectedHand = ProtectedHandEnergy(root, player);
            int protectedDeck = ProtectedNearTermDrawEnergy(player);
            int spendable = Mathf.Max(0, stock - committed - protectedHand - protectedDeck);

            // §42 first pass: hard reserve. A launch may never dip into committed or protected Energy.
            if (launchEnergyCost > spendable)
                return new ReconAirEnergyDecision(false, stock, launchEnergyCost, committed, protectedHand,
                    protectedDeck, spendable, informationValue, 0f, 0f,
                    "energy_reserved_for_playable_high_value_card");

            // §41.5 / §43 soft term — a marginal sortie is trimmed when spendable Energy is thin
            // relative to near-term income; a healthy runway makes the same sortie cheap.
            float income = map != null
                ? Mathf.Max(0f, IncomeProjection.IncomeFor(player, ResourceType.Energy, map))
                : 0f;
            float effectiveSpendable = spendable + income * AiConfigV2.reconAirEnergyIncomeHorizon;
            float opportunityCost = launchEnergyCost / Mathf.Max(1f, effectiveSpendable);
            float finalUtility = informationValue - AiConfigV2.reconAirEnergyOppWeight * opportunityCost;

            if (finalUtility < AiConfigV2.reconAirEnergyMinUtility)
                return new ReconAirEnergyDecision(false, stock, launchEnergyCost, committed, protectedHand,
                    protectedDeck, spendable, informationValue, opportunityCost, finalUtility,
                    "energy_opportunity_cost_exceeds_information_value");

            return new ReconAirEnergyDecision(true, stock, launchEnergyCost, committed, protectedHand,
                protectedDeck, spendable, informationValue, opportunityCost, finalUtility, "ok");
        }

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
