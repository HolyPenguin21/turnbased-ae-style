using System;
using Game.Core;
using Game.Map;
using UnityEngine;
namespace Game.Setup
{
    internal static class MatchSceneAssembly
    {
        internal static void ConfigureWorld(GameConfig config, HexMap map)
        {
            if (config == null || map == null) throw new InvalidOperationException("Match configuration or map is missing.");
            VisionSystem.Configure(config); MapObservationSystem.Configure(map);
            StealthSystem.TerrainMoveCostProvider = hex => map.TryGetTerrainAt(hex, out var terrain)
                && terrain != null ? Mathf.Max(1, terrain.moveCost) : 1;
            Game.Ai.AiMapMemory.EnsureSubscribed(map);
        }
        internal static void FailReconstruction(Exception exception, Game.Turns.GameTurnController turns)
        {
            MatchStateInventory.EndMatch(turns);
            GameSession.SetupError = "Could not restore the saved match: " + exception.Message;
        }
    }
}
