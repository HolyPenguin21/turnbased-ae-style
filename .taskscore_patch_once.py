from pathlib import Path
import uuid


def patch(name, before, after):
    path = Path(name)
    content = path.read_text()
    n = content.count(before)
    if n != 1:
        raise RuntimeError(f'{name}: expected one match, got {n}: {before[:110]}')
    path.write_text(content.replace(before, after, 1))
    print('PATCHED', name)


patch('Assets/Scripts/Ai/V2/Allocation/ResourceAllocator.cs',
      'if (committedApSoFar + askAp > pool.Ap + eps)',
      'if (lockedTotal + committedApSoFar + askAp > pool.Ap + eps)')

demand = 'Assets/Scripts/Ai/V2/Strategy/Demand/DemandLayer.Economy.cs'
patch(demand, '''                    float existingLoss = site.ConvertsOwnedExtractionSite
                        ? TaskScoreEvaluator.EconomicHexBenefit(site.LostExtractionIncome, 0f)
                        : 0f;''', '''                    // InfrastructureActions.TryFoundBase carries the extraction facilities
                    // into the new Base: their production is preserved, not lost.
                    float existingLoss = 0f;''')
patch(demand, '''float typeGain = StrategicCardEvaluator.BaseCardMarginalGain(
                            site.HexYield, card.Definition, type);''', '''float typeGain = StrategicCardEvaluator.BaseCardMarginalGain(
                            s, site, card.Definition, type);''')

evaluator = 'Assets/Scripts/Ai/V2/Evaluation/Cards/StrategicCardEvaluator.cs'
patch(evaluator, 'float marginalHexYield = BaseCardMarginalYield(site.HexYield, card.Definition);',
      'float marginalHexYield = BaseCardMarginalYield(s, site, card.Definition);')
patch(evaluator, '''        // `yield` is the hex's remaining UNCOLLECTED amount per type (structural site fact, see''',
'''        // Net OWNER gain, not the gross Base collection. A Base takes the first cut,
        // and can displace our own army's collection without raising the owner's income.
        internal static float BaseCardMarginalYield(WorldSnapshot s, EconomyBaseOpportunity site,
            CardDefinition definition) => ResourceBundle.All.Sum(type =>
                BaseCardMarginalGain(s, site, definition, type));

        internal static float BaseCardMarginalGain(WorldSnapshot s, EconomyBaseOpportunity site,
            CardDefinition definition, ResourceType type)
        {
            int addedCapacity = Mathf.RoundToInt(BaseCardMarginalGain(site.HexYield, definition, type));
            if (addedCapacity <= 0)
                return 0f;
            int remainingYield = Mathf.RoundToInt(site.HexYield.Get(type));
            int ownArmyCollectors = Mathf.RoundToInt((s?.Self?.Armies
                ?? System.Array.Empty<ArmySnapshot>())
                .Where(a => a != null && a.Hex.Equals(site.Hex))
                .Sum(a => a.CollectionCapacity.Get(type)));
            bool armiesCanCollect = !(s?.Known?.EnemySightings
                ?? System.Array.Empty<Game.Ai.AiMapMemory.KnownEnemySighting>())
                .Any(enemy => enemy.Hex.Equals(site.Hex));
            // BaseUncollectedYield has already subtracted existing building collection:
            // work on the remainder to avoid charging carried-over facilities twice.
            return IncomeProjection.MarginalOwnerCollectionAtHex(
                remainingYield, 0, addedCapacity, ownArmyCollectors, armiesCanCollect);
        }

        // `yield` is the hex's remaining UNCOLLECTED amount per type (structural site fact, see''')

test = Path('Assets/Editor/AiTaskScoreBaseIncomeRegressionTests.cs')
if test.exists():
    raise RuntimeError('Refusing to replace existing regression tests')
test.write_text('''#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.Ai.V2;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class AiTaskScoreBaseIncomeRegressionTests
    {
        [Test]
        public void BaseDoesNotClaimAlreadyHarvestedArmyIncome()
        {
            var hex = new HexCoord(2, -1);
            var card = new CardDefinition { cardType = CardType.Base,
                grantedAbilities = new List<string>
                { UnitAbilities.CollectAbilityFor(ResourceType.Materials) } };
            var site = new EconomyBaseOpportunity { Hex = hex,
                HexYield = new ResourceBundle { Materials = 1 } };
            var snap = new WorldSnapshot { Self = new SelfSnapshot
            {
                Armies = new List<ArmySnapshot> { new ArmySnapshot
                {
                    Hex = hex, CollectionCapacity = new ResourceBundle { Materials = 1 }
                } }
            } };
            Assert.That(StrategicCardEvaluator.BaseCardMarginalGain(snap, site,
                card, ResourceType.Materials), Is.Zero);
            Assert.That(StrategicCardEvaluator.BaseCardMarginalYield(snap, site, card), Is.Zero);
            snap.Self.Armies = new List<ArmySnapshot>();
            Assert.That(StrategicCardEvaluator.BaseCardMarginalGain(snap, site,
                card, ResourceType.Materials), Is.EqualTo(1f));
            site.HexYield = new ResourceBundle { Materials = 2 };
            snap.Self.Armies = new List<ArmySnapshot> { new ArmySnapshot
            {
                Hex = hex, CollectionCapacity = new ResourceBundle { Materials = 1 }
            } };
            Assert.That(StrategicCardEvaluator.BaseCardMarginalGain(snap, site,
                card, ResourceType.Materials), Is.EqualTo(1f));
        }

        [Test]
        public void MultiResourceBaseUsesIndependentNetCollection()
        {
            var hex = new HexCoord(3, 0);
            var card = new CardDefinition { cardType = CardType.Base,
                grantedAbilities = new List<string>
                {
                    UnitAbilities.CollectAbilityFor(ResourceType.Materials),
                    UnitAbilities.CollectAbilityFor(ResourceType.Energy)
                } };
            var site = new EconomyBaseOpportunity { Hex = hex,
                HexYield = new ResourceBundle { Materials = 1, Energy = 2 } };
            var snap = new WorldSnapshot { Self = new SelfSnapshot
            {
                Armies = new List<ArmySnapshot> { new ArmySnapshot
                {
                    Hex = hex, CollectionCapacity = new ResourceBundle { Materials = 1 }
                } }
            } };
            Assert.That(StrategicCardEvaluator.BaseCardMarginalGain(snap, site,
                card, ResourceType.Materials), Is.Zero);
            Assert.That(StrategicCardEvaluator.BaseCardMarginalGain(snap, site,
                card, ResourceType.Energy), Is.EqualTo(1f));
            Assert.That(StrategicCardEvaluator.BaseCardMarginalYield(snap, site, card),
                Is.EqualTo(1f));
            Assert.That(IncomeProjection.MarginalOwnerCollectionAtHex(1, 0, 1, 1, true), Is.Zero);
        }

        [Test]
        public void ExtractionFacilitiesAreEligibleForLosslessBaseMerge()
        {
            var site = new BuildingData { HasTieredUnlock = false };
            var facility = new CardDefinition { cardType = CardType.Facility,
                grantedAbilities = new List<string>
                { UnitAbilities.CollectAbilityFor(ResourceType.Materials) } };
            site.FacilitySlots[0] = FacilityData.FromDefinition(facility);
            Assert.That(site.CollectedAmount(ResourceType.Materials), Is.EqualTo(1));
            Assert.That(InfrastructureActions.CanMergeIntoResourceSite(site), Is.True);
        }
    }
}
#endif
''')
Path(str(test) + '.meta').write_text('fileFormatVersion: 2\nguid: ' + uuid.uuid4().hex + '\n')
print('EXACT_PATCHES_APPLIED_AND_TEST_WRITTEN')
