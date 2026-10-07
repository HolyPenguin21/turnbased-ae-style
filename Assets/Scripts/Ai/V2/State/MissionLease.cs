using System;
using System.Collections.Generic;
using System.Linq;
using Game.Players;

namespace Game.Ai.V2
{
    // Operation-scoped read view. Storage and release belong to its turn's lease book.
    internal sealed class MissionLease
    {
        private readonly MissionLeaseBook _book;
        internal MissionIntentKey Operation { get; }
        internal MissionLease(MissionLeaseBook book, MissionIntentKey operation)
        { _book = book; Operation = operation; }
        internal IReadOnlyCollection<int> ActorClaims => _book.ActorsFor(Operation);
        internal IReadOnlyList<StrategicResourceReservation> ResourceClaims => _book.ResourcesFor(Operation);
        internal void Claim(int actor, ArmyMutationContract contract = null) =>
            _book.Claim(Operation, actor, contract ?? ArmyMutationContract.FullyProtected);
        internal void Reserve(StrategicReservationReason reason, StrategicReservedResource resource,
            float amount, StrategicReservationExpiry expiry = StrategicReservationExpiry.EndOfTurn) =>
            _book.Reserve(Operation, reason, resource, amount, expiry);
    }

    // One owner of the normalized actor view and operation resource release. Mission validity
    // stays in MissionActorPolicy; the existing bank ledger remains the only resource storage.
    internal sealed class MissionLeaseBook
    {
        private sealed class ActorClaim
        {
            internal MissionIntentKey? Operation;
            internal ArmyMutationContract Contract;
            internal bool PreparationHost;
        }
        // The sole claim table; no separate mutable claimed-id set or contract index.
        private readonly Dictionary<int, List<ActorClaim>> _actors = new Dictionary<int, List<ActorClaim>>();
        private readonly PlayerSetupData _player;
        private readonly int _turn;
        private bool _closed;
        internal MissionLeaseBook(PlayerSetupData player = null, int turn = -1)
        { _player = player; _turn = turn; }
        internal MissionLease For(MissionIntentKey operation)
        { EnsureOpen(); return new MissionLease(this, operation); }
        internal void ResetActors() { EnsureOpen(); _actors.Clear(); }
        internal void Claim(MissionIntentKey operation, int actor, ArmyMutationContract contract,
            bool preparationHost = false) => Add(actor, new ActorClaim
            { Operation = operation, Contract = contract, PreparationHost = preparationHost });
        // Anonymous claims preserve pass-local actor use without inventing an operation id.
        internal void ClaimForPass(int actor, ArmyMutationContract contract) =>
            Add(actor, new ActorClaim { Contract = contract });
        private void Add(int actor, ActorClaim claim)
        {
            EnsureOpen();
            if (!_actors.TryGetValue(actor, out var claims))
                _actors.Add(actor, claims = new List<ActorClaim>());
            claims.Add(claim);
        }
        internal IReadOnlyCollection<int> ClaimedActors
        { get { EnsureOpen(); return _actors.Keys; } }
        internal bool IsClaimed(int actor) { EnsureOpen(); return _actors.ContainsKey(actor); }
        internal bool IsPreparationHost(int actor)
        { EnsureOpen(); return _actors.TryGetValue(actor, out var claims) && claims.Any(x => x.PreparationHost); }
        internal ArmyMutationContract ContractOf(int actor)
        {
            EnsureOpen();
            if (!_actors.TryGetValue(actor, out var claims)) return null;
            ArmyMutationContract result = null;
            foreach (var claim in claims)
                result = result == null ? claim.Contract : result.Intersect(claim.Contract);
            return result;
        }
        internal IReadOnlyCollection<int> ActorsFor(MissionIntentKey operation)
        {
            EnsureOpen();
            return _actors.Where(x => x.Value.Any(c => c.Operation.HasValue && c.Operation.Value.Equals(operation))).Select(x => x.Key).ToArray();
        }
        internal IReadOnlyCollection<MissionIntentKey> OwnersOf(int actor)
        {
            EnsureOpen();
            return _actors.TryGetValue(actor, out var claims)
                ? claims.Where(x => x.Operation.HasValue).Select(x => x.Operation.Value).Distinct().ToArray()
                : Array.Empty<MissionIntentKey>();
        }
        internal IReadOnlyList<StrategicResourceReservation> ResourcesFor(MissionIntentKey operation)
        {
            EnsureOpen();
            string token = ReservationOwner.ForOperation(operation);
            return StrategicResourceReservationLedger.Rows(_player, _turn).Where(x => x.Owner == token).ToArray();
        }
        internal void Reserve(MissionIntentKey operation, StrategicReservationReason reason,
            StrategicReservedResource resource, float amount, StrategicReservationExpiry expiry)
        {
            EnsureOpen();
            Upsert(_player, _turn, new StrategicResourceReservation
            { Identity = ReservationOwner.ForOperation(operation), Reason = reason,
                Resource = resource, Amount = amount, ExpirationStage = expiry });
        }
        // Called AFTER the domain policy retires the durable intent, never from leg Completed.
        internal void Retire(MissionIntentKey operation)
        {
            EnsureOpen();
            foreach (int actor in _actors.Keys.ToArray())
            {
                _actors[actor].RemoveAll(x => x.Operation.HasValue && x.Operation.Value.Equals(operation));
                if (_actors[actor].Count == 0) _actors.Remove(actor);
            }
            ReleaseResources(_player, _turn, operation);
        }
        internal static void ReleaseResources(PlayerSetupData player, int turn, MissionIntentKey operation) =>
            ReleaseByOwner(player, turn, ReservationOwner.ForOperation(operation));
        internal void Rekey(MissionIntentKey oldKey, MissionIntentKey newKey)
        {
            EnsureOpen();
            if (oldKey.Equals(newKey)) return;
            foreach (var claims in _actors.Values)
                foreach (var claim in claims)
                    if (claim.Operation.HasValue && claim.Operation.Value.Equals(oldKey)) claim.Operation = newKey;
        }
        // Compatibility-backed resource lifecycle API. The ledger stores rows; all production
        // mutation requests enter here, including pass holds, replacement and expiry.
        private static void RequireTurn(PlayerSetupData player, int turn)
        {
            var active = AiTurnSession.PeekActive(player);
            if (active != null && active.TurnNumber != turn)
                throw new InvalidOperationException("Resource claims belong to another AI turn.");
        }
        internal static void BeginTurn(PlayerSetupData player, int turn) =>
            StrategicResourceReservationLedger.BeginTurn(player, turn);
        internal static void Upsert(PlayerSetupData player, int turn, StrategicResourceReservation row)
        { RequireTurn(player, turn); StrategicResourceReservationLedger.Upsert(player, turn, row); }
        internal static bool ReleaseByOwner(PlayerSetupData player, int turn, string owner)
        { RequireTurn(player, turn); return StrategicResourceReservationLedger.ReleaseByOwner(player, turn, owner); }
        internal static bool ReleaseByReason(PlayerSetupData player, int turn, StrategicReservationReason reason)
        { RequireTurn(player, turn); return StrategicResourceReservationLedger.ReleaseByReason(player, turn, reason); }
        internal static void ReplaceReasonOwner(PlayerSetupData player, int turn,
            StrategicReservationReason reason, string owner, bool replaceOwnerRows = false)
        { RequireTurn(player, turn); StrategicResourceReservationLedger.ReplaceReasonOwner(player, turn, reason, owner, replaceOwnerRows); }
        internal static void ReleaseReasonExceptOwner(PlayerSetupData player, int turn,
            StrategicReservationReason reason, string keepOwner)
        { RequireTurn(player, turn); StrategicResourceReservationLedger.ReleaseReasonExceptOwner(player, turn, reason, keepOwner); }
        internal static bool ExpireStage(PlayerSetupData player, int turn, StrategicReservationExpiry stage)
        { RequireTurn(player, turn); return StrategicResourceReservationLedger.ExpireStage(player, turn, stage); }
        internal static void AssertClearAtTurnEnd(PlayerSetupData player, int turn)
        { RequireTurn(player, turn); StrategicResourceReservationLedger.AssertClearAtTurnEnd(player, turn); }

        internal void Close()
        { if (_closed) return; _actors.Clear(); _closed = true; }
        private void EnsureOpen()
        { if (_closed) throw new ObjectDisposedException(nameof(MissionLeaseBook)); }
    }
}
