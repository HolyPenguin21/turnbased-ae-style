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
    //  by handReplenishMinApLeft, which keeps AP for activations and aviation obligations.
    // ===========================================================================================
    internal static class HandReplenishPolicy
    {
        // How many cards to draw now. Pure: every input is a plain count.
        internal static int DrawsWanted(int handCount, int freeSlots, int deckCount,
            int ap, int drawApCost, int drawsUsedThisTurn)
        {
            if (drawApCost <= 0)
                return 0;
            int wanted = AiConfigV2.handReplenishTargetCards - handCount;
            wanted = System.Math.Min(wanted, AiConfigV2.handReplenishMaxDrawsPerTurn);
            wanted = System.Math.Min(wanted, AiConfigV2.maxTerminalDrawsPerTurn - drawsUsedThisTurn);
            wanted = System.Math.Min(wanted, freeSlots);
            wanted = System.Math.Min(wanted, deckCount);
            wanted = System.Math.Min(wanted, (ap - AiConfigV2.handReplenishMinApLeft) / drawApCost);
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
            int apBefore = SpendableAp(player, root, ctx);
            int wanted = DrawsWanted(handBefore, ctx.HandCapacity - handBefore,
                hand.RemainingDeck?.Count ?? 0, apBefore, ctx.DrawApCost, budget.DrawActionsUsed);
            if (wanted <= 0)
                return 0;

            int drawn = 0;
            for (int i = 0; i < wanted; i++)
            {
                if (!CardDrawExecutor.TryCycle(root, hand, ctx))
                    break;
                budget.RecordAction(draw: true, generationAttempt: false);
                drawn++;
            }
            AiDebugLog.Write($"[AI][V2] {player.Nickname}: hand replenish — hand {handBefore}->{hand.Hand.Count}"
                + $"/{ctx.HandCapacity}, drew {drawn}, spendable AP {apBefore}->{SpendableAp(player, root, ctx)} "
                + $"(target {AiConfigV2.handReplenishTargetCards}, keep >= {AiConfigV2.handReplenishMinApLeft} AP)");
            return drawn;
        }

        private static int SpendableAp(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx) =>
            (int)System.Math.Floor(StrategicSpendability.SpendableAp(player, root, ctx)
                + AiConfigV2.allocatorSliceEpsilon);
    }
}
