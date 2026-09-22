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
    //    * a known FOREIGN-OWNED building that is NOT known to be standing undefended. Recon
    //      still owns information, not territorial capture: nothing here makes a structure
    //      attractive (ReconGroundStepPlanner's building bonus stays 0, so no scout ever bends
    //      off its route toward one). FIX-07 only stops this rule from FORBIDDING a hex a scout
    //      would already be stepping onto for information reasons, when knowledge says the
    //      structure has nobody holding it — arriving there then resolves through the existing
    //      authoritative BuildingRegistry.CaptureOrDestroyIfUndefended as a side effect of that
    //      move. A defended structure, and one whose defence we do not know, stay blocked.
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
            if (b.HasValue && b.Value.Owner != null && b.Value.Owner != player
                && !AiMapMemory.KnownUndefendedForeignStructureAt(player, hex))
                return true;

            return false;
        }
    }
}
