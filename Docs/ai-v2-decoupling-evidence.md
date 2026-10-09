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

`Assets/Editor/AiTurnLoopTests.cs` уже содержит независимую транскрипцию исходного цикла (`Baseline`, снята с `c3cdde46`, до L4) и генератор 20 000 многошаговых сценариев против реального `TurnLoop.Run`. Для Э1 тест расширяется (исходы тел вместо записи состояния), второй похожий тест не создаётся. Транскрипция «текущего» `Run` как эталона для Э6 совпадает с этой, так как порядок переходов L4→L5 не менялся (код цикла после `f46e4529` менялся только в K1/D-правках L5).

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
**Не сделано и почему:** S3 (бюджеты assignment/reprice) требует `ResourceAllocator` с миром и армиями; S5 (кеши знания, `ResolveActive` на same revision) — `WorldSnapshot` и карту; S8 (совместные Economy+Attack+Reaction, rollback канонической операции) — игровой корень и движок; S6/S7 в части цикла закрыты `AiTurnLoopTests` (20 000 сценариев), в части реальных тел — нужны мир и движок. Managed-прогон их не построит без Unity-объектов; они пишутся как PlayMode/EditMode-fixtures на входной ревизии этапа, который их использует (S3→Э3, S5→Э5, S6/S7→Э6, S8→Э2/Э6), и запускаются в Unity владельцем. До этого соответствующий этап не стартует. Расхождений baseline с требованиями ТЗ при чтении не найдено; полный разбор S1–S9 на поведение в игре не проводился.

## 4a. Матрица зависимостей на `717871b8`

Скрипт `Tools/ai-v2-decoupling-verify/coupling_matrix.py --rev 717871b8` (в репозитории; метод тот же, что у `deps.py`): 289 файлов, **203** связи между папками `Ai/V2` — историческое число воспроизведено. `AiStrategyV2Pipeline.cs`: 14 папок, **84** внешних типа (как в отчёте сравнения). Вся папка `Orchestration/`: 16 папок, **122** внешних типа (в сравнении было 116→118 «без файлов моделей»; методика отличается — для сравнения этапов использовать только значения этого скрипта на одной и той же методике). Полный вывод: `D:/aiv-work/coupling/d0-717871b8.txt`. Это счёт ссылок на типы, не граф вызовов; вспомогательная метрика.

## 5. Коррекции документов

- `Docs/ai-v2-pipeline-simplification-level5.md`: заголовок статуса (итоговая нативная партия выполнена, §12; перечислено, что она не покрыла), строки таблицы §9 и пункта 3 §11.5; добавлено, что единственный владелец переходов есть, единственного писателя счётчиков нет. Исторические результаты тестов не менялись.
- `TurnLoop.cs`: комментарий над `TurnLoopState` приведён к тому же (без изменения кода).
- `ARCHITECTURE.md` («единственный владелец переходов») верно и не менялось.

## 6. Что не выполнено

Unity EditMode/PlayMode — берёт на себя владелец; нативные ветки (cold, rebase, stall, Phase B после Reaction, `PhaseBStateChanged`, срочные возвраты, лимиты) — не блокируют, остаются не наблюдавшимися. Остальные fixture S1–S9 (§4, колонка «Чего нет») пишутся в начале этапов. Скрипты `run.sh`, `patchrun.sh`, `regress.py` лежат только в `D:/aiv-work`.
