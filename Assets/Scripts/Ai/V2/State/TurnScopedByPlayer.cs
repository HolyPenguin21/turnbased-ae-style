using System;
using System.Collections.Generic;
using Game.Players;

namespace Game.Ai.V2
{
    // The one storage behind a per-player registry that lives for a single turn
    // (MatchStateKind.TurnScoped): a value stamped with another turn reads as absent, and the
    // first write of a new turn starts a fresh value. Owners keep no turn stamp of their own.
    internal sealed class TurnScopedByPlayer<T> where T : class
    {
        private readonly Dictionary<PlayerSetupData, (int Turn, T Value)> _byPlayer =
            new Dictionary<PlayerSetupData, (int Turn, T Value)>();
        private readonly Func<T> _create;

        internal TurnScopedByPlayer(Func<T> create) { _create = create; }

        // This turn's value, or null.
        internal T Peek(PlayerSetupData player, int turn) =>
            player != null && _byPlayer.TryGetValue(player, out var slot) && slot.Turn == turn
                ? slot.Value : null;

        // This turn's value, created fresh when absent or left over from another turn.
        internal T GetOrReset(PlayerSetupData player, int turn) =>
            Peek(player, turn) ?? Reset(player, turn);

        // A fresh value for this turn, replacing whatever was stored.
        internal T Reset(PlayerSetupData player, int turn)
        {
            T value = _create();
            _byPlayer[player] = (turn, value);
            return value;
        }

        // Drops this turn's value; a value of another turn is left alone.
        internal void Remove(PlayerSetupData player, int turn)
        {
            if (Peek(player, turn) != null)
                _byPlayer.Remove(player);
        }

        internal void Clear() => _byPlayer.Clear();
    }
}
