namespace Game.Ai
{
    // Tunable-numbers holder for the shared AI-support code that survived the V1 removal
    // (ARCH-01). Every value here is still read by Strategy V2 or by the physical rules extracted
    // out of the former V1 planners; the ~1.7k lines of V1-only weights/thresholds that used to
    // sit alongside them are gone. Strategy V2's own tuning lives in AiConfigV2.
    //
    // Plain static const class (no serialized .asset) — retune by editing this file only.
    public static class AiConfig
    {
        // Reconnaissance — VisitHex concurrency + stall watchdog (AiScoutPlanner's V2 heirs, and
        // SafeStepPathing's callers).
        public const int maxConcurrentVisitHex = 2;
        public const int visitHexStallTurns = 2;

        // How many turns an unrefreshed enemy-army sighting stays in AiMapMemory before it expires.
        public const int enemySightingMemoryTurns = 2;

        // A solo Recce flees / hides when a known non-neutral sighting is within this many hexes of
        // its current or next hex (AiScoutStealthPolicy, SafeStepPathing).
        public const int scoutFleeRadius = 2;

        // Aggression / raid physical gates (WorthIt-backed viability, used by the V2 raid lane).
        public const float aggressionBaseWeight = 100f;
        public const float raidMinimumWinChance = 0.65f;
        public const int raidThreatRadius = 2;
        public const int raidTargetMaxDefenders = 4;
        public const int raidAssembleMaxTurns = 6;
        public const int raidPlanRejectCooldownTurns = 3;

        // Defence — reaction radius + siege/patrol geometry + minimum garrison bodies.
        public const int defenceReactionRadius = 5;
        public const int siegeRadius = 4;
        public const int patrolRadius = 5;
        public const float defenceActiveWinChance = 0.6f;
        public const int secureBaseMinNonHeroUnits = 2;
        public const int secureCitadelMinNonHeroUnits = 2;

        // "scout/raid-shaped" force size ceiling for a makeshift threat read.
        public const int makeshiftScoutMinMembers = 3; // hero + 2

        // Economy / management physical capacity.
        public const int garrisonReservedSlots = 1;
        public const int neutralBuildTriggerRadius = 1;

        // BuildBase wait ceiling (turns) before a stalled founder task frees its army.
        public const int buildBaseMaxWaitTurns = 5;

        // Aviation execution knobs (AviationSupport, AirStrike/AirRecon in the V2 recon-air lane).
        public const int aviationLaunchMinReadyAircraft = 1;
        public const int maxStrikesPerSortie = 2;
        public const int airReconTargetCooldownTurns = 3;
        public const float airStrikeContinuationScore = aggressionBaseWeight + 15f;

        // Operations directive boost + development success floor (still read by V2).
        public const float operationDirectiveBoost = 16f;
        public const float developmentMinSuccessChance = 0.45f;

        // Defence competitive scores still referenced by kept code paths.
        public const float defenceActiveAssemblyScore = 120f;
        public const float defencePreemptScore = 130f;
    }
}
