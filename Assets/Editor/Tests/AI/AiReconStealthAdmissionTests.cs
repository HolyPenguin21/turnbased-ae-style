#if UNITY_INCLUDE_TESTS
using System;
using System.Collections;
using System.Reflection;
using Game.Ai;
using Game.Ai.V2;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.UI;
using Game.Units;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    public sealed class AiReconStealthAdmissionTests
    {
        private PlayerSetupData _player;
        private PlayerRoot _root;
        private GameObject _mapObject;
        private GameObject _selectorObject;
        private GameObject _popupObject;
        private GameObject _panelObject;
        private ArmyData _army;
        private UnitData _scout;

        [SetUp]
        public void SetUp()
        {
            ArmyRegistry.Clear();
            PlayerRootRegistry.Clear();
            ReconPatrolStateRegistry.ClearAll();

            _player = new PlayerSetupData();
            _root = PlayerRoot.Create(_player, "recon admission root");
            _root.ActionPoints = 10;
            PlayerRootRegistry.Register(_player, _root);
            _mapObject = new GameObject("recon admission map");
            _mapObject.AddComponent<HexMap>();
            _scout = new UnitData { Owner = _player, MoveMax = 3, MoveCurrent = 3 };
            _scout.Abilities.Add("r1s0");
            _scout.Abilities.Add("Stealth4");
            _army = new ArmyData { Owner = _player, Hex = new HexCoord(76, -12) };
            _army.Members.Add(_scout);
            ArmyRegistry.Register(_army);
            Assert.That(StealthSystem.CanEnterStealth(_scout), Is.True,
                "The witness must be able to spend AP on stealth in the old ordering.");
        }

        [TearDown]
        public void TearDown()
        {
            ArmyRegistry.Clear();
            ReconPatrolStateRegistry.ClearAll();
            PlayerRootRegistry.Clear();
            if (_panelObject != null) UnityEngine.Object.DestroyImmediate(_panelObject);
            if (_popupObject != null) UnityEngine.Object.DestroyImmediate(_popupObject);
            if (_selectorObject != null) UnityEngine.Object.DestroyImmediate(_selectorObject);
            if (_mapObject != null) UnityEngine.Object.DestroyImmediate(_mapObject);
            if (_root != null) UnityEngine.Object.DestroyImmediate(_root.gameObject);
        }

        [Test]
        public void ReconfiguredNonSoloScoutCannotSpendReservedStealthOrCreatePatrol()
        {
            _army.Members.Add(new UnitData { Owner = _player });
            Assert.That(AiArmyRoles.IsSoloRecce(_army), Is.False);

            ExecutionResult result = RunRejectedAdmission(null);

            Assert.That(result.StopReason, Is.EqualTo(ExecutionStopReason.MoverLost));
            Assert.That(result.ApSpent, Is.Zero);
            Assert.That(_root.ActionPoints, Is.EqualTo(10));
            Assert.That(_scout.IsHidden, Is.False);
            Assert.That(ReconPatrolStateRegistry.TryGet(_player, _army.Id, out _), Is.False);
        }

        [Test]
        public void ActiveBattleCannotSpendReservedStealthOrCreatePatrol()
        {
            Assert.That(AiArmyRoles.IsSoloRecce(_army), Is.True);
            _selectorObject = new GameObject("inactive recon selector");
            _selectorObject.SetActive(false);
            HexSelectionController selector = _selectorObject.AddComponent<HexSelectionController>();
            _popupObject = new GameObject("inactive battle popup");
            _popupObject.SetActive(false);
            BattleContactPopupUI popup = _popupObject.AddComponent<BattleContactPopupUI>();
            _panelObject = new GameObject("visible battle panel");
            typeof(BattleContactPopupUI).GetField("panelRoot",
                BindingFlags.NonPublic | BindingFlags.Instance).SetValue(popup, _panelObject);
            typeof(HexSelectionController).GetField("battleContactPopup",
                BindingFlags.NonPublic | BindingFlags.Instance).SetValue(selector, popup);
            Assert.That(selector.IsBattleActive, Is.True, "The test must actually represent a combat lock.");

            ExecutionResult result = RunRejectedAdmission(selector);

            Assert.That(result.StopReason, Is.EqualTo(ExecutionStopReason.BattleStarted));
            Assert.That(result.BlockedBeforeMovement, Is.True);
            Assert.That(result.ApSpent, Is.Zero);
            Assert.That(_root.ActionPoints, Is.EqualTo(10));
            Assert.That(_scout.IsHidden, Is.False);
            Assert.That(ReconPatrolStateRegistry.TryGet(_player, _army.Id, out _), Is.False);
        }

        private ExecutionResult RunRejectedAdmission(HexSelectionController selector)
        {
            var mission = new ProvisionedMission
            {
                MoverArmyId = _army.Id,
                ScoutKind = ScoutTargetKind.Explore,
                FocusHex = new HexCoord(77, -12),
                StealthApReserved = true,
            };
            var result = new ExecutionResult();
            var context = new AiTurnContext
            {
                Map = _mapObject.GetComponent<HexMap>(),
                HexSelection = selector,
                TurnNumber = 3,
            };
            Type executor = typeof(ExecutionResult).Assembly.GetType("Game.Ai.V2.ReconGroundExecutor", true);
            MethodInfo runStep = executor.GetMethod("RunStep", BindingFlags.Public | BindingFlags.Static);
            Assert.That(runStep, Is.Not.Null);
            var routine = (IEnumerator)runStep.Invoke(null, new object[]
            {
                _player, _root, context, mission, result, 10,
                new[] { mission }, 0, null, null,
            });
            Assert.That(routine.MoveNext(), Is.False,
                "Rejected admission must terminate before scheduling the first movement coroutine.");
            (routine as IDisposable)?.Dispose();
            return result;
        }
    }
}
#endif
