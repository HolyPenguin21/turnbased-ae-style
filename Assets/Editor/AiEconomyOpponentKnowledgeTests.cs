#if UNITY_INCLUDE_TESTS
using System;
using Game.Ai;
using Game.Ai.V2;
using Game.Economy;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class AiEconomyOpponentKnowledgeTests
    {
        private static readonly HexCoord Site = new HexCoord(41, -12);

        private static AiMapMemory.KnownBuilding ObservedBuilding(
            PlayerSetupData owner, int energyCapacity) =>
            new AiMapMemory.KnownBuilding(Site, owner, false, Array.Empty<string>(),
                collectedAmounts: new[] { 0, energyCapacity, 0, 0 });

        private static AiMapMemory.KnownResourceHex ObservedYield(int energy) =>
            new AiMapMemory.KnownResourceHex(Site, ResourceType.Energy,
                new ResourceYields { energy = energy });

        private static WorldSnapshot Snapshot(PlayerSetupData opponent,
            int buildingCapacity, int observedEnergy)
        {
            return new WorldSnapshot
            {
                Known = new KnownSnapshot
                {
                    Buildings = new[] { ObservedBuilding(opponent, buildingCapacity) },
                    ResourceHexes = new[] { ObservedYield(observedEnergy) },
                },
                TrueWorld = new TrueWorldSnapshot
                {
                    Opponents = new[]
                    {
                        new OpponentSnapshot
                        {
                            Player = opponent,
                            PerTurnIncome = new ResourceBundle { Energy = 99f },
                        },
                    },
                },
            };
        }

        [Test]
        public void HiddenActualIncomeDoesNotInventAnEconomyComparison()
        {
            var opponent = new PlayerSetupData();
            var snap = new WorldSnapshot
            {
                Known = new KnownSnapshot
                {
                    Buildings = Array.Empty<AiMapMemory.KnownBuilding>(),
                    ResourceHexes = Array.Empty<AiMapMemory.KnownResourceHex>(),
                },
                TrueWorld = new TrueWorldSnapshot
                {
                    Opponents = new[] { new OpponentSnapshot { Player = opponent } },
                },
            };

            float before = WorldAnalysis.ObservedOpponentIncomeFloor(snap, opponent,
                ResourceType.Energy);
            snap.TrueWorld.Opponents[0].PerTurnIncome = new ResourceBundle { Energy = 1000f };
            float after = WorldAnalysis.ObservedOpponentIncomeFloor(snap, opponent,
                ResourceType.Energy);

            Assert.That(before, Is.Zero);
            Assert.That(after, Is.EqualTo(before),
                "Unobserved TrueWorld income must not enter the Economy knowledge boundary.");
            EconomyResourceStanding standing = EconomyStanding.CalculateResource(
                ResourceType.Energy, 2f, after, 0f, 0f, 0f, 0f, 0f);
            Assert.That(standing.RelativeIncomeGap, Is.Zero,
                "Unknown rival income must not create relative economic pressure.");
        }

        [Test]
        public void LastObservedBuildingIsYieldCappedAndOnlyReobservationChangesTheFloor()
        {
            var opponent = new PlayerSetupData();
            WorldSnapshot snap = Snapshot(opponent, buildingCapacity: 7, observedEnergy: 3);
            float before = WorldAnalysis.ObservedOpponentIncomeFloor(snap, opponent,
                ResourceType.Energy);
            Assert.That(before, Is.EqualTo(3f));

            // Simulate a rival's unobserved expansion: TrueWorld changes, the observer's
            // frozen knowledge does not. The lower bound must stay unchanged.
            snap.TrueWorld.Opponents[0].PerTurnIncome = new ResourceBundle { Energy = 800f };
            Assert.That(WorldAnalysis.ObservedOpponentIncomeFloor(snap, opponent,
                ResourceType.Energy), Is.EqualTo(before));

            // Recon actually observes the resource-site change: the next snapshot has an
            // updated yield, still capped by the remembered building's collection capacity.
            snap.Known.ResourceHexes = new[] { ObservedYield(5) };
            Assert.That(WorldAnalysis.ObservedOpponentIncomeFloor(snap, opponent,
                ResourceType.Energy), Is.EqualTo(5f));
            snap.Known.ResourceHexes = Array.Empty<AiMapMemory.KnownResourceHex>();
            Assert.That(WorldAnalysis.ObservedOpponentIncomeFloor(snap, opponent,
                ResourceType.Energy), Is.Zero,
                "A remembered building without a known resource yield cannot invent production.");
        }

        [Test]
        public void EachAiObserverKeepsItsOwnOpponentIncomeKnowledge()
        {
            var opponent = new PlayerSetupData();
            var unrelated = new PlayerSetupData();
            WorldSnapshot informed = Snapshot(opponent, buildingCapacity: 8, observedEnergy: 4);
            WorldSnapshot uninformed = new WorldSnapshot
            {
                Known = new KnownSnapshot
                {
                    Buildings = new[] { ObservedBuilding(unrelated, 8) },
                    ResourceHexes = new[] { ObservedYield(4) },
                },
            };

            Assert.That(WorldAnalysis.ObservedOpponentIncomeFloor(informed, opponent,
                ResourceType.Energy), Is.EqualTo(4f));
            Assert.That(WorldAnalysis.ObservedOpponentIncomeFloor(uninformed, opponent,
                ResourceType.Energy), Is.Zero);
            Assert.That(WorldAnalysis.ObservedOpponentIncomeFloor(informed, unrelated,
                ResourceType.Energy), Is.Zero,
                "Knowledge belonging to one rival must not be attributed to another.");
        }
    }
}
#endif
