using System;
using System.Collections.Generic;
using System.Linq;
using Game.Players;

namespace Game.Ai.V2.Initiative
{
    public static partial class InitiativeAnalyticsHistory
    {
        internal static IReadOnlyDictionary<string, IReadOnlyList<InitiativeTurnRecord>> CapturePersistentState(Func<PlayerSetupData, string> playerId) =>
            AiPersistenceCollections.Capture(ByPlayer, playerId, x => AiPersistenceCollections.FreezeList(x));

        internal static void RestorePersistentState(IReadOnlyDictionary<string, IReadOnlyList<InitiativeTurnRecord>> state,
            Func<string, PlayerSetupData> player) =>
            AiPersistenceCollections.Restore(ByPlayer, state, player, x => new List<InitiativeTurnRecord>(x));
    }
}
