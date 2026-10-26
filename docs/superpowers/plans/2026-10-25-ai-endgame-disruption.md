# AI endgame and Disruption implementation plan

**Goal:** Execute the nine corrections approved in the conversation on 2026-10-25.
**Architecture:** ARCH-02; extend existing owners and lifecycle, no new AI axis or manager. Defence of every own Base remains intact. Slowing an army is a tradeoff, not intrinsically a failure.
**Tech stack:** Unity 6000.5.4f1, C#, existing EditMode regressions.
**Spec:** The approved nine-point scope in this conversation, including building-only disruption frozen per encounter.

## Constraints and review focus
- Read and trace canonical callers and consumers before edits. Existing partial classes are one owner, not duplicated implementations.
- Disruption: alive hostile carrier on the battle hex, or in the incoming ground army; building defence only; no stacking; frozen at encounter start; next encounter rechecks live presence. Own terrain/formation/armour unchanged. Existing stealth rules apply.
- No sacrifice of a secondary Base, no blanket ban on slow reinforcements, no forced losing attacks.
- Verify downstream mutation, upstream admission/valuation, cache invalidation, save/load, claims, resource holds, interruptions and chained fights.
- Manual Unity runs are the owner's responsibility and do not block delivery.

## Tasks
- [x] 1. Recon: prevent zero-purpose Refresh lookahead cycles; retain safe-route transit and service Attack observation needs.
- [x] 2. Gather: apply existing home/march policy to projected departure and actual Gather-to-Assault transitions, including preparation.
- [x] 3. Route: evaluate contacts on the actual fastest-by-turns path with current/max MP; preserve honest remembered opposition.
- [x] 4. Reinforcement: price loss of mobility against the active destination; reject only destructive task transitions; retain beneficial slow rosters.
- [x] 5. Economy: project preparation-site selection and operator duty after adding the new hero, before payment.
- [x] 6. Missiles: give independent useful strikes an executable existing air-support lifecycle; reserve/account once, retire correctly and recalculate after damage.
- [x] 7. Combat: investigate admission/retreat discrepancy; fix proven disagreement, correlate both forecasts without forcing combat outcomes.
- [x] 8. Diagnostics: phase-accurate Attack completion and complete Scout key formatting; concrete route/admission reasons.
- [x] 9. Disruption: canonical live/fog-honest defence semantics, frozen battle/UI state, estimator/coverage/cache integration, card ability metadata and AI delivery before assault.
- [x] Final: bottom-up review of every changed consumer and whole-scope cross-check; appropriate static/compiler/tests; disclose unavailable checks.

## Logic and dependencies (read in both directions)
Recon observation -> assignment -> step potential/lookahead -> canonical movement -> observation -> next step.
Attack objective -> projected assembly/gather -> AttackMarchPolicy -> allocation -> provisioning -> handoff/move -> Continuity -> home/target re-evaluation.
Economy demand -> projected roster + projected preparation duty -> placement -> refreshed snapshot -> operational lease -> Economy intent.
Missile contact -> blast value -> production/deployment -> existing air-support proposal -> allocation -> sortie -> damage -> continuity/re-observation.
Disruption carrier/known profiles -> structure defence -> WorthIt/coverage -> assembly and target selection -> live encounter freeze -> BattleAi/damage/UI -> next encounter.

## Execution ledger
- Baseline: remote master cbe5e6f04e213fc6ef0f61c9e96c98ff81070b25. Recovered truncated local snapshot files from canonical remote blobs before edits.
- Verification environment: Unity, dotnet and mono absent; setup cannot install packages. compile_check masks missing dotnet as zero errors, so that output is NOT a passing compile baseline. Do not claim compilation/tests passed.
- Authorized execution: implement all tasks directly without a further approval stop. No master merge/push requested.

- Root integration: Gather keeps observation waits, retires home-forbidden departure; complete projected donor locations used. Route loss filtered per candidate, not blanket slowdown. Independent domain implementers delegated under dispatching-parallel-agents; root reviews every change.

## Completion ledger — 2026-10-26
- Continued the existing worktree/diff; no history reconstruction or implementation restart.
- Delivery proof checks every entered hex and endpoint with IsBlockedForScout(true), plus detector risk and current MP. WaitForDisruption requires an Active owned hidden source, arriving this turn, with activation already paid or zero-cost. Embedded/already-arrived sources do not delay the assault. Tests use canonical remembered building knowledge; Scout initializers use Objective.
- Preparation: rosterAdmissible is propagated through PlanPreparationAssembly, AssembleSameHex, PickAttachableHero and all production callers (planner, continuity, demand, provisioning). Invalid slow heroes/bodies are skipped while fast alternatives remain eligible. The complete pinned roster is checked in TryRunPreparationStep before the first world mutation.
- Added regressions for admissible slower alternatives, rejected pinned preparation with unchanged donor/host, all disruption-wait cases, and suspended Attack obligations. Tests are authored, NOT executed in this environment.
- Fresh-context read-only review found one Important: two individually spareable donors could jointly uncover a remote own base. Fixed by comparing the full projected departure's hold with the original hold independently of host return ETA; every production PlanGather caller now supplies the appropriate departure veto, including preparation, continuity and demand. Existing decisive-before-enemy victory exception remains in Assess. Added ProjectedGatherCannotTakeBothDefendersOfARemoteBase with retained-defender control.
- Bottom-up review covered live ability/battle freeze, UI defence breakdown, sequential forecasts/coverage/cache keys, fog-honest memory, assembly/preparation, admission and funding, execution and intent retirement. No separate AI manager/axis added; no retreat threshold changed.
- Ruling: requested delivery may proceed without runtime verification because Unity/dotnet/mono are absent and the user explicitly accepts Unity testing on their machine. No RED/GREEN, compilation or NUnit pass is claimed; static evidence and manual test scenarios are recorded in the report.
- Ruling on review scope: balance calibration awaits real match evidence; no speculative threshold changes. The existing roster-size limits bound alternative search, but runtime profiling remains for Unity. Summoned carriers are not authored and summons are removed before the next strategic encounter; freeze occurs before battle summons, so their exclusion from projected start-of-battle sources is retained.
- Verification: git diff --check; differential tree-sitter check of all changed C# (only the three baseline parser limitations); both YAML validators on all three modified assets; all four new C# .meta files present with unique GUID; all production preparation/gather predicate callers audited. Full results and Unity scenarios: docs/ai-endgame-disruption-report.md.
- Remote baseline: checked master still equals cbe5e6f04e213fc6ef0f61c9e96c98ff81070b25 and every modified tracked file's HEAD blob matches that remote tree. Publication uses that real parent and base tree, never synthetic local HEAD. Separate branch/draft PR only; no master mutation or merge.

## Post-merge audit — explicit user request, 2026-10-26
- User authorized commit/merge/push to master, then another bottom-up review. PR #237 merged to remote master e93f58cb5c79fb8efb178a0d7b62d29c84aa04c7; this supersedes the earlier no-master integration scope.
- GitHub terrain CI failed at its unchanged asset-count assertion (baseline AridSteppe Boiling mud count3 vs1, Desert Acid lake count3 vs2). mapGeneration equals baseline. Terrain algorithm checks 51/51 passed in remote managed-stub runner; no AI/Unity compile claim.
- Fresh source review found two Important existing consumers missed before: a direct card for its own active preparation was treated as foreign committed power; a full preparation's card-refit solver omitted PreserveTaskRoute. Fixed both, plus direct deployment/WAIT/pre-payment guard to preserve one delivery contract.
- Exact ConsumerIntentKey/PrimaryArmyId/Active Gather preparation exemption preserves all other claims and independent support constraints. Existing AttackObjectiveHex now accompanies the preparation demand.
- Added parameterized AiAttackBaseRefitTests for own/foreign consumer, MP1 rejection/MP2 admission, full refit/direct delivery/WAIT parity and stale pinned operation validation. Tests authored, not run; environment exception remains explicit.
- Verification: diff --check, 86-file differential syntax check with identical baseline parser limitations, YAML validators, GUID and caller audits. Updated docs/ai-endgame-disruption-report.md carries post-merge findings and Unity scenarios. Follow-up is a separate commit on the real merged master parent; no synthetic ancestry.
- Residual demand transport audited: PhaseAPass now copies ConsumerMissionKind alongside ConsumerIntentKey. Regression exercises PublishEarlyResiduals and rechecks own/foreign preparation delivery through the cloned demand.
