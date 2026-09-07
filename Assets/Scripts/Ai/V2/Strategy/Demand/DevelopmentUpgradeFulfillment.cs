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
    //      live gates -> ApplyResearchReveal -> PayCardCost -> RollChallenge -> MintCard -> attach.
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
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, AxisDemand demand, AxisBudgetLedger ledger)
        {
            DevelopmentOpportunity op = demand?.DevOpportunity;
            if (op == null || op.Card == null || player == null || root == null || hand == null)
                return DevUpgradeResult.Skip("missing opportunity / args");

            // --- live gates (no state mutation) --------------------------------------------------
            if (!ResearchProductionSystem.IsEligible(player, op.FacilityHex, op.Mode, out string why))
                return DevUpgradeResult.Skip($"facility_contested_or_ineligible ({why})");

            UnitData hero = ResearchProductionSystem.FindActor(player, op.FacilityHex, op.Mode);
            if (hero == null
                || !ResearchProductionSystem.ActorStillQualifies(player, hero, op.FacilityHex, op.Mode))
                return DevUpgradeResult.Skip("no_qualified_hero");

            if (!ResearchProductionSystem.CanAffordCard(root, op.Card))
                return DevUpgradeResult.Skip("resources_unaffordable");
            if (!GenerationSource.FitsReservedAffordability(root, player, ctx, op.Card))
                return DevUpgradeResult.Skip("would_block_card_intake");
            if (ResearchProductionSystem.EstimateSuccessChance(hero, op.Card) < AiConfig.developmentMinSuccessChance)
                return DevUpgradeResult.Skip("success_chance_below_floor");

            // recipient still legal, live
            switch (op.RecipientKind)
            {
                case DevRecipientKind.HandCard:
                    if (op.RecipientCard == null || hand.Hand == null || !hand.Hand.Contains(op.RecipientCard)
                        || !EquipmentSystem.CanAttach(op.Card, op.RecipientCard, root, out _))
                        return DevUpgradeResult.Skip("recipient_gone");
                    break;
                case DevRecipientKind.GarrisonUnit:
                case DevRecipientKind.FieldUnit:
                    if (op.RecipientUnit == null || op.RecipientUnit.IsPrisoner || !UnitOnMap(player, op.RecipientUnit)
                        || !EquipmentSystem.CanAttach(op.Card, op.RecipientUnit, root, out _))
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
                float axisRoom = ledger.Balance(demand.RequestingAxis) - ledger.ReservedFollowup(demand.RequestingAxis);
                if (completeAp > axisRoom + AiConfigV2.allocatorSliceEpsilon)
                    return DevUpgradeResult.Skip(
                        $"axis_budget {axisRoom:0.##} < challenge+attach {completeAp}");
            }
            // Admission must cover the success path as one operation. Two independent checks would
            // both pass against the same starting pool even when their combined cost cannot.
            if (!root.CanSpendActionPoints(completeAp))
                return DevUpgradeResult.Skip("no_ap_for_challenge_and_attach");

            // --- execute (canonical primitives) -----------------------------------------------
            int apBefore = root.ActionPoints;
            bool wasHiddenHero = op.Mode == ResearchProductionMode.Research && hero.IsHidden;
            ResearchProductionSystem.ApplyResearchReveal(op.Mode, hero);
            ResearchProductionSystem.PayCardCost(root, op.Card);   // AP/resources, never refunded

            ResearchProductionSystem.ChallengeOutcome outcome =
                ResearchProductionSystem.RollChallenge(hero, op.Card, int.MaxValue);
            if (!outcome.Success)
                return new DevUpgradeResult
                {
                    Executed = true, ChallengeWon = false, StateChanged = true,
                    Detail = $"Challenge lost {outcome.Successes}/{outcome.Required}",
                };

            CardData minted = ResearchProductionSystem.MintCard(op.Card);
            bool attached;
            string attachDetail;
            if (op.RecipientKind == DevRecipientKind.HandCard)
                attached = EquipmentSystem.TryAttach(minted, op.RecipientCard, root, out attachDetail);
            else
                attached = EquipmentSystem.TryAttach(minted, op.RecipientUnit, root, out attachDetail);

            float apSpent = apBefore - root.ActionPoints;

            return new DevUpgradeResult
            {
                Executed = true, ChallengeWon = true, Attached = attached, StateChanged = true,
                ApSpent = apSpent,
                Detail = $"Challenge won {outcome.Successes}/{outcome.Required}, "
                    + (attached ? $"attached '{minted.Definition?.displayName}' -> {op.RecipientLabel}"
                                : $"attach failed ({attachDetail}) — card lost")
                    + (wasHiddenHero ? " [hero revealed]" : ""),
            };
        }

        private static bool UnitOnMap(PlayerSetupData player, UnitData u) =>
            ArmyRegistry.AllForOwner(player).Any(a => a?.Members != null && a.Members.Contains(u));
    }
}
