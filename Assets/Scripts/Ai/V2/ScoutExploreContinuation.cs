using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // Live Explore continuation after the turn-start strategic focus has been satisfied. Unlike the
    // old adjacent-only follow-through, every call rebuilds the honest frontier from CURRENT vision
    // and memory and may choose a different sector after the scout's previous movement revealed it.
    // This is intentionally recon-local: ordinary fog reveal does not rerun Radar/Demand/Economy.
    internal static class ScoutExploreContinuation
    {
        public static HexCoord? Pick(PlayerSetupData player, HexMap map, ArmyData army, int turn)
        {
            if (player == null || map == null || army == null || army.CurrentMovement <= 0)
                return null;

            bool hidden = army.Members.Any(m => m.IsHidden);
            List<HexCoord> candidates = map.AllCoords
                .Where(h => !VisionSystem.IsVisited(player, h))
                .Where(h => IsLiveFrontier(player, map, h))
                .Where(h => !AiMapMemory.IsScoutDangerous(player, h))
                .Where(h => !ScoutExecutionSafety.VantageBlockedNow(player, h, turn))
                .Where(h => hidden || !KnownEnemyExposure(player, h))
                .Where(h => VisitHexTask.FindNextSafeStep(map, army, h).HasValue)
                // Prefer a frontier the already-activated scout can actually reach with this turn's
                // remaining MP, then maximize fresh information and minimize travel. Deterministic
                // coordinates keep equal-value choices stable and prevent oscillation.
                .OrderBy(h => HexGridMath.Distance(army.Hex, h) <= army.CurrentMovement ? 0 : 1)
                .ThenByDescending(h => FreshNeighborCount(player, map, h))
                .ThenBy(h => HexGridMath.Distance(army.Hex, h))
                .ThenBy(h => h.Q)
                .ThenBy(h => h.R)
                .ToList();

            return candidates.Count > 0 ? candidates[0] : (HexCoord?)null;
        }

        private static bool IsLiveFrontier(PlayerSetupData player, HexMap map, HexCoord hex)
        {
            foreach (HexCoord n in HexGridMath.Neighbors(hex))
                if (map.TryGetTerrainAt(n, out _) && VisionSystem.IsVisited(player, n))
                    return true;
            return false;
        }

        private static bool KnownEnemyExposure(PlayerSetupData player, HexCoord hex)
        {
            int r = AiConfigV2.frontierEnemyExposureRadius;
            foreach (AiMapMemory.KnownEnemySighting s in AiMapMemory.AllKnownEnemySightings(player))
            {
                if (s.Owner != null && s.Owner.IsNeutral)
                    continue;
                if (HexGridMath.Distance(s.Hex, hex) <= r)
                    return true;
            }
            return false;
        }

        private static int FreshNeighborCount(PlayerSetupData player, HexMap map, HexCoord center)
        {
            int n = 0;
            foreach (HexCoord h in HexGridMath.Neighbors(center))
                if (map.TryGetTerrainAt(h, out _)
                    && !VisionSystem.IsVisited(player, h)
                    && !AiMapMemory.IsScoutDangerous(player, h))
                    n++;
            return n;
        }
    }
}