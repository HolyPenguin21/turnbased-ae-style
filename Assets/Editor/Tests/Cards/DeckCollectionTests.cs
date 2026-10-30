#if UNITY_INCLUDE_TESTS
using Game.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game.Cards;
using Game.Players;
using Game.Progression;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    public sealed class DeckCollectionTests
    {
        private StartingDeckCatalog starting;
        private ResearchProductionCatalog research;
        private FactionCardCatalog catalog;
        private DeckRules rules;
        private CardDefinition card;
        private string directory;
        [SetUp] public void Setup()
        {
            starting = ScriptableObject.CreateInstance<StartingDeckCatalog>();
            research = ScriptableObject.CreateInstance<ResearchProductionCatalog>();
            catalog = ScriptableObject.CreateInstance<FactionCardCatalog>(); catalog.faction = Faction.IronConcord;
            card = new CardDefinition { authoredKey = "test.unit", displayName = "Unit", faction = Faction.IronConcord, cardType = CardType.Unit, deckPointCost = 25, deckCopyLimit = 4 };
            catalog.cards.Add(card); starting.catalogs.Add(catalog);
            rules = new DeckRules(starting, research); directory = Path.Combine(Path.GetTempPath(), "collection-tests-" + Guid.NewGuid().ToString("N"));
        }
        [TearDown] public void Teardown()
        { UnityEngine.Object.DestroyImmediate(starting); UnityEngine.Object.DestroyImmediate(research); UnityEngine.Object.DestroyImmediate(catalog); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        private SavedDeck Deck(int n) => new SavedDeck { deckId = "deck", name = "Test", faction = Faction.IronConcord, mainCards = new List<DeckCardEntry> { new DeckCardEntry { cardKey = card.authoredKey, count = n } } };
        [Test] public void ProductionBlueprintResolvesExcludedAircraftWithoutChangingDeckRules()
        {
            card.cardType = CardType.Equipment;
            var aircraft = new CardDefinition { authoredKey = "test.excluded.aircraft", cardType = CardType.Unit,
                isAviation = true, deckBuilderExcluded = true, activationApCost = 1 };
            catalog.cards.Add(aircraft); research.cardCatalogs.Add(catalog);
            research.productionCards.Add(new ResearchProductionEntry { cardKey = card.authoredKey, resultCardKey = aircraft.authoredKey });
            Assert.That(rules.Permitted(card, Faction.IronConcord), Is.True);
            Assert.That(rules.Permitted(aircraft, Faction.IronConcord), Is.False);
            Assert.That(research.ResolveResult(card), Is.SameAs(aircraft));
            var attempt = new ProductionAttempt(card, null, research.ResolveResult(card));
            var result = attempt.Complete(true);
            Assert.That(result.Definition, Is.SameAs(aircraft));
            Assert.That(result.ResearchProductionCreated, Is.True);
            Assert.That(attempt.Complete(true), Is.Null);
            research.productionCards[0].resultCardKey = "missing";
            Assert.That(research.ResolveResult(card), Is.Null);
            research.productionCards[0].resultCardKey = "";
            Assert.That(research.ResolveResult(card), Is.SameAs(card));
        }

        [Test] public void Exactly100PointsIsValid() { Assert.That(rules.Validate(Deck(4), _ => 4).IsValid, Is.True); }
        [Test] public void OwnershipAndCopyLimitsAreIndependent() { Assert.That(rules.Validate(Deck(3), _ => 2).IsValid, Is.False); card.deckCopyLimit = 2; Assert.That(rules.Validate(Deck(3), _ => 4).IsValid, Is.False); }
        [Test] public void BudgetCannotBeExceeded() { card.deckPointCost = 26; Assert.That(rules.Validate(Deck(4), _ => 4).IsValid, Is.False); }
        [Test] public void NoSeparateCardCountLimit() { card.deckCopyLimit = 150; card.deckPointCost = 0; Assert.That(rules.Validate(Deck(150), _ => 150).IsValid, Is.True); }
        [Test] public void DuplicateRowsAreRejectedWithoutLosingData() { var deck = Deck(1); deck.mainCards.Add(new DeckCardEntry { cardKey = card.authoredKey, count = 1 }); Assert.That(rules.Validate(deck, _ => 4).IsValid, Is.False); Assert.That(deck.mainCards.Count, Is.EqualTo(2)); }
        [Test] public void SystemCardsNeverBecomeRewardsOrDeckCards() { card.deckBuilderExcluded = true; Assert.That(rules.Cards(Faction.IronConcord), Is.Empty); Assert.That(rules.Validate(Deck(1), _ => 4).IsValid, Is.False); }
        [Test] public void NeutralCatalogIsSharedUnlessExcluded()
        {
            catalog.faction = Faction.Neutral; card.faction = Faction.Neutral;
            foreach (var faction in DeckRules.PlayableFactions) Assert.That(rules.Cards(faction), Is.EqualTo(new[] { card }));
            card.deckBuilderExcluded = true;
            Assert.That(rules.Cards(Faction.IronConcord), Is.Empty);
        }
        [Test] public void UnknownIdentityIsKeptInDeckButIgnoredInPlay()
        {
            var deck = Deck(1); deck.mainCards.Add(new DeckCardEntry { cardKey = "removed", count = 1 });
            var validation = rules.Validate(deck, _ => 4);
            Assert.That(validation.IsValid, Is.True); Assert.That(validation.Warnings.Any(w => w.Contains("unavailable")), Is.True);
            Assert.That(new MatchLoadout(deck, rules, _ => 4).TryBuildPool(rules, out var pool, out _), Is.True);
            Assert.That(pool.Single(), Is.SameAs(card)); Assert.That(deck.mainCards.Count, Is.EqualTo(2));
        }
        [Test] public void MissingStarterCardIsSkippedWithReadableIssue()
        {
            starting.decks.Add(new StartingDeck { deckName = "Concord Starter", faction = Faction.IronConcord, cards = new List<DeckCardEntry>
                { new DeckCardEntry { cardKey = card.authoredKey, count = 1 }, new DeckCardEntry { cardKey = "removed", count = 1 } } });
            var p = new CollectionProfile(); var service = new CollectionService(rules, null, p);
            Assert.That(() => service.InitializeStarters(p), Throws.Nothing);
            Assert.That(p.savedDecks.Single(d => d.faction == Faction.IronConcord).mainCards.Single().cardKey, Is.EqualTo(card.authoredKey));
            Assert.That(service.StarterIssues.Single(), Does.Contain("Concord Starter").And.Contain("'removed'").And.Contain("not in any card catalog"));
        }
        [Test] public void RemovalRepairsOverBudgetDeckWithoutChangingOwnership() { var deck = Deck(4); card.deckPointCost = 40; Assert.That(rules.TryChange(deck, card.authoredKey, -1, _ => 4, out _), Is.True); Assert.That(deck.mainCards[0].count, Is.EqualTo(3)); }
        [Test] public void SnapshotIsDetachedFromSavedDeckAndOwnership() { var deck = Deck(2); var snapshot = new MatchLoadout(deck, rules, _ => 4); deck.mainCards[0].count = 4; Assert.That(snapshot.TryBuildPool(rules, out var pool, out _), Is.True); Assert.That(pool.Count, Is.EqualTo(2)); }
        [Test] public void PoolInstancesAreIndependent() { starting.decks.Add(new StartingDeck { faction = Faction.IronConcord, cards = Deck(2).mainCards }); var first = starting.BuildDeckPool(Faction.IronConcord); var second = starting.BuildDeckPool(Faction.IronConcord); first.Clear(); Assert.That(second.Count, Is.EqualTo(2)); }
        private BlueprintQuota Quota()
        {
            card.cardType = CardType.Equipment; card.attachmentSlot = AttachmentSlot.Mutator;
            research.cardCatalogs.Add(catalog); research.researchCards.Add(new ResearchProductionEntry { cardKey = card.authoredKey });
            var deck = Deck(0); deck.mainCards.Clear(); deck.mutators.Add(new DeckCardEntry { cardKey = card.authoredKey, count = 1 });
            return new BlueprintQuota(new MatchLoadout(deck, rules, _ => 1));
        }
        [Test] public void MutatorDoesNotRequireEquipment() { Assert.That(Quota().Unlocked(card.authoredKey), Is.True); }
        [Test] public void EquipmentAndMutatorsAllowOnlyOneCopyInDeck()
        { var quota = Quota(); var deck = Deck(0); deck.mainCards.Clear(); deck.mutators.Add(new DeckCardEntry { cardKey = card.authoredKey, count = 2 }); Assert.That(rules.Validate(deck, _ => 2).IsValid, Is.False); deck.mutators[0].count = 1; Assert.That(rules.Validate(deck, _ => 2).IsValid, Is.True); Assert.That(DeckRules.CopyLimit(card), Is.EqualTo(1)); }
        [Test] public void FailureReleasesQuotaAndRepeatedCallbackIsInert()
        { var quota = Quota(); Assert.That(quota.TryReserve(card.authoredKey), Is.True); var attempt = new ProductionAttempt(card, quota); Assert.That(quota.Available(card.authoredKey), Is.False); attempt.Complete(false); Assert.That(attempt.Complete(true), Is.Null); Assert.That(quota.Available(card.authoredKey), Is.True); }
        [Test] public void SuccessNeverConsumesBlueprintAndCancelReleases()
        { var quota = Quota(); quota.TryReserve(card.authoredKey); var attempt = new ProductionAttempt(card, quota); Assert.That(attempt.Complete(true), Is.Not.Null); Assert.That(attempt.Complete(true), Is.Null); Assert.That(quota.Available(card.authoredKey), Is.True); quota.TryReserve(card.authoredKey); new ProductionAttempt(card, quota).Dispose(); Assert.That(quota.Available(card.authoredKey), Is.True); }
        [Test] public void ProfileSurvivesReloadAndBackupRecovery()
        { var store = new CollectionProfileStore(directory); var p = new CollectionProfile(); store.Save(p); p.ownedCards.Add(new DeckCardEntry { cardKey = card.authoredKey, count = 1 }); store.Save(p); Assert.That(store.Load(out _).Owned(card.authoredKey), Is.EqualTo(1)); File.WriteAllText(Path.Combine(directory, "collection-v1.json"), "broken"); Assert.That(store.Load(out var notice).Owned(card.authoredKey), Is.EqualTo(0)); Assert.That(notice, Is.Not.Null); Assert.That(File.Exists(Path.Combine(directory, "collection-v1.json.bak")), Is.True); }
        [Test] public void DefeatAndRepeatOutcomeGrantOnlyOnce()
        { var p = new CollectionProfile(); var service = new CollectionService(rules, new CollectionProfileStore(directory), p); var rewards = new RewardService(service); var result = new ParticipantResult("match", new PlayerSetupData { IsHuman = true, Faction = Faction.IronConcord }, MatchOutcome.Defeat); Assert.That(rewards.Record(result, out _), Is.True); rewards.Record(result, out _); Assert.That(service.Owned(card.authoredKey), Is.EqualTo(1)); }
        [Test] public void VictoryPendingCanBeRestoredAndClaimedOnce()
        { var store = new CollectionProfileStore(directory); var service = new CollectionService(rules, store, new CollectionProfile()); var result = new ParticipantResult("match", new PlayerSetupData { IsHuman = true, Faction = Faction.IronConcord }, MatchOutcome.Victory); new RewardService(service).Record(result, out _); var restored = new CollectionService(rules, store, store.Load(out _)); var rewards = new RewardService(restored); Assert.That(rewards.Claim("match", new[] { card.authoredKey }, out _), Is.True); rewards.Claim("match", new[] { card.authoredKey }, out _); Assert.That(restored.Owned(card.authoredKey), Is.EqualTo(1)); }
        [Test] public void FullCollectionProducesEmptyRewardAndNeverReducesOwnedCount()
        { var p = new CollectionProfile(); p.ownedCards.Add(new DeckCardEntry { cardKey = card.authoredKey, count = 9 }); var service = new CollectionService(rules, new CollectionProfileStore(directory), p); new RewardService(service).Record(new ParticipantResult("full", new PlayerSetupData { IsHuman = true, Faction = Faction.IronConcord }, MatchOutcome.Defeat), out _); Assert.That(service.Owned(card.authoredKey), Is.EqualTo(9)); Assert.That(service.Snapshot.pendingRewards.Single().acquiredKeys, Is.Empty); }
        [Test] public void DrawAndAiDoNotGenerateRewards()
        { var service = new CollectionService(rules, new CollectionProfileStore(directory), new CollectionProfile()); var reward = new RewardService(service); reward.Record(new ParticipantResult("draw", new PlayerSetupData { IsHuman = true, Faction = Faction.IronConcord }, MatchOutcome.Draw), out _); reward.Record(new ParticipantResult("ai", new PlayerSetupData { Faction = Faction.IronConcord }, MatchOutcome.Victory), out _); Assert.That(service.Snapshot.pendingRewards, Is.Empty); }
        [Test] public void InvalidSelectionDoesNotMutateProfile()
        { var service = new CollectionService(rules, new CollectionProfileStore(directory), new CollectionProfile()); var reward = new RewardService(service); reward.Record(new ParticipantResult("win", new PlayerSetupData { IsHuman = true, Faction = Faction.IronConcord }, MatchOutcome.Victory), out _); Assert.That(reward.Claim("win", new[] { "wrong" }, out _), Is.False); Assert.That(service.Owned(card.authoredKey), Is.Zero); }
        [Test] public void SavingTwoDecksDoesNotSpendOwnedCards()
        {
            var p = new CollectionProfile(); p.ownedCards.Add(new DeckCardEntry { cardKey = card.authoredKey, count = 1 });
            var service = new CollectionService(rules, new CollectionProfileStore(directory), p);
            var first = Deck(1); var second = CollectionProfile.CopyDeck(first); second.deckId = "other";
            Assert.That(service.SaveDeck(first, out _), Is.True); Assert.That(service.SaveDeck(second, out _), Is.True);
            first.mainCards.Clear(); Assert.That(service.Snapshot.savedDecks.All(d => d.mainCards.Single().count == 1), Is.True);
            Assert.That(service.Owned(card.authoredKey), Is.EqualTo(1));
        }
        [Test] public void WriteFailureDoesNotPublishTheCandidateProfile()
        {
            var p = new CollectionProfile(); Directory.CreateDirectory(directory);
            string obstruction = Path.Combine(directory, "file"); File.WriteAllText(obstruction, "x");
            var service = new CollectionService(rules, new CollectionProfileStore(obstruction), p);
            Assert.That(service.Transact(next => next.ownedCards.Add(new DeckCardEntry { cardKey = card.authoredKey, count = 1 }), out var error), Is.False);
            Assert.That(error, Is.Not.Empty); Assert.That(service.Owned(card.authoredKey), Is.Zero);
        }
        [Test] public void ZeroCopyLimitAndNegativeCountAreRejected()
        { card.deckCopyLimit = 0; Assert.That(rules.Validate(Deck(1), _ => 4).IsValid, Is.False); Assert.That(rules.Validate(Deck(-1), _ => 4).IsValid, Is.False); }
        [Test] public void EquipmentNeverAppearsInTheMainDrawPool()
        {
            card.cardType = CardType.Equipment; research.cardCatalogs.Add(catalog);
            research.productionCards.Add(new ResearchProductionEntry { cardKey = card.authoredKey });
            var deck = Deck(0); deck.mainCards.Clear(); deck.equipment.Add(new DeckCardEntry { cardKey = card.authoredKey, count = 1 });
            var snapshot = new MatchLoadout(deck, rules, _ => 1);
            Assert.That(snapshot.TryBuildPool(rules, out var pool, out _), Is.True); Assert.That(pool, Is.Empty);
        }
        [Test, Category("Extended")] public void SixStrategyFixturesPerFactionValidateNearBudget()
        {
            var rules = new DeckRules(AssetDatabase.LoadAssetAtPath<StartingDeckCatalog>("Assets/Cards/StartingDeckCatalog.asset"),
                AssetDatabase.LoadAssetAtPath<ResearchProductionCatalog>("Assets/Cards/ResearchProductionCatalog.asset"));
            Assert.That(DeckCalibrationReport.CreateStrategyDecks(rules).Count, Is.EqualTo(18));
        }
        [TestCase(Faction.IronConcord, "concord.card.light-tank", "concord.card.scrapper", "concord.card.concord-base", 80)]
        [TestCase(Faction.Ashen, "ashen.card.rust-tank", "ashen.card.tech-scrapper", "ashen.card.ashen-base", 86)]
        [TestCase(Faction.Vessels, "vessels.21.resonance-tank", "vessels.12.salvage-drone", "vessels.01.relay-base", 84)]
        public void BalancedStarterOwnsAllBuildingsButFieldsBaseAndThreeFacilities(Faction faction, string tank, string collector, string baseCard, int points)
        {
            var rules = new DeckRules(AssetDatabase.LoadAssetAtPath<StartingDeckCatalog>("Assets/Cards/StartingDeckCatalog.asset"),
                AssetDatabase.LoadAssetAtPath<ResearchProductionCatalog>("Assets/Cards/ResearchProductionCatalog.asset"));
            var p = new CollectionProfile(); new CollectionService(rules, null, p).InitializeStarters(p);
            var deck = p.savedDecks.Single(d => d.faction == faction);
            Assert.That(deck.mainCards.Sum(e => e.count), Is.EqualTo(30));
            Assert.That(deck.mainCards.Where(e => rules.Resolve(e.cardKey).cardType == CardType.Unit).Sum(e => e.count), Is.EqualTo(18));
            Assert.That(deck.mainCards.Where(e => rules.Resolve(e.cardKey).cardType == CardType.Hero).Sum(e => e.count), Is.EqualTo(8));
            CollectionAssert.AreEquivalent(rules.Starting.GetCatalog(faction).cards.Where(c => c.cardType == CardType.Hero).Select(c => c.authoredKey),
                deck.mainCards.Where(e => rules.Resolve(e.cardKey).cardType == CardType.Hero).Select(e => e.cardKey));
            Assert.That(deck.mainCards.Single(e => e.cardKey == tank).count, Is.EqualTo(2));
            Assert.That(deck.mainCards.Any(e => e.cardKey == collector), Is.False);
            CollectionAssert.AreEquivalent(new[] { baseCard, "neutral.facility.lab", "neutral.facility.factory", "neutral.facility.intel" },
                deck.mainCards.Where(e => rules.Resolve(e.cardKey).cardType == CardType.Base || rules.Resolve(e.cardKey).cardType == CardType.Facility).Select(e => e.cardKey));
            var buildings = rules.Cards(faction).Where(c => c.cardType == CardType.Base || c.cardType == CardType.Facility).ToList();
            Assert.That(buildings.Count, Is.EqualTo(6));
            Assert.That(buildings.All(c => p.Owned(c.authoredKey) == 1), Is.True);
            Assert.That(deck.equipment.Sum(e => e.count), Is.EqualTo(faction == Faction.IronConcord ? 2 : 1));
            Assert.That(deck.mutators.Sum(e => e.count), Is.EqualTo(faction == Faction.IronConcord ? 0 : 1));
            Assert.That(deck.equipment.Concat(deck.mutators).All(e => rules.Resolve(e.cardKey).deckPointCost == 1), Is.True);
            Assert.That(rules.Validate(deck, p.Owned).IsValid, Is.True);
            Assert.That(rules.Validate(deck, p.Owned).Points, Is.EqualTo(points));
        }
        private CollectionService AuthoredCollection(CollectionProfile profile)
        {
            var authoredRules = new DeckRules(AssetDatabase.LoadAssetAtPath<StartingDeckCatalog>("Assets/Cards/StartingDeckCatalog.asset"),
                AssetDatabase.LoadAssetAtPath<ResearchProductionCatalog>("Assets/Cards/ResearchProductionCatalog.asset"));
            return new CollectionService(authoredRules, new CollectionProfileStore(directory), profile);
        }
        private static void AssertStarterBlueprints(SavedDeck deck, DeckRules rules)
        {
            var expected = new CollectionService(rules, null, new CollectionProfile()).Starter(deck.faction);
            CollectionAssert.AreEquivalent(expected.equipment.Select(e => e.cardKey + ":" + e.count), deck.equipment.Select(e => e.cardKey + ":" + e.count));
            CollectionAssert.AreEquivalent(expected.mutators.Select(e => e.cardKey + ":" + e.count), deck.mutators.Select(e => e.cardKey + ":" + e.count));
        }
        [Test] public void LegacyStartersGainSavedBlueprintsWithoutChangingIdentityOrCustomDecks()
        {
            var legacy = new CollectionProfile(); AuthoredCollection(legacy).InitializeStarters(legacy);
            var ids = legacy.savedDecks.Select(d => d.deckId).ToArray();
            foreach (var deck in legacy.savedDecks) { deck.isStarter = false; deck.equipment.Clear(); deck.mutators.Clear(); }
            var custom = CollectionProfile.CopyDeck(legacy.savedDecks[0]); custom.deckId = "custom"; custom.name = "My deck";
            custom.mainCards[0].count++; legacy.savedDecks.Add(custom);
            string customJson = JsonUtility.ToJson(custom);
            var service = AuthoredCollection(legacy);
            Assert.That(service.EnsureStarterDecks(out _), Is.True);
            var upgraded = service.Snapshot;
            Assert.That(upgraded.savedDecks.Where(d => d.isStarter).Select(d => d.deckId), Is.EqualTo(ids));
            foreach (var deck in upgraded.savedDecks.Where(d => d.isStarter))
            {
                AssertStarterBlueprints(deck, service.Rules);
                Assert.That(service.Rules.Validate(deck, service.Owned).IsValid, Is.True);
                Assert.That(upgraded.selectedDeckByFaction.Single(s => s.faction == deck.faction).deckId, Is.EqualTo(deck.deckId));
            }
            Assert.That(JsonUtility.ToJson(upgraded.savedDecks.Single(d => d.deckId == "custom")), Is.EqualTo(customJson));
            int changes = 0; service.Changed += () => changes++;
            Assert.That(service.EnsureStarterDecks(out _), Is.True); Assert.That(changes, Is.Zero);
            var loaded = new CollectionProfileStore(directory).Load(out _);
            Assert.That(loaded.savedDecks.Count(d => d.isStarter), Is.EqualTo(3));
            foreach (var deck in loaded.savedDecks.Where(d => d.isStarter)) AssertStarterBlueprints(deck, service.Rules);
        }
        [Test] public void SelectedLegacyStarterDuplicateIsUpgradedInPlace()
        {
            var legacy = new CollectionProfile(); AuthoredCollection(legacy).InitializeStarters(legacy);
            foreach (var deck in legacy.savedDecks) { deck.isStarter = false; deck.equipment.Clear(); deck.mutators.Clear(); }
            var duplicate = CollectionProfile.CopyDeck(legacy.savedDecks[0]); duplicate.deckId = "selected-duplicate";
            legacy.savedDecks.Add(duplicate);
            legacy.selectedDeckByFaction.Single(s => s.faction == duplicate.faction).deckId = duplicate.deckId;
            var service = AuthoredCollection(legacy);
            Assert.That(service.EnsureStarterDecks(out _), Is.True);
            Assert.That(service.StarterDeck(duplicate.faction).deckId, Is.EqualTo(duplicate.deckId));
            AssertStarterBlueprints(service.DefaultDeck(duplicate.faction), service.Rules);
        }
        [Test] public void MissingStarterIsRestoredWithoutPromotingCustomizedDeck()
        {
            var legacy = new CollectionProfile(); AuthoredCollection(legacy).InitializeStarters(legacy);
            var modified = legacy.savedDecks[0]; modified.isStarter = false; modified.mainCards[0].count++;
            var service = AuthoredCollection(legacy);
            Assert.That(service.EnsureStarterDecks(out _), Is.True);
            var starter = service.StarterDeck(modified.faction);
            Assert.That(starter.deckId, Is.Not.EqualTo(modified.deckId));
            Assert.That(service.Snapshot.savedDecks.Single(d => d.deckId == modified.deckId).isStarter, Is.False);
            AssertStarterBlueprints(starter, service.Rules);
        }
        [Test] public void BalanceRevisionReplacesSavedStartersOnceAndPreservesCustomDecksAndOwnership()
        {
            var profile = new CollectionProfile(); AuthoredCollection(profile).InitializeStarters(profile);
            profile.starterDeckRevision = 0;
            var expected = profile.savedDecks.ToDictionary(d => d.deckId, CollectionProfile.CopyDeck);
            foreach (var deck in profile.savedDecks)
            {
                deck.mainCards.Clear(); // All required cards are already owned: ownership must not skip the refresh.
                deck.name = "Renamed " + deck.faction;
            }
            var custom = CollectionProfile.CopyDeck(expected.Values.First()); custom.deckId = "custom"; custom.isStarter = false;
            profile.savedDecks.Add(custom);
            profile.selectedDeckByFaction.Single(s => s.faction == custom.faction).deckId = custom.deckId;
            profile.ownedCards.Add(new DeckCardEntry { cardKey = "removed.reward", count = 4 });
            profile.ownedCards.Single(e => e.cardKey == "neutral.facility.aa_defence").count = 3;
            profile.ownedCards.RemoveAll(e => e.cardKey == "neutral.facility.tactical");
            // The balance field is absent from profiles written before this update.
            profile = JsonUtility.FromJson<CollectionProfile>(JsonUtility.ToJson(profile).Replace("\"starterDeckRevision\":0,", ""));
            Assert.That(profile.starterDeckRevision, Is.Zero);
            var service = AuthoredCollection(profile);
            string customJson = JsonUtility.ToJson(custom);
            Assert.That(service.EnsureStarterDecks(out var error), Is.True, error);
            var upgraded = service.Snapshot;
            Assert.That(upgraded.starterDeckRevision, Is.GreaterThan(0));
            foreach (var deck in upgraded.savedDecks.Where(d => d.isStarter))
            {
                Assert.That(deck.name, Is.EqualTo("Renamed " + deck.faction));
                CollectionAssert.AreEquivalent(expected[deck.deckId].mainCards.Select(e => e.cardKey + ":" + e.count), deck.mainCards.Select(e => e.cardKey + ":" + e.count));
                AssertStarterBlueprints(deck, service.Rules);
                Assert.That(service.Rules.Validate(deck, service.Owned).IsValid, Is.True);
            }
            Assert.That(JsonUtility.ToJson(upgraded.savedDecks.Single(d => d.deckId == custom.deckId)), Is.EqualTo(customJson));
            CollectionAssert.AreEquivalent(profile.selectedDeckByFaction.Select(s => s.faction + ":" + s.deckId), upgraded.selectedDeckByFaction.Select(s => s.faction + ":" + s.deckId));
            Assert.That(service.Owned("removed.reward"), Is.EqualTo(4));
            Assert.That(service.Owned("neutral.facility.aa_defence"), Is.EqualTo(3));
            Assert.That(service.Owned("neutral.facility.tactical"), Is.EqualTo(1));
            var loaded = new CollectionProfileStore(directory).Load(out _);
            Assert.That(loaded.starterDeckRevision, Is.EqualTo(upgraded.starterDeckRevision));
            // A later user edit survives both another ensure call and a profile reload.
            var edited = service.StarterDeck(Faction.IronConcord); edited.mainCards.RemoveAt(0);
            Assert.That(service.SaveDeck(edited, out error), Is.True, error);
            service = AuthoredCollection(new CollectionProfileStore(directory).Load(out _));
            int changes = 0; service.Changed += () => changes++;
            Assert.That(service.EnsureStarterDecks(out error), Is.True, error);
            Assert.That(changes, Is.Zero);
            CollectionAssert.AreEquivalent(edited.mainCards.Select(e => e.cardKey + ":" + e.count), service.StarterDeck(edited.faction).mainCards.Select(e => e.cardKey + ":" + e.count));
        }
        [Test] public void FailedBalanceSaveDoesNotPublishRevisionOrComposition()
        {
            var profile = new CollectionProfile(); AuthoredCollection(profile).InitializeStarters(profile);
            profile.starterDeckRevision = 0; profile.savedDecks[0].mainCards.Clear();
            string before = JsonUtility.ToJson(profile);
            Directory.CreateDirectory(directory);
            string blocked = Path.Combine(directory, "blocked"); File.WriteAllText(blocked, "not a directory");
            var service = new CollectionService(AuthoredCollection(profile).Rules, new CollectionProfileStore(blocked), profile);
            int changes = 0; service.Changed += () => changes++;
            Assert.That(service.EnsureStarterDecks(out var error), Is.False);
            Assert.That(error, Is.Not.Empty);
            Assert.That(changes, Is.Zero);
            Assert.That(JsonUtility.ToJson(service.Snapshot), Is.EqualTo(before));
        }
        [Test] public void StarterCannotBeDeletedAfterRenameOrClearingDraftFlagButCopyCan()
        {
            var profile = new CollectionProfile(); AuthoredCollection(profile).InitializeStarters(profile);
            var service = AuthoredCollection(profile); var starter = service.StarterDeck(Faction.IronConcord);
            starter.name = "Renamed"; starter.isStarter = false;
            Assert.That(service.SaveDeck(starter, out _), Is.True);
            Assert.That(service.DeleteDeck(starter.deckId, out var error), Is.False);
            Assert.That(error, Is.EqualTo("Starter decks cannot be deleted."));
            Assert.That(service.StarterDeck(starter.faction).deckId, Is.EqualTo(starter.deckId));
            var copy = CollectionProfile.CopyDeck(starter); copy.deckId = "copy"; copy.isStarter = false;
            Assert.That(service.SaveDeck(copy, out _), Is.True);
            Assert.That(service.DeleteDeck(copy.deckId, out _), Is.True);
            Assert.That(service.SaveDeck(service.Starter(starter.faction), out _), Is.False);
        }
        [Test] public void VictoryWithOnlyThreeEligibleCardsOffersThreeAndGrantsTwo()
        {
            catalog.cards.Add(new CardDefinition { authoredKey = "other.1", cardType = CardType.Unit, deckCopyLimit = 4 });
            catalog.cards.Add(new CardDefinition { authoredKey = "other.2", cardType = CardType.Unit, deckCopyLimit = 4 });
            var service = new CollectionService(rules, new CollectionProfileStore(directory), new CollectionProfile());
            var rewards = new RewardService(service);
            rewards.Record(new ParticipantResult("three", new PlayerSetupData { IsHuman = true, Faction = Faction.IronConcord }, MatchOutcome.Victory), out _);
            var pending = service.Snapshot.pendingRewards.Single(); Assert.That(pending.offeredKeys.Count, Is.EqualTo(3));
            Assert.That(rewards.Claim("three", pending.offeredKeys.Take(2), out _), Is.True);
            Assert.That(service.Snapshot.ownedCards.Sum(e => e.count), Is.EqualTo(2));
        }
        [Test] public void PendingRewardSurvivesRemovedOffersWithoutRandomSubstitution()
        {
            var other = new CardDefinition { authoredKey = "other", cardType = CardType.Unit, deckCopyLimit = 4 };
            catalog.cards.Add(other);
            var service = new CollectionService(rules, new CollectionProfileStore(directory), new CollectionProfile());
            var rewards = new RewardService(service);
            rewards.Record(new ParticipantResult("updated", new PlayerSetupData { IsHuman = true, Faction = Faction.IronConcord }, MatchOutcome.Victory), out _);
            catalog.cards.Remove(card);
            var pending = service.Snapshot.pendingRewards.Single();
            Assert.That(rewards.AvailableOffers(pending), Is.EqualTo(new[] { other.authoredKey }));
            Assert.That(pending.offeredKeys, Does.Contain(card.authoredKey), "Original offer identity must remain in the receipt.");
            Assert.That(rewards.Claim("updated", new[] { other.authoredKey }, out _), Is.True);
            Assert.That(service.Owned(other.authoredKey), Is.EqualTo(1));
            Assert.That(service.Owned(card.authoredKey), Is.Zero);
        }
        [Test] public void PendingRewardCanFinishWhenUpdatedLimitsLeaveNoEligibleOffers()
        {
            var service = new CollectionService(rules, new CollectionProfileStore(directory), new CollectionProfile());
            var rewards = new RewardService(service);
            rewards.Record(new ParticipantResult("empty-update", new PlayerSetupData { IsHuman = true, Faction = Faction.IronConcord }, MatchOutcome.Victory), out _);
            card.deckCopyLimit = 0;
            Assert.That(rewards.AvailableOffers(service.Snapshot.pendingRewards.Single()), Is.Empty);
            Assert.That(rewards.Claim("empty-update", Array.Empty<string>(), out _), Is.True);
            Assert.That(rewards.Dismiss("empty-update", out _), Is.True);
            Assert.That(service.Owned(card.authoredKey), Is.Zero);
            Assert.That(service.Snapshot.claimedMatchIds, Does.Contain("empty-update"));
        }
        [Test] public void DuplicateRewardIdentitiesAreRejectedAsCorruptShape()
        {
            var p = new CollectionProfile();
            p.pendingRewards.Add(new PendingReward { matchId = "duplicate", outcome = MatchOutcome.Victory,
                offeredKeys = new List<string> { card.authoredKey, card.authoredKey } });
            Assert.Throws<InvalidDataException>(() => CollectionProfileStore.ValidateShape(p));
        }
        [Test] public void FutureSchemaDoesNotFallBackOrOverwriteTheCurrentFile()
        {
            var store = new CollectionProfileStore(directory); var p = new CollectionProfile();
            store.Save(p); store.Save(p); p.schemaVersion = 3;
            string path = Path.Combine(directory, "collection-v1.json");
            string future = JsonUtility.ToJson(p); File.WriteAllText(path, future);
            Assert.Throws<NotSupportedException>(() => store.Load(out _));
            Assert.That(File.ReadAllText(path), Is.EqualTo(future));
            Assert.That(File.Exists(path + ".bak"), Is.True);
        }
        [TestCase(ResearchProductionMode.Research, AttachmentSlot.Mutator)]
        [TestCase(ResearchProductionMode.Production, AttachmentSlot.Equipment)]
        public void CatalogFactionRestrictionsDriveCollectionAndDeckValidation(ResearchProductionMode mode, AttachmentSlot slot)
        {
            card.cardType = CardType.Equipment; card.attachmentSlot = slot;
            research.cardCatalogs.Add(catalog);
            var entry = new ResearchProductionEntry { cardKey = card.authoredKey, factionRestriction = Faction.Ashen };
            var offers = mode == ResearchProductionMode.Research ? research.researchCards : research.productionCards;
            offers.Add(entry);
            var deck = Deck(0); deck.mainCards.Clear();
            DeckRules.Entries(deck, DeckRules.Category(card)).Add(new DeckCardEntry { cardKey = card.authoredKey, count = 1 });
            Assert.That(rules.Cards(Faction.IronConcord), Has.None.EqualTo(card));
            Assert.That(rules.Cards(Faction.Ashen), Does.Contain(card));
            Assert.That(rules.Validate(deck, _ => 1).IsValid, Is.False);
            entry.factionRestriction = Faction.None;
            Assert.That(rules.Cards(Faction.IronConcord), Does.Contain(card));
            Assert.That(rules.Validate(deck, _ => 1).IsValid, Is.True);
            offers.Clear();
            Assert.That(rules.Cards(Faction.IronConcord), Has.None.EqualTo(card));
        }
        [Test]
        public void SelectedLoadoutDrawsNoOtherOwnedCards()
        {
            var other = new CardDefinition { authoredKey = "not-selected", cardType = CardType.Unit, deckCopyLimit = 4 };
            catalog.cards.Add(other);
            var loadout = new MatchLoadout(Deck(2), rules, _ => 4);
            Assert.That(loadout.TryBuildPool(rules, out var pool, out _), Is.True);
            Assert.That(pool, Is.EqualTo(new[] { card, card }));
            Assert.That(pool, Has.None.EqualTo(other));
        }
        [Test]
        public void DeckTotalCostIncludesEveryCopyAndBlueprintCategory()
        {
            card.apCost = 2; card.resourceCost = new ResourceCost(1, 2, 3, 4);
            var equipment = new CardDefinition { authoredKey = "cost.equipment", cardType = CardType.Equipment, apCost = 3, resourceCost = new ResourceCost(2, 3, 4, 5) };
            var mutator = new CardDefinition { authoredKey = "cost.mutator", cardType = CardType.Equipment, attachmentSlot = AttachmentSlot.Mutator, apCost = 4, resourceCost = null };
            catalog.cards.Add(equipment); catalog.cards.Add(mutator);
            var deck = Deck(2);
            deck.equipment.Add(new DeckCardEntry { cardKey = equipment.authoredKey, count = 3 });
            deck.mutators.Add(new DeckCardEntry { cardKey = mutator.authoredKey, count = 1 });
            var go = new GameObject("cost view");
            try
            {
                var view = go.AddComponent<Game.UI.CollectionScreensUI>();
                const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                typeof(Game.UI.CollectionScreensUI).GetField("collection", flags).SetValue(view, new CollectionService(rules, null, new CollectionProfile()));
                typeof(Game.UI.CollectionScreensUI).GetField("draft", flags).SetValue(view, deck);
                long[] total = (long[])typeof(Game.UI.CollectionScreensUI).GetMethod("TotalCost", flags).Invoke(view, null);
                Assert.That(total, Is.EqualTo(new long[] { 17, 8, 13, 18, 23 }));
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }
        [Test] public void AuthoredCatalogsHaveStableUniqueKeysAndCompatibleStarters() { CollectionContentValidation.Validate(); }
    }
}
#endif



