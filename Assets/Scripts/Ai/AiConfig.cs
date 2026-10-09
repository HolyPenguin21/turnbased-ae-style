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
        // How many turns an unrefreshed enemy-army sighting stays in AiMapMemory before it expires.
        public const int enemySightingMemoryTurns = 2;

        // A solo Recce flees / hides when a known non-neutral sighting is within this many hexes of
        // its current or next hex (AiScoutStealthPolicy, SafeStepPathing).
        public const int scoutFleeRadius = 2;

        // Aggression / raid physical gates (WorthIt-backed viability, used by the V2 raid lane).
        public const float aggressionBaseWeight = 100f;
        public const int raidThreatRadius = 2;

        // Siege geometry + minimum garrison bodies. (Renamed from the
        // old defence* naming: these are shared Analysis/Economy facts, not a Defence lane.)
        public const int siegeRadius = 4;
        // The minimum win chance an Economy escort must project before it is considered safe.
        public const float economyEscortMinWinChance = 0.6f;
        public const int secureBaseMinNonHeroUnits = 2;
        // Garrison defence floor (user decision 2026-09-30): the non-hero power a garrison keeps is
        // this share of the player's whole ground force (map + hand + deck, aviation excluded —
        // PlayerForceAnalysis' additive scale); at least one body always stays. Read by
        // AiArmyRoles.CanSpareGarrisonMembers (every V2 donor), Housekeeping and held-base demand.
        public const float garrisonDefenceShareCitadel = 0.10f;
        public const float garrisonDefenceShareBase = 0.05f;

        // Economy / management physical capacity.
        public const int garrisonReservedSlots = 1;

        // Aviation execution knobs (AviationSupport, AirStrike/AirRecon in the V2 recon-air lane).
        public const int airReconTargetCooldownTurns = 3;
        public const float airStrikeContinuationScore = aggressionBaseWeight + 15f;

    }
}
