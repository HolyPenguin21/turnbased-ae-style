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

# Э4 — Missions владеет портфелем, Continuity — возвратами

Статус: **реализован — проверены доступными средствами (managed, compile); Unity и native не выполнялись.** Вход этапа — `4b4a4519`.

## Что изменено

| Файл | Изменение |
|---|---|
| `Missions/MissionPortfolio.cs` (новый) | `Build(snapshot, breakdown, activeIntents, reconObjectives, aggressionObjectives, radar, demands, trace, ctx)` → `MissionPortfolioResult(Missions, Deferrals)`: обновление Recon lane pressures → 4 планировщика → `AttemptId` → `EffectiveValue = BaseValue × RadarValueScale` → `AttackPreparationPriority` → `TaskScores` → корреляция. Перенесено из `Pipeline.BuildMissionSet` (метод удалён) |
| `Continuity/LifecycleReturnPolicy.cs` | перенесён из `Orchestration/` через `git mv` вместе с `.meta` (GUID `cce6239d…` сохранён), namespace, `LastWait`, `ClearAll` не менялись |
| `Continuity/MissionContinuityLayer.ReturnDeferral.cs` (новый partial) | `DeferReturnsBeforeTempo(snapshot, player, turn, proposals, activeIntents)`: `HomeThreatened` → `SelectWaiting` → `RecordWait` → `MarkProtectedThisTurn` → лог → `Retained/Waiting/Deferrals` в прежнем порядке |
| `Orchestration/AiStrategyV2Pipeline.cs` | `MissionPortfolio.Build`; `if (view.ReturnsMayWait) DeferReturnsBeforeTempo(…)`; исход «возврат ждал» → `outcome.DeferReturns()`; фильтр парковки (Э3) стоит после ожидания, до `BindFunding`, как и был |
| `Assets/Editor/AiMissionPortfolioTests.cs` (новый) | 10 тестов |
| `ARCHITECTURE.md` | путь правила возвратов исправлен |

**Мёртвая ветка убрана.** `BuildMissionSet` имел единственного production-вызывающего (`RunAdmissionIteration`), и тот всегда передавал `aggressionPressureAlreadyRefreshed: true` — ветка повторного `RefreshAggressionOperationalFacts` не исполнялась. `Build` её не содержит; факты Aggression по-прежнему обновляет кадр решения (`RefreshOperationalFrame`) в том же шаге, что и objectives. `ReactionRoundExecutor` собирает свой набор отдельным кодом (Recon + Aggression, другие логи) и не менялся.

## Сверка с требованиями Э4

| Требование ТЗ | Результат |
|---|---|
| Порядок шагов портфеля | `ThePortfolioKeepsTheOrderOfItsSteps` (порядок вызовов в файле) + дифференциальный тест |
| `EffectiveValue = BaseValue × RadarValueScale`, баланс не менялся | `ThePortfolioMatchesTheReplacedOrchestratorMethod` (транскрипция прежнего метода, 5 размеров набора, неравномерный Radar: Recon = 0,1) и `EveryMissionIsValuedByTheRadarAndGetsAnAttemptId` |
| Правило возвратов у Continuity, порядок HomeThreatened → SelectWaiting → RecordWait → MarkProtected | `AnOrdinaryReturnWaitsIsRecordedAndProtectedAndARealTaskStays`, `AHomeThreatLeavesEveryProposalAndRecordsNothing`, `AReturnThatWaitedLastTurnGoesAtOnceAndAnActiveDefenceWithdrawalNeverWaits` (+ прежние `AiLifecycleReturnPolicyTests`) |
| `TurnLoop` сообщает только `ReturnsMayWait`; при `false` операция не вызывается | условие `if (view.ReturnsMayWait)` перед вызовом; `HomeThreatened` внутри операции |
| `ReturnsDeferred` передаётся исходом итерации | `outcome.DeferReturns()` (Э1) |
| Четыре планировщика, `AttackPreparationPriority`, `HomeThreatened`/`SelectWaiting`/`RecordWait`/`MarkProtected` не вызываются из Orchestration | `OrchestrationDoesNotAssembleThePortfolioOrRunTheReturnRule` (скан всей папки, комментарии игнорируются; запись `.EffectiveValue =`) |
| `RadarValueScale` в cold остаётся | остаётся (`ColdAxisCount`) — отмечено как необходимая зависимость |

## Проверки

| Проверка | Результат |
|---|---|
| Managed (`e4-a-p`) | 2159 тестов, 1684 прошло, 475 упало; регрессий 0 относительно `e3-a-p`, 8 новых прошедших. Тесты портфеля используют наземный Scout (`ScoutCostModel` читает карту) — в managed-прогоне они проходят только в patched-прогоне (`patchrun.sh`), в Unity штатно |
| Мутации | 7 из 7 пойманы: нет `RecordWait`; нет `MarkProtectedThisTurn`; игнор угрозы дому; нет Radar-масштаба; неверный `AttemptId`; пропущен `AttackPreparationPriority` (ловит тест порядка шагов: в фикстуре нет Attack preparation, на выходе он не виден) |
| Compile | 28 = 28, новых 0 |
| Матрица зависимостей | 203 связи. `Orchestration` целиком: 125 → **122** типа (убраны `ReconMissionPlanner`, `AggressionMissionLayer`, `EconomyMissionPlanner`, `DevelopmentMissionPlanner`, `AttackPreparationPriority`, `ThreatModel`; добавлены `MissionPortfolio`, `MissionPortfolioResult`, `ReturnDeferral`). `Pipeline`: 80 → 78 типов, 13 папок. Новые связи: `Missions → Strategy/Diagnostics/Orchestration (Radar)`, `Continuity → Analysis (ThreatModel)/Provisioning (ProvisionEvent)` |

## Перепроверка: резервирование

- Ожидание возврата и построение портфеля **ничего не резервируют**: тесты проверяют, что строки `StrategicResourceReservationLedger` не появляются ни после `DeferReturnsBeforeTempo`, ни после `Build`.
- Ожидающие возвраты исключаются из набора **до** `BindFunding` и `Pack`, поэтому ни AP, ни claims на них не выделяются в этом проходе; после первого раунда Phase B они снова в наборе (`ReturnsMayWait == false`) — как и раньше.
- `LifecycleReturnPolicy.LastWait` — постоянная политика между ходами, **не** состояние хода: её не очищают `AiTurnSession`/lease-механизмы (поиск `LastWait` по `Assets/Scripts`: используют только `RecordWait`, `MayWait`, `ClearAll` при старте матча). Проверено тестом: ожидание на ходу 5 запрещает ожидание на ходу 6.
- `MarkProtectedThisTurn` пишет только `intent.LastProtectedTurn` (подавляет `StallTurns++` на этот ход) — не резерв.
- Не проверено: порядок AP-распределения Pack/BindFunding на реальном мире (S4 в Unity).

## Перепроверка: кеши

| Состояние | Писатель | Читатель | Область | Проверка |
|---|---|---|---|---|
| `DesireBreakdown` Recon lane pressures | `StrategyLayer.RefreshReconLanePressures` (внутри `Build`, перед планировщиками) | планировщики Recon / Aggression / Economy | вызов | порядок закреплён тестом шагов; снапшот — тот, что получил вызов |
| Aggression operational facts | кадр решения (`RefreshOperationalFrame`) | `AggressionMissionLayer` | кадр | `Build` их не обновляет (прежняя ветка не исполнялась); тест порядка запрещает `RefreshAggressionOperationalFacts` в файле |
| Proposals, `AttemptId` | `Build` | `RegisterProposals`, allocator | admission | `EveryBuildProposesFreshInstances`: экземпляры не переносятся между вызовами |
| `LastWait` | `RecordWait` | `MayWait`/`SelectWaiting` следующего хода | матч (постоянно) | тесты ходов 5/6 |
| `LastProtectedTurn` | `MarkProtectedThisTurn` | `ReconcileAfterTurn` | ход | штамп = ход ожидания; на следующий ход прежнее значение |
| Radar / `RadarValueScale` | не меняется внутри хода | `Build` | ход | неравномерный Radar в тесте: оценка ≠ тождество |
| Estimate-кеши, `KnowledgeVersion`, PathingVersion, WorldDelta | не затрагиваются | — | — | этап их не менял |

Не проверено: `AttackPreparationPriority.Apply` на реальном Attack-наборе (порядок вызова закреплён, поведение функции покрыто её собственными тестами).

## Цена изменения (до / после)

Новое семейство миссий или множитель ценности: раньше — `BuildMissionSet` (Orchestration) + планировщик; теперь — `MissionPortfolio` + планировщик (Missions); реальные execution/continuity-обработчики нового вида нужны по-прежнему. Новое правило срочности возврата: раньше — `Pipeline` + политика; теперь — Continuity (`LifecycleReturnPolicy` / `DeferReturnsBeforeTempo`), `TurnLoop` не меняется. Новые глобальные факты по-прежнему идут через старые точки fan-out.

## Повторная перепроверка Э4 (реализация, банк, кеши)

### Реализация (снизу вверх)

| Уровень | Проверка | Результат |
|---|---|---|
| Перенос файла | `git diff -M` | `LifecycleReturnPolicy.cs` и `.meta` — rename 100 %; GUID `cce6239d13ff71c4a8941022ae8ea0a2` до и после совпадает |
| `MissionPortfolio.Build` | построчное сравнение с телом `BuildMissionSet` на `4b4a4519` (текстовый diff без отступов и комментариев) | отличия ровно три: сигнатура, удалённая ветка `RefreshAggressionOperationalFacts` при `!aggressionPressureAlreadyRefreshed` (единственный вызывающий всегда передавал `true`), результат в `MissionPortfolioResult`; тело шагов идентично |
| Портфель | дифференциальный тест с транскрипцией прежнего метода на **реальных** `V2TraceScope` (по экземпляру на сторону) и вторым admission на том же scope | совпали составы, `AttemptId` (счётчик продолжается между admission одного хода), оценки, deferrals |
| Правило возвратов | **дифференциальный тест** `TheContinuityMethodReproducesTheInlineRuleOverManyScenarios`: 1500 сценариев, инлайн-блок `RunAdmissionIteration` (транскрипция) против `DeferReturnsBeforeTempo`; сравниваются сохранённые/ждущие предложения, deferrals, `MayWait` на тот же и следующий ход по каждой миссии, штампы `LastProtectedTurn`; охват: ждали > 200 сценариев, ActiveDefence остаётся > 100, угроза дому > 100 | совпало во всех |
| Проводка | условие `if (view.ReturnsMayWait)` перед единственным вызовом | `TheReturnDeferralIsCalledOnlyWhileTheLoopAllowsTheWait` (скан); мутация `if (true)` поймана |
| Мутации | убрать `Except`; подменить причину; убрать `RecordWait`/`MarkProtected`; игнор угрозы дому; условие проводки | пойманы все (по 1–3 теста каждая) |
| Устаревшие ссылки | комментарии, называвшие удалённый `BuildMissionSet` | исправлены в `ReactionRoundExecutor` и `ResourceAllocator` (текст) |

### Резервирование ресурсов через банк

Тест `AWaitingReturnLeavesItsApToPhaseBAndIsFundedAfterTheFirstRound` проходит реальную цепочку `DeferReturnsBeforeTempo` → `BindFunding` → `ResourceAllocator.BeginTurn/Pack` на реальном ledger (AP хода = 6; задача 2 AP, возврат 2 AP, Hard-тир):

| Момент | Funded | AP, закреплённый за funded | Свободно для Phase B | Строк в ledger |
|---|---|---|---|---|
| первый проход, возврат ждёт | только задача | 2 | 4 | 0 |
| после первого раунда (ожидание закончено) | задача + возврат | 4 | 2 | 0 |

Что это доказывает: ожидающий возврат не привязан к финансированию (`BindFunding` получает причину отложения, а не предупреждение «не материализован») и не удерживает AP; после раунда он финансируется тем же путём; резервов в банке ни ожидание, ни `Pack` не создают (финансирование tentative). Трасса `golden/S4_bank.jsonl` (3 записи) проходит `check_boundaries.py`.

Сквозная проверка банка на всех этапах: трассы `S1_bank` и `S9_bank`, снятые на коде Э4, **идентичны** золотым (`compare_traces.py`: 7 и 4 записи).

Не проверено: расходование реальных AP и `SpendAuthority` при исполнении возврата после раунда (Unity-fixture S4/S8).

### Кеширование: чтение и запись

| Состояние | Писатель | Читатель | Проверка этой перепроверки |
|---|---|---|---|
| Счётчик `AttemptId` (`V2TraceScope`) | `Build` | диагностика, корреляция | совпадает со старым методом, в том числе на втором admission хода |
| `DesireBreakdown` Recon lane pressures | `RefreshReconLanePressures` в `Build` | планировщики | порядок шагов закреплён тестом; снапшот — текущий на момент вызова (Pipeline вызывает `Build` после блока `!ownershipFresh → RefreshStrategicKnowledge + RefreshDecisionFrame`, порядок не менялся) |
| Aggression operational facts | кадр решения (`RefreshOperationalFrame`) | `AggressionMissionLayer` | `Build` их не пишет; прежний флаг всегда был `true` |
| `LastWait` (постоянно), `LastProtectedTurn` (ход) | `DeferReturnsBeforeTempo` | следующий ход / `ReconcileAfterTurn` | дифференциальный тест сверяет оба на тот же и следующий ход |
| Список предложений | `Build` | allocator, ledger | не переносится между вызовами (`EveryBuildProposesFreshInstances`) |
| Прочие кеши (estimate, `KnowledgeVersion`, PathingVersion, WorldDelta, `PoolCache`) | — | — | этап их не трогал |

Managed (`e4-a-p`): 2162 теста, 1687 прошло, 475 упало, регрессий 0 относительно `e3-a-p`, 11 новых прошедших. Unity и native — не выполнялись.

# Э5 — владелец кадра решения

Статус: **реализован — проверены доступными средствами (managed, compile); Unity и native не выполнялись.** Вход этапа — `2444710f`.

## Что изменено

| Файл | Изменение |
|---|---|
| `Orchestration/DecisionFrame.cs` (новый) | `DecisionFrame` хранит `Snapshot`, `Recon`, `Aggression`, `Intents`, `Commitments`, `Demands`, `PostCommitments` (чтение снаружи, присваивает только сам класс) и маркер свежести; `FrameServices` — шов к мировым владельцам (`WorldAnalysis`, `StrategyLayer`, оценщики объектив, `MissionContinuityLayer`, `AiTurnSession`, `DemandLayer`, `CombatOpportunityAnalyzer`); `Production(...)` привязывает его к реальным методам |
| `Orchestration/AiStrategyV2Pipeline.cs` | локали `snapshot`, `reconObjectives`, `aggressionObjectives`, `activeIntents`, `actorCommitments`, `demands`, `postCommitments`, флаг `ownershipFreshAfterPhaseA`, локальная функция `RefreshDecisionFrame` и статический `RefreshOperationalFrame` / `OperationalFrame` удалены; все чтения — `frame.X`, все присваивания — именованные операции |
| `Assets/Editor/AiDecisionFrameTests.cs` (новый) | 11 тестов |

**Отклонение от ТЗ (с обоснованием):** отдельного `DecisionFrameView.cs` нет. Свойства кадра имеют `private set`, поэтому снаружи они уже доступны только на чтение; отдельный тип-обёртка не добавил бы ограничения, только слой. Если вам нужен именно этот файл — это обёртка без изменения поведения.

## Инвентаризация: кто писал и читал (до этапа)

| Ссылка | Места присваивания в `RunTurn` | После |
|---|---|---|
| `snapshot` | скан; обновления после Phase A / повторного допуска / формирования / раунда Phase B / cold / Housekeeping; `ObserveSettled` после авиашага, шага миссии, раунда, cold, recall (≈13) | только `DecisionFrame` (скан остаётся локалью `scanned` до создания кадра и после не используется) |
| `reconObjectives`, `aggressionObjectives`, `activeIntents`, `actorCommitments` | старт, `RefreshDecisionFrame`, формирование, раунд Phase B (Recon) | только `DecisionFrame` |
| `demands` | старт, после Phase A, замена семейств, cold | `RebuildDemands`, `ReplaceDemandFamilies` |
| `postCommitments` | раунд Phase B, итоговое владение | `PrepareTempoOwnership`, `RefreshFinalOwnership` |
| `ownershipFreshAfterPhaseA` | старт (`phaseA.StateChanged`), повторный допуск (changed), cold (changed); сброс в итерации | `StartWithCredit`, `AcceptChangedReentry`, `AcceptChangedCold`, `PrepareAdmission` |

## Моменты хода → операции (порядок вызовов внутри операции — прежний)

| Момент | Операция | Рецепт |
|---|---|---|
| старт | `EnumerateObjectives`, `ResolveInitialOwnership`, `RebuildDemands` | Recon → Aggression; `ResolveActive` **с** turn context → `RefreshActors`; все оси (логи между операциями остались на местах) |
| первый Phase A изменил мир | `AcceptChangedPhaseA` + `RebuildDemands` | `RefreshStrategicKnowledge` → warm → Recon → факты Aggression → Aggression → `ResolveActive` (без ctx) → `RefreshActors`; затем все оси; кредит не выдаётся |
| начало итерации допуска | `PrepareAdmission` | без кредита: знание + операционное решение; кредит потрачен в любом случае |
| повторный допуск | `RefreshOperationalDecision`, `GenerateDemands`, `ReplaceDemandFamilies`, `AcceptChangedReentry` | при изменении мира — знание + решение + кредит |
| шаг миссии / авиации / раунд / recall | `ObserveSettled` | `WorldAnalysis.ObserveSettled` |
| сформировано крыло | `RefreshAfterFormation` | знание → Recon → `ResolveActive` (без ctx) → `RefreshActors`; без warm и без Aggression |
| раунд Phase B | `PrepareTempoOwnership` | знание → Recon → ownership постоянного состояния; без `ResolveActive` |
| cold | `PrepareColdResidual`, `GenerateDemands`, `AcceptChangedCold` | знание + решение каждый раз; при изменении: observe → решение → все оси → кредит |
| конец | `RefreshFinalOwnership`, `AcceptHousekeeping` | ownership постоянного состояния; знание после Housekeeping при `StateChanged` |

Политика не менялась: полный refresh в тех же точках; пропуска «ревизия не изменилась» нет (`ResolveActive` и `RefreshActors` — не чистые чтения).

## Сверка с требованиями Э5

| Требование ТЗ | Результат |
|---|---|
| Полный список писателей/читателей до переноса | таблица выше; 7 ссылок и флаг сведены к одному классу |
| Операции по текущим точкам, без setters наружу | 17 операций; `private set`; скан `RunTurnHoldsNoSharedFrameLocalsAndAssignsNothingOfTheFrame` (нет прежних локалей и присваиваний `frame.X =`) |
| Порядок точек вызова операций в `RunTurn` | `RunTurnNamesEveryMomentOfTheFrameInTheBaselineOrder`: 22 вызова в порядке файла = порядку прежних встроенных присваиваний |
| Warm перед потребителем, нет пропуска по ревизии | `TheOperationalRefreshWarmsFirstAndNeverSkipsAnyStage` (повторный вызов с тем же снапшотом снова исполняет все стадии) |
| Кредит только там, где выставлялся | `AnAdmissionRefreshes…`, `OnlyAChangedReentryAndAChangedColdResidualGrantTheCredit` |
| Частичные рецепты формирования и Phase B не превращены в полный refresh | `TheFormationAndTheTempoRoundUseTheirOwnPartialRecipes` |
| Следующий читатель видит обновлённый кадр | `ARefreshedSnapshotIsWhatTheNextReaderSees`, `EveryStepOfTheRecipeConsumesWhatThePreviousStepProduced` (каждый сервис получает объекты предыдущего шага, не устаревшие) |
| Initial `ResolveActive` с ctx, operational без ctx | закреплено тестом старта и операционного refresh |
| Рецепт остаётся в Orchestration, нижние слои не зависят от кадра | `DecisionFrame` в `Orchestration/`; потребителей в Analysis/Strategy/Missions/Provisioning нет (им по-прежнему передаются предметные данные, не кадр) |

## Проверки

| Проверка | Результат |
|---|---|
| Managed (`e5-a-p`) | 2173 теста, 1698 прошло, 475 упало; регрессий 0 относительно `e4-a-p` (+11) **и относительно исходного baseline `717871b8`** (1649 → 1698, +49 новых прошедших; baseline пересоздан и совпал с первоначальным 1649 / 474) |
| Мутации | 7 из 7 пойманы: нет проверки кредита; кредит не выдаётся при повторном допуске; пропущен warm; `ResolveActive` с ctx в операционном refresh; `ResolveActive` в раунде Phase B; перестановка стадий; в `RunTurn` подменена операция (`AcceptChangedReentry` → `AcceptChangedPhaseA`) |
| Compile | 28 = 28, новых 0 |
| Матрица зависимостей | 203 связи. `Orchestration` целиком: 122 → 122 (типы переехали из `Pipeline` в `DecisionFrame` внутри той же папки). `Pipeline`: 78 → **71** тип (ушли `DemandLayer`, оба оценщика объектив, `DesireBreakdown`, `ReconObjective`, `MissionIntent`, `ActorCommitments`). Как и предупреждало ТЗ, **межслойная связность Э5 не уменьшает**; уменьшается число писателей общих данных и знание тел работ о порядке обновления |
| Лог-/банк-трассы | `golden/S1_bank`, `S9_bank`, `S4_bank` на коде Э5 идентичны золотым (`compare_traces.py`) |

## Перепроверка: резервирование ресурсов через банк

- Кадр **не пишет банк**. Единственные операции, которые влияют на резервы, — прежние `MissionContinuityLayer.ResolveActive` (может retire/rekey intents и тем самым снять строки владельца) и `AiTurnSession.RefreshActors` (проекция lease). Их вызовы вынесены в операции без изменения аргументов и порядка; последовательность вызовов и **идентичность передаваемых объектов** закреплены тестами. Нельзя доказывать этап фразой «кадр не пишет банк», поэтому эффект retire/rekey на строки проверяется прежними `AiMissionLeaseLifecycleTests` / `AiEconomyDecisionTests.ReturnBuilder_*` (не менялись, проходят) и банковскими трассами S1/S4/S9 (идентичны).
- Пропуск refresh «по ревизии» отсутствует: стейл-intent при той же ревизии мира по-прежнему проходит `ResolveActive` на каждом операционном refresh (тест повторного вызова).
- Carried reservation (`phaseB.Reservation ?? phaseA.Reservation`) не входит в кадр и по-прежнему читается в момент использования.
- Не проверено: сквозной тест «retire через `ResolveActive` → строки ledger → rollback» на реальном мире (Unity-fixture S5/S9); сервисы кадра с реальным миром — engine-bound.

## Перепроверка: кеширование на чтение и запись

| Состояние | Писатель | Читатель | Область | Проверка |
|---|---|---|---|---|
| Ссылки кадра (`Snapshot`, `Recon`, `Aggression`, `Intents`, `Commitments`, `Demands`, `PostCommitments`) | только `DecisionFrame` | тела работ `RunTurn`, `AdmissionKey`, `TakeTypedSplit` (читают в момент вызова через `frame.X`, не захватывают значение на ход) | ход | скан: нет присваиваний и прежних локалей вне кадра; поток данных проверен тестом |
| Маркер свежести (кредит) | `StartWithCredit`, `AcceptChangedReentry`, `AcceptChangedCold` | `PrepareAdmission` (тратит) | ход | три писателя + сброс = как в базе, теперь в одном классе |
| Estimate-кеш (`WorthIt`), `PoolCache` боевого анализа | `CombatOpportunityAnalyzer.WarmEstimates` | анализаторы | область `AiTurnController` | `WarmEstimates` вызывается перед каждым операционным refresh (тест порядка); второй scope не открывается |
| `KnowledgeVersion` / карта / pathing | `WorldAnalysis` | `WorldAnalysis` | — | кадр не дублирует выбор Scan / operational / full: просто вызывает `RefreshStrategicKnowledge` и `ObserveSettled` |
| `WorldDelta` | `ObserveSettled`/`Publish` | аудит | — | не затронуты |
| Aggression operational facts | `RefreshAggressionFacts` внутри операционного refresh | оценщик объективов, планировщик (Э4) | кадр | вызывается только из `RefreshOperationalDecision` |

Не проверено: семантика «та же `KnowledgeVersion`, изменились HP/карта/рука» на реальном мире (S5 — Unity-fixture); кадр хранит только ссылки и никогда не принимает решений по номеру ревизии.

## Цена изменения (до / после)

Новая составляющая кадра (например, перечень Development-возможностей на кадр): раньше — рецепт `RefreshDecisionFrame`, места присваивания и проверка флага свежести во всех читателях; теперь — поле и шаг в `DecisionFrame` плюс реальный потребитель нового факта. Один класс вместо четырнадцати функций, но новый факт по-прежнему требует producer и consumer; доменное последствие остаётся у своего владельца.

## Э5: доработка по сверке с ТЗ и повторная перепроверка

Сверка с ТЗ §10 после первой версии показала расхождения; ниже — что исправлено, что проверено и что остаётся.

### Что исправлено

| Расхождение | Исправление |
|---|---|
| Нет `DecisionFrameView` | добавлен `readonly struct DecisionFrameView` (значения ссылок на момент вызова) и `DecisionFrame.View`; свойства кадра теперь отдают `IReadOnlyList<…>` для списков (`Recon`, `Aggression`, `Intents`, `Demands`), присваивание — `private set`; потребители в `Pipeline` компилируются без изменений (все принимают `IReadOnlyList`). Не deep-immutable: сами intents и leases остаются изменяемыми у владельцев |
| Четыре неиспользуемых поля (`_ctx`, `_player`, `_root`, `_hand`) | удалены; конструктор `DecisionFrame(snapshot, services)` |
| Инвентаризация не включала `CapacityUnlock` и `DeferredFlush` | см. ниже: оба идут через `ReenterStrategicAxes` и используют `RefreshOperationalDecision` / `AcceptChangedReentry`; собственных присваиваний ссылок кадра не имеют |
| Перенос рецептов пакетно, без пошаговой трассы | добавлен **дифференциальный тест состояния**: `TheFrameFollowsTheInlineLocalsAndFlagOverRandomWalksOfTheTurn` — транскрипция прежних встроенных локалей, флага и рецептов (`RefreshDecisionFrame`, формирование, раунд, cold, итог) против `DecisionFrame` на одинаковых скриптовых сервисах, 3000 случайных прогулок по моментам хода (5–30 шагов, ~50 тыс. сравнений); после каждого шага равны последовательности вызовов сервисов, в конце — теги ссылок (какой вызов произвёл снапшот, objectives, intents, demands) и состояние кредита (проба `PrepareAdmission`). Мутации: сброс кредита, потерянная перегенерация demands в cold, лишний refresh в формировании — пойманы |
| Формулировка «банковские трассы идентичны» | уточнена: трассы `S1/S4/S9` — это регрессия ledger, **кадра они не проходят**; для Э5 основание — тест ниже |

### Банк через кадр

`TheOperationalRefreshRetiresAStaleIntentAndReleasesOnlyItsOwnRows` (реальные: `DecisionFrame`, `AiTurnSession`, lease-book, `MissionIntentState.Remove`, ledger):

| Момент | Строки ledger |
|---|---|
| старт: устаревшая операция A (Materials 3) и чужая операция (Energy 2) | 2 |
| операционный refresh кадра | A снята, чужая операция осталась (Energy 2) |
| тот же снапшот (ревизия не изменилась), добавлена вторая устаревшая операция C (Human 1) | 2 |
| повторный операционный refresh | C снята — refresh **не пропущен**, чужая осталась |
| конец хода (`CompleteReservations`) | 0 |
| начало следующего хода | 0 |

Вариант `useRealResolve = false`: политика resolve смоделирована (операция без живого актора удаляется), путь retire (`MissionIntentState.Remove` → lease → ledger) и кадр — реальные; проходит. Вариант `useRealResolve = true` вызывает реальный `MissionContinuityLayer.ResolveActive`, который сравнивает `UnityEngine.Object` (`ctx?.Map == null`), поэтому в managed-прогоне он помечен **Inconclusive** и исполняется только в Unity. Трасса `golden/E5_frame_bank.jsonl` проходит `check_boundaries.py`.

Не проверено без движка: порядок строк при rollback канонической операции и transfer/rekey на реальном мире (S5/S8/S9 в Unity).

### Кеши (повторно)

Запись/чтение ссылок кадра, кредит, estimate-кеши, `KnowledgeVersion`, `WorldDelta`, факты Aggression — как в таблице выше; новое: списки отдаются как `IReadOnlyList` (внешний код не может присвоить ссылку и не получает изменяемый `List`), `View` — значение на момент вызова; поток данных между шагами рецепта проверен тестом (`EveryStepOfTheRecipeConsumesWhatThePreviousStepProduced`) и дифференциальным тестом (теги производящих вызовов совпадают с прежними).
Не проверено на мире: «та же `KnowledgeVersion`, изменились HP / карта / рука» (S5) — кадр решений по номерам ревизий не принимает.

### Инвентаризация: недостающие точки

`CapacityUnlock` (после первого Phase A) и `DeferredFlush` / `TerminalForce` / `Trigger` — причины `ReenterStrategicAxes`; ссылки кадра они меняют только через `RefreshOperationalDecision` и `AcceptChangedReentry` (тест порядка точек), собственных присваиваний нет.

### Остаётся вне Э5

- `ReactionRoundExecutor` повторяет рецепт кадра собственным кодом (объективы, `RefreshAggressionOperationalFacts`, `ResolveActive`, `RefreshActors`): знание рецепта у одного класса достигнуто в пределах `RunTurn`, не в Reaction. Объединение меняет код вне объёма этапа и требует отдельного решения.
- Межслойная связность Э5 не уменьшает (ТЗ это предусматривает): `Orchestration` 122 → 122, `Pipeline` 78 → 71.
- Unity EditMode/PlayMode, нативная партия, S5/S8/S9 на реальном мире — не выполнялись.

### Итог проверок после доработки

Managed (`e5fix-a-p`): 2176 тестов, 1700 прошло, 475 упало; регрессий 0 относительно исходного baseline `717871b8` (1649 → 1700, +51) и относительно первой версии Э5 (+2); 1 Inconclusive (реальный `ResolveActive`). Compile 28 = 28. Банковские трассы `S1/S4/S9` идентичны золотым. Матрица: 203 связи, `Orchestration` 122 типа, `Pipeline` 71.
