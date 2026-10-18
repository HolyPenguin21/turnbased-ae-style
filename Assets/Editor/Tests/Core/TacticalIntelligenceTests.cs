#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Ai;
using Game.Ai.V2;
using Game.Cards;
using Game.Core;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class TacticalIntelligenceTests
    {
        private PlayerSetupData _observer, _enemy;

        [SetUp]
        public void SetUp()
        {
            ArmyRegistry.Clear();
            BuildingRegistry.Clear();
            MissionIntentRegistry.Clear();
            AiHandRegistry.Clear();
            VisionSystem.Clear();
            VisionSystem.Configure(null);
            _observer = new PlayerSetupData { IsHuman = true };
            _enemy = new PlayerSetupData { CitadelHexQ = 0, CitadelHexR = 0 };
        }

        [TearDown]
        public void TearDown()
        {
            ArmyRegistry.Clear();
            BuildingRegistry.Clear();
            MissionIntentRegistry.Clear();
            AiHandRegistry.Clear();
            VisionSystem.Clear();
        }

        private static ArmyData Army(PlayerSetupData owner, int q = 0, int r = 0)
        {
            var army = new ArmyData { Owner = owner, Hex = new HexCoord(q, r) };
            army.Members.Add(new UnitData { HitPointsCurrent = 5, HitPointsMax = 5, Attack = 3 });
            return army;
        }

        // Access tests isolate the read-only lookup from registration's native vision/visual
        // notifications. The actual scene and placement paths are checked by the UI fixture.
        private static void PutBuilding(BuildingData building) =>
            ((Dictionary<HexCoord, BuildingData>)typeof(BuildingRegistry)
                .GetField("ByHex", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null))
                [building.Hex] = building;

        private static void PutArmy(ArmyData army) =>
            ((Dictionary<HexCoord, List<ArmyData>>)typeof(ArmyRegistry)
                .GetField("ByHex", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null))
                [army.Hex] = new List<ArmyData> { army };

        [TestCase(0, 16)]
        [TestCase(1, 12)]
        [TestCase(2, 8)]
        [TestCase(3, 4)]
        [TestCase(4, 0)]
        [TestCase(20, 0)]
        public void DistanceRevealsExactlyOneQuarterPerHex(int distance, int count)
        {
            Assert.That(TacticalIntelligence.VisibleFieldCount(_observer, _enemy,
                new[] { Army(_observer, -distance, distance) }), Is.EqualTo(count));
        }

        [Test]
        public void BestArmyWinsWithoutStackingAndMovingAwayImmediatelyLosesCoverage()
        {
            var near = Army(_observer, 2);
            var far = Army(_observer, 3);
            Assert.That(TacticalIntelligence.VisibleFieldCount(_observer, _enemy,
                new[] { near, far }), Is.EqualTo(8));
            near.Hex = new HexCoord(5, 0);
            Assert.That(TacticalIntelligence.VisibleFieldCount(_observer, _enemy,
                new[] { near, far }), Is.EqualTo(4));
            far.Members.Clear();
            Assert.That(TacticalIntelligence.VisibleFieldCount(_observer, _enemy,
                new[] { near, far }), Is.Zero);
        }

        [Test]
        public void HiddenScoutsAndLoneHeroesObserveButContainersAndDeadUnitsDoNot()
        {
            var army = Army(_observer, 1);
            army.Members[0].IsHero = true;
            army.Members[0].IsHidden = true;
            Assert.That(TacticalIntelligence.VisibleFieldCount(_observer, _enemy,
                new[] { army }), Is.EqualTo(12));
            army.IsGarrison = true;
            Assert.That(TacticalIntelligence.IsGroundObserver(army, _observer), Is.False);
            army.IsGarrison = false; army.IsAirfield = true;
            Assert.That(TacticalIntelligence.IsGroundObserver(army, _observer), Is.False);
            army.IsAirfield = false; army.IsAirArmy = true;
            Assert.That(TacticalIntelligence.IsGroundObserver(army, _observer), Is.False);
            army.IsAirArmy = false; army.IsPrison = true;
            Assert.That(TacticalIntelligence.IsGroundObserver(army, _observer), Is.False);
            army.IsPrison = false; army.Members[0].IsAviation = true;
            Assert.That(TacticalIntelligence.IsGroundObserver(army, _observer), Is.False);
            army.Members[0].IsAviation = false; army.Members[0].HitPointsCurrent = 0;
            Assert.That(TacticalIntelligence.IsGroundObserver(army, _observer), Is.False);
            army.Members[0].HitPointsCurrent = 5; army.Members[0].IsPrisoner = true;
            Assert.That(TacticalIntelligence.IsGroundObserver(army, _observer), Is.False);
            army.Members[0].IsPrisoner = false; army.Members[0].IsSummoned = true;
            Assert.That(TacticalIntelligence.IsGroundObserver(army, _observer), Is.False);
            army.Members[0].IsSummoned = false; army.Owner = _enemy;
            Assert.That(TacticalIntelligence.IsGroundObserver(army, _observer), Is.False);
        }

        [Test]
        public void EachOpponentHasItsOwnCitadelAndUnknownOrEliminatedPlayersRevealNothing()
        {
            var army = Army(_observer, 1);
            var other = new PlayerSetupData { CitadelHexQ = 10, CitadelHexR = 0 };
            Assert.That(TacticalIntelligence.VisibleFieldCount(_observer, other, new[] { army }), Is.Zero);
            _enemy.CitadelHexQ = null;
            Assert.That(TacticalIntelligence.VisibleFieldCount(_observer, _enemy, new[] { army }), Is.Zero);
            _enemy.CitadelHexQ = 0; _enemy.IsEliminated = true;
            Assert.That(TacticalIntelligence.VisibleFieldCount(_observer, _enemy, new[] { army }), Is.Zero);
            Assert.That(TacticalIntelligence.VisibleFieldCount(_observer, _observer, new[] { army }), Is.Zero);
        }

        [Test]
        public void ReportIsRepeatableNestedAndChangesOnPersonalTurns()
        {
            int previous = 0;
            for (int count = 0; count <= 16; count += 4)
            {
                int mask = TacticalIntelligence.RevealMask("match-A", 0, 1, 7, count);
                Assert.That(Enumerable.Range(0, 16).Count(i => (mask & (1 << i)) != 0), Is.EqualTo(count));
                Assert.That(mask & previous, Is.EqualTo(previous));
                Assert.That(TacticalIntelligence.RevealMask("match-A", 0, 1, 7, count), Is.EqualTo(mask));
                previous = mask;
            }
            int current = TacticalIntelligence.RevealMask("match-A", 0, 1, 7, 8);
            Assert.That(TacticalIntelligence.RevealMask("match-A", 0, 1, 8, 8), Is.Not.EqualTo(current));
            Assert.That(TacticalIntelligence.RevealMask("match-A", 0, 2, 7, 8), Is.Not.EqualTo(current));
            Assert.That(TacticalIntelligence.RevealMask("match-B", 0, 1, 7, 8), Is.Not.EqualTo(current));
        }

        [Test]
        public void PartialReportsDoNotConsistentlyFavorEarlyFields()
        {
            var appearances = new int[TacticalIntelligence.FieldCount];
            for (int turn = 0; turn < 5000; turn++)
            {
                int mask = TacticalIntelligence.RevealMask("match-A", 0, 1, turn, 4);
                for (int field = 0; field < appearances.Length; field++)
                    if ((mask & (1 << field)) != 0) appearances[field]++;
            }
            // Expected 1250 per field; deliberately generous limits catch biased shuffles.
            Assert.That(appearances, Is.All.InRange(1000, 1500));
        }

        [Test]
        public void AccessComesFromAbilityOnAnyInPlayCarrierAndFollowsOwnership()
        {
            var building = new BuildingData { Owner = _observer, IsBase = true };
            var facility = new FacilityData { Name = "Any name, not Tactical Center" };
            facility.Abilities.Add(UnitAbilities.TacticalIntelligence);
            Assert.That(TacticalIntelligence.HasAccess(_observer), Is.False, "An unplaced card grants nothing");
            PutBuilding(building);
            building.FacilitySlots[0] = facility;
            Assert.That(TacticalIntelligence.HasAccess(_observer), Is.True);
            building.Owner = _enemy;
            Assert.That(TacticalIntelligence.HasAccess(_observer), Is.False);
            Assert.That(TacticalIntelligence.HasAccess(_enemy), Is.True);
            building.Owner = _observer;
            building.Abilities.Add(UnitAbilities.TacticalIntelligence);
            building.FacilitySlots[0] = null;
            Assert.That(TacticalIntelligence.HasAccess(_observer), Is.True, "Base ability also works");
            building.Abilities.Clear();
            Assert.That(TacticalIntelligence.HasAccess(_observer), Is.False);
            var army = Army(_observer);
            army.Members[0].Abilities.Add(UnitAbilities.TacticalIntelligence);
            PutArmy(army);
            Assert.That(TacticalIntelligence.HasAccess(_observer), Is.True, "Unit ability also works");
            army.Members[0].IsHero = true;
            Assert.That(TacticalIntelligence.HasAccess(_observer), Is.True, "Hero ability also works");
            army.IsPrison = true;
            Assert.That(TacticalIntelligence.HasAccess(_observer), Is.False);
            army.IsPrison = false; _observer.IsEliminated = true;
            Assert.That(TacticalIntelligence.HasAccess(_observer), Is.False);
        }

        [Test]
        public void LosingOneOfTwoCentersKeepsAccessUntilLastOneIsLost()
        {
            var building = new BuildingData { Owner = _observer };
            for (int i = 0; i < 2; i++)
            {
                building.FacilitySlots[i] = new FacilityData();
                building.FacilitySlots[i].Abilities.Add(UnitAbilities.TacticalIntelligence);
            }
            PutBuilding(building);
            building.FacilitySlots[0] = null;
            Assert.That(TacticalIntelligence.HasAccess(_observer), Is.True);
            building.FacilitySlots[1] = null;
            Assert.That(TacticalIntelligence.HasAccess(_observer), Is.False);
        }

        [Test]
        public void ForceReadDoesNotCreateAiHandOrMissionState()
        {
            Assert.That(MissionIntentRegistry.Peek(_enemy), Is.Null);
            Assert.That(AiHandRegistry.Peek(_enemy), Is.Null);
            PlayerForceAnalysis.Calculate(_enemy, new ArmyData[0], null, null);
            Assert.That(MissionIntentRegistry.Peek(_enemy), Is.Null);
            Assert.That(AiHandRegistry.Peek(_enemy), Is.Null);
        }
    }
}
#endif
