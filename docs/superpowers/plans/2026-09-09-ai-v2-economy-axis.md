# AI Strategy V2 Economy Axis Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement Economy as a complete, bounded AI Strategy V2 axis alongside Recon and Development.

**Architecture:** Extend the existing analysis, desire, demand, mission, allocation, continuity, provisioning, execution and orchestration owners. Route construction through existing infrastructure fulfillment and authoritative game actions; add only the horizontal `EconomyMissionPlanner` production class.

**Tech Stack:** Unity 6.0.5, C#, NUnit Unity Editor tests, Git/GitHub.

**Spec:** `docs/superpowers/specs/2026-09-09-ai-v2-economy-axis-design.md`

## Global Constraints

- Base from exact `master` SHA `298e3336949a5a92fbeca2d7ae98a4fcc9087fb9` on `feature/ai-v2-economy-axis`.
- `AiStrategyV2Pipeline.RunTurn` remains the sole global order owner and bounded mid-turn loop.
- `InfrastructureActions` remains the sole owner of game construction rules.
- `StrategicResourceReservationLedger` remains the sole strategic reservation ledger.
- `StrategicReactionPass` remains a final safety net, not Economy's ordinary loop.
- Housekeeping receives no Economy-specific logic; it relies on `ActorCommitments`.
- The only new production class/file is `Missions/EconomyMissionPlanner.cs` plus its Unity `.meta`.
- Every behavior change follows RED → GREEN → REFACTOR and each stage ends compiling.

---

### Task 1: Economic snapshot and deficit model

**Files:** Modify `WorldSnapshot.cs`, `WorldAnalysis.cs`, `AiConfigV2.cs`; test `Assets/Editor/AiEconomyDecisionTests.cs`.

**Interfaces:** Extend `EconomyResourceStanding` with `OpponentMedianIncome`, hand/deck/reserved need, spendable stock, targets/gaps/runway/pressures and `DeficitScore`; retain `EconomyStanding` as the only economic snapshot.

- [ ] Add failing tests for hand-vs-deck weighting, opponent median gap, operational reservation pressure, runway security and starvation pressure.
- [ ] Run the Editor test selection and confirm failures are caused by missing Economy fields/calculation.
- [ ] Add config horizons, discounts and the five specified deficit weights; split hand and remaining-deck aggregation and read owner-aware reservations/starvation from existing registries.
- [ ] Re-run the focused tests, inspect snapshot logs, then commit `AI V2: add economy deficit snapshot`.

### Task 2: Economy desire and breakdown

**Files:** Modify `DesireEvaluators.cs`, `AiFrameLog.cs`; test `AiEconomyDecisionTests.cs`.

**Interfaces:** Add Economy factor fields to `DesireBreakdown` and an `EconomyDesire(WorldSnapshot, DesireBreakdown)` evaluator using `0.65 * max + 0.35 * mean`, actionable latent damp and the existing smoothing/`Radar.Normalize` path.

- [ ] Add failing tests for positive deficit desire, secure-economy low desire and latent non-zero desire without an actionable opportunity.
- [ ] Run and verify RED.
- [ ] Implement the frozen-snapshot evaluator and compact breakdown reporting without a second normalization path.
- [ ] Run focused tests and commit `AI V2: evaluate economy desire`.

### Task 3: Strategic extraction and Base demands

**Files:** Modify `AxisDemand.cs`, `DemandLayer.cs`, `AiConfigV2.cs`; test `AiEconomyDecisionTests.cs`.

**Interfaces:** Add `CapabilityKind.EconomicExpansionBase`; enrich `AxisDemand` with stable Economy objective identity/build cost/site-value facts needed downstream. `EconomyDemands` emits a configured bounded set of scored candidates.

- [ ] Add failing tests for strategic sort order, deficit priority, threat/travel trade-offs, illegal/built rejection, Base cluster/capacity/logistics value and strict Base-vs-extraction completion identity.
- [ ] Run and verify RED.
- [ ] Enumerate legal extraction/Base candidates, calculate the approved score components, then sort by value, income gain, travel cost, Q and R.
- [ ] Run focused tests and commit `AI V2: score economy infrastructure demands`.

### Task 4: Economy mission identity and continuity

**Files:** Modify `AiStrategyV2Pipeline.cs`, `MissionIntent.cs`, `MissionRevalidator.cs`, `ActorCommitments.cs`; test `AiEconomyDecisionTests.cs`.

**Interfaces:** Add `MissionKind.Economy`, `EconomyTaskKind`, `EconomyIntent`, `SuspendReason.EconomyLoan`, Economy stable-key conversion, donor linkage/terminal state, Economy claims and deterministic orphan repair.

- [ ] Add failing tests for distinct stable keys, donor retention, current-hex restoration and all orphan/terminal cleanup cases.
- [ ] Run and verify RED.
- [ ] Extend the existing intent registry/revalidation lifecycle without creating another state owner; prevent loaned donor ageing/stall/reap.
- [ ] Run focused tests and commit `AI V2: add economy mission continuity`.

### Task 5: Economy mission planning

**Files:** Create `Missions/EconomyMissionPlanner.cs` and `.meta`; modify `StrategicPhaseA.cs`; test `AiEconomyDecisionTests.cs`.

**Interfaces:** `EconomyMissionPlanner` consumes already-ranked Economy demands and current snapshot/intents and returns proposals only for valid hero-delivery blockers; it never recomputes deficits/sites or mutates game state.

- [ ] Add failing tests for direct-build suppression, stale/illegal/unsatisfied/no-card/no-path rejection and justified proposal creation.
- [ ] Run and verify RED.
- [ ] Implement the horizontal planner and call it from the existing Phase-A result flow.
- [ ] Run focused tests and commit `AI V2: plan economy delivery missions`.

### Task 6: Admission and shared AP allocation

**Files:** Modify `MissionAdmissionPolicy.cs`, `ResourceAllocator.cs`, `CapabilityPoolExhaustionRegistry.cs`; test `AiEconomyDecisionTests.cs`.

**Interfaces:** Map Economy to its lane, rank by effective value/readiness/completion/loan cost/hysteresis, extend exhaustive allocator branches, and add requirement-scoped Economy hero-builder exhaustion.

- [ ] Add failing tests for Economy ranking, shared-AP contention, deterministic cooldown and independent exhaustion pools.
- [ ] Run and verify RED.
- [ ] Extend the existing admission/repack/exhaustion owners; do not select an actor in allocation.
- [ ] Run focused tests and commit `AI V2: admit and allocate economy missions`.

### Task 7: Economy actor selection and loan lifecycle

**Files:** Modify `ProvisioningManager.cs`, `MissionIntent.cs`, `MissionRevalidator.cs`, `ActorCommitments.cs`, `AiConfigV2.cs`; test `AiEconomyDecisionTests.cs`.

**Interfaces:** Economy provisioning orders free hero armies before eligible Soft Recon and not-started Soft Raid donors; loan net value includes donor loss, detour, delay, exposure and switching cost and requires same-turn move+build for borrowed actors.

- [ ] Add failing tests for all candidate priorities and exclusions, Full-mode Raid compatibility, same-turn guarantee, success/failure restoration and no blind return movement.
- [ ] Run and verify RED.
- [ ] Implement Economy dispatch in the shared provisioning session and donor suspend/restore transitions in continuity.
- [ ] Run focused tests and commit `AI V2: provision economy actors and loans`.

### Task 8: Build-follow-up reservations and direct fulfillment

**Files:** Modify `StrategicResourceReservation.cs`, `StrategicSpendability.cs`, `InfrastructureFulfillment.cs`, `StrategicPhaseA.cs`; inspect but do not change `BuildingPlayExecutor.cs` and `InfrastructureActions.cs`; test `AiEconomyDecisionTests.cs`.

**Interfaces:** Add `EconomyBuildFollowup`, `ReleaseByOwner(string owner)`, own-owner exclusion and Economy build reservation/upsert/release. Fulfillment handles extraction/Base and reports actual `V2ActionOutcome` before AP/resource accounting and typed invalidation.

- [ ] Add failing tests for Phase-B isolation, own/foreign owner visibility, turn/terminal/orphan release and direct extraction/Base fulfillment.
- [ ] Run and verify RED.
- [ ] Extend the existing ledger/spendability/fulfillment paths and reserve only same-turn reachable follow-up costs.
- [ ] Run focused tests and commit `AI V2: reserve and fulfill economy builds`.

### Task 9: Bounded Economy task execution

**Files:** Modify `TaskExecutor.cs`; test `AiEconomyDecisionTests.cs`.

**Interfaces:** `ExecuteStep` dispatches Economy, validates the intent and performs at most one movement step; it never builds and returns truthful progress/stop/state-version data.

- [ ] Add a failing test proving one call cannot move more than one step or construct infrastructure.
- [ ] Run and verify RED.
- [ ] Add Economy dispatch using existing movement/path/risk primitives and publish settled local continuation.
- [ ] Run focused tests and commit `AI V2: execute bounded economy movement`.

### Task 10: Typed Economy re-entry

**Files:** Modify `AiStrategyV2Pipeline.cs`, `StrategicPhaseA.cs`; test `AiEconomyDecisionTests.cs`.

**Interfaces:** Replace Development-only re-entry with one `ReenterCapabilityAxes` orchestration method accepting the typed family mask; consume Economy-relevant invalidations and local Economy continuation only.

- [ ] Add failing tests for post-step refresh, local bounded re-entry, no normal reaction pass, one final Phase B/Housekeeping and no-progress termination.
- [ ] Run and verify RED.
- [ ] Generalize existing re-entry without duplicating Phase A or adding a second loop.
- [ ] Run focused tests and commit `AI V2: add typed economy reentry`.

### Task 11: Scope activation and diagnostics

**Files:** Modify `AiStrategyV2Scope.cs`, `AiFrameLog.cs`, `AiV2Trace.cs`, `V2TurnActivityTelemetry.cs`; test `AiEconomyDecisionTests.cs`.

**Interfaces:** Add `ReconEconomyDevelopment`, allow Recon/Economy/Development axes and Scout/Economy missions, and emit compact analysis/demand/mission/step/build/release causal logs with detailed candidates only in verbose trace.

- [ ] Add failing tests for scope filtering, exhaustive enum dispatch and per-cycle diagnostic deduplication.
- [ ] Run and verify RED.
- [ ] Activate the completed slice and extend existing diagnostic owners.
- [ ] Run focused tests and commit `AI V2: activate economy scope and diagnostics`.

### Task 12: Regression and architecture audit

**Files:** Complete `AiEconomyDecisionTests.cs` and `.meta`; inspect all changed production files plus `NonCombatCardPlayer.cs`, `StrategicReactionPass.cs`, `HousekeepingManager.cs`, `InfrastructureActions.cs`.

**Interfaces:** The final Editor suite contains the approved 41 behavioral/architecture cases and no test-only behavior in production.

- [ ] Add any uncovered acceptance regression as a failing test and verify RED.
- [ ] Make the minimum owning-layer correction and verify GREEN.
- [ ] Search all switches over `MissionKind`, `CapabilityKind`, `SuspendReason` and scope mode; make every branch explicit and deterministic.
- [ ] Run static duplicate-owner checks, inspect `git diff --check`, changed-file list and production-new-file count.
- [ ] Run all available compile/tests, commit `AI V2: complete economy regression coverage`.

### Task 13: Publish for review

**Files:** No source changes.

- [ ] Rebase/update against the current remote `master` only if it moved, resolving within the approved scope.
- [ ] Push `feature/ai-v2-economy-axis`.
- [ ] Open a PR to `master` with base/final SHA, ownership map, tests, limitations and explicit architecture non-changes.
- [ ] Do not merge; wait for a separate direct owner instruction.
