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
        public const int scoutSurveilStaleTurnsLo = 2;           // AgeTurns under this -> staleness 0
        public const int scoutSurveilStaleTurnsHi = 8;           // AgeTurns over this -> staleness 1

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
        //   reuse bonus = a flat add for reusing an empty shell instead of paying CreateArmy AP
        //   trait bonus = a flat add when a demand's preferred trait (e.g. Stealth) is on the card
        //   TargetFit   = 1 at the demand's TargetHex, decaying linearly to 0 at stratTargetFitRange
        public const float stratCardApCostWeight = 0.15f;
        public const float stratReuseShellBonus = 0.10f;
        public const float stratTraitMatchBonus = 0.35f;
        public const int stratTargetFitRange = 10;

        // Phase B — surplus preparation. Bounded greedy; no look-ahead simulation.
        public const int maxSurplusActionsPerTurn = 2;      // bounds play/draw/play/draw draining the deck/economy
        public const bool surplusAllowDraw = true;
        public const float surplusUtilityThreshold = 0.60f; // a candidate below this FutureUtility is not worth playing
        // Real AP kept back ON TOP of housekeepingApReserve so surplus play never starves late work.
        public const float surplusApReserve = 2f;
        // Per-resource floors surplus play must leave intact (Energy non-zero for aviation head-room).
        public const int surplusHumanReserve = 0;
        public const int surplusEnergyReserve = 2;
        public const int surplusMaterialsReserve = 0;
        public const int surplusTechReserve = 0;
        // FutureUtility term weights / values.
        public const float surplusApCostWeight = 0.20f;
        public const float surplusResourceCostWeight = 0.05f;
        public const float surplusHeroVersatility = 0.35f;
        public const float surplusUnitVersatility = 0.25f;
        public const float surplusHandPressureBonus = 0.30f; // hand is full -> playing a card frees a slot
        public const float surplusScarcityHigh = 1.0f;
        public const float surplusScarcityMed = 0.5f;
        public const float surplusScarcityLow = 0.15f;
        public const int surplusScoutOversupplyAt = 3;       // ReadyScouts >= this -> another Recce is oversupply
        public const float surplusOversupplyPenalty = 0.8f;
    }
}
