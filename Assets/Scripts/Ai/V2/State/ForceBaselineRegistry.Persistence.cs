using System;
using System.Collections.Generic;
using System.Linq;
using Game.Players;

namespace Game.Ai.V2
{
    public static partial class ForceBaselineRegistry
    {
        internal static IReadOnlyDictionary<string, float> CapturePersistentState(Func<PlayerSetupData, string> playerId) =>
            AiPersistenceCollections.Capture(StartPotential, playerId, x => x);

        internal static void RestorePersistentState(IReadOnlyDictionary<string, float> state,
            Func<string, PlayerSetupData> player) =>
            AiPersistenceCollections.Restore(StartPotential, state, player, x => x);
    }
}
