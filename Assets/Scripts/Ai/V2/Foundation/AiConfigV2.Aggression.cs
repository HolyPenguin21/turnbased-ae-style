namespace Game.Ai.V2
{
    // Part of AiConfigV2 (file-split Task 2, see Docs/ai-v2-file-split-refactor-tasks.md).
    // Aggression / Raid — desire sub-block + momentum, and the Aggression/Raid objective/mission lane.
    public static partial class AiConfigV2
    {
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
        // A known enemy/neutral army sighting becomes a Raid AggressionObjective only if its
        // intrinsic strategic merit clears this. Merit is target value + a small closeness term —
        // NOT feasibility (a target we cannot beat yet still produces an objective so the Demand
        // layer can ask for the missing combat capability; see spec §11).
        public const float raidObjectiveMinBaseValue = 8f;
        // Raid BaseValue = Lerp(min, max, quality); quality blends target value and base-proximity.
        public const float raidBaseValueMin = 12f;
        public const float raidBaseValueMax = 90f;
        public const float raidValueWeight = 0.75f;
        public const float raidProximityWeight = 0.25f;
        public const int raidProximityRampLo = 3;   // target this close to a base -> proximity term 1
        public const int raidProximityRampHi = 16;  // this far or more -> proximity term 0
        // LocalAdmissionScore = BaseValue * AggRaidOpportunity sub-driver * a feasibility factor
        // (Lerp(floor, 1, assemblableWinChance)). Ranks Raid alternatives WITHIN the Aggression lane
        // only; never re-applies the whole Aggression radar weight (spec §10).
        public const float raidLocalFeasibilityFloor = 0.25f;
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
        // CombatPower the requirement projection asks for when no ready force clears the target:
        // the target's own EffectiveArmyPower times this margin.
        public const float raidCombatPowerMargin = 1.25f;
        // A target with more known defenders than this is an army-vs-army fight, not a raid — no
        // Raid objective (parity with V1 AiConfig.raidTargetMaxDefenders).
        public const int raidTargetMaxDefenders = 4;
        // Continuity: a started Raid intent is reaped after this many stalled turns / absolute
        // turns, same shape as the shared commitment* caps but a touch more patient (assembly +
        // travel is slower than a scout leg).
        public const int raidIntentStallTurns = 3;
        public const int raidIntentMaxTurns = 10;
        // A committed + ready (assembly applied / mover moving) Raid gets this Hard sunk-cost bump
        // on its LocalAdmissionScore so a small Radar wobble cannot drop it for routine recon.
        public const float raidHardCommitmentBonus = 8f;
        // Structural-failure cooldown for a Raid mission key (assembly infeasible / no mover).
        public const int raidRejectCooldownTurns = 3;

    }
}
