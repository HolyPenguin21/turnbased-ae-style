# Economy mover materialization — decision tree (reusable analysis pattern)

Captured 2026-09-14 while debugging why AI players stopped founding bases (`Concord Base` /
`Ashen Base`, `CapabilityKind.EconomicExpansionBase`) and extraction facilities
(`CapabilityKind.EconomicInfrastructure`). Kept as a template for future "why doesn't the AI do X"
investigations that hinge on mover/army selection: draw this tree first, mark PURPOSEVERED /
NOT-COVERED nodes from the actual code before proposing a fix.

## Review round 4 (2026-09-14) — materialization moved into Execution

Rounds 2-3 kept discovering new Provisioning-side accounting bugs from the same root cause: garrison
extraction (`ArmyActions.CreateArmy`/`TransferMember`) mutated the world inside
`ProvisioningManager.ProvisionEconomy`, before all affordability checks were known to pass, and
outside the orchestrator's `beforeStep`/`afterStep` observation window entirely (that window opens
only around `TaskExecutor.ExecuteStep`, which runs strictly after Provisioning). Round 4 moved the
mutation to where it belongs, mirroring the pattern `ScoutExecutorKind.AirLaunch` already
established for "the actor does not exist yet":

- `ProvisionEconomy` no longer calls `ApplyGarrisonExtraction`. For a garrison-extraction candidate
  it now only resolves the same pure `GarrisonExtractionCandidate` (tier/hero/cost, no mutation) and
  checks a conservative pre-mutation AP estimate (`plan.ApCost + plan.Hero.ActivationApCost +
  max(BuildApCost, MinimumFollowupAp)` — no live `ArmyData` needed, `EconomyMissionClaimedAp` only
  ever sums `UnitData` stats) against the envelope. If it fits, it returns a `ProvisionedMission`
  with a **synthetic negative `MoverArmyId`**
  (`ProvisioningManager.SyntheticGarrisonExtractionActorId`) and `EconomyExtractionGarrisonArmyId`
  set — nothing has mutated, so `StateChanged` is honestly `false`.
- `TaskExecutor.ExecuteStep` detects `EconomyExtractionGarrisonArmyId >= 0` as the very first thing
  it does (before its own `Resolve(pm.MoverArmyId)` gate, which a synthetic id would otherwise fail)
  and calls the new `MaterializeEconomyGarrisonBuilder`: re-resolves the same
  `GarrisonExtractionCandidate`, applies it for real (`ApplyGarrisonExtraction` — unchanged since
  round 2, still the one owner of this mutation), then runs the shared tail extracted from the old
  `ProvisionEconomy` (`ProvisioningManager.FinishEconomyBuilder` — donor loan, lightening/
  reinforcement, the authoritative AP/resource recheck against the now-live hero, resource
  reservation) against the freshly materialized army. `pm.MoverArmyId` is mutated in place from
  synthetic to real, so everything below (the normal `Resolve`/`MissionRevalidator`/`RunEconomyStep`
  path) sees an ordinary, already-real mover from that point on.
- No rollback machinery survives this round — it is not needed. By construction the hero is only
  ever made real inside `ExecuteStep`'s own step, so a failure past that point (loan rejected, AP
  short after lightening, resources unspendable) is an ordinary "this attempt did not complete"
  outcome, exactly like a partially-moved army already was — not a mutation to undo. The hero stays
  a real field army and is picked up fresh next admission pass (now via the direct-army path, no
  extraction needed). This is what let `FailAfterHero`, `orphanedCreateApThisCall`, and
  `ProvisioningApSpent` all be deleted rather than carried forward.
- `ExecutionResult.ActorMaterialized` (new field, folded into `Outcome.StateChanged`) and
  `ExecutionResult.ActualActorArmyId` (now stamped for every Ground Economy step, not only the
  materialized case — it was silently `null` before) close the observation-window gap review round
  3 flagged: `WorldAnalysis.Observation.PublishStepObservationDelta`'s existing
  `EconomyDeliveryReady` branch already marked `ResourceSite | Actor` invalidation for "arrived,
  nothing else changed" turns; it just had no actor id to attach for Ground missions until now.

**Known, documented residual**: `ProvisioningSession.ClaimedArmyIds` (a same-pass, faster-than-
`MissionIntentRegistry` double-claim guard) is populated with the mission's `MoverArmyId` at the
moment Provisioning succeeds — for a deferred mission that is still the *synthetic* id; it is never
retroactively updated to the real one once `ExecuteStep` materializes it. Within the same
Provisioning pass, a *later* mission's eligibility check could therefore in principle still see the
freshly materialized real army as unclaimed by `ClaimedArmyIds` specifically (though
`MissionIntentRegistry`/`ActorCommitments`-based checks, which every candidate predicate also
applies, should catch it once `MissionContinuityLayer.ReconcileStep` records the new mission's
intent — the same mechanism the non-deferred path already relies on). Judged acceptable for now:
narrow window, redundant safety net degrading rather than disappearing, not a correctness
regression versus what full atomicity would have cost to guarantee. Revisit if a concrete double-
claim is ever observed in a trace.

## The question this answers

For an Economy demand (`FoundBase` / `BuildExtraction`) with a target hex, how does
`ProvisioningManager.ProvisionEconomy` (`Assets/Scripts/Ai/V2/Provisioning/ProvisioningManager.cs`)
pick and materialize the actor that will carry it out?

## The tree

```
                    Economy-демонд на целевой хекс
                    (FoundBase / BuildExtraction)
                                │
                                ▼
                 EconomyBuilderRoutes формирует кандидатов
                 (WorldAnalysis.Economy.cs:332-424)
                                │
                ┌───────────────┴────────────────┐
                │                                 │
        1.1 НЕТ готового героя            1.2 ЕСТЬ готовый герой
        (ни полевой армии с героем,       (полевая ИЛИ "запасной"
         ни запасного в гарнизоне)         в гарнизоне)
                │                                 │
                ▼                        ┌────────┴─────────┐
      rankedBuildersTotal=0              │                   │
      → NoMoverExists            1.2a герой уже        1.2b герой в
      [ПОКРЫТО: честная            ведёт полевую         гарнизоне —
       нехватка ресурса,           армию                 нужен контейнер
       демонд просто ждёт]              │                   │
                                         ▼                   ▼
                                  ResolveArmy()      ResolveGarrisonExtractionCandidate
                                  напрямую,          + ApplyGarrisonExtraction
                                  shell НЕ нужен      (ProvisioningManager.cs:267,314)
                                                             │
                                         │           ┌──────┼──────────────┐
                                         ▼            │      │              │
                                  [ПОКРЫТО]    1.3 ЕСТЬ  1.4/B ЕСТЬ    1.4/A НЕТ
                                  угроза/занятость/  пустая    занятая        ничего —
                                  путь — обычные     армия     армия на      создаём
                                  eligibility-чеки   (shell)   хексе, никем  новую
                                                      на хексе  не занятая   (CreateArmy,
                                                      гарнизона  (не shell)   2 AP из
                                                         │          │        ECO-envelope)
                                                         ▼          ▼            │
                                                  FindReusableAt EconomyHost      ▼
                                                     находит её  Candidates  CreateArmy
                                                         │       — берём      + Transfer
                                                         ▼       МЕНЬШУЮ      Member
                                                  TransferMember      │           │
                                                  герой→shell          ▼           ▼
                                                         │      TransferMember  [ПОКРЫТО,
                                                         ▼       герой→army    добавлено
                                                  [ПОКРЫТО, +   (незанятую)   2026-09-14,
                                                   фикс          │            вариант A]
                                                   2026-09-14:   ▼
                                                   откат, если  [ПОКРЫТО,
                                                   след.        добавлено
                                                   проверка в   2026-09-14,
                                                   этой же      вариант B —
                                                   попытке      лёгкость/
                                                   провалится   безопасный
                                                   — 1.3 не     маршрут уже
                                                   портится     даёт общий
                                                   в 1.4]       PlanEconomy-
                                                                ArmyLightening
                                                                ниже по коду,
                                                                без дублей]
```

## Coverage status (as of 2026-09-14, after review round 2)

| Node | Covered? | Where |
|---|---|---|
| 1.1 no hero at all | Yes — honest `NoMoverExists`, demand retries next turn | `ProvisioningManager.cs` mover-selection loop |
| 1.2a hero already leads a field army | Yes — direct `ResolveArmy`, no container needed | `IsCandidateEligible` else-branch |
| 1.3 free empty shell exists | Yes, **plus** rollback-on-later-failure | `ReusableArmySelector.FindReusableAt` + `FailAfterHero`, activation-charge AP now funded |
| 1.4/B free *populated*, hero-less, unclaimed field army at the same hex | Yes | `EconomyHostCandidates` — smallest-first, excludes hero-led armies, `ActorCommitments` AND `ProvisioningSession.ClaimedArmyIds` |
| 1.4/A no container at all — mint one | Yes | `ArmyActions.CreateArmy`, charged to `funded.Tentative.Ap` (same ECO axis ledger, never double-counted into `session.ApClaimed`), never rolled back if a later step fails (shell persists as a future 1.3 candidate) |

Known residual (documented, accepted, not fixed): if `ApplyEconomyArmyLightening` fully applies and
the AP recheck *right after it* still fails, `FailAfterHero` reverses only the hero's own transfer,
not the lightening/reinforcement batch that already applied. Judged acceptable because that batch's
own atomicity means the only way to reach that line is a fully-applied batch — there is nothing
partial to unwind — and reversing a fully-applied batch would risk a second, larger mutation on an
already-failing path for a case the report itself calls "almost unreachable."

Lightening (spec 1.1.3: unload down to fast units, only over a safe route — spec 1.1.3.1) needed
**no new code**: `PlanEconomyArmyLightening` / `ApplyEconomyArmyLightening` already run unconditionally
on whatever `hero` ends up being, later in `ProvisionEconomy` — reused as-is regardless of which tier
produced the mover.

## Review round 3 (2026-09-14) — ResourceAllocator AP ledger, orphaned-create tracking

Round 2's `ClaimedAp = realAp` fix (excluding already-spent extraction AP from the raw-pool check)
traded one bug for another: `ProvisionedMission.ClaimedAp` also feeds
`ResourceAllocator.RegisterProvisionSuccess`, and `ResourceAllocator.Pack()` prices its pool from
the FROZEN `WorldSnapshot` AP figure (`ResourceAllocator.cs` ~line 613) — it has no live view of
`root.ActionPoints`, so it never learned about AP a mid-turn `CreateArmy`/activation-charge already
spent. `ClaimedAp` was one field serving two different consumers that need two different numbers.

| # | Issue | Fix |
|---|---|---|
| P0 | `ResourceAllocator` under-reserves already-spent extraction AP against its frozen-snapshot pool, causing spurious re-pack/displacement of other missions this turn | Added `ProvisionedMission.ProvisioningApSpent` (the already-spent amount). `ClaimedAp` stays `realAp`-only for `ProvisioningSession`/raw-pool checks; all three `RegisterProvisionSuccess`/`CheckProvisionEnvelope` call sites (`AiStrategyV2Pipeline.cs` ×2, `ReactionRoundExecutor.cs`) now pass `ClaimedAp + ProvisioningApSpent` to the allocator specifically. |
| P1 | A rollback that successfully returns the hero (`rolledBack == true`) still hid AP spent by `TransferMember`'s activation charge into an already-acted container | `FailAfterHero`'s `stateChanged` now also checks `extractionExtraApSpent > 0f`, not just `extractionCreatedContainer \|\| !rolledBack`. |
| P1 | A candidate that ran `ArmyActions.CreateArmy` (AP spent) and then failed its own `TransferMember` was simply abandoned by the mover-selection loop — the spend never reached any exit path's `StateChanged`/AP accounting, including total failure (`NoMoverExists`) | `ApplyGarrisonExtraction` now reports `containerCreated` truthfully (whether `CreateArmy` actually ran, not inferred from the chosen tier). The loop accumulates every such orphaned spend (`orphanedCreateApThisCall`) across ALL attempted candidates, win or lose, folds it into `extractionExtraApSpent`, and a new `FailNoHero` wrapper routes every return in the "no hero at all" branch (including the final `NoMoverExists`) through it. |

Left open, tied to the same underlying question (**decision pending, not yet acted on**):

- **P0 — state-changing materialization (`CreateArmy`/`TransferMember`) still runs inside
  `ProvisioningManager`, before all affordability checks complete**, not as an atomic Execution
  step. The review's concrete failure scenario (hero extracted directly onto a target hex whose
  `RunEconomyStep` then sees `army.Hex.Equals(target.TargetHex)` and reports no movement) was
  checked against `TaskExecutor.RunEconomyStep` + `WorldAnalysis.Observation.PublishStepObservationDelta`:
  `ExecutionResult.EconomyDeliveryReady` already exists specifically for "reached target, nothing
  else changed" and explicitly marks `ResourceSite | Actor` invalidation on exactly that condition
  — pre-dating this patch, and not tier-specific (it fires the same way regardless of which of the
  three tiers produced the mover). So the SPECIFIC scenario described appears to already have a
  working compensating mechanism; the underlying architectural point (Provisioning mutates before
  `beforeStep`/`afterStep` diffing can see it, at all — Raid assembly and Economy lightening were
  already doing this before this patch) still stands.
- **P1 — the post-lightening AP recheck (now routed through `FailAfterHero`) still cannot undo an
  already-fully-applied lightening/reinforcement batch.** The review considers this unresolved until
  materialization moves to Execution.

Moving `ApplyGarrisonExtraction` (and the lightening/reinforcement apply step) into
`Execution/TaskExecutor` as an atomic sub-step is architecturally buildable now — the pure/impure
split from round 2 (`ResolveGarrisonExtractionCandidate` vs `ApplyGarrisonExtraction`) is exactly
the building block it would need — but is a materially larger change than anything fixed so far: it
crosses the Provisioning/Execution contract, and admission would need to price travel/build AP from
the not-yet-materialized mover's stats (the sparable unit's own `MoveMax`/`ActivationApCost`, the
same estimate `WorldAnalysis.Economy.cs` already uses at Analysis time) rather than a live
`ArmyData`. Deferred pending an explicit decision on scope/timing rather than a fourth unilateral
call in the same area.

## Review round 2 (2026-09-14) — bugs found in the first pass and how they were closed

An external review of the first patch found six real issues, all inside the same owner
(`ProvisioningManager.cs`). None required a new class or a new layer — the container search was
split into a pure `ResolveGarrisonExtractionCandidate` (decides the tier, never touches state) and
`ApplyGarrisonExtraction` (the one place that mutates), mirroring the `PlanEconomyArmyLightening` /
`ApplyEconomyArmyLightening` split already established lower in the same file.

| # | Issue | Fix |
|---|---|---|
| P0 | Rollback could return the WRONG hero: `EconomyHostCandidates` allowed a host that already had its own commander, and `ArmyData.AddMemberSorted` inserts a new hero AFTER existing ones — `hero.Members.FirstOrDefault(IsHero)` then found the old commander, not the extracted one | `EconomyHostCandidates` now excludes any army that already has a hero (`!a.Members.Any(u => u.IsHero)`) — such an army is itself a potential direct mover (1.2a), not a container. The exact `UnitData` extracted is also now tracked end-to-end (`extractionHeroUnit`) instead of re-derived by scanning members. |
| P0 | Provisioning could leave an irreversible `ArmyActions.CreateArmy` mutation (AP spent, army registered) with the result still reporting `StateChanged=false` | `FailAfterHero` and the success path now report `StateChanged`/transferred-count honestly: a Create-tier extraction always reports a real change (the shell persists even if the hero itself rolls back cleanly), and the extraction transfer itself now counts even when lightening moved nobody else. A full move of `CreateArmy` into `Execution/TaskExecutor` was considered and rejected as disproportionate — Economy (and Raid) are already the codebase's own documented exception to "pure binding" provisioning; this stays inside that existing exception instead of opening a new architectural seam. |
| P1 | A "free" host/shell could silently spend AP: `ArmyActions.TransferMember` charges `unit.ActivationApCost` when the destination already acted this turn (`ArmyData.RequiresActivationCharge`), and this was never checked or funded | `ResolveGarrisonExtractionCandidate` now reads `container.RequiresActivationCharge(hero)` (the same canonical accessor `TransferMember` itself uses — no formula duplicated) and checks the resulting cost against both the ECO envelope and the raw AP pool before accepting that candidate. |
| P1 | `EconomyHostCandidates` only checked `ActorCommitments` (durable intents), not `ProvisioningSession.ClaimedArmyIds` (armies already claimed earlier in the SAME batch pass) — could hijack a freshly-provisioned Recon/Raid mover | `EconomyHostCandidates` now takes `ProvisioningSession` and excludes `session.ClaimedArmyIds` too. |
| P1 | `ClaimedAp` included the already-spent extraction AP, which `ProvisioningSession.RegisterSuccess` adds into `session.ApClaimed` — double-subtracting AP that `root.ActionPoints` already reflects, starving later missions this same pass | `ClaimedAp` reverted to `realAp` only. The extraction AP is validated once, locally, against the envelope at the point it is spent, and never re-enters the cross-mission `session.ApClaimed` ledger. |
| P1 | The diagnostic TRACE recomputed its own copy of the container search (`cShellG`/`cHostG`/`cCreateG`) and had already drifted from the real gates (missing the activation-charge cost, the existing-hero exclusion, `ClaimedArmyIds`) | TRACE now calls `ResolveGarrisonExtractionCandidate` — the exact same pure resolver the real path uses — and prints its `Tier`/`ApCost`/`Reason` fields verbatim. No second implementation left to keep in sync. |
| extra | One `return ProvisioningResult.Fail(...)` after a successful `ApplyEconomyArmyLightening` still bypassed `FailAfterHero` | Routed through `FailAfterHero` too, with a comment noting the residual (documented, accepted) limitation: it reverses only the hero's own transfer, not a lightening/reinforcement batch that already fully applied — `ApplyEconomyArmyLightening`'s own atomicity guarantees that batch is either fully applied or not reached at all, so there is no partial roster to unwind at this point. |

## How to use this pattern next time

1. Find the single "owner" function that resolves a demand into an actor (here:
   `ProvisionEconomy` / `ResolveGarrisonExtractionCandidate` + `ApplyGarrisonExtraction`).
2. Enumerate every *mutually exclusive* precondition branch it actually checks in code — not what
   you assume it checks. Cross-reference against the log trace, not just the source, since a stale
   diagnostic can lie (see the `freeReusableShell` incident below).
3. Mark each leaf either covered (with the exact function/line) or a dead end. A dead end is not
   automatically a bug — 1.1 above is a dead end by honest design (no resource exists yet). Only
   flag a dead end as a bug candidate when the code's OWN comments concede it ("if no shell exists
   the candidate is simply not offered") — that is a designed limitation the owner may want lifted.
4. When adding a new branch, prefer reusing an existing generic downstream step (here: the lightening
   pass) over writing new logic — check whether the code after your new branch already applies
   uniformly to the resolved actor before adding anything bespoke.

## Known trap: stale diagnostic trace

The `[AI][V2][Economy][TRACE]` block in `ProvisionEconomy` used to re-derive eligibility for
logging, separately from the real gate — twice: on 2026-09-13/14 it mirrored only
`IsCandidateEligible` (hero sparability + path) and printed `ELIGIBLE=True` in exactly the turn a
real attempt was failing, because it never checked `ReusableArmySelector.FindReusableAt`; the fix
for that added a SECOND hand-written copy (`cShellG`/`cHostG`/`cCreateG`) that was itself already
missing the activation-charge AP cost and the existing-hero exclusion by the time review round 2
caught it. As of review round 2 the trace calls `ResolveGarrisonExtractionCandidate` — the exact
function the real extraction path calls — so there is exactly one implementation left, not a third
copy to eventually drift again. If you add a fourth materialization tier, add it inside that one
resolver; do not let the trace re-derive it.
