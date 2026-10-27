using System;
using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Players;
using Game.Map;

namespace Game.Ai.V2
{
    internal static partial class PreparationDeliveryMemory
    {
        internal static IReadOnlyDictionary<string, IReadOnlyDictionary<int, (int Turn, IReadOnlyList<string> CardKeys)>>
            CapturePersistentState(Func<PlayerSetupData, string> playerId)
        {
            if (playerId == null) throw new ArgumentNullException(nameof(playerId));
            return AiPersistenceCollections.Freeze(ByHost.GroupBy(x => x.Key.Item1).Select(group =>
                new KeyValuePair<string, IReadOnlyDictionary<int, (int Turn, IReadOnlyList<string> CardKeys)>>(AiPersistenceCollections.CapturePlayerId(group.Key, playerId),
                    AiPersistenceCollections.Freeze(group.Select(row =>
                        new KeyValuePair<int, (int Turn, IReadOnlyList<string> CardKeys)>(row.Key.Item2,
                            (row.Value.Turn, AiPersistenceCollections.FreezeList(row.Value.CardKeys))))))));
        }
        internal static void RestorePersistentState(
            IReadOnlyDictionary<string, IReadOnlyDictionary<int, (int Turn, IReadOnlyList<string> CardKeys)>> state,
            Func<string, PlayerSetupData> player)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (player == null) throw new ArgumentNullException(nameof(player));
            var restored = new Dictionary<(PlayerSetupData, int), Entry>();
            foreach (var owner in state)
            {
                PlayerSetupData bound = AiPersistenceCollections.ResolvePlayer(owner.Key, player);
                foreach (var row in owner.Value)
                {
                    var entry = new Entry { Turn = row.Value.Turn };
                    entry.CardKeys.UnionWith(row.Value.CardKeys); restored.Add((bound, row.Key), entry);
                }
            }
            ByHost.Clear();
            foreach (var row in restored) ByHost.Add(row.Key, row.Value);
        }
    }
}
