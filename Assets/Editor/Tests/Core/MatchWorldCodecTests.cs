#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using Game.Ai;
using Game.Cards;
using Game.Combat;
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
    // Exercise the exact production tactical file transport and concrete world graph codecs.
    public sealed class MatchWorldCodecTests
    {
        private GameConfig config;
        private StartingDeckCatalog starting;
        private FactionCardCatalog faction;
        private PlayerSetupData human, ai;
        private CardDefinition definition;
        [SetUp] public void SetUp()
        {
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); AiHandRegistry.Clear(); PlayerRootRegistry.Clear();
            DelayedBattleRegistry.Clear(); HexResourceBonusRegistry.Clear(); HexEventRegistry.Clear();
            Game.Aviation.AntiAirState.Clear(); StealthSystem.Clear();
            config = ScriptableObject.CreateInstance<GameConfig>();
            starting = ScriptableObject.CreateInstance<StartingDeckCatalog>(); faction = ScriptableObject.CreateInstance<FactionCardCatalog>();
            definition = new CardDefinition { authoredKey = "codec-unit", cardType = CardType.Unit, displayName = "Card" };
            faction.cards.Add(definition); starting.catalogs.Add(faction); config.collectionDeckCatalog = starting;
            human = new PlayerSetupData { PlayerId = "h", IsHuman = true, ColorIndex = 0, Faction = Faction.IronConcord, Hand = new PlayerHandData(null, 0) };
            ai = new PlayerSetupData { PlayerId = "a", ColorIndex = 1, Faction = Faction.Ashen };
        }
        [TearDown] public void TearDown()
        {
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); AiHandRegistry.Clear(); PlayerRootRegistry.Clear();
            DelayedBattleRegistry.Clear(); HexResourceBonusRegistry.Clear(); HexEventRegistry.Clear(); Game.Aviation.AntiAirState.Clear(); StealthSystem.Clear();
            UnityEngine.Object.DestroyImmediate(config); UnityEngine.Object.DestroyImmediate(starting); UnityEngine.Object.DestroyImmediate(faction);
        }
        private MatchWorldData Json(MatchWorldData value) => MatchSaveJson.Deserialize<MatchWorldData>(MatchSaveJson.Serialize(value));
        [Test] public void JsonRoundTripRetainsCanonicalHandsCardEvidenceActivationAndDetachedShells()
        {
            var hand = new AiHandData(null, Faction.None, 0); hand.SetCurrentTurn(12); AiHandRegistry.RegisterRestored(ai, hand);
            var card = new CardData(definition) { ResearchProductionCreated = true }; hand.TryAddCard(card, true);
            var evidence = new CardData(definition) { AcquiredTurn = 4 };
            var departed = new UnitData { Name = "departed", Owner = human, OriginatingCard = definition, MoveMax = 3, MoveCurrent = 2 };
            var shell = new ArmyData { Owner = human, Hex = new HexCoord(7, -3) };
            shell.Members.Add(departed); shell.MarkActivated(); shell.Members.Clear();
            var holder = new ArmyData { Owner = human, Hex = new HexCoord(8, -4) }; holder.Members.Add(departed);
            shell.SavedArrangement[departed] = (4, 3);
            DelayedBattleRegistry.Add(new PendingBattle { Hex = shell.Hex, Participants = new List<ArmyData> { shell, holder } });
            var capture = new MatchWorldCapture(config, new[] { human, ai }); capture.IncludeCard(evidence);
            var data = Json(capture.Finish()); var identity = MatchIdentityState.Capture(); var before = MatchIdentityState.Capture();
            var graph = MatchWorldCodec.Decode(data, config, identity);
            Assert.That(graph.Army(shell.Id).Hex, Is.EqualTo(new HexCoord(7, -3)));
            Assert.That(graph.Army(holder.Id).Members[0], Is.SameAs(graph.Unit(departed.RuntimeId)));
            Assert.That(graph.Unit(departed.RuntimeId).Owner, Is.SameAs(graph.Player("h")));
            Assert.That(graph.Army(shell.Id).RequiresActivationCharge(graph.Unit(departed.RuntimeId)), Is.False);
            Assert.That(graph.Army(shell.Id).SavedArrangement[graph.Unit(departed.RuntimeId)], Is.EqualTo((4, 3)));
            Assert.That(graph.Player("a").Hand.Hand[0], Is.SameAs(graph.Card(card.RuntimeId)));
            Assert.That(graph.Card(card.RuntimeId).AcquiredTurn, Is.EqualTo(12));
            Assert.That(graph.Card(evidence.RuntimeId).AcquiredTurn, Is.EqualTo(4));
            Assert.That(graph.Player("h").Hand, Is.Not.SameAs(human.Hand));
            Assert.That(ArmyData.IdentityHighWaterMark, Is.EqualTo(before.ArmyHighWaterMark));
            Assert.That(UnitData.IdentityHighWaterMark, Is.EqualTo(before.UnitHighWaterMark));
            Assert.That(CardData.IdentityHighWaterMark, Is.EqualTo(before.CardHighWaterMark));
        }
        [Test] public void JsonRoundTripRetainsRawMovementCarrierGraphAndAttachmentAccounting()
        {
            var body = new UnitData { Owner = human, Attack = 17, MoveMax = 3, HitPointsMax = 10, HitPointsCurrent = 7, FateMax = 4, Fate = 2 };
            var carrier = new UnitData { Owner = human, MoveMax = 10, AirfieldCapacity = 4 };
            var army = new ArmyData { Owner = human }; army.Members.Add(carrier); army.Members.Add(body);
            var airfield = new ArmyData { Owner = human, IsAirfield = true, CarrierUnit = carrier };
            var deck = new ArmyData { Owner = human, CarrierUnit = carrier };
            carrier.CarrierAirfield = airfield; carrier.CarrierFlightDeck = deck;
            body.SetTransportCarrier(carrier, army); body.RestoreMovementSpent(12);
            body.AttachmentBase = new PredictedEquipmentState(new Dictionary<EquipmentStat, int> { [EquipmentStat.Attack] = 8 }, new[] { "original" });
            body.AttachmentApplied = new PredictedEquipmentState(new Dictionary<EquipmentStat, int> { [EquipmentStat.Attack] = 14 }, new[] { "applied" });
            body.AttachmentResources = new AttachmentResourceState(3, 12, 2, 7, 0, 2);
            var capture = new MatchWorldCapture(config, new[] { human, ai }); capture.IncludeArmy(army);
            var graph = MatchWorldCodec.Decode(Json(capture.Finish()), config, MatchIdentityState.Capture());
            var restored = graph.Unit(body.RuntimeId); var restoredCarrier = graph.Unit(carrier.RuntimeId);
            Assert.That(restored.Attack, Is.EqualTo(17), "Runtime deltas must not be recomputed by equipment application.");
            Assert.That(restored.MovementSpent, Is.EqualTo(12));
            Assert.That(restored.AssignedTransportCarrier, Is.SameAs(restoredCarrier));
            Assert.That(restored.TransportArmy, Is.SameAs(graph.Army(army.Id)));
            Assert.That(restoredCarrier.CarrierAirfield, Is.SameAs(graph.Army(airfield.Id)));
            Assert.That(restoredCarrier.CarrierFlightDeck, Is.SameAs(graph.Army(deck.Id)));
            Assert.That(graph.Army(airfield.Id).CarrierUnit, Is.SameAs(restoredCarrier));
            Assert.That(restored.AttachmentBase.Value.Stats[EquipmentStat.Attack], Is.EqualTo(8));
            Assert.That(restored.AttachmentApplied.Value.Stats[EquipmentStat.Attack], Is.EqualTo(14));
            Assert.That(restored.AttachmentResources.Value.MoveSpent, Is.EqualTo(12));
            Assert.That(restored.AttachmentResources.Value.LastHp, Is.EqualTo(7));
        }
        [Test] public void LateValidationFailureDoesNotIssueIdsOrAlterLiveRegistries()
        {
            var live = new ArmyData { Owner = human }; var unit = new UnitData { Owner = human }; live.Members.Add(unit);
            var capture = new MatchWorldCapture(config, new[] { human, ai }); capture.IncludeArmy(live); capture.IncludeCard(new CardData(definition));
            var data = Json(capture.Finish()); var high = MatchIdentityState.Capture();
            data.units[0].id = high.UnitHighWaterMark + 10; data.armies[0].members[0] = data.units[0].id;
            high.UnitHighWaterMark += 10; data.stealth.Detections.Add(new DetectionState { UnitId = data.units[0].id, ObserverId = "unknown", Expiry = 3 });
            var before = MatchIdentityState.Capture();
            Assert.Throws<InvalidOperationException>(() => MatchWorldCodec.Decode(data, config, high));
            Assert.That(ArmyData.IdentityHighWaterMark, Is.EqualTo(before.ArmyHighWaterMark));
            Assert.That(UnitData.IdentityHighWaterMark, Is.EqualTo(before.UnitHighWaterMark));
            Assert.That(CardData.IdentityHighWaterMark, Is.EqualTo(before.CardHighWaterMark));
            Assert.That(ArmyRegistry.AllOccupiedHexes(), Is.Empty);
            Assert.That(AiHandRegistry.Peek(ai), Is.Null);
        }
        [Test] public void JsonRoundTripKeepsAbsentAiHandAbsentAndRejectsDanglingOrDuplicateIdentities()
        {
            var capture = new MatchWorldCapture(config, new[] { human, ai }); capture.IncludeCard(new CardData(definition));
            var data = Json(capture.Finish()); var high = MatchIdentityState.Capture();
            var graph = MatchWorldCodec.Decode(data, config, high); Assert.That(graph.Player("a").Hand, Is.Null);
            data.cards.Add(data.cards[0]); Assert.Throws<InvalidOperationException>(() => MatchWorldCodec.Decode(data, config, high));
            data.cards.RemoveAt(data.cards.Count - 1); data.players[0].hand.cards.Add(high.CardHighWaterMark + 1);
            Assert.Throws<InvalidOperationException>(() => MatchWorldCodec.Decode(data, config, high));
        }
        [Test] public void HistoricalOnlyNeutralOwnerRetainsCanonicalIdentityWithoutWorldEntities()
        {
            var neutral = new PlayerSetupData { PlayerId = "neutral", IsNeutral = true, ColorIndex = 5, Faction = Faction.Neutral };
            var capture = new MatchWorldCapture(config, new[] { human, ai }); capture.IncludePlayer(neutral);
            var graph = MatchWorldCodec.Decode(Json(capture.Finish()), config, MatchIdentityState.Capture());
            Assert.That(graph.Player("neutral"), Is.Not.Null);
            Assert.That(graph.Player("neutral").IsNeutral, Is.True);
            Assert.That(graph.Players.Count, Is.EqualTo(2));
            Assert.That(graph.Player("neutral"), Is.SameAs(graph.Player("neutral")));
        }
        [Test] public void WorldImportRestoresQueuedReferencesBonusAaAndStealthWithoutRefills()
        {
            var unit = new UnitData { Owner = human, IsHidden = true, HasEnteredStealthThisTurn = true, MoveMax = 5, MoveCurrent = 1 };
            var removed = new ArmyData { Owner = human, Hex = new HexCoord(0, 0) }; removed.Members.Add(unit);
            DelayedBattleRegistry.Add(new PendingBattle { Hex = removed.Hex, Participants = new List<ArmyData> { removed } });
            Game.Aviation.AntiAirState.RecordPrompted(unit, removed.Id, true);
            StealthSystem.RestoreState(new StealthState { Detections = new List<DetectionState> { new DetectionState { UnitId = unit.RuntimeId, ObserverId = ai.PlayerId, Expiry = 173 } } },
                id => id == unit.RuntimeId ? unit : null, id => id == ai.PlayerId ? ai : human);
            HexResourceBonusRegistry.Set(removed.Hex, new Game.Economy.ResourceYields { materials = 3 });
            var capture = new MatchWorldCapture(config, new[] { human, ai });
            var graph = MatchWorldCodec.Decode(Json(capture.Finish()), config, MatchIdentityState.Capture());
            Game.Aviation.AntiAirState.Clear(); StealthSystem.Clear(); DelayedBattleRegistry.Clear(); HexResourceBonusRegistry.Clear();
            var mapObject = new GameObject("codec-map"); var map = mapObject.AddComponent<HexMap>();
            map.SetData(0, 1f, new Dictionary<HexCoord, Game.Terrain.TerrainTypeEntry> { [removed.Hex] = new Game.Terrain.TerrainTypeEntry() });
            try
            {
                graph.RestoreWorld(map); graph.RestoreKnowledge();
                var canonical = graph.Unit(unit.RuntimeId);
                Assert.That(canonical.MoveCurrent, Is.EqualTo(1)); Assert.That(canonical.IsHidden, Is.True); Assert.That(canonical.HasEnteredStealthThisTurn, Is.True);
                Assert.That(Game.Aviation.AntiAirState.CanReact(canonical, removed.Id), Is.False);
                Assert.That(StealthSystem.CaptureState().Detections[0].Expiry, Is.EqualTo(173));
                Assert.That(DelayedBattleRegistry.TakeNext().Participants[0], Is.SameAs(graph.Army(removed.Id)));
                Assert.That(ArmyRegistry.IsRegistered(graph.Army(removed.Id)), Is.False);
                Assert.That(HexResourceBonusRegistry.CaptureState()[0].Bonus.materials, Is.EqualTo(3));
            }
            finally
            {
                foreach (var player in graph.Players)
                { var root = PlayerRootRegistry.FindFor(player); if (root != null) UnityEngine.Object.DestroyImmediate(root.gameObject); }
                UnityEngine.Object.DestroyImmediate(mapObject);
            }
        }
    }
}
#endif
