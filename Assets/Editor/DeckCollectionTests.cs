#if UNITY_INCLUDE_TESTS
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
        [Test] public void Exactly100PointsIsValid() { Assert.That(rules.Validate(Deck(4), _ => 4).IsValid, Is.True); }
        [Test] public void OwnershipAndCopyLimitsAreIndependent() { Assert.That(rules.Validate(Deck(3), _ => 2).IsValid, Is.False); card.deckCopyLimit = 2; Assert.That(rules.Validate(Deck(3), _ => 4).IsValid, Is.False); }
        [Test] public void BudgetCannotBeExceeded() { card.deckPointCost = 26; Assert.That(rules.Validate(Deck(4), _ => 4).IsValid, Is.False); }
        [Test] public void NoSeparateCardCountLimit() { card.deckCopyLimit = 150; card.deckPointCost = 0; Assert.That(rules.Validate(Deck(150), _ => 150).IsValid, Is.True); }
        [Test] public void DuplicateRowsAreRejectedWithoutLosingData() { var deck = Deck(1); deck.mainCards.Add(new DeckCardEntry { cardKey = card.authoredKey, count = 1 }); Assert.That(rules.Validate(deck, _ => 4).IsValid, Is.False); Assert.That(deck.mainCards.Count, Is.EqualTo(2)); }
        [Test] public void SystemCardsNeverBecomeRewardsOrDeckCards() { card.deckBuilderExcluded = true; Assert.That(rules.Cards(Faction.IronConcord), Is.Empty); Assert.That(rules.Validate(Deck(1), _ => 4).IsValid, Is.False); }
        [Test] public void NeutralIsNotAnImplicitPermission() { catalog.faction = Faction.Neutral; card.faction = Faction.Neutral; Assert.That(rules.Cards(Faction.IronConcord), Is.Empty); }
        [Test] public void UnknownIdentityInvalidatesDeck() { var deck = Deck(1); deck.mainCards[0].cardKey = "removed"; Assert.That(rules.Validate(deck, _ => 4).IsValid, Is.False); }
        [Test] public void RemovalRepairsOverBudgetDeckWithoutChangingOwnership() { var deck = Deck(4); card.deckPointCost = 40; Assert.That(rules.TryChange(deck, card.authoredKey, -1, _ => 4, out _), Is.True); Assert.That(deck.mainCards[0].count, Is.EqualTo(3)); }
        [Test] public void SnapshotIsDetachedFromSavedDeckAndOwnership() { var deck = Deck(2); var snapshot = new MatchLoadout(deck, rules, _ => 4); deck.mainCards[0].count = 4; Assert.That(snapshot.TryBuildPool(rules, out var pool, out _), Is.True); Assert.That(pool.Count, Is.EqualTo(2)); }
        [Test] public void PoolInstancesAreIndependent() { starting.decks.Add(new StartingDeck { faction = Faction.IronConcord, cards = Deck(2).mainCards }); var first = starting.BuildDeckPool(Faction.IronConcord); var second = starting.BuildDeckPool(Faction.IronConcord); first.Clear(); Assert.That(second.Count, Is.EqualTo(2)); }
        private BlueprintQuota Quota()
        {
            card.cardType = CardType.Equipment; card.attachmentSlot = AttachmentSlot.Mutator;
            research.cardCatalogs.Add(catalog); research.researchCards.Add(new ResearchProductionEntry { cardKey = card.authoredKey });
            var deck = Deck(0); deck.mainCards.Clear(); deck.mutators.Add(new DeckCardEntry { cardKey = card.authoredKey, count = 2 });
            return new BlueprintQuota(new MatchLoadout(deck, rules, _ => 2));
        }
        [Test] public void MutatorDoesNotRequireEquipment() { Assert.That(Quota().Remaining(card.authoredKey), Is.EqualTo(2)); }
        [Test] public void FailureReleasesQuotaAndRepeatedCallbackIsInert()
        { var quota = Quota(); Assert.That(quota.TryReserve(card.authoredKey), Is.True); var attempt = new ProductionAttempt(card, quota); Assert.That(quota.Available(card.authoredKey), Is.False); attempt.Complete(false); Assert.That(attempt.Complete(true), Is.Null); Assert.That(quota.Remaining(card.authoredKey), Is.EqualTo(2)); }
        [Test] public void SuccessConsumesExactlyOneAndCancelReleases()
        { var quota = Quota(); quota.TryReserve(card.authoredKey); var attempt = new ProductionAttempt(card, quota); Assert.That(attempt.Complete(true), Is.Not.Null); Assert.That(attempt.Complete(true), Is.Null); Assert.That(quota.Remaining(card.authoredKey), Is.EqualTo(1)); quota.TryReserve(card.authoredKey); new ProductionAttempt(card, quota).Dispose(); Assert.That(quota.Available(card.authoredKey), Is.True); }
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
        [Test] public void SixStrategyFixturesPerFactionValidateNearBudget()
        {
            var rules = new DeckRules(AssetDatabase.LoadAssetAtPath<StartingDeckCatalog>("Assets/Cards/StartingDeckCatalog.asset"),
                AssetDatabase.LoadAssetAtPath<ResearchProductionCatalog>("Assets/Cards/ResearchProductionCatalog.asset"));
            Assert.That(DeckCalibrationReport.CreateStrategyDecks(rules).Count, Is.EqualTo(18));
        }
        [Test] public void VesselsStarterKeepsMutatorsInCollectionButNotInLoadout()
        {
            var rules = new DeckRules(AssetDatabase.LoadAssetAtPath<StartingDeckCatalog>("Assets/Cards/StartingDeckCatalog.asset"),
                AssetDatabase.LoadAssetAtPath<ResearchProductionCatalog>("Assets/Cards/ResearchProductionCatalog.asset"));
            var p = new CollectionProfile(); new CollectionService(rules, null, p).InitializeStarters(p);
            Assert.That(p.savedDecks.Single(d => d.faction == Faction.Vessels).mutators, Is.Empty);
            Assert.That(p.ownedCards.Any(e => rules.Resolve(e.cardKey)?.attachmentSlot == AttachmentSlot.Mutator), Is.True);
        }
        [Test] public void CollectionPreviewRemainsClickableWhileGameplayIsBlocked()
        {
            var go = new GameObject("preview", typeof(RectTransform));
            try
            {
                var preview = go.AddComponent<Game.UI.ArmyUnitCardUI>(); bool clicked = false;
                Game.UI.UIFocusUtility.SetOverlay(go, true);
                preview.SetupPreview(card, null, _ => clicked = true, () => true);
                preview.OnPointerClick(new UnityEngine.EventSystems.PointerEventData(null));
                Assert.That(clicked, Is.True);
                clicked = false; preview.SetupPreview(card, null, _ => clicked = true, () => false);
                preview.OnPointerClick(new UnityEngine.EventSystems.PointerEventData(null));
                Assert.That(clicked, Is.False, "Nested modal must keep the underlying card inert.");
            }
            finally
            {
                Game.UI.UIFocusUtility.SetOverlay(go, false); UnityEngine.Object.DestroyImmediate(go);
                // EditMode does not advance frameCount; remove the close-frame shortcut guard.
                typeof(Game.UI.UIFocusUtility).GetMethod("ResetOverlays", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, null);
            }
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
        [Test] public void AuthoredCatalogsHaveStableUniqueKeysAndCompatibleStarters() { CollectionContentValidation.Validate(); }
    }
}
#endif
