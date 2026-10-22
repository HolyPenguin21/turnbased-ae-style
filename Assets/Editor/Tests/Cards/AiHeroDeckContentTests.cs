#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Game.Cards;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // File-backed content checks also run without the native Unity asset loader.
    public sealed class AiHeroDeckContentTests
    {
        private const string DeckPath = "Assets/Cards/StartingDeckCatalog.asset";
        private static Dictionary<string, CardDefinition> Cards() => new[] { "IronConcord", "TheAshen", "TheVessels", "Neutral" }
            .SelectMany(folder => AttachmentContentTestData.Blocks($"Assets/Cards/{folder}/CardCatalog_{folder}.asset"))
            .Select(block =>
            {
                var card = AttachmentContentTestData.Read(block);
                card.deckPointCost = AttachmentContentTestData.Number(block, "deckPointCost");
                card.deckCopyLimit = AttachmentContentTestData.Number(block, "deckCopyLimit");
                return card;
            }).ToDictionary(c => c.authoredKey);
        private static List<StartingDeck> Decks() => Regex.Split(AttachmentContentTestData.Text(DeckPath)
            .Split(new[] { "  aiDecks:" }, StringSplitOptions.None)[1], @"(?m)^  - deckName: ").Skip(1)
            .Select(block => new StartingDeck
            {
                deckName = block.Split('\n')[0].Trim(),
                leaderCardKey = AttachmentContentTestData.Scalar(block, "leaderCardKey"),
                faction = (Faction)AttachmentContentTestData.Number(block, "faction"),
                cards = Rows(block, "cards"), equipment = Rows(block, "equipment"), mutators = Rows(block, "mutators")
            }).ToList();
        private static List<DeckCardEntry> Rows(string block, string field)
        {
            var section = Regex.Match(block, @"(?ms)^    " + field + @":\r?\n(.*?)(?=^    [A-Za-z]|\z)").Groups[1].Value;
            return Regex.Matches(section, @"cardKey: ([^\r\n]+)\s+count: (\d+)").Cast<Match>()
                .Select(m => new DeckCardEntry { cardKey = m.Groups[1].Value, count = int.Parse(m.Groups[2].Value) }).ToList();
        }

        [Test]
        public void EveryHeroHasExactlyOneLegalNamedDeckWithEconomyAndRecon()
        {
            var cards = Cards(); var decks = Decks();
            Assert.That(decks.Count, Is.EqualTo(24));
            CollectionAssert.AreEquivalent(cards.Values.Where(c => c.cardType == CardType.Hero
                && c.faction != Faction.Neutral).Select(c => c.authoredKey), decks.Select(d => d.leaderCardKey));
            foreach (var deck in decks)
            {
                var leader = cards[deck.leaderCardKey];
                Assert.That(deck.deckName, Is.EqualTo(leader.displayName));
                Assert.That(deck.faction, Is.EqualTo(leader.faction));
                Assert.That(deck.cards.Single(e => e.cardKey == deck.leaderCardKey).count, Is.EqualTo(1));
                var all = deck.cards.Concat(deck.equipment).Concat(deck.mutators).ToList();
                Assert.That(all.Select(e => e.cardKey).Distinct().Count(), Is.EqualTo(all.Count), deck.deckName);
                Assert.That(all.All(e => cards.ContainsKey(e.cardKey) && e.count > 0
                    && e.count <= DeckRules.CopyLimit(cards[e.cardKey])), Is.True, deck.deckName);
                Assert.That(deck.cards.Sum(e => cards[e.cardKey].deckPointCost * e.count), Is.InRange(98, 100));
                Assert.That(deck.cards.Where(e => cards[e.cardKey].cardType == CardType.Base).Sum(e => e.count), Is.EqualTo(1));
                Assert.That(deck.cards.Where(e => cards[e.cardKey].cardType == CardType.Unit && !cards[e.cardKey].isAviation
                    && AbilityParams.GetBestRecceRadius(cards[e.cardKey].grantedAbilities) > 0).Sum(e => e.count), Is.GreaterThanOrEqualTo(4));
                foreach (string facility in new[] { "neutral.facility.lab", "neutral.facility.factory", "neutral.facility.intel" })
                    Assert.That(deck.cards.Single(e => e.cardKey == facility).count, Is.EqualTo(1));
                var heroes = deck.cards.Select(e => cards[e.cardKey]).Where(c => c.cardType == CardType.Hero).ToList();
                Assert.That(heroes.Any(c => c.grantedAbilities.Contains(UnitAbilities.Researcher)), Is.True);
                Assert.That(heroes.Any(c => c.grantedAbilities.Contains(UnitAbilities.Assembler)), Is.True);
                Assert.That(heroes.Any(c => c.commandRating >= 6), Is.True);
            }
        }

        [Test]
        public void BlueprintsHaveNoAiPointChargeAndEveryDeckKeepsTheFullFactionSet()
        {
            var cards = Cards(); var decks = Decks();
            foreach (var faction in new[] { Faction.IronConcord, Faction.Ashen, Faction.Vessels })
            {
                var family = decks.Where(d => d.faction == faction).ToList();
                Assert.That(family.Count, Is.EqualTo(8));
                foreach (var deck in family)
                {
                    Assert.That(deck.equipment.Count, Is.EqualTo(48));
                    Assert.That(deck.mutators.Count, Is.EqualTo(20));
                    Assert.That(deck.equipment.Concat(deck.mutators).All(e => e.count == 1
                        && cards[e.cardKey].cardType == CardType.Equipment), Is.True);
                    CollectionAssert.AreEquivalent(family[0].equipment.Select(e => e.cardKey), deck.equipment.Select(e => e.cardKey));
                    CollectionAssert.AreEquivalent(family[0].mutators.Select(e => e.cardKey), deck.mutators.Select(e => e.cardKey));
                }
            }
        }

        [Test]
        public void ApprovedConcordThemesHaveTheirActualRequiredBodies()
        {
            var decks = Decks(); var cards = Cards();
            int Count(string deck, Func<CardDefinition, bool> predicate) => decks.Single(d => d.deckName == deck)
                .cards.Where(e => predicate(cards[e.cardKey])).Sum(e => e.count);
            Assert.That(Count("Aldric Voss", c => c.authoredKey == "concord.card.ark-carrier"), Is.EqualTo(2));
            Assert.That(Count("Aldric Voss", c => c.isAviation), Is.GreaterThanOrEqualTo(8));
            Assert.That(Count("Kessa Vrail", c => c.unitTypeTags.Contains(UnitTypeTag.Bio)
                && c.unitTypeTags.Contains(UnitTypeTag.Infantry) && c.defenseRating >= 3), Is.GreaterThanOrEqualTo(8));
            Assert.That(Count("Vector Vance", c => c.cardType == CardType.Unit && c.unitTypeTags.Contains(UnitTypeTag.Bio)), Is.GreaterThanOrEqualTo(20));
            Assert.That(Count("Dorian Kesh", c => c.unitTypeTags.Contains(UnitTypeTag.Armored)), Is.GreaterThanOrEqualTo(10));
        }
    }
}
#endif
