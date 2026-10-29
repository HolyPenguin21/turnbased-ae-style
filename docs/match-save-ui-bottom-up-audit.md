# Save/Load UI bottom-up audit — 2026-10-29

Scope: the uncommitted Save/Load UI implementation on `feature/save-load-scene-ui`,
source baseline `6c2643b3185cf50f26620efe6e57a95df50f0185`. Existing implementation
and all earlier fixes were retained. No commit, merge or push was performed.

## Confirmed defects and fixes

1. **Deleting one new position removed unrelated unsaved positions.**
   `MatchSaveSlotsUI.ReloadSlots` reconstructed the complete list from disk plus six base
   positions. Creating positions 7/8/9 and deleting position 8 also removed 7 and 9.
   The open save window now retains its current positions, removes only the chosen
   position after successful deletion, and clears transient positions on the next Show.
   A failed directory refresh retains the current list and reports the deletion error.
   Load mode still lists disk-backed positions only. Actual ConfirmDelete/ReloadSlots
   logic reproduced the failure before the fix; five managed dialog-flow cases pass.
   A matching native prefab-based EditMode regression was added.

2. **An inaccessible save directory looked empty; deletion could report success.**
   `Directory.Exists` returns false both for a missing directory and when a parent
   directory denies traversal. This suppressed access errors in Slots/Delete, causing
   Continue to fall back to campaign or deletion to publish success without removing
   the save. Real Unix permission fixtures failed in both paths before the fix.
   Slots/Delete now perform their operation directly and suppress only
   DirectoryNotFoundException. Access/IO errors reach the existing UI error handling;
   MainMenu keeps the load list reachable when enumeration fails.

## Connections checked

- Primary/backup/tmp deletion order, backup-only recovery, corrupt/unsupported slots,
  canonical numeric IDs, unrelated files, partial failures and no metadata-time quarantine.
- Temporary positions, base positions, paging, selection reset, confirmation cancellation,
  repeated opening and listener lifecycle.
- Continue priority, campaign controller/reward continuation and stale operation checks.
- Authored prefab instances, CanvasScaler inheritance, CanvasGroup/input ownership,
  capture eligibility, overlay release, in-game menu return and restore error paths.
- Gameplay fingerprint source/importer filtering, generated UI catalog exclusion and
  protected world-marker dependencies. The previously disclosed one-time incompatibility
  with the old fingerprint algorithm remains; no legacy validation bypass was introduced.

## Fresh evidence

- 30 production storage/JSON/fingerprint cases: passed, no failures or skipped cases.
- 5 production dialog-data-flow cases: passed, including actual access denial.
  Only native Draw, CancelDelete presentation and selection are adapted in a disposable copy;
  this does not prove native EventSystem or rendering behavior.
- 24 full-file capture/decode/restore transaction scenarios: passed with explicit native adapters.
- 4 compiled Continue-policy combinations, 3 authored object-graph checks and both YAML
  validators for the prefab and both scenes: passed.
- All 955 C# source files: syntax passed. Baseline/current runtime binding and all three
  changed/new native test fixtures passed against cached older Unity refs and package adapters.
- 7 verification-tool unit tests: passed.
- A fresh independent reviewer checked the UI/campaign chains and the fixes; no further
  confirmed important defects were found.

Unity 6000.5.4f1 Editor/player was unavailable. Native UI tests, raycasts, focus/navigation,
rendered layout across aspect ratios and actual scene execution still require Unity.
