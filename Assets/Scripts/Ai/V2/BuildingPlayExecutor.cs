using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  BUILDING PLAY EXECUTOR  (Strategy V2 — infrastructure card execution)
    // ===========================================================================================
    //  The SINGLE authoritative V2 path for INFRASTRUCTURE (Base / Facility / extraction site).
    //  Parity with CardPlayExecutor for Unit/Hero: V2 never mutates ownership, building
    //  collections, hand slots or the resource pool by hand — it calls the SAME domain APIs the
    //  human UI (CardHandUI / HexInfoPanelUI) uses:
    //
    //    · CardType.Base card               -> HexSelectionController.SpawnBuilding
    //    · extraction facility (GameConfig) -> HexSelectionController.TryBuildExtractionFacility
    //
    //  ATOMICITY. SpawnBuilding returns null BEFORE any mutation on a bad argument and otherwise
    //  always succeeds without touching AP/resources/hand itself — so this executor preflights the
    //  full cost, calls SpawnBuilding, and only on a non-null result charges AP + pays the resource
    //  cost + removes the card, exactly once. A failure changes nothing: no card lost, no partial
    //  spend, Built=false. TryBuildExtractionFacility is already fully atomic (it validates hero
    //  presence + AP + resources and spends them itself); this executor only measures the real
    //  PlayerRoot delta so the axis ledger stays honest.
    // ===========================================================================================
    public sealed class BuildingPlayResult
    {
        public bool Built;
        public float ApSpent;          // real PlayerRoot AP delta
        public bool StateChanged;
        public bool CardConsumed;
        public string FailReason;

        public static BuildingPlayResult Fail(string why) => new BuildingPlayResult { FailReason = why };
    }

    public static class BuildingPlayExecutor
    {
        private static readonly ResourceType[] Res =
            { ResourceType.Human, ResourceType.Energy, ResourceType.Materials, ResourceType.Tech };

        // Founding a Base from a hand card at `hex`. `hex` must hold one of the player's own
        // hero-led armies (game rule — parity with CardHandUI.IsValidBaseDropTarget), be
        // uncontested and bare. No hand mutation / spend on any failure.
        public static bool PreflightBaseCard(PlayerSetupData player, PlayerRoot root, AiHandData hand,
            AiTurnContext ctx, CardData card, HexCoord hex, out string reason)
        {
            reason = null;
            if (player == null || root == null || hand == null || ctx?.HexSelection == null || card == null)
            { reason = "missing args"; return false; }
            CardDefinition def = card.Definition;
            if (def == null || def.cardType != CardType.Base)
            { reason = $"card type {def?.cardType} is not a Base"; return false; }
            if (!hand.Hand.Contains(card))
            { reason = "card not in hand"; return false; }
            if (!HexSelectionController.HasOwnHeroArmyAt(hex, player))
            { reason = "founding a Base needs one of your hero-led armies on the hex"; return false; }
            if (ArmyRegistry.AllAt(hex).Any(a => a != null && a.Owner != player))
            { reason = "hex is contested"; return false; }
            if (BuildingRegistry.FindAt(hex) != null)
            { reason = "hex already has a building"; return false; }
            int ap = AiCardCost.PlayAp(card);
            if (!root.CanSpendActionPoints(ap))
            { reason = $"need {ap} AP"; return false; }
            ResourceCost cost = AiCardCost.PlayResources(card);
            if (cost != null && !cost.CanAfford(root))
            { reason = "resource cost unaffordable"; return false; }
            if (!string.IsNullOrEmpty(def.requiredBuildingAbility)
                && !PlacementRules.HasRequiredBuilding(player, hex, def))
            { reason = $"no owned '{def.requiredBuildingAbility}' building at the hex"; return false; }
            return true;
        }

        public static BuildingPlayResult PlayBaseCard(PlayerSetupData player, PlayerRoot root,
            AiHandData hand, AiTurnContext ctx, CardData card, HexCoord hex)
        {
            if (!PreflightBaseCard(player, root, hand, ctx, card, hex, out string reason))
                return BuildingPlayResult.Fail(reason);

            int apStart = root.ActionPoints;
            int[] resStart = Snapshot(root);

            BuildingData building = ctx.HexSelection.SpawnBuilding(card.Definition, hex, player);
            if (building == null)
            {
                // SpawnBuilding refuses before it mutates anything — nothing to roll back.
                return new BuildingPlayResult { FailReason = "SpawnBuilding refused (missing scene config)" };
            }

            root.SpendActionPoints(AiCardCost.PlayAp(card));
            AiCardCost.PlayResources(card)?.PayFrom(root);
            hand.Hand.Remove(card);

            return new BuildingPlayResult
            {
                Built = true,
                CardConsumed = true,
                StateChanged = true,
                ApSpent = apStart - root.ActionPoints,
            };
        }

        // Building one of GameConfig.extractionFacilityCards onto `hex` — TryBuildExtractionFacility
        // owns the whole transaction (hero-on-hex check, AP, resources, hero move). These cards are
        // never in hand, so there is no hand boundary here.
        public static BuildingPlayResult BuildExtractionFacility(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, CardDefinition facilityDef, HexCoord hex)
        {
            if (player == null || root == null || ctx?.HexSelection == null || facilityDef == null)
                return BuildingPlayResult.Fail("missing args");
            if (!HexSelectionController.HasOwnHeroArmyAt(hex, player))
                return BuildingPlayResult.Fail("no hero-led army on the resource hex");

            int apStart = root.ActionPoints;
            bool ok = ctx.HexSelection.TryBuildExtractionFacility(facilityDef, hex, player);
            float apSpent = apStart - root.ActionPoints;
            return new BuildingPlayResult
            {
                Built = ok,
                StateChanged = ok || apSpent > 0f,
                ApSpent = apSpent,
                FailReason = ok ? null : "TryBuildExtractionFacility rejected the hex",
            };
        }

        private static int[] Snapshot(PlayerRoot root)
        {
            var v = new int[Res.Length];
            for (int i = 0; i < Res.Length; i++)
                v[i] = root.GetResource(Res[i]);
            return v;
        }
    }
}
