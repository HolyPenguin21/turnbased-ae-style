# AI V2 — развязка уровней: доказательная база (Э0)

Статус Э0: **реализован — проверки незавершены** (managed и compile baseline сняты; Unity и нативные ветки не выполнялись).
Ветка `refactor/ai-v2-decoupling`, база `717871b824106ae282f59a73c2bdc4cdd38254e9` (= `origin/master`). Unity 6000.5.4f1 (`ProjectSettings/ProjectVersion.txt`). Игровые алгоритмы в Э0 не менялись: изменены текст отчёта L5 и один комментарий в `TurnLoop.cs`.

## 1. Baseline (до первой правки алгоритмов)

| Проверка | Команда / источник | Результат |
|---|---|---|
| Compile | `AI_VERIFY_WORK=D:/aiv-work/ai-verify bash Tools/ai-verify/compile_check.sh --baseline` на `717871b8` | 28 ошибок (UI/editor/camera заглушки), 0 в `Scripts/Ai`; набор записан как baseline |
| Managed-тесты | `D:/aiv-work`: `run.sh d0-base 717871b8` → `patchrun.sh d0-base d0-base-p` | 2123 теста, **1649 прошло, 474 упало**; сборка без ошибок |
| Сравнение с итогом L5 | `regress.py l5-a-p/result.xml d0-base-p/result.xml` | регрессий 0, новых прошедших 0 (код тот же: `300bb989`, дальше только `Docs/`) |
| Каталог baseline | `D:/aiv-work/d0-base-p` (и `l5-a-p`, `l0-base-p`) | исходный baseline не перезаписывается в следующих этапах |

Непатченный прогон `run.sh` даёт 1448 / 675: разница — обращения к `UnityEngine.Object` null-сравнением, которые `patchrun.sh` заменяет. 474 упавших — те же, что в отчёте L5 §11.3 (472 прежних + 2 новых требуют движка). Их статус в Unity не установлен.

Скрипты `run.sh`, `patchrun.sh`, `regress.py`, `ratchet.py`, `deps.py`, `loopsig.py`, `turnorder.py` находятся в `D:/aiv-work` (не в репозитории). Ограничение: приёмка зависит от этого каталога. В репозиторий в Э0 добавлены только новые инструменты ниже.

## 2. Инструменты Э0

`Tools/ai-v2-decoupling-verify/`: `compare_traces.py`, `check_boundaries.py`, `selftest.py`, `README.md` (схема JSONL и правила R1–R6).
`selftest.py`: хорошая трасса → exit 0; пять негативных фикстур (перестановка consume/reentry, стёртый owner, второй commit, income-cover release после старта первого tempo, просроченная строка на следующем ходу) → exit 1. Запущен, **SELFTEST PASSED**.
Затем добавлены recorder `Assets/Editor/AiDecouplingTrace.cs` и золотые трассы двух банковских fixture (§4); `check_boundaries.py` на них — ok. Ограничение: recorder читает ledger из теста в точках, которые выбирает fixture; полного цикла `RunTurn` он не записывает (это появится в fixture этапов). Игровые логи нужных полей не содержат и численным сравнением не считаются.

## 3. Транскрипция цикла

`Assets/Editor/AiTurnLoopTests.cs` уже содержит независимую транскрипцию исходного цикла (`Baseline`, снята с `c3cdde46`, до L4) и генератор 20 000 многошаговых сценариев против реального `TurnLoop.Run`. Для Э1 тест расширяется (исходы тел вместо записи состояния), второй похожий тест не создаётся. Транскрипция «текущего» `Run` как эталона для Э6 совпадает с этой, так как порядок переходов L4→L5 не менялся (`git log f46e4529..HEAD -- TurnLoop.cs` — пусто (до Э0 файл не менялся после `f46e4529`)).

## 4. Сценарии §12 ТЗ: сопоставление с тестами на `717871b8`

Покрытие: **полное** — тест проверяет именно то, что требует сценарий; **частичное** — часть условий; **нет** — fixture отсутствует и пишется в начале этапа, который его использует (на входной ревизии этапа, до правок кода). Нативные ветки (cold, rebase, stall, Phase B после Reaction, `PhaseBStateChanged`, срочные возвраты, лимиты) не блокируют работу по решению владельца; они остаются **не наблюдавшимися**.

| ID | Существующие тесты | Покрытие | Чего нет |
|---|---|---|---|
| S1 | `AiEconomyReservationLifecycleTests.CompletionBecomesUnexecutable_DowngradesOnlyItsOwnApAndPreservesPhysicalHold`, `InvalidCompletion_ReleasesOnlyInvalidOwnersRows`, `AiStrategicSpendabilityApTests.SwitchingDeferredEconomyHold_ReleasesPreviousOwnerOnly`; **новый** `AiDecouplingBaselineTests.S1_Bank_*` (с трассой) | частичное: банк (ledger, реальные `EconomyReservationLifecycle`) | порядок Observe→ledger→SettleStep→reconcile в `RunTurn`; income cover перед первым tempo (требует `ctx.Map`) — fixture до Э2 |
| S2 | `AiAviationSortieCycleTests.MandatoryRebase_StalledWingsStopCounting_ForTheirTurnOnly` (только движок), `AiOperationalWorkSelectionTests.AStalledObligationLeavesTheFundedMissionsSelectable`, `AiTurnLoopTests`; **новые** `S2_Triggers_*` (реальный `StepTriggerSequence`: rebase 1 пара, recovery 2; факт первого reentry остаётся pending при 1 паре и уходит во вторую), `S2_Stall_*` (stall только на свой ход) | частичное→**полное для пар и stall-реестра** | связка с реальным `StrategicInterruptRegistry` и `MandatoryAviationStep` (нужен мир) |
| S3 | `AiRetryNextTurnCarryTests`, `AiMissionStepResultTests`, `AiEconomyDecisionTests` (RepriceThisTurn) | частичное | независимые бюджеты assignment / reprice на пределе, история отказов после `SetAssignment` — fixture до Э3 |
| S4 | `AiLifecycleReturnPolicyTests` (`AReturnLegWaits_ARealTaskDoesNot`, `AnActiveDefenceWithdrawalNeverWaits`, `AReturnNeverWaitsTwoTurnsInARow`, `OnlyARealHomeThreatStopsTheWait`), `AiStrategicSpendabilityApTests.TacticalRetreat_ProtectsItsActivation_*`; **новый** `S4_Returns_*` (ожидание → `RecordWait` → `MarkProtectedThisTurn` → на следующем ходу без ожидания; ActiveDefence не ждёт) | **полное** на реестрах | tactical retreat Attack-intent как отдельный случай `IsDeferrableReturn` — в `AiStrategicSpendabilityApTests`, не в связке |
| S5 | `AiCombatOpportunityWarmTests`, `AiCombatCacheLifecycleTests`, `WorthItEstimateCacheTests`, `AiMissionLeaseLifecycleTests` | частичное | «та же ревизия знания, иные HP/рука/pathing» и same-revision stale intent с `ResolveActive` — fixture до Э5 |
| S6 | `AiStrategicReadmissionTests`, `AiOperationalWorkSelectionTests`, `AiTurnLoopTests`, `AiTurnSessionIsolationTests` | частичное | `PhaseBStateChanged` открывает проход на реальном раунде, потеря второго reentry при consume — fixture до Э6 |
| S7 | `AiTurnLoopTests` (cold, окно, StageDone+PassOpen, 20 000 сценариев), `AiPipelineOrchestrationUnitTests` | полное для цикла; частичное для warm residual | «warm unresolved demands сохранены» на реальном cold-теле |
| S8 | `AiStrategicSpendabilityApTests.ReactionRoundRelease_*`, `AiTurnResourceBookTests`, `AiReservationInvariantsTests` | частичное | совместные Economy+Attack preparation+Reaction за AP, rollback канонической операции, отмена итератора |
| S9 | `AiMissionLeaseLifecycleTests` (`StaleResourceWriteCannotResetANewTurnsReservationStorage`, rekey/retire), `AiTurnSessionIsolationTests`; **новый** `S9_Bank_*` | частичное | следующий ход «другого игрока», anonymous claims после disposal |

**Сделано в Э0:** recorder `AiDecouplingTrace` и фикстуры `S1_Bank_*`, `S9_Bank_*` (ledger-уровень) с записанными ожидаемыми значениями (4+3 AP → 3 AP после downgrade; нет утечки; следующий ход пуст; чужой игрок не затронут). Золотые трассы: `Tools/ai-v2-decoupling-verify/golden/S1_bank.jsonl` (7 записей), `S9_bank.jsonl` (4); `check_boundaries.py` — ok на обеих. Managed-прогон с ними: 2125 тестов, 1651 прошло, 474 упало, регрессий 0 относительно `d0-base-p`, 2 новых прошедших.
**Добавлены после первой версии:** `S2_Triggers_*` (2 случая), `S2_Stall_*`, `S4_Returns_*`. Прогон: 2129 тестов, 1655 прошло, 474 упало, регрессий 0 относительно `d0-base-p`, 6 новых прошедших.
**Не сделано и почему:** S3 (бюджеты assignment/reprice) требует `ResourceAllocator` с миром и армиями; S5 (кеши знания, `ResolveActive` на same revision) — `WorldSnapshot` и карту; S8 (совместные Economy+Attack+Reaction, rollback канонической операции) — игровой корень и движок; S6/S7 в части цикла закрыты `AiTurnLoopTests` (20 000 сценариев), в части реальных тел — нужны мир и движок. Managed-прогон их не построит без Unity-объектов; они пишутся как PlayMode/EditMode-fixtures на входной ревизии этапа, который их использует (S3→Э3, S5→Э5, S6/S7→Э6, S8→Э2/Э6), и запускаются в Unity владельцем. До этого соответствующий этап не стартует. Расхождений между ТЗ и кодом при сверке ссылок не найдено (§4b); сценарии S1–S9 в игре не проигрывались.

## 4a. Матрица зависимостей на `717871b8`

Скрипт `Tools/ai-v2-decoupling-verify/coupling_matrix.py --rev 717871b8` (в репозитории; метод тот же, что у `deps.py`): 289 файлов, **203** связи между папками `Ai/V2` — историческое число воспроизведено. `AiStrategyV2Pipeline.cs`: 14 папок, **84** внешних типа (как в отчёте сравнения). Вся папка `Orchestration/`: 16 папок, **122** внешних типа в первой версии скрипта. В Э2 обнаружено, что скрипт отбрасывал выражения внутри интерполированных строк (`$"…{AttackForceReadiness.MobilizationOpen(x)}…"` — это код); исправлено, на `717871b8` теперь **127** типов, `Pipeline` по-прежнему 84. В сравнении отчёта было 116→118 «без файлов моделей» — другая методика; для сравнения этапов использовать только значения скрипта одной версии (исправленной). Полный вывод: `D:/aiv-work/coupling/d0-717871b8.txt`. Это счёт ссылок на типы, не граф вызовов; вспомогательная метрика.

## 5. Коррекции документов

- `Docs/ai-v2-pipeline-simplification-level5.md`: заголовок статуса (итоговая нативная партия выполнена, §12; перечислено, что она не покрыла), строки таблицы §9 и пункта 3 §11.5; добавлено, что единственный владелец переходов есть, единственного писателя счётчиков нет. Исторические результаты тестов не менялись.
- `TurnLoop.cs`: комментарий над `TurnLoopState` приведён к тому же (без изменения кода).
- `ARCHITECTURE.md` («единственный владелец переходов») верно и не менялось.

## 6. Что не выполнено

Unity EditMode/PlayMode — берёт на себя владелец; нативные ветки (cold, rebase, stall, Phase B после Reaction, `PhaseBStateChanged`, срочные возвраты, лимиты) — не блокируют, остаются не наблюдавшимися. Остальные fixture S1–S9 (§4, колонка «Чего нет») пишутся в начале этапов. Скрипты `run.sh`, `patchrun.sh`, `regress.py` лежат только в `D:/aiv-work`.

## 7. Сверка ссылок ТЗ с кодом на `717871b8` (§4b)

Проверено чтением и `grep`; поведение не исполнялось.

| Утверждение ТЗ | Результат |
|---|---|
| `ReconcileEconomyCompletionReservations` L817/858/887, `ReleaseDeferredEconomyIncomeCover` L862, `OperationContinuationWindow.Settle` L865, `SetMobilizationOpen` L137 | совпадает; Settle/Release/Reconcile стоят в блоке первого Phase B, Reconcile на L887 — перед `UseSurplus` |
| `MarkStalled` в `RunTurn` | L485 |
| флаг `ownershipFreshAfterPhaseA` | писатели L308, L424, L969, сброс L550 — 3 писателя + сброс, как в ТЗ |
| `ResolveActive`/`RefreshActors` | старт L177/182, повтор L279/281, итог L880/1025, `RefreshOperationalFrame` L1145 |
| `BuildMissionSet` | единственный production-вызов L555 (учесть в Э4: общий `Build` для «иных callers» не нужен, если так и останется) |
| `RecordProvisionFailure/Success` в `MissionOutcomeLedger` | пишут только `PendingFailure`/`Provisioned` строки (`MissionOutcomeLedger.cs` L52–64) |
| вызовы `cycleLedger.RecordProvision*` в цикле | `RunTurn` L668/725/739 — только запись; чтение проверить повторно на входной ревизии Э3 |
| `CapabilityPoolExhaustionRegistry` | кроме `RunTurn` использует **Reaction** (`ReactionRoundExecutor` L199–210 — тот же ledger и тот же реестр): при Э3 Reaction остаётся прежним потребителем реестра, его зависимость не исчезает |
| `TurnLoop.cs` | без правок кода после `f46e4529` |

## 8. Конфигурация фикстур

Константы, от которых зависят проверки: `AiConfigV2.maxMidTurnStepsPerTurn = 96`, `maxMidTurnNoProgressCycles = 2`, `maxEndOfTurnTempoReruns = 1`, `lifecycleReturnHomeThreatSeverity = 0.25`; временное `attackRequiresDefenderCoverage = false`. Фикстуры S1/S2/S4/S9 не используют карту, seed и руки: их входы заданы в коде теста (игроки `PlayerSetupData`, ходы 21/31/5–7/10–11, стоимости и AP из тела теста). Недетерминированные партии — только smoke. Сохранённые входы миров для S3/S5/S8 появятся вместе с их fixtures.

# Э1 — один писатель `TurnLoopState`

Статус: **реализован — проверены доступными средствами (managed, compile); Unity и native не выполнялись.** Код: `663d075c`, вход этапа — `e62b624b`.

## Что изменено

| Файл | Изменение |
|---|---|
| `Orchestration/TurnLoop.cs` | + `TurnLoopView` (readonly struct: значения `SettledSteps`, `NoProgressCycles`, `ReturnsMayWait`, `WithinStepBounds`; ссылки на состояние нет), + `ProgressUpdate`, + `AdmissionIterationOutcome` (одноразовый sink: `SettledStep`, `NoFundedMission`, `NoProvisionedTask`, `StopAfterSettledStep`, `DeferReturns`, `NoProgressAfter`), + `TurnLoop.ApplyIterationOutcome` (единственная запись счётчиков итерации); `TurnLoopWork.Iteration` принимает view и outcome вместо `Action<bool>` |
| `Orchestration/AiStrategyV2Pipeline.cs` | `RunAdmissionIteration` и `RunMandatoryAviationStep` получают view/outcome; все записи `loop.SettledSteps/NoProgressCycles/ResidualWindow/ReturnsDeferred` и три `stopPass(true)` заменены вызовами outcome. Остальные записи уже были в `TurnLoop` |
| `Assets/Editor/AiTurnLoopTests.cs` | драйвер проверяемого цикла использует outcome, **эталон-транскрипция baseline не менялась** (тела пишут состояние сами, как в исходнике); + таблица исходов с литеральными ожиданиями; + view без ссылки на состояние; + проверка исходников |

Новый файл `AdmissionIterationOutcome.cs` ТЗ предлагало отдельным; типы размещены в `TurnLoop.cs` рядом с `TurnLoopWork`, потому что они часть контракта цикла и больше нигде не используются. Если нужен отдельный файл — перенос без изменений поведения.

## Сверка с требованиями Э1

| Требование ТЗ | Результат |
|---|---|
| Исходы: mandatory action, mission, no-funded, no-provisioned, mission без typed invalidation, deferred returns | таблица `TheIterationEndingsChangeTheCountersExactlyAsTheBaselineBodiesDid` с литеральными значениями (ожидания из исходника тел до Э1) |
| Все записи — в `TurnLoop.ApplyIterationOutcome` | `OnlyTurnLoopWritesTheTurnLoopState` сканирует `Assets/Scripts`: записей вне `TurnLoop.cs` нет; мутация (прямая запись `loop.ReturnsDeferred` в теле) — тест **падает** |
| view без ссылки на writable state | поля `TurnLoopView` — только значения; тест рефлексией проверяет отсутствие поля типа `TurnLoopState` |
| промежуточные boundary/log: номер шага `view.SettledSteps + delta` | `CheckBoundary` и строки `[Loop] step=` используют `stepNumber`/`taskStepNumber`; `noProgress=` в логе — `outcome.NoProgressAfter(view.NoProgressCycles)` (та же арифметика, что в `Apply`, одно место). Читателей счётчиков внутри итерации, кроме этих двух мест, нет (`grep loop\.` по `RunTurn` — пусто) |
| сброс окна при `OpenPass`, no-progress при вердикте раунда, `ReturnsDeferred` накапливается | не менялись (остались в `TurnLoop`); `ReturnsDeferred` — `|=` |
| `AiTurnLoopTests`, 20 000 сценариев против транскрипции baseline | проходит; мутация `Apply` без записи окна — **5 из 13 тестов класса падают** |

## Проверки

| Проверка | Результат |
|---|---|
| Managed (`e1-a-p`) | 2132 теста, 1658 прошло, 474 упало; регрессий 0 относительно `d0-fix-p`, 3 новых прошедших |
| Compile | 28 = 28, новых 0 (`cmpcc.py`) |
| Мутации | 2 из 2 пойманы (запись в тело; потеря записи окна в `Apply`) |
| Матрица зависимостей (`coupling_matrix.py`) | 203 связи, `Pipeline` 14 папок / 84 типа, `Orchestration` 16 / 122 — **без изменений**, как и предсказывало ТЗ (Э1 — совместная запись, а не межпапочные вызовы) |
| Писатели `TurnLoopState` | было: `TurnLoop` + тела (`SettledSteps`, `ReturnsDeferred`, `NoProgressCycles`, `ResidualWindow`); стало: только `TurnLoop` (проверено сканом) |
| Банк / кеши | не затрагивались: счётчики не банк и не кеш; порядок вызовов и точки refresh/reconcile/ledger/settle в итерации не менялись; равенство control-trace — транскрипцией `AiTurnLoopTests` |
| Unity EditMode/PlayMode, native | не выполнялись (S1/S4/S8 на реальных телах — вне managed) |

## Цена изменения (до / после)

Изменить правило no-progress (например, считать раунд Phase B без изменений шагом без прогресса): раньше — `TurnLoop` + три тела (авиация, миссия, отказ provisioning) + вердикт; теперь — `AdmissionIterationOutcome.NoProgressAfter`/`ApplyIterationOutcome` и вердикт в `TurnLoop`; тела сообщают только факт исхода. Межуровневая цена этапом не уменьшена (честная граница по ТЗ).

# Э2 — банк узнаёт о моментах хода

Статус: **реализован — проверены доступными средствами (managed, compile); Unity и native не выполнялись.** Вход этапа — `95e5eac2`.

## Решение о новом классе

Перед созданием проверены все файлы `Strategy/` и вызывающие стороны стадий: координатора, который соединяет Economy-стадии (`EconomyReservationLifecycle`, Strategy/Demand) и окно продолжения (`OperationContinuationWindow`, State), нет (`grep` по `Ai/V2`: оркестратор был единственным вызывающим). `StrategicManager` по собственному заголовку — тонкий фасад без логики, поэтому правила стадий в него не кладутся. Создан `Strategy/StrategicTurnLifecycle.cs` (ТЗ Э2) — без состояния и формул, только порядок вызовов существующих владельцев; `StrategicManager` получил четыре делегирующих входа.

## Таблица «событие → операции» (до и после)

| Событие (вход `StrategicManager`) | До: вызовы из `RunTurn` (строки на `95e5eac2`) | После: реализация `StrategicTurnLifecycle` | Точка в ходе |
|---|---|---|---|
| `ObserveInitialForce` | `AttackForceReadiness.MobilizationOpen(snapshot.Self)` → `SetMobilizationOpen` → строка лога (L136–141) | то же, тот же порядок и текст лога | после первого Scan/Warm, до Phase A |
| `AfterMissionSettlement` | `ReconcileEconomyCompletionReservations` (L817) | то же | после `SettleStep` шага миссии, до `CheckBoundary` |
| `BeforeFirstTempo` | `Reconcile…` → `ReleaseDeferredEconomyIncomeCover` → `OperationContinuationWindow.Settle` (L858–865) | то же, тот же порядок; вызывается только при `PhaseBRounds == 0` (условие остаётся в `TurnLoop`) | после `TerminalForce` последнего прохода, до первого раунда |
| `BeforeTempoSpend` | `Reconcile…` (L887) | то же | в каждом раунде Phase B, перед `UseSurplus` |
| авиация: stall | `AviationObligationStallRegistry.MarkStalled` внутри `RunMandatoryAviationStep` при `!progress` (L485) | `AviationObligations.RecordSettledStep(player, ctx, actorId, progressed)` в той же точке — после `ResolveStepTriggers` и расчёта `Progressed`; возвращает `true`, когда отметил stall, строка лога остаётся в точке вызова | после reentry-пар шага |

## Сверка с требованиями Э2

| Требование | Результат |
|---|---|
| Оркестратор не называет `EconomyReservationLifecycle`, `OperationContinuationWindow`, `AviationObligationStallRegistry` в исполняемом коде **всей папки** | `OrchestrationDoesNotNameTheBankStageOwners` (скан `Orchestration/*.cs`, комментарии игнорируются) проходит; **мутация** (возврат прямого вызова в `RunTurn`) тест ловит |
| Порядок стадий внутри момента | `TheFirstTempoMomentKeepsTheStageOrder` (индексы вызовов в `BeforeFirstTempo`: reconcile < release < settle; release ровно один); мутация (убрать `Settle`) пойманa |
| `Settle` только на закрытии обычных проходов; `AfterMissionSettlement` / `BeforeTempoSpend` окно не закрывают | `OnlyTheClosingOfTheOrdinaryPassesSettlesTheContinuationWindow` — **требует движок** (`PlayerRoot` — `UnityEngine.Object`): в managed-прогоне падает как engine-bound (`TypeInitializationException`), проверяется в Unity; мутации ловит |
| Метка mobilization на ходу и перезапись следующим сканом | `TheInitialScanStampsTheMobilizationGateOfThatTurnOnly` проходит |
| stall только на свой ход; прогресс не отмечает stall | `AnObligationThatDidNotProgressIsStalledForItsTurnOnly` проходит; мутация (stall при progress) поймана |
| Income cover освобождается ровно один раз, даже если `TerminalForce` изменил мир | порядок вызовов не менялся (`TurnLoop` вызывает `FirstPhaseBSettle` один раз при `PhaseBRounds == 0`); поведение на реальном `ctx.Map` — Unity-fixture S1/S8 |
| Диагностика mobilization: порядок и текст логов | не менялись (строка перенесена как есть) |
| Writers банка не менялись, lease API не обходятся | изменений в ledger / `MissionLeaseBook` / `AiTurnSession` нет |

## Проверки

| Проверка | Результат |
|---|---|
| Managed (`e2-a-p`) | 2137 тестов, 1662 прошло, 475 упало; регрессий 0 относительно `e1-a-p`, 4 новых прошедших; 475-й — новый engine-bound тест из таблицы выше (на baseline его не было) |
| Compile | 28 = 28, новых 0 |
| Матрица зависимостей (`coupling_matrix.py`, исправленный скрипт) | 203 связи (не изменились); `Orchestration` целиком: **127 → 124** внешних типа, убраны именно `EconomyReservationLifecycle`, `OperationContinuationWindow`, `AviationObligationStallRegistry`; `Pipeline`: 84 → 80 (ещё `AttackForceReadiness`, он остался в `DevelopmentAdmission` как владелец ключа допуска). Появились связи `Strategy → State (OperationContinuationWindow)` и `Recon → State (AviationObligationStallRegistry)` — знание переехало к владельцам правил |
| Банк / кеши | вызовы стадий в тех же точках и в том же порядке; ключ допуска Economy читает `ReasonDigest` после тех же refresh/reconcile; stall остаётся turn-scoped (`AiTurnSession` вызывает `EndTurn`) |
| Unity EditMode/PlayMode, native | не выполнялись; S1/S5/S8/S9 на реальных `root`/`ctx.Map` — Unity-fixtures |

## Цена изменения (до / после)

Новая удерживаемая до первого tempo резервация: раньше — вызов в `SettleBeforeFirstPhaseB` (`Orchestration`) + Strategy/State; теперь — `StrategicTurnLifecycle.BeforeFirstTempo` + Strategy/State, событие то же. Новый авиационный исход: раньше ветка stall в `RunTurn`; теперь `AviationObligations.RecordSettledStep` (Recon). Новый тип глобального факта по-прежнему проходит старые точки fan-out — Э2 их не устраняет.

# Э3 — Provisioning владеет подбором исполнимой миссии и парковкой прохода

Статус: **реализован — проверены доступными средствами (managed, compile); Unity и native не выполнялись.** Вход этапа — `19c80a86`.

## Что изменено

| Файл | Изменение |
|---|---|
| `Provisioning/ProvisioningManager.Selection.cs` (partial существующего класса) | `ProvisionNext(...)` — протокол подбора: Prepare → пакет отказов Scout → один re-pack на пакет (`assignmentReallocPass < max`) → выбор первой допустимой миссии → отказ/успех → re-pack (`++repriceReallocPass >= max` только для `RepriceThisTurn`). Перенесён из `RunAdmissionIteration` построчно. `SelectionSteps` — три мировые операции (`PreparePass`, `ScoutAssignmentFailures`, `Provision`) как шов для тестов; в production привязаны к методам класса |
| `Provisioning/PassParking.cs` | парковка прохода: `Park` (множество прохода + `CarryRetryNextTurn`), `Filter` (+причина `retry_next_turn_after_provision_failure` для durable-ноги); создаётся при открытии прохода |
| `Provisioning/ProvisioningSelectionOutcome.cs` | результат: `Selected`, `SelectedKey`, `SelectedIsCommitment`, `FinalAllocation`, `AttemptedKeys`, журнал `Events` (`ScoutBatchFailure` / `Failure` / `Success`), `FundedKeysAcrossPacks` — транзитный журнал одного вызова, не ledger |
| `Orchestration/AiStrategyV2Pipeline.cs` | вызов `ProvisionNext`; журнал проигрывается в `MissionOutcomeLedger` и телеметрию сразу после вызова (до исполнения шага); `retryNextTurnThisPass` заменён `PassParking`; фильтр парковки — `passParking.Filter` в прежней точке (после ожидания возвратов, до `BindFunding`) |
| `Assets/Editor/AiProvisioningSelectionTests.cs` | 11 тестов (ниже) |

Начальный `Pack` и выбор обязательной авиации остались в оркестраторе до первого вызова (иначе меняется порядок регистрации funded). `ProvisioningSession` по-прежнему открывается `using` на всю итерацию и не закрывается в `ProvisionNext`: выбранная миссия исполняется, наблюдается и повторно допускается под её claim-ами.

## Решения и сверка с требованиями Э3

| Требование ТЗ | Результат |
|---|---|
| Последовательности: batch reject → один repack; reprice; no-new-failures; Converged; RetryNextTurn; успех после отказа; два бюджета на пределе | тесты на **реальном** `AllocationSession` и реестре (Hard-commitments дают детерминированные funded без мира): успех с первой попытки; RetryNextTurn → парковка → следующая миссия; reprice ровно `max` попыток; non-reprice без других миссий; бесконечный Scout-пакет = ровно `max` re-pack и `max + 1` отказов, затем выбор; **бюджеты независимы** (3 пакета не съедают reprice) |
| Журнал в порядке, все попытки и ключи | `Events` в порядке попыток, `AttemptedKeys` — все ключи (включая отказы пакета) |
| Парковку записывать в момент отказа | `Park` вызывается сразу после регистрации отказа, до следующего `PreparePass` |
| Логи попыток синхронно внутри provisioning | строки `[Loop] assignment-batch …` и `[Loop] provision … — FAIL` не отложены; текст не менялся |
| Телеметрия: каждая попытка и все funded-паки | `provisioningFailures` считает каждое событие-отказ; `fundedKeysThisTurn` получает `FundedKeysAcrossPacks`; `provisioned.Add` для успеха |
| ledger не читается в цикле | проверено поиском: в `RunAdmissionIteration` между старым и новым местом записи `cycleLedger` не читается; `RecordProvisionFailure` пишет только `PendingFailure`, `RecordProvisionSuccess` обнуляет его (`MissionOutcomeLedger.cs`) |
| `CapabilityPoolExhaustionRegistry` и retry-цикл исчезают из Orchestration | `OrchestrationDoesNotRunTheProvisioningProtocol` (скан всей папки: реестр, оба счётчика, `maxReallocIterations`, `PreparePass`, `ScoutAssignmentFailures`, `RegisterProvision*`) |
| Pass-reset и turn-reset различаются | `ANewPassForgetsTheSetButTheRegistryKeepsTheVerdictWhileThePoolIsProvenEmpty` |
| `ProvisionNext` не закрывает session | `ProvisioningSession` не получает `Dispose` в методе (проверка чтением); `using` остался в итерации |

## Проверки

| Проверка | Результат |
|---|---|
| Managed (`e3-a-p`) | 2148 тестов, 1673 прошло, 475 упало; регрессий 0 относительно `e2-a-p`, 11 новых прошедших (упавшие — те же 475) |
| Мутации `ProvisionNext` | 5 из 5 пойманы: общий счётчик вместо двух; batch-граница `<=`; reprice-граница `>`; снятая парковка одиночного отказа |
| Compile | 28 = 28, новых 0 |
| Матрица зависимостей | 203 связи. `Pipeline`: 14 → 13 папок, 80 типов. **`Orchestration` целиком: 124 → 125 типов — рост на 1**: ушли `CapabilityPoolExhaustionRegistry`, `ProvisionFailure`, `ProvisioningResult`, `ProvisionDisposition` (4), добавились 5 типов контракта (`PassParking`, `PassParkingResult`, `ProvisionEvent`, `ProvisionEventKind`, `ProvisioningSelectionOutcome`). Счёт ссылок — вспомогательная метрика по ТЗ; знание протокола (реестр, бюджеты, порядок попыток) перешло к Provisioning, у `Provisioning` появились связи на `Allocation` (`AllocationSession`), `State`, `Diagnostics` |
| Банк | tentative claims (`ProvisioningSession`), `AllocationSession` и их порядок не менялись; durable claims по-прежнему через ledger → `SettleStep` на той же точке шага |
| Кеши | `ShouldSkipRetried` читает тот же снапшот; реестр инвалидируется `AiTurnSession`, второй кеш не создан |
| Unity EditMode/PlayMode, native | не выполнялись; S3 на реальных `Provision*` (мировая часть) — Unity-fixture |

## Наблюдение вне объёма этапа

`ReactionRoundExecutor` содержит собственный цикл повторного подбора с тем же реестром и ledger (`reallocPass`, L199–228). Он не менялся и остаётся прежним потребителем; общий протокол с `ProvisionNext` — возможное продолжение вне этой задачи.

## Цена изменения (до / после)

Новая provisioning disposition («повторить после следующего pack»): раньше — правка `while` и обоих хвостов учёта отказа в `RunAdmissionIteration` + Provisioning + State; теперь — `ProvisionNext` (+ реестр State), потребитель исхода (цикл `Events` в оркестраторе) не получает новой ветки. Новый факт, снимающий исчерпание пула, обрабатывает прежний реестр State; оркестратор его доказательств не знает.

## Перепроверка Э3: реализация, резервирование, кеши

### Реализация (снизу вверх)

| Уровень | Что проверено | Результат |
|---|---|---|
| Контракты | `PassParking`, `ProvisionEvent`, `ProvisioningSelectionOutcome` — только данные и парковка; ни исполнения, ни трат | `TheSelectionProtocolSpendsNothingAndLeavesTheSessionOpen` (скан кода файла: нет ledger резервов, `MissionLeaseBook`, `EconomyReservationLifecycle`, `OperationContinuationWindow`, `SpendAuthority`, `.Dispose(`) |
| Протокол | построчная сверка `ProvisionNext` с исходным циклом | **дифференциальный тест**: `AiProvisioningSelectionParityTests` — 1500 случайных сценариев, независимая транскрипция исходного цикла (`19c80a86`) и новый код на одинаковых реальных `AllocationSession` и реестре; сравниваются последовательность вызовов мировых шагов (prepare / scout / provision с funded-ключами), выбранная миссия, ключи, `IsCommitment`, попытанные ключи, итоговый pack, ключи всех паков, телеметрия, `MissionOutcomeLedger` (через `FinalizeSteps`), парковка, чтения реестра, состояние аллокатора. Генератор достигает ветвей (выбор > 100, без выбора > 100, парковка > 100, re-pack > 100, доказанное исчерпание пула > 50 сценариев). Прошёл |
| Чувствительность теста | мутации `ProvisionNext` | 9 из 9 пойманы: общий счётчик; граница `<=`; не вызывается `DeferNoExecutableStep`; `SettleScoutBatch` без снапшота; `RecordProvisionFailure` без снапшота; нет учёта ключей паков; нет `AttemptedKeys` у пакета; нет `RegisterProvisionSuccess`; снятая `RegisterSuccess` сессии |
| Потребитель | `Pipeline` | журнал проигрывается через `MissionOutcomeLedger.RecordProvisionAttempt` (интерпретация исхода — у Continuity), телеметрия считается в цикле по событиям в прежнем порядке |

Побочное изменение: `MissionOutcomeLedger.RecordProvisionAttempt` (Continuity) — один диспетчер `Success`/`Failure`; поведение записей не менялось.

### Резервирование ресурсов

| Вопрос | Проверка | Результат |
|---|---|---|
| Выбор тратит или резервирует что-либо сам | скан файла + тест `ASuccessfulSelectionKeepsItsClaimsOpenAndTheLedgerUntouched` | число строк `StrategicResourceReservationLedger` до/после вызова не меняется |
| Claims выбранной миссии живут до исполнения | тот же тест | `AlreadyProvisioned(selectedKey)` истинно после вызова; следующий `Pack` содержит locked claim, равный `ClaimedAp` |
| Состояние аллокатора (repriced floors, rejected, locked) | в дифференциальном тесте после прогона делается ещё один `Pack` и сравниваются: funded-ключи с `Tentative.Ap`, `LockedClaim.Ap`, число deferred, `HasNewFailures`, `Converged`, `PassNumber`, `ProvisioningSession.ApClaimed` и признаки `AlreadyProvisioned` по каждой миссии | идентично исходному циклу во всех 1500 сценариях |
| Закрытие сессии | `using var cycleProvisioning` остался в итерации; `ProvisionNext` не вызывает `Dispose` (скан) | мутация «Dispose внутри» поймана |
| Исключение посреди выбора | `cycleLedger` — локальная переменная итерации и при исключении отбрасывается вместе с ходом; журнал до проигрывания теряется так же, как терялся бы ledger | изменения поведения нет |
| Порядок относительно реальных трат | `Provision`, `PreparePass` — прежние методы; порядок их вызовов и аргументы идентичны (последовательность вызовов в дифференциальном тесте) | совпадает |

Не проверено: реальные `Provision*` на мире (списание AP/ресурсов, `Economy` completion в `ProvisioningManager.Economy*`) — это Unity-fixture S3/S8; код `Provision` в этапе не менялся.

### Кеширование: запись и чтение

| Кеш / состояние | Писатели | Читатели | Область | Что показала проверка |
|---|---|---|---|---|
| `CapabilityPoolExhaustionRegistry` (`DeferredMissions`, `RetryNextTurn`, `Exhausted`) | `DeferNoExecutableStep`, `CarryRetryNextTurn` (через `PassParking.Park`), `SettleScoutBatch`, `RecordProvisionFailure`, `MarkExhausted` | `CanAttempt` (выбор), `ShouldSkipRetried` (фильтр парковки), `IsExhausted` | ход (`BeginTurn`/`EndTurn` в `AiTurnSession`) | порядок записи/чтения совпадает с исходным: итоговые чтения `CanAttempt`/`ShouldSkipRetried`/`IsExhausted` по каждой миссии идентичны во всех сценариях, включая «застрявший» пул (доказательство исчерпания); сброс на новом ходу — `NewTurn_ForgetsTheCarriedRetry` |
| Множество парковки прохода | `PassParking.Park` | `PassParking.Filter` следующего settled-допуска | проход (новый `PassParking` при каждом `OpenPass`) | `ANewPassForgetsTheSetButTheRegistryKeepsTheVerdictWhileThePoolIsProvenEmpty`; множество парковки идентично в дифференциальном тесте |
| Снапшот мира | не изменяется внутри протокола (ни `Scan`, ни `ObserveSettled`) | `SettleScoutBatch`, `CanAttempt`, `ShouldSkipRetried` читают один и тот же `snapshot`, переданный на входе | вызов | в `Pipeline` передаётся текущая локаль `snapshot` непосредственно в точке прежнего цикла |
| `AllocationSession` (`_lastFingerprint`, rejected, floors, locked) | `RegisterProvision*`, `Pack` | `Pack`, `HasNewFailures`, `Converged` | итерация (`ResourceAllocator.BeginTurn` на итерацию) | порядок вызовов идентичен (в трассе вызовов и состоянии после) |
| `MissionOutcomeLedger` | `RegisterProposals/Commitments`, `RecordProvisionAttempt`, `RecordDeferrals`, `RecordExecution` | `FinalizeSteps` после исполнения | итерация | журнал проигрывается сразу после `ProvisionNext` и до `RecordDeferrals`/исполнения; читателей между старым и новым местом записи нет (поиск); итоговый `FinalizeSteps` идентичен в дифференциальном тесте |
| WorldDelta / `KnowledgeVersion` / PathingVersion / estimate-кеши / combat `PoolCache` | — | — | — | этап их не касается: изменены только `Provisioning/`, `Orchestration/AiStrategyV2Pipeline.cs`, `Continuity/MissionOutcomeLedger.cs` и тесты (`git diff --stat`) |

Равенство ревизий не доказывает содержимого ключей кеша: чтения реестра проверены по значениям, а не по номерам версий.

### Итог перепроверки

Managed (`e3-a-p`): 2151 тест, 1676 прошло, 475 упало; регрессий 0 относительно `e2-a-p`, 14 новых прошедших. Compile 28 = 28. Unity и native — не выполнялись; S3 и мировые части `Provision*` — Unity-fixtures.
