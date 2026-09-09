# Economy Commitment and Builder Recovery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Protect a selected Economy build without premature resource freezing and return exposed builder heroes to an owned Base/Citadel before releasing their prior mission.

**Architecture:** Extend the existing unresolved-demand reservation, Economy mission target and Continuity lifecycle. Phase B remains the only late-card arbiter, the strategic ledger remains the only numeric reservation owner, and TaskExecutor remains the only Economy movement executor. Housekeeping remains same-hex only.

**Tech Stack:** Unity C#, NUnit EditMode tests, existing AI Strategy V2 pipeline.

**Spec:** `docs/superpowers/specs/2026-09-09-economy-commitment-builder-recovery-design.md`

## Global Constraints

- Modify existing production classes only; add no new manager, ledger, pathfinder, execution loop or Housekeeping movement branch.
- Exact Base-card claims are instance-based and must not reserve H/E/M/T.
- Numeric Economy reservations remain turn-scoped and are written only by `InfrastructureFulfillment`.
- Recovery movement uses `SafeStepPathing` through `TaskExecutor.RunEconomyStep`.
- Low-risk Recon resumes immediately; high-risk Recon and non-Recon donors resume only after protected arrival.
- Extend `Assets/Editor/AiEconomyDecisionTests.cs`; add no test framework or new test class.
- Run `dotnet build Assembly-CSharp.csproj` when the Unity-generated project exists; otherwise report the missing build environment without claiming compilation.

---

### Task 1: Exact Economy Base-card option claim

**Files:**
- Modify: `Assets/Editor/AiEconomyDecisionTests.cs`
- Modify: `Assets/Scripts/Ai/V2/Strategy/Demand/DemandLayer.cs`
- Modify: `Assets/Scripts/Ai/V2/Strategy/StrategicPhaseA.cs`
- Modify: `Assets/Scripts/Ai/V2/Materialization/MaterializationCandidateBuilder.cs`
- Modify: `Assets/Scripts/Ai/V2/Strategy/PhaseB/TempoCandidateProvider.cs`

**Interfaces:**
- Produces: `MaterializationReservation.ClaimsEconomyBuildCard(CardData card) : bool`
- Consumes later: Phase-B structural candidate filtering.

- [ ] **Step 1: Write failing option-claim tests**

Add tests that create two distinct `CardData` instances from one Base definition and assert:

```csharp
var reservation = new MaterializationReservation();
reservation.UnresolvedDemands.Add(new AxisDemand { EconomyBuildCard = selected });
Assert.That(reservation.ClaimsEconomyBuildCard(selected), Is.True);
Assert.That(reservation.ClaimsEconomyBuildCard(secondCopy), Is.False);
```

Add an Economy Hero-prerequisite test asserting the emitted Hero demand retains
`EconomyBuildCard` but leaves `EconomyBuildResourceCost == null`.

- [ ] **Step 2: Run targeted EditMode tests and verify RED**

Run the Unity EditMode test filter for `Game.EditorTests.AiEconomyDecisionTests`.
Expected failure: missing `ClaimsEconomyBuildCard` and missing card payload on the Hero prerequisite.

- [ ] **Step 3: Implement the minimal claim data flow**

In `EconomyHeroPrerequisite`, copy only `EconomyBuildCard`. In
`StrategicPhaseA.CloneResidualDemand`, copy `EconomyBuildCard` for all Economy residuals.
Do not copy resource cost into a Hero prerequisite.

Add:

```csharp
public bool ClaimsEconomyBuildCard(CardData card) =>
    card != null && UnresolvedDemands.Any(d =>
        d?.EconomyBuildCard != null && ReferenceEquals(d.EconomyBuildCard, card));
```

In `TempoCandidateProvider`, use one helper that checks both possible materialization hand cards
and the non-combat card, and omit a candidate if any consumed instance is claimed.

- [ ] **Step 4: Run targeted tests and verify GREEN**

Run the same filter. Expected: the exact instance is claimed, the duplicate remains unclaimed, and
the Hero prerequisite still carries no H/E/M/T cost.

- [ ] **Step 5: Commit**

Commit message: `Protect selected Economy Base card from Phase B`.

---

### Task 2: One-turn numeric commitment horizon

**Files:**
- Modify: `Assets/Editor/AiEconomyDecisionTests.cs`
- Modify: `Assets/Scripts/Ai/V2/Strategy/Demand/InfrastructureFulfillment.cs`

**Interfaces:**
- Produces: `InfrastructureFulfillment.IsEconomyBuildCommitmentNear(AxisDemand demand, WorldSnapshot snap) : bool`
- Preserves: `ReserveEconomyCost` as the only numeric reservation writer.

- [ ] **Step 1: Write failing threshold tests**

Construct demands with builder routes and matching `ArmySnapshot.MaxMovement`:

```csharp
Assert.That(InfrastructureFulfillment.IsEconomyBuildCommitmentNear(
    DemandWithRoute(travel: 6, onTarget: false), SnapshotWithBuilder(maxMovement: 3)), Is.False);
Assert.That(InfrastructureFulfillment.IsEconomyBuildCommitmentNear(
    DemandWithRoute(travel: 3, onTarget: false), SnapshotWithBuilder(maxMovement: 3)), Is.True);
Assert.That(InfrastructureFulfillment.IsEconomyBuildCommitmentNear(
    DemandWithRoute(travel: 99, onTarget: true), SnapshotWithBuilder(maxMovement: 3)), Is.True);
```

- [ ] **Step 2: Run targeted tests and verify RED**

Expected failure: threshold method is absent and current deferred reservation is unconditional.

- [ ] **Step 3: Implement threshold and pass snapshot to reservation**

Change `ReserveDeferredEconomyResources` to accept `WorldSnapshot snap`. Resolve each route's
matching own `ArmySnapshot`; return true when `IsOnTarget` or
`TravelCost <= Mathf.Max(1, army.MaxMovement)`. Reserve the complete existing resource vector only
when true. Log a distant option claim without creating numeric ledger rows.

Update the sole `StrategicPhaseA` call site.

- [ ] **Step 4: Run targeted tests and verify GREEN**

Expected: far route is unreserved; on-target and one-turn routes reserve through the unchanged
`ReserveEconomyCost`.

- [ ] **Step 5: Commit**

Commit message: `Delay Economy build resources until one-turn horizon`.

---

### Task 3: Recovery data, destination and mission emission

**Files:**
- Modify: `Assets/Editor/AiEconomyDecisionTests.cs`
- Modify: `Assets/Scripts/Ai/V2/Orchestration/AiStrategyV2Pipeline.cs`
- Modify: `Assets/Scripts/Ai/V2/Continuity/MissionIntent.cs`
- Modify: `Assets/Scripts/Ai/V2/Missions/EconomyMissionPlanner.cs`

**Interfaces:**
- Produces: `EconomyTaskKind.ReturnBuilder`
- Produces: nullable `EconomyMissionTarget.ReturnHex`
- Produces: `EconomyIntent.ReturnHex`
- Produces: `EconomyMissionPlanner.SelectProtectedReturnHex(WorldSnapshot snap, HexCoord from) : HexCoord?`

- [ ] **Step 1: Write failing destination and proposal tests**

Cover:

```csharp
Assert.That(EconomyMissionPlanner.SelectProtectedReturnHex(snapshot, facilityHex),
    Is.EqualTo(nearestOwnedBase));
Assert.That(EconomyMissionPlanner.SelectProtectedReturnHex(snapshot, ownedBaseHex),
    Is.EqualTo(ownedBaseHex));
```

Create an active `ReturnBuilder` intent with a preferred mover and no Economy demand. Assert
`EconomyMissionPlanner.Propose` emits exactly one proposal with zero build card/resource payload and
the same mover id.

- [ ] **Step 2: Run targeted tests and verify RED**

Expected: recovery enum/data and proposal path do not exist.

- [ ] **Step 3: Add recovery payload and protected destination selection**

Extend the existing Economy enum/structs. Protected candidates come only from own known buildings
where `IsBase || IsStartingCitadel`. Use deterministic distance/threat/Citadel/coordinate ordering;
never treat a facility-only building as protected.

At the start of `EconomyMissionPlanner.Propose`, emit active recovery intents independently of
current Economy demands. Requirements contain only actor activation/movement needs, never build
H/E/M/T or follow-up build AP.

- [ ] **Step 4: Run targeted tests and verify GREEN**

Expected: destination selection and demand-independent recovery proposal pass.

- [ ] **Step 5: Commit**

Commit message: `Add durable Economy builder recovery mission`.

---

### Task 4: Recovery provisioning and execution

**Files:**
- Modify: `Assets/Editor/AiEconomyDecisionTests.cs`
- Modify: `Assets/Scripts/Ai/V2/Provisioning/ProvisioningManager.cs`
- Modify: `Assets/Scripts/Ai/V2/Execution/TaskExecutor.cs`
- Modify: `Assets/Scripts/Ai/V2/Continuity/MissionIntent.cs`

**Interfaces:**
- Consumes: `EconomyTaskKind.ReturnBuilder`, preferred mover and return target.
- Produces: preferred-actor-only recovery provisioning and reached-goal execution.

- [ ] **Step 1: Write failing policy tests**

Add pure/internal policy assertions that recovery:

- rejects a substitute hero when the preferred mover is absent;
- carries no Economy loan request;
- is satisfied only when the preferred mover reaches the protected target;
- reports `ReachedGoal` at the return hex rather than `StepCompleted`.

- [ ] **Step 2: Run targeted tests and verify RED**

Expected: ReturnBuilder follows build-delivery behavior or accepts ordinary Economy actor selection.

- [ ] **Step 3: Implement preferred-only provisioning**

Branch inside existing `ProvisionEconomy`: for ReturnBuilder resolve
`MissionProposal.PreferredMoverArmyId`, validate the original owned hero army, and return
`NoMoverExists` when absent. Do not search for another hero and do not borrow another intent.

- [ ] **Step 4: Implement return execution and satisfaction**

In `RunEconomyStep`, when ReturnBuilder is already at target, set `ReachedGoal = true` and
`StopReason = ReachedGoal`. Otherwise reuse the existing safe-step block unchanged.

Extend `EconomyObjectiveSatisfied` and `ResolveActive` so recovery completion is actor-position
based and a blocked/no-path return is retained rather than mistaken for completed construction.

- [ ] **Step 5: Run targeted tests and verify GREEN**

Expected: original actor only, safe-step movement, correct completion classification.

- [ ] **Step 6: Commit**

Commit message: `Return Economy builder through existing executor`.

---

### Task 5: Unified build-to-recovery Continuity transition

**Files:**
- Modify: `Assets/Editor/AiEconomyDecisionTests.cs`
- Modify: `Assets/Scripts/Ai/V2/Strategy/Demand/DemandLayer.cs`
- Modify: `Assets/Scripts/Ai/V2/Strategy/Demand/InfrastructureFulfillment.cs`
- Modify: `Assets/Scripts/Ai/V2/Strategy/StrategicPhaseA.cs`
- Modify: `Assets/Scripts/Ai/V2/Continuity/MissionIntent.cs`

**Interfaces:**
- Produces: one idempotent `MissionContinuityLayer.RegisterEconomyBuildCompletion(...)` transition.
- Consumes: deterministic on-target builder id, refreshed snapshot and original loan identity.

- [ ] **Step 1: Write failing transition tests**

Test the transition matrix:

- non-Recon/unassigned builder at Facility -> ReturnBuilder;
- newly founded Base/current protected hex -> no recovery;
- low-threat Recon donor -> immediate repayment, no recovery;
- high-threat Recon donor -> donor stays suspended and ReturnBuilder is created;
- Raid/non-Recon donor -> stays suspended until recovery;
- repeated registration -> one recovery intent;
- arrival -> donor activated exactly once and recovery retired.

- [ ] **Step 2: Run targeted tests and verify RED**

Expected: current completion path always repays/removes and direct Phase-A build creates no intent.

- [ ] **Step 3: Consolidate threat and builder attribution**

Extend the existing `DemandLayer.EconomyBuilderUnderImmediateThreat` rather than creating a second
risk scorer. Keep urgent asset threat and add the already-used honest known-enemy proximity signal.

Make the existing Economy builder candidate owner expose deterministic on-target selection. Pass
active intents/commitments into the sole infrastructure fulfillment call; record the chosen builder id
on `InfraFulfillResult`.

- [ ] **Step 4: Implement one idempotent Continuity transition**

Both normal Economy outcome reconciliation and direct Phase-A completion call the same transition.
It selects/retains ReturnHex, preserves LoanSource, delays repayment according to the matrix, and
rekeys the existing intent or creates one direct-build recovery intent. Repeated calls detect the
existing actor/return intent and do nothing.

Call the direct-build path only after `WorldAnalysis.RefreshOperationalState`, so Base/Citadel and
threat facts are current.

- [ ] **Step 5: Run targeted tests and verify GREEN**

Expected: every transition matrix case passes and no duplicate intent/loan repayment occurs.

- [ ] **Step 6: Commit**

Commit message: `Recover exposed heroes after Economy construction`.

---

### Task 6: Full verification and integration

**Files:**
- Review all modified files from Tasks 1–5.
- No additional behavior changes.

- [ ] **Step 1: Inspect final diff against the spec**

Confirm no Housekeeping production file, new production class, second reservation ledger, second
pathfinder or second threat formula was introduced.

- [ ] **Step 2: Run compile verification**

Run: `dotnet build Assembly-CSharp.csproj`  
Expected: exit code 0, zero compile errors.

- [ ] **Step 3: Run Economy EditMode suite**

Run the project Unity EditMode command filtering
`Game.EditorTests.AiEconomyDecisionTests`.  
Expected: all targeted tests pass with zero failures.

- [ ] **Step 4: Verify repository state**

Confirm `master` parent SHA, inspect every commit diff, and verify the remote branch points to the
final fast-forward commit.

- [ ] **Step 5: Push**

Push granular commits to `master` only after available verification succeeds. If the connector
environment has no project checkout/Unity runner, preserve the granular commits but explicitly report
that compile/runtime verification remains pending rather than claiming success.
