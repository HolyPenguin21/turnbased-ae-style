using System;
using System.Collections.Generic;
using System.Linq;
using Game.Players;

namespace Game.Ai.V2
{
    internal static partial class LifecycleReturnPolicy
    {
        internal static IReadOnlyDictionary<string, IReadOnlyDictionary<MissionIntentKey, int>> CapturePersistentState(Func<PlayerSetupData, string> playerId) =>
            AiPersistenceCollections.Capture(LastWait, playerId, x => AiPersistenceCollections.Freeze(x));

        internal static void RestorePersistentState(IReadOnlyDictionary<string, IReadOnlyDictionary<MissionIntentKey, int>> state,
            Func<string, PlayerSetupData> player) =>
            AiPersistenceCollections.Restore(LastWait, state, player, x => x.ToDictionary(row => row.Key, row => row.Value));
    }
}
