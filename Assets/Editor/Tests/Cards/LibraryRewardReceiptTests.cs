#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
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
        [Test] public void StarterMigrationPreservesSameNameUserDeckWithDifferentBlueprints()
        {
            var authored = new StartingDeck { deckName = "Ashen starter", faction = Faction.Ashen };
            authored.cards.Add(new DeckCardEntry { cardKey = card.authoredKey, count = 1 });
            starting.decks.Add(authored);
            var custom = new SavedDeck { deckId = "custom", name = authored.deckName, faction = authored.faction };
            custom.mainCards.Add(new DeckCardEntry { cardKey = card.authoredKey, count = 1 });
            custom.equipment.Add(new DeckCardEntry { cardKey = "unavailable.custom-blueprint", count = 1 });
            var profile = new CollectionProfile();
            profile.savedDecks.Add(custom);
            profile.selectedDeckByFaction.Add(new DeckSelection { faction = authored.faction, deckId = custom.deckId });
            store.Save(profile);
            Reload();

            Assert.That(collection.EnsureStarterDecks(out var error), Is.True, error);
            Assert.That(collection.StarterDeck(authored.faction).deckId, Is.Not.EqualTo(custom.deckId));
            Assert.That(collection.DefaultDeck(authored.faction).deckId, Is.EqualTo(custom.deckId));
            Assert.That(collection.GetDeck(custom.deckId).equipment.Single().cardKey, Is.EqualTo("unavailable.custom-blueprint"));
        }

        [Test] public void StarterInitializationAndUpgradeSurviveReloadAndKeepAllReceipts()
        {
            var starter = new StartingDeck { deckName = "Ashen starter", faction = Faction.Ashen };
            starter.cards.Add(new DeckCardEntry { cardKey = card.authoredKey, count = 1 });
            starting.decks.Add(starter);
            var before = new CollectionProfile();
            before.ownedCards.Add(new DeckCardEntry { cardKey = card.authoredKey, count = 2 });
            before.claimedMatchIds.Add("claimed-match");
            before.pendingRewards.Add(new PendingReward { matchId = "pending-match", faction = Faction.Ashen });
            before.eventRewards.Add(new EventRewardReceipt { matchId = "event-match", eventId = "1:2", cardKey = card.authoredKey });
            store.Save(before);
            Reload();

            Assert.That(collection.InitializeStarters(out var error), Is.True, error);
            var saved = collection.StarterDeck(Faction.Ashen);
            string id = saved.deckId;
            saved.name = "Player starter name";
            Assert.That(collection.SaveDeck(saved, out error), Is.True, error);
            var legacy = collection.Snapshot;
            legacy.starterDeckRevision = 0;
            legacy.savedDecks.Find(deck => deck.deckId == id).mainCards.Clear();
            var custom = CollectionProfile.CopyDeck(saved);
            custom.deckId = "custom";
            custom.isStarter = false;
            legacy.savedDecks.Add(custom);
            legacy.selectedDeckByFaction.Find(selection => selection.faction == Faction.Ashen).deckId = custom.deckId;
            store.Save(legacy);
            Reload();

            Assert.That(collection.EnsureStarterDecks(out error), Is.True, error);
            Reload();
            int writes = 0;
            collection.Changed += () => writes++;
            Assert.That(collection.InitializeStarters(out error), Is.True, error);
            Assert.That(collection.EnsureStarterDecks(out error), Is.True, error);
            Assert.That(writes, Is.Zero);
            Assert.That(collection.StarterDeck(Faction.Ashen).deckId, Is.EqualTo(id));
            Assert.That(collection.StarterDeck(Faction.Ashen).name, Is.EqualTo("Player starter name"));
            Assert.That(collection.StarterDeck(Faction.Ashen).mainCards.Single().cardKey, Is.EqualTo(card.authoredKey));
            Assert.That(collection.DefaultDeck(Faction.Ashen).deckId, Is.EqualTo("custom"));
            Assert.That(collection.Owned(card.authoredKey), Is.EqualTo(2));
            var after = collection.Snapshot;
            CollectionAssert.AreEqual(new[] { "claimed-match" }, after.claimedMatchIds);
            Assert.That(after.pendingRewards.Single().matchId, Is.EqualTo("pending-match"));
            Assert.That(after.eventRewards.Single().eventId, Is.EqualTo("1:2"));
        }

        [Test] public void ProfileCopyDetachesEveryMutableRecordAndKeepsAuthoredCardIdentity()
        {
            var original = new CollectionProfile { profileId = "copy-source", starterDeckRevision = 4 };
            var row = new DeckCardEntry { cardKey = card.authoredKey, count = 3 };
            original.ownedCards.Add(row);
            original.savedDecks.Add(new SavedDeck
            {
                deckId = "deck", name = "original",
                mainCards = new List<DeckCardEntry> { row },
                equipment = new List<DeckCardEntry> { row },
                mutators = new List<DeckCardEntry> { row },
            });
            original.selectedDeckByFaction.Add(new DeckSelection { deckId = "deck" });
            original.pendingRewards.Add(new PendingReward
            {
                matchId = "match", claimed = true,
                offeredKeys = new List<string> { card.authoredKey },
                acquiredKeys = new List<string> { card.authoredKey },
            });
            original.claimedMatchIds.Add("match");
            original.eventRewards.Add(new EventRewardReceipt { matchId = "match", eventId = "1:2", cardKey = card.authoredKey });
            var copy = original.Copy();
            Assert.That(collection.Rules.Resolve(copy.savedDecks[0].mainCards[0].cardKey), Is.SameAs(card));
            copy.ownedCards[0].count = 0;
            copy.savedDecks[0].name = "changed";
            copy.savedDecks[0].mainCards[0].count = 0;
            copy.savedDecks[0].equipment[0].count = 0;
            copy.savedDecks[0].mutators[0].count = 0;
            copy.savedDecks[0].mainCards.Clear();
            copy.savedDecks[0].equipment.Clear();
            copy.savedDecks[0].mutators.Clear();
            copy.selectedDeckByFaction[0].deckId = "other";
            copy.pendingRewards[0].claimed = false;
            copy.pendingRewards[0].offeredKeys.Clear();
            copy.pendingRewards[0].acquiredKeys.Clear();
            copy.eventRewards[0].cardKey = "other";
            copy.ownedCards.Clear();
            copy.savedDecks.Clear();
            copy.selectedDeckByFaction.Clear();
            copy.pendingRewards.Clear();
            copy.claimedMatchIds.Clear();
            copy.eventRewards.Clear();

            Assert.That(original.ownedCards.Single().count, Is.EqualTo(3));
            var saved = original.savedDecks.Single();
            Assert.That(saved.name, Is.EqualTo("original"));
            Assert.That(saved.mainCards.Single().count, Is.EqualTo(3));
            Assert.That(saved.equipment.Single().count, Is.EqualTo(3));
            Assert.That(saved.mutators.Single().count, Is.EqualTo(3));
            Assert.That(original.selectedDeckByFaction.Single().deckId, Is.EqualTo("deck"));
            Assert.That(original.pendingRewards.Single().claimed, Is.True);
            Assert.That(original.pendingRewards.Single().offeredKeys.Single(), Is.EqualTo(card.authoredKey));
            Assert.That(original.pendingRewards.Single().acquiredKeys.Single(), Is.EqualTo(card.authoredKey));
            Assert.That(original.claimedMatchIds.Single(), Is.EqualTo("match"));
            Assert.That(original.eventRewards.Single().cardKey, Is.EqualTo(card.authoredKey));
        }

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
            store.Save(collection.Snapshot);
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
            store.Save(collection.Snapshot);
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
            store.Save(collection.Snapshot);
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
