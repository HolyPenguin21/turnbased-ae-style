using System.Collections.Generic;
using Game.Economy;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // One concise resource delta per V2 turn. CaptureStart is deliberately idempotent for a
    // player+turn so additional strategic scans/replans cannot overwrite the real beginning state.
    internal static class TurnResourceTelemetry
    {
        private sealed class StartState
        {
            public int Turn;
            public int Ap;
            public int Human;
            public int Energy;
            public int Materials;
            public int Tech;
        }

        private static readonly Dictionary<PlayerSetupData, StartState> Starts =
            new Dictionary<PlayerSetupData, StartState>();

        public static void CaptureStart(PlayerSetupData player, PlayerRoot root, int turn)
        {
            if (player == null || root == null)
                return;
            if (Starts.TryGetValue(player, out StartState current) && current.Turn == turn)
                return;

            Starts[player] = new StartState
            {
                Turn = turn,
                Ap = root.ActionPoints,
                Human = root.GetResource(ResourceType.Human),
                Energy = root.GetResource(ResourceType.Energy),
                Materials = root.GetResource(ResourceType.Materials),
                Tech = root.GetResource(ResourceType.Tech),
            };
        }

        public static void LogEnd(PlayerSetupData player, PlayerRoot root, int turn)
        {
            if (player == null || root == null)
                return;
            if (!Starts.TryGetValue(player, out StartState start) || start.Turn != turn)
            {
                AiDebugLog.Write($"[AI][V2] {player.Nickname}: resources — start unavailable; "
                    + $"ap={root.ActionPoints}, h={root.GetResource(ResourceType.Human)}, "
                    + $"e={root.GetResource(ResourceType.Energy)}, m={root.GetResource(ResourceType.Materials)}, "
                    + $"t={root.GetResource(ResourceType.Tech)}");
                return;
            }

            AiDebugLog.Write($"[AI][V2] {player.Nickname}: resources — "
                + $"ap={start.Ap}>{root.ActionPoints}, "
                + $"h={start.Human}>{root.GetResource(ResourceType.Human)}, "
                + $"e={start.Energy}>{root.GetResource(ResourceType.Energy)}, "
                + $"m={start.Materials}>{root.GetResource(ResourceType.Materials)}, "
                + $"t={start.Tech}>{root.GetResource(ResourceType.Tech)}");
            Starts.Remove(player);
        }
    }
}
