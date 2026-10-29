#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Aviation;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Setup;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class AviationFuelDeathNotificationTests
    {
        private readonly HexCoord _hex = new HexCoord(3, 3);
        private PlayerSetupData _owner;
        private PlayerSetupData _observer;

        [SetUp]
        public void SetUp()
        {
            ArmyRegistry.Clear();
            BuildingRegistry.Clear();
            VisionSystem.Clear();
            VisionSystem.Configure(null);
            StealthSystem.Clear();
            _owner = new PlayerSetupData { PlayerId = "fuel-owner", Nickname = "owner" };
            _observer = new PlayerSetupData { PlayerId = "fuel-observer", Nickname = "observer" };
            // Arrange known visibility without needing a scene/config or recomputing vision.
            VisionSystem.RestoreState(new VisionState { Players = new List<PlayerVisionState>
            {
                new PlayerVisionState { PlayerId = _observer.PlayerId, Visible = new List<HexCoord> { _hex } }
            } }, id => id == _observer.PlayerId ? _observer : _owner);
        }

        [TearDown]
        public void TearDown()
        {
            ArmyRegistry.Clear();
            BuildingRegistry.Clear();
            VisionSystem.Clear();
            StealthSystem.Clear();
        }

        // The last aircraft dies: a notify inside the damage loop used to leave the
        // observer's final snapshot containing that dead member, with no later refresh.
        [TestCase(4, 1)]
        [TestCase(1, 4)]
        [TestCase(4, 4)]
        [TestCase(1, 1)]
        public void FuelDamagePublishesOnlyFinalRosterAndClearedDetection(int firstHp, int lastHp)
        {
            UnitData first = Aircraft("first", firstHp);
            UnitData last = Aircraft("last", lastHp);
            var wing = new ArmyData { Owner = _owner, Hex = _hex, Name = "wing" };
            wing.Members.Add(first);
            wing.Members.Add(last);
            using (MatchRestoreScope.Begin()) ArmyRegistry.Register(wing);
            foreach (UnitData aircraft in wing.Members)
            {
                aircraft.IsHidden = true;
                StealthSystem.MarkDetected(aircraft, _observer);
            }

            UnitData[] survivors = new[] { first, last }.Where(u => u.HitPointsCurrent == 4).ToArray();
            UnitData[] dead = new[] { first, last }.Where(u => u.HitPointsCurrent == 1).ToArray();
            int notifications = 0;
            int cleanupNotifications = 0;
            Action<PlayerSetupData, HexCoord> changed = (viewer, hex) =>
            {
                if (viewer != _observer || !hex.Equals(_hex)) return;
                notifications++;
                Assert.That(wing.Members, Is.EqualTo(survivors), "notify must follow the entire fuel sweep");
                foreach (UnitData aircraft in survivors)
                {
                    Assert.That(aircraft.HitPointsCurrent, Is.EqualTo(2));
                    Assert.That(aircraft.HasEmergencyFlightPenalty, Is.True);
                }
                foreach (UnitData aircraft in dead)
                {
                    Assert.That(aircraft.HitPointsCurrent, Is.Zero);
                    Assert.That(aircraft.IsHidden, Is.False);
                    Assert.That(StealthSystem.IsDetectedBy(aircraft, _observer), Is.False);
                    Assert.That(ArmyRegistry.FindArmyContaining(aircraft), Is.Null);
                }
            };
            Action cleaned = () =>
            {
                cleanupNotifications++;
                Assert.That(wing.Members, Is.EqualTo(survivors), "stealth observers also need the final roster");
                foreach (UnitData aircraft in survivors)
                    Assert.That(aircraft.HitPointsCurrent, Is.EqualTo(2));
                foreach (UnitData aircraft in dead)
                    Assert.That(aircraft.HitPointsCurrent, Is.Zero);
            };
            VisionSystem.VisibleContentChanged += changed;
            StealthSystem.StealthChanged += cleaned;
            try { AviationTurnLifecycle.ResolveEndOfTurn(_owner, null); }
            finally
            {
                VisionSystem.VisibleContentChanged -= changed;
                StealthSystem.StealthChanged -= cleaned;
            }
            Assert.That(notifications, Is.EqualTo(1));
            Assert.That(cleanupNotifications, Is.EqualTo(dead.Length), "cleanup runs once per removed aircraft");
            Assert.That(wing.Members, Is.EqualTo(survivors));
            foreach (UnitData aircraft in survivors)
            {
                Assert.That(aircraft.IsHidden, Is.True, "surviving aircraft retain stealth and detection");
                Assert.That(StealthSystem.IsDetectedBy(aircraft, _observer), Is.True);
            }
        }

        [Test]
        public void LastAllowedUnlandedEndDoesNotDamageOrPublishContentChange()
        {
            UnitData aircraft = Aircraft("within endurance", 4);
            aircraft.ConsecutiveUnlandedEnds--;
            var wing = new ArmyData { Owner = _owner, Hex = _hex };
            wing.Members.Add(aircraft);
            using (MatchRestoreScope.Begin()) ArmyRegistry.Register(wing);
            int notifications = 0;
            Action<PlayerSetupData, HexCoord> changed = (viewer, hex) => notifications++;
            VisionSystem.VisibleContentChanged += changed;
            try { AviationTurnLifecycle.ResolveEndOfTurn(_owner, null); }
            finally { VisionSystem.VisibleContentChanged -= changed; }
            Assert.That(aircraft.HitPointsCurrent, Is.EqualTo(4));
            Assert.That(aircraft.HasEmergencyFlightPenalty, Is.False);
            Assert.That(notifications, Is.Zero);
        }

        [Test]
        public void FuelDeathClearsDetectionOnlyOnceAndLeavesOtherOwnersAircraftUntouched()
        {
            UnitData doomed = Aircraft("doomed", 1);
            var wing = new ArmyData { Owner = _owner, Hex = _hex };
            wing.Members.Add(doomed);
            UnitData other = Aircraft("other owner", 4);
            other.Owner = _observer;
            other.IsHidden = true;
            var otherWing = new ArmyData { Owner = _observer, Hex = _hex };
            otherWing.Members.Add(other);
            using (MatchRestoreScope.Begin())
            {
                ArmyRegistry.Register(wing);
                ArmyRegistry.Register(otherWing);
            }
            // Detection state can outlive the hidden flag; removal must clear it too.
            StealthSystem.MarkDetected(doomed, _observer);
            StealthSystem.MarkDetected(other, _owner);
            int contentChanges = 0, stealthChanges = 0;
            Action<PlayerSetupData, HexCoord> changed = (viewer, hex) => contentChanges++;
            Action cleaned = () => stealthChanges++;
            VisionSystem.VisibleContentChanged += changed;
            StealthSystem.StealthChanged += cleaned;
            try
            {
                AviationTurnLifecycle.ResolveEndOfTurn(_owner, null);
                AviationTurnLifecycle.ResolveEndOfTurn(_owner, null);
            }
            finally
            {
                VisionSystem.VisibleContentChanged -= changed;
                StealthSystem.StealthChanged -= cleaned;
            }
            Assert.That(wing.Members, Is.Empty);
            Assert.That(StealthSystem.IsDetectedBy(doomed, _observer), Is.False);
            Assert.That(contentChanges, Is.EqualTo(1), "an empty wing must not publish or die twice");
            Assert.That(stealthChanges, Is.EqualTo(1));
            Assert.That(otherWing.Members, Is.EqualTo(new[] { other }));
            Assert.That(other.HitPointsCurrent, Is.EqualTo(4));
            Assert.That(other.ConsecutiveUnlandedEnds, Is.EqualTo(2));
            Assert.That(other.IsHidden, Is.True);
            Assert.That(StealthSystem.IsDetectedBy(other, _owner), Is.True);
        }

        [TestCase(1)]
        [TestCase(4)]
        public void PartialHumanRefuellingPublishesFinalOverflowDamageWithoutDamagingLandedAircraft(int overflowHp)
        {
            _owner.IsHuman = true;
            UnitData landed = Aircraft("landed", 4);
            landed.HasEmergencyFlightPenalty = true;
            landed.SortieLaunchPaid = true;
            UnitData overflow = Aircraft("overflow", overflowHp);
            var wing = new ArmyData { Owner = _owner, Hex = _hex };
            wing.Members.Add(landed);
            wing.Members.Add(overflow);
            using (MatchRestoreScope.Begin())
            {
                BuildingRegistry.Register(_hex, new BuildingData { Owner = _owner, Hex = _hex, AirfieldCapacity = 1 });
                ArmyRegistry.Register(wing);
            }
            StealthSystem.MarkDetected(overflow, _observer);
            var rosters = new List<UnitData[]>();
            Action<PlayerSetupData, HexCoord> changed = (viewer, hex) =>
            {
                if (viewer == _observer && hex.Equals(_hex)) rosters.Add(wing.Members.ToArray());
            };
            VisionSystem.VisibleContentChanged += changed;
            try { AviationTurnLifecycle.ResolveEndOfTurn(_owner, null); }
            finally { VisionSystem.VisibleContentChanged -= changed; }

            Assert.That(rosters.Count, Is.EqualTo(2), "landing and subsequent fuel damage each publish their result");
            Assert.That(rosters[0], Is.EqualTo(new[] { landed, overflow }));
            Assert.That(rosters[1], Is.EqualTo(overflowHp == 1 ? new[] { landed } : new[] { landed, overflow }));
            Assert.That(landed.HitPointsCurrent, Is.EqualTo(4));
            Assert.That(landed.ConsecutiveUnlandedEnds, Is.Zero);
            Assert.That(landed.HasEmergencyFlightPenalty, Is.False);
            Assert.That(landed.SortieLaunchPaid, Is.False);
            Assert.That(overflow.HitPointsCurrent, Is.EqualTo(overflowHp == 1 ? 0 : 2));
            Assert.That(StealthSystem.IsDetectedBy(overflow, _observer), Is.EqualTo(overflowHp != 1));
        }

        [TestCase(4)]
        [TestCase(1)]
        public void FuelDeathRefreshesHistoricalHumanSightingOrRemovesItWhenNoAircraftSurvive(int firstHp)
        {
            _observer.IsHuman = true;
            var wing = new ArmyData { Owner = _owner, Hex = _hex };
            UnitData first = Aircraft("first", firstHp);
            wing.Members.Add(first);
            wing.Members.Add(Aircraft("last", 1));
            using (MatchRestoreScope.Begin()) ArmyRegistry.Register(wing);
            HumanVisualMemory.ObserveArmy(_observer, wing, _hex);
            Assert.That(HumanVisualMemory.TryGetArmySighting(_observer, wing.Id, out var before), Is.True);
            Assert.That(before.Army.Members.Count, Is.EqualTo(2));

            // These are the same public memory operations used by the map's content subscriber.
            Action<PlayerSetupData, HexCoord> changed = (viewer, hex) =>
            {
                if (viewer != _observer || !hex.Equals(_hex)) return;
                var seen = ObservedEnemyArmies(viewer, hex);
                HumanVisualMemory.ReconcileVisibleHex(viewer, hex, seen.Select(a => a.Id));
                foreach (ArmyData army in seen) HumanVisualMemory.ObserveArmy(viewer, army, hex);
            };
            VisionSystem.VisibleContentChanged += changed;
            try { AviationTurnLifecycle.ResolveEndOfTurn(_owner, null); }
            finally { VisionSystem.VisibleContentChanged -= changed; }

            Assert.That(HumanVisualMemory.TryGetArmySighting(_observer, wing.Id, out var after), Is.EqualTo(firstHp == 4));
            if (firstHp == 4)
            {
                Assert.That(after.Army.Members.Count, Is.EqualTo(1));
                Assert.That(after.Army.Members[0].RuntimeId, Is.EqualTo(first.RuntimeId));
                Assert.That(after.Army.Members[0].HitPointsCurrent, Is.EqualTo(2));
            }
            Assert.That(before.Army.Members.Count, Is.EqualTo(2), "the earlier observation is an independent snapshot");
        }

        [Test]
        public void HumanMemoryQueryIncludesAircraftButExcludesHiddenOwnNeutralAndContainerRosters()
        {
            var visibleAir = new ArmyData { Owner = _owner, Hex = _hex };
            visibleAir.Members.Add(Aircraft("visible", 4));
            var hiddenAir = new ArmyData { Owner = _owner, Hex = _hex };
            hiddenAir.Members.Add(Aircraft("hidden", 4));
            hiddenAir.Members[0].IsHidden = true;
            var own = new ArmyData { Owner = _observer, Hex = _hex };
            own.Members.Add(new UnitData { Owner = _observer, HitPointsCurrent = 4 });
            var neutralOwner = new PlayerSetupData { IsNeutral = true };
            var neutral = new ArmyData { Owner = neutralOwner, Hex = _hex };
            neutral.Members.Add(new UnitData { Owner = neutralOwner, HitPointsCurrent = 4 });
            var airfield = new ArmyData { Owner = _owner, Hex = _hex, IsAirfield = true };
            airfield.Members.Add(Aircraft("stored", 4));
            var deck = new ArmyData { Owner = _owner, Hex = _hex, CarrierUnit = new UnitData { Owner = _owner } };
            deck.Members.Add(Aircraft("on deck", 4));
            var prison = new ArmyData { Owner = _owner, Hex = _hex, IsPrison = true };
            prison.Members.Add(new UnitData { Owner = _owner, IsPrisoner = true, HitPointsCurrent = 4 });
            var empty = new ArmyData { Owner = _owner, Hex = _hex };
            using (MatchRestoreScope.Begin())
                foreach (ArmyData army in new[] { visibleAir, hiddenAir, own, neutral, airfield, deck, prison, empty })
                    ArmyRegistry.Register(army);

            Assert.That(ObservedEnemyArmies(_observer, _hex), Is.EqualTo(new[] { visibleAir }));
            StealthSystem.MarkDetected(hiddenAir.Members[0], _observer);
            Assert.That(ObservedEnemyArmies(_observer, _hex), Is.EquivalentTo(new[] { visibleAir, hiddenAir }));
        }

        [TestCase(4)]
        [TestCase(1)]
        [Category("UnityNative")]
        public void RealMapContentCallbackRefreshesAircraftMemoryWithDisabledPresentation(int firstHp)
        {
            _observer.IsHuman = true;
            var wing = new ArmyData { Owner = _owner, Hex = _hex };
            wing.Members.Add(Aircraft("first", firstHp));
            wing.Members.Add(Aircraft("last", 1));
            using (MatchRestoreScope.Begin()) ArmyRegistry.Register(wing);
            var root = new UnityEngine.GameObject("fuel-memory-disabled-selection");
            root.SetActive(false);
            Action<PlayerSetupData, HexCoord> changed = null;
            try
            {
                var selection = root.AddComponent<HexSelectionController>();
                MethodInfo callback = typeof(HexSelectionController)
                    .GetMethod("OnVisibleContentChanged", BindingFlags.Instance | BindingFlags.NonPublic);
                changed = (viewer, hex) => callback.Invoke(selection, new object[] { viewer, hex });
                changed(_observer, _hex);
                Assert.That(HumanVisualMemory.TryGetArmySighting(_observer, wing.Id, out var before), Is.True);
                Assert.That(before.Army.Members.Count, Is.EqualTo(2));
                VisionSystem.VisibleContentChanged += changed;
                AviationTurnLifecycle.ResolveEndOfTurn(_owner, null);
                Assert.That(HumanVisualMemory.TryGetArmySighting(_observer, wing.Id, out var after), Is.EqualTo(firstHp == 4));
                if (firstHp == 4)
                {
                    Assert.That(after.Army.Members.Count, Is.EqualTo(1));
                    Assert.That(after.Army.Members[0].HitPointsCurrent, Is.EqualTo(2));
                }
            }
            finally
            {
                if (changed != null) VisionSystem.VisibleContentChanged -= changed;
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static List<ArmyData> ObservedEnemyArmies(PlayerSetupData viewer, HexCoord hex) =>
            (List<ArmyData>)typeof(HexSelectionController)
                .GetMethod("ObservedEnemyArmiesAt", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { viewer, hex });

        private UnitData Aircraft(string name, int hp) => new UnitData
        {
            Name = name, Owner = _owner, IsAviation = true,
            HitPointsMax = 4, HitPointsCurrent = hp,
            TurnsWithoutRefuel = 2, ConsecutiveUnlandedEnds = 2
        };
    }
}
#endif
