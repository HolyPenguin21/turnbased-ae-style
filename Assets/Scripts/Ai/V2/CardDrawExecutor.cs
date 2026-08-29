using Game.Cards;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  CARD DRAW EXECUTOR  (Strategy V2 — Strategic Manager, Phase B hand cycling)
    // ===========================================================================================
    //  Canonical hand replenishment, SEPARATE from CardPlayExecutor (deploy and draw are not one
    //  transaction). Uses AiHandData.DrawOne — the same PopRandomCard + AddCard path the human's
    //  OnDrawClicked uses, with DrawOne itself refusing to overflow the hand capacity. Never an
    //  AI-only deck rule. Only surplus preparation calls this, and only when a slot is genuinely
    //  free, the deck is non-empty, the draw AP is affordable, and reserves still hold.
    // ===========================================================================================
    public static class CardDrawExecutor
    {
        public static bool CanCycle(PlayerRoot root, AiHandData hand, AiTurnContext ctx) =>
            root != null && hand != null && ctx != null
            && hand.HasFreeSlot && hand.HasCardsLeftToDraw
            && root.CanSpendActionPoints(ctx.DrawApCost);

        public static bool TryCycle(PlayerRoot root, AiHandData hand, AiTurnContext ctx)
        {
            if (!CanCycle(root, hand, ctx))
                return false;
            CardData card = hand.DrawOne();
            if (card == null)
                return false;
            root.SpendActionPoints(ctx.DrawApCost);
            AiDebugLog.Write($"[AI][V2]   strat.B — drew \"{card.Definition?.displayName}\" ({ctx.DrawApCost} AP)");
            return true;
        }
    }
}
