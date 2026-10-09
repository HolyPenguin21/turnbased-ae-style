# Упрощение пайплайна AI V2 — Уровень 1 (общий протокол завершения шага)

Статус: **реализован — проверен доступными средствами; native не выполнен.**
База: ветка `refactor/ai-v2-pipeline-simplification` @ `81dd6c29` (Уровень 0). Результат — коммиты `316db942` (код) и `4c99fe10` (отчёт).
Среда: Unity 6000.5.4f1; прогон — net472 + mono Unity (`D:/aiv-work/run.sh`, `patchrun.sh`). Unity-редактор и Linux `compile_check.sh` не запускались.

## 1. Что изменено

| Файл | Изменение |
|---|---|
| `Analysis/WorldAnalysis.Observation.cs` | **+`ObserveSettled(snapshot, player, root, hand, ctx, before, execution)`**: `RefreshStrategicKnowledge` → `CaptureStepObservation(after)` → `PublishStepObservationDelta`; возвращает свежий snapshot |
| `State/AiTurnSession.cs` | **+`SettleStep(outcomes, attempted, snapshot, objectives)`** — доменное завершение попыток цикла; из класса вынесены `IntentTrace`, `LogTransition`, `CollectViolations` (355 → 254 строк) |
| `State/LifecycleAudit.cs` (новый, 132) | Только чтение: строка перехода lifecycle и проверки инвариантов конца хода. Ничего не меняет |
| `Strategy/Demand/EconomyReservationLifecycle.cs` (новый, 349) | Перенос 10 методов записи/перехода/снятия резервов Economy из `InfrastructureFulfillment` (1219 → 893). Тело методов **идентично** (проверено автоматически, различие только в квалификаторе `InfrastructureFulfillment.`) |
| `Orchestration/AiStrategyV2Pipeline.cs` | Шесть мест Capture/Refresh/Capture/Publish → `ObserveSettled`; два цикла `FinalizeSteps → Settle` → `SettleStep` (1568 → 1505 строк) |
| 11 файлов | Только смена квалификатора на `EconomyReservationLifecycle.` (5 test-файлов, `StrategicPhaseA`, `CapabilityDeliveryEvaluator`, `ProvisioningManager.Economy`, `TaskExecutor` (комментарий), `Pipeline`) |

Объём по ТЗ: пути «обычная задача → rebase → recovery» подключены к общему протоколу наблюдения; дополнительно подключены management, cold и recall — они имели побайтово ту же последовательность (Refresh → Capture → Publish с `execution = null`). Подключение шло одним патчем с одним regression-gate, а не «по одному» с промежуточными прогонами (отступление от ТЗ §7; компенсируется тем, что замена механическая, а прогон — полный).

## 2. Схема до/после (последействия шага)

До (обычный шаг, rebase, recovery — каждый свой текст):
`Capture(before) → execute → Refresh → Capture(after) → Publish` (×6 копий) и `FinalizeSteps → Where(attempted) → Settle` (×2 копии).

После: `Capture(before) → execute → ObserveSettled(...)` и `turnSession.SettleStep(...)`.

Удалённые узлы/переходы: 6 копий пятишагового блока наблюдения, 2 копии цикла settle. Остаётся 8 мест завершения шага (метрика ТЗ §2 п.3 не изменилась по числу **мест**, изменилась по числу **независимых алгоритмов**: наблюдение теперь одно).

**Сохраняющиеся различия (явно, не склеены):**

| Часть последействия | Обычная задача | Rebase | Recovery | Почему не объединено |
|---|---|---|---|---|
| ledger `RecordExecution` / `RefreshObjectiveStatesLive` / `Settle` | есть | нет | нет | у обязательства авиации нет funded mission / durable intent (ТЗ Ур. 1: не создавать фиктивный intent) |
| `ReconcileEconomyCompletionReservations` | есть | нет | нет | нет Economy-owner’а |
| `WaitAtObserverActionBoundary` после шага | есть | нет | нет | пауза наблюдателя на baseline есть только у обычного шага; добавление — изменение поведения |
| trigger fan-out | take→reenter→take→reenter | take→reenter (1 пара) | 2 пары | разное число пар — различие поведения; решает Уровень 3 |
| критерий progress | `er.Outcome.StateChanged` ∨ strategicChanged | moved ∨ strategicChanged | `Mutated` ∨ strategicChanged | домен-специфично |
| no-progress | `break` по `noProgress` | `MarkStalled` + `continue` | `MarkStalled` + `continue` | сохраняют блокировку wing точечно |

## 3. Таблица связанных механик (baseline-порядок → после)

| Механика / сценарий | Baseline-порядок | После | Статус доказательства |
|---|---|---|---|
| Бой/событие завершён до следующего решения | execute (корутина) → Refresh → Publish | тот же вызов в `ObserveSettled` | порядок идентичен по построению; нативный лог: 176 шагов task сохраняют структуру — **не сравнено** (нужен повторный прогон) |
| Rebase/recovery с провалом progress не блокирует миссии | `MarkStalled`, `continue` | не тронуто | native baseline: 5 recovery-шагов; **rebase на нативе не наблюдался** |
| Committed launch, wing потеряна, receipt сохраняется | `RecordExecutionMutation` внутри executor’а | не тронуто | `AiWorldDeltaTests`, `AiLifecycleIngressTests` без регрессий |
| Completed side leg не завершает операцию | `Settle` → Continuity → `Retire` после политики | `SettleStep` вызывает тот же `Settle` | `AiMissionLeaseLifecycleTests`, `AiDomainTransitionParityTests` — без регрессий |
| Observer видит согласованное состояние | Refresh → ledger → Reconcile → CheckBoundary → ObserverBoundary | порядок сохранён (ObserveSettled заменил только блок Refresh/Capture/Publish) | по построению |
| Отсутствующий result / no-op | `settled == null` → `Publish(…, null)` | то же | `SettleStepWithNoAttemptsSettlesNothing`, `…SkipsNullRows` |

## 4. Банк (отдельный проход)

Изменения Уровня 1 не добавляют и не удаляют ни одного writer’а, не меняют порядок вызовов и области жизни.

- **Writers:** 10 методов `EconomyReservationLifecycle` — те же тела; ссылки обновлены (все call-sites: `StrategicPhaseA` ×8 (включая комментарии), `CapabilityDeliveryEvaluator` ×1, `ProvisioningManager.Economy` ×3, `Pipeline` ×5 (3 вызова Reconcile/Release + комментарии), `EconomyReservationLifecycle` внутри). Единственное хранилище строк — `StrategicResourceReservationLedger` через `MissionLeaseBook` (не менялось).
- **Момент освобождения owner:** `ReleaseByOwner` в `TryFulfill` (при `Built`), `TaskExecutor.ReleaseEconomyReservation`, `Continuity` — не тронуты. `ReconcileEconomyCompletionReservations` — по-прежнему после `Settle` в обычном шаге и в двух блоках после loop/в management (3 вызова в Pipeline).
- **Cкрытый риск, который проверен:** `ReserveDeferredEconomyResourcesForPendingHero` использует `EconomyHeroPrerequisiteIdentity`, оставшуюся в `InfrastructureFulfillment`; вызов квалифицирован, поведение не изменилось (сборка + 1590 тестов без регрессий, включая `AiEconomyReservationLifecycleTests`, `AiEconomyOwnershipTests`, `AiEconomyDecisionTests`, `AiMissionLeaseLifecycleTests`).
- Aviation pre-paid cost не оплачивается повторно: rebase/recovery не получили ledger/Settle (см. §2).
- Нативная таблица stock/ledger «до/после» по ходам **не снималась** (требуется нативный повторный прогон с трассой резервов).

## 5. Кеши (чтение/запись)

| Mutation | Canonical writer | Receipt | Dirty facts | Refresh | Reader / ключ | До → после |
|---|---|---|---|---|---|---|
| Любой исполненный шаг (task, rebase, recovery, Phase B round, cold, recall) | gameplay-примитив + `WorldDeltaLifecycle` | `StateVersionAfter` | по diff snapshot в `PublishStepObservationDelta` | `RefreshStrategicKnowledge` | snapshot (identity + `KnowledgeVersion`), route key, estimate key | **не изменено**: тот же Refresh, тот же Capture(after) после него, тот же Publish с тем же `execution` |
| Conditional re-admission refresh | `ReenterStrategicAxes` | — | — | Refresh только `if (followup.StateChanged)`, потом Warm+Frame, потом Capture(after) | demands / fingerprint | **осознанно не переведён на `ObserveSettled`**: порядок Refresh → Warm → Frame → Capture(after) отличается |

- Одна публикация каждого факта: `Publish` вызывается ровно один раз на шаг внутри `ObserveSettled`; replay отсутствует (receipt не публикуется повторно).
- Полный refresh не заменён селективным (ТЗ §3.3). `WarmEstimates` не затронут.
- Для `SettleStep` и `LifecycleAudit` новых кешей нет; `LifecycleAudit` — read-only и не пишет ни в один реестр, кроме лога (`AiDebugLog.WriteDeduped`, как раньше).

## 6. DRY/SRP

| Правило | Canonical owner | Решение | Доказательство |
|---|---|---|---|
| Наблюдение после шага | `WorldAnalysis.ObserveSettled` | merge 6 копий → 1 | grep: `Capture…Publish` в Pipeline остался только в re-admission (§5) |
| Settle попыток цикла | `AiTurnSession.SettleStep` | merge 2 копии → 1 | grep: `turnSession.Settle(` в Pipeline = 0 |
| Запись/переходы резервов Economy | `EconomyReservationLifecycle` | **SRP-разделение**: demand-сторона (identity, spend authority, build transaction) осталась в `InfrastructureFulfillment` | побайтовое сравнение тел |
| Диагностика lifecycle | `LifecycleAudit` | **SRP-разделение** сессии | `AiTurnSession` больше не форматирует лог и не считает инварианты |

Общий протокол **не** содержит: Economy repayment, Attack retirement, AA-политику, scorer, формулы расхода — они остались у доменов. Нового хранилища, кеша, scorer’а или второго scheduler’а нет.

Остаточное замечание SRP (не закрыто, вне Уровня 1): `AiTurnSession.Settle` по-прежнему проецирует claims (`ReplaceOperationActors`); это ответственность session, но кандидат на отдельный проход при Уровне 4.

## 7. Команды и результаты

| Проверка | Результат |
|---|---|
| Сборка net472 (`run.sh l1-b`) | 0 ошибок |
| Все тесты без патчей | 2062 всего, 1389 прошло, 673 упало (прежние engine-bound: 673 на baseline) |
| Патченный прогон (`D:/aiv-work/l1-b-p`) | **2062 всего, 1590 прошло, 472 упало** |
| Сравнение с baseline Уровня 0 (`l0-base-p`, 1571) | регрессий **0**, новых прошедших 19 |
| Сравнение с чекпойнтом Уровня 0 (`l0-cur2-p`, 1585) | регрессий **0**, новых прошедших 5 (`AiStepCompletionProtocolTests`) |
| Unity compile / EditMode / PlayMode | **not run** |
| Нативный повторный прогон партии | **not run** — сравнение: `python D:/aiv-work/loopsig.py <baseline.log> <new.log>` (структурная сводка `[Loop]`); эталон — `D:/aiv-work/native-baseline/AiDebug.master-fe2ccdf4-plus-L0.log` |

Новые тесты Уровня 1 (`AiStepCompletionProtocolTests`, 5): `SettleStep` settles только attempted; порядок ledger и пропуск `null`; пустой набор попыток; `AuditTurnEnd` сообщает о claim-owner без intent; чистая сессия без нарушений. `ObserveSettled` в managed-харнессе не тестируется (`RefreshStrategicKnowledge` → `Scan` требует движок) — покрытие только нативное.

## 8. Проверка Уровня 1 снизу вверх (по пунктам ТЗ §7 и gate §5.4)

| Пункт ТЗ | Статус | Основание / оговорка |
|---|---|---|
| Сопоставить последовательности before-observation → … → trigger fan-out | выполнено | §2 |
| Явные различия (aviation без intent; task с allocation/provisioning; Phase A/B batch) | выполнено | §2, таблица различий |
| Characterization-тесты общих и различающихся post-step эффектов, выполнены на baseline | **частично** | `SettleStep`/`LifecycleAudit` покрыты; различия «нет ledger у rebase/recovery» закреплены только документом и кодом (нет managed-теста на `RunTurn`); тесты нового кода на baseline не собираются |
| Единый протокол только для общих эффектов в существующем owning layer | выполнено | `ObserveSettled` (Analysis), `SettleStep` (State); без новых типов-результатов |
| Подключать по одному с узким regression gate | **отступление** | подключено одним патчем (механическая замена), полный gate после |
| Удалить заменённые копии, не оставить два post-step owners | выполнено | grep §6; re-admission оставлен намеренно (§5) |
| Ожидаемый результат: «меньше самостоятельных последовательностей» | **частично** | независимых алгоритмов наблюдения 7 → 2 (ObserveSettled + re-admission), settle 2 → 1; мест завершения по-прежнему 8, trigger/boundary/progress различаются (§2) — это работа Уровней 2–3 |
| Банк: те же debits/stages/release | выполнено (по построению + тесты) | §4; нативной трассы нет |
| Кеши: одна publication, свежий observation, warm-up сохранён | выполнено (по построению) | §5 |
| Нет compile errors, потери baseline-passing tests | выполнено | §7 |
| Native passed/failed/not run | **not run** | см. §7 |

Вывод проверки: **цель Уровня 1 достигнута в части дублирования** (наблюдение и settle — по одному владельцу) **и SRP** (резервы Economy и lifecycle-диагностика вынесены в собственные классы), но **не достигнута в части «единого post-step протокола»** целиком: ledger/Reconcile/observer-boundary/trigger fan-out остаются у обычного шага и частично у rebase/recovery — это сознательное сохранение поведения до Уровней 2–3. Полный gameplay parity не заявляется до нативного сравнения.

## 9. Оставшиеся ограничения и зависимость следующего уровня

1. Нативный повторный прогон партии и сравнение `loopsig.py` с эталоном (ожидание: то же число `begin`, виды остановок, `violations=0`, структура `[Loop]` по ходу).
2. Rebase не наблюдался ни в одном нативном логе → до Уровня 2 нужен сценарий с перебазированием авиации.
3. Уровень 2 опирается на: `ObserveSettled` (общее наблюдение), `MandatoryAviationOrder` (Уровень 0), сохранённое различие «нет ledger у обязательств».
4. Уровень 3 заберёт: trigger fan-out (разное число пар), fingerprint Economy из `RunTurn`, `DecisionFrame`.

## 10. Нативный прогон на коде Уровня 1 (2026-10-09 14:27–14:31)

Файл: `D:/aiv-work/native-baseline/AiDebug.level1.log` (22 хода: Halden и Rurik, ходы 1–11). Код — ветка на коммите `316db942`.

**Сравнение с эталоном Уровня 0 неэквивалентно:** это другая партия (в эталоне Korrin/Grimm), seed не фиксировался, поэтому счётчики `begin`, `step:task` и т. п. не сравнимы и **не доказывают** паритет. Проверялось то, что от партии не зависит:

| Критерий | Результат |
|---|---|
| `[Invariant]`: нарушения | 22 хода аудированы, `violations=0`; `ERROR` — 0 |
| Исключения / NullReference в логе | 0 |
| Строки `[Lifecycle]`, `[Loop]` формата прежние | да (`loopsig.py` распознал все виды) |
| Новые на нативе пути, которых не было в эталоне | `stop — settled task produced no typed invalidation` (7), `bounded stop — no progress cycles 2` (3) — оба сработали штатно, без инвариантных нарушений |
| rebase | 0 (снова не наблюдался); recovery 0 в этой партии |

Наблюдение (не изменялось, для Уровня 4): в ходе 3 один и тот же Economy `BuildExtraction` исполняется дважды подряд с `progress=0 moved=0 ap=0 stop=StepCompleted`, пока `noProgress` не достигает 2 (bounded stop). Без baseline той же партии нельзя сказать, было ли так до рефакторинга; Уровень 1 порядок этих вызовов не менял.

**Статус Уровня 1: проверен доступными средствами; native выполнен частично** — нет нарушений и исключений, но паритет с baseline по структуре не доказан (разные партии). Для строгого сравнения нужен прогон master/Уровня 0 и Уровня 1 в одной партии (одинаковый старт).

## 11. Дополнение (сессия Уровня 2)

1. Общий trigger fan-out/progress/`noProgressCycles` для обычного шага, rebase и recovery теперь один (`ResolveStepTriggers`, см. отчёт Уровня 2 §1): часть «trigger fan-out» общих эффектов шага, оставленная в §2 как различие, сведена к одной процедуре; число пар (2/1/2) по-прежнему различается и принадлежит Уровню 3. Reconcile/ledger/observer boundary остаются у обычного шага.
2. Characterization-тесты Уровня 1 (`AiStepCompletionProtocolTests`) покрывают новый код и «на baseline» не выполнялись (там нет `SettleStep`/`LifecycleAudit`); эквивалентность держится на побайтовом сравнении тел (`EconomyReservationLifecycle`), построчном переносе и нулевых регрессиях 1590/1571. Отступление «подключено одним патчем» не устранимо задним числом.
3. `compile_check.sh` на коде Уровня 1 отдельно не запускался; прогон на коде Уровня 2 (28 = 28 с baseline Уровня 0) покрывает и изменения Уровня 1.
