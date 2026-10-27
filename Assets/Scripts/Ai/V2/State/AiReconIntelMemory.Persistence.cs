using System;
using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Players;
using Game.Map;

namespace Game.Ai.V2
{
    public static partial class AiReconIntelMemory
    {
        internal sealed class PersistentState
        {
            internal readonly IReadOnlyDictionary<string, IReadOnlyDictionary<HexCoord, int>> Observations;
            internal readonly IReadOnlyDictionary<string, int> CurrentTurns;
            internal PersistentState(Func<PlayerSetupData, string> playerId)
            {
                Observations = AiPersistenceCollections.Capture(LastObservedByPlayer, playerId, x => AiPersistenceCollections.Freeze(x));
                CurrentTurns = AiPersistenceCollections.Capture(CurrentTurnByPlayer, playerId, x => x);
            }
        }
        internal static PersistentState CapturePersistentState(Func<PlayerSetupData, string> playerId) => new PersistentState(playerId);
        internal static void RestorePersistentState(PersistentState state, Func<string, PlayerSetupData> player)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            var observations = new Dictionary<PlayerSetupData, Dictionary<HexCoord, int>>();
            var turns = new Dictionary<PlayerSetupData, int>();
            AiPersistenceCollections.Restore(observations, state.Observations, player, x => x.ToDictionary(row => row.Key, row => row.Value));
            AiPersistenceCollections.Restore(turns, state.CurrentTurns, player, x => x);
            LastObservedByPlayer.Clear(); CurrentTurnByPlayer.Clear();
            foreach (var row in observations) LastObservedByPlayer.Add(row.Key, row.Value);
            foreach (var row in turns) CurrentTurnByPlayer.Add(row.Key, row.Value);
        }
    }
}
