#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class ObservationOwnershipTests
    {
        private PlayerSetupData viewer, enemy;
        private readonly HexCoord hex = new HexCoord(1, 0);
        [SetUp] public void SetUp()
        {
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); VisionSystem.Clear(); VisionSystem.Configure(null); StealthSystem.Clear();
            viewer = new PlayerSetupData { PlayerId = "v", IsHuman = true };
            enemy = new PlayerSetupData { PlayerId = "e" };
        }
        [TearDown] public void TearDown() { ArmyRegistry.Clear(); BuildingRegistry.Clear(); VisionSystem.Clear(); StealthSystem.Clear(); }
        private ArmyData Army(PlayerSetupData owner, HexCoord at)
        {
            var army = new ArmyData { Owner = owner, Hex = at };
            army.Members.Add(new UnitData { Name = "before", Owner = owner, MoveCurrent = 4, MoveMax = 5, HitPointsCurrent = 3 });
            ArmyRegistry.Register(army); return army;
        }
        [Test] public void ObservationWorksWithSelectionDisabled()
        {
            Army(viewer, hex); var live = Army(enemy, hex);
            Assert.That(HumanVisualMemory.TryGetArmySighting(viewer, live.Id, out var seen), Is.True);
            Assert.That(seen.Army.Members[0].HitPointsCurrent, Is.EqualTo(3));
            live.Members[0].HitPointsCurrent = 2; VisionSystem.NotifyContentChanged(hex);
            Assert.That(HumanVisualMemory.TryGetArmySighting(viewer, live.Id, out seen), Is.True);
            Assert.That(seen.Army.Members[0].HitPointsCurrent, Is.EqualTo(2));
        }
        [Test] public void RememberedStateNeverReadsHiddenCurrentWorld()
        {
            var watcher = Army(viewer, hex); var live = Army(enemy, hex);
            ArmyRegistry.MoveArmy(watcher, new HexCoord(8, 0));
            live.Owner = new PlayerSetupData { PlayerId = "captor" }; live.Members[0].Name = "hidden capture";
            ArmyRegistry.Unregister(live);
            Assert.That(HumanVisualMemory.TryGetArmySighting(viewer, live.Id, out var seen), Is.True);
            Assert.That(seen.Army.Owner, Is.SameAs(enemy)); Assert.That(seen.Army.Members[0].Name, Is.EqualTo("before"));
        }
        [Test] public void NeutralObservationSurvivesHiddenCasualtyDeathAndTurnExpiryUntilReobserved()
        {
            var watcher = Army(viewer, hex);
            var neutral = new PlayerSetupData { PlayerId = "n", IsNeutral = true };
            var live = Army(neutral, hex);
            Assert.That(HumanVisualMemory.TryGetArmySighting(viewer, live.Id, out var seen), Is.True);
            ArmyRegistry.MoveArmy(watcher, new HexCoord(8, 0));
            live.Members[0].HitPointsCurrent = 1; VisionSystem.NotifyContentChanged(hex);
            ArmyActions.ClearMembers(live); ArmyActions.DeleteArmyIfEmptied(live, force: true);
            HumanVisualMemory.EndTurn(viewer);
            Assert.That(HumanVisualMemory.TryGetArmySighting(viewer, live.Id, out seen), Is.True);
            Assert.That(seen.Army.Members[0].HitPointsCurrent, Is.EqualTo(3));
            Assert.That(seen.Army.Owner, Is.SameAs(neutral));
            ArmyRegistry.MoveArmy(watcher, hex);
            Assert.That(HumanVisualMemory.TryGetArmySighting(viewer, live.Id, out _), Is.False);
        }
        [Test] public void TransientEventGuardNeverEntersNeutralMapMemory()
        {
            Army(viewer, hex);
            var neutral = new PlayerSetupData { IsNeutral = true };
            HexEventRegistry.Set(hex, new Game.Cards.EventDefinition(), "event guard", null, neutral, null);
            try
            {
                var guard = new ArmyData { Owner = neutral, Hex = hex, Name = "event guard" };
                guard.Members.Add(new UnitData { Owner = neutral, HitPointsCurrent = 3 });
                ArmyRegistry.Register(guard);
                Assert.That(HexEventRegistry.IsEventGuardArmy(hex, guard), Is.True);
                Assert.That(HumanVisualMemory.TryGetArmySighting(viewer, guard.Id, out _), Is.False);
                HumanVisualMemory.EndTurn(viewer);
                Assert.That(HumanVisualMemory.TryGetArmySighting(viewer, guard.Id, out _), Is.False);
            }
            finally { HexEventRegistry.Clear(); }
        }
        [Test] public void EverSeenDoesNotAuthorizeHiddenNeutralLiveLayout()
        {
            var neutral = Army(new PlayerSetupData { PlayerId = "n", IsNeutral = true }, hex);
            VisionSystem.RestoreState(new VisionState { CurrentViewerId = viewer.PlayerId, Players = {
                new PlayerVisionState { PlayerId = viewer.PlayerId, EverSeen = { hex } }
            } }, _ => viewer);
            var layout = typeof(HexSelectionController).GetMethod("VisibleForLayout",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            var result = (List<ArmyData>)layout.Invoke(null, new object[] { new List<ArmyData> { neutral }, hex });
            Assert.That(result, Is.Empty);
        }
        [Test] public void NeutralHistoryRoundTripPreservesViewerIsolationAndDoesNotAllocateLiveIds()
        {
            var other = new PlayerSetupData { PlayerId = "v2", IsHuman = true };
            Army(viewer, hex); Army(other, hex);
            var live = Army(new PlayerSetupData { PlayerId = "n", IsNeutral = true }, hex);
            var snapshots = new Dictionary<string, ArmyData>();
            var state = HumanVisualMemory.CaptureState(display => {
                string key = snapshots.Count.ToString(); snapshots.Add(key, display); return key;
            });
            HumanVisualMemory.RestoreState(state, id => id == viewer.PlayerId ? viewer : other, key => snapshots[key]);
            HumanVisualMemory.EndTurn(viewer);
            HumanVisualMemory.TryGetArmySighting(viewer, live.Id, out var first);
            HumanVisualMemory.TryGetArmySighting(other, live.Id, out var second);
            Assert.That(first, Is.Not.Null); Assert.That(second, Is.Not.Null);
            first.Army.Members.Clear(); Assert.That(second.Army.Members.Count, Is.EqualTo(1));
            Assert.That(new ArmyData().Id, Is.EqualTo(live.Id + 1));
            Assert.That(new UnitData().RuntimeId, Is.EqualTo(live.Members[0].RuntimeId + 1));
        }

        [Test] public void DisplayMutationCannotRewriteStoredObservationOrOtherViewer()
        {
            var second = new PlayerSetupData { PlayerId = "v2", IsHuman = true };
            var live = Army(enemy, hex);
            HumanVisualMemory.ObserveArmy(viewer, live, hex); HumanVisualMemory.ObserveArmy(second, live, hex);
            HumanVisualMemory.TryGetArmySighting(viewer, live.Id, out var seen);
            var display = seen.Army; display.Members[0].Name = "modified"; display.Members.Clear(); display.Owner = viewer;
            Assert.That(seen.Army.Members[0].Name, Is.EqualTo("before")); Assert.That(seen.Army.Owner, Is.SameAs(enemy));
            HumanVisualMemory.TryGetArmySighting(second, live.Id, out var other);
            Assert.That(other.Army.Members.Count, Is.EqualTo(1));
        }
        [Test] public void NewTurnReobservesStationaryEnemyAfterPreviousTurnMemoryExpiry()
        {
            var watcher = Army(viewer, hex); var live = Army(enemy, hex);
            var replenish = typeof(Game.Turns.GameTurnController).GetMethod("ReplenishMoveForOwner",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            HumanVisualMemory.EndTurn(viewer);
            replenish.Invoke(null, new object[] { enemy });
            replenish.Invoke(null, new object[] { viewer });
            Assert.That(HumanVisualMemory.TryGetArmySighting(viewer, live.Id, out var seen), Is.True);
            ArmyRegistry.MoveArmy(watcher, new HexCoord(8, 0));
            Assert.That(HumanVisualMemory.TryGetArmySighting(viewer, live.Id, out seen), Is.True);
            Assert.That(seen.Army.Members[0].Name, Is.EqualTo("before"));
        }
        [Test] public void ObserverMatchInitializationObservesExistingWorldWithoutSelection()
        {
            viewer.IsHuman = false; Army(viewer, hex); var live = Army(enemy, hex);
            Assert.That(HumanVisualMemory.TryGetArmySighting(viewer, live.Id, out _), Is.False);
            HumanVisualMemory.ObserverMatch = true;
            MapObservationSystem.ObserveAllViewers();
            Assert.That(HumanVisualMemory.TryGetArmySighting(viewer, live.Id, out var seen), Is.True);
            Assert.That(seen.Army.Members[0].Name, Is.EqualTo("before"));
        }
        [Test] public void DepartureIntoFogUsesObservedPayloadAndNeverHiddenDestinationDefense()
        {
            var from = new HexCoord(0, 0);
            Army(viewer, from);
            BuildingRegistry.Register(hex, new BuildingData { Owner = enemy, Hex = hex, IsBase = true, Defense = 9 });
            var live = Army(enemy, from);
            ArmyRegistry.MoveArmy(live, hex);
            Assert.That(VisionSystem.IsVisible(viewer, hex), Is.False);
            Assert.That(HumanVisualMemory.TryGetArmySighting(viewer, live.Id, out var seen), Is.True);
            Assert.That(seen.Hex, Is.EqualTo(hex));
            Assert.That(seen.Army.VisualSnapshotConstructionDefense, Is.EqualTo(0), "Hidden destination building was never observed.");
            Assert.That(VisionSystem.HasEverSeen(viewer, hex), Is.True, "The witnessed endpoint must be a valid historical observation for the save codec.");
            live.Members[0].HitPointsCurrent = 1; VisionSystem.NotifyContentChanged(hex);
            Assert.That(seen.Army.Members[0].HitPointsCurrent, Is.EqualTo(3));
        }
        [Test] public void ObservedAircraftRetainsEffectiveMovementOnEveryDisplayRead()
        {
            var live = Army(enemy, hex); live.Members[0].IsAviation = true;
            live.Members[0].MoveCurrent = 5; live.Members[0].HasEmergencyFlightPenalty = true;
            HumanVisualMemory.ObserveArmy(viewer, live, hex);
            HumanVisualMemory.TryGetArmySighting(viewer, live.Id, out var seen);
            Assert.That(seen.Army.CurrentMovement, Is.EqualTo(2));
            Assert.That(Game.Aviation.AviationRules.EffectiveMoveCurrent(seen.Army.Members[0]), Is.EqualTo(2));
            var state = HumanVisualMemory.CaptureState(_ => "plane");
            var display = seen.Army;
            HumanVisualMemory.RestoreState(state, _ => viewer, _ => display);
            HumanVisualMemory.TryGetArmySighting(viewer, live.Id, out var restored);
            Assert.That(restored.Observed.Members[0].EffectiveMoveCurrent, Is.EqualTo(2));
        }
        [Test] public void HiddenBuildingDestructionPreservesLastKnownBuildingAndDefense()
        {
            var watcher = Army(viewer, hex);
            BuildingRegistry.Register(hex, new BuildingData { Owner = enemy, Hex = hex, IsBase = true, Defense = 4 });
            var live = Army(enemy, hex);
            ArmyRegistry.MoveArmy(watcher, new HexCoord(8, 0));
            BuildingRegistry.Unregister(hex);
            Assert.That(HumanVisualMemory.IsBuildingKnown(viewer, hex), Is.True);
            HumanVisualMemory.TryGetArmySighting(viewer, live.Id, out var seen);
            Assert.That(seen.Army.VisualSnapshotConstructionDefense, Is.EqualTo(4));
        }
        [Test] public void RestoreHistoricalIdDoesNotAdvanceLiveIdentitySequence()
        {
            var display = ArmyData.CreateVisualSnapshot(); display.Hex = hex; display.Owner = enemy;
            var state = new HumanVisualMemoryState();
            state.Players.Add(new HumanPlayerMemoryState { PlayerId = "v", ArmySightings = {
                new HumanArmySightingState { SourceArmyId = 5000, Hex = hex, DisplaySnapshotKey = "old" } } });
            HumanVisualMemory.RestoreState(state, _ => viewer, _ => display);
            Assert.That(new ArmyData().Id, Is.Zero);
        }
        [Test] public void ObservedModelsDoNotAllocateLiveIdentities()
        {
            var live = Army(enemy, hex); HumanVisualMemory.ObserveArmy(viewer, live, hex);
            HumanVisualMemory.TryGetArmySighting(viewer, live.Id, out var seen);
            int unitId = live.Members[0].RuntimeId;
            var snapshots = new Dictionary<string, ArmyData>();
            var state = HumanVisualMemory.CaptureState(a => { snapshots["one"] = a; return "one"; });
            seen.Army.MarkActivated();
            HumanVisualMemory.RestoreState(state, id => id == "v" ? viewer : enemy, key => snapshots[key]);
            Assert.That(new ArmyData().Id, Is.EqualTo(live.Id + 1));
            Assert.That(new UnitData().RuntimeId, Is.EqualTo(unitId + 1));
            Assert.That(live.HasActivatedThisTurn, Is.False); Assert.That(live.Members[0].TransportCarrier, Is.Null);
        }
    }
}
#endif
