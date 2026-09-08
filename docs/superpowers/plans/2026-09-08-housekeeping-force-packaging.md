# Housekeeping Force Packaging Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace superficial army comparison with one skill-aware combat estimate and use it for coherent, zero-cost end-of-turn force packaging.

**Architecture:** Live combat modifiers remain in `ChallengeResult`; `WorthIt` projects them into deterministic roster simulation; `BattleInitiator` and Housekeeping consume that one result. Existing `WorldSnapshot`, Housekeeping analyzer/planner/executor, capability lease and `ArmyActions` are extended in place.

**Tech Stack:** Unity C#, existing single Assembly-CSharp assembly, GitHub connector workflow.

**Spec:** `docs/superpowers/specs/2026-09-08-housekeeping-force-packaging-design.md`

## Global Constraints

- Do not create another manager, combat scalar, task planner or persistent unit role.
- Housekeeping remains same-hex, zero-AP and zero-resource.
- Mission commitments and capability leases remain immutable to Housekeeping.
- No automated test files: `CLAUDE.md` records the project owner's explicit choice.
- Verify C# with `dotnet build Assembly-CSharp.csproj`; Play Mode behavior remains user verification.
- One logical change per commit with the required co-author trailer.

---

### Task 1: Canonical skill-aware combat profiles

**Files:**
- Modify: `Assets/Scripts/Combat/AbilityMagnitudes.cs`
- Modify: `Assets/Scripts/Combat/ChallengeResult.cs`
- Modify: `Assets/Scripts/Combat/WorthIt.cs`
- Modify: every existing `DefenderProfile` construction site found by repository-wide search

**Interfaces:**
- Produces: `WorthIt.DefenderProfile(..., IReadOnlyList<string> abilities)`
- Produces: profile-to-profile `WorthIt.CanDamage` / `CanDamageAll`
- Consumes: canonical `ChallengeResult.ApplyAbilityModifiers`

- [ ] Extend `AbilityMagnitudes` with Berserk gain/loss while preserving the live popup values.
- [ ] Add an ability-list overload to `ChallengeResult.ApplyAbilityModifiers`; make the `UnitData` overload delegate to it.
- [ ] Extend `DefenderProfile`, its stable seed, and every constructor call to preserve effective combat abilities and type tags.
- [ ] Make `WorthIt` full-roster simulation apply CriticalDamage, Hyperkinetic, Pyrokinetic, CeramicArmor, ShockAttack and Berserk.
- [ ] Add profile-to-profile penetration coverage and route existing projected-roster callers to it.
- [ ] Repository-wide search confirms no profile construction drops effective abilities.
- [ ] Commit the canonical combat-estimate change.

### Task 2: Contact defender selection

**Files:**
- Modify: `Assets/Scripts/Combat/BattleInitiator.cs`

**Interfaces:**
- Consumes: `WorthIt.Estimate(ArmyData, IReadOnlyCollection<DefenderProfile>, float)`
- Produces: one deterministic composition-aware contact ranking

- [ ] Replace `AttackSum + DefenseSum` ranking with attacker outcome: minimum attacker win chance, then minimum surviving HP ratio, maximum critical-after-win probability, stable army id.
- [ ] Preserve observer-aware stealth by building the estimate only from `StealthSystem.TargetableMembersFor`.
- [ ] Search confirms the legacy contact sum is gone.
- [ ] Commit the contact-selection change.

### Task 3: Distance-aware Housekeeping benchmark

**Files:**
- Modify: `Assets/Scripts/Ai/V2/Housekeeping/ArmyReorganizationModel.cs`
- Modify: `Assets/Scripts/Ai/V2/Housekeeping/ArmyReorgAnalyzer.cs`
- Modify: `Assets/Scripts/Ai/V2/Housekeeping/ArmyReorganizationPlanner.cs`
- Modify: `Assets/Scripts/Ai/V2/Housekeeping/ArmyReorganizationEvaluation.cs`
- Modify: `Assets/Scripts/Ai/V2/Housekeeping/HousekeepingManager.cs`

**Interfaces:**
- Consumes: `WorldSnapshot.TrueWorld.EnemyArmies`, `WorthIt.WinChance`, `WorthIt.CanDamageAll`
- Produces: immutable enemy benchmark profiles and ETA on each `LocalForceGroup`

- [ ] Add a `WorthIt.DefenderProfile` to each projected non-hero `ReorgUnit`.
- [ ] Pass the already-owned `WorldSnapshot` into `ArmyReorgAnalyzer.Analyze`.
- [ ] Filter deployed combat-capable enemy field compositions; keep hidden compositions but no mission-facing position.
- [ ] Compute ETA to each local group from enemy distance/current maximum movement.
- [ ] Replace `FormationStrengths` comparison with ordered exposed-defender danger profiles; use continuous distance weighting and no gate.
- [ ] Fall back to `AiPower` only when no enemy benchmark exists.
- [ ] Preserve the current hard invariant ordering and deterministic tie-breaks.
- [ ] Commit the Housekeeping packaging change.

### Task 4: Context-protected support operators and Scout lease

**Files:**
- Modify: `Assets/Scripts/Ai/V2/Strategy/Objectives/HeroRoleEvaluator.cs`
- Modify: `Assets/Scripts/Ai/V2/Housekeeping/ArmyReorgAnalyzer.cs`
- Modify: `Assets/Scripts/Ai/V2/Housekeeping/ArmyReorganizationCandidates.cs`
- Modify: `Assets/Scripts/Ai/V2/Strategy/PhaseA/CapabilityDeliveryEvaluator.cs`
- Modify: `Assets/Scripts/Ai/V2/Strategy/PhaseB/TempoActionExecutor.cs`

**Interfaces:**
- Consumes: `ResearchProductionSystem.FacilityAbility`, `RoleAbility`, existing delivery finalisation
- Produces: contextual per-unit operator protection and same-turn surplus-Scout lease

- [ ] Include mobility in intrinsic hero combat leadership.
- [ ] Project the minimum compatible facility operators as unit-level committed/protected facts.
- [ ] Deposit an active operator into local garrison when legal and reject its field donation.
- [ ] Extend existing capability finalisation to recognise successful final Scout capability without residual demand.
- [ ] Lease only the actual resulting army ids and retain current turn-local clearing.
- [ ] Commit operator and Scout lifecycle changes.

### Task 5: Strict atomic whole-fold

**Files:**
- Modify: `Assets/Scripts/Map/ArmyActions.cs`
- Modify: `Assets/Scripts/Ai/V2/Housekeeping/ArmyReorganizationModel.cs`
- Modify: `Assets/Scripts/Ai/V2/Housekeeping/HousekeepingExecutor.cs`

**Interfaces:**
- Produces: `ArmyActions.TransferMembersAtomic(...)`
- Consumes: planner operation identity identifying one whole-fold batch

- [ ] Mark whole-fold transfers with one batch identity in the existing plan model.
- [ ] Add complete projected-state preflight to `ArmyActions`.
- [ ] Commit no member until every member passes ownership, membership, capacity, same-hex and AP validation.
- [ ] Execute a whole-fold through one batch call; keep single transfer and swap paths unchanged.
- [ ] Commit the atomic mutation change.

### Task 6: Decision-oriented diagnostics and architecture text

**Files:**
- Modify: `Assets/Scripts/Ai/V2/Housekeeping/HousekeepingManager.cs`
- Modify: `Assets/Scripts/Ai/V2/Housekeeping/HousekeepingExecutor.cs`
- Modify: `Assets/Scripts/Ai/V2/Diagnostics/AiFrameLog.cs` only if the existing frame owner must suppress a duplicate
- Modify: `Assets/Scripts/Ai/V2/ARCHITECTURE.md`

**Interfaces:**
- Produces: one before/benchmark/decision/execution/after Housekeeping summary per touched hex

- [ ] Aggregate normal per-unit moves into one execution summary; retain detailed lines behind the existing verbose option.
- [ ] Log benchmark army visibility, ETA, win chance and selected defender.
- [ ] Log protected Scout/operator reasons and final unresolved defects from projected final membership.
- [ ] Update Housekeeping ownership wording from narrow invariant-only text to task-neutral local force packaging.
- [ ] Commit diagnostics and documentation.

### Task 7: Verification

**Files:**
- No production changes unless verification finds a defect.

- [ ] Search all C# for remaining `AttackSum + DefenseSum` army-ranking logic and duplicated profile coverage.
- [ ] Run `dotnet build Assembly-CSharp.csproj` and record exit code and errors.
- [ ] Review the final commit diff against every spec section.
- [ ] Report the Play Mode scenarios that still require the project owner's run.
