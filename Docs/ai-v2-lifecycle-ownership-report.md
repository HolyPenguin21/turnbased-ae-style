# AI V2 lifecycle ownership migration — execution report

Status: **incomplete full task; verified session/lease, typed-payload and domain transition checkpoint**. No merge to master.

Initial baseline: connector-verified master `706b1bbddee9abe4c4f052caea353bc3a790a332`.
Continuation baseline: connector-verified master `13210b16fc1ed6a42facc17ec3cc55d06aa83afb`. Feature-only merge `a69c7392` preserves upstream army UI/scene/badge changes; master was not modified. The new master has no overlapping AI changes. Its separate managed baseline remains 1,509 cases, 984 passed / 525 failed; the original baseline is retained unchanged.
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
| actor claims / mutation contracts | session MissionLeaseBook actor table | ActorCommitments is a view; detached pass views remain derived snapshots |
| actor role validity | MissionActorPolicy | same methods; old public accessors are delegates |
| explicit resource ownership/lifecycle | MissionLeaseBook API over StrategicResourceReservationLedger storage | typed ReservationOwner carries existing MissionIntentKey; token bank compatibility retained |
| world revision and fact publication | WorldDeltaLifecycle | V2StateVersion and interrupt Mark are storage-free compatibility adapters |
| common step disposition | result boundary + registered MissionContinuityLayer domain classifiers | one MissionStepResult.Disposition; old Outcome/StructuralFailure are projections |
| typed domain facts | MissionStepResult payload store | one typed value per payload type; legacy field API owns no copies |
| domain durable lifecycle transitions | existing MissionContinuityLayer domain paths | same transitions in domain partial policies; common coordinator uses disposition and ordered handlers; common AdvanceIntent accounting delegates domain role/fact/capability policy; ResolveActive and transaction migration still pending |

## C. Changed files

| File/group | Why / before → after |
|---|---|
| State/AiTurnSession.cs; Orchestration/AiStrategyV2Pipeline.cs | scattered start/end calls → one player/turn boundary; normal end explicit, disposal fallback, next-turn recovery of abandoned scope |
| Continuity/MissionIntentState.cs; State/EconomyLifecycleState.cs; State/DevelopmentLifecycleState.cs; State/ReconTurnState.cs | mixed storage → three scoped owners plus durable intent dictionary; all production persistent-domain callers now use the specialised owner |
| State/ActorCommitments.cs; State/MissionLease.cs; Continuity/MissionActorPolicy.cs | separate claimed set/contracts → one actor claim table with operation owners; same role predicates feed it; normalized membership remains O(1) |
| Continuity/MissionOutcomeLedger.cs; MissionTurnOutcome.cs; MissionStepResult.cs; MissionStepResultPolicy.cs | ledger correlates facts; legacy outcome fields now project typed payloads; generic MissionStepResult<TPayload> permits a domain fact schema without editing generic result fields; shared fact record makes the legacy adapter a noncopying view |
| Continuity/*StepPayload.cs; MissionContinuityLayer.DomainResults.cs and domain partials | domain fields, classification branches and live objective readers move to typed facts and existing domain continuity policies; code bindings dispatch the same rules, not another Continuity |
| State/EconomyLifecycleState.cs; MissionRevalidator; Economy provisioning | live Economy objective predicate moves unchanged to the specialised domain owner; production no longer asks the telemetry ledger to decide it |
| State/WorldDelta.cs; V2StateVersion.cs; StrategicInterruptRegistry.cs; Execution/BuildingPlayExecutor.cs | one revision owner; synchronous FoundBase scope stages child card-play stamps, drops rollback and advances once on commit; Raid/Attack paired commit/publication use one Apply with original dirty mask |
| Execution/*, MaterializationExecutor, ProvisioningResult, WorldAnalysis.Observation, strategic card/development/phase paths | mechanical redirect of revision/publication calls; no new dirty masks or costs |
| CapabilityPoolExhaustionRegistry, AviationObligationStallRegistry, StrategicTempoBudget, StrategicSpendability | scoped EndTurn adapters used by session after final strategic reads |
| AiTurnSession.Settle; Pipeline; Continuity public entry points | operational result ingress accepts MissionStepResult; existing domain handlers use a noncopying compatibility view, preserving leg/campaign distinctions |
| Continuity domain partials; GroundCombatTransitions; DomainResults | ReconcileOutcome mission branches → ordered domain callbacks; same side-leg/completion/recovery/payload precedence; 14 moved helpers are byte-identical |
| AiDomainTransitionParityTests | two frozen 5,760-transition fingerprints, completed-leg/terminal-operation independent lease cleanup, pinned mover and frozen support-role tests |
| AdvanceIntent / domain fact and mover callbacks | inline role interpretation and domain fact mutations → ordered domain callbacks; accounting, existing suspension/stall/reap order unchanged |
| ARCHITECTURE.md | documents actual migrated contracts and explicitly lists remaining work |
| nine new Editor test files (+ Unity metadata) | session/persistent/result/delta/domain fact parity plus lease lifetime, bank stages, stale-turn write and pre-intent failure tests; no product verification stubs |

Existing Unity GUIDs, scenes and prefabs are unchanged. New C# files include fresh .meta files.

## D. Removed coupling / duplication

* Generic intent storage no longer contains Recon clocks/sets or Economy/Development storage dictionaries.
* Two independent Recon trim/used clocks become one immutable turn scope.
* Generic actor view no longer contains the mission branches or copied eligibility conditions; compatibility calls use one policy.
* Ledger no longer implements Classify, ClassifyProvisionFailure or live objective interpretation.
* Two writable result status representations (Outcome + StructuralFailure) become projections of one disposition.
* Pipeline normal reservation expiry/assert and start registry coordination route through session.
* V2StateVersion no longer contains a parallel revision store; all production bump sites route to WorldDeltaLifecycle.

* ActorCommitments no longer owns independent claimed-id, contract and preparation-host stores. One lease table owns those facts and identifies the operation.
* All production explicit-reservation writes, replacements, release and expiry requests use MissionLeaseBook. The ledger retains its existing bank storage/replacement rules as an adapter, with no second reservation copy.
* Removing a durable intent releases its operation actor claims and resource rows through the lease owner. Existing in-place Recon rekeys transfer actor ownership; sub-leg Completed does not trigger retirement.
* Terminal fresh failures now release the operation's pre-intent resource hold in the same turn; another operation's hold remains. Previously RetireEconomyIntent returned on intent=null.
* FoundBase no longer advances revision for a rolled-back refusal or once per nested card play plus outer commit. Child stamps belong to the canonical synchronous transaction.

* Domain facts now reside in Recon/Raid/Attack/Defence/Economy/Development typed payloads plus shared ground-combat facts. Legacy outcome properties are projections; common result schema need not add fields for a new payload.
* Result classification branches now execute in existing domain Continuity partials. They are invoked once by the normalization boundary; later intent reconciliation reads that result and domain facts rather than re-running classifiers.
* The normalization coordinator no longer implements six live objective branches. Existing domain policies provide registered readers with the original predicates and fog-of-war restrictions.
* TaskExecutor no longer stamps a child-completed mutation a second time. A nonnegative existing StateVersionAfter is the child receipt; Raid/Attack terminal handoff now carries that receipt. Ground routines that may still move after a child stamp deliberately retain their old boundaries pending a full action transaction audit.
* MissionOutcomeLedger no longer declares any domain payload fields or provides the Economy objective predicate. It contains only fact correlation/delegation; the legacy facade moved to its own file.

Common operational ingestion is now migrated; operation-level terminal interpretation and all revision transaction boundaries remain incomplete. Pipeline uses FinalizeSteps → session.Settle(MissionStepResult). Internal legacy domain handlers temporarily consume a view sharing the exact common fact record and typed payload dictionary. There is no conversion copy or second disposition. New tests cover all six dispositions through a generic typed result, stale-session refusal and six-domain ingress parity.

The ReconcileOutcome coordinator no longer embeds Raid campaign completion, Attack intermediate/side-leg/completion, Recon external waypoint continuation, Economy progress/recovery/no-progress or ordered payload creation branches. Domain partial callbacks retain the original order. Economy retirement/loan repayment and generated intent constructors moved to their corresponding domains; generic retirement delegates domain preparation, then uses the same intent removal/lease owner. Fourteen moved helper methods match the pre-extraction source byte-for-byte.

AdvanceIntent no longer directly copies Scout/Attack/Raid/Economy payloads or interprets support/air actors as a primary. Ordered mover callbacks call the same ground-leg owner, preserve durable Economy/Development pins and reconcile Recon exclusivity. Economy suppression-on-capability-failure runs in the Economy partial. The accounting and suspension/stall/reap sequence remains unchanged; no additional eligibility predicate was introduced.

## E. Behaviour parity

No score, threshold, target, priority, cost, movement, combat/capture, endurance, capacity, production choice or Housekeeping policy was edited. The first checkpoint compared nine mechanically moved method bodies byte-for-byte against baseline. The subsequent actor-build change adds operation keys to the same claims and stores the same preparation-host flag; role validity predicates are unchanged. A golden result matrix of **5,442** provisioning/execution combinations across Recon, Economy, Raid, Attack, ActiveDefence and Development exactly matches the original master fingerprint:

`88C62EC22801E3E8127D1F2E15205FB3978A32FBA292484249A1E3EABAAECE8F`

A second frozen fingerprint covers **5,760 lifecycle transitions** against the assembly before this domain extraction (after prior validated session/lease migration): `C521F075668400B3A1CBE227EB370F298ADF2F5714D78E01761F77A07DDE5335`. It includes six mission kinds/dispositions, existing/fresh intent, progress, satisfied/external goal, operation start, five legs, accounting and cooldown state. New lease tests distinguish completed Raid/Attack legs (existing durable continuation retained) from terminal main-operation invalidation/failure and verify another operation remains untouched.

A third frozen pre-extraction fingerprint covers **5,760 capability-failure/aging transitions**: `FF74620661454EAA6C80DAF01394D39EB1E5ED37F9B3C9FA875F92FAA5DFBECF`. It exercises missing/contended mover, pool exhaustion, age/stall edges, moverless Recon and Economy collector cases. Twelve added cases include that fingerprint, durable pinning and Raid/Attack side actors whose live phase may already have changed. Both transition fingerprints were captured by compiling the fixture against the immutable pre-extraction assembly, then running with an isolated copy of that assembly.

This proves result-fact parity for that matrix and differential unit coverage, not full native gameplay parity. Representative Unity E2E scenarios were **not run**.

## F. Separate bank audit

Physical stock reads, TurnResourceBook.Free/MayDrawOn, ledger amount/replacement/downgrade/expiry rules, allocator accounting, SpendAuthority token encoding and canonical spending remain unchanged. ReservationOwner is immutable; Owner is a legacy token projection, not another writable identity store. Session delegates the existing end expiry/assert and does not mint, debit or redistribute resources. Old-turn AP is not available through a new session. New tests cover player isolation, end cleanup, aborted prior-scope cleanup, independent operations, completion/deferred upgrade/downgrade/idempotence, typed owner preservation in row copies, terminal pre-intent release and rejected stale-turn writes.

Existing tests cover owner-scoped Economy completion/deferred downgrade/upgrade and same-turn spendability. The deferred H/E/M/T saving claim may exceed physical stock by existing design; completion/reaction committed claims retain existing coverage checks. No stricter bank policy was introduced.

Production/Development paths still use their original bank APIs. The continuation separately reconfirmed that all production ledger mutation sites remain inside MissionLeaseBook. Terminal main-operation tests for all six domains prove actor/resource release does not remove another operation's rows. No reservation reason, amount, upgrade/downgrade, physical stock or spending implementation changed in this phase. Multiple simultaneous native Economy build scenarios remain unverified in Unity.

## G. Separate cache audit

Mutation endpoints use one process-monotonic policy; observation publication does not advance revision. FoundBase now has a tested synchronous child/outer commit boundary and rollback/no-op drops pending stamps. TaskExecutor respects an existing child StateVersionAfter receipt instead of advancing again for the same settled action. A reflection proof against the actual immutable baseline and current assemblies reports child+aggregate revision delta **2 → 1**; new tests cover stamped, unstamped and no-op aggregate cases. This is an intentional freshness correction, not a gameplay rule change. Dirty masks, reason-scoped payloads, freshness equality checks, snapshot refresh placement, AiMapMemory knowledge revision and route/combat keys are unchanged. FoundBase still lets the existing observation pass determine dirty facts; no speculative new mask was added. No new cache was added. WorldDelta freezes its actor/contact/hex payload.

This domain transition phase introduces no revision writer, cache or dirty-mask change; revision/storage adapters and snapshot refresh ordering are untouched. The new atomic session.Apply API and nested synchronous transaction policy are tested. Raid/Attack handoff commit and publication have been combined without changing their masks. Other producers still retain separate commit/observation endpoints. Complete WorldDelta transaction migration and complete write→refresh→next-read gameplay verification remain pending. Cache fixture results are below; native combat cache tests did not pass in either managed baseline or refactor.

## H. Tests and environment limits

The prescribed setup.sh could not install tools in this container. Used the already available Roslyn/.NET SDK and real Unity reference DLLs for a differential compile, plus the existing reflection NUnit harness. These are scratch-only verification tools; no stub/adapter entered product code.

Raw compile baseline has three old reference/stub errors: two FindObjectsInactive overloads and one Mathf.SmoothDamp. Diagnostic copies adapt only those UI/audio calls to compile runnable managed tests; baseline and refactor use identical adapters. This is not a Unity build.

Baseline: **1,509 cases; 984 passed / 525 failed**.
Refactor: **1,629 cases; 1,104 passed / 525 failed**.
**120 new cases pass; zero previously passing cases regress or disappear.** The first error line of every baseline failure is unchanged. The baseline failures include unsupported native Unity Object equality and asset loading. They were not fixed or hidden.

During the continuation, one token API initially returned ReservationOwner where an existing assertion expected a string; the token compatibility API was restored while reservation writers retain typed identity. New test setup initially assigned read-only role projections; it was corrected to use the existing Objective model. During typed payload migration, two existing loan-repayment tests exposed nullable getters coalescing missing identity to struct/integer zero. Getters now use explicitly nullable defaults; new tests distinguish null from legal actor id 0 and prove that read access does not create payload facts. A moved shared-ground helper initially lacked its System.Linq import; the diagnostic compile caught it and the import was restored. The final compile/test gates have zero new failures.

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

1. Complete operation-level normalized disposition migration. The operational Pipeline/session/Continuity entry points now accept MissionStepResult, including generic typed results. Internal domain transitions now bind ordered partial-policy handlers and still use a noncopying MissionTurnOutcome view of the same core record/payloads. ReconcileOutcome has no embedded mission branches. AdvanceIntent now retains common accounting/suspension/aging and delegates actor-role and payload facts to domain callbacks. Its two existing capability-failure aging expressions are one domain composition predicate, preserving Raid's reap-only exception and existing Recon/Economy predicates. ResolveActive still contains domain coordination to review; generic operation-level disposition/remaining transaction acceptance is not claimed complete. Current common lease retirement consumes the existing domain decision to remove the durable intent; it deliberately does not re-interpret leg Completed as whole-operation completion. New-mission extension still needs transition-policy adaptation before the full criterion is met.
2. Audit and consolidate every remaining actual mutation boundary (including continuous ground/capture and air support strike→return sequences). Known TaskExecutor stamped-child duplication and FoundBase are fixed and proven at the synchronous scope boundary; the global exactly-once transaction criterion is not claimed yet.
3. Remove remaining compatibility callers only after parity. Current adapters: MissionIntentState methods (fixtures and Recon production callers), ActorCommitments.FromIntents/static validity methods and detached pass views, MissionTurnOutcome legacy projections and noncopying domain ingress view, V2StateVersion readers/tests, interrupt discovery/capability wrappers. The old resource ledger's production mutation callers are now only MissionLeaseBook.
4. Full Unity 6000.5.4f1 EditMode suite and specified Recon/Economy base/Attack/Raid/Defence/Development E2E scenarios. Native null/combat/asset failures cannot be evaluated by this managed harness.
5. Complete bottom-up domain-local support release and generated-output handling review, all transaction/cache gameplay acceptance and mission-extension proof before declaring the architectural goal achieved.

No dual-written storage was introduced: one persistent store per domain, one actor claim table per active session, the original resource row store and one revision policy. Detached pass views are derived from intents/snapshot and do not retire or mutate the session's operation lifecycle. The new generic lease coordinator contains no mission eligibility, scores or strategic transitions. The full task is still incomplete because operation-level transitions and remaining mutation adapters still require mission-specific migration work. Common ingestion plus typed payload transport alone does not satisfy the end-to-end new-mission criterion.
