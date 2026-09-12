namespace Game.Ai.V2
{
    // Part of AiConfigV2 (file-split Task 2, see Docs/ai-v2-file-split-refactor-tasks.md).
    // Recon — desire sub-block, mission planner, scout capability/stealth quality models, air recon (per-step scoring, boomerang routing, strategic anchor, energy pressure), aviation sortie reservation.
    public static partial class AiConfigV2
    {
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
        public const float reconExploreRampHi = 1.00f;
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
        //  from K (how many GROUND Recon operations may actually execute per AI turn). MissionLayer
        //  emits the beam; ReconAssignmentPlanner applies K once executor kind is known, while air
        //  retains its own actor cap; ProvisioningManager proves the selection can be delivered.
        // =======================================================================================
        // K — the absolute cap on concurrently executing GROUND Recon missions per AI turn.
        // Owned by ReconAssignmentPlanner. Parity with V1 AiConfig.maxConcurrentVisitHex.
        public const int maxConcurrentReconExecutions = 2;
        // N — how many ordinary Recon alternatives MissionLayer passes downstream. Tuning baseline,
        // NOT a gameplay invariant. Must remain >= maxConcurrentReconExecutions (a wider beam only
        // gives Assignment / re-pack more backups to fall through to).
        public const int scoutCandidateBeamWidth = 6;
        // Two Scout focus hexes must be at least this far apart to be two missions worth funding
        // separately — adjacent hexes are the same frontier. Enforced for ground-bound pairs by
        // ReconAssignmentPlanner once executor kind is known, NOT in the beam.
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

        // RECON-AIR-01 — a generic (non-stealth) Refresh/Surveil mission is executable by EITHER a
        // ground scout OR an air actor (see ReconAssignmentPlanner.AppendAirCandidates); the
        // Mission-stage estimate must therefore size an envelope wide enough for air's typical
        // activation cost too, not only a ground scout's. Both are notional, worst-reasonable-case
        // figures — Assignment/Provisioning refine to the real bound actor's cost afterward, exactly
        // like the ground AP estimate already does.
        public const float airReconNotionalActivationAp = 1f;
        public const float airReconNotionalLaunchEnergy = 2f;

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
        //     ReconReactionPolicy / ReconPatrolState / ReconConcurrencyPolicy / ReconGroundStepPlanner.
        public const float scoutReactionAttackWinChance = 0.80f;   // ReconReactionPolicy — min win chance for an opportunistic solo-Recce attack
        public const float scoutReactionAttackMaxCriticalAfter = 0.25f; // ...reject the attack if even a WIN leaves the scout critically wounded this often (WorthIt.BattleEstimate)
        public const float scoutReactionFleeWinChance = 0.50f;     // ReconReactionPolicy — flee when the worst exposed known threat drops our win chance below this
        // Flee-destination scoring (spec §14) — nearest base is a fallback only, not the goal.
        public const float scoutFleeThreatDistWeight = 1.0f;        // farther from the threat hex is better
        public const float scoutFleeFriendlyApproachWeight = 3.0f;  // closeness to the nearest own garrison, as 1/(1+dist)
        public const float scoutFleeFutureReconWeight = 1.5f;       // a flee hex from which recon can usefully resume (unvisited-neighbour fraction)
        public const float scoutFleeDetectorWeight = 2.5f;          // penalty per unit of known detector risk at the flee hex
        public const float scoutFleeBacktrackWeight = 0.5f;         // penalty per recent scout-trail hit at the flee hex
        public const int reconAssignmentModeHoldTurns = 1;         // ReconPatrolStateRegistry — min turns between Explore<->Refresh mode switches for one actor
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
        // RECON-AIR-05 (round 5) / Bug B fix (round 6) — the strongest anchor: Assignment/Continuity
        // already bound this sortie to a SPECIFIC Refresh/Surveil target this turn (or a durable one,
        // for a continuing sortie), and the tactical planner must drift toward it rather than pick a
        // fresh unrelated objective. Weighted above every discovered/inferred anchor (Citadel
        // included) since it is a real commitment, not an inference — AirReconRouteScorer.Score gives
        // this its own additive term (missionFocusDir), same shape as Citadel's own additive term
        // (citadelDir = airReconCitadelDirectionWeight * confidence, confidence <= 1), so defining
        // this weight as citadel's weight PLUS an explicit margin makes
        // "mission focus dominates Citadel when both are present" true BY CONSTRUCTION, not by
        // coincidence of two independently-picked constants:
        //   missionFocusDir_max (0.90) = airReconCitadelDirectionWeight (0.70) + margin (0.20)
        //                              > citadelDir_max = airReconCitadelDirectionWeight * 1.0 (0.70)
        public const float airReconMissionFocusDominanceMargin = 0.20f;
        public const float airReconMissionFocusWeight = airReconCitadelDirectionWeight + airReconMissionFocusDominanceMargin;
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
        // Sector coverage HARD rule (spec §5 "already adequately covered by another assigned Recon
        // actor"). OtherSectorClaims counts air sorties + ground scouts working the step's coarse
        // 60°-wedge sector (populated for storage launches too). A candidate is rejected outright
        // when the wedge already holds at least this many recon actors AND the candidate's forward
        // corridor carries no substantial new observation (raw Σ per-hex usefulness ≤ the floor).
        // Kept at 2 so a single scout loosely inside a wide wedge is NOT treated as full coverage —
        // it is the genuinely-saturated case (two sorties both picking the same wedge, or a wing +
        // a scout with a redundant air step) that this hard-rejects; the soft divisor still applies
        // from the first claim.
        public const int airReconSectorAdequateCoverage = 2;
        public const float airReconSectorCoveredNoveltyFloor = 1.0f;

        // =======================================================================================
        //  AIR RECON ENERGY-PRESSURE MEASUREMENTS  (ReconAirEnergyPolicy helpers, spec §41–§44)
        //  Tunables for the generic hand/deck Energy-pressure reads consumed by the ONE canonical
        //  sortie-reservation decision (AviationSortieReservationEvaluator). ReconAirEnergyPolicy no
        //  longer makes an admission decision of its own, so the retired soft-opportunity-term
        //  tunables (income horizon / opp weight / min utility) are gone — see the
        //  AVIATION SORTIE RESERVATION EVALUATOR block below for that decision's tunables.
        // =======================================================================================
        public const float reconAirEnergyExtraHandFraction = 0.35f; // weight on playable hand cards beyond the single largest when computing ProtectedHandEnergy
        public const int reconAirEnergyHighValueMinCost = 2;        // a playable hand card's Energy cost must be at least this to count as "high value" worth protecting (spec §41.2)
        public const float reconAirEnergyDeckDrawFraction = 0.10f;  // low weight on the Energy the turn's likely next draw would need (spec §44 — never the whole remaining deck)

        // =======================================================================================
        //  AVIATION SORTIE RESERVATION EVALUATOR  (AviationSortieReservationEvaluator)
        //  Resource Outlook -> Hand/Deck Energy Pressure -> Sortie Value -> Reservation Decision.
        //  Reuses ReconAirEnergyPolicy's hand/deck pressure scan; these tunables govern only the
        //  staged decision itself (headroom horizon, sortie-value floor, opportunity weighting).
        // =======================================================================================
        public const float aviationReserveIncomeHorizon = 3f;       // turns of Energy income folded into headroom / effective-spendable
        public const float aviationReserveMinSortieUtility = airReconMinimumUsefulScore; // a route below this is never worth protecting, regardless of resources
        public const float aviationReserveOpportunityWeight = 0.5f; // how hard the soft opportunity term pulls net utility down
        public const float aviationReserveMinNetUtility = 0f;       // reserve only when sortieUtility - opportunityCost clears this

        // --- Resource-starvation economic feedback (spec §17, P2). Bounded, decaying pressure
        //     raised when AGG/RCN strategic chains keep failing for lack of a specific empty
        //     resource stock; consumed as ONE bounded Economy value bump on a known extraction
        //     site for that resource. Fast decay so it expires within a couple of quiet turns.
        public const float starvationHitGain = 0.34f;          // EWMA add per recorded block, clamp01
        public const float starvationDecayPerTurn = 0.6f;      // multiply each turn (once)
        public const float starvationEconomyTrigger = 0.5f;    // below this -> no extra Economy demand
        public const float starvationEconomyValueBonus = 35f;  // max added to the site's Value
        // Phase-B card-resource opportunity cost for a CURRENT, exactly witnessed AGG/RCN
        // capability block. This is a soft marginal term: urgency, affordability horizon and the
        // fraction of required stock the candidate would consume all scale it below this ceiling.
        public const float starvationResidualPreservationMax = 3.0f;
        // FutureUtility term values. AP/resource costs use the shared stratCard /
        // stratChain dynamic cost model; Phase B has no parallel cost weights.
        public const float surplusHeroVersatility = 0.35f;
        public const float surplusUnitVersatility = 0.25f;
        // A deployed ApBonus source pays back every following turn. Keep this large enough to beat
        // a generic low-value garrison body in Phase B, without bypassing required Phase-A demands.
        // DEPRECATED (AI-MGR — Dynamic Strategic Effect Utility): the flat "+0.75 because ApBonus is
        // present" is gone. Recurring-AP value is now the dynamic model above (effectGlobal* /
        // effectRecurring* / apMarginalUtil*). Kept only so any stale reference still compiles.
        public const float surplusRecurringApIncomeBonus = 0.75f;
        public const float surplusHandPressureBonus = 0.30f; // hand is full -> playing a card frees a slot
        public const float surplusScarcityHigh = 1.0f;
        public const float surplusScarcityMed = 0.5f;
        public const float surplusScarcityLow = 0.15f;
        public const int surplusScoutOversupplyAt = 3;       // ReadyScouts >= this -> another Recce is oversupply
        public const float surplusOversupplyPenalty = 0.8f;

    }
}
