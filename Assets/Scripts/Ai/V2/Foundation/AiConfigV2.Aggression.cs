namespace Game.Ai.V2
{
    // Part of AiConfigV2 (file-split Task 2, see Docs/ai-v2-file-split-refactor-tasks.md).
    // Aggression axis readiness and shared Aggression/Raid objective and mission configuration.
    public static partial class AiConfigV2
    {
        public const float raidRepairMinWinChanceGain = 0.001f;
        // The last combat army on an own Citadel/Base stays put while a known hostile force that
        // can damage it is at most this many turns away
        // (ActiveDefenceObjectiveEvaluator.IsPinnedStrongholdDefender).
        public const int strongholdDefenderPinEnemyEta = 1;
        // 2026-10-01 (user decision) — ActiveDefence answers armies, not scouts: a hostile
        // contact whose known roster is weaker than this (AiPower scale) is no Intercept target.
        public const float activeDefenceMinEnemyPower = 10f;
        // 2026-10-01 (user decision) — ActiveDefence stays near home: past this many hexes from
        // the nearest own Base/Citadel every further hex lowers OwnTerritoryProximity by
        // taskScoreActiveDefenceLeashPerHex (a desire penalty, never a hard gate).
        public const int activeDefenceLeashHexes = 4;
        public const float taskScoreActiveDefenceLeashPerHex = 2f;
        // 2026-10-07 (user decision) — a Raid stays near its home network: first the events
        // around the Citadel, then a Base, then raids from that Base. Past this many hexes from
        // the nearest own Base/Citadel (~1.3 turns at 3 MP) every further hex lowers
        // OwnTerritoryProximity by taskScoreRaidLeashPerHex (a desire penalty, never a hard gate).
        public const int raidLeashHexes = 4;
        public const float taskScoreRaidLeashPerHex = 2f;
        // 2026-10-01 (user decision) — a regroup at the Citadel or a withdrawal walks every usable
        // field army home: only for a threat that reaches its asset within this many turns. A
        // farther one is deferred (the field armies keep their tasks; the next pass re-decides).
        public const int activeDefenceWithdrawMaxEnemyEta = 2;
        // A fist that comes for a Base / Citadel is answered by standing on it when the defence
        // estimate (ActiveDefenceObjectiveEvaluator.HoldChanceAtAsset) reaches this chance.
        public const float activeDefenceHoldWinChance = 0.70f;
        // 2026-10-01 (user decision) — a preparation's frozen target roster is re-frozen when the
        // peak (TotalMilitaryPotential) grew by more than this share since it was frozen.
        public const float attackTargetRosterRefreezeGrowth = 0.10f;
        // Generic military readiness for the Aggression Radar axis.
        public const float aggRelEdgeRampLo = 0.80f;
        public const float aggRelEdgeRampHi = 2.20f;
        public const float aggRelEdgeNoIntel = 0.50f;   // "haven't seen them" != "I'm winning"
        // Attack readiness (strike force step 4) — how much of the available force stands in one
        // fist and how much of the ceiling is already on the map (SelfSnapshot force measures):
        //   assembly   = FistPower / FieldPotential                                  ramp lo..hi
        //   deployment = FieldPotential / (TotalMilitaryPotential + Reserve.Equipment) ramp lo..hi
        //   readiness  = assembly * deployment -> TaskScore.AttackReadiness (Base/Citadel only).
        public const float attackAssemblyReadyLo = 0.50f;
        public const float attackAssemblyReadyHi = 0.90f;
        public const float attackDeploymentReadyLo = 0.40f;
        public const float attackDeploymentReadyHi = 0.80f;
        public const float aggSurplusRampLo = 0.10f;
        public const float aggSurplusRampHi = 0.60f;
        // RequiredDefensiveReserve = Σ over threatened Citadel/Base/Facility assets of
        //   strongestThreateningContact.EffectiveArmyPower * aggDefenceConfidenceMargin,
        //   floored at aggHomeGuardFloor (diagnostic + ForceNeed.Defensive).
        //   OffensiveFreePower = max(0, TotalPower - aggHomeGuardFloor): since 2026-10-04 the
        //   threat reserve no longer damps the Radar surplus (it starved ActiveDefence).
        public const float aggDefenceConfidenceMargin = 1.30f;
        public const float aggHomeGuardFloor = 3f; // AiPower units (one Light Infantry alone = 4.85)
        public const float aggEcoGateLo = 0.50f;        // ecoGate = Lerp(this, 1, EconomicSecurity)
        // =======================================================================================
        //  AGGRESSION / RAID  (Strategy V2 build-order step 9 — the second objective/mission lane)
        //  Raid is the first Aggression Objective type. Objective discovery reuses the shared
        //  CombatOpportunityAnalyzer (snapshot tier); the numbers below only shape Raid-LOCAL
        //  admission ordering, the resource envelope and the assembly/continuity guards. Cross-lane
        //  ordering stays on BaseValue, AP budget stays on the radar / TurnResourceBook.
        // =======================================================================================
        // A known neutral target becomes a Raid RaidObjective only if its canonical TaskScore
        // has some real strategic merit. This threshold is on the unified TaskScore scale (where
        // RaidReward is 8), not the retired Raid-local 12..90 Lerp scale.
        // Feasibility is still NOT part of this discovery gate: an objective may survive so Demand
        // can ask for the missing combat capability; WorthIt/assembly remain the execution owners.
        public const float raidObjectiveMinBaseValue = 0.25f;
        // ATK §35 — how many hexes of detour off the anchor -> hostile-citadel corridor cost an
        // Attack target its whole CorridorAlignment term. A ground campaign tolerates a wider
        // swing than an Economy base site does (economyBaseFoundScanRadius = 3), because the
        // capture itself moves the support network rather than depending on the existing one.
        public const float attackCorridorDetourScale = 6f;
        // ---- Attack tactical opportunity (ATK §9-§18) ---------------------------------------
        //  A TacticalOpportunity is a side strike an Attack army takes on a weak enemy FIELD army
        //  it passes on its way to the Base, without changing the strategic target (§10) and
        //  without ever raising capability Demand (§14). These two numbers answer only §12's
        //  question — "is this army significant enough to be worth diverting for at all" — on the
        //  intentionally crude raw Attack+Defense scalar that §12 mandates. They are NOT a battle
        //  estimator: safety is decided afterwards by the one shared WorthIt/GroundCombatFeasibility
        //  owner (§13).
        //
        //  NOT calibrated against a playthrough yet: the share is the natural self-scaling shape
        //  ("a quarter of my own raw strength is a noticeable slice of the enemy's field force")
        //  and the floor only keeps a single scrap unit from pulling a campaign off its axis. Both
        //  are meant to be retuned from real [AI][V2][Attack][Tactical] log lines.
        // N — how many Raid alternatives AggressionMissionPlanner hands downstream (beam width).
        // Execution capacity is bounded by real armies / heroes / commitments / resources, NOT a
        // fixed K (spec §20), so there is no maxConcurrentRaidExecutions.
        public const int raidCandidateBeamWidth = 4;
        // Raid execution AP envelope (activation of the raid mover only; movement is MP, not AP).
        public const float raidNotionalActivationAp = 1f;
        public const float raidActivationApMax = 3f;
        // Structural requirement projection: a raid roster must clear this Monte-Carlo win chance
        // (parity with opportunityMinViableWinChance).
        public const float raidMinViableWinChance = 0.80f;
        // Strike force step 5 — past the gate a gather keeps recruiting a support only while it
        // adds at least this much win chance (one Monte-Carlo trial is 0.04: less is noise).
        public const float attackGatherMinWinGain = 0.05f;
        // 2026-10-04 (user decision, TEST BEHAVIOR) — Attack does not require known defender
        // coverage (WorthIt.CanDamageAll), neither before nor during the assault: a fist may march
        // on a site holding a defender none of its bodies can damage. Only Attack's no-threshold
        // gate (GroundCombatAdmissionPolicy.AttackCoverageGate) reads it; Raid / ActiveDefence keep
        // coverage. Static, not const, so tests can exercise both rules.
        public static bool attackRequiresDefenderCoverage = false;
        // 2026-10-08 (user decision) — the win chance a VOLUNTARY local fight of an Attack army must
        // reach: intercepting a known field army, taking an optional intermediate Base, or fighting
        // a contact that stands on the path. The main Base / Citadel keeps no floor
        // (GroundCombatAdmissionPolicy.AttackCoverageGate); independent Raid / ActiveDefence keep
        // their own 0.80 / 0.55. A relevant, significant hostile army below this chance sends the
        // marching Attack home (AttackMissionPhase.RecoveryReturn).
        public const float attackLocalMinWinChance = 0.40f;
        // A committed Assault meets an existing support only on the primary's own route to the
        // target (it never backtracks); the primary may hold at the meeting hex for at most this
        // many turns waiting for the support. A support that needs longer does not stop the march.
        public const int attackReinforcementMaxWaitTurns = 1;
        // Perf pre-filter:
        // GroundCombatAssemblyPlanner.Plan()/EligibleActorIds() would otherwise
        // run the 25-trial Monte-Carlo WinChance once per ready army per Raid target, per
        // settled step. Below this attackerPower/defenderPower ratio (aggregate
        // Attack+Defense+HP+0.25*Initiative, as in GroundCombatFeasibility.Clears), Monte Carlo is
        // skipped and the matchup is treated as not clearing raidMinViableWinChance. Calibration
        // (one playthrough, 2644 matchups): the lowest ratio that ever produced win >= 0.65 was
        // 0.57, and all 869 matchups below it topped out at win 0.24. This constant sits well under
        // that floor to leave margin for unseen roster compositions — raise it only after a fresh
        // calibration pass confirms the floor is still safely above it.
        public const float raidPowerRatioPreFilter = 0.40f;
        // CombatPower the requirement projection asks for when no ready force clears the target:
        // the target's own EffectiveArmyPower times this margin.
        public const float raidCombatPowerMargin = 1.25f;
        // Continuity: a started Raid intent is reaped after this many stalled turns / absolute
        // turns, same shape as the shared commitment* caps but a touch more patient (assembly +
        // travel is slower than a scout leg).
        public const int raidIntentStallTurns = 3;
        public const int raidIntentMaxTurns = 10;
        // Structural-failure cooldown for a Raid mission key (assembly infeasible / no mover).
        public const int raidRejectCooldownTurns = 3;

    }
}

