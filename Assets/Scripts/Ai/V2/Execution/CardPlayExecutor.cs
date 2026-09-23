using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;

using Game.Ai;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  CARD PLAY EXECUTOR  (Strategy V2 — Strategic Manager)
    // ===========================================================================================
    // The SINGLE authoritative V2 path for strategic card deployment. V2 code never scatters
    // hand mutation across StrategicManager / axis planners — card removal from hand happens
    // HERE, exactly once, only after a successful ArmyActions.DeployUnitFromCard. V1's own
    // card-play path (AiTurnController.PlayCardRoutine) is untouched, and hand ownership is NOT
    // moved into ArmyActions.DeployUnitFromCard globally.
    //
    // Draw / hand cycling is a SEPARATE operation (CardDrawExecutor) — never part of this
    // transaction.
    //
    // MULTI-STEP PREFLIGHT. CreateArmy -> DeployUnitFromCard is not atomic in the engine.
    // This existing method owns V2 planning preconditions; ArmyActions remains the authoritative
    // gameplay transaction. Fresh-army deployment is atomic in ArmyActions, so a failure cannot
    // leave a charged/published empty shell. Play() still reports the REAL AP/resource delta.
    // ===========================================================================================
    public enum DeploymentKind
    {
        NewArmy,        // pay CreateArmy AP, deploy into the fresh solo army
        ReusableShell,  // deploy into an existing zero-member army already at the hex
        ExistingArmy,   // deploy into an existing non-empty army with room (plain reserve / hero-led)
        Garrison,       // deploy into the base garrison at the hex
    }

    public readonly struct CardPlayPlan
    {
        public readonly CardData Card;
        public readonly HexCoord DeploymentHex;
        public readonly DeploymentKind Kind;
        public readonly ArmyData TargetArmy;   // null only for NewArmy

        public CardPlayPlan(CardData card, HexCoord hex, DeploymentKind kind, ArmyData targetArmy)
        {
            Card = card;
            DeploymentHex = hex;
            Kind = kind;
            TargetArmy = targetArmy;
        }

        public static CardPlayPlan NewArmyAt(CardData card, HexCoord hex) =>
            new CardPlayPlan(card, hex, DeploymentKind.NewArmy, null);
        public static CardPlayPlan Into(CardData card, HexCoord hex, DeploymentKind kind, ArmyData army) =>
            new CardPlayPlan(card, hex, kind, army);

        public bool RequiresCreateArmy => Kind == DeploymentKind.NewArmy;

        public int TotalApCost =>
            (RequiresCreateArmy ? ArmyActions.CreateArmyApCost : 0) + CardCostRules.PlayAp(Card);
    }

    public sealed class CardPlayResult : IV2ActionResult
    {
        public bool Deployed;
        public bool ArmyCreated;
        public ArmyData ArmyShell;   // reused/created/target; a retained reusable asset if deploy failed
        public float ApSpent;        // REAL AP delta measured on PlayerRoot
        public ResourceCost ResourcesSpent;  // REAL H/E/M/T delta (null = none)
        public bool StateChanged;
        public int StateVersionAfter = -1;
        public string FailReason;

        public V2ActionOutcome Outcome => new V2ActionOutcome(
            succeeded: Deployed, stateChanged: StateChanged, apSpent: ApSpent, resourcesSpent: ResourcesSpent,
            played: Deployed, generated: false, attached: false, moved: false, created: ArmyCreated,
            needsReplan: false, stateVersionAfter: StateVersionAfter, failReason: Deployed ? null : FailReason);
    }

    public static class CardPlayExecutor
    {
        private static readonly ResourceType[] Res =
            { ResourceType.Human, ResourceType.Energy, ResourceType.Materials, ResourceType.Tech };

        // Predictive composite only. Container identity remains a V2 admission concern here,
        // while the actual capacity-after-add rule is owned by ArmyData and shared with execution.
        internal static bool CanFitAfterDeploy(ArmyData target, CardDefinition def)
        {
            if (target == null || def == null || def.isAviation
                || target.IsPrison || target.IsAirfield
                || target.Members.Any(m => m.IsAviation))
                return false;
            return target.CanFitAdditionalCard(def);
        }

        // Full preflight of the atomic fresh-army/existing-army card deployment. No spend, no mutation.
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
            if (root.Setup != player)
            { reason = "resource owner does not match card owner"; return false; }
            // CreateArmy charges PlayerRootRegistry.FindFor(player), whereas DeployUnitFromCard
            // receives this explicit root and the result measures its delta. Two different roots
            // with the SAME Setup would split the payment and silently omit CreateArmy's 2 AP
            // from the V2 ledger. Bind the entire chain to the domain registry's actual root.
            if (!object.ReferenceEquals(PlayerRootRegistry.FindFor(player), root))
            { reason = "resource root is not the registered player root"; return false; }
            // This is the same physical prerequisite as human CardHandUI.IsValidDropTarget and
            // ArmyActions.DeployUnitFromCard, for ALL placement kinds. Check before CreateArmy
            // charges its 2 AP, including when requiredBuildingAbility is empty.
            if (!ArmyActions.HasRequiredGroundDeploymentBuilding(player, plan.DeploymentHex, def)
            { reason = $"no owned '{def.requiredBuildingAbility}' building at deployment hex"; return false; }

            int totalAp = plan.TotalApCost;
            if (!root.CanSpendActionPoints(totalAp))
            { reason = $"need {totalAp} AP for the full sequence"; return false; }
            if (!AiResourceReservation.CanAffordCardPlay(root, player, plan.Card))
            { reason = "resource cost unaffordable"; return false; }

            switch (plan.Kind)
            {
                case DeploymentKind.NewArmy:
                    // A first Hero replaces the field army's default capacity even when its
                    // CommandRating is zero. Reuse the existing projection rule before the
                    // separate CreateArmy transaction can spend 2 AP on an unusable shell.
                    if (!ArmyData.ProjectedRosterFits(
                            ArmyData.ComputeCapacity(System.Array.Empty<Game.Units.UnitData>(), false),
                            hasExistingHero: false, projectedMemberCount: 1, incoming: def))
                    { reason = "first card would not fit in a fresh army"; return false; }
                    break;
                case DeploymentKind.ReusableShell:
                    if (plan.TargetArmy == null || plan.TargetArmy.Owner != player
                        || plan.TargetArmy.IsPrison || plan.TargetArmy.Members.Count != 0
                        || !plan.TargetArmy.Hex.Equals(plan.DeploymentHex)
                        || !CanFitAfterDeploy(plan.TargetArmy, def))
                    { reason = "shell is no longer a valid owned empty army at the deployment hex"; return false; }
                    break;
                case DeploymentKind.Garrison:
                    if (plan.TargetArmy == null || plan.TargetArmy.Owner != player
                        || !plan.TargetArmy.Hex.Equals(plan.DeploymentHex)
                        || !plan.TargetArmy.IsGarrison
                        || !PlacementRules.CanDepositIntoGarrison(plan.TargetArmy)
                        || !CanFitAfterDeploy(plan.TargetArmy, def))
                    { reason = "garrison no longer a valid owned deposit target (reserved slots/capacity)"; return false; }
                    break;
                case DeploymentKind.ExistingArmy:
                    if (plan.TargetArmy == null || plan.TargetArmy.Owner != player
                        || !plan.TargetArmy.Hex.Equals(plan.DeploymentHex)
                        || plan.TargetArmy.IsPrison || plan.TargetArmy.Members.Count == 0
                        || !CanFitAfterDeploy(plan.TargetArmy, def))
                    { reason = "target army no longer owned/valid / projected roster has no room"; return false; }
                    break;
                default:
                    // A corrupt/stale enum value must not be treated as an ExistingArmy plan.
                    // Never allow a new placement mode to inherit existing-army permissions by
                    // accident without an explicit validation branch in this one preflight.
                    reason = "unknown deployment kind";
                    return false;
            }

            // The domain deployment requires HexSelectionController for SpawnUnit. Without it,
            // CreateArmy itself can still consume 2 AP and register an empty shell before the
            // subsequent deployment rejects the null controller. This is a required executable
            // dependency, not just a UI rendering concern; guard the entire chain here.
            if (ctx.HexSelection == null)
            { reason = "missing deployment controller"; return false; }
            // CreateArmy additionally needs the faction catalog. Guard it here instead of
            // offering a plan that can only fail at the next transaction boundary.
            if (plan.RequiresCreateArmy && ctx.StartingDeckCatalog?.GetCatalog(player.Faction) == null)
            { reason = "missing faction army catalog"; return false; }

            return true;
        }

        public static CardPlayResult Play(PlayerSetupData player, PlayerRoot root, AiHandData hand,
            AiTurnContext ctx, CardPlayPlan plan)
        {
            var result = new CardPlayResult { ArmyShell = plan.TargetArmy };
            if (!Preflight(player, root, hand, ctx, plan, out string reason))
            {
                result.FailReason = reason;
                return result;
            }

            int apStart = root.ActionPoints;
            var resStart = Snapshot(root);

            ArmyData shell = plan.TargetArmy;
            bool deployed;
            string deployFail;
            if (plan.RequiresCreateArmy)
            {
                FactionCardCatalog catalog = ctx.StartingDeckCatalog?.GetCatalog(player.Faction);
                deployed = ArmyActions.DeployUnitFromCardToNewArmy(plan.Card.Definition, player,
                    plan.DeploymentHex, catalog, root, ctx.HexSelection, out shell, out deployFail,
                    attachedEquipment: plan.Card.Equipment, sourceCard: plan.Card);
                result.ArmyCreated = deployed && shell != null;
                result.ArmyShell = shell;
            }
            else
            {
                deployed = ArmyActions.DeployUnitFromCard(plan.Card.Definition, player, shell, root,
                    ctx.HexSelection, out deployFail,
                    attachedEquipment: plan.Card.Equipment, sourceCard: plan.Card);
            }

            // The domain transaction is atomic for fresh-army deployment: FALSE means no new army,
            // no AP/resource debit and no published unit. Existing-recipient deployment uses the
            // same validation/payment/spawn core and is measured here for the action ledger.
            result.ApSpent = apStart - root.ActionPoints;
            bool resChanged = !SameResources(resStart, Snapshot(root));
            result.StateChanged = result.ApSpent > 0f || resChanged || result.ArmyCreated;

            if (!deployed)
            {
                result.FailReason = deployFail ?? "DeployUnitFromCard failed";
                Stamp(result, resStart, root);
                return result;
            }

            hand.RemoveCard(plan.Card);   // exactly once, only on success — the canonical V2 boundary
            result.Deployed = true;
            // A successful deploy ALWAYS changed the world: a new unit exists, the target army
            // grew, the hand shrank, capability supply moved — even for a 0-AP / 0-resource card.
            result.StateChanged = true;
            Stamp(result, resStart, root);
            return result;
        }

        private static int[] Snapshot(PlayerRoot root)
        {
            var v = new int[Res.Length];
            for (int i = 0; i < Res.Length; i++)
                v[i] = root.GetResource(Res[i]);
            return v;
        }

        private static bool SameResources(int[] a, int[] b)
        {
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        // Res == { Human, Energy, Materials, Tech }.
        private static ResourceCost DeltaCost(int[] start, int[] end)
        {
            int h = start[0] - end[0], e = start[1] - end[1], m = start[2] - end[2], t = start[3] - end[3];
            return (h | e | m | t) == 0 ? null
                : new ResourceCost { human = h, energy = e, materials = m, tech = t };
        }

        private static void Stamp(CardPlayResult r, int[] resStart, PlayerRoot root)
        {
            r.ResourcesSpent = DeltaCost(resStart, Snapshot(root));
            if (r.StateChanged) V2StateVersion.Bump();
            r.StateVersionAfter = V2StateVersion.Current;
        }
    }
}
