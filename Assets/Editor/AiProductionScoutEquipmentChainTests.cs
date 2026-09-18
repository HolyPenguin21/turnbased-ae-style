#if UNITY_INCLUDE_TESTS
using Game.Ai.V2;
using Game.Cards;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Exercises the existing MaterializationChainMatching prefilter/final-ability contract.
    // EnumerateForDemand uses both checks for direct, AttachDeploy and GenerateAttachDeploy.
    public sealed class AiProductionScoutEquipmentChainTests
    {
        [Test]
        public void NonRecceHostCanReachScoutChainsOnlyWhenEquipmentGrantsRecce()
        {
            var host = new CardDefinition { cardType = CardType.Unit };
            var baseAbilities = MaterializationChainMatching.EffectiveAbilities(host, null);
            Assert.That(MaterializationChainMatching.MatchesCapabilityDef(
                host, CapabilityKind.ScoutCapability), Is.True,
                "The host prefilter must leave room for an Equipment-granted Recce ability");
            Assert.That(MaterializationChainMatching.AbilitiesSatisfyCapability(
                baseAbilities, host.cardType, CapabilityKind.ScoutCapability), Is.False,
                "Without Recce, the direct deployment must still fail final capability matching");

            var recceGrant = new EquipmentGrant();
            recceGrant.addAbilities.Add("r1s4");
            var projected = EquipmentSystem.EffectiveAbilities(baseAbilities, recceGrant);
            Assert.That(MaterializationChainMatching.AbilitiesSatisfyCapability(
                projected, host.cardType, CapabilityKind.ScoutCapability), Is.True,
                "Attaching existing or generated Recce Equipment should unlock the Scout chain");

            var facility = new CardDefinition { cardType = CardType.Facility };
            Assert.That(MaterializationChainMatching.MatchesCapabilityDef(
                facility, CapabilityKind.ScoutCapability), Is.False,
                "Opening the prefilter must not admit non-deployable facility cards");
            host.isAviation = true;
            Assert.That(MaterializationChainMatching.MatchesCapabilityDef(
                host, CapabilityKind.ScoutCapability), Is.False,
                "Aviation keeps its own executor; it must not enter ground Scout materialization");
        }
    }
}
#endif
