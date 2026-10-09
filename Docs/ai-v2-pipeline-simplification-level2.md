# AI V2 — упрощение пайплайна, Уровень 2: обязательная авиация в общем выборе

Ветка `refactor/ai-v2-pipeline-simplification`, база `4c99fe10`. Статус и результаты — в конце документа (заполняются по факту прогонов).

## 1. DRY/SRP-таблица до патча (baseline `4c99fe10`)

| Правило | Canonical owner | Callers / копии | Повтор / другая политика | Решение |
|---|---|---|---|---|
| Какие wings обязаны действовать | `AviationRebasePlanner.FindMandatoryContinuations` (rebase), `ReconAirExecutor.FindMandatoryRecoveryActors` (recovery); оба уже исключают stalled через `AviationObligationStallRegistry.IsStalled` (проверено по коду: `AviationRebasePlanner.cs:81-82`, `ReconAirExecutor.cs:244`) | `RunTypedAdmissions`, `AviationObligations.Pending/ActivationAp` | списки не дублируются | **retain** |
| Порядок двух видов | `MandatoryAviationOrder.RebaseFirst(int?, int?)` | `RunTypedAdmissions` (вызывает вручную, потом `rebaseContinuations[0]`) | выбор «вид + актор» собирается в Pipeline из `Count`/`[0].Id` | **move**: `MandatoryAviationOrder.Next` возвращает `(Kind, Actor)` |
| Каркас шага: Capture(before) → исполнение → `ObserveSettled` → `settledSteps++` → `CheckBoundary` → take/reenter → progress → `noProgressCycles` → лог → stall | нет единого владельца | 2 копии в `RunTypedAdmissions` (~100 строк) | идентичный каркас; различаются: исполнитель, число пар take→reenter (1 / 2), источник «действие изменило мир», тексты лога | **merge** в одну локальную функцию; различия — данные вида |
| Исполнение шага | `AviationRebasePlanner.ExecuteContinuation`, `ReconAirExecutor.RunActorStep` | те же 2 места | разные сигнатуры, разные результаты (`moved` callback / `AirReconExecutionResult.Mutated`) | **retain** исполнителей, **adapter** `MandatoryAviationStep.Execute` (callback `Action<bool>`), без policy/store |
| Различие пар take→reenter (1 у rebase, 2 у recovery) и критерий progress | — | — | поведение baseline | **retain** (унификация — Уровень 3) |
| Phase A defer / flush / force | `AviationObligations.Pending`, `DeferredStrategicAdmission` | `RunTurn` | — | **retain** без изменений |
| Финансирование | — (обязательство оплачено при sortie) | — | оба пути выполняются после `Pack`, ledger/`Settle`/observer boundary нет | **retain**: не финансировать |

SRP: `Pipeline` отвечает сейчас за выбор, исполнение и пост-обработку двух видов; после патча выбор — `MandatoryAviationOrder`, исполнение — прежние владельцы + адаптер, пост-обработка — одна локальная функция оркестрации (в замыкании `RunTurn`, т. к. использует `settledSteps`, `noProgressCycles`, `ReenterStrategicAxes`; перенос в класс — Уровень 3/4, когда появится `DecisionFrame`).

## 2. Схема до/после

До: `RunTypedAdmissions` после `Pack` содержал две самостоятельные ветки (rebase ~45 строк, recovery ~60 строк) с одинаковым каркасом.
После: `MandatoryAviationOrder.Next(rebase, recoveries)` выбирает `(Kind, Actor)` → `RunMandatoryAviationStep(kind, actor)` (одна локальная функция: Capture(before) → `MandatoryAviationStep.Execute` → `ObserveSettled` → `settledSteps++` → `CheckBoundary` → `TriggerPairs(kind)` пар take→reenter → `progress = actionChanged ∨ strategicChanged` → `noProgressCycles` → `[Loop]` → stall) → `continue`.

| Механика | Baseline | После |
|---|---|---|
| Порядок видов | `RebaseFirst(rebase[0].Id, recovery[0].Id)` | `Next` (тот же `RebaseFirst`; tie → rebase) |
| Пары take→reenter | rebase 1, recovery 2 | `TriggerPairs`: 1 / 2 (сохранено) |
| Критерий progress | `moved ∨ strategic` / `Mutated ∨ strategic` | `actionChanged ∨ strategic`, `actionChanged` = `moved` / `Mutated` |
| Stall | `MarkStalled` + текст по виду | то же, текст — `StallMessage` (строки идентичны) |
| `[Loop]`-строка | `aviation-rebase actor=#` / `recovery actor=#` | `{Label} actor=#` — те же строки |
| Финансирование, ledger, `Settle`, observer boundary | нет | нет |
| Phase A defer / flush / force-flush | `AviationObligations.Pending`, `DeferredStrategicAdmission` | не тронуто |
| Air Scout, Explore, Recall, `GroundCombatLegStep` | отдельные маршруты | не тронуты |

Банковский проход: mandatory aviation ничего не резервирует и не списывает через банк ни до, ни после; порядок `Pack` → mandatory → `continue` сохранён, `ReconcileEconomyCompletionReservations`/`ReleaseDeferredEconomyIncomeCover`/`Settle` не затронуты. Кеш-проход: действие → `ObserveSettled` (Refresh → Capture(after) → Publish) → `TakeTypedTriggers` → `ReenterStrategicAxes` → кадр обновляется на следующей итерации (`!ownershipFreshAfterPhaseA`) — порядок идентичен §12.2 Уровня 0.

Единственный маршрут: `ExecuteContinuation` из mandatory-цикла вызывается только в `MandatoryAviationStep` (прочие вызовы — `GroundCombatLegStep` и recall, это другие work item); `RunActorStep` из цикла — только там же; `MarkStalled` в Pipeline — один вызов.

## 3. DRY/SRP после

| Правило | Owner после | Копий |
|---|---|---|
| Выбор следующего mandatory item | `MandatoryAviationOrder.Next` (чистая, тесты) | 1 |
| Исполнение | прежние `AviationRebasePlanner` / `ReconAirExecutor`; адаптер `MandatoryAviationStep` без policy/state | 1 |
| Пост-шаговый протокол | `RunMandatoryAviationStep` (замыкание `RunTurn`) | 1 (было 2) |
| Per-kind факты (пары, тексты) | `MandatoryAviationOrder` | 1 |

Остаётся: пост-шаг всё ещё в замыкании `RunTurn` (использует `settledSteps`, `noProgressCycles`, `ReenterStrategicAxes`); вынос в класс — Уровни 3–4 вместе с `DecisionFrame`. Класс-разделения (не partial) на Уровне 2 не требовались: затронуты только маленькие классы; `AiStrategyV2Pipeline` уменьшился (см. diff).

## 4. Команды и результаты

| Проверка | Результат |
|---|---|
| `run.sh l2-a` (сборка net472 + NUnitLite) | build errors 0; всего 2068, passed 1395, failed 673 (до патча Unity-null) |
| `patchrun.sh l2-a l2-a-p` | всего 2068, **passed 1596**, failed 472 (engine-bound, как в `l1-b-p`) |
| `regress.py l1-b-p l2-a-p` | **регрессий 0**, новых проходящих 6 (`AiMandatoryAviationOrderTests`) |
| `regress.py l0-base-p l2-a-p` | регрессий 0, новых проходящих 25 |
| `compile_check.sh` / Unity compile / EditMode в Unity | **not run** |
| Нативный прогон нового кода | **not run** |
| `loopsig.py` на новом нативном логе | **not run** (старые логи Уровня 0/1 — см. Уровень 1 §10 — код Уровня 2 не содержат) |

## 5. Проверка Уровня 2 снизу вверх (ТЗ §8)

| Пункт | Статус | Основание |
|---|---|---|
| Baseline-арбитраж rebase/recovery зафиксирован | выполнено | §1, тесты `Next`/`RebaseFirst` (tie, Id, null, head only) |
| Reuse routing, без нового `TaskExecutor`/store | выполнено | `MandatoryAviationStep` — статический адаптер без состояния |
| Ordering-тесты: два вида, last obligation, stalled, air Scout и обычная задача | **частично** (не покрыты: last obligation без dirty trigger, air Scout, обычная задача — всё внутри `RunTurn`) | два вида, tie, пустые, головы списков — тесты есть; stalled-фильтр проверен по коду (`AviationRebasePlanner.cs:81`, `ReconAirExecutor.cs:244`) и существующим `AiReconAuditBugTests`, но цепочка «no-progress → MarkStalled → continue → обычная задача» внутри `RunTurn` управляемым тестом не покрыта (корутина зависит от движка) |
| Общий select + протокол Уровня 1 | **частично** | оба вида авиации выбираются одним `Next` и идут одним протоколом (`ObserveSettled`); выбор «авиация против обычной миссии» остаётся двумя последовательными ветками цикла (`Next != None → continue`), единый выбор operational work item — Уровень 4 (`TurnWork`) |
| Без финансирования оплаченного | выполнено | `Pack` и порядок не менялись; ledger/`Settle` не добавлены |
| Delayed Phase A flush, force-flush | выполнено | код не тронут (`Pending`, `ReenterStrategicAxes(flush/force)`) |
| Продолжение обычных задач после no-progress | выполнено по коду | `MarkStalled` + `continue`, `FindMandatory*` исключают stalled |
| Удаление старых post-step веток, один маршрут на item | выполнено | §2 |
| Сохранены различия пар 1/2 и критерия progress | выполнено | `TriggerPairs`, тест |
| Нативное подтверждение | **не выполнено** | детерминированной партии нет; rebase ни в одном нативном логе не наблюдался (0 в обоих логах), recovery — только в логе Уровня 0 (5 шагов) |

**Статус Уровня 2: реализован и проверен доступными средствами (managed-прогон без регрессий); native не доказан, rebase-путь нативного покрытия не имеет.** Просьба к владельцу (не блокирует): нативный лог со сценарием перебазирования wing, а затем `python D:/aiv-work/loopsig.py <лог>` — ожидается `violations=0`, без `ERROR`, строки `aviation-rebase`/`recovery` прежнего вида.

Масштаб: изменение оркестрации (локальное по поведению — порядок и тексты сохранены).
