using System;
using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Players;
using Game.Map;

namespace Game.Ai.V2
{
    internal static partial class ReconCapacityDeficitRegistry
    {
        internal static IReadOnlyDictionary<string, IReadOnlyDictionary<ReconDeficitKind, (int FirstSeenTurn, int LastSeenTurn)>>
            CapturePersistentState(Func<PlayerSetupData, string> playerId) => AiPersistenceCollections.Capture(ByPlayer, playerId,
                x => AiPersistenceCollections.Freeze(x.Select(row => new KeyValuePair<ReconDeficitKind, (int, int)>(row.Key,
                    (row.Value.FirstSeenTurn, row.Value.LastSeenTurn)))));
        internal static void RestorePersistentState(
            IReadOnlyDictionary<string, IReadOnlyDictionary<ReconDeficitKind, (int FirstSeenTurn, int LastSeenTurn)>> state,
            Func<string, PlayerSetupData> player) => AiPersistenceCollections.Restore(ByPlayer, state, player,
                x => x.ToDictionary(row => row.Key, row => new Entry
                { FirstSeenTurn = row.Value.FirstSeenTurn, LastSeenTurn = row.Value.LastSeenTurn }));
    }
}
