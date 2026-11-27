using System.Linq;
using Game.Cards;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  DEVELOPMENT UPGRADE ADMISSION  (Strategy V2 — CapabilityKind.CardUpgrade consumer)
    // ===========================================================================================
    //  The live gates of a CardUpgrade demand's DevelopmentOpportunity, taken VERBATIM (no card is
    //  re-picked) right before Phase A runs its Challenge (MaterializationExecutor.ExecuteGeneration):
    //  a useful future recipient, the shared AP pool, and the creation stage still spendable.
    //  Starting the Challenge costs card.apCost plus its ResourceCost and is never refunded on loss;
    //  a win leaves the minted equipment in hand for a separately funded attachment.
    // ===========================================================================================
    internal static class DevelopmentUpgradeFulfillment
    {
        // null = admitted (`generation` is the Challenge to run); otherwise why not.
        public static string Admit(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, AxisDemand demand,
            MaterializationPlan plan, PhaseAApBudget apBudget, out GenerationStep generation)
        {
            DevelopmentOpportunity op = plan?.DevelopmentUpgrade ?? demand?.DevOpportunity;
            generation = plan?.Generation ?? op?.Generation;
            if (op == null || generation == null || op.Card == null || player == null
                || root == null || hand == null || ctx == null)
                return "missing opportunity / plan / args";

            // Validate a useful future recipient without demanding its attachment AP today.
            switch (op.RecipientKind)
            {
                case DevRecipientKind.HandCard:
                    if (op.RecipientCard == null || hand.Hand == null || !hand.Hand.Contains(op.RecipientCard)
                        || !EquipmentSystem.CanAttachPreview(op.Card, op.RecipientCard, out _))
                        return "recipient_gone";
                    break;
                case DevRecipientKind.GarrisonUnit:
                case DevRecipientKind.FieldUnit:
                    if (op.RecipientUnit == null || op.RecipientUnit.IsPrisoner
                        || !UnitOnMap(player, op.RecipientUnit)
                        || !EquipmentSystem.CanAttachPreview(op.Card, op.RecipientUnit, out _))
                        return "recipient_gone";
                    break;
                default:
                    return "unsupported_recipient_kind";
            }

            int challengeAp = ResearchProductionSystem.AttemptApCost(op.Card);
            int attachAp = Mathf.Max(0, op.Card.activationApCost);
            // ApCost is THIS stage. The full prospective score still prices the deferred attach.
            if (apBudget != null)
            {
                float axisRoom = apBudget.UnreservedBalance();
                if (challengeAp > axisRoom + AiConfigV2.allocatorSliceEpsilon)
                    return $"axis_budget {axisRoom:0.##} < creation {challengeAp}";
            }
            if (!root.CanSpendActionPoints(challengeAp))
                return "no_ap_for_creation";

            // Protect the canonical creation-stage plan before irreversible Challenge payment.
            // PlanFactory owns ApCost/ResCost; no parallel resource calculator or new ledger.
            if (plan == null || plan.Kind != MaterializationChainKind.GenerateAttachUpgrade
                || plan.ApCost != challengeAp || plan.DeferredAttachmentAp != attachAp
                || DevelopmentOpportunityEvaluator.PendingEquipmentCovers(op, hand, snap,
                    CapabilityInventory.Build(snap, player, null))
                || challengeAp > StrategicSpendability.SpendableAp(player, root, ctx, demand?.SpendAuthority ?? default)
                || !TradeFunding.CanFund(player, root, ctx, plan.ResCost, demand?.SpendAuthority ?? default))
                return "upgrade_chain_no_longer_spendable";

            return null;
        }

        private static bool UnitOnMap(PlayerSetupData player, UnitData u) =>
            OwnWorld.Of(player).Armies.Any(a => a?.Members != null && a.Members.Contains(u));
    }
}
