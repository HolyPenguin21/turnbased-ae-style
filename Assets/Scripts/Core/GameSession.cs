using System.Collections.Generic;
using Game.Players;

namespace Game.Core
{
    // Carries the configured players from the main menu's setup panel into the Game scene.
    // Plain static holder — simplest way to pass data across a scene load without needing a
    // DontDestroyOnLoad object.
    public static class GameSession
    {
        public static List<PlayerSetupData> Players { get; set; } = new List<PlayerSetupData>();
    }
}
