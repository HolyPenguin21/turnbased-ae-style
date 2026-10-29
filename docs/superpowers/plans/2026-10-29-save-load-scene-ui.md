# Save/Load scene UI implementation plan

> **For agentic workers:** Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Route Continue by saved matches > campaign > disabled; author one editable Save/Load prefab in MainMenu and Game; support confirmed slot deletion.

**Architecture:** Retain MatchSaveStore and the existing capture/restore pipeline. Replace transient UI construction with serialized controls in a reusable prefab; use six reusable authored rows and pagination. Game menu owns the scene window and restores its own controls after closing it.

**Tech Stack:** Unity 6000.5.4f1, C#, uGUI, TMP, Unity YAML.

**Spec:** User-approved analysis and clarification in this conversation: when both campaign and matches exist, open the load window; no campaign entry inside it.

## Global Constraints
- Save only on a settled confirmed human turn, outside combat and pending actions; retain CanSaveMatch checks.
- Preserve GUIDs, existing on-disk save format, campaign operation validation and reward receipts.
- Implementation/review initially remained uncommitted; commit, merge into master and push were explicitly authorized by the user after the audit.
- New UI text is English. Changes to purely UI assets must not invalidate future saves.

## Review Focus
- A backup-only, corrupt or future-version slot must be visible and deletable.
- Cancelling deletion and repeated opening must not mutate files or duplicate listeners.
- Window must remain interactive while GameMenu blocks other gameplay canvases.
- Deleting the last save must refresh Continue to campaign or disabled.
- Changing pure menu visuals must leave the gameplay compatibility fingerprint unchanged.

### Task 1: Storage deletion
- [x] Extend MatchSaveFileTests with main+backup+tmp removal, backup-only, invalid/missing slot, deletion failure and other-slot isolation.
- [x] Observe failing tests; implement MatchSaveStore.Delete(int slot), removing backup before primary to prevent fallback resurrection on partial failure.
- [x] Run production file tests.

### Task 2: Authored dialog and routing
- [x] Add native tests for reopen/close, delete cancel/confirm, prefab bindings and Continue precedence.
- [x] Refactor MatchSaveSlotsUI to serialized reusable controls and instance Show; retain capture/restore safety checks.
- [x] Bind MainMenuController and GameMenuPanelUI directly to the scene instance. Remove AddMatchLoadButton and apply strict Continue priority.
- [x] Author Assets/Prefabs/UI/Panels/MatchSaveSlots.prefab with full-screen blocker, scalable panel, six rows and delete confirmation; install prefab instances in both scenes.
- [x] Verify assets and runtime binding; native UI tests remain for the editor when unavailable.

### Task 3: Compatibility and verification
- [x] Add coverage for compatibility input filtering; omit scenes and pure UI/audio/font/menu assets while retaining gameplay content protection.
- [x] Run file tests, syntax/runtime binding against immutable base 6c2643b3185cf50f26620efe6e57a95df50f0185, integration and both YAML tools.
- [x] Review bottom-up, obtain a fresh whole-change review, fix important findings and record remaining Unity checks.

## Execution ledger
- Setup: fresh isolated clone, feature/save-load-scene-ui; source baseline 6c2643b3185cf50f26620efe6e57a95df50f0185. No existing user changes.
- Ruling: execute the already authorized implementation inline without a second approval stop. Keep commits/push/merge for an explicit later instruction.
- Task 1: complete — production file tests observed missing Delete RED, then GREEN; main/backup/tmp deletion, backup-only, missing slot, partial failure and other-slot isolation covered.
- Task 2: complete — one authored prefab and both serialized instances; source runtime binding and test fixture binding passed against immutable base. Native lifecycle/UI tests added but require Unity to execute.
- Review: fresh reviewer found background keyboard navigation, list-time file quarantine, MainMenu canvas scaling and generated UI catalog fingerprint propagation. Fixed by hiding/restoring MainMenu controls, read-only metadata fallback, mounting under the scaled scene Canvas, and omitting the generated catalog/HUD-only shader from compatibility inputs. File quarantine and compatibility regressions were observed RED then GREEN; object-graph scaling check added.
- Ruling: fingerprint algorithm changes once; older fingerprint saves remain rejected rather than weakening validation. New saves tolerate later edits in declared UI directories and the generated asset registry. The original save files are retained.
- Ruling: keep author-created six base save positions; deleting occupied base positions empties them; extra saved/temporary positions disappear. Load mode lists occupied slots only.
- Final verification: 28 production file/compatibility scenarios passed; 24 full-file capture/restore transaction checks passed with explicit native/UI adapters; 4 compiled Continue-policy combinations passed; 3 authored object-graph checks passed; both YAML validators passed for the prefab and both scenes; 7 verification-tool tests passed. All 955 C# files passed syntax checks; baseline and current runtime binding plus all 3 changed/new native test fixture files passed against cached older Unity refs/adapters. No Unity Editor/player or native UI test execution is claimed.
- Verification infrastructure note: one binding rerun collided with a still-running check using the same scratch directory. A fresh isolated binding-verified directory completed successfully; no game-code change was needed.
- Final state: implementation remains uncommitted on feature/save-load-scene-ui as requested scope did not authorize commit/push/merge. No deferred review findings.
- Follow-up user-requested bottom-up audit: confirmed and fixed unrelated unsaved positions being discarded during deletion/refresh, plus access-denied directories being treated as missing by Slots/Delete. Both reproduced before fixes. Fresh evidence: 30 production file checks, 5 dialog data-flow checks, 24 full-file integration checks, 4 Continue combinations, 3 object-graph checks, both YAML tools, 7 tool-unit checks, all-source syntax and baseline/current binding plus changed-fixture binding. Native Unity execution remains unavailable. Full findings: docs/match-save-ui-bottom-up-audit.md.

- Publication authorized: user requested commit, merge into master and push. Remote master checked at 6c2643b3185cf50f26620efe6e57a95df50f0185; fresh pre-commit checks passed: 30 storage/compatibility, 5 dialog-flow, 3 authored graph checks and both YAML tools. Native Unity remains unavailable.
