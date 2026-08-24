namespace Game.Ai
{
    // Single tunable-numbers holder for every static AI class (AiTurnController, AiScoutPlanner,
    // AiEconomyPlanner, AiGoalScorer, AiArmyRoles) — same "one place, referenced wherever needed"
    // idea as Game.Core.GameConfig, but for AI tuning specifically. Plain static class with const
    // fields now (converted 2026-08-19, project owner's own call) — this used to be a
    // ScriptableObject loaded via Resources.Load, but the serialized .asset silently kept its own
    // stale field values on disk forever, so editing a default here never actually took effect at
    // runtime unless someone remembered to manually re-save the asset in the Inspector (see the
    // project owner's own "BuildFacility scored 205 instead of the new ~130" report — the asset
    // still had economyBaseWeight: 200 from before the 110 rebalance). A const in code can't silently
    // disagree with itself the way a serialized asset could — retune by editing this file only, no
    // separate .asset to keep in sync and no Resources/AiConfig.asset any more.
    public static class AiConfig
    {
        // ---- Turn Loop ----
        // Guards against an accidental infinite loop — not a real gameplay limit, just a safety
        // net (a normal turn resolves in well under this many steps). Raised from 12 to 40
        // (project owner's own report, 2026-08-22 — AiDebug.log showed real turns hitting the
        // old cap exactly, step 12/12, with AP still unspent and a legitimate MoveArmy candidate
        // still on the table: a handful of steps that cost no AP at all — AssembleRaidForce,
        // DrawCard, ReserveArmy — were eating into the same per-turn step budget as the AP-
        // costing ones, so this genuinely started acting as a real gameplay limit instead of a
        // pure safety net as the empire grew). Each of those free-action tiers is still bounded
        // by its own finite pool (hand size, idle army count) regardless of this cap, and a truly
        // stuck army is separately guarded by `stuckScouts` in RunTurn below — so a normal turn
        // still settles in well under this many steps, same as before.
        public const int maxStepsPerTurn = 40;

        // ---- AiMapMemory — память о вражеских армиях ----
        // 2026-08-23 (project owner's own call): an enemy-army sighting (AiMapMemory.
        // EnemySightings — NOT the resource-hex/event-guard stores, both of which stay
        // permanent-until-corrected on purpose, see AiMapMemory's own class comment) used to
        // linger forever once observed, corrected only by actually re-observing that exact hex
        // again. That let every "avoidance" reader (VisitHexTask.TryFlee/ScoreCandidate/
        // FindNextSafeStep, RaidWeakerArmyTask, AiDefencePlanner, ...) keep routing around or
        // fleeing a hex the enemy may have vacated many turns ago. A sighting now expires
        // enemySightingMemoryTurns turns after it was last (re)observed — see
        // AiMapMemory.OnTurnStarted, called once at the very top of each AI player's own
        // AiTurnController.RunTurn, before that turn's Decide loop ever reads memory.
        public const int enemySightingMemoryTurns = 2;

        // ---- Task Arbiter — Category Base Weights ----
        // Every candidate action a turn could take gets a Score in this same shared space, and
        // the single highest-scoring one wins the step (see AiTurnController.Decide). Tuned so
        // the everyday case still lands in the old Economy > Recon > Management order, without
        // hard-coding that order — a weak Economy target and a strong Recon one (e.g.
        // raidCounterAttackBonus) CAN cross.
        // Rebalanced 2026-08-19 (project owner's own call): every category base weight normalized
        // to a common ~100 scale instead of an ad hoc spread (was 200/150/220/50) — modifiers now
        // read as small, deliberate nudges off a shared baseline instead of needing to overcome a
        // 50-70 point head start baked into the base itself.
        // 110 → 105 (2026-08-23, project owner's own top-of-arbiter ladder spec) — Экономика's own
        // "базовый" travel tier now lines up with BuildBase Travel (aggressionBaseWeight+5=105) and
        // managementReturnHomeScore (105) instead of sitting 5 above them on its own; see
        // BuildFacilityTask.TravelScore's own comment for how ScoreHex's modifiers (income deficit,
        // citadel distance) still move off this base, now capped at economyTravelScoreCap.
        public const float economyBaseWeight = 105f;
        public const float reconBaseWeight = 100f;
        public const float aggressionBaseWeight = 100f;
        public const float managementBaseWeight = 50f;

        // Экономика's own top-of-arbiter ceiling/tactical tier (2026-08-23, project owner's own
        // ladder spec — full arbiter documented top-to-bottom, this project's own "нужно смотреть
        // на всё сразу, не по одной задаче" rebalance): "никакой обычный Economy travel после всех
        // бонусов не должен превышать 115. 120+ оставляем для немедленного завершения/тактических
        // действий, 125 — для спасения scout."
        //
        // economyTravelScoreCap — hard ceiling on economyBaseWeight+ScoreHex (BuildFacilityTask.
        // TravelScore/ResourcesScrapTask.TravelScore, both callers) regardless of how large
        // AiGoalScorer.IncomeBehindBonus's own deficit term gets tuned to later — a cap enforced at
        // the point the two combine, not a magnitude constraint on IncomeBehindBonus itself, so the
        // 115 ceiling holds even if that term's own scale changes independently down the line.
        public const float economyTravelScoreCap = 115f;
        // economyExecuteScore — Экономика's own "немедленное завершение/тактическая реакция" tier,
        // ONE shared value for three genuinely distinct triggers (same "no near-duplicate constant"
        // reasoning AiConfig.defencePreemptScore's own comment already gives for its own reuse):
        // BuildFacilityTask's arrival "build now" step, a counter-attack against a known weaker
        // army encountered en route to a build/collect site, and a Scrap collector's own final
        // arrival step. Deliberately a flat economyBaseWeight+15 — NEVER modified by ScoreHex/
        // IncomeBehindBonus the way ordinary Economy travel still is (project owner's own explicit
        // spec: "BuildFacility Execute = 120 фиксированно, а не 115 + IncomeBonus. Так шкала будет
        // проще и предсказуемее") — so completing/reacting right now always outranks merely being
        // further behind on income, instead of the two effects competing.
        public const float economyExecuteScore = economyBaseWeight + 15f;

        // ---- Разведка — Задача 1 (Посещение хекса) ----
        // 2026-08-23 (project owner's own call): 3 → 2 — fewer scouts wandering at once.
        public const int maxConcurrentVisitHex = 2;
        // Stall watchdog (2026-08-24, project owner's own root-cause report) — how many REAL GAME
        // TURNS AiTask.VisitLastProgressTurn may sit stale (no hex actually changed, flee or
        // routine alike) before AiScoutPlanner.TryContinueVisitTask gives up on that task and frees
        // the army, rather than letting a permanently boxed-in/blocked scout occupy one of only
        // maxConcurrentVisitHex slots forever. Turn-counted, not call-counted, same "elapsed =
        // ctx.TurnNumber - lastLandedTurn" shape AiTask.AssemblyProgressTurn/GarrisonSeedStartedTurn
        // already use, so several no-progress calls inside the SAME turn (movement exhausted, AP
        // short, fog-boxed) never trip this early.
        public const int visitHexStallTurns = 2;
        // How far past the map's own nearest still-unvisited hex (measured from the citadel) a
        // Задача 1 candidate is still allowed to be, so visiting sweeps outward from the citadel
        // "as a wave" rather than beelining for whatever's farthest. Агрессия no longer shares
        // this band (RaidWeakerArmyTask's own target pool isn't wavefront-bounded at all — see
        // its own class comment) — VisitHexTask is the only user now.
        public const int visitRingBand = 3;
        // scoutProximityWeight is also reused by RaidWeakerArmyTask's own ProximityScore —
        // "closer to the mover" means the same thing whether the target is unexplored fog or a
        // known raid target. Both this and freshNeighborWeight are Разведка's own INTERNAL target-
        // ranking terms only (project owner's own 2026-08-19 call) — used by VisitHexTask.
        // ScoreCandidate to pick which candidate hex wins among several, never added to the final
        // cross-category AiDecision.Score AiScoutPlanner hands to Decide (see ReconMoveWeight's own
        // callers) — VisitHex always contributes exactly reconBaseWeight (minus
        // reconAggressionSuppressionPenalty, see below) to the arbiter regardless of which specific
        // hex won internally.
        public const float scoutProximityWeight = 5f;
        public const float freshNeighborWeight = 4f;
        // Разведка's own internal-only citadel-distance term (see scoutProximityWeight's own
        // comment above for why this never reaches the cross-category score) — separate from
        // citadelDistancePenaltyPerHex below, which IS cross-category for Экономика/Агрессия.
        public const float visitTargetCitadelWeight = 2f;
        // While Агрессия has an active RaidWeakerArmy task (any — a real committed raid force
        // matters more than another routine scouting hop), subtracted from VisitHex's own flat
        // reconBaseWeight contribution to the arbiter (project owner's own 2026-08-19 rebalance —
        // replaces the old raidCommittedBonus top-up on Агрессия's own side; suppressing Разведка
        // achieves the same "raid reliably keeps moving" outcome from the other direction instead).
        public const float reconAggressionSuppressionPenalty = 10f;

        // ---- Разведка — приоритет передвижения по ходам ----
        // From this turn on, a Recce army's own routine MoveArmy score (Задача 1/2 only — see
        // AiScoutPlanner.ReconMoveWeight, the sole reader) starts tapering off instead of
        // staying flat at reconBaseWeight forever. Early game, scouting SHOULD win the arbiter
        // over everything else — there's nothing else worth doing with a fresh map.
        // 2026-08-23 (project owner's own call): assembly/request candidates (SpawnReconArmy/
        // AssembleRecceScout/PlayCard-Recce) now read off this same taper too — building the
        // scout pipeline shouldn't stay a flat, undecayed priority once actual scouting itself
        // has already faded well past it. TryFlee's own score is the one exception (see
        // scoutFleeBonus below) — a scout fleeing a real threat must not lose urgency just
        // because the turn counter has moved on.
        public const int reconPriorityDecayStartTurn = 5;
        // Flat reduction per turn past reconPriorityDecayStartTurn, subtracted from
        // reconBaseWeight for a MoveArmy/assembly/request candidate — never below
        // reconPriorityDecayFloor.
        public const float reconPriorityDecayPerTurn = 5f;
        // A scouting move should still beat outright idleness (managementFallbackHighScore/Low)
        // even once fully decayed — this floor sits comfortably above those.
        public const float reconPriorityDecayFloor = 60f;

        // ---- Разведка — сборка Recce-состава ----
        // Added on top of reconBaseWeight (negative) — no empty army anywhere to receive a Recce
        // card/unit yet. Below a real recon move, generally above Менеджмент's own flat Reserve
        // fallback (managementFallbackHighScore/Low) so a genuine Разведка need still wins.
        // Was -100 until the project owner's own 2026-08-19 follow-up: post-rebalance that left
        // the candidate at reconBaseWeight-100 = 0, actually BELOW managementFallbackHighScore
        // (15) despite the comment above — the request could lose the tie-break to outright
        // Менеджмент idleness. -10 keeps the candidate at 90, comfortably above both fallback
        // tiers again.
        public const float reconRequestArmyPenalty = -10f;
        // reconRequestCardPenalty removed 2026-08-19 (project owner's own follow-up, same shape
        // as reconRequestArmyPenalty's own fix above): an empty army exists, waiting on a
        // matching Recce card from hand — used to score reconBaseWeight-60 = 40. This step of the
        // Разведка pipeline (empty army already exists, card is the only thing missing) is now
        // scored at the plain reconBaseWeight, same as a real recon move. Moot as a tie-break
        // concern since 2026-08-19's later change: Recce cards no longer reach
        // AiManagementPlanner.TryPlayCardCandidates at all (see cardRoleBacklogShareWeight's own
        // comment) — Разведка is the only path a Recce card ever gets played through now, nothing
        // left to lose a tie against.
        // Added on top of reconBaseWeight — a Recce-tagged unit/hero is already deployed and
        // sitting on the SAME hex as an empty army, just buried inside a bigger army; see
        // AiScoutPlanner.FindBuriedRecceUnit for why this is rare in practice.
        // 2026-08-22 rebalance (project owner's own call): +20 → −5. At +20 this scored 120,
        // ABOVE every real VisitHex move (reconBaseWeight=100 or less once decay starts) —
        // meaning "assemble one more scout" unconditionally preempted "actually walk the scout(s)
        // we already have" every single time a buried Recce member existed anywhere, which combined
        // with GarrisonReorgTask's own consolidation (sweeps a not-yet-departed solo Recce back
        // into the garrison at end of turn — see that class's own class comment point 1.2) fed an
        // endless extract→never-move→re-bury→extract-again loop that starved actual scouting.
        // −5 keeps this ordering instead: 100 (a scout's own move, or PlayCard handing us one
        // outright) → 95 (assemble one from an already-deployed member) → 90 (reconRequestArmyPenalty,
        // build one from nothing) — assembly still comfortably beats spinning up a brand new empty
        // army from scratch, but never again outranks just using a scout that's ready to go.
        public const float reconAssemblePenalty = -5f;

        // ---- Агрессия — Задача 1 (Зачистка нейтралов/эвентов) ----
        // Target = a known neutral army and/or the Hex Event it may be guarding — composition is
        // no longer a fixed predicate (see RaidWeakerArmyTask's own class comment): assembled/
        // picked via WorthIt against whichever target is chosen, up to this many raid tasks
        // running at once.
        // 2026-08-20, project owner's own call — dropped from 2: only one raid campaign running
        // at a time, tune back up later depending on how this plays.
        public const int maxConcurrentRaid = 1;
        // RaidWeakerArmyTask.FindTarget's own internal-only ranking term (2026-08-23, project
        // owner's own call, same "never leaks into the cross-category score" treatment
        // scoutProximityWeight/buildHeroTravelCostWeight already get) — added on top of
        // ProximityScore's own distance terms, weighted by WinChanceAgainst(army, threat) (0..1,
        // the SAME honest win chance IsReady itself decides readiness against). Without this, a
        // strong-but-close target could outrank a weak-but-slightly-farther one purely on
        // proximity, even though the weak one needs little to no further assembly and the strong
        // one might never actually finish gathering enough force. Roughly "a full 0%→100% win-
        // chance swing is worth about 6 hexes of proximity" at this default — tune independently.
        public const float raidWinChanceRankWeight = 30f;
        // RaidWeakerArmyTask.IsReady's own readiness bar for a VOLUNTARY raid specifically
        // (2026-08-24 P1 fix, project owner's own report — "50% слишком близко к coin flip").
        // IsReady's own raw-stats/ThreatStrength overloads still default to a bare >0.5f for
        // every OTHER caller (Оборона's reactive intercepts, a raid's own in-transit counter-
        // attack, BuildBase's opportunistic detour) — those are reactions to a threat that showed
        // up on its own, where declining still leaves the army standing right next to it, so a
        // close-to-even fight is an acceptable gamble. A voluntary Raid CHOOSES to march on a
        // target and commit AP/turns getting there — the same asymmetry defenceActiveWinChance
        // already prices in for Оборона's own Active posture (a 60/40 target, not 50/50) — so it
        // should demand real odds, not a coin flip, before setting out. 0.65 (not defenceActive
        // WinChance's own 0.6) is the project owner's own starting pick, tune independently.
        public const float raidMinimumWinChance = 0.65f;
        // Retarget hysteresis (2026-08-24, project owner's own report) —
        // AiAggressionPlanner.TryRaidAssembleCandidates' own StillAssembling retarget check no
        // longer switches off the CURRENT TargetHex just because ANY other known target scores
        // marginally higher this exact step (RaidWeakerArmyTask.ScoreTarget re-ranks the old hex
        // the same honest way FindTarget's own scan ranks every candidate) — the new target has to
        // beat it by MORE than this, or win outright via a readiness transition or the old target
        // going invalid (see that method's own comment for the full override list). 5f mirrors
        // scoutProximityWeight's own per-hex weight — a new target has to be worth roughly one full
        // hex of proximity better than the old one, not just noise, before the force gathered so
        // far toward the old one gets abandoned. Only matters while StillAssembling — once the army
        // starts travelling this whole retarget check doesn't run at all any more (see that
        // method's own comment), so nothing changes mid-route.
        public const float raidRetargetMinImprovement = 5f;
        // raidReadyArmyBonus removed 2026-08-20 (project owner's own call) — a target already
        // beatable by an existing idle army as-is (RaidWeakerArmyTask's own "fast path", no
        // assembly needed) now scores the same flat aggressionBaseWeight as any other ready
        // continuation, no separate top-up.
        // A known non-neutral (real player) army within raidThreatRadius of the raiding army's own
        // hex — if our current force still beats it, added on top of the normal score for
        // attacking THAT army this turn instead of the original neutral/event target (see
        // RaidWeakerArmyTask's own threat-reaction comment); if our force does NOT beat it, this
        // task retreats to the garrison instead — no bonus applies to that branch at all, it isn't
        // a scored candidate, just a forced redirect.
        // 2026-08-20 rebalance (project owner's own call): 30 → 20.
        public const float raidCounterAttackBonus = 20f;
        public const int raidThreatRadius = 2;
        // raidCommittedBonus removed 2026-08-19 — superseded by Разведка's own
        // reconAggressionSuppressionPenalty (see above): keeping a committed raid ahead of routine
        // scouting is now handled by suppressing Разведка's score instead of inflating Агрессия's.
        // Recall step's own score — an idle army elsewhere on the map walking back toward the
        // garrison to be folded into an assembling raid force (see RaidWeakerArmyTask's own
        // "Композиция" comment), OR nothing left to do at all (TryRaidReturnHomeCandidates — merged
        // in here 2026-08-20, project owner's own call: same "walk home, low priority" meaning as
        // the old separate aggressionWaitScore, no need for two constants). Retuned 2026-08-20
        // (was -15, before that 30) — still below routine Разведка/Экономика work, but not so low
        // it never wins arbitration at all against genuine idleness.
        public const float raidRecallScore = 85f;
        // Раздел 5 ("рейд экономики") no longer carries its own bonus (removed 2026-08-19,
        // project owner's own call — raidCounterAttackBonus already covers "attack a known enemy
        // target we're stronger than"; an enemy building is now just another RaidWeakerArmyTask
        // target scored on ProximityScore alone, same as any neutral/event target).
        // Сборка с нуля (AiTurnController.TryRaidAssembleCandidates) — own dedicated numbers,
        // same shape as Разведка's reconRequestArmyPenalty/reconAssemblePenalty but not shared with
        // them (each task's own copy, established pattern by now). Also reused as-is by
        // AiDefencePlanner's own request-new-army fallback — same "spend AP on a fresh empty army"
        // situation, no need for a second copy. 2026-08-20 rebalance: -30 → -5, now paired with a
        // guard (see TryRaidAssembleCandidates/AiDefencePlanner.TryStartDefenceCandidates) that
        // skips this candidate entirely whenever an idle empty army already exists anywhere on the
        // map — this penalty only ever applies to the genuinely-nothing-spare case now.
        public const float raidRequestArmyPenalty = -5f;
        // 2026-08-20 rebalance (project owner's own call): 20 → 10.
        public const float raidAssembleBonus = 10f;
        // TryRaidRegroupCandidates' own dispatch step, AND TryContinueRaidTask's own in-flight
        // "weakened mid-travel, not from an enemy threat" branch (both AiDecision.
        // DispatchReinforcement) — a field army chose to wait rather than march home itself
        // (cheaper per its own AP/distance comparison), so a single non-hero courier peels off
        // from the garrison. 2026-08-20 rebalance: flattened to aggressionBaseWeight + 15 (was a
        // standalone 200) — above raidAssembleBonus's own 110, below raidCounterAttackBonus's 120.
        public const float raidReinforceDispatchScore = aggressionBaseWeight + 15f;

        // ---- Агрессия — capture-step opportunity nudge (Feature 3, 2026-08-24; narrowed to a
        // true next-hex bias 2026-08-24 P0 fix, project owner's own code-review report) ----
        // RaidWeakerArmyTask.FindCaptureStepDestination's own detour budget (project owner's own
        // report: the opportunity-capture mechanism itself already works — RaidWeakerArmyTask.
        // FindTarget's own Section 5 already logs "enemy building at (...), no known guard, score 100"
        // and an army does start moving toward one once it wins FindTarget's own ranking outright —
        // the actual gap is that an army ALREADY travelling toward some OTHER destination never
        // deviates for a DIFFERENT such opportunity it happens to pass close by, since FindTarget
        // only ever gets consulted when picking a brand-new target, not mid-route). The FIRST
        // shipped version of this let a whole MoveArmy decision aim its full destination straight
        // at the candidate building — a real multi-hex, potentially multi-turn route override, not
        // a next-hex nudge, and it compared raw HexGridMath.Distance instead of real terrain-
        // weighted cost. Narrowed same-day: this is now the REAL PATH-COST (HexPathfinder.
        // FindPath.TotalCost, not hex count — see FindCaptureStepDestination's own comment) budget
        // for how much extra the OVERALL route (current hex → the building's own next-hex-of-travel
        // → the real destination) may cost over the direct route before the closer real destination
        // wins outright instead. Internal-only, same "never leaks into the cross-category
        // AiDecision.Score" scoping raidWinChanceRankWeight/buildHeroTravelCostWeight already get in
        // this file — only ever picks THIS STEP's own actual move destination among otherwise-equal
        // in-progress-movement candidates, never the task's own long-term TargetHex/HomeHex (see
        // AiAggressionPlanner's own call sites for where it's applied, and its own explicit
        // exclusions for Scout retreat/citadel emergency/ReinforceSwap courier/BuildBase-BuildFacility
        // travel — none of those may get side-tracked by this).
        public const int captureStepDetourTolerance = 2;
        // The two bonuses FindCaptureStepDestination's own next-hex bias actually picks between
        // (2026-08-24 P0 fix) — internal-only, same scoping as captureStepDetourTolerance above.
        // captureStepBonus — the reachable next hex of movement toward the real destination is
        // ALSO, this step, literally adjacent enough that the qualifying building's own hex IS the
        // next hex a route there would enter — capturing it costs nothing beyond what ordinary
        // movement already would this step, so this always outranks the smaller approach bonus
        // below.
        public const float captureStepBonus = 10f;
        // captureApproachBonus — the building isn't reachable as THIS step's own next hex yet, but
        // biasing toward it (within captureStepDetourTolerance's own real-cost budget) meaningfully
        // shortens the route to it for a later step. Deliberately smaller than captureStepBonus — a
        // multi-step approach is still just a bias on top of the ordinary route, not a reason to
        // treat it as urgently as an immediate, free capture.
        public const float captureApproachBonus = 4f;

        // ---- Агрессия — Задача 2 (Постройка дополнительной базы) ----
        // Trigger gate — see BuildBaseTask's own class comment for the full trigger/condition list;
        // this is just the turn-number floor, checked fresh every step like every other trigger
        // here (project owner's own call — not a one-time "unlocked at turn 10" latch).
        public const int buildBaseMinTurn = 7;
        // At most one base-building campaign running at once — same reasoning as maxConcurrentRaid.
        public const int maxConcurrentBuildBase = 1;
        // "Агрессивная армия примерно равна силе активных армий противника" — the composing
        // army's own (Attack+Defense) must reach at least this SHARE of the single STRONGEST real
        // enemy army anywhere on the map, among every opponent (AiAggressionPlanner.
        // RequiredBuildBaseStrength — a deliberate cheat reading live ArmyData, same value whether
        // there's 1, 2, or 3+ opponents; 2026-08-22, project owner's own call, superseding the
        // 2026-08-21 "sum of each known enemy player's own honest-memory max" version, which turned
        // out to have its own bug — a stale AiMapMemory sighting could inflate the requirement
        // forever, see RequiredBuildBaseStrength's own comment). "Примерно равна", not "beats
        // outright", so a floor below 1.0 rather than a WorthIt.Beats-style strict comparison.
        // 2026-08-22 rebalance (project owner's own call): 0.8 → 0.4 — a second base is itself a
        // strength investment (new production/defense, a forward position), not a reward for
        // already being strong, so the AI no longer needs to be almost caught up with the single
        // strongest enemy army before it's allowed to commit to one; being able to field at least
        // 40% of that army's own combined Attack+Defense is enough of a floor to rule out founding
        // one with a hero-led army too weak to survive contact at all.
        public const float buildBaseStrengthToleranceRatio = 0.4f;
        // Travel score (start-new dispatch AND ordinary in-flight continuation, both in
        // AiAggressionPlanner) — used to be plain aggressionBaseWeight, tied EXACTLY with
        // RaidWeakerArmy's own ordinary travel score. AiTurnController.Decide's own candidate list
        // adds the Raid continuation earlier than the BuildBase one every step (see
        // TryContinueRaidTask/TryContinueBuildBaseTask's own call order there), and ties break by
        // list order (`candidate.Score > best.Score`, strict), so on an exact tie BuildBase always
        // lost arbitration for that step (2026-08-21 simulation report finding). +5 (project
        // owner's own pick) clears the tie without displacing BuildBase from its overall tier —
        // still well below buildBaseExecuteScore/raidCounterAttackBonus reactions.
        public const float buildBaseTravelBonus = 5f;
        // ARRIVED-at-target execution step's own score — a bump above ordinary travel
        // (aggressionBaseWeight + buildBaseTravelBonus, see above), same shape
        // raidReinforceDispatchScore already uses for "finish the job now that we're here".
        // 2026-08-23 (project owner's own ladder spec): +15 → +20, same tier/offset
        // raidCounterAttackBonus already uses (aggressionBaseWeight+20=120) — "BuildBase Execute"
        // now sits on the same "немедленное завершение" rung as every other category's own execute/
        // counter-attack reaction, not one rung below it.
        public const float buildBaseExecuteScore = aggressionBaseWeight + 20f;
        // 2026-08-24 rewrite (project owner's own report): BuildBaseTask.FindTargetHex used to
        // project a single world-space "aim point" a fixed number of hexes out and only ever
        // scored that hex's own immediate neighbors — the old buildBaseForwardDistanceHexes(4)
        // plus a world-units-per-hex approximation that only held exactly along one grid
        // direction, so the REAL HexGridMath.Distance from the citadel could land well past 4 in
        // other directions (project owner's own log read: a base landed 6 hexes out this way),
        // and any good hex outside that seven-hex neighbor patch was never even considered.
        // FindTargetHex now sweeps every legal hex on the whole map instead (see HexMap.AllCoords)
        // and ranks each one honestly by real hex distance from whichever of the player's own
        // Base-tagged buildings is NEAREST to that specific candidate (not always the starting
        // citadel — see IsLegalHex's own nearestOwnBase out param) — a second, third, etc. base
        // naturally chains outward from whichever base is actually closest, rather than every one
        // re-measuring from the original citadel. buildBasePreferredDistance is the sweet spot
        // ScoreCandidateHex's own distance term aims for; buildBaseMinDistanceFromExistingBase/
        // buildBaseMaxDistanceFromExistingBase are the hard legality floor/ceiling around it.
        public const int buildBasePreferredDistance = 3;
        // Target hex legality floor — a candidate strictly closer than this to whichever of the
        // player's own Base-tagged buildings is nearest to it is illegal (too cramped, would
        // overlap that base's own useful radius) — the starting citadel itself always counts as
        // one of those buildings. 2026-08-24 (project owner's own spec, alongside the
        // buildBaseMaxDistanceFromExistingBase ceiling below): previously a single "never within 3"
        // constant made the EFFECTIVE minimum legal distance 4 (distance<=3 was illegal) with no
        // ceiling at all; now min/max are both explicit and inclusive (distance must be >= min and
        // <= max), matching the project owner's own "допустимый диапазон 2–4" spec directly rather
        // than through an off-by-one exclusive floor.
        public const int buildBaseMinDistanceFromExistingBase = 2;
        // Target hex legality ceiling — see buildBaseMinDistanceFromExistingBase's own comment.
        // Paired with buildBasePreferredDistance(3) landing safely inside [min, max].
        public const int buildBaseMaxDistanceFromExistingBase = 4;
        // ScoreCandidateHex's own per-hex-of-deviation penalty from buildBasePreferredDistance —
        // keeps the internal ranking centered on the sweet spot even though every candidate in
        // [buildBaseMinDistanceFromExistingBase, buildBaseMaxDistanceFromExistingBase] is legal.
        public const float buildBaseDistanceWeight = 8f;
        // ScoreCandidateHex's own SOFT directional term (2026-08-24 rewrite, project owner's own
        // spec point 2: "вектор должен давать дополнительный score... но не должен делать боковой
        // ресурсный хекс нелегальным") — a dot product between the unit vector from the candidate's
        // own nearest-own-base anchor toward the candidate, and that same anchor's own bisector
        // direction toward every known enemy citadel (same bisector math FindTargetHex's own old
        // aim-point version used, just no longer the sole determinant of WHICH hexes get scored at
        // all). Ranges roughly [-1, 1] before this weight, so a hex built exactly backward from the
        // enemy loses this much, and one built exactly toward them gains it — never enough on its
        // own to outweigh a strong defense/resource read on a good lateral hex (buildBaseResource
        // TypeWeight/buildBaseResourceSiteMergeBonus/buildBaseDefenseBonusWeight's own scale).
        public const float buildBaseForwardAlignmentWeight = 15f;
        // ScoreCandidateHex's own economic-awareness term (2026-08-24 rewrite, project owner's own
        // spec point 3) — a flat bonus for a candidate this player already knows carries a resource
        // bonus (AiMapMemory.IsResourceHexKnown — "видел, не обязательно посетил", same honest
        // fog-of-war rule BuildFacilityTask/ResourcesScrapTask's own hex picks already use) but
        // hasn't been built on at all yet. Deliberately flat, not scaled by the hex's own actual
        // yield amount — this codebase's AiMapMemory only ever remembers a hex's DOMINANT resource
        // TYPE (see AiEconomyPlanner.DominantResourceType's own comment: "a hex only ever carries
        // one meaningful bonus in practice"), never a magnitude, so there's no honestly-known
        // amount to weight this by without reading the hidden ResourceYields directly (a cheat this
        // class explicitly avoids elsewhere — see HasThreateningEnemyNear's own comment on the one
        // deliberate exception it takes). A hex that already has a mergeable resource SITE built on
        // it keeps using buildBaseResourceSiteMergeBonus instead (strictly better — it preserves a
        // real standing Facility, not just a known bonus), so the two never both apply to the same
        // candidate.
        public const float buildBaseResourceTypeWeight = 15f;
        // ScoreCandidateHex's own second-type bonus (2026-08-24 P2 fix, project owner's own
        // report) — on TOP of buildBaseResourceTypeWeight, once a known hex carries 2+ resource
        // types rather than just 1. Still never reads a hidden YIELD AMOUNT (see
        // buildBaseResourceTypeWeight's own comment on why that stays off-limits) — only COUNTS how
        // many of the hex's own resource types are non-zero via HexResourceBonusRegistry.GetBonus,
        // the same live registry read BuildFacilityTask's own pre-pass already performs once a hex
        // is confirmed AiMapMemory.IsResourceHexKnown — a hex's own resource TYPE SET is a static,
        // permanent property (never changes turn to turn), so reading it live for an
        // already-known hex is exactly as honest as the single-dominant-type read this class
        // already did before this fix, just not throwing away the "how many" part of that same
        // already-permitted read.
        public const float buildBaseMultiResourceBonus = 10f;
        // Both the target-selection pre-filter (BuildBaseTask.FindTargetHex skips a hex with a
        // threatening known non-neutral sighting this close) and the cancel condition once a task
        // is actually under way (a known enemy sighted within this radius of the target, that could
        // actually beat the army building there, cancels the task outright). 2026-08-22 rebalance
        // (project owner's own call): 4 → 2, alongside switching the check itself from bare
        // presence to a real WorthIt read (see buildBaseMinWinChance and BuildBaseTask.
        // HasThreateningEnemyNear) — a strength-gated check can afford to sit closer without
        // over-cancelling on a real threat that hasn't actually closed the distance yet.
        public const int buildBaseCancelRadius = 2;
        // BuildBaseTask.HasThreateningEnemyNear's own floor (2026-08-22, project owner's own
        // simplification — reframed from "the sighting's own win chance" to the more direct "OUR
        // own win chance"): the BUILDING ARMY's own win chance against a known nearby sighting
        // (WorthIt.WinChance, buildingArmy as attacker) must stay AT OR ABOVE this before the hex
        // stays legal / the task keeps going. Below it — cancel. Not the usual 0.5 "would win" bar
        // every other readiness check in this codebase uses — this is a construction site, not a
        // fight either side is committing to, so only a real long-shot (below 30%) is worth
        // abandoning the spot over.
        public const float buildBaseMinWinChance = 0.3f;
        // Internal (non-cross-category) hex-ranking weights only — BuildBaseTask.ScoreCandidateHex
        // never leaks these into AiDecision.Score, same principle BuildFacilityTask.RankHex/
        // RaidWeakerArmyTask.ProximityScore already establish for their own internal hex picks.
        public const float buildBaseDefenseBonusWeight = 10f;
        public const float buildBaseResourceSiteMergeBonus = 20f;
        // See AiTask.BuildBaseWaitStartedTurn's own comment — real elapsed turns the task may sit at
        // its own target hex genuinely unable to afford the Base card (reservation included) before
        // giving up and freeing the army rather than holding it hostage to a stale plan. First-pass
        // placeholder, same as every other freshly-added BuildBase tunable — flagged for the
        // project owner's own tuning later.
        public const int buildBaseMaxWaitTurns = 5;
        // See AiTask.GarrisonSeedStartedTurn's own comment (Feature 2, 2026-08-24; turn-boundary
        // P1 fix same day) — how many REAL GAME TURNS the AwaitingGarrisonSeed phase may elapse
        // unable to spare a non-hero unit for the new garrison before giving up and completing the
        // task anyway. Deliberately short relative to buildBaseMaxWaitTurns — unlike that wait
        // (saving up AP/resources, which genuinely improves with more turns), a hero-only builder
        // army isn't going to grow a spare non-hero member just by waiting longer, so there's
        // nothing to gain from a long timeout here. While this counts down, AiManagementPlanner.
        // FindPlacement/GarrisonHexesForPlacement already gives this same garrison hex front-of-
        // queue priority for ANY freshly played Unit/Hero card the WHOLE time AwaitingGarrisonSeed
        // is true, not just after this timeout fires — a real routing PREFERENCE (not a hard
        // reservation), judged sufficient here (2026-08-24 code review) since this codebase has no
        // separate "reserve a future unit/card" primitive to reach for instead
        // (AiResourceReservation only ever reserves raw resource POOLS toward a task's own build
        // cost, never a specific future unit) — building one would be a materially bigger feature
        // than this timeout fix calls for, so completing on timeout with an empty garrison (left to
        // that same routing nudge from here on) stays the intended behavior.
        public const int garrisonSeedMaxWaitTurns = 2;

        // ---- Менеджмент — Feature 4B (застрявшие одиночные полевые армии), P0 fix 2026-08-24 ----
        // AiTaskKind.ReturnForConsolidation's own competing score, now that it's a real persistent
        // task advanced through AiTurnController.Decide's own arbiter instead of a free end-of-turn
        // sweep (see that enum value's own comment). Same tier as managementReorgScore (80) — "a
        // real in-progress task, not idle housekeeping" — comparable to Агрессия's own
        // raidRecallScore(85) for the identical "isolated idle army walks home" shape, just under
        // Менеджмент since this army was never Агрессия-task-claimed to begin with. Own named
        // constant rather than reusing managementReorgScore directly (2026-08-24, same reasoning
        // AiConfig.defenceActiveAssemblyScore's own comment already gives for not reusing
        // defenceActiveScore) — an unrelated future retune of one must never silently move the
        // other.
        public const float returnForConsolidationWeight = 80f;

        // ---- Оборона (Patrol / Active / Turtle) ----
        // Full redesign 2026-08-21 (project owner's own spec) — ONE persistent DefendCitadel task/
        // army cycling through three Posture values (see AiTask.AiDefencePosture) rather than a
        // single reactive "attack the threat" shape. Triggers/composition retuned again 2026-08-22
        // (project owner's own follow-up spec — explicit per-posture triggers, WorthIt-only
        // comparisons everywhere). See AiDefencePlanner's own class comment for the full decision
        // tree.
        // Raised from 1 to 2 (2026-08-21, project owner's own call) — a later-founded second base
        // now fields its own dedicated defender rather than sharing the single roaming one with the
        // citadel (see AiTask.HomeHex and AiDefencePlanner.TryStartDefenceCandidates' own per-home
        // loop). The citadel is still serviced first every step — a hard tie-break, not a scoring
        // bonus — so a second base's own assembly only ever proceeds once the citadel's own task has
        // nothing left to ask for that step.
        public const int maxConcurrentDefend = 2;
        // 1.1 (Active) — a known non-neutral army within this many hexes of ANY of this player's
        // own Base-tagged hexes (citadel, or a later-built Base) is a real threat worth reacting
        // to. Wider than the old raidThreatRadius(2) it replaces for this purpose — the project
        // owner's own spec ("в пределах пяти хексов от любого хекса с базой"). This alone is 1.1's
        // own TRIGGER (2026-08-22, project owner's own follow-up spec — "видел или видит армию", in
        // radius) — no separate reachability-in-turns gate any more (removed alongside
        // defenceReachTurns/TravelTurns): a sighting this close simply IS worth reacting to, full
        // stop. Whether the task actually moves to attack THIS step is a composition question
        // instead — see defenceActiveWinChance below.
        public const int defenceReactionRadius = 5;
        // 1.1 — Active's own chase-abandon radius (2026-08-22, project owner's own follow-up spec —
        // "не надо преследовать если армия врага ушла дальше шести хексов от цитадели"): the
        // sighted target must stay within this many hexes of the task's own HomeHex (the citadel,
        // or a later-founded base for its own task — NOT the pursuing army's current hex; measured
        // from home the same way defenceReactionRadius itself is) for Active to keep pursuing it,
        // re-checked fresh every step exactly like every other trigger in this file — the chase
        // simply stops the moment the target's own known position drifts past this, no separate
        // "gave up" bookkeeping needed (an army that arrives at a last-known hex and finds nobody
        // there falls through to ordinary Patrol from wherever it ended up, same as any other "no
        // target" step). Deliberately WIDER than defenceReactionRadius(5) — that one only gates
        // STARTING a chase; a target that drifts a little past the trigger radius while already
        // being pursued shouldn't immediately cancel the chase that radius itself just started, or
        // Active would flip-flop start/stop right at the boundary. See
        // AiDefencePlanner.FindActiveThreatSighting.
        public const int defenceChaseAbandonRadius = 6;
        // 1.1 — Active's own composition target, dynamic against whichever real sighting triggered
        // it (2026-08-22, project owner's own spec: "сильнее... шанс победы 60 на 40%... если армии
        // равны то это 50 на 50%") — WorthIt.WinChance must clear this before the task actually
        // moves to intercept rather than keep assembling in place. 0.5 would be a bare coin-flip
        // "technically wins"; 0.6 is the project owner's own explicit margin.
        public const float defenceActiveWinChance = 0.6f;
        // AiDefencePlanner.TryStrengthenCandidate's own full-but-insufficient upgrade gate
        // (2026-08-24 fix, project owner's own report: Rust Tank→Scrap Mortar→Rad Brute→Colossus,
        // four swaps in a row against the same target, each a genuine raw Defense+Attack gain yet
        // the army never got measurably closer to actually clearing defenceActiveWinChance above).
        // A candidate swap is only worth issuing if it closes a WorthIt.CanDamageAll coverage gap
        // outright, or buys at least this much real WinChance against the confirmed sighting —
        // raw power alone no longer qualifies. Small on purpose: this only needs to rule out
        // swaps that don't move the needle at all, not demand a huge single-swap leap.
        public const float defenceSwapMinWinChanceGain = 0.03f;
        // 1.3 (Turtle) — AiDefencePlanner.IsUnderSiege's own trigger radius: a known non-neutral
        // army THIS CLOSE to the citadel, stronger (WorthIt) than the current DefendCitadel task's
        // own army (or the bare garrison if no task exists yet) forces full alarm — mass at the
        // citadel, recall active raids (see AiAggressionPlanner). Narrower than
        // defenceReactionRadius on purpose: 1.1 reacts early at range, Turtle is the "it's already
        // at the door and we can't win" fallback.
        public const int siegeRadius = 4;
        // 1.2 (Patrol) — how far from the citadel this player's own extraction facilities are
        // still patrol targets. Same number as defenceReactionRadius today, own constant since the
        // project owner specified it independently (nothing requires the two to stay equal).
        public const int patrolRadius = 5;
        // 1.2 — Patrol's own fixed composition target (2026-08-22, project owner's own spec: "два
        // юнита или герой + два юнита") — replaces the old CheatEstimateRaiderThreat-sized target
        // entirely; a hero is welcome but never required to count as ready. CheatEstimateRaiderThreat
        // is now purely Patrol's own TRIGGER (see AiDefencePlanner.PatrolThreatPresent) — a real
        // scout/raider within defenceReactionRadius of a base is what starts/grows this task at all,
        // not what sizes it.
        public const int defencePatrolMinUnits = 2;
        // 1.2's own LOCAL reaction radius — distinct from defenceReactionRadius (which is measured
        // from the CITADEL, not from the patrol itself). A known non-neutral army THIS CLOSE to the
        // patrol's own current hex, regardless of distance from base, is Patrol's own business to
        // react to (2026-08-21, project owner's own follow-up spec — "на то он и патруль"): beat it
        // (WorthIt) → attack, then resume patrol; can't beat it → retreat to the garrison, one-way
        // (reuses AiTask.Retreating, same shape Агрессия's own outmatched-threat reaction already
        // uses). Deliberately tight (2, not patrolRadius/defenceReactionRadius's own 5) — this is
        // "something right next to me", not "somewhere in my patrol zone".
        public const int patrolLocalThreatRadius = 2;
        // 1.3.3 — Turtle's own forced raid-recall path avoids every hex within this many hexes of
        // the known threat (not just the threat's own hex) so a retreating army's own Recce isn't
        // spotted marching home weak — see AiAggressionPlanner.FindNextRetreatStep's own blockHex
        // parameter. The garrison hex itself is always exempt from this buffer (the project
        // owner's own call: the last step may enter home directly even inside the buffer).
        public const int defenceRetreatAvoidRadius = 1;
        // 1.1 — Active engagement/assembly score. Retuned 2026-08-21 (simulation report finding):
        // a flat 100 tied EXACTLY with Recon's own undecayed baseline and Aggression's own routine
        // continuation, both of which — the same simulation's own arbitration read of
        // AiTurnController.Decide's `candidate.Score > best.Score` — are gathered EARLIER in
        // Decide's own candidate list, so on a tie Оборона silently lost to routine scouting/raiding
        // instead of the "гарантированная немедленная реакция" the project owner asked for. Bumped
        // clear of economyBaseWeight (105) too, same shape Агрессия's own raidCounterAttackBonus
        // already uses for "react to a nearby known army" (aggressionBaseWeight + 20).
        public const float defenceActiveScore = 120f;
        // 1.1b — Active's own assembly/strengthen score, split out 2026-08-23 (project owner's own
        // report: "враг реально идёт на Base → Defence недостаточно сильна → нужный юнит лежит в
        // гарнизоне → Raid забирает его на 110 — это плохо"). TryStartDefenceCandidatesFor used to
        // score EVERY recruit/strengthen pull off DynamicPatrolUrgencyScore alone (ceiling
        // defencePatrolScore=90, deliberately below raidAssembleBonus's flat 110 tier — see that
        // method's own comment) even once activeSighting.HasValue was true, i.e. even once the
        // threat was no longer a proximity heuristic but the exact same confirmed AiMapMemory
        // sighting BuildPostureDecision's own Active branch reacts to — so a real, known army
        // closing on a base could still lose its own next recruit to a routine Raid assembly pull
        // every single step, right up until the moment it either finished assembling on its own or
        // the enemy actually arrived. Pinned to the same 120 tier as defenceActiveScore itself
        // (project owner's own call) — under a real confirmed threat, feeding the defender its
        // missing recruit IS the urgent response, exactly as time-critical as the engagement itself
        // would be if the force were already complete. Kept as its own named constant rather than
        // reusing defenceActiveScore directly so TryStartDefenceCandidatesFor's own assembly call
        // site stays self-documenting about which of the two concepts (buildup vs. actual
        // engagement) it's scoring — see that method's own comment.
        public const float defenceActiveAssemblyScore = 120f;
        // 1.2 — Patrol's own routine movement score, deliberately BELOW the real-work tier
        // (economyBaseWeight 105, reconBaseWeight/aggressionBaseWeight 100) — patrol is proactive
        // background coverage, not urgent, but still comfortably above every Менеджмент idle
        // fallback (managementFallbackHighScore 15). Known open question (simulation report): at
        // 90 Patrol can lose arbitration to Economy/Recon/Aggression on essentially every busy
        // step — left as-is pending the project owner's own call on whether that's acceptable.
        public const float defencePatrolScore = 90f;
        // SecureBase (2026-08-24, project owner's own spec) — a fresh/captured/weakened second
        // base's own initial-defence task, ranked above routine Patrol(90) — an unsecured base is
        // more urgent than ordinary background coverage — but below the real-threat tier
        // (defenceActiveScore/defenceActiveAssemblyScore 120, defenceTurtleScore/
        // defencePreemptScore 130) — a base with nobody actually attacking it yet still loses
        // arbitration to an army already fighting for its life. secureBaseTravelScore covers both
        // the courier's own dispatch (SecureBaseTask picks a donor + unit) and its travel toward
        // the target base; secureBaseDeliverScore is the "arrived, hand it over" step, one tier
        // up — same "travel vs. arrived/execute" split every other multi-step task in this codebase
        // already uses (see e.g. buildBaseTravelBonus/buildBaseExecuteScore).
        public const float secureBaseTravelScore = 100f;
        public const float secureBaseDeliverScore = 110f;
        // AiArmyRoles.IsBaseGarrisonSecure's own floor — a non-citadel base's own garrison counts
        // as genuinely secure once it holds at least this many combat-capable NON-HERO members
        // (a hero may sit alongside them, but never substitutes for this headcount — see that
        // method's own comment). Shared by SecureBaseTask's own trigger/completion, card-placement
        // routing (AiManagementPlanner.GarrisonHexesForPlacement), and the donor guard
        // (AiArmyRoles.CanSpareGarrisonMember) — one number, one place, per the project owner's
        // own "IsBaseGarrisonSecure нужен как минимум четырём механизмам" call.
        public const int secureBaseMinNonHeroUnits = 2;
        // AiArmyRoles.CanSpareGarrisonMember's own floor for the CITADEL specifically when called
        // with allowCitadelEmergency:false (2026-08-24 P0 fix, project owner's own report — see
        // that method's own comment) — SecureBaseTask's own donor search passes false so it can
        // never drain the citadel down to zero non-hero defenders while topping up a second base.
        // Same value as secureBaseMinNonHeroUnits today, kept as its own constant per the project
        // owner's own call so the two can be tuned independently later (the citadel arguably
        // deserves a higher floor than an ordinary second base once real balancing starts).
        public const int secureCitadelMinNonHeroUnits = 2;
        // AiManagementPlanner.TryPlayCardCandidates' own priority bump (2026-08-24 P1 fix, project
        // owner's own report) — a Unit card FindPlacement is about to route into a non-citadel base
        // that ISN'T secure yet gets this score instead of the ordinary Hero/Unit alternation score,
        // so "play the card that's already headed there" always outranks SecureBase's own courier
        // dispatch (secureBaseTravelScore=100) the way the project owner's own spec requires
        // ("сначала направлять в незащищённую базу подходящие Unit-карты"). Pinned to the same 110
        // tier as secureBaseDeliverScore — landing a card is exactly as good as a courier arriving,
        // whichever happens to be ready first.
        public const float secureBaseCardPlacementScore = 110f;
        // How many SecureBase tasks may be registered across this player's own bases at once —
        // same "don't let one Level-1 category spread itself across every base at once" intent
        // every other maxConcurrentX cap in this codebase already enforces (MaxConcurrentVisitHex/
        // MaxConcurrentRaid/maxConcurrentDefend). A brand-new base's own SecureBase task usually
        // resolves in one or two courier trips, so this stays a tight cap rather than a soft one.
        public const int maxConcurrentSecureBase = 1;
        // Patrol's own dynamic floor (2026-08-21, project owner's own "option 2" call) — the score
        // an assembling/growing Defence force gets when AiDefencePlanner.DynamicPatrolUrgencyScore
        // finds nothing worth reacting to anywhere near a base or facility hex. Deliberately below
        // aggressionBaseWeight/reconBaseWeight (100) so an empty map never lets Оборона's own
        // buildup outrank Агрессия just because it exists — the whole point of the dynamic score is
        // that early-game buildup with nothing nearby should NOT compete for AP/recruits against
        // real work. Still comfortably above managementFallbackHighScore so an idle army still
        // prefers slowly forming a patrol over doing nothing.
        public const float defencePatrolScoreFloor = 55f;
        // How far a real (cheat-read, see DynamicPatrolUrgencyScore's own comment) enemy army has to
        // be from a base/facility hex before it stops contributing to Patrol's own urgency at all —
        // one flat radius for every enemy army shape (2026-08-22, project owner's own follow-up
        // call, dropped the old scout-vs-real-army split this used to share with
        // patrolScoutDangerRadius, now removed — "если враг в радиусе 8 хексов, то нужен патруль и
        // всё", a scout included).
        public const int patrolDangerRadius = 8;
        // 1.3 — Turtle's own march-to/hold-at-citadel score. Used to be pinned exactly to
        // defenceActiveScore (a sortie out of Turtle IS a conversion into Active, see
        // AiDefencePlanner's own class comment) — split into its own value 2026-08-23 (project
        // owner's own top-of-arbiter ladder spec, full arbiter documented top-to-bottom): Turtle now
        // sits at the very top of the whole arbiter (130, "Citadel emergency"), one tier above
        // defenceActiveScore's own 120 ("tactical combat/execute") — the citadel actually massing
        // under an active siege must outrank ordinary tactical engagement, not merely tie it.
        public const float defenceTurtleScore = 130f;
        // AiDefencePlanner's own preempt tier — gated on IsUnderSiege (Turtle only, per the project
        // owner's own call: outside an active siege there's no urgent need to strip another
        // category's task for the citadel's sake). Retuned 2026-08-21 alongside defenceActiveScore
        // for the same reason (simulation report: a flat 100 tied routine Recon/Aggression work and
        // lost outright to Economy(110) — an "emergency reinforcement" must not be a coin flip
        // against ordinary turns). 120 → 130 (2026-08-23, project owner's own top-of-arbiter ladder
        // spec) — pinned to the same top "Citadel emergency" tier as defenceTurtleScore now that the
        // two have split apart, since an emergency field-army recall is exactly as urgent as Turtle's
        // own march-home. Also reused as-is by AiAggressionPlanner's own siege-forced raid recall
        // (see TryContinueRaidTask) and by AiDefencePlanner's own siege-strip of a StillAssembling
        // raid parked at the citadel (see TryDefencePreemptCandidates/FindSiegeRaidStripCandidate)
        // — the project owner's own call to keep every "siege demands this army/body NOW" reaction
        // on the same urgent tier, rather than a near-duplicate constant per reaction.
        public const float defencePreemptScore = 130f;
        // 1.2's own local retreat (AiDefencePlanner.ContinueLocalRetreat) — used to share
        // defenceActiveScore(120) with Active intercept/Local patrol attack (see that method's own
        // former comment). Split into its own value 2026-08-23 (project owner's own top-of-arbiter
        // ladder spec, explicit call): a patrol falling back to safety reads as urgent as a scout
        // fleeing a real threat (scoutFleeBonus's own 125 tier, "Scout Flee"), not merely as urgent
        // as ordinary tactical engagement — pinned to that same 125 tier instead of 120.
        public const float defenceRetreatScore = 125f;

        // ---- Разведка — реакция на угрозу (Задача 1) ----
        // A known enemy army within this many hexes of a scout's own current hex reroutes it
        // toward the garrison for one turn instead of whatever Задача 1 would otherwise propose.
        // Neutral armies never trigger this — see VisitHexTask.TryFlee.
        public const int scoutFleeRadius = 2;
        // Retuned 2026-08-20 (was 120, before that 50) — 120 pushed the total flee score to
        // ~210-220 (reconBaseWeight + this, minus AggressionSuppressionPenalty if a raid is
        // active), badly out of scale with the rest of the arbiter; 25 keeps the nominal total at
        // reconBaseWeight(100) + 25 = 125 — still reliably above every routine candidate (still
        // above scoutFleeRadius's own trigger radius reasoning: a scout under threat must win
        // arbitration over everything else the same step, just not by triple the score of
        // anything it's actually competing against).
        // 2026-08-23 (project owner's own call): a flee candidate's base stays the plain,
        // undecayed reconBaseWeight even past reconPriorityDecayStartTurn — unlike routine
        // MoveArmy/assembly candidates (see reconPriorityDecayPerTurn above), fleeing a real
        // threat shouldn't get weaker just because the turn counter has moved on.
        public const float scoutFleeBonus = 25f;

        // 2026-08-24 fix (project owner's own report): enemySightingMemoryTurns is only 2 turns,
        // so a scout that flees home and stops observing the threat let that sighting go stale
        // long before the actual enemy army had moved on — VisitHexTask.FindTarget's own
        // known-sighting exclusion then had nothing left to exclude, and the scout walked straight
        // back into the same still-there army every few turns (AiMapMemory.ScoutDangerZones). This
        // cooldown deliberately outlives enemySightingMemoryTurns so the sector actually stays
        // closed for a while after a retreat, not just until the sighting itself expires.
        public const int scoutDangerCooldownTurns = 4;
        // Same radius as scoutFleeRadius — the zone should cover exactly the area that would
        // trigger ANOTHER flee if re-entered, no wider.
        public const int scoutDangerRadius = scoutFleeRadius;

        // ---- Экономика — Задача 1 (Постройка facility) ----
        // "1 постройка за раз" — the project owner's own 2026-08-19 call: several concurrent builds
        // were each reserving a different resource type, between them locking card play out of all
        // four at once (see AiResourceReservation). The existing scarcity-switch (isScarcer, see
        // TryStartEconomyCandidates) still lets the one active hero redirect to a newly-found
        // scarcer hex even at this cap — only starting a genuinely NEW build is capped.
        public const int maxConcurrentBuildFacility = 1;
        // See AiEconomyPlanner.EconomyCommitmentMargin's own comment — how many units scarcer a
        // rival resource hex must be before it's worth redirecting a hero off an already-committed
        // BuildFacility task, scaled by how much progress that task has already made. First-pass
        // placeholder values, flagged for the project owner's own tuning later, same as every other
        // freshly-added tunable in this codebase.
        public const int economyCommitmentMarginSelected = 2;
        public const int economyCommitmentMarginEnRoute = 4;
        public const int economyCommitmentMarginArrived = 8;
        public const int economyCommitmentMarginReserved = 15;
        // buildFacilityReadyBonus/economyHeroDetachScore/economyReturnHomeScore all removed
        // 2026-08-19 (project owner's own call) — a task standing at its own base score
        // (economyBaseWeight, now 105) already reliably wins arbitration on its own; none of these
        // sub-steps needs its own inflated top-up any more the way they did stacked on the old
        // 200-point base.

        // ---- Задачи 1 уровня — дальность от цитадели (кросс-категорийно) ----
        // Shared by Экономика (BuildFacilityTask.ScoreHex) and Агрессия (RaidWeakerArmyTask.
        // ProximityScore) — project owner's own 2026-08-19 rebalance: "мы не особо хотим строить/
        // рейдить в далеке от цитадели", applied identically to both category base scores. NOT
        // shared with Разведка — VisitHex's own citadel-distance term (visitTargetCitadelWeight,
        // see above) stays purely internal to which hex VisitHexTask itself picks, by explicit
        // contrast with these two.
        public const int citadelPenaltyFreeRadius = 3;
        public const float citadelDistancePenaltyPerHex = 5f;

        // ---- Менеджмент — Починка юнита ----
        // Owned by AiManagementPlanner, not AiEconomyPlanner — see AiTask.cs's own AiTaskKind.
        // RepairUnit comment for why. Deliberately its own tier, not economyBaseWeight — cheap, so
        // it should usually beat a typical unpressured PlayCard score (managementBaseWeight + a
        // small role bonus, roughly 65-90 — see AiManagementPlanner.TryPlayCardCandidates), but
        // well below economyBaseWeight itself and below a heavily hand-backlogged PlayCard score,
        // so real pressure to play a card still wins on its own without any bespoke ordering rule
        // (see AiManagementPlanner.WouldBlockAffordableCard for the one explicit exception —
        // repair still yields for a turn if paying for it would specifically make a pricier,
        // otherwise-affordable Unit/Hero card unaffordable).
        public const float repairUnitBaseWeight = 90f;
        // Never start, and never continue, a BuildFacility task while a known NEUTRAL army sits
        // within this many hexes of the target — a neutral guarding the area isn't a threat to
        // react to (we can walk past/around one freely, per the project owner's own call — this
        // radius governs BUILDING near it, not travel), it's simply a bad spot to commit a facility
        // to (see BuildFacilityTask.HasNeutralThreat/HasAdjacentNeutralThreat, now the SAME radius
        // for both the initial hex pre-pass and an already-committed task's own cancel check —
        // 2026-08-21 fix, project owner's own report: a separate, wider neutralBuildAvoidRadius
        // used to gate cancellation alone, so a hex just outside the narrower start-trigger radius
        // but still inside that wider one would pass the pre-pass, get selected, spawn a fresh
        // hero-led army for it, and cancel again almost immediately — leaving a pile of empty,
        // never-reused army shells behind every single time that same hex got re-picked next turn).
        // Cancels the task outright (same as picking a different hex never having been offered in
        // the first place) so a better, unguarded hex gets picked instead — same hard-cancel
        // treatment a known ENEMY within economySafetyRadius now also gets (see BuildFacilityTask.
        // HasEnemyThreat, a genuinely different, combat-strength-based reaction — real enemy
        // players never route through this neutral-only radius); Задача 1 no longer has a temporary
        // one-turn retreat like Разведка's own tasks do, the project owner's own call that a hero
        // mid-build has nothing better to fall back to anyway.
        public const int neutralBuildTriggerRadius = 1;
        // Blunt safeguard against a permanently-stuck task (hex claimed by someone else, facility
        // slot full).
        public const int maxBuildAttempts = 3;
        // Экономика's own INTERNAL hex-ranking terms only (project owner's own 2026-08-19 call,
        // same "какой ресурс выбрать первым — не должно течь наружу" principle as Разведка's own
        // scoutProximityWeight/freshNeighborWeight) — used by BuildFacilityTask.RankHex to pick
        // WHICH known free resource hex TryStartEconomyCandidates commits to next, one per player
        // per step (maxConcurrentBuildFacility caps it at one build overall anyway). Never added to
        // BuildFacilityTask.ScoreHex — the cross-category score every OTHER category's candidate
        // actually competes against — any more.
        //
        // buildScarcityWeight — a scarcer type (lower root.GetResource) ranks higher, same
        // "строим то, чего меньше всего в закромах" heuristic Разведка · Задача 2 used to use.
        public const float buildScarcityWeight = 1f;
        // buildNoIncomeBonus — flat rank bonus for a resourceType with NO current income source at
        // all (see BuildFacilityTask.HasIncomeSource), replacing buildScarcityWeight's own
        // stockpile term entirely rather than adding to it (project owner's own call, 2026-08-17:
        // deficit must be judged by income first, current stock only second).
        public const float buildNoIncomeBonus = 100f;
        // ---- Экономика — Задача 1 · спрос руки на ресурс (Feature 1, 2026-08-24) ----
        // AiManagementPlanner.ComputeHandResourceDemand's own per-card cap (project owner's own
        // report: Thane ending turns with 8 unplayable Unit/Hero cards while Economy kept building
        // whatever local hex ranked best, never once asking what the unplayed HAND actually needed)
        // — caps each individual card's own contribution to a resourceType's total demand so ten
        // copies of one expensive card can't produce an unbounded weight; the deficit itself is
        // still read fresh per card (deficit = cardCost - AiResourceReservation.Available), this
        // only bounds how much ONE card may add to the running sum.
        public const float handDemandPerCardCap = 4f;
        // BuildFacilityTask.RankHex's own hand-demand term (see HandDemandBonus's own comment) —
        // internal hex-ranking weight only, same "never leaks into ScoreHex/AiDecision.Score"
        // scoping every other RankHex term in this file already documents. Applied only to whichever
        // resourceType currently carries the HAND's own single highest demand, never split across
        // several at once for the same hex.
        public const float handDemandRankWeight = 2f;

        // buildHeroTravelCostWeight — BuildFacilityTask.RankHex's own hero-movement term
        // (2026-08-23, project owner's own call): degrades a candidate hex by the REAL terrain-
        // weighted move cost (HexPathfinder.FindPath.TotalCost, not plain hex distance — see
        // HeroTravelCostScore's own comment) of whichever hero FindActor would actually send there.
        // Same order of magnitude as citadelDistancePenaltyPerHex since a path hex typically costs
        // ~1 (Mathf.Max(1, terrain.moveCost)) — tune independently if terrain costs in this project
        // ever skew much higher than that.
        public const float buildHeroTravelCostWeight = 5f;

        // ---- Экономика — Задача 2 (ResourcesScrap) ----
        // Added on top of economyBaseWeight — scrapping via a unit's own CollectX ability costs no
        // AP/resources, so it should still edge out a Задача 1 candidate, just by a slimmer margin
        // now (project owner's own 2026-08-19 rebalance: 20 → 5, was winning arbitration too
        // reliably).
        // 5 → 0 (2026-08-23, project owner's own ladder spec): "базовый Scrap Travel" now reads at
        // the exact same 105 tier as "базовый BuildFacility Travel"/BuildBase Travel, no separate
        // edge — the two Экономика sub-tasks no longer need to out-rank each other at the travel
        // stage now that ScoreHex's own modifiers (and the shared economyExecuteScore tier once
        // either one is ready to finish) already give real work its own priority.
        public const float resourceScrapBaseWeightBonus = 0f;
        // Задача 2's own INTERNAL hex-ranking term (project owner's own 2026-08-23 call, same
        // "which hex first, never leak into the cross-category score" split BuildFacilityTask.
        // RankHex already uses — see ResourcesScrapTask.RankHex) — degrades a candidate hex the
        // further its nearest available collector actually has to walk. Reuses
        // buildScarcityWeight's own sibling term (BuildFacilityTask.ScarcityBonus) for the
        // deficit half instead of a separate constant, since it's the exact same "income first,
        // stockpile second" formula either task's RankHex plugs a resourceType into.
        public const float resourceScrapDistancePenaltyPerHex = 1f;
        // Never start, and never continue, a ResourcesScrap task while a known enemy army sits
        // within this many hexes of the target. Shared with Задача 1's own enemy-threat check
        // (BuildFacilityTask.HasEnemyThreat) — one "how close is too close for Economy" number for
        // both tasks, each still free to get its own value later if that turns out wrong.
        public const int economySafetyRadius = 2;

        // ---- Менеджмент ----
        // Multi-base routing (2026-08-21, project owner's own call) — which of this player's own
        // garrisoned hexes (citadel or a later-founded base) card-play/reserve-spawn/garrison-reorg
        // should favor right now: whichever has more real, fog-of-war-honest enemy activity nearby
        // (AiMapMemory sightings only — deliberately NOT the raw-ArmyData cheat AiDefencePlanner.
        // CheatEstimateRaiderThreat/DynamicPatrolUrgencyScore use for Оборона's own proactive
        // patrol sizing; Менеджмент's routing only ever reacts to what's actually been scouted).
        // Same value as AiConfig.defenceReactionRadius today, own constant since the two measure
        // different things and may need to diverge later.
        public const int managementActivityRadius = 5;
        // "не надо их плодить каждый ход, одной армии про запас должно хватить" (2026-08-23,
        // project owner's own call — down from 2: the fallback-created spare kept ending up as
        // one more empty shell alongside whatever FindPlacement's own fallback tier had already
        // spun up for a card with nowhere else to go, so one spare reserve army now covers both
        // needs instead of two separate ones).
        public const int maxSpareArmies = 1;
        // Garrison stops accepting fresh PlayCard deposits (Unit or Hero card alike) once only
        // THIS many slots remain open — "если в гарнизоне уже заканчивается место (остаётся один
        // слот), юниты перераспределяются в резервную армию" (the project owner's own spec). Card
        // deposits only — GarrisonReorgTask.FindGarrisonOverflow used to aim for this exact same
        // "Capacity - 1" equilibrium from the eviction side too, but doesn't any more (2026-08-20,
        // project owner's own fix — see that method's own comment): it now evicts down to LITERAL
        // full capacity, not one below, so the garrison no longer settles at this same buffer from
        // both directions any more, just this one (card deposits).
        public const int garrisonReservedSlots = 1;
        // MaxActiveHeroArmies/minArmyStrengthShare removed 2026-08-19 (project owner's own call —
        // "что это вообще такое? = надо убрать"): FindPlacement no longer benches a Hero card in
        // garrison to stay under a hero-army cap — every Unit/Hero card now goes through the same
        // garrison-first placement (see FindPlacement's own comment), and
        // GarrisonReorgTask.FindGarrisonHeroToPromote no longer gated on it either (its own former
        // sibling, FindHeroEscortFromGarrison, has since been removed entirely — see
        // GarrisonReorgTask's own class comment). Nothing left reads either constant.
        // AiArmyRoles.IsSoloHeroAwaitingEscort's own fallback move — protecting this fragile,
        // escort-less hero outranks every OTHER Менеджмент action. 2026-08-20: 100 → 105.
        // 105 → 104 (2026-08-23, project owner's own report/spec): sitting at the exact same 105
        // shared by economyBaseWeight/aggressionBaseWeight/BuildBase's own travel tier let this
        // fallback WIN a same-step tie against a real, freshly-available Экономика candidate for
        // the very same hero (Decide's own arbiter only replaces `best` on a STRICT >, so whichever
        // tier is gathered first keeps a tie — this one is gathered before Экономика's own start
        // tier, see AiTurnController.Decide's own candidate order) — observed as a hero walking
        // itself home for an escort one step, then immediately being pulled straight back out to
        // build the next. One point below the shared 105 tier is enough to lose that tie to any
        // genuinely productive same-step candidate while still safely clearing every real
        // Менеджмент score (PlayCard ~65-90, managementReorgScore 80, managementBaseWeight 50) —
        // this fallback still fires as soon as nothing else wants the hero this step, exactly the
        // "not a real fallback tier" case it exists for.
        public const float managementReturnHomeScore = 104f;
        // Экономика · Задача 2's own detach-prerequisite base (see ResourceScrapDetachScore) —
        // garrison-overflow/consolidation no longer read a score at all any more (see
        // AiTurnController.RunGarrisonReorgPhase's own comment). Kept above PlayCard on purpose: an
        // Economy collector detach is a real
        // in-progress task, not idle housekeeping.
        public const float managementReorgScore = 80f;
        // Recce cards never reach TryPlayCardCandidates at all any more (2026-08-19, project
        // owner's own call — "ScoutPlanner должен сам решить куда положить своего скаута,
        // менеджеру об этом знать не обязательно") — AiScoutPlanner.
        // TryStartReconAssemblyCandidatesFor fetches and places its own matching Recce card
        // directly, at AiScoutPlanner.ReconMoveWeight (reconBaseWeight, decayed past
        // reconPriorityDecayStartTurn same as every other Разведка candidate — see that field's
        // own comment), without ever competing against this file's own managementBaseWeight.
        // Менеджмент's own backlog pressure below is Unit/Hero only.
        //
        // Scaled by a role's own SHARE of the unplayed Unit+Hero pool (unplayedThisRole /
        // (unplayedHero + unplayedUnit), 2026-08-21 fix, project owner's own report — a growing
        // backlog itself raises the urgency of playing ANY of them, rather than staying pinned at
        // a flat managementBaseWeight forever and routinely losing the tie-break to
        // RequestRaidArmy/SpawnReconArmy's own flat 50 (the project owner's own "AI won't deploy
        // units" report — cards just piled up in hand turn after turn). Deliberately a SHARE, not
        // a raw per-extra-card count the way this used to work (own report, 2026-08-21: "карта
        // героя не розыгрывается") — Hero cards are structurally rarer in this deck than Unit
        // cards, so a plain "+10 per card beyond the first" formula let Unit's own backlog climb
        // several times higher than Hero's ever could just from deck composition alone, no matter
        // how long Hero cards sat unplayed; cardRoleAlternationDamping's own halving on top of
        // that still never closed a 3:1+ gap, so Hero essentially never won. Share is bounded to
        // [0,1] for BOTH roles regardless of how lopsided the deck's own Hero:Unit ratio is — 50 →
        // a role that's 100% of the currently-unplayed pool tops out at managementBaseWeight 50 +
        // 50 = 100, the same ceiling either role could reach on its own, never structurally
        // favoring whichever role the deck simply deals more copies of. Unit and Hero cards each
        // read their own share of the SAME pool (they sum to 1 between them), but share this one
        // weight and formula — no separate Hero-only bonus any more (playHeroCardBonus removed
        // 2026-08-19, project owner's own call: "убираем playHeroCardBonus, альтернейшен закроет
        // необходимость в героях" — see cardRoleAlternationDamping below for the mechanism that's
        // meant to cover it instead).
        public const float cardRoleBacklogShareWeight = 50f;
        // Hero/Unit PlayCard alternation (see AiManagementPlanner's own "Разыгрывание карты —
        // чередование ролей" section, IsCardRoleCoolingDown/NotifyCardRolePlayed) — multiplies a
        // role's own backlog term down for the step right after THAT role's own card just got
        // played, until the OTHER role gets one played instead. The project owner's own
        // 2026-08-17 follow-up: with a hand holding several of both, the AI kept exhausting every
        // Hero card back-to-back before touching a single Unit (or vice versa) purely because
        // whichever role still had the taller backlog pile always kept winning — this makes
        // playing ONE card of a role cool that same role off for a turn, so a hand with 3 heroes
        // and 3 units alternates between them turn to turn instead. Lowered from 0.5 to 0.1
        // (2026-08-21 fix, project owner's own report — same "карта героя не розыгрывается"
        // investigation cardRoleBacklogShareWeight's own comment describes): even with backlog now
        // read as a bounded SHARE, 0.5 still isn't a strong enough discount to flip the winner once
        // one role's share gets much bigger than the other's — the just-played role only loses the
        // tie once damping × itsShare < theOtherRole'sShare, i.e. (since the two shares sum to 1)
        // once damping < otherShare / (1 − otherShare). At 0.5 that only ever holds once the
        // damped role's own share drops below 1/3 of the pool — comfortably above what a real
        // Hero-light hand (say 1 Hero in 9 relevant cards, share ≈0.1) ever reaches, so Unit kept
        // winning turn after turn regardless. 0.1 holds the alternation up to roughly a 1-in-9
        // split — the discounted role still wins outright once the OTHER role's hand is completely
        // empty (Partial, not full suppression — if the OTHER role has nothing left in hand at
        // all, this role still has to keep winning, just at this same discount).
        public const float cardRoleAlternationDamping = 0.1f;
        // Absolute deployment backlog pressure (2026-08-24, project owner's own report: cardRole
        // BacklogShareWeight above reads only a role's own SHARE of the unplayed Hero+Unit pool,
        // which is capped at 1.0 by construction — a hand holding 1 Unit card and a hand holding
        // 10 both score the exact same managementBaseWeight+cardRoleBacklogShareWeight ceiling
        // whenever nothing of the OTHER role is in hand, so a real pile-up of ready-to-play cards
        // never pushed PlayCard any higher than a single lonely one would, and kept losing every
        // step to routine Recon/Patrol scores well above it). This is a SEPARATE additive term on
        // top of that share-based score — the total unplayed Hero+Unit count in hand, past a small
        // soft cushion (managementBacklogSoftLimit) so an ordinary 1-3 card hand isn't pressured at
        // all, capped hard (managementDeploymentScoreCap) well below every real emergency tier
        // (Raid assembly/attack, Active Defence, Turtle — see their own constants) so a deep
        // backlog can out-rank routine Recon/Patrol without ever preempting an actual emergency.
        // Self-limiting by construction: playing a card lowers the backlog count, which lowers this
        // same bonus next step — no new persistent state, no runaway score.
        public const int managementBacklogSoftLimit = 3;
        public const float managementBacklogPerCardBonus = 5f;
        public const float managementBacklogBonusCap = 40f;
        public const float managementDeploymentScoreCap = 95f;
        // Unit card composition-fit — see AiManagementPlanner.UnitCompositionFitBonus's own
        // comment for the full list of gaps this checks (Defense/Attack imbalance, melee/ranged
        // imbalance, too many ability-heavy units, a critically wounded raid-force member of a
        // matching type, a counter-tech ability against an already-scouted enemy TypeTag). Flat
        // and shared across every one of those checks on purpose (2026-08-19, project owner's own
        // call: "нормальный скоринг" — several can independently apply to the same card and
        // simply add up, rather than each needing its own hand-tuned weight before any of this
        // has even been playtested once). Purely an INTERNAL ranking key, same "видимость с
        // памятью"-style scoping VisitHexTask.FindTarget's own Score and BuildFacilityTask.
        // RankHex already keep to themselves — TryPlayCardCandidates' own pre-pass uses this only
        // to pick which ONE Unit card to nominate this step; the AiDecision.Score that card
        // actually competes against MoveArmy/BuildFacility/etc. with never includes it at all
        // (the project owner's own explicit check: "надеюсь это внутренний скоринг ... не влияет
        // на скоринг между остальными задачами" — confirmed, and enforced by construction now,
        // not just by convention). So this constant's own absolute magnitude doesn't need to stay
        // small relative to cardRoleBacklogShareWeight the way a cross-category term would — it
        // only ever has to be able to tell two Unit cards apart from each other.
        public const float unitCompositionGapBonus = 15f;
        // AiManagementPlanner.CardCombatValue's own UnitAbilities.ApBonus term (2026-08-24,
        // project owner's own report) — without this, a card whose whole point is a flat +2 AP
        // grant (e.g. a hero with Attack 0/Defense 0 but ApBonus) reads as strictly worse than
        // any ordinary combat hero on the same raw-stat tie-break, even though it's arguably the
        // stronger pick strategically. Same "purely an INTERNAL ranking key" scoping as
        // unitCompositionGapBonus right above (CardCombatValue only ever picks ONE preferred
        // card among comparable ones in hand — see TryPlayCardCandidates' own Unit pre-pass and
        // its now-matching Hero pre-pass — the AiDecision.Score actually proposed never carries
        // this value), so its magnitude only has to out-rank a comparable ordinary card, not stay
        // small relative to any cross-category weight. Starting modest (not the skill's full
        // long-run value, which compounds every future turn) per the project owner's own call —
        // this only needs to break ties among comparable cards in hand, not overhaul the whole
        // PlayCard arbiter.
        public const float apBonusCardStrategicValue = 5f;
        // AiManagementPlanner.CardCombatValue's own UnitAbilities.RapidReaction term (2026-08-24
        // P1 fix, project owner's own report) — same "internal ranking key only" scoping as
        // apBonusCardStrategicValue right above. RapidReaction already grants two real economic
        // advantages elsewhere in this project — ArmyActions.EffectiveDeployApCost reads it as 0
        // deploy AP, and HexSelectionController.Factory.cs sets a spawned Rapid unit's own
        // ActivationApCost to 0 too (cheaper to get the whole army moving every following turn) —
        // but CardCombatValue never weighed either of those, so a Rapid card with otherwise
        // middling stats routinely lost the internal tie-break to a plain higher-stat card that
        // costs strictly more AP to actually use. Sized to match apBonusCardStrategicValue's own
        // tier — both are one flat AP-economy skill getting the same weight.
        public const float rapidReactionCardStrategicValue = 5f;
        // AiManagementPlanner.TaskNeedBonus's own Defence-vs-Raid weighting (2026-08-23, project
        // owner's own spec — generalizes the old Raid-only "closes the assembling task's own
        // CanDamageAll gap" criterion into one shared read across every still-recruiting combat
        // task, Raid and Defence today). Both start from the same flat unitCompositionGapBonus tier
        // — this multiplies ONLY Defence's own share, and ONLY once a real, currently-known threat
        // (AiDefencePlanner.CurrentActiveThreat) is actually driving that assembly, never for
        // routine headcount buildup with nothing sighted yet (see DefenceNeedBonus's own comment) —
        // "приоритет Defence должен быть выше Raid, если есть реальная угроза базе". Internal
        // ranking key only, same scope as unitCompositionGapBonus itself.
        public const float defenceNeedBonusMultiplier = 2f;
        // DefenceNeedBonus's own Turtle tier, split above defenceNeedBonusMultiplier (2026-08-23,
        // project owner's own spec — "Turtle need > Active Defence need > Raid need > generic
        // composition need", "не обязательно через огромные числа — достаточно multiplier/priority
        // внутри TaskNeedBonus"). Only ever applies to the CITADEL's own DefendCitadel task while
        // AiDefencePlanner.IsUnderSiege is true — an Active sighting at some OTHER base still uses
        // the plain defenceNeedBonusMultiplier tier, same as before; this is strictly the "the
        // citadel itself is genuinely under siege right now" case, the same gate
        // TryDefencePreemptCandidates already reserves its own 130 cross-category tier for. Internal
        // ranking key only, same scope as unitCompositionGapBonus/defenceNeedBonusMultiplier — never
        // leaks into the cross-category AiDecision.Score.
        public const float turtleNeedBonusMultiplier = 3f;
        // managementGarrisonBalanceScore removed 2026-08-20 (project owner's own call: "задача
        // бесплатная, поэтому ей не с кем конкурировать") — garrison-overflow split and lone-army
        // consolidation are no longer AiDecision.Score-bearing candidates in Decide's own per-step
        // arbitration at all; AiTurnController.RunGarrisonReorgPhase runs them unconditionally
        // instead, once per turn, as the very last thing a turn does (see that method's own
        // comment). GarrisonReorgTask.CanAffordTransferInto already gates every move on real
        // affordability, so neither ever needed a score to justify running.
        // Safety net for RunGarrisonReorgPhase only, same shape as maxStepsPerTurn's own comment —
        // each iteration performs at most one move and GarrisonReorgTask's own tiers strictly move
        // the roster toward a more balanced state (nothing in that class proposes undoing a move it
        // just made), so a real turn settles in well under this many. Not the same constant as
        // maxStepsPerTurn on purpose — that one bounds the whole turn's main arbitrated loop, this
        // one bounds only the dedicated end-of-turn reorg drain. Deliberately NOT lowered for the
        // 2026-08-23 CollapseTemporaryAssembly redesign, even though collapse now folds what used to
        // be several per-unit tier-1b steps into one atomic move (project owner's own correction
        // during planning) — that atomicity cuts BOTH ways: a full-but-not-overflowing garrison can
        // legitimately need several drain iterations (IdleBalance freeing one slot at a time via its
        // own strength-balance step, recomputed fresh each iteration) before Collapse ever finds
        // enough room to fire even once, so the new mechanism doesn't reliably shrink the iteration
        // count the way it first looked like it would. Leave this alone until a real playtest log
        // shows it's safe to shrink.
        public const int maxGarrisonReorgStepsPerTurn = 20;
        // GarrisonReorgTask.FindGarrisonArmyStrengthBalanceMove's own target split of combined
        // non-hero power between the garrison and the one field army it's currently leveling
        // against (2026-08-20, project owner's own pick out of the 80/20-50/50 range they offered
        // — "70/30 (Рекомендую)"). Tolerance keeps a share that's already close to target from
        // triggering a correction every single drain call over a fractional-percent difference —
        // same purpose as garrisonReservedSlots above, just for a ratio instead of a slot count.
        public const float garrisonPowerShareTarget = 0.7f;
        public const float garrisonPowerShareTolerance = 0.1f;
        // GarrisonReorgTask.FindHexBalanceMove's own floor (2026-08-20, project owner's own point
        // 3) — with this few non-hero units total on a hex, there's nothing worth balancing: three
        // armies of one unit each get picked off one at a time, so everything just stays put in
        // the garrison as one coherent stack instead of getting split toward a ratio that doesn't
        // matter yet.
        public const int minHexUnitsForBalancing = 3;
        // GarrisonReorgTask.FindHexBalanceMove/FindReorgSwap's own composition-evening gate
        // (2026-08-23, project owner's own call, part of the CollapseTemporaryAssembly/HandleOverflow/
        // IdleBalance split) — a raw 1-count gap (4 melee/3 ranged, say) used to trigger a move every
        // single drain call for a difference nobody would actually call imbalanced, the main source
        // of the churn the project owner flagged when asking for this. An initial tuning value, NOT
        // yet validated against a real playtest AiDebug.log — revisit once one exists.
        public const int compositionImbalanceThreshold = 2;
        // GarrisonReorgTask.CollapseTemporaryAssemblyRoutine's own per-unit trace (2026-08-23,
        // project owner's own "внутри можно оставить verbose debug-флаг" ask) — off by default so a
        // collapse logs its one summary line only; flip to true to also see each individual
        // ArmyActions.TransferMember call it made along the way. `static readonly`, not `const` —
        // a `const false` gets folded in by the compiler as a compile-time constant, and the
        // per-unit logging it guards then reads as statically unreachable (CS0162) rather than a
        // real runtime toggle.
        public static readonly bool verboseGarrisonReorgLogging = false;
        // Leftover-AP fallbacks (Reserve army / draw a card) — whichever AiManagementPlanner.
        // IsPreferred says is due next gets High, the other gets Low, so the two alternate turn by
        // turn.
        public const float managementFallbackHighScore = 15f;
        public const float managementFallbackLowScore = 5f;
        // An arrived BuildFacility task that still can't build (short on AP/still saving up) — a
        // deliberately tiny score so real work always wins, but this still beats a silent Pass.
        public const float economyWaitScore = 1f;

        // ---- Army Roles (AiArmyRoles) ----
        // AiArmyRoles.IsMakeshiftScoutCapable's own lower bound — filled to at least Hero+2 (or as
        // full as a lower-CommandRating hero's own Capacity allows).
        public const int makeshiftScoutMinMembers = 3; // hero + 2

        // Экономика · Задача 2's own base weight — see resourceScrapBaseWeightBonus's own comment.
        public static float ResourceScrapBaseWeight => economyBaseWeight + resourceScrapBaseWeightBonus;

        // Экономика · Задача 2's own detach-prerequisite score — same tier as the actual
        // ResourcesScrap walk/collect score itself now (project owner's own 2026-08-19 call: no
        // longer pinned to managementReorgScore), so a detach never loses arbitration to routine
        // garrison housekeeping. No on-active-task penalty any more (2026-08-23) — an active-task
        // source is now excluded from FindCollectorDetachPlan entirely, never just deprioritized.
        public static float ResourceScrapDetachScore => ResourceScrapBaseWeight;
    }
}
