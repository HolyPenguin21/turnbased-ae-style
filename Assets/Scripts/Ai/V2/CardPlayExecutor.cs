using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  CARD PLAY EXECUTOR  (Strategy V2 — Strategic Manager)
    // ===========================================================================================
    //  The SINGLE authoritative V2 path for strategic card deployment. V2 code never scatters
    //  hand mutation across StrategicManager / axis planners — card removal from hand happens
    //  HERE, exactly once, only after a successful ArmyActions.DeployUnitFromCard. V1's own
    //  card-play path (AiTurnController.PlayCardRoutine) is untouched, and hand ownership is NOT
    //  moved into ArmyActions.DeployUnitFromCard globally.
    //
    //  Draw / hand cycling is a SEPARATE operation (CardDrawExecutor) — never part of this
    //  transaction.
    //
    //  MULTI-STEP PREFLIGHT. CreateArmy -> DeployUnitFromCard is not atomic in the engine. Play()
    //  preflights the whole sequence (affordability of CreateArmy AP + deploy AP + resource cost,
    //  capacity, required building, hand state) before spending anything. If CreateArmy succeeds
    //  but the deploy then fails, the fresh empty ArmyData is KEPT as a reusable asset — never
    //  rolled back or deleted as garbage (StateChanged is still true).
    // ===========================================================================================
    public readonly struct CardPlayPlan
    {
        public readonly CardData Card;
        public readonly HexCoord DeploymentHex;
        public readonly ArmyData ExistingShell;   // non-null -> reuse this shell; null -> CreateArmy
        public readonly bool RequiresCreateArmy;

        public CardPlayPlan(CardData card, HexCoord hex, ArmyData shell)
        {
            Card = card;
            DeploymentHex = hex;
            ExistingShell = shell;
            RequiresCreateArmy = shell == null;
        }

        public int TotalApCost =>
            (RequiresCreateArmy ? ArmyActions.CreateArmyApCost : 0) + AiCardCost.PlayAp(Card);
    }

    public sealed class CardPlayResult
    {
        public bool Deployed;
        public bool ArmyCreated;
        public ArmyData ArmyShell;   // reused or newly created; a retained reusable asset if deploy failed
        public float ApSpent;
        public bool StateChanged;
        public string FailReason;
    }

    public static class CardPlayExecutor
    {
        // Full preflight of the CreateArmy -> DeployUnitFromCard sequence. No spend, no mutation.
        public static bool Preflight(PlayerSetupData player, PlayerRoot root, AiHandData hand,
            AiTurnContext ctx, CardPlayPlan plan, out string reason)
        {
            reason = null;
            if (player == null || root == null || hand == null || ctx == null || plan.Card == null)
            { reason = "missing args"; return false; }
            if (!hand.Hand.Contains(plan.Card))
            { reason = "card not in hand"; return false; }

            CardDefinition def = plan.Card.Definition;
            if (def == null) { reason = "card has no definition"; return false; }
            if (def.isAviation) { reason = "aviation card not handled by StrategicManager"; return false; }
            if (def.cardType != CardType.Unit && def.cardType != CardType.Hero)
            { reason = $"card type {def.cardType} not a Unit/Hero deploy"; return false; }

            int totalAp = plan.TotalApCost;
            if (!root.CanSpendActionPoints(totalAp))
            { reason = $"need {totalAp} AP for the full sequence"; return false; }
            if (!AiCardCost.CanAffordPlayResources(root, player, plan.Card))
            { reason = "resource cost unaffordable"; return false; }

            if (!plan.RequiresCreateArmy)
            {
                ArmyData shell = plan.ExistingShell;
                if (shell == null || shell.Members.Count != 0 || !shell.Hex.Equals(plan.DeploymentHex))
                { reason = "shell is no longer a valid empty army at the deployment hex"; return false; }
                if (!shell.HasRoom)
                { reason = "shell has no room"; return false; }
            }

            if (!string.IsNullOrEmpty(def.requiredBuildingAbility)
                && !PlacementRules.HasRequiredBuilding(player, plan.DeploymentHex, def))
            { reason = $"no owned '{def.requiredBuildingAbility}' building at deployment hex"; return false; }

            return true;
        }

        public static CardPlayResult Play(PlayerSetupData player, PlayerRoot root, AiHandData hand,
            AiTurnContext ctx, CardPlayPlan plan)
        {
            var result = new CardPlayResult { ArmyShell = plan.ExistingShell };
            if (!Preflight(player, root, hand, ctx, plan, out string reason))
            {
                result.FailReason = reason;
                return result;
            }

            ArmyData shell = plan.ExistingShell;
            if (plan.RequiresCreateArmy)
            {
                FactionCardCatalog catalog = ctx.StartingDeckCatalog?.GetCatalog(player.Faction);
                shell = ArmyActions.CreateArmy(player, plan.DeploymentHex, catalog, ctx.HexSelection);
                if (shell == null)
                {
                    result.FailReason = "CreateArmy failed";
                    return result;
                }
                result.ArmyCreated = true;
                result.ArmyShell = shell;
                result.ApSpent += ArmyActions.CreateArmyApCost;
                result.StateChanged = true;   // an empty army now exists — a retained reusable asset
            }

            bool deployed = ArmyActions.DeployUnitFromCard(plan.Card.Definition, player, shell, root,
                ctx.HexSelection, out string deployFail, sourceCard: plan.Card);
            if (!deployed)
            {
                // The newly created shell (if any) is NOT rolled back — it stays a reusable asset.
                result.FailReason = deployFail ?? "DeployUnitFromCard failed";
                return result;
            }

            hand.Hand.Remove(plan.Card);   // exactly once, only on success — the canonical V2 boundary
            result.Deployed = true;
            result.ApSpent += AiCardCost.PlayAp(plan.Card);
            result.StateChanged = true;
            return result;
        }
    }
}
