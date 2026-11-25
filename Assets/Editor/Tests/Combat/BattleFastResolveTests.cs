#if UNITY_INCLUDE_TESTS
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.UI;
using Game.Units;
using NUnit.Framework;
#if UNITY_6000_3_OR_NEWER
using UnityEngine;
using UnityEngine.TestTools;
#endif

namespace Game.EditorTests
{
    public sealed class BattleFastResolveTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        [TestCase(BattleChallengeMode.GroundCombat)]
        [TestCase(BattleChallengeMode.CaptureKill)]
        public void FastHumanFateDuel_UsesTheExistingAllAiDecisionPath(BattleChallengeMode mode)
        {
            var human = new PlayerSetupData { IsHuman = true };
            var otherHuman = new PlayerSetupData { IsHuman = true };
            BattleChallengeSession fast = RunAutomaticDuel(mode, human, otherHuman, true);
            BattleChallengeSession allAi = RunAutomaticDuel(mode,
                new PlayerSetupData(), new PlayerSetupData(), false);

            Assert.That(fast.AttackerTokens, Is.EqualTo(allAi.AttackerTokens));
            Assert.That(fast.DefenderTokens, Is.EqualTo(allAi.DefenderTokens));
            Assert.That(fast.AttackerFateRemaining, Is.EqualTo(allAi.AttackerFateRemaining));
            Assert.That(fast.DefenderFateRemaining, Is.EqualTo(allAi.DefenderFateRemaining));
            Assert.That(fast.AttackerFateRemaining + fast.DefenderFateRemaining, Is.LessThan(5),
                "the test must exercise a real automatic Fate spend, not just two empty accepts");
            Assert.That(human.IsHuman && otherHuman.IsHuman, Is.True,
                "automatic control must never rewrite player ownership");
        }

        [Test]
        public void ClosingAutomaticChallenge_RestoresHumanDecisionRouting()
        {
            var popup = Shell<BattleAttackPopupUI>();
            var human = new PlayerSetupData { IsHuman = true };
            Set(popup, "_attacker", new UnitData { Owner = human });
            Set(popup, "_automateHumanSides", true);
            var automatic = (IEnumerator)Call(popup, "RunTurn", false);
            Assert.That(automatic.MoveNext(), Is.True);
            Assert.That(automatic.Current.GetType().Name, Does.Contain("RunAiTurn"));

            popup.Hide();
            var ordinary = (IEnumerator)Call(popup, "RunTurn", false);
            Assert.That(ordinary.MoveNext(), Is.True);
            Assert.That(ordinary.Current.GetType().Name, Does.Contain("RunHumanTurn"));
            Assert.That(human.IsHuman, Is.True);
        }

        [Test]
        public void FastHumanArmy_HasNoManualUnitActions_AndRetainsResultIdentity()
        {
            var screen = Shell<BattleScreenUI>();
            var owner = new PlayerSetupData { IsHuman = true };
            var army = new ArmyData { Owner = owner };
            var unit = new UnitData { Owner = owner };
            Set(screen, "_localArmy", army);
            Set(screen, "_fastResolve", true);

            Assert.That(Call(screen, "IsHumanAction", unit), Is.False);
            Assert.That(Call(screen, "IsAutomatedSide", army), Is.True);
            Assert.That(Get(screen, "_localArmy"), Is.SameAs(army),
                "the final outcome still needs the human army for Victory/Defeat and manual acknowledgement");

            Set(screen, "_fastResolve", false);
            Assert.That(Call(screen, "IsHumanAction", unit), Is.True);
            Assert.That(Call(screen, "IsAutomatedSide", army), Is.False);
        }

        [Test]
        public void ReadyWhileAiDeploymentIsRunning_DoesNotSaveLayoutOrStartRound()
        {
            var screen = Shell<BattleScreenUI>();
            var army = new ArmyData { Owner = new PlayerSetupData { IsHuman = true } };
            var unit = new UnitData { Owner = army.Owner };
            army.SavedArrangement[unit] = (4, 2);
            Set(screen, "_localArmy", army);
            Set(screen, "_arranging", true);
            Set(screen, "_arrangeInteractive", true);
            var pending = Shell<UnityEngine.Coroutine>();
            GC.SuppressFinalize(pending); // this managed sentinel owns no native coroutine
            Set(screen, "_aiArrangementRoutine", pending);
            // No live grid is installed: reaching the old save/start path would fail.
            Call(screen, "OnReadyClicked");
            Assert.That(army.SavedArrangement[unit], Is.EqualTo((4, 2)));
            Assert.That(Get(screen, "_arranging"), Is.True);
        }

        [Test]
        public void StaleDeploymentCoroutine_DoesNotEnterTheReplacementEncounter()
        {
            var screen = Shell<BattleScreenUI>();
            var oldEngine = Shell<BattleEngine>();
            var replacement = Shell<BattleEngine>();
            Set(screen, "_battleEngine", replacement);
            var attacker = new ArmyData { Owner = new PlayerSetupData() };
            Set(screen, "_battleState", new BattleState(default, attacker, null, new BattleGrid(), 1));
            var pending = Shell<UnityEngine.Coroutine>(); GC.SuppressFinalize(pending);
            Set(screen, "_aiArrangementRoutine", pending);
            var routine = (IEnumerator)Call(screen, "ArrangeAiAndContinue", oldEngine);
            Assert.That(routine.MoveNext(), Is.True, "initial UI frame yield");
            Assert.That(routine.MoveNext(), Is.False, "old work must stop before entering either army's search");
            Assert.That(Get(screen, "_battleEngine"), Is.SameAs(replacement));
            Assert.That(Get(screen, "_aiArrangementRoutine"), Is.SameAs(pending));
        }

        private static BattleChallengeSession RunAutomaticDuel(BattleChallengeMode mode,
            PlayerSetupData attackerOwner, PlayerSetupData defenderOwner, bool fastResolve)
        {
            var attacker = new UnitData { Owner = attackerOwner, Attack = 3, Range = 1,
                HitPointsCurrent = 5, HitPointsMax = 5 };
            var defender = new UnitData { Owner = defenderOwner, Defense = 2,
                HitPointsCurrent = 5, HitPointsMax = 5 };
            int roll = 0;
            var session = new BattleChallengeSession(mode, attacker, defender, 3, 2, 3, 2,
                AbilityMagnitudes.Default, rollTokens: count =>
                {
                    if (roll++ == 0) return new[] { true, true, false };
                    if (roll == 2) return new[] { true, false };
                    return new[] { true };
                });
            session.Roll();
            var popup = Shell<BattleAttackPopupUI>();
            Set(popup, "_automateHumanSides", fastResolve);
            Set(popup, "_attacker", attacker);
            Set(popup, "_defender", defender);
            Set(popup, "_attackerHero", new UnitData { Owner = attackerOwner, IsHero = true, Fate = 3 });
            Set(popup, "_defenderHero", new UnitData { Owner = defenderOwner, IsHero = true, Fate = 2 });
            Set(popup, "_challengeSession", session);
            Call(popup, "SyncChallengePresentationState");
            Drain((IEnumerator)Call(popup, "RunDuel"));
            return session;
        }

        private static void Drain(IEnumerator routine)
        {
            int steps = 0;
            while (routine.MoveNext())
            {
                Assert.That(++steps, Is.LessThan(100), "automatic Fate duel must terminate");
                Assert.That(routine.Current, Is.InstanceOf<IEnumerator>(),
                    "automatic challenges must not wait for input, animation or pacing");
                Drain((IEnumerator)routine.Current);
            }
        }

        // These tests exercise managed decision routing without needing a scene or invoking
        // MonoBehaviour lifecycle methods, as in AiAviationMissionRegressionTests.
        private static T Shell<T>() => (T)FormatterServices.GetUninitializedObject(typeof(T));
        private static void Set(object target, string name, object value) =>
            target.GetType().GetField(name, Private).SetValue(target, value);
        private static object Get(object target, string name) =>
            target.GetType().GetField(name, Private).GetValue(target);
        private static object Call(object target, string name, params object[] args) =>
            target.GetType().GetMethod(name, Private).Invoke(target, args);

    }
}
#endif
