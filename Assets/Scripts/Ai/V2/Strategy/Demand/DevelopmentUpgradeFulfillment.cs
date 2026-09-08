using System.Linq;
using Game.Cards;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  DEVELOPMENT UPGRADE FULFILLMENT  (Strategy V2 — CapabilityKind.CardUpgrade consumer)
    // ===========================================================================================
    //  StrategicManager Phase A calls this for a CardUpgrade demand — it runs the
    //  DevelopmentOpportunity the demand carries VERBATIM (it does not re-pick a card):
    //      live recipient gates -> MaterializationExecutor.TryGenerate -> attach.
    //
    //  Starting the Research/Production Challenge costs card.apCost plus its ResourceCost and is
    //  never refunded on loss. A win then pays the minted equipment's activation AP to attach it.
    //  The caller debits the actual combined AP delta to the Development axis.
    //
    //  Enemy-on-hex is enforced here (via ResearchProductionSystem.IsEligible) as the ONLY place
    //  it gates — a contested facility skips execution this turn but the opportunity stays scored.
    // ===========================================================================================
    internal sealed class DevUpgradeResult
    {
        public bool Executed;        // the Challenge was rolled (AP/resources spent)
        public bool ChallengeWon;
        public bool Attached;        // a won Challenge's card reached its recipient
        public bool StateChanged;
        public float ApSpent;        // actual Challenge + successful attach AP
        public string Detail = "";

        public static DevUpgradeResult Skip(string why) => new DevUpgradeResult { Detail = why };
    }

    internal static class DevelopmentUpgradeFulfillment
    {
        public static bool Handles(CapabilityKind k) => k == CapabilityKind.CardUpgrade;

        public static DevUpgradeResult TryFulfill(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, AxisDemand demand,
            MaterializationPlan plan, AxisBudgetLedger ledger)
        {
            DevelopmentOpportunity op = plan?.DevelopmentUpgrade ?? demand?.DevOpportunity;
            GenerationStep generation = plan?.Generation ?? op?.Generation;
            if (op == null || generation == null || op.Card == null || player == null
                || root == null || hand == null || ctx == null)
                return DevUpgradeResult.Skip("missing opportunity / plan / args");

            // Validate the recipient with a created-card preview: generation already pays the
            // ResourceCost, therefore attachment must test activation AP and zero resources.
            CardData preview = ResearchProductionSystem.MintCard(op.Card);
            switch (op.RecipientKind)
            {
                case DevRecipientKind.HandCard:
                    if (op.RecipientCard == null || hand.Hand == null || !hand.Hand.Contains(op.RecipientCard)
                        || !EquipmentSystem.CanAttach(preview, op.RecipientCard, root, out _))
                        return DevUpgradeResult.Skip("recipient_gone");
                    break;
                case DevRecipientKind.GarrisonUnit:
                case DevRecipientKind.FieldUnit:
                    if (op.RecipientUnit == null || op.RecipientUnit.IsPrisoner
                        || !UnitOnMap(player, op.RecipientUnit)
                        || !EquipmentSystem.CanAttach(preview, op.RecipientUnit, root, out _))
                        return DevUpgradeResult.Skip("recipient_gone");
                    break;
                default:
                    return DevUpgradeResult.Skip("unsupported_recipient_kind");
            }

            int challengeAp = ResearchProductionSystem.AttemptApCost(op.Card);
            int attachAp = Mathf.Max(0, op.Card.activationApCost);
            int completeAp = challengeAp + attachAp;
            if (ledger != null)
            {
                float axisRoom = ledger.Balance(demand.RequestingAxis)
                    - ledger.ReservedFollowup(demand.RequestingAxis);
                if (completeAp > axisRoom + AiConfigV2.allocatorSliceEpsilon)
                    return DevUpgradeResult.Skip(
                        $"axis_budget {axisRoom:0.##} < challenge+attach {completeAp}");
            }
            if (!root.CanSpendActionPoints(completeAp))
                return DevUpgradeResult.Skip("no_ap_for_challenge_and_attach");

            int apBefore = root.ActionPoints;
            bool wasHiddenHero = generation.Mode == ResearchProductionMode.Research
                && generation.Hero != null && generation.Hero.IsHidden;
            MaterializationExecutor.GenerationOutcome generated =
                MaterializationExecutor.TryGenerate(generation, player, root, hand, ctx);
            if (!generated.Attempted)
                return DevUpgradeResult.Skip(generated.FailReason ?? "generation_preflight_failed");
            if (!generated.Success)
            {
                if (generated.StateChanged) V2StateVersion.Bump();
                return new DevUpgradeResult
                {
                    Executed = true,
                    ChallengeWon = false,
                    StateChanged = generated.StateChanged,
                    ApSpent = apBefore - root.ActionPoints,
                    Detail = generated.FailReason ?? "Challenge lost",
                };
            }

            CardData minted = generated.Minted;
            bool attached;
            string attachDetail;
            if (op.RecipientKind == DevRecipientKind.HandCard)
                attached = EquipmentSystem.TryAttach(minted, op.RecipientCard, root, out attachDetail);
            else
                attached = EquipmentSystem.TryAttach(minted, op.RecipientUnit, root, out attachDetail);

            // Mint always enters the hand at the canonical generation boundary. It leaves only
            // after a successful attachment; a stale recipient/affordability failure preserves it
            // for later Phase-B use instead of destroying a won card.
            if (attached)
                hand.RemoveCard(minted);
            if (generated.StateChanged || attached)
                V2StateVersion.Bump();

            return new DevUpgradeResult
            {
                Executed = true,
                ChallengeWon = true,
                Attached = attached,
                StateChanged = generated.StateChanged || attached,
                ApSpent = apBefore - root.ActionPoints,
                Detail = attached
                    ? $"Challenge won, attached '{minted.Definition?.displayName}' -> {op.RecipientLabel}"
                        + (wasHiddenHero ? " [hero revealed]" : "")
                    : $"Challenge won, attach failed ({attachDetail}) — card retained in hand"
                        + (wasHiddenHero ? " [hero revealed]" : ""),
            };
        }

        private static bool UnitOnMap(PlayerSetupData player, UnitData u) =>
            ArmyRegistry.AllForOwner(player).Any(a => a?.Members != null && a.Members.Contains(u));
    }
}
