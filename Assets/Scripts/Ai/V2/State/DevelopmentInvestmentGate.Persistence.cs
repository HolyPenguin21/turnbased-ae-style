using System;
using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Players;
using Game.Map;

namespace Game.Ai.V2
{
    internal static partial class DevelopmentInvestmentGate
    {
        internal static IReadOnlyDictionary<string, (int LastTurn, IReadOnlyDictionary<Game.Economy.ResourceType, int> Streak, ResourceBundle Surplus)>
            CapturePersistentState(Func<PlayerSetupData, string> playerId) =>
            AiPersistenceCollections.Capture(ByPlayer, playerId, x =>
                (x.LastTurn, AiPersistenceCollections.Freeze(x.Streak), x.Surplus));
        internal static void RestorePersistentState(
            IReadOnlyDictionary<string, (int LastTurn, IReadOnlyDictionary<Game.Economy.ResourceType, int> Streak, ResourceBundle Surplus)> state,
            Func<string, PlayerSetupData> player) => AiPersistenceCollections.Restore(ByPlayer, state, player, x =>
            {
                var restored = new State { LastTurn = x.LastTurn, Surplus = x.Surplus };
                foreach (var row in x.Streak) restored.Streak.Add(row.Key, row.Value);
                return restored;
            });
    }
}
