using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.HexGrid;
using Game.Players;

namespace Game.Map
{
    public enum ArmyMovementEnd { Arrived, Interrupted, Rejected, Cancelled, Dead }
    public enum ArmyMovementPhase { Pending, Moving, Resolving, Finishing, Settled }

    public sealed class ArmyMovementResult
    {
        public ArmyMovementEnd Kind { get; }
        public HexCoord Hex { get; }
        public int EnteredSteps { get; }
        internal ArmyMovementResult(ArmyMovementEnd kind, HexCoord hex, int enteredSteps)
        { Kind = kind; Hex = hex; EnteredSteps = enteredSteps; }
    }

    // A move owns the legal world state, including while presentation is suspended. The route
    // is detached from its caller. Animation receives entered steps; it never spends MP or
    // chooses an endpoint. Cancellation terminates the outstanding reaction before notifying.
    public sealed partial class ArmyMovementOperation
    {
        private readonly ArmyData army;
        private readonly PlayerSetupData owner;
        private readonly HexCoord[] path;
        private readonly Func<HexCoord, int?> terrainCost;
        private readonly Func<bool> activate;
        private readonly Action arrival;
        private readonly bool registered;
        private int index;
        private bool finishing;
        private bool running;
        private ArmyController.StepResolutionOutcome reaction;
        public ArmyMovementPhase Phase { get; private set; } = ArmyMovementPhase.Pending;
        public bool IsBusy => Phase != ArmyMovementPhase.Settled;
        public bool LastStepWasVisible { get; private set; }
        public HexCoord CurrentHex { get; private set; }
        public int EnteredSteps => index;
        public ArmyMovementResult Result { get; private set; }
        public event Action<ArmyMovementResult> Completed;

        public ArmyMovementOperation(ArmyData army, IEnumerable<HexCoord> path,
            Func<HexCoord, int?> terrainCost, Func<bool> activate = null, Action arrival = null)
        {
            this.army = army ?? throw new ArgumentNullException(nameof(army));
            if (army.Movement?.IsBusy == true) throw new InvalidOperationException("Army already has an active move.");
            this.path = path?.ToArray() ?? Array.Empty<HexCoord>();
            this.terrainCost = terrainCost ?? throw new ArgumentNullException(nameof(terrainCost));
            this.activate = activate; this.arrival = arrival;
            owner = army.Owner; registered = ArmyRegistry.IsRegistered(army); CurrentHex = army.Hex;
            army.Movement = this;
        }

        public static ArmyMovementOperation ForMap(ArmyData army, HexMap map, IEnumerable<HexCoord> path,
            Func<bool> activate = null, Action arrival = null)
            => new ArmyMovementOperation(army, path, hex =>
            {
                if (map == null || !map.CanEnter(hex, army)) return null;
                map.TryGetTerrainAt(hex, out var terrain);
                return terrain != null ? Math.Max(1, terrain.moveCost) : 1;
            }, activate, arrival);

        private bool Eligible => army.Members.Count > 0 && ReferenceEquals(owner, army.Owner)
            && !army.IsGarrison && !army.IsPrison && !army.IsAirfield
            && (!registered || ArmyRegistry.IsRegistered(army)) && army.Hex.Equals(CurrentHex);

        private bool NextStep(out HexCoord next, out int cost)
        {
            next = default; cost = 0;
            if (Result != null || !Eligible || path.Length < 2 || index >= path.Length - 1
                || !path[index].Equals(CurrentHex)) return false;
            next = path[index + 1];
            if (HexGridMath.Distance(CurrentHex, next) != 1) return false;
            int? terrain = terrainCost(next);
            if (!terrain.HasValue) return false;
            cost = AviationRules.MovementCost(army, Math.Max(1, terrain.Value));
            return army.CurrentMovement >= cost;
        }
        // Evaluated again after AA/stealth/contact; never cache a pre-reaction roster or MP.
        public bool CanContinue => NextStep(out _, out _);

        // Explicit stepping is a compatibility driver for raw ForMap operations. Once Run
        // owns the operation, neither another driver nor a caller may bypass its pending effects.
        public bool TryEnterNextStep(out HexCoord from, out HexCoord to)
        {
            from = CurrentHex; to = CurrentHex;
            return !running && entryEffects == null && TryEnterNextStepCore(out from, out to);
        }
        private bool TryEnterNextStepCore(out HexCoord from, out HexCoord to)
        {
            from = CurrentHex; to = CurrentHex;
            if (Result != null) return false;
            if (!NextStep(out HexCoord next, out int cost)) { SettleCore(); return false; }
            using (VisionSystem.BeginMutation())
            {
                if (index == 0 && activate != null && !activate()) { Finish(ArmyMovementEnd.Rejected, false); return false; }
                // Activation may change carrier membership; validate that formation before debit.
                if (!IsBusy || !NextStep(out next, out cost)) { if (IsBusy) SettleCore(); return false; }
                foreach (var member in army.Members)
                    member.MoveCurrent = Math.Max(0, member.MoveCurrent - (member.HasEmergencyFlightPenalty ? cost * 2 : cost));
                LastStepWasVisible = VisionSystem.IsVisible(owner, next);
                CurrentHex = next; index++; Phase = ArmyMovementPhase.Moving;
                if (registered) ArmyRegistry.MoveArmy(army, next);
                else army.SetHex(next);
            }
            to = next;
            return IsBusy;
        }

        public void Cancel() => Finish(army.Members.Count == 0 ? ArmyMovementEnd.Dead : ArmyMovementEnd.Cancelled, false);
        public void Settle()
        {
            if (!running && entryEffects == null) SettleCore();
        }
        private void SettleCore()
        {
            if (Result != null) return;
            if (army.Members.Count == 0 || registered && !ArmyRegistry.IsRegistered(army))
                Finish(ArmyMovementEnd.Dead, false);
            else if (index == 0) Finish(ArmyMovementEnd.Rejected, false);
            else Finish(index == path.Length - 1 ? ArmyMovementEnd.Arrived : ArmyMovementEnd.Interrupted, true);
        }
        private void Finish(ArmyMovementEnd kind, bool runArrival)
        {
            if (Result != null || finishing) return;
            finishing = true; Phase = ArmyMovementPhase.Finishing;
            var activeReaction = reaction; reaction = null;
            // Claim the terminal result before callbacks, so a late coroutine cannot arrive.
            Result = new ArmyMovementResult(kind, CurrentHex, index);
            try
            {
                activeReaction?.CancelResolution?.Invoke();
                if (runArrival && EligibleForArrival()) arrival?.Invoke();
            }
            finally
            {
                army.PendingAirStrikePolicy = null;
                Phase = ArmyMovementPhase.Settled;
                VisionSystem.PublishAfterMutation(Completed, Result);
            }
        }
        private bool EligibleForArrival() => army.Members.Count > 0 && ReferenceEquals(owner, army.Owner)
            && (!registered || ArmyRegistry.IsRegistered(army)) && army.Hex.Equals(CurrentHex);

        public IEnumerator Run(Func<HexCoord, IEnumerator> animate,
            Action<HexCoord, HexCoord> started = null, Action<HexCoord, HexCoord> entered = null,
            Func<HexCoord, HexCoord, ArmyController.StepResolutionOutcome, IEnumerator> resolve = null,
            Func<HexCoord, bool> stop = null)
        {
            if (Result != null) yield break;
            if (running) throw new InvalidOperationException("This move already has an active driver.");
            running = true;
            resolve = resolve ?? defaultStepResolution;
            try
            {
                while (IsBusy && TryEnterNextStepCore(out HexCoord from, out HexCoord to))
                {
                    started?.Invoke(from, to);
                    if (!IsBusy) yield break;
                    if (animate != null) yield return animate(to);
                    if (!IsBusy) yield break;
                    if (!Eligible) { SettleCore(); yield break; }
                    entered?.Invoke(from, to);
                    if (!IsBusy) yield break;
                    if (resolve != null)
                    {
                        reaction = new ArmyController.StepResolutionOutcome { CanContinueMovement = () => CanContinue };
                        Phase = ArmyMovementPhase.Resolving;
                        yield return resolve(from, to, reaction);
                        var outcome = reaction; reaction = null;
                        if (!IsBusy) yield break;
                        if (!Eligible) { SettleCore(); yield break; }
                        if (outcome?.Cancelled == true) { Cancel(); yield break; }
                        if (outcome != null && outcome.StopMovement) { SettleCore(); yield break; }
                    }
                    Phase = ArmyMovementPhase.Moving;
                    bool stopForEntry = entryEffects != null && entryEffects(to);
                    if (!IsBusy) yield break;
                    if (stopForEntry || stop != null && stop(to)) { SettleCore(); yield break; }
                }
            }
            finally
            {
                try { if (IsBusy) Cancel(); }
                finally { running = false; }
            }
        }
    }
}
