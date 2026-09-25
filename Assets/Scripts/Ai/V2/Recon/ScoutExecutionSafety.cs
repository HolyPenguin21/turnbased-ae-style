using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  SCOUT EXECUTION SAFETY  (Strategy V2 build-order step 6b)
    // ===========================================================================================
    // The ONE Recon rule for "may a scout step onto / stand on this hex" — every per-step Recon
    // choice (ReconGroundStepPlanner, ReconReactionPolicy) and every live vantage check
    // (ProvisioningManager, MissionRevalidator) asks this, so a Recon choice can never be one the
    // execution gate (AiTurnController.MoveArmyRoutine) then rejects — the class of bug that
    // strands a scout re-picking the same refused hex every turn.
    //
    // A hex is BLOCKED for a scout when:
    //    * it lies in an active scout-danger cooldown zone (a recently fled-from area), or
    //    * arriving there would set something off — AiMapMemory.KnownGroundArrival, the single
    //      AI arrival rule the execution gate itself uses: a known army fights a visible scout,
    //      a known undefended foreign structure is taken over by it. A FULLY hidden scout sets
    //      neither off and may share the hex (stealth design). A scout never seeks either.
    // Hex Events are not blocks: a Transit scout always Skips them and keeps stealth/movement.
    // `moverFullyHidden` is the mover's own stealth state as it will move (a planned stealth
    // entry counts: callers pass requiresStealth for a mission that enters stealth first).
    // ===========================================================================================
    public static class ScoutExecutionSafety
    {
        public static bool StepBlocked(PlayerSetupData player, HexCoord hex, bool moverFullyHidden)
            => AiMapMemory.IsScoutDangerous(player, hex)
               || AiMapMemory.KnownGroundArrival(player, hex, moverFullyHidden).HasOutcome;

        public static bool StepBlocked(PlayerSetupData player, ArmyData mover, HexCoord hex)
            => StepBlocked(player, hex, StealthSystem.IsArmyFullyHidden(mover));

        // LIVE vantage check — ProvisioningManager / MissionRevalidator. A Surveil vantage is a
        // hex the scout must physically reach and stand on, so it is exactly StepBlocked.
        // `moverArrivesHidden` — ScoutMoverSelector.ArrivesHiddenLive, never the mission's bare
        // stealth wish: the step gate judges the scout's real hidden state.
        public static bool VantageBlockedNow(PlayerSetupData player, HexCoord hex, int currentTurn,
            bool moverArrivesHidden)
            => StepBlocked(player, hex, moverArrivesHidden);
    }
}
