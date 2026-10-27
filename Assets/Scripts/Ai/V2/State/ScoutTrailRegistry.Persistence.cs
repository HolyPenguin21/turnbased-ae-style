using System;
using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Players;
using Game.Map;

namespace Game.Ai.V2
{
    internal static partial class ScoutTrailRegistry
    {
        internal static IReadOnlyDictionary<string, IReadOnlyDictionary<int, (HexCoord? JustLeft, IReadOnlyList<HexCoord> Recent)>>
            CapturePersistentState(Func<PlayerSetupData, string> playerId) => AiPersistenceCollections.Capture(ByPlayer, playerId,
                x => AiPersistenceCollections.Freeze(x.Select(row =>
                    new KeyValuePair<int, (HexCoord? JustLeft, IReadOnlyList<HexCoord> Recent)>(row.Key,
                        (row.Value.JustLeft, AiPersistenceCollections.FreezeList(row.Value.Recent))))));
        internal static void RestorePersistentState(
            IReadOnlyDictionary<string, IReadOnlyDictionary<int, (HexCoord? JustLeft, IReadOnlyList<HexCoord> Recent)>> state,
            Func<string, PlayerSetupData> player) => AiPersistenceCollections.Restore(ByPlayer, state, player,
                x => x.ToDictionary(row => row.Key, row =>
                {
                    var trail = new Trail { JustLeft = row.Value.JustLeft };
                    trail.Recent.AddRange(row.Value.Recent); return trail;
                }));
    }
}
