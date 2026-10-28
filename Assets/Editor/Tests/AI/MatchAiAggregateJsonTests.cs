#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Ai.V2;
using Game.Cards;
using Game.Core;
using Game.Map;
using Game.Players;
using Game.Saves;
using Game.Setup;
using Game.Units;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    public sealed partial class AiPersistentGraphRoundTripTests
    {
        [Test]
        public void ProductionCodecAll22OwnersSurviveProductionJsonAndCanonicalRebinding()
        {
            ProductionCodecRoundtrip(value => MatchSaveJson.Deserialize<MatchAiData>(MatchSaveJson.Serialize(value)), true);
        }

        // The managed runner uses the same production JSON transport; authored asset setup needs native Unity.
        internal void ProductionCodecRoundtrip(Func<MatchAiData, MatchAiData> jsonRoundtrip, bool nativeContentCheck)
        {
            ClearOwners();
            var owner = Player("ai"); var enemy = Player("enemy", 7);
            var army = new ArmyData { Owner = owner, Hex = Home };
            var hero = new UnitData(); var evidence = new CardData(null); var generated = new CardData(null); var build = new CardData(null);
            int historical = ArmyData.IdentityHighWaterMark + 40;
            Seed(owner, enemy, army, hero, evidence, generated, build, historical);
            var expected = Continue(owner, army.Id);
            ClearOwners();
            Seed(owner, enemy, army, hero, evidence, generated, build, historical);
            var retainedArmies = new List<ArmyData>(); var retainedCards = new List<CardData>(); var retainedUnits = new List<UnitData>();
            var data = MatchAiCodec.Capture(p => p.PlayerId, retainedArmies.Add, retainedUnits.Add, retainedCards.Add);
            Assert.That(retainedArmies, Does.Contain(army), "Sortie-owned army outside the map must be retained.");
            Assert.That(retainedCards, Does.Contain(evidence)); Assert.That(retainedCards, Does.Contain(generated));
            Assert.That(retainedUnits, Does.Contain(hero));
            Assert.That(data.HistoricalArmyIds, Does.Contain(historical));
            var before = Fields(data);
            data = jsonRoundtrip(data);
            ClearOwners();
            var newOwner = Player("ai"); var newEnemy = Player("enemy", 7);
            var newArmy = ArmyData.WithIdentity(army.Id); newArmy.Owner = newOwner; newArmy.Hex = Home;
            var newHero = UnitData.WithRuntimeId(hero.RuntimeId);
            var cards = new[] { evidence, generated, build }.ToDictionary(x => x.RuntimeId, x => CardData.WithRuntimeId(null, x.RuntimeId));
            PlayerSetupData PlayerResolver(string id) => id == "ai" ? newOwner : id == "enemy" ? newEnemy : null;
            ArmyData ArmyResolver(int id) => id == newArmy.Id ? newArmy : null;
            UnitData UnitResolver(int id) => id == newHero.RuntimeId ? newHero : null;
            CardData CardResolver(int id) => cards.TryGetValue(id, out var value) ? value : null;
            if (nativeContentCheck)
            {
                var config = ScriptableObject.CreateInstance<GameConfig>();
                var catalog = ScriptableObject.CreateInstance<StartingDeckCatalog>();
                var faction = ScriptableObject.CreateInstance<FactionCardCatalog>();
                faction.cards = new List<CardDefinition>();
                foreach (string key in new[] { "historical-operator", "held-card", "other-card", "upgrade-A", "upgrade-B" })
                    faction.cards.Add(new CardDefinition { authoredKey = key });
                catalog.catalogs.Add(faction); config.collectionDeckCatalog = catalog;
                MatchAiCodec.Validate(data, PlayerResolver, ArmyResolver, UnitResolver, CardResolver, config);
                int highBefore = ArmyData.IdentityHighWaterMark;
                string savedKey = data.DevelopmentHistory.Entries[0].Value.Values[0].CardKey;
                data.DevelopmentHistory.Entries[0].Value.Values[0].CardKey = "unknown-card";
                Assert.Throws<ArgumentException>(() => MatchAiCodec.Validate(data, PlayerResolver, ArmyResolver, UnitResolver, CardResolver, config));
                Assert.IsNull(MissionIntentRegistry.Peek(newOwner));
                Assert.AreEqual(highBefore, ArmyData.IdentityHighWaterMark);
                data.DevelopmentHistory.Entries[0].Value.Values[0].CardKey = savedKey;
                UnityEngine.Object.DestroyImmediate(faction); UnityEngine.Object.DestroyImmediate(catalog); UnityEngine.Object.DestroyImmediate(config);
            }
            using (MatchRestoreScope.Begin()) MatchAiCodec.Restore(data, PlayerResolver, ArmyResolver, UnitResolver, CardResolver, null);
            var recaptured = MatchAiCodec.Capture(p => p.PlayerId, _ => { }, _ => { }, _ => { });
            foreach (var field in before) Assert.AreEqual(field.Value, Fields(recaptured)[field.Key], "Production JSON codec changed: " + field.Key);
            Assert.AreSame(newEnemy, AiMapMemory.KnownSightingOf(newOwner, historical).Value.Owner);
            Assert.AreSame(newHero, MissionIntentRegistry.Peek(newOwner).All.Single(x => x.Development != null).Development.Hero);
            Assert.AreEqual(expected, Continue(newOwner, newArmy.Id));
        }
    }
}
#endif
