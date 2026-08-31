using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  INFRASTRUCTURE FULFILLMENT  (Strategy V2 — DEF/ECO/DEV demand consumer for buildings)
    // ===========================================================================================
    //  StrategicManager Phase A calls this for EconomicInfrastructure / DevelopmentInfrastructure
    //  demands, BEFORE the Unit/Hero MaterializationCandidateBuilder loop — infrastructure is a
    //  different chain (a Base/Facility card, or a GameConfig extraction card) played through
    //  BuildingPlayExecutor, not a unit deploy.
    //
    //  GAME-RULE PRECONDITION (parity with the human UI): founding a Base or building an extraction
    //  facility both need one of the player's own HERO-LED armies standing on the target hex.
    //  Strategy V2 has no economy/development mover mission yet, so this fires only when a hero is
    //  already in position — a genuine end-to-end slice (demand -> authoritative API -> world
    //  change -> atomic spend) for exactly that case. Routing a hero to a build site is the
    //  remaining piece (a Develop/BuildFacility mission type).
    // ===========================================================================================
    internal sealed class InfraFulfillResult
    {
        public bool Built;
        public float ApSpent;
        public bool StateChanged;
        public string Detail;

        public static InfraFulfillResult No(string why) => new InfraFulfillResult { Detail = why };
    }

    internal static class InfrastructureFulfillment
    {
        public static bool Handles(CapabilityKind k) =>
            k == CapabilityKind.EconomicInfrastructure || k == CapabilityKind.DevelopmentInfrastructure;

        public static InfraFulfillResult TryFulfill(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, AxisDemand demand)
        {
            if (demand == null || ctx == null || root == null || player == null)
                return InfraFulfillResult.No("missing args");
            if (demand.Capability == CapabilityKind.EconomicInfrastructure)
                return TryEconomy(snap, player, root, hand, ctx, demand);
            if (demand.Capability == CapabilityKind.DevelopmentInfrastructure)
                return TryDevelopment(snap, player, root, hand, ctx, demand);
            return InfraFulfillResult.No("not an infrastructure demand");
        }

        private static InfraFulfillResult TryEconomy(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, AxisDemand demand)
        {
            // Preferred: an extraction facility on the demanded resource hex, when a hero is there.
            if (demand.TargetHex.HasValue)
            {
                HexCoord hex = demand.TargetHex.Value;
                ResourceType? type = ResourceTypeAt(snap, hex);
                if (type.HasValue && HexSelectionController.HasOwnHeroArmyAt(hex, player))
                {
                    // TryBuildExtractionFacility itself rejects a foreign-owned building on the hex.
                    CardDefinition facilityDef = ExtractionDef(ctx, type.Value);
                    if (facilityDef != null)
                    {
                        BuildingPlayResult r = BuildingPlayExecutor.BuildExtractionFacility(
                            player, root, ctx, facilityDef, hex);
                        if (r.Built)
                            return new InfraFulfillResult
                            {
                                Built = true, ApSpent = r.ApSpent, StateChanged = r.StateChanged,
                                Detail = $"extraction {facilityDef.displayName} @({hex.Q},{hex.R}) for {type.Value}",
                            };
                        AiDebugLog.Write($"[AI][V2]   infra.ECO — extraction @({hex.Q},{hex.R}) rejected: {r.FailReason}");
                    }
                }
            }

            // Fallback: found a Base from hand where a hero already stands on bare ground.
            return TryPlayBuildingCardWhereHeroStands(player, root, hand, ctx,
                c => c.Definition != null && c.Definition.cardType == CardType.Base, "ECO");
        }

        private static InfraFulfillResult TryDevelopment(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, AxisDemand demand)
        {
            return TryPlayBuildingCardWhereHeroStands(player, root, hand, ctx,
                c => c.Definition != null && c.Definition.cardType == CardType.Base
                    && c.Definition.grantedAbilities != null
                    && (c.Definition.grantedAbilities.Contains(UnitAbilities.Research)
                        || c.Definition.grantedAbilities.Contains(UnitAbilities.Production)),
                "DEV");
        }

        private static InfraFulfillResult TryPlayBuildingCardWhereHeroStands(PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, System.Func<CardData, bool> cardFilter, string tag)
        {
            if (hand?.Hand == null)
                return InfraFulfillResult.No("no hand");

            CardData card = hand.Hand
                .Where(c => c != null && cardFilter(c))
                .OrderBy(c => c.Definition.displayName, System.StringComparer.Ordinal)
                .FirstOrDefault();
            if (card == null)
                return InfraFulfillResult.No($"{tag}: no eligible building card in hand");

            HexCoord? hex = ArmyRegistry.AllForOwner(player)
                .Where(a => a != null && a.Members.Any(m => m != null && m.IsHero))
                .Select(a => (HexCoord?)a.Hex)
                .OrderBy(h => h.Value.Q).ThenBy(h => h.Value.R)
                .FirstOrDefault(h => BuildingPlayExecutor.PreflightBaseCard(
                    player, root, hand, ctx, card, h.Value, out _));
            if (hex == null)
                return InfraFulfillResult.No($"{tag}: no hero on a legal bare hex for {card.Definition.displayName}");

            BuildingPlayResult r = BuildingPlayExecutor.PlayBaseCard(player, root, hand, ctx, card, hex.Value);
            if (!r.Built)
            {
                AiDebugLog.Write($"[AI][V2]   infra.{tag} — Base {card.Definition.displayName} "
                    + $"@({hex.Value.Q},{hex.Value.R}) failed: {r.FailReason}");
                return InfraFulfillResult.No($"{tag}: {r.FailReason}");
            }
            return new InfraFulfillResult
            {
                Built = true, ApSpent = r.ApSpent, StateChanged = r.StateChanged,
                Detail = $"Base {card.Definition.displayName} @({hex.Value.Q},{hex.Value.R})",
            };
        }

        private static ResourceType? ResourceTypeAt(WorldSnapshot snap, HexCoord hex)
        {
            if (snap?.Known?.ResourceHexes == null)
                return null;
            foreach (System.Collections.Generic.KeyValuePair<HexCoord, ResourceType> kv in snap.Known.ResourceHexes)
                if (kv.Key.Equals(hex))
                    return kv.Value;
            return null;
        }

        private static CardDefinition ExtractionDef(AiTurnContext ctx, ResourceType type)
        {
            CardDefinition[] arr = ctx?.GameConfig?.extractionFacilityCards;
            int i = (int)type;
            return arr != null && i >= 0 && i < arr.Length ? arr[i] : null;
        }
    }
}
