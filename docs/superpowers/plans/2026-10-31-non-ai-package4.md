# Package 4 Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans; implement the supplied Package 4 specification in order, without changing AI policies.

**Goal:** separate movement state/rules from animation and observation from input refresh.
**Architecture:** ArmyMovementOperation owns copied route, legal step, activation, MP, indexed position, reactions and one terminal result. ArmyController renders it. MapObservationSystem observes committed world changes for each eligible viewer. HumanVisualMemory stores immutable observations and offers detached legacy display projections; frozen renderer snapshots remain per viewer.
**Tech Stack:** Unity 6000.5.4f1, C#, NUnit EditMode and existing Extended coroutine fixtures.
**Spec:** user-supplied Package 4, 4.1–4.4 and acceptance scenarios.

## Constraints and review focus
- Reuse ArmyRegistry.MoveArmy, HexPathfinder, ArmyActions, existing encounter entry and ReconcileHexVisualState.
- No AI policy/balance changes, no new live identities or activation/transport effects in historical reads.
- Reentrant cancellation/removal while notifications run; death during animation/reactions.
- Changed transport, ground/air classification, route eligibility after reactions.
- Disabled selection still observes every viewer; capture/destruction behind fog preserve history.
- Restore/capture retain frozen visual coverage and viewer independence.

### Task 1: movement lifecycle
- [x] Add operation tests for cancel before/after entry, death, route mutation and reaction roster changes.
- [x] Extract copied plan, legality, MP, indexed entry, reaction/cancellation and one-shot result into ArmyMovementOperation.
- [x] Adapt ArmyController and IssueMoveOrder; effects execute from the operation through existing actions/encounter entry.
- [x] Verify movement tests and existing pathfinder/aviation/lifecycle coverage.

### Task 2: observation ownership
- [x] Add world-fact tests with no enabled selection, independent viewers and hidden changes.
- [x] Introduce explicit MapObservationSystem at committed visibility/content/stealth/building boundaries; move turn expiry to turn owner.
- [x] Make selection refresh project observations and frozen appearances without writing memory.
- [x] Verify observation and mutation regression fixtures.

### Task 3: immutable historical payloads
- [x] Test detached display mutations, restore/capture, live ID sequence and transport/activation isolation.
- [x] Store ObservedArmy/ObservedUnit records; preserve compatibility projection without shared mutable state.
- [x] Capture movement/defense at observation; remove hidden live lookups from remembered projection; preserve codec and frozen coverage.
- [x] Verify memory, remembered state and presentation codec fixtures.

### Task 4: integration and bottom-up review
- [x] Trace registry, movement, aviation, stealth, contact, observation and save consumers bottom-up; search duplication.
- [x] Run available differential compile/tests against unchanged base; document unavailable native Unity checks.
- [x] Document settled/busy/cancellation and immutable observation contracts for packages 5/8.

## Execution ledger
Base: 5f752169. Fresh checkout, branch refactor/non-ai-package-4. Supplied specification is the authorized design. No additional design approval needed. Unity Editor unavailable; reconstruct managed verification outside Assets, never count it as native Unity validation.

Task 1–3: implementation present; focused managed tests exercised. Observation baseline reproduced missing observations and mutable-history failure; cancellation regressions reproduced pending-headless removal and cancelled-reaction arrival. Native coroutine tests expanded but cannot run without Unity.
Ruling: retain full reconciliation and separate viewer frozen visual caches — required observer semantics take precedence over local refresh optimization.
Ruling: historical restore must not advance live identity counters — MatchIdentityState remains the owner of issued live high-water marks.

Final review findings addressed: cancellation visual pose, existing save busy gate, default headless AA; follow-up TDD cases cover notification cancellation, hidden destination defense, usable aircraft movement and resident reveal while a headless move is pending.
- [ ] Native Unity 6000.5.4f1 full EditMode/Extended and interactive visual validation (Editor unavailable).

Final managed evidence: 775/775 explicit passing list; final broad 1011 total / 775 passed / 236 failed; 0 new regressions among baseline pass cases (one explicitly migrated identity test), 235 unsupported reference/native/content cases and the same pre-existing inventory assertion. Exact names in Docs/NonAI_Package4_Verification.md. Metadata present for all six new scripts/tests; existing GUIDs unchanged; git diff --check clean.


## Follow-up bottom-up audit (user requested)

The pre-audit source snapshot is retained independently at verification/audit-base; do not replace it. Review proceeds from legal stepping/registry/MP through AA, death and cancellation, then turn observation/stealth, frozen projection, events, AI waiting and save gates.

Confirmed defects fixed: duplicate movement drivers; memory expiry without turn re-observation; stale moving-casualty notification suppression; headless death preserving an empty shell; cancellation calling success settlement and clobbering reentrant replacement; synchronous event Skip rejected while old operation is busy; live neutral everSeen bypass. Preserve long neutral memory using immutable records, exclude transient event guards through the existing helper, and share remembered-marker inspection across human/observer paths. Update native tests that still reflected removed position storage or drove memory through a UI callback.

Evidence and remaining native validation: Docs/NonAI_Package4_BottomUp_Audit.md. Commit/merge/push were not authorized during the audit; the owner subsequently authorized publication to master.

Follow-up verification: eight new red-to-green managed cases; 1011/775/236 pre-audit → 1019/783/236 broad, one explicit neutral-query expectation migration, no new failing cases. Final source explicit passing list 783/783, exit 0. Extended native fixture reference compilation exit 0; no native Unity execution. Independent observation and movement reviews completed, concrete findings addressed. Existing metadata/serialized assets unchanged; diff whitespace check passed.
