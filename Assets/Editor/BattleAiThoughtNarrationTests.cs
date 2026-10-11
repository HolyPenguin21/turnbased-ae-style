#if UNITY_INCLUDE_TESTS
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Game.Combat;
using Game.Players;
using Game.UI;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class BattleAiThoughtNarrationTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

#if UNITY_6000_3_OR_NEWER
        [Test]
        public void NarrationDoesNotConsumeGameRandom()
        {
            UnityEngine.Random.State saved = UnityEngine.Random.state;
            try
            {
                UnityEngine.Random.InitState(917);
                float expected = UnityEngine.Random.value;
                UnityEngine.Random.InitState(917);
                foreach (AiThoughtCategory category in Enum.GetValues(typeof(AiThoughtCategory)))
                {
                    BattleAiPhraseBank.GetRandomPhrase(category, "Scout", true);
                    BattleAiPhraseBank.GetRandomPhrase(category, "Scout", false);
                }
                Assert.That(UnityEngine.Random.value, Is.EqualTo(expected));
            }
            finally { UnityEngine.Random.state = saved; }
        }
#endif

        [Test]
        public void EveryCategoryAndVoiceProducesCompleteFormattedText()
        {
            foreach (AiThoughtCategory category in Enum.GetValues(typeof(AiThoughtCategory)))
            foreach (bool hasHero in new[] { true, false })
            for (int sample = 0; sample < 20; sample++)
            {
                string phrase = BattleAiPhraseBank.GetRandomPhrase(category, "Scout", hasHero);
                Assert.That(phrase, Is.Not.Empty, category.ToString());
                Assert.That(phrase, Does.Not.Contain("{0}"), category.ToString());
            }
            Assert.That(BattleAiPhraseBank.GetRandomPhrase((AiThoughtCategory)(-1)), Is.Empty);
        }

        [TestCase(BattleChallengeMode.GroundCombat, 2, 1, AiThoughtCategory.GoodRoll)]
        [TestCase(BattleChallengeMode.GroundCombat, 1, 1, AiThoughtCategory.BadRoll)]
        [TestCase(BattleChallengeMode.CaptureKill, 2, 1, AiThoughtCategory.GoodRoll)]
        [TestCase(BattleChallengeMode.CaptureKill, 1, 1, AiThoughtCategory.GoodRoll)]
        [TestCase(BattleChallengeMode.CaptureKill, 1, 2, AiThoughtCategory.BadRoll)]
        public void RollNarrationMatchesTheChallengeAndDefendersPerspective(
            BattleChallengeMode mode, int attackerHits, int defenderHits, AiThoughtCategory expected)
        {
            var popup = Shell<BattleAttackPopupUI>();
            var attacker = new UnitData { Owner = new PlayerSetupData(), Name = "Attacker" };
            var defender = new UnitData { Owner = new PlayerSetupData(), Name = "Defender" };
            var session = Session(mode, attacker, defender, Hits(attackerHits), Hits(defenderHits), 0, 0);
            Set(popup, "_attacker", attacker);
            Set(popup, "_defender", defender);
            Set(popup, "_challengeSession", session);
            Type kind = typeof(BattleAttackPopupUI).GetNestedType("ChallengeKind", BindingFlags.NonPublic);
            Set(popup, "_kind", Enum.Parse(kind, mode.ToString()));
            var thoughts = new List<(UnitData actor, AiThoughtCategory reason, string target)>();
            Set(popup, "_onAiThought", new Action<UnitData, AiThoughtCategory, string>((actor, reason, target) => thoughts.Add((actor, reason, target))));
            Call(popup, "FireRollThought");
            Assert.That(thoughts.Count, Is.EqualTo(2));
            Assert.That(thoughts[0].actor, Is.SameAs(attacker));
            Assert.That(thoughts[0].reason, Is.EqualTo(expected));
            Assert.That(thoughts[0].target, Is.EqualTo("Defender"));
            Assert.That(thoughts[1].actor, Is.SameAs(defender));
            Assert.That(thoughts[1].reason, Is.EqualTo(expected == AiThoughtCategory.GoodRoll ? AiThoughtCategory.BadRoll : AiThoughtCategory.GoodRoll));
            Assert.That(thoughts[1].target, Is.EqualTo("Attacker"));
        }

        [TestCase(false, 2)]
        [TestCase(true, 0)]
        public void NoHeroOrNoAvailableFateNeverAnnouncesSavingFate(bool hasHero, int fate)
        {
            var (popup, thoughts, _) = FatePopup(hasHero, fate, new[] { true, false }, new[] { true }, 5);
            Drain((IEnumerator)Call(popup, "RunAiTurn", false));
            Assert.That(thoughts, Is.Empty);
        }

        [Test]
        public void ARealDeclineWithAvailableFateAnnouncesSavingIt()
        {
            var (popup, thoughts, session) = FatePopup(true, 2, new[] { true, true, false }, new[] { true }, 1);
            Drain((IEnumerator)Call(popup, "RunAiTurn", false));
            Assert.That(thoughts, Is.EqualTo(new[] { AiThoughtCategory.FateSpendSkip }));
            Assert.That(session.AttackerFateRemaining, Is.EqualTo(2));
        }

        [Test]
        public void ARealSpendIsAnnouncedOnceBeforeTheRerollAnimation()
        {
            var (popup, thoughts, session) = FatePopup(true, 2, new[] { true, false, false }, new[] { true }, 5);
            var defender = (UnitData)typeof(BattleAttackPopupUI).GetField("_defender", Private).GetValue(popup);
            defender.Owner.IsHuman = true; // This makes RunAiTurn wait for its reroll animation.
            var routine = (IEnumerator)Call(popup, "RunAiTurn", false);
            Assert.That(routine.MoveNext(), Is.True);
            Assert.That(thoughts, Is.EqualTo(new[] { AiThoughtCategory.FateSpendWorthIt }));
            Assert.That(session.AttackerFateRemaining, Is.EqualTo(1));
            Drain(routine);
            Assert.That(thoughts, Is.EqualTo(new[] { AiThoughtCategory.FateSpendWorthIt }));
            Assert.That(session.AttackerFateRemaining, Is.LessThan(2));
        }

        [Test]
        public void HeroLessNamedPhrasesHaveNoPersonalNarratorAndAllCategoriesHaveText()
        {
            var bank = typeof(BattleAiPhraseBank);
            var plain = (Dictionary<AiThoughtCategory, string[]>)bank.GetField("Phrases", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            var named = (Dictionary<AiThoughtCategory, string[]>)bank.GetField("NamedPhrases", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            foreach (AiThoughtCategory category in Enum.GetValues(typeof(AiThoughtCategory)))
                Assert.That(plain.ContainsKey(category) && plain[category].Length > 0, Is.True, category.ToString());
            foreach (var pool in named.Values)
                foreach (string phrase in pool)
                {
                    Assert.That(phrase.Contains("{0}"), Is.True);
                    Assert.That(phrase, Does.Not.Contain(" I ").And.Not.Contain(" my ").And.Not.Contain("We ").And.Not.Contain("we ").And.Not.Contain("our "));
                    Assert.That(string.Format(phrase, "Scout"), Does.Not.Contain("{0}"));
                }
        }

        private static (BattleAttackPopupUI popup, List<AiThoughtCategory> thoughts, BattleChallengeSession session) FatePopup(
            bool hasHero, int fate, bool[] attackerDice, bool[] defenderDice, int targetHp)
        {
            var attacker = new UnitData { Owner = new PlayerSetupData(), Name = "Attacker", Attack = attackerDice.Length };
            var defender = new UnitData { Owner = new PlayerSetupData(), Name = "Defender", HitPointsCurrent = targetHp, HitPointsMax = targetHp };
            var session = Session(BattleChallengeMode.GroundCombat, attacker, defender, attackerDice, defenderDice, fate, 0);
            var popup = Shell<BattleAttackPopupUI>();
            Set(popup, "_attacker", attacker);
            Set(popup, "_defender", defender);
            Set(popup, "_attackerHero", hasHero ? new UnitData { Name = "Commander", IsHero = true, Fate = fate } : null);
            Set(popup, "_challengeSession", session);
            Call(popup, "SyncChallengePresentationState");
            var thoughts = new List<AiThoughtCategory>();
            Set(popup, "_onAiThought", new Action<UnitData, AiThoughtCategory, string>((actor, reason, target) => thoughts.Add(reason)));
            return (popup, thoughts, session);
        }

        private static BattleChallengeSession Session(BattleChallengeMode mode, UnitData attacker, UnitData defender,
            bool[] attackerDice, bool[] defenderDice, int attackerFate, int defenderFate)
        {
            int roll = 0;
            var session = new BattleChallengeSession(mode, attacker, defender, attackerDice.Length, defenderDice.Length,
                attackerFate, defenderFate, AbilityMagnitudes.Default,
                rollDice: count => roll++ == 0 ? (bool[])attackerDice.Clone() : roll == 2 ? (bool[])defenderDice.Clone() : new[] { true });
            session.Roll();
            return session;
        }

        private static bool[] Hits(int count) { var result = new bool[count]; for (int i = 0; i < count; i++) result[i] = true; return result; }
        private static void Drain(IEnumerator routine) { int steps = 0; while (routine.MoveNext()) { Assert.That(++steps, Is.LessThan(50)); if (routine.Current is IEnumerator nested) Drain(nested); } }
        private static T Shell<T>() => (T)FormatterServices.GetUninitializedObject(typeof(T));
        private static void Set(object target, string name, object value) => target.GetType().GetField(name, Private).SetValue(target, value);
        private static object Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Private).Invoke(target, args);
    }
}
#endif
