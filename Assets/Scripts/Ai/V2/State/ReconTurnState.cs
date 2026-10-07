using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Game.Ai.V2
{
    // One immutable player/turn scope. No independent trim/used clocks to synchronize.
    internal sealed class ReconTurnState
    {
        internal int TurnNumber { get; }
        private bool _ended;
        private int _trimCount;
        private readonly HashSet<int> _trimmedActors = new HashSet<int>();
        private readonly HashSet<int> _usedActors = new HashSet<int>();

        internal ReconTurnState(int turn) => TurnNumber = turn;

        private void RequireTurn(int turn)
        {
            if (_ended) throw new ObjectDisposedException(nameof(ReconTurnState));
            if (turn != TurnNumber) throw new InvalidOperationException("Recon state belongs to another turn.");
        }

        internal bool TryConsumeReconLaneTrim(int turn)
        {
            RequireTurn(turn);
            if (_trimCount >= AiConfigV2.maxReconLaneTrimPerTurn) return false;
            ++_trimCount;
            return true;
        }

        internal void MarkReconActorTrimmed(int turn, int armyId)
        {
            RequireTurn(turn);
            // Actor id 0 is valid; absence is represented by nullable actor identity upstream.
            _trimmedActors.Add(armyId);
        }

        internal IReadOnlyCollection<int> ReconActorsTrimmedThisTurn(int turn)
        {
            RequireTurn(turn);
            return _trimmedActors;
        }

        internal IReadOnlyCollection<int> ReconGroundActorsUsedThisTurn(int turn)
        {
            RequireTurn(turn);
            return _usedActors;
        }

        internal void MarkReconGroundActorUsed(int turn, int armyId)
        {
            RequireTurn(turn);
            _usedActors.Add(armyId);
        }

        internal void End()
        {
            _trimCount = 0;
            _trimmedActors.Clear();
            _usedActors.Clear();
            _ended = true;
        }
    }

    // Detached lookups have no second set. A session owns the same per-player scope.
    // Detached fixtures use their own identity, never a shared null player.
    internal static class ReconTurnStateStore
    {
        private sealed class Entry { internal int Turn; internal ReconTurnState State; }
        private static ConditionalWeakTable<object, Entry> ByOwner = new ConditionalWeakTable<object, Entry>();

        internal static ReconTurnState For(object owner, int turn)
        {
            if (owner == null) return new ReconTurnState(turn);
            Entry entry = ByOwner.GetValue(owner, _ => new Entry());
            if (entry.State == null || entry.Turn != turn)
            {
                entry.State?.End();
                entry.Turn = turn;
                entry.State = new ReconTurnState(turn);
            }
            return entry.State;
        }

        internal static ReconTurnState Begin(object owner, int turn)
        {
            End(owner);
            return For(owner, turn);
        }

        internal static void End(object owner)
        {
            if (owner == null) return;
            if (ByOwner.TryGetValue(owner, out Entry entry)) entry.State?.End();
            ByOwner.Remove(owner);
        }

        internal static void ClearAll() => ByOwner = new ConditionalWeakTable<object, Entry>();
    }
}
