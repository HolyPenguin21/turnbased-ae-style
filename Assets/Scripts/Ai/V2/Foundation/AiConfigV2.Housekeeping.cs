namespace Game.Ai.V2
{
    // Part of AiConfigV2 (file-split Task 2, see Docs/ai-v2-file-split-refactor-tasks.md).
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
        // A garrison donor in the zero-AP reorg pass must leave the garrison with at least this
        // much EffectivePower, ON TOP OF the non-hero headcount floor — so Housekeeping can never
        // strip a strong Citadel to prop up a weak field army. (The reorg pass also no longer uses
        // the garrison as a seed donor for a purposeless shell at all — this is defence in depth
        // for the benched-hero lending paths and smaller second-base garrisons.)
        public const float housekeepingGarrisonReservePower = 20f;
        // Fewer than this many friendly containers (garrison + field armies) on one hex -> there
        // is nothing to reorganise, the hex is skipped.
        public const int housekeepingMinContainersForGroup = 2;
        // Hard bound on the planner's greedy best-improvement loop per hex. Each iteration applies
        // at most one accepted structural move; the loop stops early the moment no move improves
        // the lexicographic outcome.
        public const int housekeepingMaxPlanIterationsPerHex = 24;
        // Canonical BaseCapacity / GarrisonBaseCapacity from Game.Map.ArmyData.ComputeCapacity,
        // mirrored here so the pure planner can size virtual rosters without a live ArmyData.
        // Keep in step with ArmyData if those ever change.
        public const int armyBaseCapacityNoHero = 2;
        public const int garrisonBaseCapacityNoHero = 4;

        // --- Hero operational-role model (spec §8). A combat-leadership score from canonical hero
        //     data only: CommandRating (how large a force it can lead) plus the hero's own
        //     AiPower.ToPowerUnit contribution, scaled by movement fitness against the canonical
        //     MobileCombat MoveMax line (HitPoints / Initiative / Resistance / Fate —
        //     heroes carry NO Attack/Defense). Never card or display names. A hero carrying a
        //     Researcher/Assembler/ApBonus support vocation (or too slow to keep pace with a field
        //     force at all — see homeHeroMoveMaxThreshold) whose score is below
        //     heroRoleFlexibleCombatFloor is a SupportOperator (preserve it for base/research/
        //     production/Economy duty); a non-support
        //     hero at or above heroRoleCombatLeaderFloor is a CombatLeader; everything else is
        //     Flexible. The classification is a PREFERENCE, never an absolute bar — an urgent raid
        //     may still take a SupportOperator.
        public const float heroRoleCommandWeight = 1.0f;
        public const float heroRoleCombatContributionWeight = 0.6f;
        public const float heroRoleCombatLeaderFloor = 7f;
        public const float heroRoleFlexibleCombatFloor = 8f;
        // A hero this slow is a "home" hero by design regardless of ability tags — it cannot keep
        // pace with a field force (see mobileCombatMoveMax=5) and is worth more standing garrison
        // duty. Read only by HeroRoleEvaluator.HasSupportVocation.
        public const int homeHeroMoveMaxThreshold = 2;

    }
}
