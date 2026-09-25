# AI Strategy V2 — Architecture (ARCH-02)

This document is **normative**. The original V2 pipeline (phase ordering, decision
authority, score/resource/lifecycle semantics) is unchanged; ARCH-02 only
consolidates *ownership* — which class/file/folder owns which responsibility — so
that new objectives, missions, cards, equipment, generated-card mechanics and
skills/effects have an obvious place to live.

## Folder taxonomy (semantic ownership, not assembly boundaries)

Everything stays in namespace `Game.Ai.V2` (flat). Folders express ownership only.

| Folder | Owns |
|---|---|
| `Orchestration/` | Turn *ordering* only. No scoring, capacity, placement or eligibility logic. |
| `Foundation/` | Cross-cutting config, enums, shared low-level primitives. |
| `State/` | Turn-scoped registries, ledgers, reservations, commitments, budgets. Mutated only through each type's explicit lifecycle API. |
| `Analysis/` | Read-only world scan and derived facts. One coherent `WorldSnapshot`. |
| `Strategy/Desire/` | Axis desire intensities → radar. |
| `Strategy/Objectives/` | Concrete known opportunities per axis. |
| `Strategy/Demand/` | Missing capability detection. **Never selects cards.** |
| `Strategy/` (root) | Phase-A / Phase-B coordination, maintenance & pressure policy. |
| `Evaluation/Cards/` | The single strategic card scoring entry point (`StrategicCardEvaluator`). |
| `Evaluation/Effects/` | `StrategicEffectRegistry` — canonical skills/effects semantic bridge. |
| `Evaluation/Power/` | `AiPower` — canonical own-force power. |
| `Materialization/` | Chain enumeration, placement options, plan, cost, consumption, delivery, execution. |
| `Missions/` | Objective → `MissionProposal` planners + admission. `Missions/Raid/` for raid assembly. |
| `Allocation/` | The one `ResourceAllocator`. |
| `Provisioning/` | Funded mission → actor binding → assembly → provisioned mission. Never plays strategic cards. |
| `Reaction/` | Interrupt lifecycle coordinator + probe/witness/reservation/solver/executor. |
| `Execution/` | Plan → canonical gameplay calls → structured result. Never re-plans or re-scores. |
| `Continuity/` | Mission intent lifecycle: `ResolveActive` (before planning) and `Reconcile` (after execution). |
| `Housekeeping/` | Turn-final, task-neutral, zero-AP local force packaging and invariant repair. It may sort/fold same-hex rosters against world-snapshot combat benchmarks, but never selects objectives, creates missions, moves armies, plays cards or spends resources. |
| `Recon/` | Recon-mission machinery (route/step planning, scout pricing, air/ground policy). |
| `Diagnostics/` | Logging, telemetry, audit. No influence on ordering, score, eligibility or state. |

## Mission axes (`MissionKind`)

Four axes compete for the same turn's AP/resources, all through the same
`MissionProposal` shape and the same allocator. **There is still no Defence axis.**
Aggression produces three mission shapes — `Raid`, `ActiveDefence` and `Attack` —
from the same `Missions/AggressionMissionPlanner`,
scored by the same `TaskScore`, admitted by the same `GroundCombatAdmissionPolicy`
thresholds and funded from the same Aggression slice. ActiveDefence is a durable
threat-interception mission (`Intercept → Return`) owned by Aggression, **not** a
fifth axis and no longer the "reactive-only via `Reaction/`" stub older revisions of
this document described: `Reaction/` remains the end-of-turn interrupt safety net
only. Its win-chance gate selection (fresh start vs. pinned continuation) is the
same `GroundCombatAdmissionPolicy` pair Raid uses, applied identically in Missions,
`State/GroundCombatAdmissionRegistry` and `Provisioning`.

| Axis | Produced by | Task shape |
|---|---|---|
| `Economy` | `Missions/EconomyMissionPlanner` | `EconomyTaskKind`: BuildExtraction, FoundBase, MobileCollection, ReturnCollector, ReturnBuilder. Several build obligations may be active at once: Continuity keeps each one on its own facts (`Continuity/MissionContinuityLayer.ResolveActive`), real ownership conflicts (same actor / same objective / same physical card) are resolved where ownership is granted (`BeginEconomyDelivery`), and each obligation holds its own owner-scoped rows in `StrategicResourceReservationLedger`. Keeping an obligation and funding it are separate decisions — the allocator still owns the budget. |
| `Raid` | `Missions/AggressionMissionPlanner` | One durable multi-turn mission moving through `RaidMissionPhase`: Assault → Reinforcement/SupportReturn/Return → AirSupport / RecoveryReturn → Refit. Not five competing tasks — five phases of one committed raid. |
| `ActiveDefence` (Aggression) | `Missions/AggressionMissionPlanner` (`AppendActiveDefence`) | `ActiveDefencePhase`: Intercept → Return. May borrow an Assault-phase Raid's army (`SuspendReason.ActiveDefencePreemption` + `ActiveDefenceIntent.SuspendedOffensiveIntentKey`); Continuity resumes that Raid exactly once, and repairs it if no live defence still borrows it. |
| `Attack` (Aggression) | `Missions/AggressionMissionPlanner` (`AppendAttack`) | One known enemy Base/Citadel or enemy Facility (extractor/lab/factory; winning the site destroys it, success = the structure is gone) per durable intent, defended or not — its garrison and every army on the hex are one defender package. Any fight on a known hostile structure is Attack's (`AttackObjectiveEvaluator.IsHostileAttackStructure`): ActiveDefence defers an enemy standing on one, and an Attack side strike never detours onto one. `AttackMissionPhase`: (Gather →) Assault → Reinforcement/SupportReturn or RecoveryReturn. A fresh objective no single army nor same-hex package can take opens in **Gather**: `GroundCombatAssemblyPlanner.PlanGather` picks the host with the lowest total AP (Σ support activation × walking turns + assembled activation × assault turns) and the supports by win gain per AP; the host holds, supports walk to it in parallel and hand over through the Reinforcement convoy/handoff, and the gather builds the fist to its peak: past the gate a support joins only while it adds `attackGatherMinWinGain`, and the host marches once every planned support is spent and it clears Attack's win-chance floor (no count/distance cap; an exhausted plan is re-planned, then falls back to Reinforcement). A donor that handed over walks home on its own `GatherReturn` leg, which never touches the operation's lifecycle. A fresh objective one army can already take still weighs the peak gather against the direct assault on the same TaskScore. The host marches under `HeroRoleEvaluator.BestCommanderFor` its fight, promoted in the assault transaction; a support's hero that would lead better is handed over with its bodies (`GroundCombatReinforcement.CommandHandover`, one atomic `ArmyActions.TransferMembersAtomic(..., promoteToCommander)`). A gather may also buy the primary of an active Raid / ActiveDefence at that operation's value (`GroundCombatDonorPolicy.BorrowableDonorApPrices`); Continuity then retires the lender. Air support is a side leg (`AttackMissionPhase.AirSupport`): Continuity binds a free wing (`GroundCombatAirSupport`, the same core Raid's AirSupport phase uses) when its strike lands 1..`attackAirSupportLeadTurns` turns before the primary reaches the site and raises its fight by `attackAirSupportMinWinGain`; the wing strikes every defender (`AirStrikePolicy.Standard`) and lands. Attack stays ground-first; successful capture retires the intent and leaves the army on the captured structure. A held base whose garrison is below its floor is garrisoned from hand first (`AppendHeldBaseGarrisonDemands`, `CapabilityDeliveryShape.Garrison`), otherwise Housekeeping moves the holding army's most wounded / weakest body in. With no hostile Base/Citadel known, Attack asks Recon to observe the enemy citadel at its cheat anchor (`WorldAnalysis.TryEnemyCitadelAnchor`); it becomes a target only once observed. |
| `Scout` (Recon) | `Missions/ReconMissionPlanner` | `ScoutTargetKind`: Explore, Surveil, Refresh, AirSweep — crossed with `ScoutExecutorKind` (Ground / AirExisting / AirLaunch) at Assignment time. Aviation is support: it serves ONLY `AirSweep` (`ReconAirCapacityPolicy.IsAirServiceable`), an aviation-only observation pass toward the cheat-read enemy army concentration (else enemy citadel, `WorldAnalysis.TryAirSweepAnchor`), flown as deep as refuel endurance allows (`ReconAirSortieState.OutboundCapFor`: plane half its move then back the same turn, helicopter its whole move then back next turn). Explore / Refresh / Surveil are ground jobs; AirSweep never sizes ground Recon capacity. |
| `Development` | `Missions/DevelopmentMissionPlanner` | Place an existing hero as operator on a Research or Production facility (`ResearchProductionMode`). Laboratory/Factory are a late resource sink that strengthens units already on the map: **when** they may spend is `State/DevelopmentInvestmentGate` (each resource the concrete chain consumes has turn-start headroom ≥ threshold for N consecutive turns; unconsumed resources never matter, a cost-free spend is always open), **what** is worth doing is `DevelopmentOpportunityEvaluator.Enumerate` (READY upgrades + PREPARE facility/operator, valued against known threats — no live mission has to witness the need). Facility/operator requests run in Phase A's residual infrastructure pass, after card arbitration. |

## Unified task scoring (one struct, one fold)

`Evaluation/TaskScore.cs` is the **only** way a mission candidate's merit may be
computed. It is a plain struct of named bonus/penalty slots (CardPrice, Delivery,
OwnTerritoryProximity, MoverOpportunityCost, MilitaryTargetRelevance/RaidReward,
TerrainDefense, DetectionRisk, InfoGain, ContactRelevance, Staleness, …) folded
by `TaskScoreEvaluator`/`.Value` into one comparable number across Economy, Recon,
Development, Raid, ActiveDefence and Attack alike. A new scoring fact must be added as a named slot on
`TaskScore` and constructed *before* the fold — never applied to `.Value`/
`BaseValue` after the fact from planner- or allocator-local code. See the
canonical-seams table below.

Radar answers which **axis** matters now. `TaskScore` answers how good a concrete
world task is. `EffectiveValue` is the intrinsic `BaseValue` multiplied by that
task's axis weight. Radar never chooses Attack versus Raid: all three military
families share the Aggression axis and therefore compete by their canonical task
values in the global allocator. Lifecycle hysteresis may break an otherwise local
tie, but it is not intrinsic world-task value and may not manufacture a new score.

Attack readiness (`AttackObjectiveEvaluator.Readiness`: assembly = Fist / P_field,
deployment = P_field / (P_deck + equipment reserve), each ramped, multiplied) is an
Attack-only strategic fact. `AttackObjectiveEvaluator` converts it into the existing
`MilitaryTargetRelevance` slot before the fold (Base/Citadel only). It does not enter
the common Aggression desire, so it cannot raise Raid or ActiveDefence value. Attack's
win chance is not gated at the fresh raid gate: `GroundCombatAdmissionPolicy.AttackWinChanceFloor`
(0.2) is its one floor, fresh and continuing alike, and above it the win chance is the
`WinChance` slot of the score. Coefficients are calibration points pending gameplay logs.

The force measures all live on `SelfSnapshot`, on one scale — the AiPower strength of
one composed ground stack — and are built in one pass by
`WorldAnalysis.BuildForceMeasures`: `FieldPotential` (P_field, map only),
`BestStackPotential` (map + hand), `TotalMilitaryPotential` (P_deck, + deck), `FistPower`
(strongest existing army), `StartPotential` (P_start, `ForceBaselineRegistry`) and
`Reserve` (units / hero / equipment / aviation that hand + deck can still add). The pools
are nested, so P_field + Reserve.Units + Reserve.Hero = P_deck. Aviation is support and
never joins a ground stack.

One completed Raid target is one completed strategic objective. Continuity never
selects or mutates the intent to a second neutral target. It exposes the surviving
army to fresh mission construction/allocation; a new Raid, Attack or ActiveDefence
must win the shared competition. A zero-value Return/Recovery fallback remains
available if no fresh operation is admitted.

## Dependency direction (must hold)

```
Orchestration
   ↓
Strategy / Materialization / Missions / Allocation / Provisioning / Reaction / Continuity
   ↓
Analysis / Evaluation / Foundation / State
   ↓
Game domain
```

Forbidden edges: `Evaluator → StrategicManager`, `Domain → MissionPlanner`,
`Executor → ObjectiveEvaluator`, `Demand → MaterializationExecutor`, and any
cycle across the tiers above. `Execution/` receives concrete plans and calls
canonical game actions; it never selects objectives or invents alternative actions.

## Mid-turn loop boundary (rollout contract)

The loop is a bounded repetition of the existing horizontal pipeline, not a new
vertical manager. Ownership remains:

| Level | Mid-turn responsibility |
|---|---|
| `Orchestration/` | Chooses the next admitted task family, enforces turn/cycle/step bounds and stops. No scoring. |
| `Analysis/` | Refreshes `WorldSnapshot`, compares the previous observation with the new one and reports factual deltas. Domain event/reward code never depends on V2. |
| `Strategy/` | Maps typed factual invalidations to dirty task families and re-runs only the existing affected policy. |
| `State/` | Keeps turn-scoped reservations, commitments, state version and persistent no-op parking across cycles. |
| Existing executors | Execute one admitted atomic task step through canonical gameplay calls and return one structured result. They never re-plan. |
| `Reaction/` | End-of-turn safety net, bounded external-interrupt fallback and final reconciliation; not the ordinary post-step loop owner. |

A **task step** contains at most one canonical state-changing gameplay operation,
or one explicit no-op/blocked result. Its executor must wait until that operation
and any battle/event consequence have settled before returning. Only then may
Analysis refresh observations and produce typed invalidations. This preserves
method atomicity: the loop surrounds existing operations; it does not yield from
inside their mutation boundary.

| Lifetime | Starts | Ends | May survive |
|---|---|---|---|
| Turn | V2 turn entry | final reconciliation / end turn | reservations, commitments, mission intent, parking |
| Cycle | refreshed observation + dirty-family admission | one bounded task selection/replan pass | turn-owned state only |
| Step | admitted task result is selected | execution settles and result is observed | no executor-local planning state |

Phase A and Phase B are not deleted. Their existing policies remain the owners of
capability fulfilment and surplus/tempo arbitration. They are re-entered only
through bounded adapters and retain turn-scoped parking/reservation state; the
terminal Phase-B/reaction path remains the final safety net.

**Rollout is complete, not partial.** The bounded typed loop is the single production
execution path. There is no runtime strategy/focus mode and no axis-scope filtering.
Every turn builds the real Desire evaluators, normalizes one Radar and runs all four
axes — Recon, Economy, Aggression and Development. `AiStrategyV2Scope` remains only
as a pure `MissionKind → DesireAxis`/operational-invalidation mapping helper.

Ground Recon concurrency is value-gated `0..maxConcurrentReconExecutions`.
`DemandUrgencyPolicy.NormalizedWorldValue` is the canonical adapter from TaskScore
to “material value”; frontier-region count or stale pressure may justify a second
lane only when a second runnable observation objective passes that value gate.
There is no mandatory first lane, third production lane or turn-number decay.
Consequently Recon naturally falls to zero on a fully known/current map and can
reactivate when important contact becomes stale or blind again.

## Canonical seams (one owner each)

| Concern | Canonical owner |
|---|---|
| Cross-axis mission/task scoring | `Evaluation/TaskScore.cs` (`TaskScoreEvaluator`) — the only mission-candidate scorer; see "Unified task scoring" above |
| Own-force power | `Evaluation/Power/AiPower` — no `ReactionPower` / `RaidPower` |
| Tactical roster odds + per-defender penetration | `Game.Combat.WorthIt` — skill-aware; each side's commander (initiative bonus, Fate rerolls through `FateDuelAi`'s policy); a multi-army hex is `EstimateSequential` (strongest defender first, wounds carry, Fate refills). The aggregate-sum estimator is gone |
| Who leads an army (capacity, battle initiative, Fate) | `ArmyData.Commander` — the army's first hero; battle, UI and AI read only this |
| Which hero SHOULD lead a formation | `HeroRoleEvaluator.ProjectCommand` + `CompareCandidates` — the formation's fight under that hero (WorthIt, with its capacity/initiative/Fate) against the opposition, then capacity, then role/leadership. Same-hex assembly (`GroundCombatDonorPolicy.PickAttachableHero`), Housekeeping's commander reorder, bench pick and `CommanderMismatch` all use it; so does `CombatOpportunityAnalyzer` for the assemblable roster, over `SelfSnapshot.CommandHeroes` (map and hand heroes, a hero card through `HeroRoleEvaluator.Profile(CardDefinition)`), and Phase A/B's hero-card value (`StrategicCardEvaluator.HeroCommandMarginalValue`: would it lead the destination, and the win gain if so). With no concrete target the fight is `HeroRoleEvaluator.CommandContext` — the strongest enemy field army (`IsCommandBenchmark`) |
| Own force measures (P_field, best stack, P_deck, Fist, P_start, reinforcement reserve) | `WorldAnalysis.BuildForceMeasures` → `SelfSnapshot`, on `AiPower`'s one-stack scale. P_start is player memory in `State/ForceBaselineRegistry`, written once by the pipeline after the first scan |
| A body's quick combat value (Attack+Defense+HP+0.25·Initiative) | `WorthIt.CombatValue` |
| The opposition of a ground fight (each defending army + its observed commander) | `AiV2Util.KnownOpposition` (Raid/ActiveDefence target), `AttackObjectiveEvaluator.KnownSiteOpposition` (Attack site); flat defender lists are `WorthIt.UnitsOf` of these. `GroundCombatFeasibility.Clears` takes the attacker's commander + the opposition |
| Strategic card value | `Evaluation/Cards/StrategicCardEvaluator` — the only strategic scorer |
| Skills / effects semantics | `Evaluation/Effects/StrategicEffectRegistry` |
| Materialization delivery ("can this satisfy demand X") | `Materialization/MaterializationDeliveryPolicy` (plan- and army-level) |
| Chain enumeration (raw shapes only — no preflight, no feasibility, no score) | `Materialization/MaterializationChainEnumerator` |
| Per-chain feasibility (Preflight + Phase-A entitlement/AP/resource gate + Phase-B reserves/strategic-claim gate) | `Materialization/MaterializationFeasibility` (`FilterForDemand` / `FilterSurplus`) |
| Air-recon per-step tactical decisions (phase machine / mode / `Pick` / return-step + landing hysteresis / activation gates / opportunistic-strike arbitration) | `Recon/AirReconStepDirector` |
| Air-recon information-weighting (Explore vs Refresh) | `AirReconModePolicy` (internal class inside `Recon/AirReconStepDirector.cs`, not its own file) |
| Plan construction + `StrategicActionCost` + `StableKey` | `Materialization/MaterializationPlanFactory` |
| Capability / trait / equipment-host matching | `Materialization/MaterializationChainMatching` |
| Joint physical projection (recipient / hero / hand slots) | `Materialization/ProjectedPhysicalState` |
| Projected army capacity rule (planner == executor) | `Materialization/ArmyCapacityRules` |
| Air-recon actor/target selection (round 4 — same owner as ground) | `Recon/ReconAssignmentPlanner` (`AppendAirCandidates`) |
| Air-recon execution-input assembly (mode / launch-subset re-derivation / first-step gate / energy) | `Recon/AirReconPlanner` |
| What a live Attack needs observed, and how Recon serves it | `AttackObjectiveEvaluator.ObservationNeeds` publishes the operation's target site; `ReconObjectiveEvaluator.AttackNeedRefresh` turns it into an ordinary Refresh (stale past `attackIntelMaxAgeTurns`, full relevance). No new Recon kind |
| Air support of a ground fight (wing options, strike estimate split back per defending army, second strike, landing base, leg requirements, wing provisioning, the flight step) | `Missions/GroundCombat/GroundCombatAirSupport` + `Execution/GroundCombatLegStep.AirStrikeSortie`. Raid (its AirSupport recovery phase) and Attack (a side leg) keep only their target identity, strike policy, win read and lifecycle. The held wing is `GroundCombatLegs.HeldAirSupportArmyId`; an airborne strike sortie no operation holds becomes a landing obligation (`ReleaseOrphanStrikes` → `AviationRebasePlanner.FindMandatoryContinuations`) |
| Turns an army needs to cover a distance | `AiV2Util.TurnsToCover` |
| Typed strategic invalidations | `State/StrategicInterruptRegistry` — factual reason mask plus per-reason payload; no second event bus |
| Execution state-version counter | `State/V2StateVersion` |
| Materialization action cost | `Materialization/MaterializationPlan` accounting fields (`ApCost` / `ResCost` / `HandSlotsNeededAtPeak` / `Generation`) — the canonical `StrategicActionCost` |
| Physical card / equipment / generation consumption | `Materialization/MaterializationConsumptionState` |
| Jointly-feasible materialization portfolio | `Strategy/PhaseA/MaterializationPortfolioSolver` |
| Delivered capability + Housekeeping lease | `Strategy/PhaseA/CapabilityDeliveryEvaluator` |
| Card placement legality | `Materialization/PlacementRules` |
| Strategic spendability ("does this cost fit spendable resources") | `State/StrategicSpendability` |
| Research/Production investment window (every Challenge, facility build, operator delivery) | `State/DevelopmentInvestmentGate` — written once per turn by `DemandLayer.Generate`, read by `GenerationSource.Enumerate` and `DevelopmentOpportunityEvaluator.Enumerate` |
| Development opportunity admission (READY upgrade / PREPARE facility+operator, one investment EV) | `Strategy/Objectives/DevelopmentOpportunityEvaluator.Enumerate` — Demand and the capacity-upgrade look-ahead consume its list, never a predicate of their own |
| Actor occupancy truth | `State/ActorCommitments` |
| Explicit resource reservations (owner-aware) | `StrategicResourceReservationLedger` (`State/StrategicResourceReservation.cs`) |
| Phase-A AP pool (one scalar pool + follow-up reserve; the axis is a log label only) | `State/ApBudgetLedger` (AP-only) |
| Turn tempo budget | `State/StrategicTempoBudget` |
| Persistent-resource hold policy | `Strategy/PhaseB/HoldEvaluator` |
| Raid actor eligibility | `IsStructuralRaidActor` field on the army snapshot in `WorldSnapshot`, computed by `Analysis/WorldAnalysis.Self.cs` (no separate `RaidActorEligibility` type any more — no "Ready" alias) |
| Ground-combat assembly: same-hex package, eligible-actor enumeration, cross-hex Attack gather | `Missions/GroundCombat/GroundCombatAssemblyPlanner` (`Plan`, `EligibleActorIds`, `PlanGather`, `SupportImprovesPrimary`) |
| Ground-combat leg occupancy (which legs pin actors, join the batch solve, own their mover; held supports) | `Missions/GroundCombat/GroundCombatLegs` — read by ProvisioningManager, ProvisioningSession, ActorCommitments, AdvanceIntent and the admission fingerprints |
| Non-capturing transit step of every lifecycle leg (Raid/Attack/ActiveDefence returns, Raid/Attack convoys and gathers) | `Execution/GroundCombatLegStep.Transit`; each lane executor maps the step onto its own result semantics |
| Ground-combat operation core (primary / support army) | `IGroundCombatOperation` on Raid/Attack/ActiveDefence intents; `MissionIntent.PreferredMoverArmyId` projects onto it |
| Ground-combat win-chance gates (start vs continue) | `GroundCombatAdmissionPolicy` (internal class inside `Missions/GroundCombat/GroundCombatAssemblyPlanner.cs`, not `Missions/Raid/`) |
| Ground-combat (ActiveDefence) win-chance gates | `GroundCombatAdmissionPolicy` — one owner of `FreshStartWinChanceGate` / `ContinuationWinChanceFloor` / `AttackWinChanceFloor` (Attack's one floor) and of the assigned-assault selection `AssaultGate`. Missions, `GroundCombatAdmissionRegistry` and `ProvisioningManager` only *select* between them, all on the same predicate (the proposal continues a durable intent whose pinned actor is the actor being bound). A re-check must never apply a stricter gate than the admission it is re-checking: a `GroundCombatAssemblyPlan` carries the `WinChanceGate` it was admitted at, and `GroundCombatAssaultTransactionRunner.Run` re-checks at exactly that gate. |
| Which win-chance gate a stage re-plans at | `GroundCombatAdmissionPolicy.PinnedOrFreshGate` (the durable operation's own pinned actor vs anything new), `RaidPrimaryGate` (a started Raid stays in Assault down to the continuation floor; Continuity's phase machine and Demand read the same gate), `AssaultGate` (Provisioning). No stage writes its own ternary |
| Ground-combat response score (win chance, activation AP now, recurring AP, mover opportunity cost) | `TaskScoreEvaluator.WithResponse` / `WithActorResponse` — Raid, Attack and ActiveDefence fold the same slots onto their intrinsic objective score |
| "Does a bound primary need a NEW support army" (Raid and Attack) | `AggressionDemandEvaluator.BoundPrimaryShortage` |
| Transactional assault assembly (Raid, Attack, ActiveDefence intercept) | `Provisioning/GroundCombatAssaultTransaction.cs` — `GroundCombatAssaultTransactionRunner.Run` |
| Per-lane continuity | `MissionContinuityLayer.ResolveActive` dispatches to `ResolveRaidIntent` (`Continuity/MissionContinuityLayer.Raid.cs`), `ResolveAttackIntent` (`.Attack.cs`) and `ResolveActiveDefenceIntent` (`.ActiveDefence.cs`); `TryResumePreemptedOffensive` is the one resume edge of a borrowed offensive |
| Walk-home destination of every lifecycle leg | `MissionContinuityLayer.KeepOrReselectHome` (keep a still-owned, reachable base; otherwise `SelectReturnBase`) |
| Support-local failure of a leg (never ends the operation) | `GroundCombatLegs.IsSupportLeg` / `RaidLegOf` / `AttackLegOf` — the ledger's execution and provisioning classifiers and `ReconcileOutcome` (`ReleaseInvalidSupport`) |
| Strategic knowledge of an enemy army | `Analysis/AiMapMemory` sightings. Objectives, Missions, Provisioning and Execution read enemy existence/position only from there. A global `ArmyRegistry` sweep may confirm the outcome of a canonical operation the AI itself just performed (e.g. did the target survive the battle our army fought) — it may never stand in for knowledge of a hidden army, and "absent from the world" is never objective completion. |
| Reaction feasibility evidence | `ReactionWitness` (struct in `Reaction/StrategicReactionPass.cs`) + `Reaction/ReactionOpportunityProbe` |
| Reaction witness arbitration (§28) | `Reaction/ReactionWitnessSelector` |

## Verified boundary invariants (02F–02H audit)

* **One `ResourceAllocator`** — no per-mission/per-axis allocator.
* **One `StrategicCardEvaluator`** — no `Hero`/`Reaction`/`PhaseB`/`Aviation` card scorer.
* **Executors do not plan or rescore** — `Execution/TaskExecutor`, `ReconGroundExecutor` and
  `ReactionRoundExecutor` call canonical gameplay actions and return a structured result; the
  only evaluator calls are `Is*SatisfiedLive` completion checks (a legit §37 concern), never
  objective selection or replacement-mission synthesis (the stale-Explore replacement builder
  was removed — a stale-goal Scout is recorded and re-targeted by Continuity next pass).
* **Air recon Assignment/Execution split (round 4).** WHICH air actor (an existing ready standalone
  wing) or WHICH airfield+launch-subset serves a funded Observation (Refresh / non-stealth Surveil)
  mission is decided by `Recon/ReconAssignmentPlanner.AppendAirCandidates` — the SAME single
  Assignment owner, and the same batch one-actor-per-job solver, Ground candidates already go
  through (`BuildCandidates` / `AssignFunded`; `ScoutExecutorKind.AirExisting` / `AirLaunch` on
  `ScoutExecutionCandidate`). Feasibility reuses `ReconAirReservationPrepass.SlotWouldFly` — the
  same primitive the pre-Demand capacity sizing prepass uses — so the two can never diverge into two
  different feasibility answers for the same question. `Provisioning/ProvisioningManager.ProvisionAir`
  claims the concrete actor/subset the same way ground Provisioning claims a ground mover, producing
  a `ProvisionedMission` tagged with `ExecutorKind`/`AirfieldHex`/`LaunchSubset`. Air never satisfies
  Explore/GroundTraversal and never a stealth-Required / positive-DetectionRisk mission — both hard
  invariants are enforced in `AppendAirCandidates` before any candidate is built.
  The shared allocator deliberately does not apply Recon's ground `HardCap`, because executor kind
  is unknown there. `ReconAssignmentPlanner` applies that cap only to ground-bound candidates and
  preserves the independent `MaxAirReconActorsPerTurn` ceiling for aviation, including across
  provisioning re-packs. Thus air observation may run in addition to the allowed ground lanes.
  Air recon stays plan-then-execute for the *tactical* half. `Recon/AirReconPlanner.Plan` no longer
  selects; it turns this pass's air-bound `ProvisionedMission`s (plus wings already continuing a
  prior sortie, which are Mission Continuity's concern, not fresh Assignment's) into an `AirReconPlan`
  — re-deriving the concrete first step/landing/score fresh via `PickFromStorage` against current
  world state (legitimate live execution-input assembly, mirroring how `ReconGroundExecutor`
  re-derives its own next step every turn too). Every *per-step* tactical decision — the
  Outbound/Turning/Hold/Return phase machine, live `ReconMode` resolution, the
  `ReconAirStepPlanner.Pick` call, the return-step + landing hysteresis, the activation energy /
  affordability gates and the opportunistic-strike arbitration — still lives in
  `Recon/AirReconStepDirector.PlanStep`, which replans live on every call; the tactical step-scoring
  algorithm itself (`AirReconRouteScorer` / `AirReconAnchorModel`) is unchanged this round — it does
  not take a bound target hex (see the round-4 report for why that was left as a documented, narrow,
  deliberate scope boundary rather than a rewrite). `Execution/ReconAirExecutor` only issues the
  canonical Move / Strike / assignment-bookkeeping call each returned `StepDecision` names and bumps
  `V2StateVersion` on each confirmed mutation; it produces an `AirReconExecutionResult`
  (`IV2ActionResult`). The orchestrator splits `TaskExecutor`'s ground/raid input from air-executed
  Scout `ProvisionedMission`s before calling `TaskExecutor.Execute`, then runs air plan+execute as a
  terminal stage; `TaskExecutor` itself still never references air recon. A launch that goes
  unaffordable mid-pass is skipped and logged, never re-planned.
* **Provisioning plays no strategic cards** — `Provisioning/*` binds actors and locks; it never
  calls `MaterializationExecutor` / `StrategicPhaseA/B`.
* **The strategic layer is skill-agnostic** — Strategy / Materialization / Missions / Reaction
  branch only on the AI's own `CapabilityKind` taxonomy, never on a gameplay ability name
  (`Splash` / `Regen` / `Summon` / `AbilityKind.*`). Ability specifics stay behind
  `StrategicEffectRegistry.Roles(...)`, so a new effect needs a registry entry, not a manager
  `if`.
* **Execution results are a structured family** — `MaterializationResult` / `CardPlayResult` /
  `BuildingPlayResult` / `InfraFulfillResult` / `ExecutionResult` / `AirReconExecutionResult`
  implement `IV2ActionResult` and project to the common `V2ActionOutcome` (`Succeeded` /
  `StateChanged` / `ApSpent` / `ResourcesSpent` / `Played` / `Generated` / `Attached` / `Moved` /
  `Created` / `NeedsReplan` / `StateVersionAfter` / `FailReason`). Each keeps its domain payload; a
  caller that only needs the lifecycle facts reads `.Outcome`. `MaterializationExecutor` measures
  the real H/E/M/T delta for `ResourcesSpent`; `BuildingPlayExecutor.BuildExtractionFacility`
  reports the facility definition's `resourceCost` and `InfraFulfillResult.Outcome.Played` is the
  real hand-card consumption (true for the DEV Facility card, false for the hero-built extraction
  site). (`ProvisioningResult` stays on its own `Success`/`Failure` shape — provisioning is §34,
  not §35 execution.)
* **One execution state-version counter** — `State/V2StateVersion`. It is bumped by every V2
  execution-tier operation that mutates authoritative world state: `CardPlayExecutor` /
  `BuildingPlayExecutor` / `MaterializationExecutor` / `TaskExecutor`, the air-recon executor
  (per confirmed move / launch / strike) and `StrategicPhaseB` tempo (Draw / capacity-upgrade /
  Pressure, which their own executors do not version). Phase B's parked-candidate lifecycle keys
  on `V2StateVersion.Current` directly — there is no second local counter.
