namespace Game.Ai.V2
{
    // Part of AiConfigV2 (file-split Task 2, see Docs/ai-v2-file-split-refactor-tasks.md).
    // Aggression / Raid — desire sub-block + momentum, and the Aggression/Raid objective/mission lane.
    public static partial class AiConfigV2
    {
        public const int raidRecoveryMaxWaitTurns = 2;
        public const float raidRepairMinWinChanceGain = 0.001f;
        // How many turns old a neutral sighting may be and still authorize an air strike (project
        // owner's own 2026-09-21 call: an exact-this-turn-only requirement made AirSupport fire
        // almost exclusively on first contact — a small window lets it fire on a routine re-scout
        // too, at the cost of striking a roster that may be up to this many turns stale).
        public const int raidAirSupportSightingMaxAgeTurns = 2;
        // ---- Aggression (single axis; two internal drivers, max()'d) -----------------------
        //  raidOpportunity — "there is a profitable target I can take right now" (from the shared
        //                    CombatOpportunityAnalyzer — same estimator MissionLayer/Provisioning
        //                    will use, never a throwaway aggression-only one).
        //  warPressure     — "built out, economy is fine, time to commit to breaking the main
        //                    opponent" (potential saturation + security-optional force + eco).
        //  rawAggression = max(raidOpportunity, warPressure) * (UnderSiege ? aggSiegeDamp : 1).
        public const float aggRelEdgeRampLo = 0.80f;
        public const float aggRelEdgeRampHi = 2.20f;
        public const float aggRelEdgeNoIntel = 0.50f;   // "haven't seen them" != "I'm winning"
        public const float aggPotentialSatRampLo = 0.60f;
        public const float aggPotentialSatRampHi = 0.95f;
        public const float aggSurplusRampLo = 0.10f;
        public const float aggSurplusRampHi = 0.60f;
        // RequiredDefensiveReserve = Σ over threatened Citadel/Base/Facility assets of
        //   strongestThreateningContact.EffectiveArmyPower * aggDefenceConfidenceMargin,
        //   floored at aggHomeGuardFloor. OffensiveFreePower = max(0, TotalPower - reserve).
        public const float aggDefenceConfidenceMargin = 1.30f;
        public const float aggHomeGuardFloor = 3f;
        public const float aggEcoGateLo = 0.50f;        // ecoGate = Lerp(this, 1, EconomicSecurity)
        public const float aggSiegeDamp = 0.20f;
        public const float aggRaidOppWeightOpportunity = 0.50f;
        public const float aggRaidOppWeightSurplus = 0.20f;
        public const float aggRaidOppWeightRelEdge = 0.15f;
        public const float aggRaidOppWeightMomentum = 0.15f;
        public const float aggWarWeightPotentialSat = 0.45f;
        public const float aggWarWeightSurplus = 0.25f;
        public const float aggWarWeightEcoGate = 0.20f;
        public const float aggWarWeightRelEdge = 0.10f;
        // ---- momentum (transient, both sides) ---------------------------------------------
        //  momentum = Clamp01(0.5 + 0.5*enemyLossPulse - 0.5*ownLossPulse); 0.5 = neutral.
        //  Each pulse is a decaying spike off a same-turn strength drop. Enemy losses are
        //  OBSERVED-ONLY: matched contact-to-contact by owner within enemyLossMatchRadius, so a
        //  force merely walking out of vision (contact vanishes) contributes nothing. Own losses
        //  need no such guard (no fog on ourselves; TotalPower only moves on real roster change).
        public const float lossPulseDecay = 0.60f;
        public const float lossPulseRampLo = 0.12f;
        public const float lossPulseRampHi = 0.50f;
        public const int enemyLossMatchRadius = 3;
        // =======================================================================================
        //  AGGRESSION / RAID  (Strategy V2 build-order step 9 — the second objective/mission lane)
        //  Raid is the first Aggression Objective type. Objective discovery reuses the shared
        //  CombatOpportunityAnalyzer (snapshot tier); the numbers below only shape Raid-LOCAL
        //  admission ordering, the resource envelope and the assembly/continuity guards. Cross-lane
        //  ordering stays on BaseValue, AP budget stays on the radar / AxisBudgetLedger.
        // =======================================================================================
        // A known neutral target becomes a Raid AggressionObjective only if its canonical TaskScore
        // has some real strategic merit. This threshold is on the unified TaskScore scale (where
        // MilitaryTargetRelevance tops out at 12), not the retired Raid-local 12..90 Lerp scale.
        // Feasibility is still NOT part of this discovery gate: an objective may survive so Demand
        // can ask for the missing combat capability; WorthIt/assembly remain the execution owners.
        public const float raidObjectiveMinBaseValue = 0.25f;
        // N — how many Raid alternatives AggressionMissionPlanner hands downstream (beam width).
        // Execution capacity is bounded by real armies / heroes / commitments / resources, NOT a
        // fixed K (spec §20), so there is no maxConcurrentRaidExecutions.
        public const int raidCandidateBeamWidth = 4;
        // Raid execution AP envelope (activation of the raid mover only; movement is MP, not AP).
        public const float raidNotionalActivationAp = 1f;
        public const float raidActivationApMax = 3f;
        // Structural requirement projection: a raid roster must clear this Monte-Carlo win chance
        // (parity with V1 AiConfig.raidMinimumWinChance / opportunityMinViableWinChance).
        public const float raidMinViableWinChance = 0.65f;
        // Perf pre-filter (2026-09-20, project owner's own call — profiler frame showed
        // GroundCombatAdmissionRegistry.Record()/GroundCombatAssemblyPlanner.Plan() re-running the
        // 25-trial Monte-Carlo WinChance O(readyArmies^2) times per Raid target, per settled step).
        // Below this attackerPower/defenderPower ratio (same aggregate Attack+Defense+HP+0.25*
        // Initiative sum GroundCombatFeasibility.Clears already had the ingredients for), Monte
        // Carlo is skipped outright and the matchup is treated as not clearing raidMinViableWinChance
        // without ever running it. Calibrated against a real playthrough's AiDebug.log
        // ([AI][V2][CALIBRATION] lines, since removed): across 2644 real matchups, the lowest ratio
        // that ever produced win >= 0.65 was 0.57, and every one of the 869 matchups below that
        // topped out at win 0.24. This constant sits well under that observed floor (0.57) to leave
        // margin for roster compositions this one playthrough didn't happen to hit — raise it only
        // after a fresh calibration pass confirms the floor is still safely above it.
        public const float raidPowerRatioPreFilter = 0.40f;
        // CombatPower the requirement projection asks for when no ready force clears the target:
        // the target's own EffectiveArmyPower times this margin.
        public const float raidCombatPowerMargin = 1.25f;
        // Continuity: a started Raid intent is reaped after this many stalled turns / absolute
        // turns, same shape as the shared commitment* caps but a touch more patient (assembly +
        // travel is slower than a scout leg).
        public const int raidIntentStallTurns = 3;
        public const int raidIntentMaxTurns = 10;
        // Active Defence may borrow the exact primary actor of a Raid only for a one-turn detour.
        public const int activeDefenceRaidMaxDetourTurns = 1;
        // Structural-failure cooldown for a Raid mission key (assembly infeasible / no mover).
        public const int raidRejectCooldownTurns = 3;

    }
}
