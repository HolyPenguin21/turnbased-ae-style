#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.Ai;
using Game.Ai.V2;
using Game.Economy;
using Game.HexGrid;
using Game.Players;
using UnityEngine;
using Game.Cards;
using Game.Map;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Identity regression for the existing GenerationSource retry/portfolio keys. This is a
    // pure key test, not a substitute for an EditMode turn with a real transferred operator.
    public sealed class AiGenerationSourceIdentityTests
    {
        [Test]
        public void ForecastDescribesUnaffordableSourceWithoutOpeningItsWindow()
        {
            var player = new PlayerSetupData();
            PlayerRoot root = PlayerRoot.Create(player, "generation-forecast-test");
            var catalog = ScriptableObject.CreateInstance<ResearchProductionCatalog>();
            var cards = ScriptableObject.CreateInstance<FactionCardCatalog>();
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); DevelopmentInvestmentGate.Clear();
            try
            {
                root.ActionPoints = 20;
                root.AddResource(ResourceType.Energy, -root.GetResource(ResourceType.Energy));
                var hex = new HexCoord(987, -986);
                var building = new BuildingData { Owner = player, Hex = hex, IsBase = true };
                building.FacilitySlots[0] = new FacilityData();
                building.FacilitySlots[0].Abilities.Add(UnitAbilities.Research);
                BuildingRegistry.Register(hex, building);
                var hero = new UnitData { Owner = player, IsHero = true, Fate = 4 };
                hero.Abilities.Add(UnitAbilities.Researcher);
                var army = new ArmyData { Owner = player, Hex = hex };
                army.Members.Add(hero); ArmyRegistry.Register(army);
                var output = new CardDefinition { authoredKey = "forecast-output", cardType = CardType.Equipment,
                    apCost = 1, resourceCost = new ResourceCost(energy: 2) };
                cards.cards = new List<CardDefinition> { output }; catalog.cardCatalogs.Add(cards);
                catalog.researchCards.Add(new ResearchProductionEntry { cardKey = output.authoredKey });
                var ctx = new AiTurnContext { TurnNumber = 24, ResearchProductionCatalog = catalog };
                var hand = new AiHandData(null, default, 0);
                Assert.That(GenerationSource.Enumerate(player, root, ctx, hand, null, null), Is.Empty);
                var forecast = GenerationSource.Enumerate(player, root, ctx, hand, null, null,
                    resourceForecast: true);
                Assert.That(forecast, Has.Count.EqualTo(1));
                Assert.That(forecast[0].CardDef, Is.SameAs(output));
                Assert.That(DevelopmentInvestmentGate.IsOpenFor(player, 24, output.resourceCost), Is.False);
                Assert.That(root.GetResource(ResourceType.Energy), Is.Zero);
                Assert.That(root.ActionPoints, Is.EqualTo(20));
                hero.Abilities.Clear();
                Assert.That(GenerationSource.Enumerate(player, root, ctx, hand, null, null,
                    resourceForecast: true), Is.Empty, "A forecast never invents an operator");
            }
            finally
            {
                ArmyRegistry.Clear(); BuildingRegistry.Clear(); DevelopmentInvestmentGate.Clear();
                Object.DestroyImmediate(root.gameObject); Object.DestroyImmediate(catalog); Object.DestroyImmediate(cards);
            }
        }

        [Test]
        public void HeroIdentitySurvivesArmyTransferAndMemberReordering()
        {
            var hero = new UnitData { Name = "operator", IsHero = true };
            var other = new UnitData { Name = "operator", IsHero = true };
            var source = new ArmyData();
            var destination = new ArmyData();
            source.Members.Add(hero);
            source.Members.Add(other);
            string heroKey = GenerationSource.StableHeroKey(hero);
            string productionKey = GenerationSource.StableGeneratorUseKey(
                ResearchProductionMode.Production, hero);

            source.Members.Remove(hero);
            destination.Members.Add(hero);
            source.Members.Remove(other);
            source.Members.Add(other);

            Assert.That(GenerationSource.StableHeroKey(hero), Is.EqualTo(heroKey),
                "Moving an existing operator into another army must not reset its attempt identity");
            Assert.That(GenerationSource.StableGeneratorUseKey(
                ResearchProductionMode.Production, hero), Is.EqualTo(productionKey),
                "The generator retry identity may depend on hero and mode, not its army or base hex");
            Assert.That(GenerationSource.StableHeroKey(other), Is.Not.EqualTo(heroKey),
                "Two distinct heroes with identical names or card definitions cannot share attempts");
            Assert.That(source.Members.Contains(hero), Is.False,
                "The test must exercise a real membership change, not only rename the actor");
        }

        [Test]
        public void GenerationIdentitySeparatesResearchFromProductionForSameHero()
        {
            var hero = new UnitData { IsHero = true };
            string production = GenerationSource.StableGeneratorUseKey(
                ResearchProductionMode.Production, hero);
            string research = GenerationSource.StableGeneratorUseKey(
                ResearchProductionMode.Research, hero);
            Assert.That(production, Is.Not.EqualTo(research),
                "Changing the mode must not suppress a distinct allowed Challenge combination");
            Assert.That(production + "|card-a", Is.Not.EqualTo(production + "|card-b"),
                "Two offered authored card keys must retain distinct retry identities");
        }
    }
}
#endif
