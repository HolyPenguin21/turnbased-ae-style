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

#if UNITY_6000_3_OR_NEWER
        [UnityTest]
        public IEnumerator DeploymentCompletion_DoesNotDestroyInteractiveArrangementCells()
        {
            var screenObject = new GameObject("Deployment completion test screen");
            var popupObject = new GameObject("Deployment completion test popup");
            var cellObject = new GameObject("Player cell being arranged");
            try
            {
                var screen = screenObject.AddComponent<BattleScreenUI>();
                var popup = popupObject.AddComponent<BattleArrangePopupUI>();
                Set(screen, "panelRoot", screenObject); Set(screen, "arrangePopup", popup);
                Set(popup, "panelRoot", popupObject); popupObject.SetActive(false);
                // Two human sides avoid scheduling an actual AI search; exercise its UI
                // completion against a player who is already arranging, with real cells.
                screen.Show(new HexCoord(0, 0), new List<ArmyData>
                {
                    TestArmy(new PlayerSetupData { IsHuman = true }, "A", 3),
                    TestArmy(new PlayerSetupData { IsHuman = true }, "B", 3),
                }, null);
                Call(screen, "BeginArrangement");
                var cell = cellObject.AddComponent<BattleGridCellUI>();
                var cells = (List<BattleGridCellUI>)Get(screen, "_cells"); cells.Add(cell);
                yield return (IEnumerator)Call(screen, "ArrangeAiAndContinue", Get(screen, "_battleEngine"));
                Assert.That(cellObject.activeSelf, Is.True);
                Assert.That(cells.Contains(cell), Is.True, "finishing AI deployment must preserve a human's drag object");
                screen.Hide();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(screenObject);
                UnityEngine.Object.DestroyImmediate(popupObject);
                if (cellObject != null) UnityEngine.Object.DestroyImmediate(cellObject);
            }
        }

        [UnityTest]
        public IEnumerator FastBattle_WaitsForPlayerOnFinalOutcome_ThenNormalBattleOffersArrangement()
        {
            ArmyRegistry.Clear();
            BuildingRegistry.Clear();
            DelayedBattleRegistry.Clear();
            var screenObject = new GameObject("Fast resolve test screen");
            var attackObject = new GameObject("Fast resolve test attack");
            var outcomeObject = new GameObject("Fast resolve test outcome");
            var arrangeObject = new GameObject("Fast resolve test arrangement");
            try
            {
                var screen = screenObject.AddComponent<BattleScreenUI>();
                var attack = attackObject.AddComponent<BattleAttackPopupUI>();
                var outcome = outcomeObject.AddComponent<BattleOutcomePopupUI>();
                var arrangement = arrangeObject.AddComponent<BattleArrangePopupUI>();
                Set(screen, "panelRoot", screenObject);
                Set(screen, "attackPopup", attack);
                Set(screen, "outcomePopup", outcome);
                Set(screen, "arrangePopup", arrangement);
                Set(attack, "panelRoot", attackObject);
                Set(outcome, "panelRoot", outcomeObject);
                Set(arrangement, "panelRoot", arrangeObject);
                attackObject.SetActive(false);
                outcomeObject.SetActive(false);
                arrangeObject.SetActive(false);
                var human = new PlayerSetupData { IsHuman = true };
                var opponent = new PlayerSetupData();
                ArmyData attacker = TestArmy(human, "Human", 100);
                ArmyData defender = TestArmy(opponent, "Opponent", 1);
                int closed = 0;
                screen.Show(new HexCoord(0, 0), new List<ArmyData> { attacker, defender },
                    () => closed++, fastResolve: true);
                for (int frame = 0; frame < 200 && !outcome.IsShowing; frame++)
                    yield return null;
                Assert.That(outcome.IsShowing, Is.True, "Fast Resolve must reach the real result panel");
                for (int frame = 0; frame < 3; frame++) yield return null;
                Assert.That(outcome.IsShowing, Is.True, "human outcome must not use the all-AI auto-close");
                Assert.That(closed, Is.Zero);
                Assert.That(screen.IsShowing, Is.True, "strategic input stays blocked before confirmation");
                Call(outcome, "OnOkClicked");
                Assert.That(closed, Is.EqualTo(1));
                Assert.That(human.IsHuman, Is.True);

                screen.Show(new HexCoord(0, 0), new List<ArmyData>
                    { TestArmy(human, "Next human", 100), TestArmy(opponent, "Next opponent", 1) }, null);
                Assert.That(arrangement.IsShowing, Is.True, "Fast Resolve must not leak into the next ordinary battle");
                Call(screen, "BeginArrangement");
                Call(screen, "OnReadyClicked");
                Assert.That(((BattleEngine)Get(screen, "_battleEngine")).Phase, Is.EqualTo(BattleCommandPhase.Deployment),
                    "Ready must wait for the coroutine's complete AI layout");
                screen.Hide();
                for (int frame = 0; frame < 3; frame++) yield return null;
                Assert.That(Get(screen, "_aiArrangementRoutine"), Is.Null);
                Assert.That(Get(screen, "_battleEngine"), Is.Null, "a cancelled deployment must not reopen the battle");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(screenObject);
                UnityEngine.Object.DestroyImmediate(attackObject);
                UnityEngine.Object.DestroyImmediate(outcomeObject);
                UnityEngine.Object.DestroyImmediate(arrangeObject);
                ArmyRegistry.Clear();
                BuildingRegistry.Clear();
                DelayedBattleRegistry.Clear();
            }
        }

        private static ArmyData TestArmy(PlayerSetupData owner, string name, int attack)
        {
            var army = new ArmyData { Owner = owner, Name = name };
            army.Members.Add(new UnitData { Owner = owner, Name = name, Attack = attack, Range = 10,
                Initiative = attack, HitPointsCurrent = 1, HitPointsMax = 1 });
            return army;
        }
#endif
    }
}
#endif
