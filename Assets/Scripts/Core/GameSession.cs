using System.Collections.Generic;
using Game.Map;
using Game.Players;

namespace Game.Core
{
    // Carries the configured players from the main menu's setup panel into the Game scene.
    // Plain static holder — simplest way to pass data across a scene load without needing a
    // DontDestroyOnLoad object.
    public static class GameSession
    {
        public static List<PlayerSetupData> Players { get; set; } = new List<PlayerSetupData>();

        // The one local human player, if any — every AI/Neutral slot is never IsHuman. Was
        // reimplemented independently as `GameSession.Players?.Find(p => p != null &&
        // p.IsHuman)` in several unrelated files; centralized here since they're all reading the
        // exact same list, not just coincidentally-similar code.
        public static PlayerSetupData FindHumanPlayer() => Players?.Find(p => p != null && p.IsHuman);

        // The human's PlayerRoot (action points/resources) — null if there's no human player yet
        // (e.g. before setup finishes) or no PlayerRoot registered for them yet.
        public static PlayerRoot FindHumanRoot()
        {
            PlayerSetupData human = FindHumanPlayer();
            return human != null ? PlayerRootRegistry.FindFor(human) : null;
        }
    }
}
