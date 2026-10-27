#if UNITY_INCLUDE_TESTS
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Game.Ai;
using Game.Cards;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Progression;
using Game.UI;
using Game.Units;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;

namespace Game.EditorTests
{
    public sealed class LibraryEventPolicyTests
    {
        [TearDown] public void TearDown() { HexEventRegistry.Clear(); AiMapMemory.Clear(); }

        [Test] public void AiCannotDiscoverOrExploreLibraryAndHumanCan()
        {
            var library = new EventDefinition { grantsRandomCollectionCard = true };
            var ai = new PlayerSetupData();
            var human = new PlayerSetupData { IsHuman = true };
            var hex = new HexCoord(2, 1);
            HexEventRegistry.Set(hex, library, null, null, null, null);
            var entry = HexEventRegistry.FindAt(hex);
            AiMapMemory.EnsureSubscribed();
            HexEventRegistry.MarkDiscovered(hex, ai);
            HexEventRegistry.MarkSkipped(hex, ai);
            Assert.That(library.CanVisit(ai), Is.False);
            Assert.That(HexEventGuardEstimate.ShouldExplore(new ArmyData { Owner = ai }, entry), Is.False);
            Assert.That(entry.KnownBy, Is.Empty);
            Assert.That(entry.Skipped, Is.False);
            Assert.That(AiMapMemory.KnownEventStateAt(ai, hex), Is.EqualTo(AiMapMemory.KnownEventState.Unknown));
            Assert.That(entry.Consumed, Is.False);
            HexEventRegistry.MarkDiscovered(hex, human);
            Assert.That(library.CanVisit(human), Is.True);
            Assert.That(entry.KnownBy, Does.Contain(human));
        }

        [Test] public void OrdinaryEventsStillAllowAi()
        {
            var definition = new EventDefinition();
            Assert.That(definition.CanVisit(new PlayerSetupData()), Is.True);
            Assert.That(HexEventGuardEstimate.ShouldExplore(new ArmyData(), new HexEventRegistry.Entry { Definition = definition }), Is.True);
        }

        [Test] public void PendingLibraryDescriptionDoesNotAdvertiseDefeatedGuards()
        {
            var entry = new HexEventRegistry.Entry { Definition = new EventDefinition { grantsRandomCollectionCard = true }, CollectionRewardPending = true };
            entry.ResolvedGuardMembers.Add((new CardDefinition(), 3));
            var description = typeof(EventChoicePopupUI).GetMethod("BuildDescription", BindingFlags.NonPublic | BindingFlags.Static);
            string text = (string)description.Invoke(null, new object[] { entry });
            Assert.That(text, Does.Contain("guards have been defeated"));
            Assert.That(text, Does.Contain("COLLECTION"));
            Assert.That(text, Does.Not.Contain("3 targets"));
        }

        [Test] public void SummaryNamesCollectionOnlyAfterSuccessfulGrant()
        {
            var entry = new HexEventRegistry.Entry { Definition = new EventDefinition { grantsRandomCollectionCard = true } };
            var summary = typeof(EventRewardPopupUI).GetMethod("BuildSummary", BindingFlags.NonPublic | BindingFlags.Static);
            string Read() => (string)summary.Invoke(null, new object[] { entry, new ArmyData { Name = "Scout" } });
            Assert.That(Read(), Does.Contain("already own"));
            entry.CollectionRewardError = "The reward could not be saved. Revisit the Library to try again.";
            Assert.That(Read(), Does.Contain("could not be saved"));
            entry.CollectionRewardError = null;
            entry.ResolvedCardRewards.Add((new RewardEntry { type = RewardType.Card }, new CardDefinition { displayName = "Foreign unit" }));
            Assert.That(Read(), Does.Contain("Foreign unit"));
            Assert.That(Read(), Does.Contain("COLLECTION"));
            Assert.That(Read(), Does.Contain("hand"));
        }
    }

    public sealed class LibraryCollectionRewardTests
    {
        private StartingDeckCatalog starting;
        private ResearchProductionCatalog research;
        private FactionCardCatalog own, foreign, neutral;
        private CollectionService collection;
        private CollectionProfileStore store;
        private string directory;
        private CardDefinition card;

        [SetUp] public void SetUp()
        {
            starting = ScriptableObject.CreateInstance<StartingDeckCatalog>();
            research = ScriptableObject.CreateInstance<ResearchProductionCatalog>();
            own = ScriptableObject.CreateInstance<FactionCardCatalog>(); own.faction = Faction.IronConcord;
            foreign = ScriptableObject.CreateInstance<FactionCardCatalog>(); foreign.faction = Faction.Ashen;
            neutral = ScriptableObject.CreateInstance<FactionCardCatalog>(); neutral.faction = Faction.Neutral;
            starting.catalogs.AddRange(new[] { own, foreign, neutral });
            card = new CardDefinition { authoredKey = "library.foreign", displayName = "Foreign unit", faction = Faction.Ashen, cardType = CardType.Unit, deckCopyLimit = 3 };
            foreign.cards.Add(card);
            directory = Path.Combine(Path.GetTempPath(), "library-tests-" + Guid.NewGuid().ToString("N"));
            store = new CollectionProfileStore(directory);
            collection = new CollectionService(new DeckRules(starting, research), store, new CollectionProfile());
        }

        [TearDown] public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(starting); UnityEngine.Object.DestroyImmediate(research);
            UnityEngine.Object.DestroyImmediate(own); UnityEngine.Object.DestroyImmediate(foreign); UnityEngine.Object.DestroyImmediate(neutral);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        [Test] public void ForeignFactionCardIsPermanentlySavedWithoutChangingSavedDeck()
        {
            Assert.That(collection.Transact(p => p.savedDecks.Add(new SavedDeck { deckId = "unchanged", name = "Player deck", faction = Faction.IronConcord }), out _), Is.True);
            Assert.That(collection.TryGrantRandomCollectionCard(out var acquired, out var error), Is.True, error);
            Assert.That(acquired, Is.SameAs(card));
            Assert.That(store.Load(out _).Owned(card.authoredKey), Is.EqualTo(1));
            Assert.That(collection.Snapshot.savedDecks.Single().mainCards, Is.Empty);
            Assert.That(collection.Snapshot.pendingRewards, Is.Empty);
            Assert.That(collection.Snapshot.claimedMatchIds, Is.Empty);
        }

        [Test] public void FullCardsAndSystemCardsCannotBeChosen()
        {
            var full = new CardDefinition { authoredKey = "library.full", cardType = CardType.Unit, deckCopyLimit = 1 };
            own.cards.Add(full);
            own.cards.Add(new CardDefinition { authoredKey = "library.excluded", cardType = CardType.Unit, deckBuilderExcluded = true, deckCopyLimit = 2 });
            neutral.cards.Add(new CardDefinition { authoredKey = "library.tactic", cardType = CardType.Tactic, deckCopyLimit = 2 });
            Assert.That(collection.Transact(p => p.ownedCards.Add(new DeckCardEntry { cardKey = full.authoredKey, count = 1 }), out _), Is.True);
            for (int i = 0; i < 3; i++)
            {
                Assert.That(collection.TryGrantRandomCollectionCard(out var acquired, out var error), Is.True, error);
                Assert.That(acquired, Is.SameAs(card));
            }
            Assert.That(collection.TryGrantRandomCollectionCard(out var exhausted, out _), Is.True);
            Assert.That(exhausted, Is.Null);
            Assert.That(collection.Owned(card.authoredKey), Is.EqualTo(3));
            Assert.That(collection.Owned(full.authoredKey), Is.EqualTo(1));
        }

        [Test] public void HeroAndBlueprintUseExistingOneCopyLimits()
        {
            foreign.cards.Clear(); card.cardType = CardType.Hero; foreign.cards.Add(card);
            Assert.That(collection.TryGrantRandomCollectionCard(out _, out _), Is.True);
            Assert.That(collection.TryGrantRandomCollectionCard(out var secondHero, out _), Is.True);
            Assert.That(secondHero, Is.Null);
            card.cardType = CardType.Equipment; card.attachmentSlot = AttachmentSlot.Equipment;
            research.cardCatalogs.Add(foreign);
            research.productionCards.Add(new ResearchProductionEntry { cardKey = card.authoredKey });
            Assert.That(collection.TryGrantRandomCollectionCard(out var secondBlueprint, out _), Is.True);
            Assert.That(secondBlueprint, Is.Null);
        }

        [Test] public void LibraryDoesNotConsumeOrRewriteMatchReward()
        {
            var rewards = new RewardService(collection);
            var player = new PlayerSetupData { IsHuman = true, Faction = Faction.Ashen };
            Assert.That(rewards.Record(new ParticipantResult("match-independent", player, MatchOutcome.Victory), out _), Is.True);
            var before = collection.Snapshot.pendingRewards.Single();
            Assert.That(collection.TryGrantRandomCollectionCard(out _, out _), Is.True);
            var after = collection.Snapshot.pendingRewards.Single();
            Assert.That(after.offeredKeys, Is.EqualTo(before.offeredKeys));
            Assert.That(after.claimed, Is.False);
            Assert.That(after.acquiredKeys, Is.Empty);
            Assert.That(collection.Snapshot.claimedMatchIds, Is.Empty);
            Assert.That(rewards.Claim(before.matchId, new[] { card.authoredKey }, out var error), Is.True, error);
            Assert.That(collection.Owned(card.authoredKey), Is.EqualTo(2));
        }

        [Test] public void SharedNeutralCardIsEligibleAndCannotExceedItsLimit()
        {
            foreign.cards.Clear(); card.faction = Faction.Neutral; card.deckCopyLimit = 1; neutral.cards.Add(card);
            Assert.That(collection.TryGrantRandomCollectionCard(out var acquired, out _), Is.True);
            Assert.That(acquired, Is.SameAs(card));
            Assert.That(collection.TryGrantRandomCollectionCard(out var exhausted, out _), Is.True);
            Assert.That(exhausted, Is.Null);
            Assert.That(collection.Owned(card.authoredKey), Is.EqualTo(1));
        }

        [Test] public void AuthoredLibraryHasThreeRealGuardVariantsAndNoFixedRewards()
        {
            var events = AssetDatabase.LoadAssetAtPath<EventCatalog>("Assets/Cards/EventCatalog.asset");
            var armies = AssetDatabase.LoadAssetAtPath<NeutralArmyCatalog>("Assets/Cards/NeutralArmyCatalog.asset");
            var library = events.events.Single(e => e.name == "Library");
            Assert.That(library.grantsRandomCollectionCard, Is.True);
            Assert.That(library.variants.Count, Is.EqualTo(3));
            foreach (var variant in library.variants)
            {
                Assert.That(armies.GetArmy(variant.guardArmyName).members, Is.Not.Empty);
                Assert.That(variant.resourceCount, Is.Zero);
                Assert.That(variant.rewards, Is.Empty);
            }
        }

        [Test] public void FailedDiskSaveDoesNotPublishReward()
        {
            Directory.CreateDirectory(directory);
            string obstruction = Path.Combine(directory, "file-instead-of-directory"); File.WriteAllText(obstruction, "blocked");
            collection = new CollectionService(collection.Rules, new CollectionProfileStore(obstruction), new CollectionProfile());
            Assert.That(collection.TryGrantRandomCollectionCard(out var acquired, out var error), Is.False);
            Assert.That(acquired, Is.Null);
            Assert.That(error, Is.Not.Empty);
            Assert.That(collection.Owned(card.authoredKey), Is.Zero);
        }

        [Test, Category("UnityScene")] public void LibraryGrantAddsSameCardToFullHandAndCollectionOnlyOnce()
        {
            WithScene((selector, hand) =>
            {
                hand.BindHumanHand(new PlayerHandData(null, 0, capacity: 1));
                hand.AddCard(new CardData(card));
                var human = new PlayerSetupData { IsHuman = true, Faction = Faction.IronConcord };
                var hex = new HexCoord(3, 1);
                HexEventRegistry.Set(hex, new EventDefinition { grantsRandomCollectionCard = true }, null, null, null, null);
                Assert.That(HexEventRegistry.FindAt(hex).ResolvedCardRewards, Is.Empty);
                var army = new ArmyData { Owner = human };
                selector.GrantEventReward(hex, army);
                selector.GrantEventReward(hex, army);
                Assert.That(hand.HumanHand.Count(), Is.EqualTo(2));
                Assert.That(hand.HumanHand.Last().Definition, Is.SameAs(card));
                Assert.That(store.Load(out _).Owned(card.authoredKey), Is.EqualTo(1));
                Assert.That(HexEventRegistry.FindAt(hex).Consumed, Is.True);
            });
        }

        [Test, Category("UnityScene")] public void ReplayedLibraryVisitRestoresOriginalHandCardWithoutPermanentDuplicate()
        {
            WithScene((selector, hand) =>
            {
                var human = new PlayerSetupData { IsHuman = true, Faction = Faction.IronConcord };
                var hex = new HexCoord(3, 1);
                var definition = new EventDefinition { grantsRandomCollectionCard = true };
                HexEventRegistry.Set(hex, definition, null, null, null, null);
                var army = new ArmyData { Owner = human };
                selector.GrantEventReward(hex, army);
                var original = hand.HumanHand.Single().Definition;
                collection = new CollectionService(collection.Rules, store, store.Load(out _));
                typeof(ProgressionContext).GetProperty("Collection").SetValue(null, collection);
                // Equivalent event/hand state to a tactical snapshot taken before the visit.
                hand.BindHumanHand(new PlayerHandData(null, 0));
                HexEventRegistry.Set(hex, definition, null, null, null, null);
                selector.GrantEventReward(hex, army);
                selector.GrantEventReward(hex, army);
                Assert.That(hand.HumanHand.Single().Definition, Is.SameAs(original));
                Assert.That(collection.Owned(original.authoredKey), Is.EqualTo(1));
                Assert.That(HexEventRegistry.FindAt(hex).ResolvedCardRewards.Single().Item2, Is.SameAs(original));
                Assert.That(HexEventRegistry.FindAt(hex).Consumed, Is.True);
            });
        }

        [Test, Category("UnityScene")] public void AiEntryPointsDoNotStopMoveSpawnGuardOrConsumeLibrary()
        {
            WithScene((selector, hand) =>
            {
                var ai = new PlayerSetupData();
                var army = new ArmyData { Owner = ai };
                var hex = new HexCoord(3, 1);
                HexEventRegistry.Set(hex, new EventDefinition { grantsRandomCollectionCard = true }, "Library guard",
                    new System.Collections.Generic.List<(CardDefinition, int)> { (card, 1) }, new PlayerSetupData { IsNeutral = true }, null);
                var clean = typeof(HexSelectionController).GetMethod("HasUnclaimedCleanEventHex", BindingFlags.NonPublic | BindingFlags.Static);
                Assert.That(clean.Invoke(null, new object[] { army, hex }), Is.False);
                Assert.That(selector.TriggerAiEventExplore(army, hex), Is.False);
                Assert.That(selector.TriggerHexEventIfClear(hex, army), Is.False);
                selector.GrantEventReward(hex, army);
                var entry = HexEventRegistry.FindAt(hex);
                Assert.That(entry.Consumed || entry.Triggered || entry.Skipped, Is.False);
                Assert.That(entry.KnownBy, Is.Empty);
                Assert.That(ArmyRegistry.AllAt(hex), Is.Empty);
                Assert.That(hand.HumanHand, Is.Empty);
                Assert.That(collection.Owned(card.authoredKey), Is.Zero);
            });
        }

        [Test, Category("UnityScene")] public void HeroOnlyLibraryVisitorIsTheCaptureKillTarget()
        {
            WithScene((selector, hand) =>
            {
                var human = new PlayerSetupData { IsHuman = true };
                var neutralPlayer = new PlayerSetupData { IsNeutral = true };
                var hex = new HexCoord(3, 1);
                var hero = new UnitData { Owner = human, IsHero = true, HitPointsCurrent = 1, HitPointsMax = 1, Fate = 2, FateMax = 2 };
                var army = new ArmyData { Owner = human, Hex = hex }; army.Members.Add(hero); ArmyRegistry.Register(army);
                HexEventRegistry.Set(hex, new EventDefinition { grantsRandomCollectionCard = true }, "Library guard",
                    new System.Collections.Generic.List<(CardDefinition, int)> { (card, 1) }, neutralPlayer, null);
                var screen = selector.gameObject.AddComponent<BattleScreenUI>();
                var popup = selector.gameObject.AddComponent<BattleAttackPopupUI>();
                SetField(screen, "attackPopup", popup);
                SetField(selector, "battleScreen", screen);
                var explore = typeof(HexSelectionController).GetMethod("ResolveEventExplore", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.That(explore.Invoke(selector, new object[] { army, hex, HexEventRegistry.FindAt(hex) }), Is.True);
                var defender = typeof(BattleAttackPopupUI).GetField("_defender", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.That(defender.GetValue(popup), Is.SameAs(hero));
                var state = (BattleState)typeof(BattleScreenUI).GetField("_battleState", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(screen);
                Assert.That(state.Attacker.Owner, Is.SameAs(neutralPlayer));
                Assert.That(state.Defender, Is.SameAs(army));
                Assert.That(hand.HumanHand, Is.Empty);
                Assert.That(collection.Owned(card.authoredKey), Is.Zero);
            });
        }

        [Test, Category("UnityScene")] public void PendingLibraryClaimUsesExploreActionWithoutRespawningGuard()
        {
            WithScene((selector, hand) =>
            {
                var human = new PlayerSetupData { IsHuman = true };
                var hex = new HexCoord(3, 1);
                var visitor = new UnitData { Owner = human, MoveMax = 3, MoveCurrent = 3 };
                var army = new ArmyData { Owner = human, Hex = hex }; army.Members.Add(visitor);
                HexEventRegistry.Set(hex, new EventDefinition { grantsRandomCollectionCard = true }, "Library guard",
                    new System.Collections.Generic.List<(CardDefinition, int)> { (card, 1) }, new PlayerSetupData { IsNeutral = true }, null);
                var entry = HexEventRegistry.FindAt(hex); entry.Triggered = true; entry.CollectionRewardPending = true;
                var explore = typeof(HexSelectionController).GetMethod("ResolveEventExplore", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.That(explore.Invoke(selector, new object[] { army, hex, entry }), Is.False);
                Assert.That(visitor.MoveCurrent, Is.Zero);
                Assert.That(entry.Consumed, Is.True);
                Assert.That(entry.CollectionRewardPending, Is.False);
                Assert.That(ArmyRegistry.AllAt(hex), Is.Empty);
                Assert.That(hand.HumanHand.Single().Definition, Is.SameAs(card));
            });
        }

        [TestCase(true), TestCase(false), Category("UnityScene")]
        public void FailedHumanExplorationRemovesOnlyLibraryGuardBeforeAiArrival(bool isLibrary)
        {
            WithScene((selector, hand) =>
            {
                var hex = new HexCoord(3, 1);
                var human = new PlayerSetupData { IsHuman = true };
                var neutralPlayer = new PlayerSetupData { IsNeutral = true };
                var explorer = new ArmyData { Owner = human, Hex = hex };
                var guard = new ArmyData { Owner = neutralPlayer, Name = "Library guard", Hex = hex };
                guard.Members.Add(new UnitData { Owner = neutralPlayer, HitPointsCurrent = 1, HitPointsMax = 1 });
                ArmyRegistry.Register(explorer); ArmyRegistry.Register(guard);
                HexEventRegistry.Set(hex, new EventDefinition { grantsRandomCollectionCard = isLibrary }, guard.Name,
                    new System.Collections.Generic.List<(CardDefinition, int)> { (card, 1) }, neutralPlayer, null);
                HexEventRegistry.MarkDiscovered(hex, human);
                var entry = HexEventRegistry.FindAt(hex); entry.Triggered = true;
                var state = new BattleState(hex, explorer, guard, new BattleGrid(), 123);
                var finalization = BattleEngine.FinalizeEncounter(state, selector);
                Assert.That(entry.Consumed, Is.False);
                Assert.That(entry.Triggered, Is.EqualTo(!isLibrary));
                Assert.That(finalization.Survivor, isLibrary ? Is.Null : Is.SameAs(guard));
                var nextAi = new ArmyData { Owner = new PlayerSetupData(), Hex = hex };
                Assert.That(BattleInitiator.FindEnemyAt(hex, nextAi), isLibrary ? Is.Null : Is.SameAs(guard));
                Assert.That(collection.Owned(card.authoredKey), Is.Zero);
            });
        }

        [Test, Category("UnityScene")] public void SurvivingExplorerHeroStillFacesLibraryGuard()
        {
            WithScene((selector, hand) =>
            {
                var hex = new HexCoord(3, 1);
                var human = new PlayerSetupData { IsHuman = true };
                var neutralPlayer = new PlayerSetupData { IsNeutral = true };
                var explorer = new ArmyData { Owner = human, Hex = hex };
                explorer.Members.Add(new UnitData { Owner = human, IsHero = true, HitPointsCurrent = 1, HitPointsMax = 1 });
                var guard = new ArmyData { Owner = neutralPlayer, Name = "Library guard", Hex = hex };
                guard.Members.Add(new UnitData { Owner = neutralPlayer, HitPointsCurrent = 1, HitPointsMax = 1 });
                ArmyRegistry.Register(explorer); ArmyRegistry.Register(guard);
                HexEventRegistry.Set(hex, new EventDefinition { grantsRandomCollectionCard = true }, guard.Name,
                    new System.Collections.Generic.List<(CardDefinition, int)> { (card, 1) }, neutralPlayer, null);
                var entry = HexEventRegistry.FindAt(hex); entry.Triggered = true;
                var result = BattleEngine.FinalizeEncounter(new BattleState(hex, explorer, guard, new BattleGrid(), 123), selector);
                Assert.That(result.Survivor, Is.SameAs(guard));
                Assert.That(entry.Triggered, Is.True);
                Assert.That(BattleInitiator.FindEnemyAt(hex, guard), Is.SameAs(explorer));
            });
        }

        private static void SetField(object target, string field, object value) => target.GetType()
            .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(target, value);

        private void WithScene(Action<HexSelectionController, CardHandUI> check)
        {
            var previous = ProgressionContext.Collection;
            string previousMatchId = Game.Core.GameSession.MatchId;
            var scene = new GameObject("Library reward test"); scene.SetActive(false);
            HexEventRegistry.Clear(); ArmyRegistry.Clear();
            try
            {
                typeof(ProgressionContext).GetProperty("Collection").SetValue(null, collection);
                typeof(Game.Core.GameSession).GetProperty("MatchId").SetValue(null, "library-scene-test");
                var selector = scene.AddComponent<HexSelectionController>();
                var hand = scene.AddComponent<CardHandUI>();
                var container = new GameObject("hand", typeof(RectTransform)); container.transform.SetParent(scene.transform);
                var template = new GameObject("card", typeof(RectTransform)); template.transform.SetParent(scene.transform);
                SetField(hand, "handContainer", container.GetComponent<RectTransform>());
                SetField(hand, "cardPrefab", template.AddComponent<CardUI>());
                SetField(selector, "cardHandUI", hand);
                check(selector, hand);
            }
            finally
            {
                typeof(ProgressionContext).GetProperty("Collection").SetValue(null, previous);
                typeof(Game.Core.GameSession).GetProperty("MatchId").SetValue(null, previousMatchId);
                HexEventRegistry.Clear(); ArmyRegistry.Clear();
                UnityEngine.Object.DestroyImmediate(scene);
            }
        }
    }
}
#endif
