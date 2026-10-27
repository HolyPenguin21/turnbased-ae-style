#if UNITY_INCLUDE_TESTS
using System;
using System.IO;
using System.Linq;
using Game.Cards;
using Game.Players;
using Game.Progression;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    public sealed class LibraryRewardReceiptTests
    {
        private StartingDeckCatalog starting;
        private ResearchProductionCatalog research;
        private FactionCardCatalog catalog;
        private CollectionProfileStore store;
        private CollectionService collection;
        private CardDefinition card;
        private string directory;

        [SetUp] public void Setup()
        {
            starting = ScriptableObject.CreateInstance<StartingDeckCatalog>();
            research = ScriptableObject.CreateInstance<ResearchProductionCatalog>();
            catalog = ScriptableObject.CreateInstance<FactionCardCatalog>();
            catalog.faction = Faction.Ashen; starting.catalogs.Add(catalog);
            card = new CardDefinition { authoredKey = "library.original", cardType = CardType.Unit, deckCopyLimit = 3 };
            catalog.cards.Add(card);
            directory = Path.Combine(Path.GetTempPath(), "library-receipts-" + Guid.NewGuid().ToString("N"));
            store = new CollectionProfileStore(directory);
            collection = new CollectionService(new DeckRules(starting, research), store, new CollectionProfile());
        }
        [TearDown] public void Cleanup()
        {
            UnityEngine.Object.DestroyImmediate(starting); UnityEngine.Object.DestroyImmediate(research); UnityEngine.Object.DestroyImmediate(catalog);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        private void Reload() => collection = new CollectionService(collection.Rules, store, store.Load(out _));
        private bool Grant(string match, string instance, out CardDefinition acquired, out string error)
            => collection.TryGrantRandomCollectionCard(match, instance, out acquired, out error);
        [Test] public void ReplayingPreEventSaveReturnsExactCardWithoutSecondPermanentCopy()
        {
            Assert.That(Grant("match", "3:1", out var first, out var error), Is.True, error);
            Reload();
            Assert.That(Grant("match", "3:1", out var replay, out error), Is.True, error);
            Assert.That(replay, Is.SameAs(first));
            Assert.That(store.Load(out _).Owned(card.authoredKey), Is.EqualTo(1));
            Assert.That(collection.Snapshot.pendingRewards, Is.Empty);
            Assert.That(collection.Snapshot.claimedMatchIds, Is.Empty);
        }
        [Test] public void MatchAndEventIdentitiesBothScopeTheReceipt()
        {
            Assert.That(Grant("first", "3:1", out _, out var error), Is.True, error);
            Assert.That(Grant("first", "4:1", out _, out error), Is.True, error);
            Assert.That(Grant("second", "3:1", out _, out error), Is.True, error);
            Reload();
            Assert.That(Grant("first", "3:1", out var replay, out error), Is.True, error);
            Assert.That(replay, Is.SameAs(card));
            Assert.That(collection.Owned(card.authoredKey), Is.EqualTo(3));
        }
        [Test] public void ExhaustedResultSurvivesReloadAndNewEligibleContent()
        {
            catalog.cards.Clear();
            Assert.That(Grant("match", "3:1", out var empty, out var error), Is.True, error);
            Assert.That(empty, Is.Null);
            Assert.That(File.Exists(Path.Combine(directory, "collection-v1.json")), Is.True);
            catalog.cards.Add(card); Reload();
            Assert.That(Grant("match", "3:1", out var replay, out error), Is.True, error);
            Assert.That(replay, Is.Null);
            Assert.That(collection.Owned(card.authoredKey), Is.Zero);
            Assert.That(Grant("match", "4:1", out var next, out error), Is.True, error);
            Assert.That(next, Is.SameAs(card));
        }
        [Test] public void ReplayKeepsExactResultAfterLimitAndEligibilityChange()
        {
            Assert.That(Grant("match", "3:1", out _, out var error), Is.True, error);
            card.deckCopyLimit = 0; card.deckBuilderExcluded = true; Reload();
            Assert.That(Grant("match", "3:1", out var replay, out error), Is.True, error);
            Assert.That(replay, Is.SameAs(card));
            Assert.That(collection.Owned(card.authoredKey), Is.EqualTo(1));
        }
        [Test] public void UnavailableSavedCardFailsWithoutRerollAndCanRetryWhenRestored()
        {
            Assert.That(Grant("match", "3:1", out _, out var error), Is.True, error);
            catalog.cards.Clear();
            var replacement = new CardDefinition { authoredKey = "library.replacement", cardType = CardType.Unit };
            catalog.cards.Add(replacement); Reload();
            Assert.That(Grant("match", "3:1", out var unavailable, out error), Is.False);
            Assert.That(unavailable, Is.Null); Assert.That(error, Is.Not.Null.And.Not.Empty);
            Assert.That(collection.Owned(replacement.authoredKey), Is.Zero);
            catalog.cards.Add(card);
            Assert.That(Grant("match", "3:1", out var restored, out error), Is.True, error);
            Assert.That(restored, Is.SameAs(card));
            Assert.That(collection.Owned(card.authoredKey), Is.EqualTo(1));
        }
        [TestCase(null, "3:1"), TestCase("", "3:1"), TestCase(" ", "3:1")]
        [TestCase("match", null), TestCase("match", ""), TestCase("match", " ")]
        public void EmptyRewardIdentityCannotGrantOrCreateProfile(string match, string instance)
        {
            Assert.That(Grant(match, instance, out var acquired, out var error), Is.False);
            Assert.That(acquired, Is.Null); Assert.That(error, Is.Not.Null.And.Not.Empty);
            Assert.That(collection.Owned(card.authoredKey), Is.Zero);
            Assert.That(Directory.Exists(directory), Is.False);
        }
        [Test] public void FailedDiskWriteDoesNotPublishReceiptAndRetryGrantsOnce()
        {
            Directory.CreateDirectory(directory);
            string obstruction = Path.Combine(directory, "blocked"); File.WriteAllText(obstruction, "file");
            store = new CollectionProfileStore(obstruction);
            collection = new CollectionService(collection.Rules, store, new CollectionProfile());
            Assert.That(Grant("match", "3:1", out var failed, out var error), Is.False);
            Assert.That(failed, Is.Null); Assert.That(error, Is.Not.Null.And.Not.Empty);
            Assert.That(collection.Owned(card.authoredKey), Is.Zero);
            File.Delete(obstruction);
            Assert.That(Grant("match", "3:1", out var acquired, out error), Is.True, error);
            Reload(); Assert.That(Grant("match", "3:1", out var replay, out error), Is.True, error);
            Assert.That(replay, Is.SameAs(acquired));
            Assert.That(collection.Owned(card.authoredKey), Is.EqualTo(1));
        }
        [Test] public void BackupRecoveryKeepsReceiptAndPermanentCopyTogether()
        {
            Assert.That(Grant("match", "3:1", out _, out var error), Is.True, error);
            Assert.That(collection.Transact(p => { }, out error), Is.True, error);
            File.WriteAllText(Path.Combine(directory, "collection-v1.json"), "broken");
            var loaded = store.Load(out var notice);
            Assert.That(notice, Is.Not.Null.And.Not.Empty);
            collection = new CollectionService(collection.Rules, store, loaded);
            Assert.That(Grant("match", "3:1", out var replay, out error), Is.True, error);
            Assert.That(replay, Is.SameAs(card));
            Assert.That(collection.Owned(card.authoredKey), Is.EqualTo(1));
        }
        [Test] public void LegacyProfileMigrationRetainsOwnershipAndStartsAnEmptyLedger()
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "collection-v1.json"),
                "{\"schemaVersion\":1,\"profileId\":\"legacy\",\"ownedCards\":[{\"cardKey\":\"library.original\",\"count\":2}],\"savedDecks\":[],\"selectedDeckByFaction\":[],\"pendingRewards\":[],\"claimedMatchIds\":[]}");
            Reload();
            Assert.That(collection.Snapshot.schemaVersion, Is.EqualTo(2));
            Assert.That(collection.Owned(card.authoredKey), Is.EqualTo(2));
            Assert.That(collection.Snapshot.eventRewards, Is.Empty);
            Assert.That(Grant("new", "3:1", out _, out var error), Is.True, error);
            Reload(); Assert.That(Grant("new", "3:1", out _, out error), Is.True, error);
            Assert.That(collection.Owned(card.authoredKey), Is.EqualTo(3));
        }
        [Test] public void CurrentSchemaMissingLedgerRecoversReceiptFromBackup()
        {
            Assert.That(Grant("match", "3:1", out _, out var error), Is.True, error);
            Assert.That(collection.Transact(p => { }, out error), Is.True, error);
            File.WriteAllText(Path.Combine(directory, "collection-v1.json"),
                "{\"schemaVersion\":2,\"profileId\":\"broken\",\"ownedCards\":[],\"savedDecks\":[],\"selectedDeckByFaction\":[],\"pendingRewards\":[],\"claimedMatchIds\":[]}");
            var loaded = store.Load(out var notice);
            Assert.That(notice, Is.Not.Null.And.Not.Empty);
            collection = new CollectionService(collection.Rules, store, loaded);
            Assert.That(Grant("match", "3:1", out var replay, out error), Is.True, error);
            Assert.That(replay, Is.SameAs(card)); Assert.That(collection.Owned(card.authoredKey), Is.EqualTo(1));
        }
        [Test] public void DuplicateReceiptsAndMalformedIdentitiesAreRejected()
        {
            var profile = new CollectionProfile();
            profile.eventRewards.Add(new EventRewardReceipt { matchId = "match", eventId = "3:1", cardKey = card.authoredKey });
            profile.eventRewards.Add(new EventRewardReceipt { matchId = "match", eventId = "3:1", cardKey = "" });
            Assert.Throws<InvalidDataException>(() => CollectionProfileStore.ValidateShape(profile));
            profile.eventRewards.RemoveAt(1); profile.eventRewards[0].eventId = " ";
            Assert.Throws<InvalidDataException>(() => CollectionProfileStore.ValidateShape(profile));
            profile.eventRewards[0].eventId = "3:1"; profile.eventRewards[0].cardKey = " ";
            Assert.Throws<InvalidDataException>(() => CollectionProfileStore.ValidateShape(profile));
        }
        [Test] public void ReceiptLimitRejectsNewGrantWithoutDiscardingOldReplay()
        {
            var profile = new CollectionProfile();
            profile.ownedCards.Add(new DeckCardEntry { cardKey = card.authoredKey, count = 1 });
            profile.eventRewards.Add(new EventRewardReceipt { matchId = "match", eventId = "first", cardKey = card.authoredKey });
            for (int i = 1; i < CollectionProfileStore.MaximumEventRewards; i++)
                profile.eventRewards.Add(new EventRewardReceipt { matchId = "match", eventId = "event" + i, cardKey = "" });
            collection = new CollectionService(collection.Rules, store, profile);
            Assert.That(Grant("match", "new", out var refused, out var error), Is.False);
            Assert.That(refused, Is.Null); Assert.That(error, Is.Not.Null.And.Not.Empty);
            Assert.That(Grant("match", "first", out var replay, out error), Is.True, error);
            Assert.That(replay, Is.SameAs(card));
            Assert.That(collection.Owned(card.authoredKey), Is.EqualTo(1));
            Assert.That(collection.Snapshot.eventRewards.Count, Is.EqualTo(CollectionProfileStore.MaximumEventRewards));
        }
        [Test] public void OversizedProfileWriteLeavesKnownGoodProfileIntact()
        {
            Assert.That(Grant("match", "3:1", out _, out var error), Is.True, error);
            var oversized = collection.Snapshot;
            oversized.profileId = new string('x', CollectionProfileStore.MaximumProfileBytes);
            Assert.Throws<InvalidDataException>(() => store.Save(oversized));
            Reload(); Assert.That(Grant("match", "3:1", out var replay, out error), Is.True, error);
            Assert.That(replay, Is.SameAs(card)); Assert.That(collection.Owned(card.authoredKey), Is.EqualTo(1));
        }
        [Test] public void EventReceiptDoesNotConsumeOrRepeatTheMatchReward()
        {
            var rewards = new RewardService(collection);
            var player = new PlayerSetupData { IsHuman = true, Faction = Faction.Ashen };
            Assert.That(rewards.Record(new ParticipantResult("match", player, MatchOutcome.Victory), out var error), Is.True, error);
            Assert.That(Grant("match", "3:1", out _, out error), Is.True, error);
            Assert.That(rewards.Claim("match", new[] { card.authoredKey }, out error), Is.True, error);
            Reload(); rewards = new RewardService(collection);
            Assert.That(Grant("match", "3:1", out _, out error), Is.True, error);
            Assert.That(rewards.Record(new ParticipantResult("match", player, MatchOutcome.Victory), out error), Is.True, error);
            Assert.That(rewards.Claim("match", new[] { card.authoredKey }, out error), Is.True, error);
            Assert.That(collection.Owned(card.authoredKey), Is.EqualTo(2));
            Assert.That(collection.Snapshot.pendingRewards.Single().claimed, Is.True);
            Assert.That(collection.Snapshot.eventRewards.Single().cardKey, Is.EqualTo(card.authoredKey));
        }
        [Test] public void FutureSchemaNeverFallsBackToAnOlderReceiptLedger()
        {
            Assert.That(Grant("match", "3:1", out _, out var error), Is.True, error);
            Assert.That(collection.Transact(p => { }, out error), Is.True, error);
            var future = collection.Snapshot; future.schemaVersion = 3;
            string path = Path.Combine(directory, "collection-v1.json");
            string json = JsonUtility.ToJson(future); File.WriteAllText(path, json);
            Assert.Throws<NotSupportedException>(() => store.Load(out _));
            Assert.That(File.ReadAllText(path), Is.EqualTo(json));
        }
        [Test] public void OverlongRewardIdentitiesAreRejectedBeforeAnyGrant()
        {
            string longIdentity = new string('x', CollectionProfileStore.MaximumRewardIdentityLength + 1);
            Assert.That(Grant(longIdentity, "event", out var cardResult, out var error), Is.False);
            Assert.That(cardResult, Is.Null); Assert.That(error, Is.Not.Null.And.Not.Empty);
            Assert.That(Grant("match", longIdentity, out cardResult, out error), Is.False);
            Assert.That(cardResult, Is.Null); Assert.That(error, Is.Not.Null.And.Not.Empty);
            Assert.That(collection.Owned(card.authoredKey), Is.Zero);
        }
    }
}
#endif
