using System;
using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Players;
using Game.Map;

namespace Game.Ai.V2
{
    internal static partial class OperationContinuationWindow
    {
        // Only the cross-turn hold is persisted. Settled/open stamps belong to the outgoing turn.
        internal static IReadOnlyDictionary<string, int> CapturePersistentState(Func<PlayerSetupData, string> playerId) =>
            AiPersistenceCollections.Capture(MobilizationHoldUntil, playerId, x => x);
        internal static void RestorePersistentState(IReadOnlyDictionary<string, int> state, Func<string, PlayerSetupData> player)
        {
            AiPersistenceCollections.Restore(MobilizationHoldUntil, state, player, x => x);
            SettledTurn.Clear(); MobilizationOpenTurn.Clear();
        }
    }
}
