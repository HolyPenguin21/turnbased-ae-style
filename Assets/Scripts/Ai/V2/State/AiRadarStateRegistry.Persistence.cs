using System;
using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Players;
using Game.Map;

namespace Game.Ai.V2
{
    public static partial class AiRadarStateRegistry
    {
        internal sealed class PersistentState
        {
            internal readonly int LastTurn;
            internal readonly IReadOnlyDictionary<DesireAxis, float> Smoothed;
            internal PersistentState(AiRadarState state)
            { LastTurn = state.LastTurn; Smoothed = AiPersistenceCollections.Freeze(state.Smoothed); }
            internal AiRadarState Restore()
            {
                var state = new AiRadarState { LastTurn = LastTurn };
                foreach (var row in Smoothed) state.Smoothed.Add(row.Key, row.Value);
                return state;
            }
        }
        internal static IReadOnlyDictionary<string, PersistentState> CapturePersistentState(Func<PlayerSetupData, string> playerId) =>
            AiPersistenceCollections.Capture(ByPlayer, playerId, x => new PersistentState(x));
        internal static void RestorePersistentState(IReadOnlyDictionary<string, PersistentState> state,
            Func<string, PlayerSetupData> player) => AiPersistenceCollections.Restore(ByPlayer, state, player, x => x.Restore());
    }
}
