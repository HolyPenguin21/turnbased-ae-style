using System;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Core;
using Game.Units;

namespace Game.UI
{
    // Text for the collection window's three detail fields: title, type line, description.
    internal static class CollectionCardDetail
    {
        // Single-text form for popups that have one label (reward messages).
        internal static string Describe(CardDefinition card, GameConfig config, int owned)
            => card == null ? Description(card, config, owned)
                : Title(card) + "\n" + Type(card) + "\n" + Description(card, config, owned);

        internal static string Title(CardDefinition card) => card == null ? string.Empty : card.displayName;

        // "N points - CardType / Tag / Tag"; a tag that repeats the card type (Hero / Bio / Hero) or an earlier tag is skipped.
        internal static string Type(CardDefinition card)
        {
            if (card == null) return string.Empty;
            var parts = new List<string> { card.cardType.ToString() };
            foreach (var tag in card.unitTypeTags)
            {
                string name = tag.ToString();
                if (!parts.Contains(name, StringComparer.OrdinalIgnoreCase)) parts.Add(name);
            }
            return card.deckPointCost + " points - " + string.Join(" / ", parts);
        }

        // Game stats, then abilities (name + full description from UnitAbilityCatalog). Name, cost and type tags
        // are left to Title/Type, so nothing is shown twice. `owned` is kept for callers; ownership is on the card cell.
        internal static string Description(CardDefinition card, GameConfig config, int owned)
        {
            if (card == null) return string.Empty;
            string text = string.Empty;
            if (card.cardType == CardType.Equipment) return text + EquipmentCardText.Description(card, config);
            if (card.cardType == CardType.Unit || card.cardType == CardType.Hero)
            {
                var unit = UnitData.CreateProjection();
                unit.Name = card.displayName; unit.IsHero = card.cardType == CardType.Hero;
                unit.Attack = card.attack; unit.Defense = card.defenseRating;
                unit.HitPointsMax = unit.HitPointsCurrent = card.hitPoints;
                unit.Range = card.range; unit.MoveMax = unit.MoveCurrent = card.moveMax;
                unit.CommandRating = card.commandRating; unit.Fate = card.fate; unit.Initiative = card.initiative;
                unit.IsAviation = card.isAviation; unit.TurnsWithoutRefuel = card.turnsWithoutRefuel;
                foreach (var tag in card.unitTypeTags) unit.TypeTags.Add(tag);
                foreach (var tag in card.grantedAbilities) unit.Abilities.Add(tag);
                return text + UnitDetailFormatter.FormatBody(unit, config);
            }
            if (card.cardType == CardType.Base || card.cardType == CardType.Facility)
                text += $"Level 1\nDefense {card.defenseRating}\nStructure points {card.hitPoints}\nFate {card.fate}\n";
            return text + config.FormatAbilitiesDetailed(card.grantedAbilities);
        }
    }
}
