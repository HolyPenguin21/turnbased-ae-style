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
    //      live future-recipient gates -> MaterializationExecutor.TryGenerate -> hand.
    //
    //  Starting the Research/Production Challenge costs card.apCost plus its ResourceCost and is
    //  never refunded on loss. A win leaves the minted equipment in hand for a separately funded attachment.
    //  The caller debits the actual creation AP delta to the Development axis.
    //
    //  Enemy-on-hex is enforced here (via ResearchProductionSystem.IsEligible) as the ONLY place
    //  it gates — a contested facility skips execution this turn but the opportunity stays scored.
    // ===========================================================================================
    internal sealed class DevUpgradeResult
    {
        public bool Executed;        // the Challenge was rolled (AP/resources spent)
        public bool ChallengeWon;
        public bool StateChanged;
        public float ApSpent;        // actual Challenge AP
        public string Detail = "";

        public static DevUpgradeResult Skip(string why) => new DevUpgradeResult { Detail = why };
    }

    internal static class DevelopmentUpgradeFulfillment
    {
        public static bool Handles(CapabilityKind k) => k == CapabilityKind.CardUpgrade;

        public static DevUpgradeResult TryFulfill(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, AxisDemand demand,
            MaterializationPlan plan, PhaseAApBudget apBudget)
        {
            DevelopmentOpportunity op = plan?.DevelopmentUpgrade ?? demand?.DevOpportunity;
            GenerationStep generation = plan?.Generation ?? op?.Generation;
            if (op == null || generation == null || op.Card == null || player == null
                || root == null || hand == null || ctx == null)
                return DevUpgradeResult.Skip("missing opportunity / plan / args");

            // Validate a useful future recipient without demanding its attachment AP today.
            switch (op.RecipientKind)
            {
                case DevRecipientKind.HandCard:
                    if (op.RecipientCard == null || hand.Hand == null || !hand.Hand.Contains(op.RecipientCard)
                        || !EquipmentSystem.CanAttachPreview(op.Card, op.RecipientCard, out _))
                        return DevUpgradeResult.Skip("recipient_gone");
                    break;
                case DevRecipientKind.GarrisonUnit:
                case DevRecipientKind.FieldUnit:
                    if (op.RecipientUnit == null || op.RecipientUnit.IsPrisoner
                        || !UnitOnMap(player, op.RecipientUnit)
                        || !EquipmentSystem.CanAttachPreview(op.Card, op.RecipientUnit, out _))
                        return DevUpgradeResult.Skip("recipient_gone");
                    break;
                default:
                    return DevUpgradeResult.Skip("unsupported_recipient_kind");
            }

            int challengeAp = ResearchProductionSystem.AttemptApCost(op.Card);
            int attachAp = Mathf.Max(0, op.Card.activationApCost);
            // ApCost is THIS stage. The full prospective score still prices the deferred attach.
            if (apBudget != null)
            {
                float axisRoom = apBudget.UnreservedBalance();
                if (challengeAp > axisRoom + AiConfigV2.allocatorSliceEpsilon)
                    return DevUpgradeResult.Skip(
                        $"axis_budget {axisRoom:0.##} < creation {challengeAp}");
            }
            if (!root.CanSpendActionPoints(challengeAp))
                return DevUpgradeResult.Skip("no_ap_for_creation");

            // Protect the canonical creation-stage plan before irreversible Challenge payment.
            // PlanFactory owns ApCost/ResCost; no parallel resource calculator or new ledger.
            if (plan == null || plan.Kind != MaterializationChainKind.GenerateAttachUpgrade
                || plan.ApCost != challengeAp || plan.DeferredAttachmentAp != attachAp
                || DevelopmentOpportunityEvaluator.PendingEquipmentCovers(op, hand, snap,
                    CapabilityInventory.Build(snap, player, null))
                || !StrategicSpendability.ReservesOkAfterChain(root, ctx, plan, player,
                    demand?.SpendAuthority ?? default))
                return DevUpgradeResult.Skip("upgrade_chain_no_longer_spendable");

            int apBefore = root.ActionPoints;
            bool wasHiddenHero = generation.Mode == ResearchProductionMode.Research
                && generation.Hero != null && generation.Hero.IsHidden;
            MaterializationExecutor.GenerationOutcome generated =
                MaterializationExecutor.TryGenerate(generation, player, root, hand, ctx,
                    demand?.SpendAuthority ?? default);
            if (!generated.Attempted)
                return DevUpgradeResult.Skip(generated.FailReason ?? "generation_preflight_failed");
            if (!generated.Success)
            {
                if (generated.StateChanged) WorldDeltaLifecycle.CommitMutation();
                return new DevUpgradeResult
                {
                    Executed = true,
                    ChallengeWon = false,
                    StateChanged = generated.StateChanged,
                    ApSpent = apBefore - root.ActionPoints,
                    Detail = generated.FailReason ?? "Challenge lost",
                };
            }

            // Minting already added the paid card to the normal hand. Hand attachment is a
            // separate stage owned by NonCombatCardPlayer; it reselects a useful legal recipient
            // from the settled world, this turn if affordable or on a later turn.
            if (generated.StateChanged) WorldDeltaLifecycle.CommitMutation();
            return new DevUpgradeResult
            {
                Executed = true, ChallengeWon = true, StateChanged = generated.StateChanged,
                ApSpent = apBefore - root.ActionPoints,
                Detail = $"Challenge won, '{generated.Minted?.Definition?.displayName}' retained in hand; attachment pending"
                    + (wasHiddenHero ? " [hero revealed]" : ""),
            };
        }

        private static bool UnitOnMap(PlayerSetupData player, UnitData u) =>
            ArmyRegistry.AllForOwner(player).Any(a => a?.Members != null && a.Members.Contains(u));
    }
}
