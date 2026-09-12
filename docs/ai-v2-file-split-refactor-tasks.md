# AI Strategy V2 — file-split refactor task set

Purely mechanical readability refactor: split the heaviest files in `Assets/Scripts/Ai/V2/`
along their existing internal boundaries. **No behavior change.** Motivation: several of the
Economy/Recon bugs fixed in the 2026-09-12 session were hard to see precisely because the owning
method sat inside a 1500-1900 line file — this does not fix bugs, it makes the next one faster
to find.

Recall phrase: "продолжаем file-split рефакторинг V2 — см. Docs/ai-v2-file-split-refactor-tasks.md"

## Ground rules (apply to every task below)

- **Method bodies do not change.** Only *where* a method physically lives changes.
- `namespace` and access modifiers (`public`/`internal`/`private`) stay exactly as they are.
- Add `partial` only where a task explicitly calls for it — do not partial-ize a class that is
  being split into independent standalone types instead (see MissionIntent.cs below).
- Every new file needs a Unity `.meta` — let Unity generate it on reimport, then include it in
  the commit (do not hand-write GUIDs).
- One task = one commit. Do not mix two files' splits in one commit, and do not mix this
  refactor with any logic change.
- After each task: `dotnet build Assembly-CSharp.csproj` must stay 0 warnings / 0 errors.
- Do not play-test-gate this on Unity reimport succeeding — that still needs the user, same as
  every other V2 change — but the dotnet build must be clean before handing it back.
- Verification is **structural**, not a log diff: confirm the same methods exist, same
  signatures, same call sites resolve — do not attempt to byte-compare `Logs/AiDebug.log`
  before/after (CallerFilePath/line numbers embedded in log lines will differ even with zero
  behavior change; that is expected and not a regression).

## Progress tracker

| # | Task | Status |
|---|---|---|
| 1 | Extend `AiV2Trace` correlation format into downstream logs | Done |
| 2 | Split `AiConfigV2.cs` into `partial` sections (pilot) | Done |
| 3 | Split `MissionIntent.cs` into its 15 existing types | Done |
| 4 | Split `DemandLayer.cs` into `partial` per axis | Done |
| 5 | Split `WorldAnalysis.cs` into `partial` per snapshot family | Pending |

Do them in this order — each is independent of the others, but this order goes
lowest-risk/highest-value first and saves the most sensitive file (WorldAnalysis) for last,
once the mechanics of the previous four have been proven safe.

---

## Task 1 — Extend causal trace correlation into downstream logs

**Not a file split.** Small, targeted addition to the diagnostics layer.

The trace pieces already exist and should NOT be duplicated or replaced:
- `AxisDemand.TraceId`
- `MissionProposal.AttemptId`
- `MissionProposal.CauseDemandTraceIds`
- `AiV2Trace.CorrelateDemandsToMissions` — the one place that computes
  `CauseDemandTraceIds` for a mission. **Keep this the single owner.**

Known small inconsistency to clean up first: `EconomyMissionPlanner` currently also writes into
`CauseDemandTraceIds` before `AiV2Trace.CorrelateDemandsToMissions` recomputes it. Today the two
happen to agree, but there should be exactly one writer. Remove `EconomyMissionPlanner`'s direct
write; let `CorrelateDemandsToMissions` be the sole writer, called after mission proposals are
built (verify the call site ordering still runs after `EconomyMissionPlanner.Propose`).

Add `AiV2Trace.FormatCorrelation(MissionProposal mission)` returning something like:

```
attempt=T7-P1-M-M03 causeDemand=[T7-P1-M-D02]
```

(`causeDemand` is plural/bracketed because `CauseDemandTraceIds` is a set — one mission can be
caused by several demands, and one demand can spawn several attempts. Keep it a set; do not
collapse it to a single id.)

Use this format string in the **existing** log lines (do not add new log lines) in:
- `Allocation/ResourceAllocator.cs`
- provisioning-related logs in `Orchestration/AiStrategyV2Pipeline.cs`
- `Execution/TaskExecutor.cs`
- `Continuity/MissionContinuityLayer.cs` (inside `MissionIntent.cs` today — see Task 3, this call
  site moves with it)

Acceptance: grep on either a `TraceId` or an `AttemptId` should now let you follow one strategic
decision from `Demand` creation through `Mission` → `Allocation` → `Provisioning` → `Execution` →
`Outcome` without cross-referencing anything by hand.

---

## Task 2 — Split `AiConfigV2.cs` (pilot mechanical split)

Current: `Assets/Scripts/Ai/V2/Foundation/AiConfigV2.cs`, 1270 lines, almost entirely `const
float`/`const int` grouped under existing `// SECTION NAME` comment banners. Exactly one mutable
field: `frameLogEnabled`.

Target:

```
Foundation/
├── AiConfigV2.cs                  (keep: class declaration, frameLogEnabled, any shared statics)
├── AiConfigV2.Diagnostics.cs
├── AiConfigV2.Recon.cs
├── AiConfigV2.Economy.cs
├── AiConfigV2.Development.cs
├── AiConfigV2.Allocation.cs
├── AiConfigV2.Materialization.cs
├── AiConfigV2.Continuity.cs
└── AiConfigV2.Combat.cs
```

Use the existing `// SECTION` banners in the file as the authoritative boundary — do not
re-judge which constant belongs where; if a section banner doesn't map cleanly to one of the
file names above, add the file it actually maps to rather than forcing a fit (this list is a
starting point, not a hard contract).

`frameLogEnabled` (the one non-const field) stays in `AiConfigV2.Diagnostics.cs`.

This is the safest of the five tasks — pure data, no method bodies, near-zero risk of a
static-initialization-order surprise since almost everything is a `const` (consts have no
initialization order at all; only watch for any field whose initializer references another
field in a different partial file).

---

## Task 3 — Split `MissionIntent.cs`

Current: `Assets/Scripts/Ai/V2/Continuity/MissionIntent.cs`, 1784 lines. **This is not one
class** — it already contains ~15 independent types bundled into one file:
- Model types: `MissionIntent`, `ScoutIntent`, `RaidIntent`, `EconomyIntent`
- `MissionIntentState`, `MissionIntentRegistry`
- `MissionTurnOutcome`
- `MissionOutcomeLedger`
- `MissionContinuityLayer`

Because these are independent types (not one type split across files), **do not** make this a
`partial class MissionIntent.cs`-family split. Give each real type/type-group its own file:

```
Continuity/
├── MissionIntent.Models.cs        (MissionIntent, ScoutIntent, RaidIntent, EconomyIntent)
├── MissionIntentState.cs          (MissionIntentState, MissionIntentRegistry)
├── MissionOutcomeLedger.cs        (MissionTurnOutcome, MissionOutcomeLedger)
└── MissionContinuityLayer.cs
```

If, after this split, `MissionContinuityLayer.cs` alone is still too large to navigate
comfortably, that is a **separate follow-up decision** — split it as `partial` by responsibility
(e.g. Recon / Economy / Reconcile) only then, as its own mechanical step. Do not do that in this
task; get the type-level split landed and verified first.

This is the task most likely to touch call sites outside the file (everything that references
`MissionIntent`, `EconomyIntent`, etc. by type name is unaffected — C# doesn't care which file a
type lives in — but if anything referenced these via a shared partial-class assumption, check
for it). Should be safe since these are genuinely separate types today, just co-located.

---

## Task 4 — Split `DemandLayer.cs`

Current: `Assets/Scripts/Ai/V2/Strategy/Demand/DemandLayer.cs`, 1895 lines — the heaviest file
in the codebase and the one most recently the site of real bugs (2026-09-12 session: cross-family
Extraction/Base conflict, fresh-vs-fresh dedup, extraction-loss penalty, EconomyHeroPrerequisite
reservation gap). This one **is** one class (`static class DemandLayer`) with clearly separable
static methods, so `partial` is correct here.

```
Strategy/Demand/
├── DemandLayer.cs                 (Generate — the single assembly point; keep this the ONLY
│                                    place that calls the per-axis methods below)
├── DemandLayer.Readiness.cs       (BaselineForceReadinessDemands)
├── DemandLayer.Recon.cs           (ReconDemands)
├── DemandLayer.Aggression.cs      (AggressionDemands)
├── DemandLayer.Defence.cs         (DefenceDemands)
├── DemandLayer.Economy.cs         (EconomyDemands, AddBaseCandidates, EconomyHeroPrerequisite,
│                                    HasActiveEconomyBuildIntent, IsActiveBaseCommitment,
│                                    HasActiveEconomyIntentAtHexOfKind, EconomyBuilderChoice,
│                                    SelectEconomyBuilder, and every other Economy-only private
│                                    helper — all of it, not just the top-level method)
└── DemandLayer.Development.cs     (DevelopmentDemands and its private helpers)
```

Naming rationale (explicit, don't drift from it): `DemandLayer.Economy.cs`, not
`EconomyDemandLayer.cs` — the file name must make it visually obvious that no new "Economy demand
owner" type was introduced. It is still exactly the same `DemandLayer` class; only its Economy
slice moved to its own file.

`AddBaseCandidates` belongs in `DemandLayer.Economy.cs`, not a shared/common file — it is not a
general-purpose candidate builder, it is specifically part of how the Economy axis forms Base
demands, and every current caller of it is inside `EconomyDemands`.

Architecture level does not change: everything stays `Strategy/Demand`. Aggression and Defence
move file only — their inclusion/scope logic (`AiStrategyV2Scope`, etc.) is untouched.

---

## Task 5 — Split `WorldAnalysis.cs` (do this last)

Current: `Assets/Scripts/Ai/V2/Analysis/WorldAnalysis.cs`, 1893 lines, ~58 methods. Also `static
partial class` split (one class, separable static methods) — but this is the most sensitive file
in the codebase: it is the **single** builder of `WorldSnapshot`, feeding Self / Known / TrueWorld
/ MapKnowledge / Economy / Development / Threat, and several methods cross-reference each other's
outputs within the same `Scan` pass.

```
Analysis/
├── WorldAnalysis.cs               (Scan, RefreshStrategicKnowledge, RefreshOperationalState —
│                                    the entry points and whatever genuinely orchestrates all
│                                    the builders below)
├── WorldAnalysis.Observation.cs   (step stamps, invalidation detection)
├── WorldAnalysis.Self.cs          (BuildSelf, army/ability projection)
├── WorldAnalysis.Knowledge.cs     (Known / TrueWorld / MapKnowledge builders)
├── WorldAnalysis.Economy.cs       (BuildEconomy and its Economy-only helpers — note some of
│                                    these, e.g. EconomyKnownHexYield, are ALSO called from
│                                    non-Economy builders; check every call site before moving)
├── WorldAnalysis.Development.cs   (BuildDevelopment)
└── WorldAnalysis.Threat.cs        (threat/contact/asset builders)
```

Before moving a single method: build a table of **method → which snapshot family it populates
→ which OTHER methods call it**. Several helpers (e.g. resource-cluster/yield helpers) are
shared across Economy and Base-opportunity building — those either go in whichever family calls
them most, with the other call sites updated to reference the cross-file static method (fine,
C# doesn't care), or — if genuinely shared by 3+ families — get their own
`WorldAnalysis.Shared.cs`. Decide this from the actual call table, not by re-guessing from
memory; do this table-building as the first step of the task, before moving any code.

This is why it's last: the earlier four tasks (Config, MissionIntent, DemandLayer) will have
already proven out the partial-split mechanics and the .meta/commit workflow safely, on lower
blast-radius files, before touching the one file every V2 pass depends on for its worldview.

---

## Acceptance criteria (every task)

- Method bodies unchanged (diff should be pure move — new file gets the method verbatim, old
  file loses it, no logic edits mixed in).
- `namespace` and access modifiers unchanged.
- `partial` added only where specified (Tasks 2, 4, 5 — not Task 3).
- New Unity `.meta` files included in the commit.
- `dotnet build Assembly-CSharp.csproj` — 0 warnings, 0 errors.
- Decision behavior identical before/after: same demand set, same mission set, same allocation,
  same outcome for a given world state — verify by reasoning about the diff (pure code motion),
  not by comparing `Logs/AiDebug.log` byte-for-byte (CallerFilePath/line numbers embedded in log
  text will legitimately change).
