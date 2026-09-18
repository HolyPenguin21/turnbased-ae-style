using System.Collections.Generic;
using Game.Map;
using Game.Players;
using Game.Terrain;

namespace Game.Core
{
    // Carries the configured players (and now map size/biome) from the main menu's setup panel
    // into the Game scene. Plain static holder — simplest way to pass data across a scene load
    // without needing a DontDestroyOnLoad object.
    public static class GameSession
    {
        public static List<PlayerSetupData> Players { get; set; } = new List<PlayerSetupData>();

        // Null until the pre-game setup panel actually sets them (GameSetupController.
        // OnStartGameClicked) — nullable rather than defaulting outright so HexMapGenerator/
        // FogOfWarController can tell "no session choice made" (editor-time generation, a scene
        // opened directly) apart from an explicit choice, and fall back to MapGenerationSettings'
        // own design-time radius/Arid default instead.
        public static MapSize? SelectedMapSize { get; set; }
        public static Biome? SelectedBiome { get; set; }

        // fallbackRadius is MapGenerationSettings.radius — its own design-time default.
        public static int ResolveMapRadius(int fallbackRadius) =>
            SelectedMapSize.HasValue ? (int)SelectedMapSize.Value : fallbackRadius;

        public static Biome ResolveBiome() => SelectedBiome ?? Biome.Arid;

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
