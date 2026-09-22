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
    //      EXCEPT when `requiresStealth` — the executor (ReconGroundExecutor, review fix be521ef6)
    //      never lets a stealth-required mission actually take a structure over (that would force
    //      an unplanned decloak), so this rule must not unblock the hex for the planner either:
    //      doing so would let a stealth-required step pick a destination the executor is
    //      guaranteed to then reject outright, wasting the whole step (the exact class of bug this
    //      file exists to prevent — see the header comment above). The undefended-structure
    //      carve-out therefore only fires when stealth is not required, matching what the executor
    //      will actually permit rather than merely resembling it.
    // ===========================================================================================
    public static class ScoutExecutionSafety
    {
        // LIVE check — ProvisioningManager / TaskExecutor. `requiresStealth` must reflect the SAME
        // fact the executor gates the actual capture permission on (ProvisionedMission.
        // RequiresStealth for Explore/Refresh; always true for a Surveil call site — Surveil is
        // always StealthRequirement.Required, see ARCHITECTURE.md's Scout row).
        public static bool VantageBlockedNow(PlayerSetupData player, HexCoord hex, int currentTurn,
            bool requiresStealth)
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
                && (requiresStealth || !AiMapMemory.KnownUndefendedForeignStructureAt(player, hex)))
                return true;

            return false;
        }
    }
}
