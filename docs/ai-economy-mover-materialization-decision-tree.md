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
                                  ResolveArmy()      TryExtractGarrisonHeroForEconomy
                                  напрямую,          (ProvisioningManager.cs:252)
                                  shell НЕ нужен            │
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

## Coverage status (as of 2026-09-14, after the Variant A/B patch)

| Node | Covered? | Where |
|---|---|---|
| 1.1 no hero at all | Yes — honest `NoMoverExists`, demand retries next turn | `ProvisioningManager.cs` mover-selection loop |
| 1.2a hero already leads a field army | Yes — direct `ResolveArmy`, no container needed | `IsCandidateEligible` else-branch |
| 1.3 free empty shell exists | Yes, **plus** rollback-on-later-failure (2026-09-14 bug fix) | `ReusableArmySelector.FindReusableAt` + `FailAfterHero` |
| 1.4/B free *populated* non-shell army at the same hex, unclaimed | Yes (added 2026-09-14) | `EconomyHostCandidates` — smallest-first, `!commitments.IsArmyClaimed` |
| 1.4/A no container at all — mint one | Yes (added 2026-09-14) | `ArmyActions.CreateArmy`, charged to `funded.Tentative.Ap` (same ECO axis ledger), never rolled back if a later step fails (shell persists as a future 1.3 candidate) |

Lightening (spec 1.1.3: unload down to fast units, only over a safe route — spec 1.1.3.1) needed
**no new code**: `PlanEconomyArmyLightening` / `ApplyEconomyArmyLightening` already run unconditionally
on whatever `hero` ends up being, later in `ProvisionEconomy` — reused as-is regardless of which tier
produced the mover.

## How to use this pattern next time

1. Find the single "owner" function that resolves a demand into an actor (here:
   `ProvisionEconomy` / `TryExtractGarrisonHeroForEconomy`).
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

The `[AI][V2][Economy][TRACE]` block in `ProvisionEconomy` re-derives eligibility for logging,
separately from the real gate. On 2026-09-13/14 it mirrored only `IsCandidateEligible` (hero
sparability + path) and printed `ELIGIBLE=True` in exactly the turn a real attempt was failing,
because it never checked `ReusableArmySelector.FindReusableAt` — the real, additional gate
`TryExtractGarrisonHeroForEconomy` applies. Every time a new materialization tier is added to the
real function, the trace must be updated in the same commit, or it will mislead the next debugging
session the same way.
