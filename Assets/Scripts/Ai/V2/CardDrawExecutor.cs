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
    //  OnDrawClicked uses, with DrawOne itself refusing to overflow the hand capacity.
    //
    //  IMPORTANT: terminal Draw is the LAST strategic fallback. Before consuming its AP this seam
    //  checks the post-strategy actions Phase-B materialization cannot express directly:
    //  Base capacity/internal Facility maintenance, Equipment on already-deployed units,
    //  standalone Research/Production, and decisive movement toward an honestly-known enemy
    //  Citadel once the ordinary army-targeted Raid lane has no contact left. If one is available
    //  we leave the AP untouched and let Housekeeping's pre-reorganisation pass execute it.
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

            if (AiHandRegistry.TryGetOwner(hand, out PlayerSetupData maintenanceOwner)
                && maintenanceOwner != null
                && (StrategicMaintenancePolicy.HasPriorityAction(maintenanceOwner, root, hand, ctx)
                    || StrategicPressureAdvance.HasAction(maintenanceOwner, root, hand, ctx)))
            {
                AiDebugLog.Write("[AI][V2]   strat.B terminal — preserve AP: strategic maintenance/pressure outranks Draw");
                return false;
            }

            CardData card = hand.DrawOne();
            if (card == null)
                return false;
            root.SpendActionPoints(ctx.DrawApCost);
            AiDebugLog.Write($"[AI][V2]   strat.B — drew \"{card.Definition?.displayName}\" ({ctx.DrawApCost} AP)");

            // StrategicManager.RunTerminalDraws normally checks whether the CURRENT hand became
            // actionable at the top of the next loop iteration. A successful draw can itself make
            // that next iteration impossible, however: it may fill the hand, exhaust the deck, or
            // leave less AP than another draw costs. Previously that boundary card was therefore
            // never examined for a residual-demand / worthwhile-surplus chain (the turn-2 b_Lab
            // case: the card filled hand 10/10 and 5 AP were stranded until next turn).
            //
            // Do not duplicate StrategicManager's actionability logic here. Instead, only on this
            // exact terminal boundary, raise the already-existing bounded hand-opportunity
            // interrupt. The reaction pass performs the authoritative fresh snapshot / demand /
            // materialization evaluation. A useless boundary card costs one extra bounded replan,
            // but can never cause an extra play; an actionable one can no longer be missed.
            bool terminalBoundary = !hand.HasFreeSlot
                || !hand.HasCardsLeftToDraw
                || !root.CanSpendActionPoints(ctx.DrawApCost);
            if (terminalBoundary
                && AiHandRegistry.TryGetOwner(hand, out PlayerSetupData owner)
                && owner != null)
            {
                StrategicInterruptRegistry.MarkHandOpportunity(owner, ctx.TurnNumber, hand);
                AiDebugLog.Write("[AI][V2] strategic interrupt — terminal boundary draw changed the hand; "
                    + "bounded replan will validate the newly drawn card before turn end");
            }
            return true;
        }
    }
}
