using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  HAND REPLENISHMENT AT TURN START
    // ===========================================================================================
    //  Terminal Draw (Phase B) only receives the AP that missions left over, and by then that is
    //  usually 0-1 AP. A hand that ran dry therefore stayed dry for many turns (playtest
    //  2026-10-01: hand 0/10 with 19 cards in deck for six turns, stockpile piling up), and
    //  Phase A had nothing to materialize demands from.
    //
    //  When the hand is below handReplenishTargetCards, draw up to it BEFORE the turn's scan, so
    //  the drawn cards enter this turn's demand fulfilment. Bounded by the per-turn draw cap and
    //  by the AP that must stay: handReplenishMinApLeft for the turn's own work, plus the
    //  activation of pending aviation obligations. Those are protected by running before card
    //  play (AviationObligations), and this refill runs earlier still, so it keeps them itself.
    //  Spendable AP already excludes the bank's claims (continuing Hard operations).
    // ===========================================================================================
    internal static class HandReplenishPolicy
    {
        // How many cards to draw now. Pure: every input is a plain count.
        internal static int DrawsWanted(int handCount, int freeSlots, int deckCount,
            int spendableAp, int apToKeep, int drawApCost, int drawsUsedThisTurn)
        {
            if (drawApCost <= 0)
                return 0;
            int wanted = AiConfigV2.handReplenishTargetCards - handCount;
            wanted = System.Math.Min(wanted, AiConfigV2.handReplenishMaxDrawsPerTurn);
            wanted = System.Math.Min(wanted, AiConfigV2.maxTerminalDrawsPerTurn - drawsUsedThisTurn);
            wanted = System.Math.Min(wanted, freeSlots);
            wanted = System.Math.Min(wanted, deckCount);
            int apAbove = spendableAp - System.Math.Max(0, apToKeep);
            wanted = System.Math.Min(wanted, apAbove < 0 ? 0 : apAbove / drawApCost);
            return System.Math.Max(0, wanted);
        }

        // Draws the wanted cards; returns how many were drawn. Each draw debits the shared
        // per-turn tempo budget, so Phase B's terminal draws see the reduced allowance.
        internal static int Run(PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx)
        {
            if (player == null || root == null || hand == null || ctx == null)
                return 0;
            StrategicTempoBudget budget = StrategicTempoBudget.For(player, ctx.TurnNumber);
            int handBefore = hand.Hand.Count;
            int deckCount = hand.RemainingDeck?.Count ?? 0;
            // Cheap structural pre-check first: the aviation scan below is not free.
            if (handBefore >= AiConfigV2.handReplenishTargetCards || deckCount <= 0 || !hand.HasFreeSlot)
                return 0;

            int apBefore = SpendableAp(player, root, ctx);
            int aviationAp = AviationObligations.ActivationAp(player, ctx);
            int apToKeep = AiConfigV2.handReplenishMinApLeft + aviationAp;
            int wanted = DrawsWanted(handBefore, ctx.HandCapacity - handBefore, deckCount,
                apBefore, apToKeep, ctx.DrawApCost, budget.DrawActionsUsed);

            int drawn = Draw(player, root, hand, ctx, budget, wanted, "strat.replenish");
            AiDebugLog.Write($"[AI][V2] {player.Nickname}: hand replenish - hand {handBefore}->{hand.Hand.Count}"
                + $"/{ctx.HandCapacity}, drew {drawn}, spendable AP {apBefore}->{SpendableAp(player, root, ctx)} "
                + $"(target {AiConfigV2.handReplenishTargetCards}, keep {apToKeep} AP = "
                + $"{AiConfigV2.handReplenishMinApLeft} + aviation {aviationAp})");
            return drawn;
        }

        // 2026-11-02 (owner decision, variant B) — a count target cannot see a hand of cards no
        // demand can use (playtest: Draven T15-T21, hand 3/10 of a held scout, a Factory and an
        // equipment card, E/M/T up to 16/15/16, every AGG demand "no-capability-card", the Attack
        // preparation waiting on undrawn_card). Phase A calls this when a round found no feasible
        // chain: each materialization demand Phase A judged structurally undeliverable from the hand
        // that a deck card of its capability serves, payable from today's spendable stock, asks for one more
        // draw — under the same per-turn draw cap, free slots and kept AP as the turn-start refill.
        // Returns how many cards were drawn; 0 lets Phase A end the pass as before.
        internal static int RunForDemands(PlayerSetupData player, PlayerRoot root, AiHandData hand,
            AiTurnContext ctx, IEnumerable<AxisDemand> demands, float reservedFollowupAp = 0f)
        {
            if (player == null || root == null || hand == null || ctx == null || demands == null
                || !hand.HasFreeSlot)
                return 0;
            List<CardDefinition> deck = hand.RemainingDeck?.Where(d => d != null).ToList();
            if (deck == null || deck.Count == 0)
                return 0;
            int served = demands.Count(d => d != null && d.StructurallyUndeliverable
                && deck.Any(def => DeckServes(def, d)
                    && StrategicSpendability.FitsSpendableResources(player, root, ctx, def.resourceCost)));
            if (served == 0)
                return 0;

            StrategicTempoBudget budget = StrategicTempoBudget.For(player, ctx.TurnNumber);
            // Follow-up AP Phase A already promised to delivered capabilities stays kept too.
            int apToKeep = AiConfigV2.handReplenishMinApLeft + AviationObligations.ActivationAp(player, ctx)
                + (int)System.Math.Ceiling(System.Math.Max(0f, reservedFollowupAp));
            int handBefore = hand.Hand.Count;
            int wanted = DrawsWanted(0, ctx.HandCapacity - handBefore, deck.Count,
                SpendableAp(player, root, ctx), apToKeep, ctx.DrawApCost, budget.DrawActionsUsed);
            wanted = System.Math.Min(wanted, served);
            int drawn = Draw(player, root, hand, ctx, budget, wanted, "strat.demand-draw");
            if (drawn > 0)
                AiDebugLog.Write($"[AI][V2] {player.Nickname}: demand draw - {served} structurally "
                    + $"unserved demand(s) a deck card answers; hand {handBefore}->{hand.Hand.Count}"
                    + $"/{ctx.HandCapacity}, drew {drawn} (keep {apToKeep} AP)");
            return drawn;
        }

        // The canonical capability matcher of the materialization chain, on the definition.
        private static bool DeckServes(CardDefinition def, AxisDemand d) =>
            MaterializationChainMatching.MatchesCapabilityDef(def, d.Capability)
            && MaterializationChainMatching.AbilitiesSatisfyCapability(def.grantedAbilities, def.cardType,
                d.Capability, d.EconomyResourceType, recceMayFight: d.AttackFistIsPreparationHost);

        private static int Draw(PlayerSetupData player, PlayerRoot root, AiHandData hand,
            AiTurnContext ctx, StrategicTempoBudget budget, int wanted, string scope)
        {
            int drawn = 0;
            for (int i = 0; i < wanted; i++)
            {
                ReservationInvariants.SpendProbe probe = ReservationInvariants.BeginSpend(
                    player, root, ctx, "hand replenish draw");
                bool ok = CardDrawExecutor.TryCycle(root, hand, ctx,
                    scope: scope, raiseBoundaryInterrupt: false);
                ReservationInvariants.EndSpend(player, root, ctx, probe);
                if (!ok)
                    break;
                // Same bookkeeping as a Phase B draw: one budget debit and one version bump.
                budget.RecordAction(draw: true, generationAttempt: false);
                WorldDeltaLifecycle.CommitMutation();
                drawn++;
            }
            return drawn;
        }

        private static int SpendableAp(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx) =>
            (int)System.Math.Floor(StrategicSpendability.SpendableAp(player, root, ctx)
                + AiConfigV2.allocatorSliceEpsilon);
    }
}
