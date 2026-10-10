using Game.Cards;
using Game.Core;
using Game.Units;

namespace Game.UI
{
    internal static class CollectionCardDetail
    {
        internal static string Describe(CardDefinition card, GameConfig config, int owned)
        {
            if (card == null) return "Select a card.";
            string text = card.displayName + "\n" + (card.faction == Game.Players.Faction.Neutral || card.faction == Game.Players.Faction.None ? "Shared" : card.faction.ToString()) + " / " + card.cardType
                + "\nPoints: " + card.deckPointCost + "\nOwned: " + owned + " / " + card.deckCopyLimit + "\n\n";
            if (card.cardType == CardType.Equipment) return text + EquipmentCardText.Description(card, config);
            if (card.cardType == CardType.Unit || card.cardType == CardType.Hero)
            {
                var unit = UnitData.CreateProjection();
                unit.Name = card.displayName; unit.IsHero = card.cardType == CardType.Hero;
                unit.Attack = card.attack; unit.Defense = card.defenseRating; unit.Resistance = card.resistanceRating;
                unit.HitPointsMax = unit.HitPointsCurrent = card.hitPoints;
                unit.Range = card.range; unit.MoveMax = unit.MoveCurrent = card.moveMax;
                unit.CommandRating = card.commandRating; unit.Fate = card.fate; unit.Initiative = card.initiative;
                unit.IsAviation = card.isAviation; unit.TurnsWithoutRefuel = card.turnsWithoutRefuel;
                foreach (var tag in card.unitTypeTags) unit.TypeTags.Add(tag);
                foreach (var tag in card.grantedAbilities) unit.Abilities.Add(tag);
                return text + UnitDetailFormatter.Format(unit, config, 0);
            }
            if (card.cardType == CardType.Base) text += $"Defense {card.defenseRating}\nResistance {card.resistanceRating}\nHP {card.hitPoints}\n";
            return text + config.FormatAbilitiesDetailed(card.grantedAbilities);
        }
    }
}
