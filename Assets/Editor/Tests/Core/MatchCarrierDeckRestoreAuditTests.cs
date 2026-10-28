#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.Ai;
using Game.Cards;
using Game.Core;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Saves;
using Game.Setup;
using Game.Units;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    public sealed class MatchCarrierDeckRestoreAuditTests
    {
        [Test] public void DecodeRejectsCarrierContainerLinkToAnUnrelatedEnemyArmy()
        {
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); AiHandRegistry.Clear(); PlayerRootRegistry.Clear();
            Game.Combat.DelayedBattleRegistry.Clear(); HexResourceBonusRegistry.Clear(); HexEventRegistry.Clear();
            Game.Aviation.AntiAirState.Clear(); StealthSystem.Clear();
            var config = ScriptableObject.CreateInstance<GameConfig>();
            var catalog = ScriptableObject.CreateInstance<StartingDeckCatalog>(); config.collectionDeckCatalog = catalog;
            var human = new PlayerSetupData { PlayerId = "human", IsHuman = true, Hand = new PlayerHandData(null, 0) };
            var ai = new PlayerSetupData { PlayerId = "ai", ColorIndex = 1 };
            var unit = new UnitData { Owner = human, AirfieldCapacity = 4 };
            var host = new ArmyData { Owner = human }; host.Members.Add(unit);
            var enemy = new ArmyData { Owner = ai };
            try
            {
                var capture = new MatchWorldCapture(config, new[] { human, ai });
                capture.IncludeArmy(host); capture.IncludeArmy(enemy);
                var data = capture.Finish(); data.units[0].airfieldArmy = enemy.Id;
                var before = MatchIdentityState.Capture();
                Assert.Throws<System.InvalidOperationException>(() => MatchWorldCodec.Decode(data, config, before),
                    "Moving the restored host would otherwise relocate the unrelated enemy army.");
                Assert.That(ArmyData.IdentityHighWaterMark, Is.EqualTo(before.ArmyHighWaterMark));
                Assert.That(UnitData.IdentityHighWaterMark, Is.EqualTo(before.UnitHighWaterMark));
                Assert.That(ArmyRegistry.AllOccupiedHexes(), Is.Empty);
            }
            finally { Object.DestroyImmediate(config); Object.DestroyImmediate(catalog); }
        }

        [Test] public void DecodeRejectsTransportArmyThatDoesNotContainItsPassenger()
        {
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); AiHandRegistry.Clear(); PlayerRootRegistry.Clear();
            Game.Combat.DelayedBattleRegistry.Clear(); HexResourceBonusRegistry.Clear(); HexEventRegistry.Clear();
            Game.Aviation.AntiAirState.Clear(); StealthSystem.Clear();
            var config = ScriptableObject.CreateInstance<GameConfig>();
            var catalog = ScriptableObject.CreateInstance<StartingDeckCatalog>(); config.collectionDeckCatalog = catalog;
            var human = new PlayerSetupData { PlayerId = "human", IsHuman = true, Hand = new PlayerHandData(null, 0) };
            var ai = new PlayerSetupData { PlayerId = "ai", ColorIndex = 1 };
            var unit = new UnitData { Owner = human }; var carrier = new UnitData { Owner = human };
            var host = new ArmyData { Owner = human }; host.Members.Add(carrier);
            unit.SetTransportCarrier(carrier, host);
            try
            {
                var capture = new MatchWorldCapture(config, new[] { human, ai }); capture.IncludeUnit(unit);
                Assert.Throws<System.InvalidOperationException>(() => MatchWorldCodec.Decode(capture.Finish(), config, MatchIdentityState.Capture()));
            }
            finally { Object.DestroyImmediate(config); Object.DestroyImmediate(catalog); }
        }

        [Test] public void RestoredReadyCarrierDeckRetainsControllerRequiredForFlightOrders()
        {
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); AiHandRegistry.Clear(); PlayerRootRegistry.Clear();
            Game.Combat.DelayedBattleRegistry.Clear(); HexResourceBonusRegistry.Clear(); HexEventRegistry.Clear();
            Game.Aviation.AntiAirState.Clear(); StealthSystem.Clear();
            var config = ScriptableObject.CreateInstance<GameConfig>();
            var catalog = ScriptableObject.CreateInstance<StartingDeckCatalog>();
            var faction = ScriptableObject.CreateInstance<FactionCardCatalog>();
            var prefabObject = new GameObject("carrier-deck-test-prefab");
            var mapObject = new GameObject("carrier-deck-test-map");
            MatchWorldGraph graph = null;
            var human = new PlayerSetupData { PlayerId = "human", IsHuman = true, ColorIndex = 0,
                Faction = Faction.IronConcord, Hand = new PlayerHandData(null, 0) };
            var ai = new PlayerSetupData { PlayerId = "ai", ColorIndex = 1, Faction = Faction.Ashen };
            faction.faction = human.Faction;
            faction.armyPrefab = prefabObject.AddComponent<MapObjectVisual>();
            catalog.catalogs.Add(faction); config.collectionDeckCatalog = catalog;
            var carrier = new UnitData { Owner = human, AirfieldCapacity = 4, MoveMax = 8, MoveCurrent = 3 };
            var aircraft = new UnitData { Owner = human, IsAviation = true, MoveMax = 9, MoveCurrent = 7 };
            var host = new ArmyData { Owner = human }; host.Members.Add(carrier);
            var hangar = new ArmyData { Owner = human, CarrierUnit = carrier, IsAirfield = true };
            var deck = new ArmyData { Owner = human, CarrierUnit = carrier, LaunchCarrier = carrier, IsAirArmy = true };
            deck.Members.Add(aircraft); carrier.CarrierAirfield = hangar; carrier.CarrierFlightDeck = deck;
            try
            {
                // The same stable linked containers and ready roster produced by TryLaunch,
                // without invoking normal registration's carrier creation or vision effects.
                using (MatchRestoreScope.Begin())
                { ArmyRegistry.Register(host); ArmyRegistry.Register(hangar); ArmyRegistry.Register(deck); }
                var capture = new MatchWorldCapture(config, new[] { human, ai });
                var bytes = MatchSaveJson.Serialize(capture.Finish());
                graph = MatchWorldCodec.Decode(MatchSaveJson.Deserialize<MatchWorldData>(bytes), config, MatchIdentityState.Capture());
                ArmyRegistry.Clear();
                var map = mapObject.AddComponent<HexMap>();
                map.SetData(0, 1f, new Dictionary<HexCoord, Game.Terrain.TerrainTypeEntry>
                    { [host.Hex] = new Game.Terrain.TerrainTypeEntry() });
                graph.RestoreWorld(map);
                var ready = graph.Army(deck.Id);
                Assert.That(ready.Controller, Is.Not.Null,
                    "SelectArmyForOrders refuses a ready wing without its ArmyController.");
                Assert.That(ready.Members[0], Is.SameAs(graph.Unit(aircraft.RuntimeId)));
                Assert.That(ready.CarrierUnit, Is.SameAs(graph.Unit(carrier.RuntimeId)));
                Assert.That(ready.CarrierUnit.CarrierFlightDeck, Is.SameAs(ready));
                Assert.That(ready.Members[0].MoveCurrent, Is.EqualTo(7));
                Assert.That(ready.Members[0].SortieLaunchPaid, Is.False);
                Assert.That(graph.Army(hangar.Id).Controller, Is.Null);
            }
            finally
            {
                if (graph != null) foreach (var p in graph.Players)
                { var root = PlayerRootRegistry.FindFor(p); if (root != null) Object.DestroyImmediate(root.gameObject); }
                ArmyRegistry.Clear(); PlayerRootRegistry.Clear(); AiHandRegistry.Clear();
                Object.DestroyImmediate(mapObject); Object.DestroyImmediate(prefabObject);
                Object.DestroyImmediate(config); Object.DestroyImmediate(catalog); Object.DestroyImmediate(faction);
            }
        }
    }
}
#endif
