#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Collections;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class ArmyMovementOperationTests
    {
        private ArmyData army;
        private readonly HexCoord origin = new HexCoord(0, 0), first = new HexCoord(1, 0), last = new HexCoord(2, 0);
        [SetUp] public void SetUp()
        {
            VisionSystem.Clear(); VisionSystem.Configure(null); ArmyRegistry.Clear(); BuildingRegistry.Clear();
            army = new ArmyData { Owner = new PlayerSetupData(), Hex = origin };
            army.Members.Add(new UnitData { Owner = army.Owner, MoveMax = 5, MoveCurrent = 5, HitPointsCurrent = 4 });
            ArmyRegistry.Register(army);
        }
        [TearDown] public void TearDown() { ArmyRegistry.Clear(); VisionSystem.Clear(); BuildingRegistry.Clear(); }
        private ArmyMovementOperation Move(Action arrival = null, Func<bool> activate = null, Func<HexCoord, int?> terrain = null)
            => new ArmyMovementOperation(army, new[] { origin, first, last }, terrain ?? (_ => 1), activate, arrival);
        [Test] public void CancelledMoveNeverRunsArrivalOrChargesUnstartedStep()
        {
            int arrivals = 0, activations = 0, results = 0;
            var move = Move(() => arrivals++, () => { activations++; return true; });
            move.Completed += _ => results++;
            move.Cancel(); move.Cancel();
            Assert.That(move.TryEnterNextStep(out _, out _), Is.False);
            Assert.That(army.Hex, Is.EqualTo(origin)); Assert.That(army.CurrentMovement, Is.EqualTo(5));
            Assert.That(activations, Is.Zero); Assert.That(arrivals, Is.Zero); Assert.That(results, Is.EqualTo(1));
        }
        [Test] public void CancelAfterEntryKeepsIndexedHexAndPaidMovementWithoutArrival()
        {
            int arrivals = 0;
            var move = Move(() => arrivals++);
            Assert.That(move.TryEnterNextStep(out _, out _), Is.True);
            Assert.That(army.Hex, Is.EqualTo(first)); Assert.That(ArmyRegistry.At(first), Does.Contain(army));
            Assert.That(ArmyRegistry.At(origin), Does.Not.Contain(army));
            move.Cancel(); move.Settle();
            Assert.That(army.CurrentMovement, Is.EqualTo(4)); Assert.That(arrivals, Is.Zero);
            Assert.That(move.Result.EnteredSteps, Is.EqualTo(1)); Assert.That(move.IsBusy, Is.False);
        }
        [Test] public void DeathNeverRunsArrivalAndPublishesOneTerminalResult()
        {
            int arrivals = 0, results = 0;
            var move = Move(() => arrivals++); move.Completed += _ => results++;
            move.TryEnterNextStep(out _, out _);
            ArmyActions.RemoveMembers(army, army.Members.ToArray());
            move.Settle(); move.Cancel();
            Assert.That(move.Result.Kind, Is.EqualTo(ArmyMovementEnd.Dead));
            Assert.That(arrivals, Is.Zero); Assert.That(results, Is.EqualTo(1));
        }
        [Test] public void IllegalFirstStepNeverPaysActivation()
        {
            int paid = 0;
            var move = Move(activate: () => { paid++; return true; }, terrain: _ => null);
            Assert.That(move.TryEnterNextStep(out _, out _), Is.False);
            Assert.That(paid, Is.Zero); Assert.That(army.CurrentMovement, Is.EqualTo(5));
            Assert.That(move.Result.Kind, Is.EqualTo(ArmyMovementEnd.Rejected));
        }
        [Test] public void MovementRevalidatesAfterAircraftReaction()
        {
            var slow = army.Members[0]; slow.IsAviation = true; slow.MoveCurrent = 1;
            var fast = new UnitData { Owner = army.Owner, IsAviation = true, MoveCurrent = 5, MoveMax = 5, HitPointsCurrent = 3 };
            army.Members.Add(fast); army.IsAirArmy = true;
            var move = Move(); Assert.That(move.TryEnterNextStep(out _, out _), Is.True);
            Assert.That(move.CanContinue, Is.False);
            ArmyActions.RemoveMember(army, slow);
            Assert.That(move.CanContinue, Is.True);
            Assert.That(move.TryEnterNextStep(out _, out _), Is.True);
            Assert.That(fast.MoveCurrent, Is.EqualTo(3)); Assert.That(army.Hex, Is.EqualTo(last));
        }
        [Test] public void RouteIsCopiedAndChangedEligibilityStopsAtLastLegalHex()
        {
            var route = new List<HexCoord> { origin, first, last };
            var move = new ArmyMovementOperation(army, route, _ => 1);
            route[1] = new HexCoord(8, 8);
            Assert.That(move.TryEnterNextStep(out _, out _), Is.True);
            army.IsGarrison = true;
            Assert.That(move.TryEnterNextStep(out _, out _), Is.False);
            Assert.That(army.Hex, Is.EqualTo(first)); Assert.That(army.CurrentMovement, Is.EqualTo(4));
        }
        private static void Drain(IEnumerator iterator)
        {
            try { while (iterator.MoveNext()) if (iterator.Current is IEnumerator nested) Drain(nested); }
            finally { (iterator as IDisposable)?.Dispose(); }
        }
        [Test] public void EmptyHeadlessMoveAtOwnBarracksIsRemovedAsDeath()
        {
            var building = new BuildingData { Owner = army.Owner, Hex = first, IsBase = true };
            building.Abilities.Add(Game.Cards.UnitAbilities.Barracks); BuildingRegistry.Register(first, building);
            var move = Move(); move.TryEnterNextStep(out _, out _);
            army.Members.Clear(); ArmyActions.DeleteArmyIfEmptied(army);
            Assert.That(ArmyRegistry.IsRegistered(army), Is.False);
            Assert.That(move.Result.Kind, Is.EqualTo(ArmyMovementEnd.Dead));
            Assert.That(move.Result.Hex, Is.EqualTo(first));
        }
        [Test] public void SecondRunnerCannotAdvanceAnOperationAwaitingReaction()
        {
            var move = Move();
            IEnumerator Reaction(HexCoord from, HexCoord to, ArmyController.StepResolutionOutcome result) { yield return null; }
            var firstRun = move.Run(null, resolve: Reaction);
            Assert.That(firstRun.MoveNext(), Is.True);
            try
            {
                var second = move.Run(null);
                Assert.Throws<InvalidOperationException>(() => second.MoveNext());
                Assert.That(army.Hex, Is.EqualTo(first)); Assert.That(army.CurrentMovement, Is.EqualTo(4));
                Assert.That(move.IsBusy, Is.True);
            }
            finally { ((IDisposable)firstRun).Dispose(); }
        }
        [Test] public void ExternalStepAndSettleCannotBypassUnresolvedReaction()
        {
            int arrivals = 0; var move = Move(() => arrivals++);
            IEnumerator Reaction(HexCoord from, HexCoord to, ArmyController.StepResolutionOutcome result) { yield return null; }
            var run = move.Run(null, resolve: Reaction);
            Assert.That(run.MoveNext(), Is.True);
            try
            {
                Assert.That(move.TryEnterNextStep(out _, out _), Is.False);
                move.Settle();
                Assert.That(move.Phase, Is.EqualTo(ArmyMovementPhase.Resolving));
                Assert.That(move.IsBusy, Is.True); Assert.That(arrivals, Is.Zero);
                Assert.That(army.Hex, Is.EqualTo(first)); Assert.That(army.CurrentMovement, Is.EqualTo(4));
            }
            finally { ((IDisposable)run).Dispose(); }
        }
        [Test] public void HeadlessMovingArmyDoesNotResolveResidentRevealBeforeStepEffects()
        {
            army.Members[0].IsHidden = true;
            var move = Move(); int contacts = 0;
            Action<ArmyData> revealed = _ => contacts++;
            StealthSystem.ResidentRevealed += revealed;
            try { StealthSystem.ExitStealth(army.Members[0]); }
            finally { StealthSystem.ResidentRevealed -= revealed; move.Cancel(); }
            Assert.That(contacts, Is.Zero);
        }
        [Test] public void SaveGateIncludesPendingHeadlessMovementUntilCancelled()
        {
            var move = Move();
            Assert.That(army.Controller, Is.Null);
            Assert.That(ArmyRegistry.HasMovementInProgress, Is.True);
            move.Cancel(); Assert.That(ArmyRegistry.HasMovementInProgress, Is.False);
        }
        [Test] public void NotificationCancellationDoesNotRunStaleStepPresentation()
        {
            int starts = 0, arrivals = 0;
            var move = Move(() => arrivals++);
            Action<PlayerSetupData, HexCoord> cancel = (viewer, hex) => { if (hex.Equals(first)) move.Cancel(); };
            VisionSystem.VisibleContentChanged += cancel;
            try { Drain(move.Run(null, started: (from, to) => starts++)); }
            finally { VisionSystem.VisibleContentChanged -= cancel; }
            Assert.That(starts, Is.Zero); Assert.That(arrivals, Is.Zero);
            Assert.That(move.Result.Hex, Is.EqualTo(first));
        }
        [Test] public void UnregisterCancelsPendingHeadlessMoveWithoutArrival()
        {
            int arrivals = 0; var move = Move(() => arrivals++);
            ArmyRegistry.Unregister(army);
            Assert.That(move.IsBusy, Is.False); Assert.That(arrivals, Is.Zero);
        }
        [Test] public void CancelledReactionNeverFallsThroughToArrival()
        {
            int arrivals = 0; var move = Move(() => arrivals++);
            IEnumerator Reaction(HexCoord from, HexCoord to, ArmyController.StepResolutionOutcome outcome)
            { outcome.Cancelled = true; outcome.StopMovement = true; yield break; }
            Drain(move.Run(null, resolve: Reaction));
            Assert.That(move.Result.Kind, Is.EqualTo(ArmyMovementEnd.Cancelled)); Assert.That(arrivals, Is.Zero);
        }
        [Test] public void CancelDuringReactionCannotRunArrivalAfterIteratorResumes()
        {
            int arrivals = 0, cancelled = 0;
            var move = Move(() => arrivals++);
            IEnumerator Reaction(HexCoord from, HexCoord to, ArmyController.StepResolutionOutcome outcome)
            {
                outcome.CancelResolution = () => cancelled++;
                move.Cancel();
                yield return null;
            }
            Drain(move.Run(null, resolve: Reaction));
            Assert.That(arrivals, Is.Zero); Assert.That(cancelled, Is.EqualTo(1));
            Assert.That(army.Hex, Is.EqualTo(first)); Assert.That(army.CurrentMovement, Is.EqualTo(4));
        }
        [Test] public void DisposedMoveDoesNotRunArrivalOrChargeAnotherStep()
        {
            int arrivals = 0;
            var move = Move(() => arrivals++);
            IEnumerator Animation(HexCoord hex) { yield return null; }
            var run = move.Run(Animation);
            Assert.That(run.MoveNext(), Is.True);
            ((IDisposable)run).Dispose();
            Assert.That(move.Result.Kind, Is.EqualTo(ArmyMovementEnd.Cancelled));
            Assert.That(army.Hex, Is.EqualTo(first)); Assert.That(army.CurrentMovement, Is.EqualTo(4));
            Assert.That(arrivals, Is.Zero);
        }
        [Test] public void TransportLossAfterReactionRevalidatesPassengerMovement()
        {
            var passenger = army.Members[0]; passenger.MoveMax = 1; passenger.MoveCurrent = 1;
            var transport = new UnitData { Owner = army.Owner, MoveMax = 5, MoveCurrent = 5, HitPointsCurrent = 3 };
            transport.Abilities.Add(Game.Cards.UnitAbilities.Transport);
            army.Members.Add(transport); army.RefreshTransport();
            var move = Move();
            Assert.That(move.TryEnterNextStep(out _, out _), Is.True);
            ArmyActions.RemoveMember(army, transport);
            Assert.That(move.CanContinue, Is.False);
            Assert.That(move.TryEnterNextStep(out _, out _), Is.False);
            Assert.That(army.Hex, Is.EqualTo(first)); Assert.That(passenger.MoveCurrent, Is.Zero);
        }
        [Test] public void PendingOperationIsBusyAndCancellationCannotReenterArrival()
        {
            var move = Move();
            Assert.That(army.IsMovementBusy, Is.True);
            Assert.That(move.Phase, Is.EqualTo(ArmyMovementPhase.Pending));
            move.Cancel(); Assert.That(army.IsMovementBusy, Is.False);
        }
        [Test] public void FirstStepRecordsVisibilityBeforeIndexedRelocation()
        {
            var move = Move();
            Assert.That(VisionSystem.IsVisible(army.Owner, first), Is.False);
            move.TryEnterNextStep(out _, out _);
            Assert.That(VisionSystem.IsVisible(army.Owner, first), Is.True);
            Assert.That(move.LastStepWasVisible, Is.False);
        }
        [Test] public void ArrivalRemainsBusyUntilEffectsFinish()
        {
            ArmyMovementOperation move = null;
            move = Move(() => Assert.That(army.IsMovementBusy, Is.True));
            move.TryEnterNextStep(out _, out _); move.TryEnterNextStep(out _, out _); move.Settle();
            Assert.That(move.IsBusy, Is.False);
        }
        [Test] public void ArrivalAndCompletionAreOneShotAndReleaseBusyBeforeNotification()
        {
            int arrivals = 0, results = 0;
            var move = Move(() => arrivals++);
            move.Completed += _ => { results++; Assert.That(army.Movement.IsBusy, Is.False); };
            move.TryEnterNextStep(out _, out _); move.TryEnterNextStep(out _, out _);
            move.Settle(); move.Settle(); move.Cancel();
            Assert.That(arrivals, Is.EqualTo(1)); Assert.That(results, Is.EqualTo(1));
            Assert.That(move.Result.Hex, Is.EqualTo(last));
        }
    }
}
#endif
