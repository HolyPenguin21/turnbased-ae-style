# Economy mover materialization — decision tree (reusable analysis pattern)

Captured 2026-09-14 while debugging why AI players stopped founding bases (`Concord Base` /
`Ashen Base`, `CapabilityKind.EconomicExpansionBase`) and extraction facilities
(`CapabilityKind.EconomicInfrastructure`). Kept as a template for future "why doesn't the AI do X"
investigations that hinge on mover/army selection: draw this tree first, mark PURPOSEVERED /
NOT-COVERED nodes from the actual code before proposing a fix.

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
