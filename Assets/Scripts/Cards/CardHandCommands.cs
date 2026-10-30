using System.Linq;
using Game.Aviation;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Cards
{
    public enum CardPlayTargetKind { None, Hex, Army, Building, HandCard, Unit }

    // UI supplies only the selected surface/identity. The same validation and execution
    // resolves both hex and modal targets against the live world.
    public readonly struct CardPlayTarget
    {
        public readonly CardPlayTargetKind Kind;
        public readonly HexCoord Hex;
        public readonly ArmyData Army;
        public readonly BuildingData Building;
        public readonly CardData HandCard;
        public readonly UnitData Unit;
        private CardPlayTarget(CardPlayTargetKind kind, HexCoord hex = default, ArmyData army = null,
            BuildingData building = null, CardData handCard = null, UnitData unit = null)
        { Kind = kind; Hex = hex; Army = army; Building = building; HandCard = handCard; Unit = unit; }
        public static CardPlayTarget AtHex(HexCoord hex) => new CardPlayTarget(CardPlayTargetKind.Hex, hex);
        public static CardPlayTarget InArmy(ArmyData army) => new CardPlayTarget(CardPlayTargetKind.Army, army: army);
        public static CardPlayTarget InBuilding(BuildingData building) => new CardPlayTarget(CardPlayTargetKind.Building, building: building);
        public static CardPlayTarget OnCard(CardData card) => new CardPlayTarget(CardPlayTargetKind.HandCard, handCard: card);
        public static CardPlayTarget OnUnit(UnitData unit) => new CardPlayTarget(CardPlayTargetKind.Unit, unit: unit);
    }

    public readonly struct CardCommandResult
    {
        public readonly bool Ok;
        public readonly string FailReason;
        public readonly CardData Card;
        public readonly PaymentQuote Cost;
        private CardCommandResult(bool ok, string reason, CardData card, PaymentQuote cost)
        { Ok = ok; FailReason = reason; Card = card; Cost = cost; }
        internal static CardCommandResult Fail(string reason) => new CardCommandResult(false, reason, null, default);
        internal static CardCommandResult Success(CardData card, PaymentQuote cost) => new CardCommandResult(true, null, card, cost);
    }

    public static class CardHandCommands
    {
        private static bool Account(PlayerSetupData owner, PlayerHandData hand, out PlayerRoot root, out string reason)
        {
            root = PlayerRootRegistry.FindFor(owner); reason = null;
            if (owner == null || owner.IsEliminated || hand == null || !ReferenceEquals(owner.Hand, hand)
                || root == null || !ReferenceEquals(root.Setup, owner))
            { reason = "This player's hand or account is no longer available."; return false; }
            return true;
        }
        private static bool Holds(PlayerHandData hand, CardData card) => card?.Definition != null
            && hand != null && hand.Hand.Any(c => c.RuntimeId == card.RuntimeId && ReferenceEquals(c, card));

        public static CardCommandResult TryDraw(PlayerSetupData owner, PlayerHandData hand, int apCost)
        {
            using var mutation = VisionSystem.BeginMutation();
            if (!Account(owner, hand, out var root, out string reason)) return CardCommandResult.Fail(reason);
            if (!hand.HasFreeSlot) return CardCommandResult.Fail($"Hand full ({hand.Capacity}). Play or discard a card before drawing.");
            if (!hand.HasCardsLeftToDraw || hand.RemainingDeck.Any(c => c == null))
                return CardCommandResult.Fail("No cards left to draw.");
            var quote = new PaymentQuote(apCost, null);
            if (!quote.TryDebit(root, out var receipt, out _)) return CardCommandResult.Fail("Not enough AP to draw a card.");
            var card = hand.DrawOne();
            if (card == null)
            { receipt.TryCompensate(); mutation.Discard(); return CardCommandResult.Fail("Could not draw a card."); }
            return CardCommandResult.Success(card, quote);
        }

        // Quotes are advisory. TryPlay revalidates owner, exact instance, target, capacity and
        // affordability immediately before executing; it never trusts a previously made quote.
        public static bool TryQuotePlay(PlayerSetupData owner, PlayerHandData hand, CardData card,
            CardPlayTarget target, HexMap map, out PaymentQuote quote, out string reason)
            => Prepare(owner, hand, card, target, map, out _, out _, out _, out quote, out reason);

        private static bool Prepare(PlayerSetupData owner, PlayerHandData hand, CardData card,
            CardPlayTarget target, HexMap map, out PlayerRoot root, out ArmyData army, out HexCoord hex,
            out PaymentQuote quote, out string reason)
        {
            army = null; hex = target.Hex; quote = default;
            if (!Account(owner, hand, out root, out reason)) return false;
            if (!Holds(hand, card))
            { reason = "This exact card is no longer in your hand."; return false; }
            quote = new PaymentQuote(CardCostRules.PlayAp(card), CardCostRules.PlayResources(card));
            var def = card.Definition;
            if (def.cardType == CardType.Equipment)
            {
                if (target.Kind == CardPlayTargetKind.HandCard && !ReferenceEquals(card, target.HandCard)
                    && Holds(hand, target.HandCard)) return EquipmentSystem.CanAttach(card, target.HandCard, root, out reason);
                if (target.Kind == CardPlayTargetKind.Unit)
                {
                    army = ArmyRegistry.FindArmyContaining(target.Unit);
                    if (army != null && army.Owner == owner && ArmyRegistry.IsRegistered(army) && !army.IsPrison
                        && target.Unit.Owner == owner && !target.Unit.IsPrisoner && target.Unit.HitPointsCurrent > 0)
                        return EquipmentSystem.CanAttach(card, target.Unit, root, out reason);
                }
                reason = "Select one of your live units or a card in your hand."; return false;
            }
            if (target.Kind == CardPlayTargetKind.Army)
            {
                army = target.Army;
                if (army == null || army.Owner != owner || !ArmyRegistry.IsRegistered(army))
                { reason = "This army is no longer available."; return false; }
                hex = army.Hex;
            }
            else if (target.Kind == CardPlayTargetKind.Building)
            {
                var building = target.Building;
                if (building == null || building.Owner != owner || !ReferenceEquals(BuildingRegistry.FindAt(building.Hex), building))
                { reason = "This building is no longer available."; return false; }
                hex = building.Hex;
            }
            else if (target.Kind != CardPlayTargetKind.Hex)
            { reason = "Select a valid card target."; return false; }
            if (map == null || !map.CanEnter(hex))
            { reason = "Cards cannot be played on this terrain."; return false; }
            switch (def.cardType)
            {
                case CardType.Unit:
                case CardType.Hero:
                    if (target.Kind == CardPlayTargetKind.Building)
                    { reason = "Deploy this card to an army or hex."; return false; }
                    army = army ?? (def.isAviation ? AviationRules.FindAirfieldAt(hex, owner) : ArmyRegistry.FindGarrisonAt(hex, owner));
                    return ArmyActions.CanDeployCard(card, owner, army, root, map, out reason);
                case CardType.Base:
                    if (target.Kind != CardPlayTargetKind.Hex)
                    { reason = "Select a hex for your base."; return false; }
                    return InfrastructureActions.CanFoundBase(def, hex, owner, quote.Ap, card.EffectivePlayResourceCost, out reason, map);
                case CardType.Facility:
                    if (target.Kind == CardPlayTargetKind.Army)
                    { reason = "Facilities require a base."; return false; }
                    return InfrastructureActions.CanPlaceFacility(def, hex, owner, quote.Ap, card.EffectivePlayResourceCost, out reason);
                default: reason = "This card cannot be played to this target."; return false;
            }
        }

        public static CardCommandResult TryPlay(PlayerSetupData owner, PlayerHandData hand, CardData card,
            CardPlayTarget target, HexMap map, HexSelectionController presentation = null)
        {
            using var mutation = VisionSystem.BeginMutation();
            if (!Prepare(owner, hand, card, target, map, out var root, out var army, out var hex,
                    out var quote, out string reason)) return CardCommandResult.Fail(reason);
            bool applied;
            switch (card.Definition.cardType)
            {
                case CardType.Unit:
                case CardType.Hero:
                    applied = ArmyActions.TryDeployCard(card, owner, army, root, map, presentation, out reason);
                    break;
                case CardType.Base:
                    var founded = InfrastructureActions.TryFoundBaseOnMap(map, card.Definition, hex, owner,
                        quote.Ap, card.EffectivePlayResourceCost, presentation);
                    applied = founded.Ok; reason = founded.FailReason; break;
                case CardType.Facility:
                    var facility = InfrastructureActions.TryPlaceFacility(card.Definition, hex, owner, quote.Ap, card.EffectivePlayResourceCost);
                    applied = facility.Ok; reason = facility.FailReason; break;
                case CardType.Equipment:
                    applied = target.Kind == CardPlayTargetKind.HandCard
                        ? EquipmentSystem.TryAttach(card, target.HandCard, root, out reason)
                        : EquipmentSystem.TryAttach(card, target.Unit, root, out reason);
                    break;
                default: return CardCommandResult.Fail("Unsupported card play.");
            }
            if (!applied) return CardCommandResult.Fail(reason);
            // No user callback or notification runs between validation and this consume.
            // The outer scope publishes only after account, target and hand are final.
            hand.RemoveCard(card);
            return CardCommandResult.Success(card, quote);
        }
    }
}
