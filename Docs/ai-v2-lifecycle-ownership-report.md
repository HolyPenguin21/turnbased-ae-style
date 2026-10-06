# AI V2 lifecycle ownership migration — execution report

Status: **incomplete full task; verified WRAP/CENTRALIZE migration checkpoint**. No merge to master.

Baseline: connector-verified master `706b1bbddee9abe4c4f052caea353bc3a790a332`.
Branch: `refactor/ai-v2-lifecycle-ownership`.
Unity: `6000.5.4f1` (`d550df8bd089`).

## A. Root cause / before

Confirmed mixed lifetimes in MissionIntentState, role-specific validation inside the normalized actor view, domain interpretation inside outcome ledger, and scattered turn boundaries. Importantly, result completion often describes a sub-leg rather than a terminal durable operation. A generic release-on-Completed migration cannot be applied to the existing status without changing gameplay.

The audit is in [ai-v2-lifecycle-ownership-audit.md](ai-v2-lifecycle-ownership-audit.md). It includes owner scope, mutation/reset ownership, baseline external readers/writers, cache contracts and the additional capability lease registry outside State/.

## B. Current ownership map

| Concept | Current authoritative owner | Migration limit |
|---|---|---|
| turn lifetime | AiTurnSession | storage adapters and independently correct pass/persistent owners retained |
| persistent mission intents | MissionIntentState dictionary | compatibility facade also exposes specialised persistent owners |
| Economy suppression/progress evidence | EconomyLifecycleState | original counters/expiry unchanged |
| generated Development operator identity/age | DevelopmentLifecycleState | original uniqueness/reconciliation/expiry unchanged |
| Recon trim/used actors | one ReconTurnState scope, attached to session | compatibility lookup owns no second copy |
| actor claims / mutation contracts | ActorCommitments | normalized derived view; no unified operation lease yet |
| actor role validity | MissionActorPolicy | same methods; old public accessors are delegates |
| resource ownership | StrategicResourceReservationLedger | existing string owner / bank authority retained; typed lease migration pending |
| world revision and fact publication | WorldDeltaLifecycle | V2StateVersion and interrupt Mark are storage-free compatibility adapters |
| common step disposition | MissionStepResult.Disposition | old Outcome/StructuralFailure are projections |
| domain durable lifecycle transitions | existing MissionContinuityLayer domain paths | not replaced by result policy; operation-level terminal unification pending |

## C. Changed files

| File/group | Why / before → after |
|---|---|
| State/AiTurnSession.cs; Orchestration/AiStrategyV2Pipeline.cs | scattered start/end calls → one player/turn boundary; normal end explicit, disposal fallback, next-turn recovery of abandoned scope |
| Continuity/MissionIntentState.cs; State/EconomyLifecycleState.cs; State/DevelopmentLifecycleState.cs; State/ReconTurnState.cs | mixed storage → three scoped owners plus durable intent dictionary; all production persistent-domain callers now use the specialised owner |
| State/ActorCommitments.cs; Continuity/MissionActorPolicy.cs | normalized claims plus mission validation → generic view and one domain policy; authoritative eligibility method bodies identical |
| Continuity/MissionOutcomeLedger.cs; MissionStepResult.cs; MissionStepResultPolicy.cs | ledger stores facts and interprets domains → records/correlates facts, delegates normalization/live objective interpretation; one stored disposition with compatibility projections |
| State/WorldDelta.cs; V2StateVersion.cs; StrategicInterruptRegistry.cs | independent publication API + revision API → one policy owner, old wrappers contain no revision storage; existing mutation/observation boundaries preserved |
| Execution/*, MaterializationExecutor, ProvisioningResult, WorldAnalysis.Observation, strategic card/development/phase paths | mechanical redirect of revision/publication calls; no new dirty masks or costs |
| CapabilityPoolExhaustionRegistry, AviationObligationStallRegistry, StrategicTempoBudget, StrategicSpendability | scoped EndTurn adapters used by session after final strategic reads |
| ARCHITECTURE.md | documents actual migrated contracts and explicitly lists remaining work |
| five new Editor test files (+ Unity metadata) | session/persistent/result/delta/domain fact parity tests; no product verification stubs |

Existing Unity GUIDs, scenes and prefabs are unchanged. New C# files include fresh .meta files.

## D. Removed coupling / duplication

* Generic intent storage no longer contains Recon clocks/sets or Economy/Development storage dictionaries.
* Two independent Recon trim/used clocks become one immutable turn scope.
* Generic actor view no longer contains the mission branches or copied eligibility conditions; compatibility calls use one policy.
* Ledger no longer implements Classify, ClassifyProvisionFailure or live objective interpretation.
* Two writable result status representations (Outcome + StructuralFailure) become projections of one disposition.
* Pipeline normal reservation expiry/assert and start registry coordination route through session.
* V2StateVersion no longer contains a parallel revision store; all production bump sites route to WorldDeltaLifecycle.

No claim is made that the full actor/resource cleanup duplication is removed. MissionLease is not implemented at this checkpoint.

## E. Behaviour parity

No score, threshold, target, priority, cost, movement, combat/capture, endurance, capacity, production choice or Housekeeping policy was edited. Nine mechanically moved authoritative method bodies compare byte-for-byte equal to baseline. A golden result matrix of **5,442** provisioning/execution combinations across Recon, Economy, Raid, Attack, ActiveDefence and Development exactly matches the original master fingerprint:

`88C62EC22801E3E8127D1F2E15205FB3978A32FBA292484249A1E3EABAAECE8F`

This proves result-fact parity for that matrix and differential unit coverage, not full native gameplay parity. Representative Unity E2E scenarios were **not run**.

## F. Separate bank audit

Physical stock reads, TurnResourceBook.Free/MayDrawOn, ledger storage/replacement/downgrade/expiry, allocator accounting, SpendAuthority and canonical spending remain unchanged. Session delegates the existing end expiry/assert and does not mint, debit or redistribute resources. Old-turn AP is not available through a new session. New tests cover player isolation, end cleanup and aborted prior-scope cleanup.

Existing tests cover owner-scoped Economy completion/deferred downgrade/upgrade and same-turn spendability. The deferred H/E/M/T saving claim may exceed physical stock by existing design; completion/reaction committed claims retain existing coverage checks. No stricter bank policy was introduced.

Production/Development paths still use their original bank APIs. Multiple simultaneous native Economy build scenarios remain unverified in Unity.

## G. Separate cache audit

Mutation endpoints advance the same process-monotonic stamp through one policy; observation publication does not add a second advance. Dirty masks, reason-scoped payloads, freshness equality checks, snapshot refresh placement, AiMapMemory knowledge revision and route/combat keys are unchanged. No new cache was added. WorldDelta freezes its actor/contact/hex payload.

The new atomic session.Apply API is tested, but existing producers still preserve separate commit and observation endpoints. Combined WorldDelta transaction migration and complete write→refresh→next-read gameplay verification remain pending. Cache fixture results are below; native combat cache tests did not pass in either managed baseline or refactor.

## H. Tests and environment limits

The prescribed setup.sh could not install tools in this container. Used the already available Roslyn/.NET SDK and real Unity reference DLLs for a differential compile, plus the existing reflection NUnit harness. These are scratch-only verification tools; no stub/adapter entered product code.

Raw compile baseline has three old reference/stub errors: two FindObjectsInactive overloads and one Mathf.SmoothDamp. Diagnostic copies adapt only those UI/audio calls to compile runnable managed tests; baseline and refactor use identical adapters. This is not a Unity build.

Baseline: **1,509 cases; 984 passed / 525 failed**.
Refactor: **1,550 cases; 1,025 passed / 525 failed**.
**41 new cases pass; zero previously passing cases regress or disappear.** The baseline failures include unsupported native Unity Object equality and asset loading. They were not fixed or hidden.

The harness covers Test/TestCase fixtures in Game.EditorTests; it does not execute UnityTest coroutines or TestCaseSource cases. Full ai-verify under Mono and the full Unity Editor suite were not run. No failure was called a passing Unity test.

| Required fixture | Managed passed | Existing baseline failed |
|---|---:|---:|
| AiReservationInvariantsTests | 7 | 0 |
| AiEconomyReservationLifecycleTests | 2 | 4 |
| AiEconomyContinuityAuditTests | 26 | 1 |
| AiEconomyOwnershipTests | 13 | 22 |
| AiTurnResourceBookTests | 25 | 0 |
| AiStrategicSpendabilityApTests | 10 | 4 |
| AiRaidActorCommitmentTests | 7 | 0 |
| AiRaidIntentStateTests | 7 | 8 |
| AiAttackLaneTests | 23 | 33 |
| AiAggressionOwnershipRegressionTests | 12 | 10 |
| AiHousekeepingMissionContractTests | 14 | 0 |
| AiReconTrimEligibilityTests | 8 | 3 |
| AiReconContinuityPayloadTests | 3 | 1 |
| AiReconAirLifecycleTests | 10 | 4 |
| AiAviationSortieCycleTests | 49 | 24 |
| AiDevelopmentDecisionPathTests | 15 | 19 |
| AiDevelopmentReadmissionTests | 6 | 6 |
| AiCombatCacheLifecycleTests | 0 | 6 |
| AiRouteCacheIsolationTests | 4 | 0 |
| AiTaskScoreAllocatorRegressionTests | 3 | 0 |
| AiV2ArchitecturalRegressionTests | 11 | 0 |

## I. Remaining work / adapters

The overall task is **not done**. Remaining requirements:

1. MissionLease integrating actor/resource ownership and all operation termination paths, with operation-level disposition from the existing domain transitions. Add AiMissionLeaseLifecycleTests; do not treat legacy sub-leg Completed as parent-operation termination.
2. Typed resource owner linked to existing MissionIntentKey; retain reaction pass ownership and exact spend-authority, replacement and downgrade semantics. No new independent identity is justified by this audit.
3. Typed individual domain payloads and domain lifecycle result handlers; generic coordinator must not gain the current policy's domain branches.
4. Consolidate commit/observation into actual combined WorldDelta transactions after transaction-specific parity. Do not blindly attach an extra revision bump to observation publication.
5. Remove old production adapters only after parity. Current adapters: MissionIntentState methods (fixtures and Recon production callers), ActorCommitments.FromIntents/static validity methods, MissionTurnOutcome legacy projections, V2StateVersion readers/tests, interrupt discovery/capability wrappers.
6. Full Unity 6000.5.4f1 EditMode suite and specified Recon/Economy base/Attack/Raid/Defence/Development E2E scenarios. Native null/combat/asset failures cannot be evaluated by this managed harness.
7. Complete bottom-up actor/resource lease cleanup, domain terminal intent transitions and bank/cache gameplay acceptance before declaring the architectural goal achieved.

No dual-written storage was introduced. Persistent state, bank ledger and revision have no mirrored copy. However, the common operation-owned lease/lifecycle contract requested in the full task is still absent, so adding a mission can still require operation-specific resource cleanup. This checkpoint deliberately does not claim the critical completion criterion.
