#if UNITY_INCLUDE_TESTS
using System;
using Game.Cards;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class ResearchProductionSessionTests
    {
        [TestCase(true)] [TestCase(false)]
        public void AcceptFreezesResultAndNeverTouchesLiveFate(bool hit)
        {
            var hero = new UnitData { Fate = 2, FateMax = 1 };
            using var session = new ResearchProductionChallengeSession(hero, new CardDefinition { fate = 2 }, n => new[] { hit });
            session.Roll();
            Assert.That(session.TrySpend(out _, out _), Is.True);
            Assert.That(hero.Fate, Is.EqualTo(2));
            hero.Fate = 7; // an unrelated live mutation must survive every terminal path
            var result = session.Accept();
            Assert.That(result.Success, Is.EqualTo(hit));
            Assert.That(session.Accept().Successes, Is.EqualTo(result.Successes));
            Assert.That(session.TrySpend(out _, out _), Is.False);
            session.Cancel(); session.Dispose();
            Assert.That(hero.Fate, Is.EqualTo(7));
            Assert.That(session.IsAccepted, Is.True);
        }

        [TestCase(false)] [TestCase(true)]
        public void CancelAndDisposePreserveUnrelatedLiveFate(bool dispose)
        {
            var hero = new UnitData { Fate = 2, FateMax = 1 };
            var session = new ResearchProductionChallengeSession(hero, new CardDefinition { fate = 3 }, n => new[] { false });
            session.Roll(); session.TrySpend(out _, out _);
            hero.Fate = 8;
            if (dispose) session.Dispose(); else session.Cancel();
            Assert.That(hero.Fate, Is.EqualTo(8));
            Assert.That(session.IsCancelled, Is.True);
            Assert.That(session.CanSpend, Is.False);
            Assert.Throws<InvalidOperationException>(() => session.Accept());
        }

        [Test]
        public void TokensInitializeOnlyOnceAndBothDirectionsAreCopied()
        {
            var session = new ResearchProductionChallengeSession(null, new CardDefinition { fate = 1 });
            var original = new[] { false };
            session.Tokens = original;
            original[0] = true;
            Assert.That(session.Success, Is.False);
            var view = session.Tokens; view[0] = true;
            Assert.That(session.Success, Is.False);
            Assert.Throws<InvalidOperationException>(() => session.Tokens = new[] { true });
            session.Roll();
            Assert.That(session.Success, Is.False);
        }

        [Test]
        public void SpendRerollsFirstMissBeforeAppendingOverflow()
        {
            var session = new ResearchProductionChallengeSession(new UnitData { Fate = 2, FateMax = 2 },
                new CardDefinition { fate = 3 }, n => new[] { true });
            session.Tokens = new[] { true, false };
            Assert.That(session.TrySpend(out int index, out bool appended), Is.True);
            Assert.That(index, Is.EqualTo(1)); Assert.That(appended, Is.False);
            Assert.That(session.TrySpend(out index, out appended), Is.True);
            Assert.That(index, Is.EqualTo(2)); Assert.That(appended, Is.True);
            Assert.That(session.Accept().Successes, Is.EqualTo(3));
        }

        [TestCase(0)] [TestCase(1)] [TestCase(3)]
        public void HeadlessAndManualCommandsShareInjectedRollsAndBudget(int budget)
        {
            var hero = new UnitData { Fate = 3, FateMax = 2 };
            var card = new CardDefinition { fate = 3 };
            int calls = 0;
            Func<int, bool[]> roller = n => ++calls == 1 ? new[] { false, true } : new[] { true };
            var headless = ResearchProductionSystem.RollChallenge(hero, card, budget, roller);
            calls = 0;
            using var session = new ResearchProductionChallengeSession(hero, card, roller);
            session.Roll();
            while (session.FateSpent < budget && !session.Success && session.CanSpend) session.TrySpend(out _, out _);
            var manual = session.Accept();
            Assert.That(headless.Successes, Is.EqualTo(manual.Successes));
            Assert.That(headless.FateSpent, Is.EqualTo(manual.FateSpent));
            Assert.That(hero.Fate, Is.EqualTo(3));
        }

        [Test]
        public void PendingTacticPreviewCanBlockOverflowWithoutCommittingModifiers()
        {
            using var session = new ResearchProductionChallengeSession(new UnitData { Fate = 1, FateMax = 1 },
                new CardDefinition { fate = 2 }, n => new[] { true });
            session.Tokens = new[] { true };
            Assert.That(session.CanSpendWithModifiers(1, 0), Is.False);
            Assert.That(session.TrySpendWithModifiers(1, 0, out _, out _), Is.False);
            Assert.That(session.FateRemaining, Is.EqualTo(1));
            Assert.That(session.TrySpendWithModifiers(0, 0, out _, out bool appended), Is.True);
            Assert.That(appended, Is.True);
            Assert.That(session.SuccessModifier, Is.Zero);
        }

        [Test]
        public void ThrowingTacticObserverCancelsSessionAndNeverTouchesLiveFate()
        {
            var owner = new Game.Players.PlayerSetupData();
            var hero = new UnitData { Owner = owner, Fate = 4 };
            var tactic = new CardData(new CardDefinition { cardType = CardType.Tactic,
                tacticEffect = TacticEffect.AddSuccess, tacticAmount = 1 });
            owner.Tactics.Add(tactic);
            var session = new ResearchProductionChallengeSession(hero, new CardDefinition { fate = 2 });
            session.Tokens = new[] { true };
            owner.Tactics.Changed += () => throw new ApplicationException("Injected observer failure");
            Assert.Throws<ApplicationException>(() => session.TryPlay(tactic));
            Assert.That(session.IsCancelled, Is.True);
            Assert.That(hero.Fate, Is.EqualTo(4));
            Assert.That(owner.Tactics.Contains(tactic), Is.False);
        }

        [TestCase(false)] [TestCase(true)]
        public void RollSourceExceptionCancelsWithoutSpendingLiveFate(bool duringSpend)
        {
            var hero = new UnitData { Fate = 2, FateMax = 1 };
            var session = new ResearchProductionChallengeSession(hero, new CardDefinition { fate = 2 },
                n => throw new ApplicationException("Injected roll failure"));
            if (duringSpend)
            {
                session.Tokens = new[] { false };
                Assert.Throws<ApplicationException>(() => session.TrySpend(out _, out _));
            }
            else Assert.Throws<ApplicationException>(() => session.Roll());
            Assert.That(session.IsCancelled, Is.True);
            Assert.That(hero.Fate, Is.EqualTo(2));
        }
    }
}
#endif
