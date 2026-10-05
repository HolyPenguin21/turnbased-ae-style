#if UNITY_INCLUDE_TESTS
using System;
using System.Linq;
using Game.Ai;
using Game.Ai.V2;
using Game.Aviation;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class ArmyRemovalRegistryTests
    {
        private readonly HexCoord _origin = new HexCoord(0, 0);
        private readonly HexCoord _death = new HexCoord(2, 0);
        private PlayerSetupData _owner;
        private PlayerSetupData _observer;

        [SetUp]
        public void SetUp()
        {
            ArmyRegistry.Clear();
            BuildingRegistry.Clear();
            VisionSystem.Clear();
            VisionSystem.Configure(null);
            AiMapMemory.Clear();
            AiMapMemory.EnsureSubscribed();
            AirSortieRegistry.Clear();
            StrategicResourceReservationLedger.ClearAll();
            _owner = new PlayerSetupData { Nickname = "owner" };
            _observer = new PlayerSetupData { Nickname = "observer" };
        }

        [TearDown]
        public void TearDown()
        {
            ArmyRegistry.Clear();
            BuildingRegistry.Clear();
            VisionSystem.Clear();
            AiMapMemory.Clear();
            AirSortieRegistry.Clear();
            StrategicResourceReservationLedger.ClearAll();
        }

        private ArmyData Army(PlayerSetupData owner, HexCoord hex)
        {
            var army = new ArmyData { Owner = owner, Hex = hex, Name = "test" };
            army.Members.Add(new UnitData { Owner = owner, Name = "unit", HitPointsCurrent = 5,
                HitPointsMax = 5, MoveCurrent = 3, MoveMax = 3 });
            ArmyRegistry.Register(army);
            return army;
        }

        [Test]
        public void LateMoveCannotReRegisterRemovedArmyOrChangeDeathHex()
        {
            ArmyData army = Army(_owner, _origin);
            army.Members.Clear();
            ArmyRegistry.Unregister(army, _death);
            ArmyRegistry.MoveArmy(army, new HexCoord(3, 0));
            Assert.That(army.Hex, Is.EqualTo(_death));
            Assert.That(ArmyRegistry.AllForOwner(_owner), Does.Not.Contain(army));
            Assert.That(AiV2Util.ResolveArmy(_owner, army.Id), Is.Null);
        }

        [Test]
        public void RemovalPublishesBothHexesOnlyAfterFinalConsistentState()
        {
            Army(_observer, _origin);
            Army(_observer, _death);
            ArmyData army = Army(_owner, _origin);
            int originWrites = 0;
            int deathWrites = 0;
            Action<PlayerSetupData, HexCoord> changed = (viewer, hex) =>
            {
                if (viewer != _observer) return;
                Assert.That(ArmyRegistry.AllAt(_origin), Does.Not.Contain(army));
                Assert.That(ArmyRegistry.AllAt(_death), Does.Not.Contain(army));
                Assert.That(army.Hex, Is.EqualTo(_death));
                if (hex.Equals(_origin)) originWrites++;
                if (hex.Equals(_death)) deathWrites++;
            };
            VisionSystem.VisibleContentChanged += changed;
            try
            {
                army.Members.Clear();
                ArmyRegistry.Unregister(army, _death);
                ArmyRegistry.Unregister(army, _death);
                ArmyRegistry.MoveArmy(army, _origin);
            }
            finally { VisionSystem.VisibleContentChanged -= changed; }
            Assert.That(originWrites, Is.EqualTo(1));
            Assert.That(deathWrites, Is.EqualTo(1));
        }

        [Test]
        public void VisibleRemovalInvalidatesMemoryAndItsRouteVersion()
        {
            Army(_observer, _origin);
            Army(_observer, _death);
            ArmyData army = Army(_owner, _origin);
            Assert.That(AiMapMemory.AllKnownEnemySightings(_observer).Any(s => s.ArmyId == army.Id), Is.True);
            int before = AiMapMemory.KnowledgeVersionFor(_observer);
            long routeBefore = AiMapMemory.RouteMemoryVersionFor(_observer);
            army.Members.Clear();
            ArmyRegistry.Unregister(army, _death);
            Assert.That(AiMapMemory.AllKnownEnemySightings(_observer).Any(s => s.ArmyId == army.Id), Is.False);
            Assert.That(AiMapMemory.KnowledgeVersionFor(_observer), Is.GreaterThan(before));
            Assert.That(AiMapMemory.RouteMemoryVersionFor(_observer), Is.Not.EqualTo(routeBefore));
        }

        [Test]
        public void HiddenRemovalPreservesHonestHistoricalContact()
        {
            ArmyData observerArmy = Army(_observer, _origin);
            ArmyData army = Army(_owner, _origin);
            ArmyRegistry.Unregister(observerArmy);
            Assert.That(VisionSystem.IsVisible(_observer, _origin), Is.False);
            army.Members.Clear();
            ArmyRegistry.Unregister(army, _death);
            Assert.That(AiMapMemory.AllKnownEnemySightings(_observer).Any(s => s.ArmyId == army.Id), Is.True,
                "a death outside vision cannot reveal hidden live state");
        }

        [Test]
        public void LiveMoveStillCommitsIndexBeforeRelocationEvent()
        {
            ArmyData army = Army(_owner, _origin);
            int events = 0;
            Action<ArmyData, HexCoord, HexCoord> moved = (who, from, to) =>
            {
                if (who != army) return;
                events++;
                Assert.That(ArmyRegistry.AllAt(from), Does.Not.Contain(army));
                Assert.That(ArmyRegistry.AllAt(to), Does.Contain(army));
                Assert.That(army.Hex, Is.EqualTo(to));
            };
            ArmyRegistry.ArmyRelocated += moved;
            try { ArmyRegistry.MoveArmy(army, _death); }
            finally { ArmyRegistry.ArmyRelocated -= moved; }
            Assert.That(events, Is.EqualTo(1));
            Assert.That(AiV2Util.ResolveArmy(_owner, army.Id), Is.SameAs(army));
        }

        [Test]
        public void SortieRemovalIsScopedToLostArmyAndIdempotent()
        {
            ArmyData lost = Army(_owner, _origin);
            ArmyData other = Army(_owner, _origin);
            var lostSortie = new AirSortie { Army = lost, Kind = AirSortieKind.Strike };
            var otherSortie = new AirSortie { Army = other, Kind = AirSortieKind.Strike };
            AirSortieRegistry.Add(_owner, lostSortie);
            AirSortieRegistry.Add(_owner, otherSortie);
            ArmyRegistry.Unregister(lost, _death);
            AirSortieRegistry.Remove(_owner, lost.Id);
            AirSortieRegistry.Remove(_owner, lost.Id);
            Assert.That(AirSortieRegistry.For(_owner), Does.Not.Contain(lostSortie));
            Assert.That(AirSortieRegistry.For(_owner), Does.Contain(otherSortie));
        }

        [Test]
        public void OwnerScopedReserveReleaseLeavesOtherTaskClaimsIntact()
        {
            const int turn = 1;
            StrategicResourceReservationLedger.BeginTurn(_owner, turn);
            foreach (string owner in new[] { "lost-builder", "ground-primary", "other-wing" })
                StrategicResourceReservationLedger.Upsert(_owner, turn, new StrategicResourceReservation
                {
                    Owner = owner, Amount = 3, Resource = StrategicReservedResource.Energy,
                    Reason = StrategicReservationReason.EconomyDeferredBuild,
                    ExpirationStage = StrategicReservationExpiry.EndOfTurn,
                });
            Assert.That(StrategicResourceReservationLedger.ReleaseByOwner(_owner, turn, "lost-builder"), Is.True);
            Assert.That(StrategicResourceReservationLedger.ReleaseByOwner(_owner, turn, "lost-builder"), Is.False);
            Assert.That(StrategicResourceReservationLedger.Active(_owner, turn,
                StrategicReservedResource.Energy), Is.EqualTo(6));
            Assert.That(TurnResourceBook.Free(10, TurnResourceBook.LedgerClaims(_owner, turn),
                StrategicReservedResource.Energy, default), Is.EqualTo(4));
        }
    }
}
#endif
