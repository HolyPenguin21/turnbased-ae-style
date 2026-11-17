# FIX-01: facility destruction notification boundary

Date: 2026-11-17. Base: `ecf785cc8897f44a09221cc96969d6a5c9f10d58`.
Branch: `fix/facility-destruction-notifications`. Unity project version: 6000.5.4f1.
Scope: the FIX-01 task from the non-AI refactoring specification, edition 2026-11-16.

## Change

`BuildingRegistry.CaptureOrDestroyIfUndefended` opens the existing
`VisionSystem.BeginMutation` scope after its eligibility checks and retains it
through both `CaptureOrDestroy` and the mover's `MoveCurrent = 0` sweep.

Previously, the inner destruction scope published callbacks before the caller
ended the mover's movement. No new transaction mechanism or gameplay rule was
introduced. The public signature, base-capture movement, defended/hidden no-ops,
carrier return and prior-vision witness ordering remain unchanged.

## Task checklist

- [x] Trace destruction, observer dispatch, arrival, resident reveal and retreat callers.
- [x] Add regression coverage before changing production code.
- [x] Reproduce nonzero MP in destruction callbacks on the original implementation.
- [x] Extend the existing mutation boundary through all mandatory consequences.
- [x] Check every callback sees the removed building, stopped members, final army index,
  returned aircraft and removed airfield shell.
- [x] Check prior-owner observation before vision loss, final observed member MP,
  and absence of knowledge for an unrelated player.
- [x] Check repeat invocation and nesting inside an existing mutation scope.
- [x] Independently review the final production change and all three real callers.
- [ ] Complete native V-01 acceptance in Unity 6000.5.4f1.

## Regression evidence

The initial implementation added four NUnit cases to existing fixtures:

- `WorldMutationContractTests.UndefendedFacilityNotificationsObserveStoppedMover(false/true)`.
- `StealthBuildingContactTests.ResidentFacilityDestructionNotifiesAfterMovementEnds`.
- `ArmyMovementOperationTests.FacilityArrivalNotifiesAfterMovementEnds`.

Before the production fix: 3 failed, 1 passed. Normal arrival exposed 4 MP;
resident reveal exposed 2 MP; direct destruction callbacks exposed intermediate
movement. The already-nested case passed, as expected.

After the fix: all 4 passed. The initial focused run of all runnable cases in these
three fixtures passed **92/92**. The broader comparison had 95 cases: 89 passing
before, 92 after; no previously passing case regressed.

Three existing native cases errored identically before and after and were excluded
from the 92-case portable run:

- `ArmyMovementOperationTests.TransitExploresUnguardedEventAndStopsBeforeDestination(false)`.
- `ArmyMovementOperationTests.TransitExploresUnguardedEventAndStopsBeforeDestination(true)`.
- `ArmyMovementOperationTests.UnguardedEventWaitsForHostileContact`.

They fail at native `GameObject` construction in this environment. Their Unity
results remain pending. The initial pass only traced actual retreat through
`BattleEngine.PerformRetreat`; the follow-up below adds executable coverage.

## Bottom-up and horizontal recheck

The follow-up reviewed all three production callers and the notification consumers
in human observation/memory, AI observation/structure-loss memory, map presentation,
turn/elimination handling, tutorial invalidation and radio invalidation. AI policies
and their implementation were not changed.

| Boundary | Verification |
| --- | --- |
| Destruction/roster/container/MP | Existing callback test observes all members, index, cargo return and empty-shell removal together |
| Prior vision and fog memory | Existing test observes final member MP before former-owner vision disappears; unrelated viewer gets no observation |
| Normal arrival/save busy fact | Arrival test additionally confirms movement stays busy during destruction publication and settles before returning |
| Actual retreat | `RetreatOntoFacilityNotifiesAfterRelocationAndMovementEnd` calls real `BattleEngine.PerformRetreat` with one legal exit and checks callback location/index/MP |
| Callback resets notification epoch | `FacilityObserverResetCannotPublishStaleNoticesOrChangeMovementAfterCallback(false/true)` covers reset from destroy and pre-vision visual callbacks, cancelled stale notices and no movement write after callback |
| Base capture | `UndefendedBaseCapturePreservesMovementForObservers` checks both visual and capture callbacks retain mover MP |
| Guard/no-op behavior | Existing hidden, defended, own-building and repeated-call cases remain in the passing suite; the repeat call also covers an already-absent building |

The two reset cases fail with 4 MP when the production fix is removed **only in a
disposable verification copy**, and pass with the fix. The added retreat and base
cases pass both versions and protect existing behavior. There are now eight
FIX-01-specific cases in total.

Final portable result: **96/96 runnable cases pass**, including the original 92
and four added follow-up cases. The same three native-only cases remain outside
the portable acceptance. The save condition was traced from
`GameTurnController.ReadSaveConditions().Moving` to
`ArmyRegistry.HasMovementInProgress`; the arrival test exercises that underlying
busy fact, not native save UI or on-disk capture.

No additional production defect was found in the FIX-01 boundary; this follow-up
changes tests and this report only. Native rendering, actual marker lifetime and
Unity scene/coroutine integration remain part of V-01 acceptance.

## Verification environment and limits

The temporary managed runner compiled all runtime C# sources and the three test
fixtures with .NET SDK 8.0.428, NUnit/NUnitLite 3.14 and legacy Unity reference DLLs.
It reused the repository's `Tools/match-save-verify/audit-world/native-run.py`
portability setup, with disposable adapters for native object null semantics,
map construction, logging, engine API declarations and managed math. Destruction,
movement, stealth, registries, notification batching and observation remained the
real production implementations. No verification adapter was added to Assets.

The repository's unadapted all-Assets compile template reported the same 20 legacy
reference/stub errors at base and current (17 distinct diagnostics after location
normalization), with **zero new diagnostics**. Declaration errors can hide body
errors; this comparison is not proof of a Unity build. The portable full-runtime
compilation and 92-case test run are separate evidence.

`git diff --check` passed. Independent read-only review found no material defect.
Unity compilation, full native EditMode/PlayMode suites and the in-editor gameplay
scenario were not run. Scenes, prefabs, GUIDs, save schema and AI sources were not
changed. The native V-01 gate is not marked complete by this report.
