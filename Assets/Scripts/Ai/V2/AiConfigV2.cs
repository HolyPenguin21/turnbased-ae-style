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
        //  exploration  — DECAYS as the reachable map opens. Driven by
        //                 MapKnowledge.ExplorableUnknownFrac (build-order step 4's real frontier
        //                 flood — the dark map still reachable on foot), NOT raw UnknownFrac: the
        //                 slice behind an enemy citadel / hostile guard simply isn't in that
        //                 number. 0 exactly when the frontier is empty. NO turn-number term
        //                 (project owner's call — decay is state-driven, not clock-driven).
        //  surveillance — SUSTAINED all game: a non-burning baseline (re-scan hex content, keep
        //                 resource sites / vision current) plus a bump for TARGETABLE contacts
        //                 (honest + positioned) gone stale. Cheat uncertainty is enemyBlindness's
        //                 job, never this — the three signals must not overlap.
        //  enemyBlindness— we KNOW an opponent is fielded (honest opponent list) but have zero
        //                 honest sightings of it. Magnitude only; a with-error direction is the
        //                 step-4 planner's job.
        public const float reconExploreRampLo = 0.03f;
        public const float reconExploreRampHi = 0.60f;
        public const float reconSurveillanceBaseline = 0.18f;
        public const float reconStaleShareWeight = 0.50f;

        // Composite RefreshPressure (spec §4, DesireEvaluators.ReconRefreshPressure). baseline is
        // reconSurveillanceBaseline above; these weight the other contributions, all [0..1] before
        // the weight. First-pass.
        public const float reconRefreshWeightIntelAge = 0.35f;      // whole-known-map strategic IntelAge
        public const float reconRefreshWeightStaleContacts = 0.30f; // share of honest enemy contacts gone stale (the `surveillance` term)
        public const float reconRefreshWeightPerimeter = 0.25f;     // staleness of hexes around own bases/citadel
        public const float reconRefreshWeightCorridor = 0.20f;      // staleness sampled between own citadel and the nearest known enemy
        public const float reconRefreshWeightConcentration = 0.15f; // coarse enemy-concentration direction pressure
        public const int reconRefreshPerimeterRadius = 3;           // ring radius around each own asset for the perimeter term
        public const int reconRefreshCorridorRadius = 2;            // sample radius around the citadel<->enemy midpoint
        public const float reconRefreshConcentrationNorm = 3f;      // this many true-world enemy armies -> concentration term 1
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
        //  DEF / ECO / DEV DEMAND CONSUMERS  (DemandLayer minimal vertical slices)
        //  Deterministic thresholds, not combat simulation. A DEF demand is raised ONLY when a
        //  Citadel/Base is under real threat AND its committed defence is below requirement (see
        //  the saturation gate in DemandLayer.DefenceDemands); ECO/DEV only when a genuine
        //  structural gap exists (starved resource with an unbuilt site, or no development
        //  facility). None of the three fire merely because resources are free.
        // =======================================================================================
        public const float defenceSeverityTrigger = 0.18f;    // AssetThreatSnapshot.Severity at/above this raises DEF
        public const float defencePerBodyPowerEstimate = 6f;   // ~power one garrison body adds, for sizing DesiredAmount
        public const int defenceMaxBodiesPerAsset = 2;         // cap on bodies requested for one asset in one turn
        public const int defenceMaxDemandsPerTurn = 2;         // anti-spam cap across all threatened assets
        public const float defenceReserveMargin = 1.15f;       // requiredDefence = threateningPower * this

        public const int economyMaxDemandsPerTurn = 1;         // one extraction-infrastructure demand at a time
        public const int developmentMaxDemandsPerTurn = 1;     // one development-infrastructure demand at a time

        // Garrison saturation / composition-diversity modifier in MaterializationCandidateBuilder
        // .ScorePlanA — applied ONLY to a GarrisonCombatPower demand landing in an existing
        // garrison / defensive army. Deterministic; keeps one destination from absorbing every
        // defensive card once it already covers the threat.
        public const float garrisonSaturatedPenalty = 6f;          // destination already meets RequiredCapabilityPower
        public const float garrisonCrowdingPenaltyPerMember = 0.35f; // grows with the destination's current member count
        public const float garrisonDuplicateTypePenalty = 0.6f;    // the card's primary type already dominates the destination

        // Bounded live replacement for stale Explore missions (TaskExecutor). One replacement per
        // stale mission (a replacement can never spawn another), plus this hard cap on the whole
        // execution pass. Deterministic frontier pick, no pipeline re-run.
        public const int maxReplacementMissionsPerPass = 2;

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

        // =======================================================================================
        //  RECON MISSION PLANNER  (Strategy V2 build-order step 4, + step 7.1 candidate beam)
        //  MissionLayer turns one WorldSnapshot + the Recon DesireBreakdown into a CANDIDATE BEAM
        //  of up to scoutCandidateBeamWidth Scout proposals. Two candidate kinds, one shared
        //  0..100 scale:
        //    Explore — a MapKnowledge.Frontier hex. Value from info gain + how central it is.
        //    Surveil — a stale honest contact's last-known hex. Value from staleness x the
        //              ThreatModel severity already attached to that contact (Recon reuses the
        //              same threat picture Defence will).
        //  BaseValue is the mission's INTRINSIC merit and is what goes in MissionProposal.BaseValue.
        //  The breakdown weights (ReconExploration / ReconSurveillance) are applied ONLY to
        //  LocalAdmissionScore (= BaseValue * that weight * risk) — never folded into BaseValue, or
        //  Recon's strategic pull would be counted twice (once in the radar, once here).
        //
        //  STEP 7.1 — N (how many sensible alternatives the planner hands downstream) is separated
        //  from K (how many Recon operations may actually execute per AI turn). MissionLayer emits
        //  the beam; ResourceAllocator applies K + mission conflicts and selects the executable
        //  portfolio; ProvisioningManager proves it can be delivered.
        // =======================================================================================
        // K — the absolute cap on concurrently EXECUTING Recon missions per AI turn. Owned by the
        // allocator (MissionAdmissionPolicy.Capacity). Parity with V1 AiConfig.maxConcurrentVisitHex.
        public const int maxConcurrentReconExecutions = 2;
        // N — how many ordinary Recon alternatives MissionLayer passes downstream. Tuning baseline,
        // NOT a gameplay invariant. Must remain >= maxConcurrentReconExecutions (a wider beam only
        // gives the allocator / re-pack more backups to fall through to).
        public const int scoutCandidateBeamWidth = 6;
        // Two Scout focus hexes must be at least this far apart to be two missions worth funding
        // separately — adjacent hexes are the same frontier. Enforced by the allocator (via
        // MissionAdmissionPolicy.Conflicts) when building the funded portfolio, NOT in the beam.
        public const int scoutTargetMinSeparation = 2;

        // Frontier shape (WorldAnalysis.BuildMapKnowledge).
        public const int frontierWaveBand = 2;             // ring width past the leading edge (V1 visitRingBand kin)
        public const int frontierEnemyExposureRadius = 3;  // a known non-neutral this close ANNOTATES a frontier hex EnemyExposure (does NOT drop it; V1 scoutFleeRadius kin)
        public const float scoutDetectionRiskNorm = 2f;    // this many stealth-capable detectors near the focus -> DetectionRisk 1
        // A Surveil target IS a (stale) enemy contact — always stealth-Required, and its own
        // last-known hex carries at least this much detection risk (scaled by contact confidence)
        // before any currently-known detectors nearby are added on top.
        public const float scoutSurveilBaseDetectionRisk = 0.5f;
        // Planner-local only: LocalAdmissionScore *= (1 - this * DetectionRisk). Keeps BaseValue /
        // the radar clean (execution risk is not intrinsic information value) while still making the
        // planner prefer the safer of two equally valuable recon jobs.
        public const float scoutDetectionRiskSelectionPenalty = 0.30f;

        // Recon observation history (AiReconMemory) — longer than V1's 2-turn tactical enemy
        // memory so the Surveil staleness ramp (scoutSurveilStaleTurns*) is actually reachable.
        // Must exceed scoutSurveilStaleTurnsHi so the whole ramp is observable before purge.
        public const int reconObservationMemoryTurns = 12;

        // Scout BaseValue = Lerp(min, max, quality); quality = Σ weighted terms / Σ weights, each term [0..1].
        public const float scoutBaseValueMin = 15f;
        public const float scoutBaseValueMax = 65f;
        public const float scoutInfoGainWeight = 0.45f;          // Explore only (Surveil passes infoGain 0)
        public const float scoutStrategicProximityWeight = 0.25f; // both — closeness to our own bases
        public const float scoutThreatWeight = 0.45f;            // Surveil only (Explore passes threatRelevance 0)
        public const float scoutInfoGainNorm = 4f;               // FreshNeighbors that maps to a full info term
        public const int scoutProximityRampLo = 2;               // base-distance: at/under this -> proximity 1
        public const int scoutProximityRampHi = 12;              // base-distance: at/over this -> proximity 0

        // Spec §4/§8/§9/§12 — home/local exploration pressure. A soft strategic preference (never a
        // hard leash): Citadel/base local coverage first, then regional expansion, then distant
        // exploration. "Home" is the starting Citadel AND every owned base hex, whichever is nearest.
        //  Objective level: Explore BaseValue leans harder on closeness-to-home than the generic
        //  proximity term, and its proximity ramp decays across the local->regional band (not out to
        //  distance 12) so a nearby frontier out-scores an equally informative distant one while
        //  meaningful nearby unknown remains.
        public const float scoutExploreHomeProximityWeight = 0.55f; // vs scoutInfoGainWeight 0.45 in the Explore quality blend
        public const int scoutExploreProximityRampHi = 7;           // Explore-only: home-distance at/over this -> home proximity 0
        //  Live step level: an adjacent step that increases distance from the nearest home asset is
        //  penalized while local unexplored coverage is still materially incomplete; the penalty
        //  fades to nothing once the local ring is well covered or the scout is already outside it.
        public const int scoutStepHomeLocalRingRadius = 4;          // hexes from nearest home asset that count as "local"
        public const float scoutStepHomeOutwardPenaltyWeight = 0.45f; // max fraction shaved off an outward step's score when localGap=1
        public const float scoutStepHomeInwardBonusWeight = 0.10f;  // small reward for a step that closes home distance while local gaps remain
        public const int scoutSurveilStaleTurnsLo = 2;           // AgeTurns under this -> staleness 0
        public const int scoutSurveilStaleTurnsHi = 8;           // AgeTurns over this -> staleness 1

        // Spec AI-INTEL-01 — Observed != GroundVisited. `GroundVisited == false` on its own must
        // not keep an Explore focus an attractive target: if the cell and the unvisited neighbours
        // that make up its FreshNeighbors count were already observed recently (ground vision,
        // static vision or an air flyby) the map information is in hand, and only the physical
        // frontier-expansion merit (scoutExploreHomeProximityWeight) should carry the objective.
        // The information half of the Explore quality blend is scaled by a factor that sits at this
        // floor at IntelAge 0 and recovers linearly to 1 across scoutSurveilStaleTurnsLo..Hi. It is
        // a floored multiplier, never a hard exclusion — a genuinely stale or strategically hot
        // cell still scores here and, separately, as a Refresh objective.
        public const float scoutExploreObservedInfoDiscountFloor = 0.25f;

        // ScoutCostModel — resources to fund THIS allocation cycle, not a multi-turn projection.
        // A ground Scout spends AP to activate and MOVEMENT (not AP) to travel; ActivationEnergy
        // is a game rule (non-zero only for a real air army, so 0 here). Stealth is a separate
        // opt-in 1 AP, only when the route carries real detection risk.
        public const int scoutNotionalActivationAp = 1;  // used when no concrete mover exists yet (Provisioning, step 6, resolves it)
        public const int scoutOptionalStealthAp = 1;

        // =======================================================================================
        //  RESOURCE ALLOCATOR  (Strategy V2 build-order step 5)
        //  radar -> per-axis BudgetSlices of the shared pool -> many-to-many packing -> ordered
        //  TentativeAllocation. AP is the only live resource dimension; Energy / Human / Materials
        //  / Tech stay out of allocation until step 9.
        // =======================================================================================
        // AP held OUT of the whole turn's allocatable pool for the off-budget HousekeepingManager
        // (step 8). Renamed from allocatorManagerApReserve — same role, clearer owner. 0 for now —
        // reservation cleanup does not spend AP; raise this only when garrison-reorg with a real AP
        // cost lands in the HousekeepingManager stage. Strategic Manager (Phase A + Phase B) and
        // mission allocation must all leave this amount untouched.
        public const float housekeepingApReserve = 0f;
        // Hard bound for the step-6 pack -> provision -> re-pack loop. Step 5 only builds the
        // AllocationSession/retry seam and executes one pack per turn.
        public const int maxReallocIterations = 3;
        // Structural provisioning failure cooldown. Budget deferral never starts this cooldown.
        public const int allocatorRejectCooldownTurns = 2;
        // Shared tolerance for AP slice affordability / atomic draw / remainder comparisons.
        public const float allocatorSliceEpsilon = 0.01f;

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

        // =======================================================================================
        //  MISSION CONTINUITY  (Strategy V2 build-order step 7)
        //  Two separated concerns:
        //    Intent  — "I still want to finish THIS objective / keep tracking THIS army". Durable,
        //              outlives any single MissionProposal. Drives retarget hysteresis for every
        //              recon mission.
        //    Commitment — a funding POLICY on an Intent: "do not drop this from the budget over a
        //              small Radar wobble". Soft for a far Surveil that has actually started
        //              moving; Hard (raid) lands in step 9.
        //  Invariants held here: Intent != Proposal, Intent != Commitment, Progress != AP spent,
        //  post-execution observation != strategic policy.
        // =======================================================================================
        // Retarget hysteresis. A fresh candidate only displaces the hex an in-flight intent is
        // already heading for if it beats it by this margin on LocalAdmissionScore. Applied via
        // MissionAdmissionPolicy.AdmissionRank at BOTH pruning points — the MissionLayer beam and
        // the allocator's K-cut (step 7.1) — one knob, one formula, no separate incumbent bonus
        // (that stacked to ~1.5x and became a hard lock). Progress-aware margins are a later pass.
        public const float commitmentRetargetMargin = 0.20f;
        // Absolute emergency cap on how long a single intent may persist without completing —
        // safety net only. The real mechanism (deadline = first-executed ETA + slack) is a later
        // step; the ETA is unknown at intent creation because a Surveil proposal has no vantage yet.
        public const int commitmentMaxTurns = 6;
        // Consecutive turns an intent may make NO forward progress (no step, no stealth entry, no
        // productive stop) before it is retired and its key put on the allocator reject cooldown.
        public const int commitmentStallTurns = 2;

        // =======================================================================================
        //  STRATEGIC MANAGER  (Strategy V2 — centralized card play + capability preparation)
        //  NOT a DesireAxis and NOT radar-sliced. Two phases:
        //    Phase A — FulfillDemands: before mission planning, satisfies AxisDemands with cards.
        //              AP is charged to demand.RequestingAxis via the shared AxisBudgetLedger.
        //    Phase B — UseSurplus: after mission execution, spends GENUINELY remaining real
        //              AP/resources on proactive preparation + hand cycling. No radar slice.
        //  Both play cards ONLY through V2 CardPlayExecutor (the single authoritative V2 path).
        // =======================================================================================
        // Phase A — hard safety bound on demand-fulfilment card plays per AI turn.
        public const int maxDemandFulfillmentActionsPerTurn = 3;

        // Card-candidate scoring, shared by Phase A + Phase B.
        //   costFactor  = 1 + stratCardApCostWeight * plan.TotalApCost   (higher AP -> lower score)
        //   trait bonus = a flat add when a demand's preferred trait (e.g. Stealth) is on the card
        //   TargetFit   = 1 at the demand's TargetHex, decaying linearly to 0 at stratTargetFitRange
        public const float stratCardApCostWeight = 0.15f;
        public const float stratTraitMatchBonus = 0.35f;
        public const int stratTargetFitRange = 10;

        // GRADED placement preference (add to the candidate score). Mirrors V1's card-placement
        // principle — fill an existing suitable army / the garrison before founding a new one:
        //   Garrison  >  Existing suitable army  >  Reusable empty shell  >  new army (0).
        // A solo (Recce / ScoutCapability) card is shell-or-new only, so the top two never apply
        // to it. Garrison respects garrisonReservedSlots (PlacementRules.CanDepositIntoGarrison).
        public const float stratPlacementGarrisonBonus = 0.30f;
        public const float stratPlacementExistingArmyBonus = 0.20f;
        public const float stratPlacementReusableShellBonus = 0.10f;

        // ---- Step 8B: generated cards + equipment chains ---------------------------------
        //  StrategicManager reasons about the COMPLETE action chain (MaterializationPlan): at most
        //  one Research/Production generation step + one Equipment attachment + one final deploy.
        //  RequiredTraits stay a hard feasibility gate on the projected end result; these knobs
        //  only shape RANKING between feasible chains — a cheap sufficient Direct must still be
        //  able to win against a generation / equipment chain (spec §30 / AC #36).
        //   costFactor += stratChainResCostWeight * Σ(chain R/H/M/T)
        //   score      -= per-extra-step penalty (attach / generation)
        //   score      *= Lerp(stratChainGenerationChanceFloor, 1, SuccessChance)   when a chain generates
        //   score      -= stratChainScarcityPenalty   when a chain would spend a unique Stealth item
        //                                              on a Demand that does not require Stealth
        public const float stratChainResCostWeight = 0.05f;
        public const float stratChainAttachStepPenalty = 0.08f;
        public const float stratChainGenerationStepPenalty = 0.15f;
        public const float stratChainGenerationChanceFloor = 0.35f;
        public const int stratChainStealthScarceAt = 1;   // StealthScouts <= this -> preserve a unique Stealth item
        public const float stratChainScarcityPenalty = 0.40f;
        // Generalized scarcity (spec §5): a Hero body spent on a non-Hero, non-Scout demand while
        // no free deployed hero exists. Scout demands carry their own contextual hero-opportunity
        // term (scoutQualityHeroOppCostMax) and are excluded here to avoid double-counting.
        public const int stratChainHeroScarceAt = 0;      // AvailableHeroes <= this -> the Hero body is a live bottleneck
        public const float stratChainHeroScarcityPenalty = 0.30f;

        // ---- Capability Quality Model — Scout profile (spec §1–§8 / §16 / §17) -----------------
        //  A BOUNDED multiplier on ScorePlanA's cost/fit base score, built from MARGINAL mission
        //  value: every term is "how much more useful is this body than the cheapest feasible
        //  alternative, HERE", never an unconditional raw-stat bonus. Whole-chain AP/resource
        //  affordability and follow-up reservation stay authoritative regardless of this number.
        public const float scoutQualityMobilityWeight = 0.16f;      // per extra moveMax over the feasible-set minimum, scaled by map darkness
        public const float scoutQualityMobilityEtaWeight = 0.22f;   // per whole turn shaved off the ETA to the focus vs that baseline
        public const float scoutQualityMobilityFollowThroughFactor = 0.35f; // raw-headroom value kept when the baseline mover already reaches the focus this turn
        public const float scoutQualityVisionWeight = 0.16f;        // per Recce radius over 1, scaled by how much dark it can actually open
        public const float scoutQualitySpotWeight = 0.16f;          // Recce spot strength, only meaningful in a detection/surveil context
        public const int   scoutQualitySpotNorm = 6;                // spot strength that maps to a full spot term
        public const float scoutQualitySpotIrrelevantFactor = 0.06f;// residual spot value on a plain Explore (near zero)
        public const float scoutQualityStealthOptionValue = 0.10f;  // safe-context option value of a stealth-capable body (ceiling)
        public const float scoutQualityStealthRiskValue = 0.45f;    // protective value of stealth at detection risk 1
        public const float scoutQualityHeroOppCostMax = 0.45f;      // full opportunity cost of burning a Hero as a solo Recce
        public const int   scoutQualityHeroAbundantAt = 2;          // AvailableHeroes >= this -> hero opportunity cost ~0
        public const int   scoutQualityHeroScarceAt = 0;            // AvailableHeroes <= this -> acute hero opportunity cost
        public const float scoutQualityActivationApWeight = 0.12f;  // per activation AP over 1 (drag costFactor's deploy-AP term misses)
        public const float scoutQualityMultiplierMin = 0.55f;
        public const float scoutQualityMultiplierMax = 1.60f;
        public const float scoutQualityLogRunnerUpMargin = 0.15f;   // log the runner-up when it is this close on score

        // ---- Optional (non-Required) Scout stealth AP decision (spec §9 / §10 / §20) -----------
        public const float scoutOptionalStealthMinRisk = 0.15f;          // leg detection risk under this -> never enter
        public const float scoutOptionalStealthProtectionScale = 0.9f;   // protection value = risk * this (* strategic-body factor)
        public const float scoutOptionalStealthStrategicBodyFactor = 1.3f;// a hero-led scout's skin is worth more
        public const float scoutOptionalStealthBaseApOpportunity = 0.06f;// a spent AP is never entirely free late-turn
        public const float scoutOptionalStealthDrawOpportunity = 0.35f;  // extra opportunity cost when the spend would kill a legal draw
        public const float scoutOptionalStealthEnterMargin = 0.10f;      // enter only when (threatProtection + routeBenefit) - opportunity clears this
        public const float scoutStealthRouteAccessWeight = 0.9f;         // spec §12 — RouteAccessBenefit contribution to total stealth benefit (hiding unlocks an otherwise-blocked step)
        public const float scoutStealthRouteShorteningWeight = 0.5f;     // spec §12 — RouteShorteningBenefit contribution (a hidden corridor threads a cluster of occupied hexes)
        // Hard bound on Research/Production Challenges the Strategic Manager may ATTEMPT per AI turn
        // (Phase A + Phase B share it). Generation is resource-expensive and probabilistic — one is
        // a safe first pass; raise only against real AiDebug.log runs.
        public const int maxGenerationActionsPerTurn = 1;

        // Phase B may proactively generate / attach with GENUINELY remaining resources, behind
        // every existing reserve + the Phase-A generator claim. Bounded, never a production planner.
        public const bool surplusAllowGeneration = true;
        public const bool surplusAllowAttach = true;
        public const float surplusAttachTraitBonus = 0.30f;   // added when a proactive attach grants a scarce trait

        // Phase B — surplus preparation. Bounded greedy; no look-ahead simulation.
        public const int maxSurplusActionsPerTurn = 2;      // bounds play/draw/play/draw draining the deck/economy
        public const bool surplusAllowDraw = true;
        public const float surplusUtilityThreshold = 0.60f; // a candidate below this FutureUtility is not worth playing
        // Standalone TERMINAL draw (spec §11–§15): once Phase B has no residual demand it can
        // action and no worthwhile surplus chain, the AP that is left cannot be carried to the
        // next turn — convert it to card option value, bounded by this many draws per turn.
        public const int maxTerminalDrawsPerTurn = 4;
        // Phase B non-combat lane (Aviation / Base / Facility). Guaranteed minimum actions the
        // non-combat lane may still take AFTER the shared maxSurplusActionsPerTurn budget was
        // fully spent by the materialization-surplus loop, so a playable stored-Aviation card can
        // never be starved for turns while it simultaneously blocks terminal AP->draw conversion.
        // >= 2 so a same-turn playable Base AND Aviation both clear (BestPlay ranks Base above
        // Aviation, so a single reserved slot could take Base and leave Aviation stuck). Real AP
        // still bounds it — NonCombatCardPlayer.Execute fails and stops the lane when unaffordable.
        public const int surplusNonCombatReservedActions = 2;
        // Generic (no-residual) combat surplus into the garrison is capped once the garrison is
        // already a strong defensive stack and nothing threatens an asset: the surplus-admission
        // threshold is multiplied by this so the loop stops (and converts stranded AP to draws)
        // instead of grinding the garrison from 6 to 40+ power with threats=0.
        public const float garrisonSaturatedSurplusThresholdMult = 6f;
        // The garrison counts as "already strong enough" for the cap above once its EffectivePower
        // is at least this fraction of the player's best assemblable stack (BestStackPotential).
        public const float garrisonSaturatedReserveFractionOfBestStack = 0.60f;
        // NOTE: the old speculative Phase-B floors (surplusApReserve / surplus{Human,Energy,
        // Materials,Tech}Reserve) are RETIRED. Phase B runs AFTER ordinary mission execution;
        // AP cannot be banked and there is no resource/AP-costing late V2 stage (housekeeping is
        // zero-AP by invariant). StrategicManager.ReservesOkAfterChain now protects only the REAL
        // remaining pool. A future late stage that genuinely needs resources after Phase B must
        // add its own explicit V2 reservation contract rather than reviving a fixed floor here.

        // AI-MGR-02 §4 — end-of-turn tempo spending. When the bounded reaction pass does NOT run,
        // HousekeepingManager releases the AP StrategicResourceReservationLedger held for it and
        // re-runs StrategicManager.UseSurplus so the freed AP is offered to Play / Draw again the
        // same turn. UseSurplus keeps its own internal per-pass action/draw bounds; this only caps
        // how many times that re-run itself may repeat (>= 1; 1 is the safe first pass).
        public const int maxEndOfTurnTempoReruns = 1;

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
        //     AiPower.ToPowerUnit contribution (HitPoints / Initiative / Resistance / Fate —
        //     heroes carry NO Attack/Defense). Never card or display names. A hero carrying a
        //     Researcher/Assembler support vocation whose score is below heroRoleFlexibleCombatFloor
        //     is a SupportOperator (preserve it for base/research/production duty); a non-support
        //     hero at or above heroRoleCombatLeaderFloor is a CombatLeader; everything else is
        //     Flexible. The classification is a PREFERENCE, never an absolute bar — an urgent raid
        //     may still take a SupportOperator.
        public const float heroRoleCommandWeight = 1.0f;
        public const float heroRoleCombatContributionWeight = 0.6f;
        public const float heroRoleCombatLeaderFloor = 7f;
        public const float heroRoleFlexibleCombatFloor = 8f;

        // --- Scout retrace / backtrack route penalty (spec §5). Bounded, snapshot- + short-trail
        //     scoped; never a hard block. scoutTrailLength hexes of recent movement are kept per
        //     scout. Immediate A->B->A reversal is the strongest penalty; re-treading the recent
        //     trail is next; an ordinary older-visited route is weighted most lightly (its floor).
        public const int scoutTrailLength = 8;
        public const float scoutImmediateReversalFactor = 0.55f;   // multiply route value on a reversal
        public const float scoutRecentTrailPenaltyPerHex = 0.18f;  // 1/(1 + p*hits)
        public const float scoutExploredRouteFloor = 0.72f;        // fully-visited route keeps this fraction
        // Spec §3/§7 — a ground Explore/Refresh candidate whose route witness found NO path from any
        // eligible mover ("route unknown") is penalized, not treated as a healthy known route. Soft:
        // a genuinely unreachable objective still stays selectable if nothing better exists, so a
        // scout is never idle when only unknown-route work remains.
        public const float scoutRouteUnknownAdmissionMultiplier = 0.35f;

        // --- Ground Recon reaction / assignment / concurrency / step-scoring tunables (spec §24).
        //     Previously scattered as private/internal consts and inline literals across
        //     ReconReactionPolicy / ReconAssignment / ReconConcurrencyPolicy / ReconGroundStepPlanner.
        public const float scoutReactionAttackWinChance = 0.80f;   // ReconReactionPolicy — min win chance for an opportunistic solo-Recce attack
        public const float scoutReactionAttackMaxCriticalAfter = 0.25f; // ...reject the attack if even a WIN leaves the scout critically wounded this often (WorthIt.BattleEstimate)
        public const float scoutReactionFleeWinChance = 0.50f;     // ReconReactionPolicy — flee when the worst exposed known threat drops our win chance below this
        // Flee-destination scoring (spec §14) — nearest base is a fallback only, not the goal.
        public const float scoutFleeThreatDistWeight = 1.0f;        // farther from the threat hex is better
        public const float scoutFleeFriendlyApproachWeight = 3.0f;  // closeness to the nearest own garrison, as 1/(1+dist)
        public const float scoutFleeFutureReconWeight = 1.5f;       // a flee hex from which recon can usefully resume (unvisited-neighbour fraction)
        public const float scoutFleeDetectorWeight = 2.5f;          // penalty per unit of known detector risk at the flee hex
        public const float scoutFleeBacktrackWeight = 0.5f;         // penalty per recent scout-trail hit at the flee hex
        public const int reconAssignmentModeHoldTurns = 1;         // ReconAssignmentRegistry — min turns between Explore<->Refresh mode switches for one actor
        public const float reconModeSwitchMargin = 0.15f;          // ...and the requested mode's strategic score must beat the current mode's by at least this (spec §25 — score-based, not just time-based)
        public const int reconAssignmentReassignHoldTurns = 1;     // ...min turns between strategic anchor/sector reassignments
        public const int reconAssignmentStallTurns = 2;            // ...no-progress turns after which an anchor reassignment is allowed early
        public const int reconConcurrencyReconOnlyHardCap = 3;     // ReconConcurrencyPolicy — max concurrent scouts in the isolated ReconOnly acceptance environment
        public const float reconConcurrencySecondLaneMinBaseValue = 50f;
        public const float reconConcurrencySecondLaneMinRelValue = 0.80f;
        public const float reconConcurrencySecondLaneMinDarkFrac = 0.35f;
        public const float reconConcurrencyThirdLaneMinBaseValue = 40f;
        public const float reconConcurrencyThirdLaneMinRelValue = 0.65f;
        public const float reconConcurrencyThirdLaneMinDarkFrac = 0.55f;
        // §P1 — when active durable Scout lanes exceed desired concurrency, shed at most this many
        // per turn (gradual contraction, not a one-pass collapse). Only Soft/None-funded lanes are
        // ever shed; a Hard-funded lane is kept even if it leaves active above desired.
        public const int maxReconLaneTrimPerTurn = 1;
        // Generic Phase-B surplus may hold at most desiredConcurrency + this many scout-shaped
        // (IsSoloRecce) armies before it stops founding more; ReconConcurrencyPolicy.HardCap is
        // the separate absolute ceiling above that.
        public const int scoutSurplusWarmSpare = 1;
        public const int reconDemandRegionMergeDistance = 2;         // frontier hexes within this many hexes count as one reachable unexplored region (spec §28)
        public const float reconDemandRefreshLaneThreshold = 0.55f;  // Refresh pressure at/above this earns one dedicated Refresh scout on top of the Explore-driven count
        // --- AI-RECON-02 Unified Recon Capacity model (ReconCapacitySnapshot / DemandLayer.ReconDemands).
        //     Observation lanes (Refresh / Surveil — keep eyes on it) may be served by a ground
        //     scout, a ready aircraft, an airborne recon wing, or a funded-but-unlaunched air
        //     sortie; a ground-traversal lane (Explore — a hex that must be physically stood on)
        //     can ONLY be served by a ground actor, never by aviation. DemandLayer materialises a
        //     new Scout for Recon only when a USABLE capacity deficit (already net of ready/airborne/
        //     funded aviation and idle ground scouts) has held for this many consecutive demand
        //     evaluations: 0 = act the same turn the deficit appears; 1 = require it to persist one
        //     extra turn, filtering single-turn flicker between mission stages (spec §7 "persistent").
        public const int reconCapacityDeficitPersistTurns = 1;
        public const float scoutStepCoverageSectorWeight = 0.30f;  // ReconGroundStepPlanner coverageFactor: 1/(1 + this*sectorClaims + nearbyWeight*nearbyClaims)
        public const float scoutStepCoverageNearbyWeight = 0.55f;
        public const float scoutStepDeadEndFactor = 0.70f;         // an Explore step into a zero-frontier unvisited pocket keeps this fraction of its value
        public const float scoutStepRefreshFreshNeighborWeight = 0.25f; // Refresh info term weight on fresh-neighbour count (Explore uses the full weight)
        public const float scoutLookaheadNearbyClaimWeight = 0.35f;     // bounded-lookahead per-hex nearby-claim discount
        public const float scoutStepUndefendedBuildingBonus = 2.0f;     // added to an ADJACENT step's score when it lands on a foreign undefended Facility/Base (spec §13/§20) — never in lookahead, so it is a local bend only

        // =======================================================================================
        //  AIR RECON PER-STEP FLIGHT SCORING  (ReconAirStepPlanner, spec §24 — no scattered magic
        //  numbers). "Information" is the never-observed / stale-IntelAge value inside the wing's
        //  own vision footprint at the candidate hex; penalties price the proven round-trip route
        //  and the wing's first activation. First-pass.
        // =======================================================================================
        public const float airReconNeverObservedWeight = 1.00f;     // per never-observed hex the step would reveal
        public const float airReconStaleWeight = 0.80f;             // per unit of averaged IntelAge staleness revealed
        public const float airReconDirectionWeight = 0.65f;         // sanitized enemy-direction sector pressure toward the step
        public const float airReconRouteCostPenalty = 0.10f;        // per MP of the proven round-trip route
        public const float airReconExtraTurnPenalty = 0.25f;        // per extra real turn a multi-turn sortie needs
        public const float airReconActivationApPenalty = 0.35f;     // per AP of the wing's first activation
        public const float airReconActivationEnergyPenalty = 0.20f; // per Energy of the wing's first activation
        public const float airReconMinimumUsefulScore = 0.15f;      // a step/launch below this is not worth flying — turn for home / do not launch
        // Air Recon flips from Refresh scoring to never-observed (Explore) weighting once at least
        // this fraction of the map has NEVER been observed by anything — measured from
        // AiReconIntelMemory (recorded intel age), the exact basis ReconAirStepPlanner.
        // ScoreInformation scores against, NOT ground-Visited. Aviation still only reveals; it
        // never marks a hex ground-Visited, and it runs after every provisioned ground scout so
        // it cannot displace a mandatory ground Explore/Visit. Lower this to make aviation chase
        // the last unknown pockets harder.
        public const float airReconExploreDarkFloor = 0.25f;

        // =======================================================================================
        //  AIR RECON BOOMERANG ROUTING + PHASE STATE  (ReconAirExecutor / ReconAirStepPlanner,
        //  spec §33 / §34 / §48). Outbound presses toward information with a soft boomerang nudge;
        //  a single Turning pivot step is logged once one trigger fires; Return then prioritises a
        //  safe landing. All soft — the shared aviation safety filter always wins. First-pass.
        // =======================================================================================
        public const float airReconTurningMarginalGainFloor = 0.35f; // Outbound step score <= this * best Outbound step so far -> pivot to Return
        public const int airReconTurningMpReserveSlack = 1;          // pivot once MP left after the step would exceed the proven return cost by no more than this
        public const float airReconOutboundTrailOverlapPenalty = 0.30f; // per sortie-trail hex within one hex of a candidate Outbound step
        public const float airReconLateralNoveltyBonus = 0.20f;     // small bonus for an informative step that sweeps sideways rather than straight out
        public const float airReconCoverageOverlapPenalty = 0.35f;  // score /= 1 + this*claims — per OTHER active sortie already claiming the step's sector (spec §49)

        // Landing-base hysteresis (spec §38). Once a sortie has a chosen landing base it is kept
        // across steps unless it stops being a viable return target, or a challenger is clearly
        // better — so a small score wobble cannot cause airfield A<->B ping-pong on the way home.
        public const int airReconLandingSwitchForwardMargin = 2;   // challenger must be at least this many hexes more forward (NearestKnownEnemyDistance) to take over
        public const int airReconLandingSwitchCostMargin = 3;      // ...or at least this many MP cheaper on the remaining route home

        // Opportunistic air attack (spec §46). AirRecon never launches FOR an attack; after a step
        // it may strike an honestly-visible target sharing its hex only when the SHARED estimator
        // (AviationCombatEstimator) is favourable AND a safe landing still provably exists both
        // before and after the strike. Same threshold shape as AirStrikeTask's own gate.
        public const float airReconOpportunisticMinDamageFraction = 0.45f; // expected fraction of the target's total HP the strike removes
        public const float airReconOpportunisticMinKillProbability = 0.30f; // expected chance of removing at least one enemy unit

        // =======================================================================================
        //  AIR RECON STRATEGIC ANCHOR + WHOLE-ROUTE SCORING  (AI-AIR-01, spec §1–§5)
        //  A sortie's direction is formed FIRST from strategic landmarks (AirReconAnchorModel):
        //  known/hidden enemy concentration, the enemy Citadel, own facility perimeters with stale
        //  intel, and the corridors between the enemy and our valuable assets. The omniscient read
        //  biases DIRECTION only — it never marks a hex observed. Every candidate first step is then
        //  scored for the PROVEN whole route (outbound + return path), not just its destination
        //  footprint, so a longer sweep past a stale facility / probable corridor can beat a shorter
        //  radial out-and-back. Additive composite, components kept separate for the [Route] log.
        //  First-pass — tune against real AiDebug.log [Recon][Air][Route] lines.
        // =======================================================================================
        public const float airReconRouteObservationWeight = 0.45f;  // per unit of summed per-hex info usefulness along the proven route (never-observed=1, stale age ramps 0..1)
        public const float airReconRouteObservationDecay = 0.82f;   // geometric decay per route hex away from the aircraft — near-term coverage counts most
        public const float airReconRouteObservationRingWeight = 0.35f; // weight on a route hex's 6 immediate neighbours (corridor width), on top of the hex itself
        public const int airReconRouteObservationMaxHexes = 14;     // hard cap on scored route hexes per candidate (bounds the per-decision cost)
        public const float airReconCitadelDirectionWeight = 0.70f;  // first step heads into the enemy-Citadel sector (× confidence: 1.0 known, 0.55 hidden-bias only)
        public const float airReconCitadelHiddenConfidence = 0.55f;
        public const float airReconFacilityCoverWeight = 0.40f;     // route passes within airReconFacilityCoverRadius of an OWN facility whose perimeter intel is stale
        public const int airReconFacilityCoverRadius = 2;
        public const int airReconFacilityStaleAgeMin = 4;           // facility-perimeter IntelAge (turns) at/above which it counts as "stale" and worth an anchor
        public const float airReconCombatOpportunityWeight = 0.22f; // route hex within 1 of an HONESTLY-known enemy sighting — chance to spot / opportunistic strike (halved if that sighting has AA)
        public const float airReconCombatOpportunityCap = 0.66f;
        public const float airReconRecoveryRiskWeight = 0.30f;      // × (extra required turns + 0.5·required unlanded turn-ends + count of route hexes adjacent to KNOWN AA)
        public const float airReconRedundancyRecentObsPenalty = 0.55f; // per informative route hex this player's air recon already flew within AiConfig.airReconTargetCooldownTurns
        public const float airReconRedundancyRecentObsRejectFrac = 0.75f; // ≥ this fraction of the route's informative hexes recently air-observed -> reject the candidate outright (spec §5)
        public const float airReconStrategicValueFloor = 0.02f;     // reject a candidate whose ENTIRE positive side (info + route obs + every anchor term) is below this — "its only value is GroundVisited==false" (spec §5)
        public const float airReconAnchorFrontierWeight = 0.12f;    // unknown-frontier sectors feed anchor pressure only weakly — used after every more meaningful source (spec §1 last bullet)
        public const float airReconAnchorCorridorWeight = 0.45f;    // sector of the midpoint between a known enemy (army/Citadel) and our nearest valuable asset
        public const float airReconAnchorConcentrationWeight = 1.00f; // sanitized enemy-concentration sector pressure (one base unit per true-world army, normalised)

        // =======================================================================================
        //  AIR RECON ENERGY OPPORTUNITY COST  (ReconAirEnergyPolicy, spec §40–§44)
        //  Splits the Energy stock into committed (other in-flight AirRecon activations) +
        //  protected (a playable high-value hand card's need) + spendable, so a routine refresh
        //  sortie can no longer eat Energy a card / research needs just because it is individually
        //  affordable. First-pass; tune against real AiDebug.log [Recon][Air][Energy] lines.
        // =======================================================================================
        public const float reconAirEnergyExtraHandFraction = 0.35f; // weight on playable hand cards beyond the single largest when computing ProtectedEnergy
        public const int reconAirEnergyHighValueMinCost = 2;        // a playable hand card's Energy cost must be at least this to count as "high value" worth protecting (spec §41.2)
        public const float reconAirEnergyDeckDrawFraction = 0.10f;  // low weight on the Energy the turn's likely next draw would need (spec §44 — never the whole remaining deck)
        public const float reconAirEnergyIncomeHorizon = 3f;        // turns of Energy income folded into the effective spendable pool for the soft opportunity term
        public const float reconAirEnergyOppWeight = 0.5f;          // how hard the soft opportunity term pulls final utility down
        public const float reconAirEnergyMinUtility = 0f;           // launch only when informationValue - oppWeight*oppCost clears this (hard reserve already handled separately)

        // --- Resource-starvation economic feedback (spec §17, P2). Bounded, decaying pressure
        //     raised when AGG/RCN strategic chains keep failing for lack of a specific empty
        //     resource stock; consumed as ONE bounded Economy value bump on a known extraction
        //     site for that resource. Fast decay so it expires within a couple of quiet turns.
        public const float starvationHitGain = 0.34f;          // EWMA add per recorded block, clamp01
        public const float starvationDecayPerTurn = 0.6f;      // multiply each turn (once)
        public const float starvationEconomyTrigger = 0.5f;    // below this -> no extra Economy demand
        public const float starvationEconomyValueBonus = 35f;  // max added to the site's Value
        // FutureUtility term weights / values.
        public const float surplusApCostWeight = 0.20f;
        public const float surplusResourceCostWeight = 0.05f;
        public const float surplusHeroVersatility = 0.35f;
        public const float surplusUnitVersatility = 0.25f;
        // A deployed ApBonus source pays back every following turn. Keep this large enough to beat
        // a generic low-value garrison body in Phase B, without bypassing required Phase-A demands.
        public const float surplusRecurringApIncomeBonus = 0.75f;
        public const float surplusHandPressureBonus = 0.30f; // hand is full -> playing a card frees a slot
        public const float surplusScarcityHigh = 1.0f;
        public const float surplusScarcityMed = 0.5f;
        public const float surplusScarcityLow = 0.15f;
        public const int surplusScoutOversupplyAt = 3;       // ReadyScouts >= this -> another Recce is oversupply
        public const float surplusOversupplyPenalty = 0.8f;

        // =======================================================================================
        //  ADAPTIVE INITIATIVE INVESTMENT  (pre-budget, one-way isolated — Game.Ai.V2.Initiative)
        //  Prices excess Human/Energy/Materials/Tech into extra initiative dice ONLY when the AI
        //  has enough real AP workload to justify the cost. NOT a DesireAxis / mission / demand /
        //  StrategicManager action; its output never re-enters the strategic pipeline. The dice
        //  count, price ladder and AP-by-rank live in Game.Turns.InitiativeRules, NOT here.
        //  All first-pass, meant to be tuned against real AiDebug.log runs.
        // =======================================================================================
        // Per-player initiative AP-telemetry ring buffer + how it reads "starvation" vs "idle".
        public const int initiativeHistoryMaxSamples = 8;
        public const int initiativeStarvationApThreshold = 1;   // EndAp <= this + work still queued => needed more AP
        public const float initiativeWasteLeftoverFrac = 0.35f; // EndAp/StartAp >= this + nothing to do => wasted AP

        // CurrentApPressure — structural workload only (no mission planners consulted).
        public const float initiativeApPressureArmyFull = 4f;    // this many separate field armies -> army term 1
        public const float initiativeApPressurePowerFull = 40f;  // this much EffectiveArmyPower -> power term 1
        public const float initiativeApPressureCardsFull = 4f;   // this many AP-costing hand cards -> card term 1
        public const float initiativeApPressureWeightArmies = 0.40f;
        public const float initiativeApPressureWeightPower = 0.35f;
        public const float initiativeApPressureWeightCards = 0.25f;

        // ApPressure = w_cur*CurrentApPressure + w_hist*HistoricalApPressure (both [0..1]).
        public const float initiativeApPressureCurrentWeight = 0.55f;
        public const float initiativeApPressureHistoryWeight = 0.45f;
        // TurnOrderPressure = CurrentApPressure * this — tempo stays a discounted secondary benefit.
        public const float initiativeTurnOrderPressureScale = 0.60f;

        // Marginal resource opportunity-cost model (PreTurnCapacityAnalysis.MarginalCostAt).
        public const float initiativeDeckDemandWeight = 0.50f;   // fraction of remaining-deck appetite that counts as "future demand"
        public const float initiativeIncomeHorizonTurns = 6f;    // income * this folded into effective supply
        public const float initiativeCostAtParity = 1.0f;        // one unit costs this when supply == demand
        public const float initiativeCoverageFloor = 0.25f;      // clamp on supply/demand ratio (scarce)
        public const float initiativeCoverageCeil = 4f;          // clamp on supply/demand ratio (abundant)
        public const int initiativeLowStockUnits = 3;            // draining at/under this many units adds a steep penalty
        public const float initiativeLowStockPenalty = 2.0f;

        // Value model — converts expected-AP / earliness gains into resource-comparable units.
        public const float initiativeApBenefitPerExpectedAp = 4.0f;   // one expected AP is worth ~this many resource units at full pressure
        public const float initiativeTempoBenefitPerEarliness = 3.0f; // full [0..1] earliness swing is worth ~this at full tempo pressure
        public const float initiativeNetValueEpsilon = 0.01f;         // candidates within this net value are "effectively equal"

        // =======================================================================================
        //  STRATEGIC CARD EVALUATOR  (AI-MGR-01 — the shared Card x IntendedUse model used by BOTH
        //  StrategicManager phases). Replaces ScorePlanA's cost/fit product and SurplusUtility's
        //  additive sum with one breakdown (RoleFit / ImmediateTempo / NextTurnPotential /
        //  CapabilityGapValue / ForceGrowthValue / ThreatResponseValue / ResourceEfficiency /
        //  SynergyValue / Deployability / ScarcityValue / RedundancyPenalty / AlternativeUseValue /
        //  HoldValue / ResourcePressureBenefit / HandPressureBenefit). The Hero card CLASS adds no
        //  flat bonus or penalty — hero fitness is HeroRoleEvaluator's characteristic score, and
        //  the only hero cost is AlternativeUseValue when a scarce hero is spent off its best use.
        //  First-pass; tune against real AiDebug.log strat.eval lines.
        // =======================================================================================
        // ForceGrowthValue = SurplusCombatReadinessUtility (marginal AiPower, [0..2]) * Lerp(
        //   baselineReadinessGrowthFloor, 1, BaselineForceReadiness.Need) * this. Keeps an ordinary
        //   combat body worth materialising at AGG = 0 / DEF = 0 without out-bidding a real demand.
        public const float forceGrowthValueWeight = 0.60f;
        // Flat bonus for a card that closes a capability the AI currently lacks ENTIRELY (0 scouts,
        // 0 heroes, no field body). Same magnitude band as surplusScarcityMed.
        public const float capabilityGapValue = 0.50f;
        // ThreatResponseValue (AntiArmor / AntiAir roles only) = clamp(enemyDriverPower / norm) * weight.
        // Uses omniscient enemy power as a DIRECTIONAL strategic bias; never becomes normal AI intel.
        public const float threatResponseNorm = 40f;
        public const float threatResponseValueWeight = 0.30f;
        // NextTurnPotential — a fresh independent actor (new army / reusable shell) opens next turn.
        public const float nextTurnActorPotential = 0.15f;
        // A non-recce body with at least this moveMax also offers the MobileCombat role.
        public const int mobileCombatMoveMax = 5;
        // Hero fitness for a field-command role: HeroRoleEvaluator-style leadership score / norm,
        // clamped to cap. A weak hero scores low; there is NO flat hero bonus.
        public const float heroLeadershipFitNorm = 8f;
        public const float heroLeadershipFitCap = 1.20f;
        public const float heroSupportFitValue = 0.30f;   // a Researcher/Assembler hero evaluated for the Support role
        // HoldValue (spec §3) parts.
        public const float holdUniqueRoleValue = 0.40f;    // a rare stealth body / a support hero while a combat leader is already fielded
        public const float holdScarcityValue = 0.25f;      // the card carries a scarce capability (SurplusScarcity >= med)
        public const float holdHandPressurePenalty = 0.50f;// a full hand argues against holding
        public const float holdLostTempoPenalty = 0.35f;   // Phase B — not playing now forfeits this turn's tempo

        // --- BaselineForceReadiness (spec §4) — radar-DEMAND-INDEPENDENT standing-force signal.
        //     Need in [0..1]: high when the fielded force / combat-actor count / capability coverage
        //     is thin for the game stage, economy and known enemy strength. Consumed by
        //     ForceGrowthValue AND by DemandLayer.BaselineForceReadinessDemands (one low-priority
        //     FieldCombatPower demand so an ordinary unit gets Phase-A pull, not only surplus).
        public const int baselineReadinessStageRampLo = 2;    // turn at/under which "stage" is 0 (very little standing force expected)
        public const int baselineReadinessStageRampHi = 18;   // turn at/over which "stage" is 1 (a full standing force is expected)
        public const float baselineReadinessBaseTargetPower = 12f;   // minimum expected fielded power regardless of enemy
        public const float baselineReadinessEnemyMatchFrac = 0.60f;  // ...or this fraction of known enemy strength, whichever is larger
        public const float baselineReadinessEarlyTargetFrac = 0.35f; // fraction of the target that applies at stage 0
        public const int baselineReadinessTargetActors = 2;          // combat-capable field actors the AI wants standing
        public const float baselineReadinessPowerGapWeight = 0.45f;
        public const float baselineReadinessActorGapWeight = 0.35f;
        public const float baselineReadinessCoverGapWeight = 0.20f;
        public const float baselineReadinessSecureDamp = 0.55f;      // a fully secure economy multiplies Need by this
        public const float baselineReadinessGrowthFloor = 0.40f;     // ForceGrowthValue keeps at least this fraction of its marginal value at Need 0
        public const float baselineReadinessDemandMinNeed = 0.45f;   // below this Need, DemandLayer raises no baseline demand
        public const float baselineReadinessDemandValue = 22f;       // AxisDemand.Value ceiling for the baseline demand (scaled by Need) — deliberately low so real threats/raids outrank it
        public const float baselineReadinessSatisfiedPower = 14f;    // free raid-eligible field power at/above this + enough actors -> no baseline demand
    }
}
