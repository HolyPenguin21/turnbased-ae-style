#if UNITY_INCLUDE_TESTS && UNITY_6000_3_OR_NEWER
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Ai;
using Game.Ai.V2;
using Game.Aviation;
using Game.Core;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Terrain;
using Game.Units;
using Game.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Game.EditorTests
{
    // These Editor-hosted tests explicitly enter PlayMode: Destroy, nested coroutines and
    // WaitUntil must run in the engine. The old ai-verify reference DLLs cannot run this fixture.
    public sealed class ArmyMovementLifecyclePlayModeTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();
        private HexMap _map;
        private HexSelectionController _selection;
        private PlayerSetupData _owner;
        private PlayerRoot _root;
        private readonly HexCoord _origin = new HexCoord(0, 0);
        private readonly HexCoord _first = new HexCoord(1, 0);
        private readonly HexCoord _last = new HexCoord(2, 0);

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            yield return new EnterPlayMode();
            ArmyRegistry.Clear();
            BuildingRegistry.Clear();
            VisionSystem.Clear();
            VisionSystem.Configure(null);
            AiMapMemory.Clear();
            AiMapMemory.EnsureSubscribed();
            AirSortieRegistry.Clear();
            ReconAirSortieRegistry.ClearAll();
            ReconPatrolStateRegistry.ClearAll();
            PlayerRootRegistry.Clear();
            GameSession.Players = new List<PlayerSetupData>();
            _owner = new PlayerSetupData { Nickname = "movement-test" };
            _root = PlayerRoot.Create(_owner, "movement-test-root");
            _objects.Add(_root.gameObject);
            _root.ActionPoints = 20;
            _root.AddResource(ResourceType.Energy, 20);
            PlayerRootRegistry.Register(_owner, _root);
            _map = NewObject("map").AddComponent<HexMap>();
            var terrain = new Dictionary<HexCoord, TerrainTypeEntry>();
            foreach (HexCoord hex in HexGridMath.HexesInRange(_origin, 3))
                terrain[hex] = new TerrainTypeEntry { terrainName = "plain", moveCost = 1 };
            _map.SetData(3, 1, terrain);
            _selection = NewObject("selection").AddComponent<HexSelectionController>();
            _selection.enabled = false;
            Field(_selection, "map", _map);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            foreach (GameObject go in _objects)
                if (go != null) Object.Destroy(go);
            _objects.Clear();
            yield return null;
            ArmyRegistry.Clear();
            BuildingRegistry.Clear();
            VisionSystem.Clear();
            AiMapMemory.Clear();
            PlayerRootRegistry.Clear();
            AirSortieRegistry.Clear();
            ReconAirSortieRegistry.ClearAll();
            ReconPatrolStateRegistry.ClearAll();
            StrategicResourceReservationLedger.ClearAll();
            yield return new ExitPlayMode();
        }

        private GameObject NewObject(string name)
        {
            var go = new GameObject(name);
            _objects.Add(go);
            return go;
        }

        private static void Field(object obj, string name, object value) =>
            obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(obj, value);

        private ArmyData Army(bool air = true, int count = 1)
        {
            var army = new ArmyData { Owner = _owner, Name = "mover", Hex = _origin, IsAirArmy = air };
            for (int i = 0; i < count; i++)
                army.Members.Add(new UnitData { Owner = _owner, Name = "unit", IsAviation = air,
                    MoveMax = 5, MoveCurrent = 5, ActivationApCost = 1, LaunchEnergyCost = air ? 2 : 0,
                    HitPointsCurrent = 5, HitPointsMax = 5 });
            var controller = NewObject("mover").AddComponent<ArmyController>();
            controller.SetData(army);
            army.Controller = controller;
            Field(controller, "stepDuration", 0f);
            ArmyRegistry.Register(army);
            return army;
        }

        private ArmyController LayoutMarker()
        {
            var go = NewObject("layout marker");
            var renderer = go.AddComponent<SpriteRenderer>();
            var visual = go.AddComponent<MapObjectVisual>();
            Field(visual, "innerCircle", renderer);
            var controller = go.AddComponent<ArmyController>();
            controller.SetData(new ArmyData { Owner = _owner });
            VisionSystem.CurrentViewer = _owner;
            visual.SetVisible(true);
            controller.SetLayoutPosition(Vector3.zero, false);
            return controller;
        }

        [UnityTest]
        public IEnumerator RepeatedReconciliationDoesNotRestartLayoutTransition()
        {
            var marker = LayoutMarker();
            marker.SetLayoutPosition(Vector3.right, true);
            Assert.That(marker.transform.position, Is.EqualTo(Vector3.zero), "no instant jump");
            float previous = 0f;
            for (int frame = 0; frame < 180 && marker.transform.position.x != 1f; frame++)
            {
                yield return null;
                Assert.That(marker.transform.position.x, Is.GreaterThanOrEqualTo(previous));
                Assert.That(marker.transform.position.x, Is.LessThanOrEqualTo(1f));
                previous = marker.transform.position.x;
                marker.SetLayoutPosition(Vector3.right, true);
            }
            Assert.That(marker.transform.position, Is.EqualTo(Vector3.right));
            Assert.That(marker.IsMoving, Is.False, "layout never becomes a gameplay move");
        }

        [UnityTest]
        public IEnumerator LayoutRetargetContinuesFromTheActualPosition()
        {
            var marker = LayoutMarker();
            marker.SetLayoutPosition(Vector3.right, true);
            yield return null;
            Vector3 before = marker.transform.position;
            marker.SetLayoutPosition(Vector3.left, true);
            Assert.That(marker.transform.position, Is.EqualTo(before));
            for (int frame = 0; frame < 180 && marker.transform.position.x != -1f; frame++)
                yield return null;
            Assert.That(marker.transform.position, Is.EqualTo(Vector3.left));
        }

        [UnityTest]
        public IEnumerator ViewerSwitchAndRevealDoNotAnimateFromAnotherPerspective()
        {
            var marker = LayoutMarker();
            marker.SetLayoutPosition(Vector3.right, true);
            yield return null;
            VisionSystem.CurrentViewer = new PlayerSetupData { IsHuman = true };
            marker.SetLayoutPosition(Vector3.left, true);
            Assert.That(marker.transform.position, Is.EqualTo(Vector3.left));
            marker.Visual.SetVisible(false);
            marker.SetLayoutPosition(Vector3.zero, false);
            Assert.That(marker.Visual.IsVisible, Is.False);
            yield return null;
            Assert.That(marker.transform.position, Is.EqualTo(Vector3.zero));
            marker.Visual.SetVisible(true);
            Assert.That(marker.transform.position, Is.EqualTo(Vector3.zero));
        }

        private AiTurnContext Context() => new AiTurnContext
            { Map = _map, HexSelection = _selection, StepDelay = 0f, TurnNumber = 1 };

        private static IEnumerator Settled(ArmyData army)
        {
            // A test bound, never a gameplay timeout: failure retains the hung-order diagnosis.
            for (int frame = 0; frame < 120 && army.Controller != null && army.Controller.IsMoving; frame++)
                yield return null;
            Assert.That(army.Controller == null || !army.Controller.IsMoving, Is.True);
        }

        private IEnumerator FatalStep(ArmyData army, HexCoord at, ArmyController.StepResolutionOutcome outcome)
        {
            yield return null; // the AI WaitUntil is already polling while the resolver is suspended
            army.Members.Clear();
            outcome.StopMovement = true;
            _selection.DeleteArmyIfEmptied(army);
        }

        private IEnumerator DestructionAt(HexCoord deathHex)
        {
            ArmyData army = Army();
            ArmyController controller = army.Controller;
            int arrivals = 0;
            controller.SettleThen(() => controller.MoveAlong(_map,
                new List<HexCoord> { _origin, _first, _last }, _ => Vector3.zero,
                onComplete: () => { arrivals++; ArmyRegistry.MoveArmy(army, controller.CurrentHex); },
                resolveStepAsync: (_, to, outcome) => to.Equals(deathHex)
                    ? FatalStep(army, to, outcome) : NoReaction()));
            var waiting = new WaitUntil(() => army.Controller == null || !army.Controller.IsMoving);
            yield return Settled(army);
            Assert.That(waiting.keepWaiting, Is.False);
            Assert.That(army.Hex, Is.EqualTo(deathHex));
            Assert.That(army.Controller, Is.Null);
            Assert.That(controller.IsMoving, Is.False); // managed terminal flag survives native destruction
            Assert.That(arrivals, Is.Zero);
            ArmyRegistry.MoveArmy(army, _last); // simulate a late completion callback
            Assert.That(AiV2Util.ResolveArmy(_owner, army.Id), Is.Null);
            Assert.That(ArmyRegistry.AllAt(_origin), Has.None.EqualTo(army));
            Assert.That(ArmyRegistry.AllAt(deathHex), Has.None.EqualTo(army));
            yield return null;
            Assert.That(controller == null, Is.True, "Unity destroyed-object semantics must be exercised");
        }

        private static IEnumerator NoReaction() { yield break; }

        [UnityTest] public IEnumerator DestructionDuringIntermediateResolution() => DestructionAt(_first);
        [UnityTest] public IEnumerator DestructionDuringFinalResolution() => DestructionAt(_last);

        [UnityTest]
        public IEnumerator EmptyAirStepUsesPresenterDeletionPath()
        {
            ArmyData army = Army();
            ArmyController controller = army.Controller;
            var presenter = NewObject("presenter").AddComponent<AviationCombatPresenter>();
            Field(presenter, "hexSelection", _selection);
            IEnumerator Resolve(HexCoord from, HexCoord to, ArmyController.StepResolutionOutcome outcome)
            {
                yield return null;
                // Inject the post-AA roster, then run the real presenter's terminal deletion branch.
                // Dice/popup interaction is deliberately outside this lifecycle fixture.
                army.Members.Clear();
                yield return (IEnumerator)typeof(AviationCombatPresenter)
                    .GetMethod("ResolveAirArmyStep", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(presenter, new object[] { army, to, outcome });
            }
            controller.SettleThen(() => controller.MoveAlong(_map, new List<HexCoord> { _origin, _first },
                _ => Vector3.zero, resolveStepAsync: Resolve));
            yield return Settled(army);
            Assert.That(army.Hex, Is.EqualTo(_first));
            Assert.That(ArmyRegistry.IsRegistered(army), Is.False);
        }

        [UnityTest]
        public IEnumerator PartialLossContinuesAndCompletesOnce()
        {
            ArmyData army = Army(count: 2);
            ArmyController controller = army.Controller;
            int arrivals = 0;
            IEnumerator Resolve(HexCoord from, HexCoord to, ArmyController.StepResolutionOutcome outcome)
            {
                yield return null;
                if (to.Equals(_first)) army.Members.RemoveAt(0);
            }
            controller.SettleThen(() => controller.MoveAlong(_map,
                new List<HexCoord> { _origin, _first, _last }, _ => Vector3.zero,
                onComplete: () => { arrivals++; ArmyRegistry.MoveArmy(army, controller.CurrentHex); },
                resolveStepAsync: Resolve));
            yield return Settled(army);
            Assert.That(army.Hex, Is.EqualTo(_last));
            Assert.That(army.Members.Count, Is.EqualTo(1));
            Assert.That(arrivals, Is.EqualTo(1));
            Assert.That(_root.ActionPoints, Is.EqualTo(20));
        }

        private (WorldSnapshot snapshot, MissionProposal proposal) DefenceReturnFixture(ArmyData army,
            ActiveDefenceReturnPurpose purpose)
        {
            var enemy = new PlayerSetupData { Nickname = "known-threat" };
            var enemyBody = new Game.Combat.WorthIt.DefenderProfile(8, false, null, 8, 18, 3);
            var contact = new EnemyContactSnapshot { Position = new HexCoord(-3, 0),
                Knowledge = ContactKnowledge.Exact, Confidence = 1f, LastObservedTurn = 1,
                Army = new ArmySnapshot { ArmyId = 999, Owner = enemy, MemberCount = 1,
                    EffectiveArmyPower = 34f, Members = new[] { enemyBody } } };
            army.Members[0].Attack = 9;
            army.Members[0].Defense = 7;
            army.Members[0].HitPointsCurrent = army.Members[0].HitPointsMax = 12;
            army.Members[0].Initiative = 3;
            var actor = new ArmySnapshot { ArmyId = army.Id, Owner = _owner, Hex = army.Hex,
                IsStructuralRaidActor = true, MemberCount = 1, ActivationApCost = army.ActivationApCost,
                HasActivatedThisTurn = army.HasActivatedThisTurn, EffectiveArmyPower = 28f,
                CurrentMovement = army.CurrentMovement, MaxMovement = army.MaxMovement,
                ReachableOwnBaseHexes = new[] { _last },
                Members = new[] { new Game.Combat.WorthIt.DefenderProfile(7, false, null, 9, 12, 3) } };
            var snap = new WorldSnapshot { Observer = _owner, TurnNumber = 1, Map = _map,
                Self = new SelfSnapshot { ActionPoints = _root.ActionPoints, BaseHexes = new[] { _last },
                    Citadel = _last, Armies = new[] { actor, new ArmySnapshot { ArmyId = -20,
                        Owner = _owner, Hex = _last, IsGarrison = true, MemberCount = 1,
                        Members = actor.Members, EffectiveArmyPower = 28f } } },
                Known = new KnownSnapshot { EnemySightings = new[] { new AiMapMemory.KnownEnemySighting(
                    contact.Position.Value, enemy, "known", 1, 8, 8, new[] { enemyBody }, false, 0, 0, 1, 999) } },
                Threat = new ThreatModel { Contacts = new[] { contact }, Threats = new[] {
                    new AssetThreatSnapshot { Asset = new StrategicAssetSnapshot { Kind = AssetKind.Base,
                        Hex = _last, Value = 10f }, Contact = contact, CanDamage = true, EnemyEta = 1,
                        PotentialDamage = 1f, Confidence = 1f, Severity = 1f, AttackWinChance = 1f } } } };
            var target = ActiveDefenceObjectiveEvaluator.Enumerate(snap).Single().Target;
            target.Phase = ActiveDefencePhase.Return;
            target.ReturnPurpose = purpose;
            target.PrimaryArmyId = army.Id;
            target.ReturnHex = _last;
            var proposal = new MissionProposal { Kind = MissionKind.ActiveDefence, Target = target,
                PreferredMoverArmyId = army.Id, BaseValue = 10f, LocalAdmissionScore = 10f,
                Requirements = GroundCombatLegs.PinnedLegRequirements(actor, _last, out _) };
            proposal.Axes.Value[DesireAxis.Aggression] = 1f;
            return (snap, proposal);
        }

        private IEnumerator DefenceReturnBankCase(bool alreadyActivated, ActiveDefenceReturnPurpose purpose)
        {
            _root.ActionPoints = 5;
            ArmyData army = Army(air: false);
            if (alreadyActivated) army.MarkActivated();
            var fixture = DefenceReturnFixture(army, purpose);
            using var turn = AiTurnSession.Begin(_owner, _root, null, Context());
            var radar = Radar.Even();
            foreach (DesireAxis axis in DesireAxes.All) radar.Weight[axis] = axis == DesireAxis.Aggression ? 1f : 0f;
            var allocator = ResourceAllocator.BeginTurn(fixture.snapshot, radar,
                new List<MissionProposal> { fixture.proposal }, new List<Commitment>(), _owner);
            FundedEntry funded = allocator.Pack().Funded.Single();
            using var session = new ProvisioningSession(fixture.snapshot, turn);
            ProvisioningResult provision = ActiveDefenceProvisioner.Provision(_owner, _root, Context(), session, funded);
            Assert.That(provision.Success, Is.True);
            allocator.RegisterProvisionSuccess(funded, provision.Provisioned.ClaimedAp, provision.Provisioned.ClaimedPhysical);
            var execution = new ExecutionResult();
            yield return ActiveDefenceExecutor.RunActiveDefence(_owner, _root, Context(), provision.Provisioned, execution, 5);
            int spent = alreadyActivated ? 0 : army.ActivationApCost;
            Assert.That(execution.StepsMoved, Is.EqualTo(2));
            Assert.That(execution.ReachedGoal, Is.True);
            Assert.That(execution.ApSpent, Is.EqualTo(spent));
            Assert.That(_root.ActionPoints, Is.EqualTo(5 - spent));
            Assert.That(_root.GetResource(ResourceType.Energy), Is.EqualTo(20));
            Assert.That(provision.Provisioned.ClaimedPhysical.AnyPhysical, Is.False);
            TestContext.WriteLine($"ActiveDefence bank purpose={purpose} activated={alreadyActivated} "
                + $"AP-before=5 funded={funded.Tentative.Ap} actual={execution.ApSpent} AP-after={_root.ActionPoints} claims-settlement=checked-separately");
            StrategicResourceReservationLedger.AssertClearAtTurnEnd(_owner, 1);
            Assert.That(ReservationInvariants.Violations(_owner, 1), Is.Empty);
        }

        [UnityTest]
        public IEnumerator ActiveDefenceRegroup_AllocatorProvisionAndTwoHexExecution_ChargesOnce() =>
            DefenceReturnBankCase(false, ActiveDefenceReturnPurpose.RegroupForAsset);

        [UnityTest]
        public IEnumerator ActiveDefenceRegroup_ActivatedArmyDoesNotPayAgain() =>
            DefenceReturnBankCase(true, ActiveDefenceReturnPurpose.RegroupForAsset);

        [UnityTest]
        public IEnumerator ActiveDefenceWithdrawal_UsesTheSameApOnlyMovementContract() =>
            DefenceReturnBankCase(false, ActiveDefenceReturnPurpose.SafeWithdrawal);

        [UnityTest]
        public IEnumerator ActiveDefenceRegroup_StaleSmallEnvelopeCannotMoveForFree()
        {
            ArmyData army = Army(air: false);
            var fixture = DefenceReturnFixture(army, ActiveDefenceReturnPurpose.RegroupForAsset);
            using var session = new ProvisioningSession(fixture.snapshot);
            var result = ActiveDefenceProvisioner.Provision(_owner, _root, Context(), session,
                new FundedEntry { Mission = fixture.proposal, Tentative = ResourceVector.Zero });
            Assert.That(result.Success, Is.False);
            Assert.That(result.Failure.Kind, Is.EqualTo(ProvisionFailureKind.EnvelopeTooSmall));
            Assert.That(army.Hex, Is.EqualTo(_origin));
            Assert.That(_root.ActionPoints, Is.EqualTo(20));
            yield return null;
        }

        [UnityTest]
        public IEnumerator OrdinaryGroundOrderChargesActivationOnlyOnce()
        {
            ArmyData army = Army(air: false);
            yield return AiTurnController.MoveArmyRoutine(_owner, AiDecision.Move(army, _first, "test", 0f), Context());
            yield return AiTurnController.MoveArmyRoutine(_owner, AiDecision.Move(army, _last, "test", 0f), Context());
            Assert.That(army.Hex, Is.EqualTo(_last));
            Assert.That(_root.ActionPoints, Is.EqualTo(19));
            Assert.That(_root.GetResource(ResourceType.Energy), Is.EqualTo(20));
        }

        [UnityTest]
        public IEnumerator LiveRouteInterruptionKeepsPartialProgress()
        {
            ArmyData army = Army(air: false);
            ArmyController controller = army.Controller;
            int arrivals = 0;
            controller.SettleThen(() => controller.MoveAlong(_map,
                new List<HexCoord> { _origin, _first, _last }, _ => Vector3.zero,
                () => { arrivals++; ArmyRegistry.MoveArmy(army, controller.CurrentHex); },
                shouldStopEarly: _ => true));
            yield return Settled(army);
            Assert.That(army.Hex, Is.EqualTo(_first));
            Assert.That(arrivals, Is.EqualTo(1));
            Assert.That(ArmyRegistry.IsRegistered(army), Is.True);
        }

        [UnityTest]
        public IEnumerator GroundLossDuringArrivalDoesNotResurrectMover()
        {
            ArmyData army = Army(air: false);
            ArmyController controller = army.Controller;
            int arrivals = 0;
            controller.SettleThen(() => controller.MoveAlong(_map,
                new List<HexCoord> { _origin, _first }, _ => Vector3.zero,
                () =>
                {
                    arrivals++;
                    ArmyRegistry.MoveArmy(army, controller.CurrentHex);
                    army.Members.Clear();
                    _selection.DeleteArmyIfEmptied(army);
                }));
            yield return Settled(army);
            _selection.DeleteArmyIfEmptied(army);
            ArmyRegistry.MoveArmy(army, _last);
            Assert.That(arrivals, Is.EqualTo(1));
            Assert.That(army.Hex, Is.EqualTo(_first));
            Assert.That(AiV2Util.ResolveArmy(_owner, army.Id), Is.Null);
        }

        [UnityTest]
        public IEnumerator DisableCancelsBeforeFirstStepWithoutArrivalOrSpend()
        {
            ArmyData army = Army();
            ArmyController controller = army.Controller;
            int started = 0;
            int cancelled = 0;
            controller.SetSelected(true);
            Field(controller, "settleDuration", 10f);
            controller.SettleThen(() => started++, _ => cancelled++);
            army.PendingAirStrikePolicy = AirStrikePolicy.Transit;
            controller.gameObject.SetActive(false);
            controller.CancelMovement();
            yield return null;
            Assert.That(controller.IsMoving, Is.False);
            Assert.That(army.PendingAirStrikePolicy, Is.Null);
            Assert.That(started, Is.Zero);
            Assert.That(cancelled, Is.EqualTo(1));
            Assert.That(_root.ActionPoints, Is.EqualTo(20));
        }



        [UnityTest]
        public IEnumerator FreshOwnSnapshotExcludesRemovedArmy()
        {
            ArmyData army = Army();
            MethodInfo build = typeof(WorldAnalysis).GetMethod("BuildSelf", BindingFlags.Static | BindingFlags.NonPublic);
            SelfSnapshot before = (SelfSnapshot)build.Invoke(null, new object[] { _owner, _root, null, Context() });
            Assert.That(before.Armies.Any(a => a.ArmyId == army.Id), Is.True);
            army.Members.Clear();
            _selection.DeleteArmyIfEmptied(army);
            SelfSnapshot after = (SelfSnapshot)build.Invoke(null, new object[] { _owner, _root, null, Context() });
            Assert.That(after.Armies.Any(a => a.ArmyId == army.Id), Is.False);
            yield return null;
        }

        [UnityTest]
        public IEnumerator LiveAiOrderStillWaitsForOpenBattle()
        {
            ArmyData army = Army(air: false);
            Field(army.Controller, "stepDuration", 0.05f);
            var screen = NewObject("battle").AddComponent<BattleScreenUI>();
            screen.enabled = false;
            GameObject panel = NewObject("panel");
            panel.SetActive(false);
            Field(screen, "panelRoot", panel);
            Field(_selection, "battleScreen", screen);
            bool done = false;
            var trace = new AiMoveExecutionTrace();
            IEnumerator OpenBattle()
            {
                yield return new WaitUntil(() => army.Controller != null && army.Controller.IsMoving
                    && army.Controller.CurrentHex.Equals(_first));
                panel.SetActive(true);
            }
            IEnumerator Move()
            {
                yield return AiTurnController.MoveArmyRoutine(_owner,
                    AiDecision.Move(army, _first, "test", 0f), Context(), trace);
                done = true;
            }
            _map.StartCoroutine(OpenBattle());
            _map.StartCoroutine(Move());
            // Wait for the physical completion while leaving the battle open.
            for (int frame = 0; frame < 120 && !trace.EndHex.Equals(_first); frame++)
                yield return null;
            Assert.That(trace.EndHex, Is.EqualTo(_first));
            Assert.That(trace.BattleOccurred, Is.True);
            Assert.That(done, Is.False);
            panel.SetActive(false);
            for (int frame = 0; frame < 120 && !done; frame++) yield return null;
            Assert.That(done, Is.True);
        }

        [UnityTest]
        public IEnumerator DisposedAiOrderCancelsMovementAndUnsubscribesEncounterTrace()
        {
            ArmyData army = Army();
            Field(army.Controller, "stepDuration", 10f);
            var screen = NewObject("battle").AddComponent<BattleScreenUI>();
            screen.enabled = false;
            Field(_selection, "battleScreen", screen);
            FieldInfo encounter = typeof(BattleScreenUI).GetField("EncounterResolved",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var before = encounter.GetValue(screen) as Delegate;
            var trace = new AiMoveExecutionTrace();
            IEnumerator move = AiTurnController.MoveArmyRoutine(_owner,
                AiDecision.Move(army, _last, "test", 0f), Context(), trace);
            Coroutine running = _map.StartCoroutine(move);
            for (int frame = 0; frame < 120 && !army.Controller.IsMoving; frame++) yield return null;
            Assert.That(army.Controller.IsMoving, Is.True);
            Assert.That(army.PendingAirStrikePolicy.HasValue, Is.True);
            ((IDisposable)move).Dispose();
            _map.StopCoroutine(running);
            Assert.That(army.Controller.IsMoving, Is.False);
            Assert.That(army.Hex, Is.EqualTo(_first));
            Assert.That(army.PendingAirStrikePolicy, Is.Null);
            Assert.That(encounter.GetValue(screen), Is.EqualTo(before));
            yield return null;
        }

        [UnityTest]
        public IEnumerator MoverLostKeepsStepSpendAndReleasesOnlyItsEconomyReserve()
        {
            ArmyData army = Army(air: false);
            Field(army.Controller, "stepDuration", 0.05f);
            var pm = new ProvisionedMission
            {
                Kind = MissionKind.Economy, MoverArmyId = army.Id, ExecutionHex = _origin,
                FocusHex = _first, ReservationOwner = "lost-builder",
                EconomyTarget = new EconomyMissionTarget
                { Kind = EconomyTaskKind.ReturnBuilder, TargetHex = _first, BuilderArmyId = army.Id },
            };
            foreach (string owner in new[] { "lost-builder", "other-task" })
                StrategicResourceReservationLedger.Upsert(_owner, 1, new StrategicResourceReservation
                {
                    Owner = owner, Amount = 3, Resource = StrategicReservedResource.Energy,
                    Reason = StrategicReservationReason.EconomyDeferredBuild,
                    ExpirationStage = StrategicReservationExpiry.EndOfTurn,
                });
            IEnumerator Kill()
            {
                yield return new WaitUntil(() => army.Controller != null && army.Controller.IsMoving
                    && army.Controller.CurrentHex.Equals(_first));
                army.Members.Clear();
                _selection.DeleteArmyIfEmptied(army);
            }
            _map.StartCoroutine(Kill());
            var results = new List<ExecutionResult>();
            yield return TaskExecutor.ExecuteStep(_owner, _root, Context(), pm, results,
                enforceFreshPlan: false);
            ExecutionResult result = results.Single();
            Assert.That(result.StopReason, Is.EqualTo(ExecutionStopReason.MoverLost));
            Assert.That(result.FinalHex, Is.EqualTo(_first));
            Assert.That(result.StepsMoved, Is.EqualTo(1));
            Assert.That(result.ApSpent, Is.EqualTo(1));
            Assert.That(result.ReachedGoal, Is.False, "death at the destination is not delivery");
            Assert.That(_root.ActionPoints, Is.EqualTo(19));
            Assert.That(_root.GetResource(ResourceType.Energy), Is.EqualTo(20));
            Assert.That(StrategicResourceReservationLedger.Active(_owner, 1,
                StrategicReservedResource.Energy), Is.EqualTo(3));
            Assert.That(StrategicResourceReservationLedger.ReleaseByOwner(_owner, 1, "lost-builder"), Is.False);
        }

        [UnityTest]
        public IEnumerator ReconLossFinishesWithMoverLostAndDropsOnlyItsFlightRecords()
        {
            ArmyData lost = Army();
            ArmyData other = Army();
            var lostSortie = new AirSortie { Army = lost, Kind = AirSortieKind.Recon };
            var otherSortie = new AirSortie { Army = other, Kind = AirSortieKind.Strike };
            AirSortieRegistry.Add(_owner, lostSortie);
            AirSortieRegistry.Add(_owner, otherSortie);
            lost.Members.Clear();
            _selection.DeleteArmyIfEmptied(lost);
            var result = new ExecutionResult { FinalHex = _first };
            var control = new ReconAirExecutor.ActorStepControl { MovedAny = true, CanContinue = true };
            typeof(ReconAirExecutor).GetMethod("FinishActorStep", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { _owner, lost.Id, result, control, ExecutionStopReason.StepCompleted });
            Assert.That(result.StopReason, Is.EqualTo(ExecutionStopReason.MoverLost));
            Assert.That(result.FinalHex, Is.EqualTo(_first));
            Assert.That(control.CanContinue, Is.False);
            Assert.That(AirSortieRegistry.For(_owner), Has.None.EqualTo(lostSortie));
            Assert.That(AirSortieRegistry.For(_owner), Does.Contain(otherSortie));
            yield return null;
        }

        [UnityTest]
        public IEnumerator AiWaitReportsDeathHexAndRetainsPaidLaunch()
        {
            ArmyData army = Army();
            Field(army.Controller, "stepDuration", 0.05f);
            int paidAp = army.PendingActivationApCost;
            int paidEnergy = army.PendingActivationEnergyCost;
            var trace = new AiMoveExecutionTrace();
            IEnumerator KillOnStep()
            {
                yield return new WaitUntil(() => army.Controller != null && army.Controller.IsMoving
                    && army.Controller.CurrentHex.Equals(_first));
                army.Members.Clear();
                _selection.DeleteArmyIfEmptied(army);
            }
            // A stable runner is independent of the marker that Destroy will stop.
            _map.StartCoroutine(KillOnStep());
            yield return AiTurnController.MoveArmyRoutine(_owner,
                AiDecision.Move(army, _last, "test", 0f), Context(), trace);
            Assert.That(trace.MoveResult, Is.EqualTo(MoveOrderResult.Started));
            Assert.That(trace.EndHex, Is.EqualTo(_first));
            Assert.That(trace.ReachedDestination, Is.False);
            Assert.That(_root.ActionPoints, Is.EqualTo(20 - paidAp));
            Assert.That(_root.GetResource(ResourceType.Energy), Is.EqualTo(20 - paidEnergy));
            Assert.That(AiV2Util.ResolveArmy(_owner, army.Id), Is.Null);
            Assert.That(army.PendingAirStrikePolicy, Is.Null);
        }
    }
}
#endif
