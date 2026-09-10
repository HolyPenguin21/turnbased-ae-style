using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Ai
{
    // Layer-neutral scout-movement support (not V1, not V2 — both call it). Answers: given an
    // army heading for a target, what is the next hex it should step to while routing AROUND
    // remembered (fog-of-war-honest) enemy/neutral sightings and still-cooling scout-danger
    // hexes? Extracted verbatim from the former Game.Ai.VisitHexTask (ARCH-01, 2026-09-04).
    //
    // Only a REMEMBERED army on the path is blocked (AiMapMemory.KnownEnemySightingAt — honest,
    // fog-of-war-respecting, enemy AND neutral alike), never fog itself: the whole point of a
    // scout is to walk into unseen ground. `targetHex` is exempt regardless — the caller already
    // refuses to pick a destination with a known sighting on or near it; this only guards the
    // hexes along the WAY. Null when no such route exists yet — treat as "nothing to do this
    // step", not a reason to abandon the target.
    public static class SafeStepPathing
    {
        public static HexCoord? FindNextSafeStep(HexMap map, ArmyData army, HexCoord targetHex)
        {
            if (map == null || army == null)
                return null;
            // Routed through the shared AiTurnController.FindAffordableStep — this path (blocked
            // around known sightings) can differ from an unblocked one, so THIS is the path whose
            // first step must be checked against army.CurrentMovement.
            return AiTurnController.FindAffordableStep(map, army, targetHex,
                SafeRouteBlocker(army, targetHex));
        }

        // Canonical cost of the same fog-honest route FindNextSafeStep executes. Analysis freezes
        // this witness into Economy opportunities; Provisioning re-runs it live immediately before
        // binding. Keeping the blocker here prevents planning/execution from drifting.
        public static int FindSafePathCost(HexMap map, ArmyData army, HexCoord targetHex)
        {
            if (map == null || army == null)
                return int.MaxValue;
            return FindSafePathCost(map, army.Owner, army.Hex, targetHex);
        }

        // Same canonical blocker for a projected leg whose mover is not physically standing at
        // `from` yet (Economy uses it for the post-build return leg). This keeps outbound and
        // return costing on the exact route policy execution already uses.
        public static int FindSafePathCost(HexMap map, PlayerSetupData owner,
            HexCoord from, HexCoord targetHex)
        {
            if (map == null || owner == null)
                return int.MaxValue;
            HexPath path = HexPathfinder.FindPath(map, from, targetHex,
                blockHex: SafeRouteBlocker(owner, targetHex));
            return path?.TotalCost ?? int.MaxValue;
        }

        private static System.Func<HexCoord, bool> SafeRouteBlocker(
            ArmyData army, HexCoord targetHex) => SafeRouteBlocker(army.Owner, targetHex);

        private static System.Func<HexCoord, bool> SafeRouteBlocker(
            PlayerSetupData owner, HexCoord targetHex) => hex => !hex.Equals(targetHex)
                && (AiMapMemory.KnownEnemySightingAt(owner, hex).HasValue
                    || AiMapMemory.IsScoutDangerous(owner, hex));
    }
}
