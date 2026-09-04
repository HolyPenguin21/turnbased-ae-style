using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  SCOUT EXECUTION SAFETY  (Strategy V2 build-order step 6b)
    // ===========================================================================================
    //  The ONE rule for "can a Surveil scout stand on this hex". SurveilVantageSelector applies it
    //  against the snapshot; ProvisioningManager and TaskExecutor apply it against LIVE memory.
    //  Keeping the rule in one place stops the class of bug where the selector says "acceptable
    //  risk, vantage allowed" and the executor says "invalid target" — which strands a scout that
    //  never moves at all.
    //
    //  A vantage is BLOCKED when it is CURRENTLY occupied by something a scout cannot / must not
    //  share:
    //    * a currently-known NON-NEUTRAL force standing on it. A STALE last-known enemy position is
    //      NOT a block — deliberately closing on one is what Surveil is for; it only raises risk.
    //    * any known NEUTRAL army on it — a scout never fights.
    //    * a known FOREIGN-OWNED building on it — walking a ground army onto one auto-runs
    //      BuildingRegistry.CaptureOrDestroyIfUndefended, turning a recon mission into an
    //      aggression action. An OWN building is fine. An UNKNOWN building in fog is NOT excluded
    //      (that would be a cheat); the executor's ordinary post-step contact handling covers it.
    // ===========================================================================================
    public static class ScoutExecutionSafety
    {
        // LIVE check — ProvisioningManager / TaskExecutor.
        public static bool VantageBlockedNow(PlayerSetupData player, HexCoord hex, int currentTurn)
        {
            AiMapMemory.KnownEnemySighting? s = AiMapMemory.KnownEnemySightingAt(player, hex);
            if (s.HasValue)
            {
                bool neutral = s.Value.Owner != null && s.Value.Owner.IsNeutral;
                bool current = s.Value.SeenTurn >= currentTurn || VisionSystem.IsVisible(player, hex);
                if (neutral || current)
                    return true;
            }

            AiMapMemory.KnownBuilding? b = AiMapMemory.KnownBuildingAt(player, hex);
            if (b.HasValue && b.Value.Owner != player)
                return true;

            return false;
        }
    }
}
