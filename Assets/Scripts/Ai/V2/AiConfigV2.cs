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
    }
}
