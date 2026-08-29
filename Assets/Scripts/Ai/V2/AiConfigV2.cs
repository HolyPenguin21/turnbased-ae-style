namespace Game.Ai.V2
{
    // Flat tunables for the Strategy V2 pipeline (Game.Ai.V2), kept out of the 1600-line V1
    // AiConfig so the two never have to be read together and a V2 constant is never mistaken for
    // a shipping one. Everything here feeds the WorldAnalysis scan (build-order step 2) and the
    // evaluators/allocator built on top of it. All values are first-pass and meant to be tuned
    // against real AiDebug.log runs, exactly like V1's own tuning passes.
    //
    // NOTHING here changes V1 behaviour — the whole namespace is dead code until
    // AiConfig.aiStrategyV2Enabled is set.
    public static class AiConfigV2
    {
        // =======================================================================================
        //  STRENGTH MODEL  (AiPower) — replaces V1's flat WorthIt.AttackSum + DefenseSum.
        //  UnitPower = weighted sum of the raw combat stats, times an ability multiplier. This is
        //  a cheap RANKING scalar for the radar and the potential estimates, deliberately NOT a
        //  battle prediction — real win probability still comes from WorthIt's Monte Carlo
        //  (see ThreatModel.AttackWinChance).
        // =======================================================================================
        public const float powerAttackWeight = 1.0f;
        public const float powerDefenseWeight = 0.8f;
        public const float powerHitPointsWeight = 0.6f;
        public const float powerInitiativeWeight = 0.25f;
        public const float powerResistanceWeight = 0.35f;
        // A hero's Fate (battle re-rolls) folded into its own UnitPower on top of its raw stats.
        public const float powerHeroFateWeight = 1.5f;

        // Multiplicative bumps for abilities that meaningfully raise a unit's real combat value
        // beyond its raw stat line. Applied as (1 + Σ bump) to that unit's base power. Situational
        // abilities (Hyperkinetic/Pyrokinetic only matter against a matching enemy type) get a
        // small flat bump here rather than a full situational model — that lives in the
        // evaluators, not the snapshot.
        public const float powerBumpCeramicArmor = 0.20f;
        public const float powerBumpShockAttack = 0.15f;
        public const float powerBumpCriticalDamage = 0.15f;
        public const float powerBumpSituationalCounter = 0.08f; // Hyperkinetic / Pyrokinetic

        // Composition quality maps [0..1] onto a multiplier of (compoFloor .. 1). A single unit or
        // an all-one-type stack still counts for compoFloor of its raw power; a balanced,
        // type-diverse, hero-led stack counts for the full amount.
        public const float compoFloor = 0.55f;
        public const float compoWeightTypeCoverage = 0.45f; // distinct UnitTypeTags present / target
        public const float compoWeightRangeBalance = 0.30f;  // has both a front (range 1) and a reach unit
        public const float compoWeightHeroPresent = 0.25f;
        public const int compoTypeCoverageTarget = 3;        // distinct damage-relevant tags that count as "full"

        // =======================================================================================
        //  ECONOMY STANDING
        // =======================================================================================
        // How many turns the AI is allowed to take to afford one "typical" wanted card when
        // sizing DeckResourceNeed into a per-turn target income (variant (a): derive the absolute
        // floor from what the deck actually costs, not a fixed 2/2/2/2).
        public const float economyDeckNeedHorizonTurns = 3f;
        // Blend weights for EconomicSecurity = w_abs*AbsFloor + w_rel*relTerm + w_bot*(1-Bottleneck).
        public const float economySecurityAbsWeight = 0.5f;
        public const float economySecurityRelWeight = 0.3f;
        public const float economySecurityBottleneckWeight = 0.2f;

        // =======================================================================================
        //  THREAT MODEL
        // =======================================================================================
        // Confidence stamped on a contact by knowledge tier.
        public const float threatConfidenceExact = 1.0f;
        public const float threatConfidenceLastKnown = 0.7f;
        public const float threatConfidenceCheatRegion = 0.5f;

        // A cheat (fog-ignoring) contact is only ever emitted per own base and only for a
        // scout/raid-shaped force — same scope V1's AiDefencePlanner.CheatEstimateRaiderThreat
        // used. It carries the base's own sector, never the army's hex (spec-18, now a type
        // invariant — see EnemyContactSnapshot).
        //   Radii reuse the V1 AiConfig constants at the call site (defenceReactionRadius,
        //   makeshiftScoutMinMembers) so the two never silently diverge on the numbers.

        // Severity = confidence * clamp01( wWin*AttackWinChance + wDmg*potentialDamageFrac
        //                                  + wEta*etaUrgency + wCanDmg*(canDamage?1:0)
        //                                  - wResp*responseHeadstart ).
        public const float severityWinChanceWeight = 0.35f;
        public const float severityDamageWeight = 0.25f;
        public const float severityEtaWeight = 0.25f;
        public const float severityCanDamageWeight = 0.15f;
        public const float severityResponseHeadstartWeight = 0.25f;
        // Only pairs above this land in ThreatModel.Threats at all — keeps the list to real
        // Enemy->Asset pressure instead of every contact against every asset.
        public const float severityListingCutoff = 0.08f;

        // ThreatModel.UnderSiege — the AI-behaviour label (there is NO game "siege" state) for
        // "an enemy force I cannot currently beat is at the gates of my citadel/base, so drop
        // scouting/raiding/economy and consolidate". Same shape as V1 AiDefencePlanner.
        // IsUnderSiege but with V2's own tighter radius: a known enemy within siegeRadius of a
        // Citadel/Base asset (or <= siegeEnemyEtaTurns out) whose attack on it would probably WIN.
        // NOTE: V1's own IsUnderSiege (AiConfig.siegeRadius = 4) is still OR'd into
        // ThreatModel.UnderSiege for behaviour parity, so the effective trigger is min(this, V1)
        // until the V1 branch is retired.
        public const int siegeRadius = 3;
        public const float siegeEnemyWinChanceThreshold = 0.5f;
        public const int siegeEnemyEtaTurns = 1;

        // =======================================================================================
        //  STRATEGIC ASSET VALUE  (importance, set by the snapshot — missions react to
        //  Value * Severity and do not compute strategic value themselves).
        // =======================================================================================
        public const float assetValueCitadel = 100f;
        public const float assetValueBase = 60f;
        public const float assetValueFacilityBase = 25f;
        public const float assetValueFacilityBarracksBonus = 15f;
        public const float assetValueFacilityDevBonus = 20f;    // Research / Production facility
        public const float assetValueFacilityCollectorBonus = 20f; // scaled by that resource's income share
        public const float assetValueArmyPowerDivisor = 4f;     // EffectiveArmyPower / this
        public const float assetValueArmyCap = 50f;

        // =======================================================================================
        //  ETA  (first pass — plain hex distance / move budget, no real pathfinding yet).
        // =======================================================================================
        public const int etaFallbackMoveBudget = 1; // when an army reports 0 MaxMovement
        public const int etaUnknownContactPenalty = 6; // notional turns for a Region/Unknown contact

        // =======================================================================================
        //  DESIRE EVALUATORS  (Strategy V2 build-order step 3)
        //  One response-curve evaluator per axis; each raw desire is Σ weighted contributions in
        //  [0..1], normalised to the simplex exactly once in Radar.Normalize. Recon + Aggression
        //  are the two live axes; Defence/Economy/Development return a flat placeholder until
        //  their own evaluators land in later build-order steps.
        // =======================================================================================

        // ---- Recon (single axis, three named contributions) --------------------------------
        //  exploration  — DECAYS as the reachable map opens. effectiveUnknown discounts the slice
        //                 that will never be safely visited (behind an enemy citadel, hostile-
        //                 guarded). A flat floor here is a crude stand-in; build-order step 4's
        //                 real Frontier replaces it. NO turn-number term (project owner's call —
        //                 decay is state-driven, not clock-driven).
        //  surveillance — SUSTAINED all game: a non-burning baseline (re-scan hex content, keep
        //                 resource sites / vision current) plus a bump for contacts gone stale.
        //  enemyBlindness— we KNOW an opponent is fielded (honest opponent list) but have zero
        //                 honest sightings of it. Magnitude only; a with-error direction is the
        //                 step-4 planner's job.
        public const float reconUnreachableFloor = 0.15f;
        public const float reconExploreRampLo = 0.03f;
        public const float reconExploreRampHi = 0.60f;
        public const float reconSurveillanceBaseline = 0.18f;
        public const float reconStaleShareWeight = 0.50f;
        public const float reconBlindnessMagnitude = 1.0f;
        public const float reconWeightExploration = 0.55f;
        public const float reconWeightSurveillance = 0.55f;
        public const float reconWeightBlindness = 0.35f;

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

        // ---- smoothing / placeholders / out-of-simplex scalars ---------------------------
        public const float desireSmoothing = 0.40f;          // weight on the previous smoothed value
        public const float desirePlaceholderInactive = 0.30f; // DEF/ECO/DEV until their evaluators land
        public const float militaryThreatSiegeFloor = 0.90f;  // UnderSiege forces MilitaryThreat >= this

        // =======================================================================================
        //  COMBAT OPPORTUNITY ANALYZER  (shared estimator — ONE ESTIMATOR, MANY STAGES)
        //  Snapshot-fidelity tier: ranks known enemy/neutral ARMY sightings (the target set that
        //  actually carries per-unit DefenderProfiles) by whether a realistically assemblable
        //  force clears the same CanDamageAll / WinChance bar a raid is gated on. The live tier
        //  (AiResourcePool + CanLeaveWithoutOvercrowding, exact WorthIt.Estimate cost-of-victory)
        //  is a later overload of the same method, filling the same CombatOpportunity contract —
        //  build-order steps 6/9. Buildings / event guards / cheat-region targets: deferred there.
        // =======================================================================================
        public const float opportunityMinViableWinChance = 0.65f; // parity with AiConfig.raidMinimumWinChance
        public const float opportunityNoHeroPenalty = 0.35f;      // raids are hero-led; no hero obtainable -> weak
        public const float opportunityValueNorm = 30f;            // targetValue that maps to a full value term
        // A safe, cheap, close win is itself worth wanting — one weak neutral next door is "a good
        // reason to be aggressive" (project owner's call), so a target that CLEARS the viability
        // gate scores on at least this much value even if its defenders are near worthless. The
        // reported CombatOpportunity.TargetValue stays the true (un-floored) number.
        public const float opportunityBeatableValueFloor = 12f;
        public const float opportunityEtaWeight = 0.40f;
        public const float opportunityCostWeight = 0.40f;
        public const float opportunityScoreNorm = 0.50f;          // raw product that maps to OpportunityScore 1
    }
}
