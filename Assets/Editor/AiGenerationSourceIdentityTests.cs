#if UNITY_INCLUDE_TESTS
using Game.Ai.V2;
using Game.Cards;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Identity regression for the existing GenerationSource retry/portfolio keys. This is a
    // pure key test, not a substitute for an EditMode turn with a real transferred operator.
    public sealed class AiGenerationSourceIdentityTests
    {
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
