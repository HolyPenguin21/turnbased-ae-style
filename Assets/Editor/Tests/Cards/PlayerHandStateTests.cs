#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Cards;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class PlayerHandStateTests
    {
        private static CardDefinition Card(string key) => new CardDefinition { authoredKey = key, cardType = CardType.Unit };

        [Test]
        public void HumanModelOwnsCardsOrderAndDeckWithoutAnyPresentation()
        {
            var first = Card("first"); var second = Card("second");
            var hand = new PlayerHandData(new[] { first, first, second }, 0, 2);
            var a = new CardData(first); var b = new CardData(second);
            int events = 0; hand.HandChanged += () => events++;
            Assert.That(hand.TryAddCard(a), Is.True);
            Assert.That(hand.TryAddCard(b), Is.True);
            Assert.That(hand.MoveCard(b, 0), Is.True);
            Assert.That(hand.Hand, Is.EqualTo(new[] { b, a }));
            Assert.That(hand.DrawOne(), Is.Null);
            Assert.That(hand.RemainingDeck, Is.EqualTo(new[] { first, first, second }));
            Assert.That(hand.TryAddCard(new CardData(first)), Is.False);
            Assert.That(events, Is.EqualTo(3));
            Assert.That(a.AcquiredTurn, Is.EqualTo(-1));
            Assert.That(hand.RemoveCard(a), Is.True);
            Assert.That(events, Is.EqualTo(4));
        }

        [Test]
        public void ExplicitOverflowRetainsLibraryAndPrepaidCardsWhileOrdinaryGrantsStayCapped()
        {
            var definition = Card("card");
            var hand = new PlayerHandData(null, 0, 0);
            Assert.That(hand.TryAddCard(new CardData(definition)), Is.False);
            Assert.That(hand.TryAddCard(new CardData(definition), allowOverflow: true), Is.True);
            Assert.That(hand.TryAddCard(new CardData(definition) { ResearchProductionCreated = true }), Is.True);
            Assert.That(hand.TryAddCard(new CardData(definition)), Is.False);
            Assert.That(hand.Hand.Count, Is.EqualTo(2));
        }

        [Test]
        public void RestorePreservesIdsAttachmentsFlagsAgeOrderAndEveryUndrawnCopy()
        {
            var unit = Card("unit"); var gear = Card("gear"); var mutator = Card("mutator");
            var definitions = new Dictionary<string, CardDefinition> { ["unit"] = unit, ["gear"] = gear, ["mutator"] = mutator };
            var original = new AiHandData(null, Faction.None, 0, 1);
            original.SetCurrentTurn(3);
            original.AddCard(new CardData(unit) { Equipment = gear, Mutator = mutator, ResearchProductionCreated = true });
            original.SetCurrentTurn(9);
            var state = original.CaptureState();
            state.remainingDeckKeys.AddRange(new[] { "unit", "gear", "unit" });
            var restored = AiHandData.FromState(state, key => definitions[key]);
            Assert.That(restored.CurrentTurn, Is.EqualTo(9));
            Assert.That(restored.Hand.Single().RuntimeId, Is.EqualTo(original.Hand.Single().RuntimeId));
            Assert.That(restored.Hand.Single().Equipment, Is.SameAs(gear));
            Assert.That(restored.Hand.Single().Mutator, Is.SameAs(mutator));
            Assert.That(restored.Hand.Single().ResearchProductionCreated, Is.True);
            Assert.That(restored.Hand.Single().AcquiredTurn, Is.EqualTo(3), "Restore must bypass AddCard's acquisition stamp.");
            Assert.That(restored.MutationVersion, Is.EqualTo(original.MutationVersion));
            Assert.That(restored.RemainingDeck, Is.EqualTo(new[] { unit, gear, unit }));
            restored.RemoveCard(restored.Hand.Single());
            var next = new CardData(unit); restored.AddCard(next);
            Assert.That(next.AcquiredTurn, Is.EqualTo(9), "New cards retain AI acquisition semantics.");
            Assert.That(next.RuntimeId, Is.GreaterThan(state.cards[0].runtimeId));
            state.remainingDeckKeys.Clear();
            Assert.That(restored.RemainingDeckCount, Is.EqualTo(3), "Snapshot lists must not alias live state.");
        }


        [Test]
        public void SnapshotPreservesOverflowAndHumanOrderingWithoutSharingLists()
        {
            var card = Card("card");
            var hand = new PlayerHandData(null, 0, 1);
            var a = new CardData(card); var b = new CardData(card);
            hand.TryAddCard(a); hand.TryAddCard(b, allowOverflow: true); hand.MoveCard(b, 0);
            var state = hand.CaptureState();
            var restored = PlayerHandData.FromState(state, key => card);
            Assert.That(restored.Hand.Select(c => c.RuntimeId), Is.EqualTo(new[] { b.RuntimeId, a.RuntimeId }));
            Assert.That(restored.Capacity, Is.EqualTo(1));
            state.cards.Clear();
            Assert.That(restored.Hand.Count, Is.EqualTo(2));
            Assert.That(restored.Hand[0].AcquiredTurn, Is.EqualTo(-1));
        }

        [Test]
        public void BlueprintSnapshotOwnsItsUnlocksAndPendingReservations()
        {
            var state = new BlueprintQuotaState();
            state.unlockedKeys.AddRange(new[] { "gear", "mutator" });
            state.pendingKeys.Add("gear");
            var restored = BlueprintQuota.FromState(state);
            Assert.That(restored.Available("gear"), Is.False);
            Assert.That(restored.Available("mutator"), Is.True);
            var copy = restored.CaptureState();
            state.unlockedKeys.Clear(); copy.pendingKeys.Clear();
            Assert.That(restored.Unlocked("gear"), Is.True);
            Assert.That(restored.Available("gear"), Is.False);
            restored.Release("gear");
            Assert.That(restored.Available("gear"), Is.True);
            var invalid = new BlueprintQuotaState(); invalid.pendingKeys.Add("unknown");
            Assert.Throws<InvalidOperationException>(() => BlueprintQuota.FromState(invalid));
        }
        [Test]
        public void RestoredAiRegistryRebindsTheExactModelWithoutStartingDeal()
        {
            AiHandRegistry.Clear();
            try
            {
                var player = new PlayerSetupData { Faction = Faction.IronConcord };
                var old = AiHandRegistry.GetOrCreate(player, null, 0);
                var restored = AiHandData.FromState(old.CaptureState(), key => null);
                AiHandRegistry.RegisterRestored(player, restored);
                Assert.That(AiHandRegistry.Peek(player), Is.SameAs(restored));
                Assert.That(AiHandRegistry.GetOrCreate(player, null, 7), Is.SameAs(restored));
                Assert.That(AiHandRegistry.TryGetOwner(old, out _), Is.False);
                Assert.That(AiHandRegistry.TryGetOwner(restored, out var owner), Is.True);
                Assert.That(owner, Is.SameAs(player));
            }
            finally { AiHandRegistry.Clear(); }
        }

        [Test]
        public void RestoreRejectsUnknownKeysAndDuplicateInstanceIds()
        {
            var card = Card("known");
            var source = new PlayerHandData(null, 0); source.AddCard(new CardData(card));
            var state = source.CaptureState();
            state.remainingDeckKeys.Add("missing");
            Assert.Throws<InvalidOperationException>(() => PlayerHandData.FromState(state, key => key == "known" ? card : null));
            state.remainingDeckKeys.Clear(); state.cards.Add(state.cards[0]);
            Assert.Throws<InvalidOperationException>(() => PlayerHandData.FromState(state, key => card));
        }
    }
}
#endif
