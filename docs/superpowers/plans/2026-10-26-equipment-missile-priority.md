# Equipment versus missile priority implementation plan

> Execute inline using executing-plans; one whole-change reviewer after implementation.

**Goal:** Prioritize useful missile production over optional reserve equipment while preserving needed recipients and no-missile reserve upgrades.
**Architecture:** Existing EquipmentUpgradeValuation partial, NonCombatCardPlayer aviation candidate builder and admission/memo owners. No new budget/manager.
**Tech stack:** Unity 6000.5.4f1 / C# EditMode tests.
**Spec:** docs/superpowers/specs/2026-10-26-equipment-missile-priority-design.md (approved situational approach; coefficient 0.25 is implementation calibration).
**Remote base:** fa1a55a647d14939e95312af04ceda628c94aabf. Local checkpoint 2590a16 is synthetic and MUST NOT be used as remote parent.

## Review focus
- Same-key copies must not all acquire a preparation purpose; actual host/support and enough specific copies remain protected.
- Threatened bases, economic/scouting/development roles and operator value survive equipment calibration.
- Existing missile stock/hand cards, route/capacity/resource/AP/launch constraints decide whether production really competes.
- Apply one signed gain policy to all equipment consumers; no calibration of costs, failure probability or negative vetoes.
- Cache scopes/keys must reflect copy identity, target-roster multiplicity, duty and missile-state changes without per-recipient route-search storms.

## Task 1 — finite required recipients
- [x] Add regression tests for quota2 of3 copies, physical host preference, hand copies, status/support/defence roles and no matching overclaim.
- [x] Implement recipient selection in EquipmentUpgradeValuation.Priority.cs and use it for preparation-purpose lookup/labels; cache only inside existing EquipmentTargetMemo.
- [x] Verify addressed callsites and syntax; record unavailable NUnit.

## Task 2 — real missile alternative and equipment calibration
- [x] Add integration regressions around existing AiMissileStockTests setup for available/supplied/unreachable/unaffordable alternatives and identity/accounting neutrality.
- [x] Add read-only HasExecutableMissileProduction to existing NonCombatCardPlayer, using existing BuildAviationPlays and source eligibility. No parallel score/budget model.
- [x] Apply 0.25 to positive optional-recipient gains in the one EquipmentDeltaParts pipeline; preserve negative components and needed roles. All consumers share that value.
- [x] Cover source, attachment/deploy, held and generated paths; avoid double scaling.

## Task 3 — cache, broad review and delivery
- [x] Extend admission/recipient keys for finite copy allocation and duty; retain existing missile/budget/path/supply facts.
- [x] Run git diff --check, differential tree-sitter and .meta/GUID/caller audits; no YAML changes expected.
- [x] One fresh-context review of full task diff, resolve concrete Important/Critical findings, update journal/report and publish separate reviewable branch/PR based on real remote parent. No automatic master merge in this new task.

## Ledger
- Initial worktree contains the already-published #237/#238 tree. Saved as local checkpoint2590a16 and isolated new branch work/ai-equipment-missile-priority; no prior work is reimplemented.
- Environment: Unity/dotnet/mono absent. User previously accepted Unity testing locally; do not claim RED/GREEN, compilation or NUnit pass. Static checks supplement, never replace runtime checks.
- Pre-flight: recipient purpose and missile pressure both feed EquipmentDeltaParts; cache key updates must travel with those inputs. Existing aviation candidate builder is the only executable alternative witness, existing allocator retains spending authority.

- Implemented finite physical recipient selection, shared signed-gain calibration, the canonical aviation-production witness, Phase-A required-recipient hint and memo scopes. Added tests for same-key copies, held support/scout/secondary-base duty, signed vetoes, live/hand/deploy/Phase-A utility, covered/unreachable/unfunded/closed-window/used/full-hand alternatives and identity/accounting neutrality. Tests are authored, NOT executed.
- Whole-change review found and resolved: incoming demanded bodies missing the required hint; preparation and mobilized peak counting separate full copy quotas; stale verdicts across window observation/live spendability/quota/retry changes; unreserved forecasts reusing an already tried source. Exact retry state stays with MaterializationReservation. Forecasts without it after any generation attempt preserve reserve utility until actual card selection; an explicit untried alternative still competes.
- Fixed an existing qualified-name typo in AiIndependentDevelopmentTests (UnityEngine.UnityEngine.Object -> UnityEngine.Object) found during the caller/compile-plausibility audit.
- Verification: git diff --check; differential tree-sitter on all 10 task C# files (zero parser errors); both new .meta pairs present and GUIDs unique; existing assembly predicate callers remain 8 PlanGather / 6 PlanPreparationAssembly. No YAML assets changed in this task; rerunning both validators on the prior three assets also passed. Unity/dotnet/mono are absent, so no real compilation, NUnit/EditMode or gameplay execution occurred. The misleading missing-dotnet compile script result is not used.
- Publication uses verified real remote parent fa1a55a647d14939e95312af04ceda628c94aabf and its tree, replacing only the task's files. Local synthetic checkpoint2590a16 is not a remote parent. Report: docs/ai-equipment-missile-priority-report-2026-10-26.md.
