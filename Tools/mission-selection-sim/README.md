# mission-selection-sim

Standalone acceptance harness for **Strategy V2 build-order step 7.1** — Mission Candidate Beam /
Execute Capacity (`Assets/Scripts/Ai/V2/MissionAdmissionPolicy.cs`, plus the N>K changes in
`ReconMissionPlanner` / `ResourceAllocator`).

Same pattern as `Tools/commitment-sim` and `Tools/radar-sim`: a console `Exe` that
`ProjectReference`s the Unity-generated `Assembly-CSharp.csproj`, so it always runs the *current*
game code. Only the pure model layer is exercised — no `MonoBehaviour`, scene, or coroutine.
Execution / provisioning against live `ArmyData` cannot run headless, so `RunLoop` scripts
provisioning outcomes and feeds them back through `AllocationSession.RegisterProvision{Success,
Failure}`, driving the same bounded pack → provision → re-pack loop `Pipeline.RunTurn` runs.

This is an **allocator / portfolio** harness (separate from `commitment-sim`, which is the
continuity state machine): it pins how N candidate proposals are turned into ≤ K funded
executions, and how a re-pack falls through to a backup the same turn.

## Run

```
cd Tools/mission-selection-sim
dotnet run -c Debug
```

Exit code `0` = all scenarios passed.

## What it checks

| # | scenario | asserts |
|---|----------|---------|
| 00 | config invariant | `scoutCandidateBeamWidth >= maxConcurrentReconExecutions`; K unchanged at 2 |
| 01 | N > K | `MissionLayer` emits a beam (> K, ≤ N); allocator funds ≤ K Recon; provisioning sees ≤ K; the surplus is `ExecutionCapacity`, never a failure |
| 02 | backup after provisioning failure | top pick A fails provisioning → the same turn a re-pack promotes backup C (deferred `ExecutionCapacity` on pass 1) into a funded slot |
| 03 | conflict backup | B conflicts A → deferred `MissionConflict` (not `ExecutionCapacity`); once A is rejected this turn the conflict clears and B funds on a later pass |
| 04 | execution capacity | exactly K funded; the rest `ExecutionCapacity` — not provisioning failures, not budget |
| 05 | commitment consumes a slot | 1 Soft commitment + 3 fresh, K=2 → commitment funded first, only 1 fresh funded, the other two `ExecutionCapacity` |
| 06 | two commitments | Soft A + Soft B fill K; fresh C → `ExecutionCapacity` (commitments get no magic extra slot) |
| 07 | locked claim survives re-pack | A succeeds and locks (1/K), B fails → the re-pack tops up to K but never past it; D held out by `ExecutionCapacity` |
| 08 | budget fallback | the top-ranked pick is unaffordable → deferred `InsufficientBudget`; the allocator falls through to B + C rather than leaving a K slot empty |
| 09 | incumbent duplicate | fresh generation re-proposes an active incumbent's objective → downstream gets ONE proposal, the incumbent version (`FromDurableIntent` + `PreferredMoverArmyId` carried) |
| 10 | deterministic | reversing the candidate input order yields the identical funded portfolio |
| 11 | no cooldown | candidates deferred by `MissionConflict` / `ExecutionCapacity` get no allocator cooldown — they fund freely the next turn |
| 12 | existing-behaviour baseline | N == K, feasible, no conflicts, ample AP → both funded, nothing deferred, one pass (pre-step-7.1 behaviour) |
