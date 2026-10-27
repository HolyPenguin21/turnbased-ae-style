using System;
using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Players;
using Game.Map;

namespace Game.Ai.V2
{
    public sealed partial class AiAllocatorState
    {
        internal IReadOnlyDictionary<StableMissionKey, MissionCooldownInfo> CapturePersistentState() =>
            AiPersistenceCollections.Freeze(_cooldowns);
        internal static AiAllocatorState RestorePersistentState(IReadOnlyDictionary<StableMissionKey, MissionCooldownInfo> state)
        {
            var restored = new AiAllocatorState();
            foreach (var row in state) restored._cooldowns.Add(row.Key, row.Value);
            return restored;
        }
    }
    public static partial class AiAllocatorStateRegistry
    {
        internal static IReadOnlyDictionary<string, IReadOnlyDictionary<StableMissionKey, MissionCooldownInfo>> CapturePersistentState(
            Func<PlayerSetupData, string> playerId) =>
            AiPersistenceCollections.Capture(ByPlayer, playerId, x => x.CapturePersistentState());
        internal static void RestorePersistentState(
            IReadOnlyDictionary<string, IReadOnlyDictionary<StableMissionKey, MissionCooldownInfo>> state,
            Func<string, PlayerSetupData> player) =>
            AiPersistenceCollections.Restore(ByPlayer, state, player, AiAllocatorState.RestorePersistentState);
    }
}
