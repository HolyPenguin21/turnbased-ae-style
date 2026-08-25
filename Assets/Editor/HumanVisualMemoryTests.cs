#if UNITY_INCLUDE_TESTS
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class HumanVisualMemoryTests
    {
        private PlayerSetupData _viewer;
        private PlayerSetupData _enemy;

        [SetUp]
        public void SetUp()
        {
            HumanVisualMemory.Clear();
            _viewer = new PlayerSetupData { IsHuman = true, Nickname = "Viewer" };
            _enemy = new PlayerSetupData { Nickname = "Enemy" };
        }

        [TearDown]
        public void TearDown() => HumanVisualMemory.Clear();

        [Test]
        public void ObserveArmy_CapturesPositionAndRosterWithoutTrackingLaterLiveChanges()
        {
            var live = new ArmyData { Name = "Raiders", Owner = _enemy, Hex = new HexCoord(1, 0) };
            live.Members.Add(new UnitData
            {
                Name = "Scout",
                Owner = _enemy,
                HitPointsMax = 4,
                HitPointsCurrent = 3,
                Attack = 2,
            });

            HumanVisualMemory.ObserveArmy(_viewer, live, new HexCoord(3, 0));
            live.Hex = new HexCoord(4, 0);
            live.Members[0].HitPointsCurrent = 1;
            live.Members.Clear();

            Assert.That(HumanVisualMemory.TryGetArmySighting(_viewer, live.Id, out HumanVisualMemory.ArmySighting sighting), Is.True);
            Assert.That(sighting.Hex, Is.EqualTo(new HexCoord(3, 0)));
            Assert.That(sighting.Army.Name, Is.EqualTo("Raiders"));
            Assert.That(sighting.Army.Members, Has.Count.EqualTo(1));
            Assert.That(sighting.Army.Members[0].Name, Is.EqualTo("Scout"));
            Assert.That(sighting.Army.Members[0].HitPointsCurrent, Is.EqualTo(3));
        }

        [Test]
        public void ObserveArmy_ASecondObservedStepReplacesTheLastSeenHexAndRoster()
        {
            var live = new ArmyData { Name = "Raiders", Owner = _enemy, Hex = new HexCoord(1, 0) };
            live.Members.Add(new UnitData { Name = "Scout", Owner = _enemy, HitPointsCurrent = 3 });
            HumanVisualMemory.ObserveArmy(_viewer, live, new HexCoord(2, 0));

            live.Members[0].HitPointsCurrent = 2;
            HumanVisualMemory.ObserveArmy(_viewer, live, new HexCoord(3, 0));

            Assert.That(HumanVisualMemory.TryGetArmySighting(_viewer, live.Id, out HumanVisualMemory.ArmySighting sighting), Is.True);
            Assert.That(sighting.Hex, Is.EqualTo(new HexCoord(3, 0)));
            Assert.That(sighting.Army.Members[0].HitPointsCurrent, Is.EqualTo(2));
        }

        [Test]
        public void EndTurn_ForgetsOnlyThatHumansArmySightings()
        {
            var otherViewer = new PlayerSetupData { IsHuman = true, Nickname = "Other viewer" };
            var live = new ArmyData { Owner = _enemy };
            live.Members.Add(new UnitData { Name = "Scout", Owner = _enemy });
            HumanVisualMemory.ObserveArmy(_viewer, live, new HexCoord(3, 0));
            HumanVisualMemory.ObserveArmy(otherViewer, live, new HexCoord(5, 0));

            HumanVisualMemory.EndTurn(_viewer);

            Assert.That(HumanVisualMemory.TryGetArmySighting(_viewer, live.Id, out _), Is.False);
            Assert.That(HumanVisualMemory.TryGetArmySighting(otherViewer, live.Id, out _), Is.True);
        }

        [Test]
        public void ReobserveHex_RemovesAStoredArmyThatIsNoLongerThere()
        {
            var live = new ArmyData { Owner = _enemy };
            live.Members.Add(new UnitData { Name = "Scout", Owner = _enemy });
            HexCoord lastSeen = new HexCoord(3, 0);
            HumanVisualMemory.ObserveArmy(_viewer, live, lastSeen);

            HumanVisualMemory.ReconcileVisibleHex(_viewer, lastSeen, System.Array.Empty<int>());

            Assert.That(HumanVisualMemory.TryGetArmySighting(_viewer, live.Id, out _), Is.False);
        }
    }
}
#endif
