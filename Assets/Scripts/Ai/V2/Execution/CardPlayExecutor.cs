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
    //  The SINGLE authoritative V2 path for strategic card deployment. V2 code never scatters
    //  hand mutation across StrategicManager / axis planners — card removal from hand happens
    //  HERE, exactly once, only after a successful ArmyActions.DeployUnitFromCard. V1's own
    //  card-play path (AiTurnController.PlayCardRoutine) is untouched, and hand ownership is NOT
    //  moved into ArmyActions.DeployUnitFromCard globally.
    //
    //  Draw / hand cycling is a SEPARATE operation (CardDrawExecutor) — never part of this
    //  transaction.
    //
    //  MULTI-STEP PREFLIGHT. CreateArmy -> DeployUnitFromCard is not atomic in the engine, and
    //  DeployUnitFromCard itself spends AP/resources BEFORE it spawns (a null spawn returns false
    //  with the cost already gone). Play() preflights the whole sequence, then reports the REAL
    //  AP/resource delta measured on PlayerRoot — not the nominal card cost — so the ledger and
    //  the refresh trigger stay honest even on a partial failure. A fresh empty ArmyData left by
    //  CreateArmy after a failed deploy is KEPT as a reusable asset, never rolled back.
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

        // CANONICAL projected battle-cell capacity of a destination roster AFTER `incoming` joins.
        // The ONE place the "a hero rewrites capacity" rule lives for the V2 path — mirrors
        // ArmyActions.DeployUnitFromCard exactly: a hero sets capacity to its CommandRating ONLY
        // when it is the FIRST hero in the roster (a REPLACEMENT of the nominal value, never a
        // Math.Max — a low-CommandRating first hero can make a roomy no-hero base too small); a
        // SUBSEQUENT hero, or any non-hero, leaves the nominal capacity untouched (a second hero is
        // appended after the existing commander and never becomes commander without an explicit
        // TryReorderCommander, which the executor does not do). Both the strategic planner
        // (StrategicEffectRegistry.ResolveDestination) and this preflight go through here so the
        // projected-capacity rule cannot drift between planning and execution.
        internal static int ProjectedCapacityAfterDeploy(
            int nominalCapacity, bool targetHasHero, CardDefinition incoming)
        {
            bool incomingHero = incoming != null && incoming.cardType == CardType.Hero;
            return ArmyCapacityRules.ProjectedCapacity(nominalCapacity, targetHasHero,
                incomingHero ? 1 : 0, incomingHero ? incoming.commandRating : 0);
        }

        // Shared V2 projected-capacity predicate. Mirrors ArmyActions.DeployUnitFromCard: capacity
        // is evaluated after the incoming card joins, so a first hero may raise a full 2/2 army to
        // (for example) 3/5 instead of being rejected by the old pre-join HasRoom value.
        internal static bool CanFitAfterDeploy(ArmyData target, CardDefinition def)
        {
            if (target == null || def == null)
                return false;
            if (target.IsAirfield)
                return true; // V2 rejects aviation cards earlier; airfield capacity is handled elsewhere.
            int projectedCapacity = ProjectedCapacityAfterDeploy(
                target.Capacity, target.Members.Any(m => m.IsHero), def);
            return projectedCapacity >= target.Members.Count + 1;
        }

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
            if (!AiResourceReservation.CanAffordCardPlay(root, player, plan.Card))
            { reason = "resource cost unaffordable"; return false; }

            switch (plan.Kind)
            {
                case DeploymentKind.NewArmy:
                    break; // a fresh army always has room for the first member
                case DeploymentKind.ReusableShell:
                    if (plan.TargetArmy == null || plan.TargetArmy.Members.Count != 0
                        || !plan.TargetArmy.Hex.Equals(plan.DeploymentHex)
                        || !CanFitAfterDeploy(plan.TargetArmy, def))
                    { reason = "shell is no longer a valid empty army at the deployment hex"; return false; }
                    break;
                case DeploymentKind.Garrison:
                    if (plan.TargetArmy == null || !plan.TargetArmy.Hex.Equals(plan.DeploymentHex)
                        || !plan.TargetArmy.IsGarrison
                        || !PlacementRules.CanDepositIntoGarrison(plan.TargetArmy)
                        || !CanFitAfterDeploy(plan.TargetArmy, def))
                    { reason = "garrison no longer a valid deposit target (reserved slots/capacity)"; return false; }
                    break;
                default: // ExistingArmy
                    if (plan.TargetArmy == null || !plan.TargetArmy.Hex.Equals(plan.DeploymentHex)
                        || plan.TargetArmy.IsPrison || plan.TargetArmy.Members.Count == 0
                        || !CanFitAfterDeploy(plan.TargetArmy, def))
                    { reason = "target army no longer valid / projected roster has no room"; return false; }
                    break;
            }

            if (!string.IsNullOrEmpty(def.requiredBuildingAbility)
                && !PlacementRules.HasRequiredBuilding(player, plan.DeploymentHex, def))
            { reason = $"no owned '{def.requiredBuildingAbility}' building at deployment hex"; return false; }

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
            if (plan.RequiresCreateArmy)
            {
                FactionCardCatalog catalog = ctx.StartingDeckCatalog?.GetCatalog(player.Faction);
                shell = ArmyActions.CreateArmy(player, plan.DeploymentHex, catalog, ctx.HexSelection);
                if (shell == null)
                {
                    result.ApSpent = apStart - root.ActionPoints;   // real (0 on a clean refusal)
                    result.StateChanged = result.ApSpent > 0f;
                    result.FailReason = "CreateArmy failed";
                    Stamp(result, resStart, root);
                    return result;
                }
                result.ArmyCreated = true;
                result.ArmyShell = shell;   // an empty army now exists — a retained reusable asset
            }

            bool deployed = ArmyActions.DeployUnitFromCard(plan.Card.Definition, player, shell, root,
                ctx.HexSelection, out string deployFail,
                attachedEquipment: plan.Card.Equipment, sourceCard: plan.Card);

            // Real mutation, measured — DeployUnitFromCard spends AP/resources before it spawns, so
            // even a FALSE return can have moved the books.
            result.ApSpent = apStart - root.ActionPoints;
            bool resChanged = !SameResources(resStart, Snapshot(root));
            result.StateChanged = result.ApSpent > 0f || resChanged || result.ArmyCreated;

            if (!deployed)
            {
                result.FailReason = deployFail ?? "DeployUnitFromCard failed";
                Stamp(result, resStart, root);
                return result;
            }

            hand.Hand.Remove(plan.Card);   // exactly once, only on success — the canonical V2 boundary
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
