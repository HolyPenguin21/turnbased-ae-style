# Task: read-only audit of the three heaviest AI V2 files

Assigned to: a fresh agent, no prior context on this session. Read this file fully before
touching any code.

## Goal

The file-split refactor in `docs/ai-v2-file-split-refactor-tasks.md` (Tasks 1-5, all marked
Done) covered `AiConfigV2.cs`, `MissionIntent.cs`, `DemandLayer.cs`, `WorldAnalysis.cs`. It did
**not** cover the three files that are now the largest in `Assets/Scripts/Ai/V2/`:

| File | Lines |
|---|---|
| `Provisioning/ProvisioningManager.cs` | 3241 |
| `Continuity/MissionContinuityLayer.cs` | 2803 |
| `Orchestration/AiStrategyV2Pipeline.cs` | 2262 |

Your job is **analysis only** — produce a written report and a concrete task list for a
mechanical file split (same style as the file-split-refactor doc). Do not write or move any
code in this pass.

## Why this matters

The point is not code aesthetics. Root-cause debugging over `Logs/AiDebug.log` gets slower the
more a single owning method is buried inside a multi-thousand-line file mixing several
responsibilities. This mirrors the reasoning already recorded in
`docs/ai-v2-file-split-refactor-tasks.md` — read its intro before starting, and follow the same
ground rules philosophy (pure code motion, no behavior change, no logic edits mixed in).

## Also re-check: `docs/ai-duplicate-methods-analysis.md`

That report (2026-09-13, partially remediated per its own update note at the top) found
duplicate/drifted logic specifically inside these three files — e.g. `ProvisioningManager.cs`
sections B/C/D (provisioning-skeleton copies, AP-gate copies, micro-helper copies) and several
`D1`-`D15` drift entries. Before proposing a split, check which of those findings are still
present in the current code (some may have been fixed since without the doc being updated) and
fold any still-open ones into your report — a method that's both misplaced *and* duplicated
should be flagged once, not treated as two separate problems.

## Deliverable

A new doc, `docs/ai-v2-provisioning-continuity-pipeline-split-tasks.md`, structured like
`docs/ai-v2-file-split-refactor-tasks.md`:

1. For each of the three files: a table of its top-level members (methods/nested types) with
   line count and a one-line responsibility label — this is how you find the real internal
   boundaries, not by guessing from names.
2. A proposed target file layout (partial-class split or type-level split — decide per file
   using the same criteria the existing doc used: is it one class with separable static/instance
   methods, or several independent types bundled together?).
3. Explicit call-out of any method used across the boundary you're proposing to draw (shared
   helpers, cross-references) — these need the same "decide from an actual call table, not from
   memory" treatment that Task 5 (`WorldAnalysis.cs`) demanded.
4. Any still-open duplicate/drift finding from `ai-duplicate-methods-analysis.md` that lives in
   one of these three files, cross-referenced by its original ID (e.g. D1, D4) so it isn't
   re-discovered from scratch.
5. A numbered task list ordered lowest-risk-first, same acceptance criteria section as the
   existing doc (method bodies unchanged, namespace/modifiers unchanged, `.meta` files included,
   `dotnet build Assembly-CSharp.csproj` clean, structural verification not log-diffing).

## Ground rules

- Read-only pass. No edits to `.cs` files. The output is the doc above.
- Do not re-litigate or re-run the duplicate-methods analysis from scratch for the whole
  codebase — scope it to these three files, referencing the existing report rather than
  duplicating its method.
- If a finding requires more than a mechanical split to fix safely (e.g. it's a real behavior
  drift, not just a location problem), say so explicitly and separate it from the pure-motion
  task list — those go to the project owner as a decision point, not into the split task list.
