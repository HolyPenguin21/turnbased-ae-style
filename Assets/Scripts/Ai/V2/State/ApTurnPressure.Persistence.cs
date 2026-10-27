using System;
using System.Collections.Generic;
using System.Linq;
using Game.Players;

namespace Game.Ai.V2
{
    internal static partial class ApTurnPressure
    {
        internal static IReadOnlyDictionary<string, IReadOnlyList<(int Turn, float Demand)>> CapturePersistentState(Func<PlayerSetupData, string> playerId) =>
            AiPersistenceCollections.Capture(ByPlayer, playerId, x => AiPersistenceCollections.FreezeList(x));

        internal static void RestorePersistentState(IReadOnlyDictionary<string, IReadOnlyList<(int Turn, float Demand)>> state,
            Func<string, PlayerSetupData> player) =>
            AiPersistenceCollections.Restore(ByPlayer, state, player, x => new List<(int Turn, float Demand)>(x));
    }
}
