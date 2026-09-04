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
| `Housekeeping/` | End-of-turn invariant repair only. Not a strategic planner. |
| `Recon/` | Recon-mission machinery (route/step planning, scout pricing, air/ground policy). |
| `Diagnostics/` | Logging, telemetry, audit. No influence on ordering, score, eligibility or state. |

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

## Canonical seams (one owner each)

| Concern | Canonical owner |
|---|---|
| Own-force power | `Evaluation/Power/AiPower` — no `ReactionPower` / `RaidPower` |
| Strategic card value | `Evaluation/Cards/StrategicCardEvaluator` — the only strategic scorer |
| Skills / effects semantics | `Evaluation/Effects/StrategicEffectRegistry` |
| Materialization delivery ("can this satisfy demand X") | `Materialization/MaterializationDeliveryPolicy` (plan- and army-level) |
| Chain enumeration (raw shapes only — no preflight, no feasibility, no score) | `Materialization/MaterializationChainEnumerator` |
| Per-chain feasibility (Preflight + Phase-A entitlement/AP/resource gate + Phase-B reserves/strategic-claim gate) | `Materialization/MaterializationFeasibility` (`FilterForDemand` / `FilterSurplus`) |
| Air-recon per-step tactical decisions (phase machine / mode / `Pick` / return-step + landing hysteresis / activation gates / opportunistic-strike arbitration) | `Recon/AirReconStepDirector` |
| Air-recon information-weighting (Explore vs Refresh) | `Recon/AirReconModePolicy` |
| Plan construction + `StrategicActionCost` + `StableKey` | `Materialization/MaterializationPlanFactory` |
| Capability / trait / equipment-host matching | `Materialization/MaterializationChainMatching` |
| Joint physical projection (recipient / hero / hand slots) | `Materialization/ProjectedPhysicalState` |
| Projected army capacity rule (planner == executor) | `Materialization/ArmyCapacityRules` |
| Air-recon planning (actor / mode / launch / first-step / energy) | `Recon/AirReconPlanner` |
| Execution state-version counter | `State/V2StateVersion` |
| Materialization action cost | `Materialization/MaterializationPlan` accounting fields (`ApCost` / `ResCost` / `HandSlotsNeededAtPeak` / `Generation`) — the canonical `StrategicActionCost` |
| Physical card / equipment / generation consumption | `Materialization/MaterializationConsumptionState` |
| Jointly-feasible materialization portfolio | `Strategy/PhaseA/MaterializationPortfolioSolver` |
| Delivered capability + Housekeeping lease | `Strategy/PhaseA/CapabilityDeliveryEvaluator` |
| Card placement legality | `Materialization/PlacementRules` |
| Strategic spendability ("does this cost fit spendable resources") | `State/StrategicSpendability` |
| Actor occupancy truth | `State/ActorCommitments` |
| Explicit resource reservations (owner-aware) | `State/StrategicResourceReservationLedger` |
| AP entitlement split | `State/AxisBudgetLedger` (AP-only) |
| Turn tempo budget | `State/StrategicTempoBudget` |
| Persistent-resource hold policy | `Strategy/PhaseB/HoldEvaluator` |
| Raid actor eligibility | `Missions/Raid/RaidActorEligibility.IsStructuralRaidActor` (no "Ready" alias) |
| Raid win-chance gates (start vs continue) | `Missions/Raid/RaidAdmissionPolicy` |
| Reaction feasibility evidence | `Reaction/ReactionWitness` + `ReactionOpportunityProbe` |
| Reaction witness arbitration (§28) | `Reaction/ReactionWitnessSelector` |

## Verified boundary invariants (02F–02H audit)

* **One `ResourceAllocator`** — no per-mission/per-axis allocator.
* **One `StrategicCardEvaluator`** — no `Hero`/`Reaction`/`PhaseB`/`Aviation` card scorer.
* **Executors do not plan or rescore** — `Execution/TaskExecutor`, `ReconGroundExecutor` and
  `ReactionRoundExecutor` call canonical gameplay actions and return a structured result; the
  only evaluator calls are `Is*SatisfiedLive` completion checks (a legit §37 concern), never
  objective selection or replacement-mission synthesis (the stale-Explore replacement builder
  was removed — a stale-goal Scout is recorded and re-targeted by Continuity next pass).
* **Air recon is plan-then-execute — including the per-step loop.** `Recon/AirReconPlanner.Plan`
  does the pass-level admission (aircraft discovery, actor selection/ordering, `ReconMode`,
  launch-subset, the `PickFromStorage` minimum-useful-step gate, the energy-policy check),
  producing an `AirReconPlan`. Every *per-step* tactical decision — the Outbound/Turning/Hold/
  Return phase machine, live `ReconMode` resolution, the `ReconAirStepPlanner.Pick` call, the
  return-step + landing hysteresis, the activation energy / affordability gates and the
  opportunistic-strike arbitration — lives in `Recon/AirReconStepDirector.PlanStep`, which
  replans live on every call. `Execution/ReconAirExecutor` only issues the canonical Move /
  Strike / assignment-bookkeeping call each returned `StepDecision` names and bumps
  `V2StateVersion` on each confirmed mutation; it produces an `AirReconExecutionResult`
  (`IV2ActionResult`). The orchestrator runs plan+execute as a terminal stage after
  `TaskExecutor.Execute`; `TaskExecutor` no longer references air recon. A launch that goes
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
