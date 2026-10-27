using System;
using System.Collections.Generic;
using System.Linq;
using Game.Players;

namespace Game.Ai.V2
{
    internal static partial class DevelopmentDiversity
    {
        internal static IReadOnlyDictionary<string, IReadOnlyList<(int Turn, string CardKey)>> CapturePersistentState(Func<PlayerSetupData, string> playerId) =>
            AiPersistenceCollections.Capture(History, playerId, x => AiPersistenceCollections.FreezeList(x));

        internal static void RestorePersistentState(IReadOnlyDictionary<string, IReadOnlyList<(int Turn, string CardKey)>> state,
            Func<string, PlayerSetupData> player) =>
            AiPersistenceCollections.Restore(History, state, player, x => new List<(int Turn, string CardKey)>(x));
    }
}
