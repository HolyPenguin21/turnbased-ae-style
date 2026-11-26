namespace Game.Ai.V2
{
    // Part of AiConfigV2 (one partial file per topic).
    // Housekeeping manager.
    public static partial class AiConfigV2
    {
        // =======================================================================================
        //  HOUSEKEEPING MANAGER  (Strategy V2 build-order step 8C)
        //  The OFF-BUDGET late-turn local army/garrison reorganisation pass. It runs AFTER
        //  Strategic Manager Phase B + the final operational refresh and does deterministic,
        //  same-hex structural cleanup only — never movement, never a mission, never card play,
        //  never Equipment. It reduces the number of pointless occupied formations (non-exempt
        //  singletons, non-viable weak armies) while preserving mission ownership, gameplay
        //  legality, garrison safety, and reusable empty ArmyData shells. See the Step 8C design
        //  record for the full ownership boundaries and the lexicographic policy.
        // =======================================================================================
        // Σ of member AiPower.UnitPower for an occupied ground field army below this reads as
        // "non-viable" — a conservative structural floor, NOT a battle prediction. A singleton
        // (one non-hero member) and a lone hero are non-viable regardless of this number.
        public const float housekeepingViabilityPowerFloor = 6f;
        // Hard bound on the planner's greedy best-improvement loop per hex. Each iteration applies
        // at most one accepted structural move; the loop stops early the moment no move improves
        // the lexicographic outcome.
        public const int housekeepingMaxPlanIterationsPerHex = 24;
        // The night fold's whole-hex hold (HousekeepingNightFold.ThreatRowsOf): trials of
        // WorthIt.EstimateHexControl, and the attacker-win difference below which two layouts count as
        // equal (about one standard deviation at 50 trials; at 25 trials a real gain could drown in noise).
        public const int housekeepingHexHoldTrials = 50;
        public const float housekeepingHexHoldMargin = 0.06f;

        // --- Hero operational-role model (spec §8). A combat-leadership score from canonical hero
        //     data only: CommandRating (how large a force it can lead) plus the hero's own
        //     AiPower.ToPowerUnit contribution, scaled by movement fitness against the canonical
        //     MobileCombat MoveMax line (HitPoints / Initiative / Fate —
        //     heroes carry NO Attack/Defense). Never card or display names. A hero carrying a
        //     Researcher/Assembler/ApBonus support vocation (or too slow to keep pace with a field
        //     force at all — see homeHeroMoveMaxThreshold) whose score is below
        //     heroRoleFlexibleCombatFloor is a SupportOperator (preserve it for base/research/
        //     production/Economy duty); every other hero is Flexible (2026-11-25, owner: the
        //     CombatLeader tier is removed - no hero could reach it). The classification is a
        //     PREFERENCE, never an absolute bar — an urgent raid may still take a SupportOperator.
        public const float heroRoleCommandWeight = 1.0f;
        public const float heroRoleCombatContributionWeight = 0.6f;
        public const float heroRoleFlexibleCombatFloor = 8f;
        // A hero this slow is a "home" hero by design regardless of ability tags — it cannot keep
        // pace with a field force (see mobileCombatMoveMax=5) and is worth more standing garrison
        // duty. Read only by HeroRoleEvaluator.HasSupportVocation.
        public const int homeHeroMoveMaxThreshold = 2;

    }
}
