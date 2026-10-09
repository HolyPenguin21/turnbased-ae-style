# AI V2 — развязка архитектурных уровней: итоговая приёмка (Э0–Э6)

Ветка `refactor/ai-v2-decoupling`, база `717871b824106ae282f59a73c2bdc4cdd38254e9` (= `origin/master`), последний код — `3569d9e6`. Unity 6000.5.4f1 (`ProjectSettings/ProjectVersion.txt`).

**Статус: реализован — проверен доступными средствами (managed, compile, статический анализ, дифференциальные тесты); Unity EditMode / PlayMode и нативные партии не выполнялись.** Полная приёмка упрощения и развязки остаётся открытой до прогона в Unity. Коммиты локальные; push и merge не выполнялись.

Подробные доказательства по этапам — `Docs/ai-v2-decoupling-evidence.md`. Инструменты — `Tools/ai-v2-decoupling-verify/`. Карта ответственности — `Assets/Scripts/Ai/V2/ARCHITECTURE.md` (раздел «Responsibility map after the decoupling»).

## 1. Контрольные точки

| Этап | SHA | Содержание |
|---|---|---|
| Э0 | `7fdc25e8`, `d1910997`, `b9352725`, `e62b624b` | baseline, коррекции L5, инструменты трасс, recorder, фикстуры S1/S2/S4/S9, матрица зависимостей, сверка ссылок ТЗ |
| Э1 | `663d075c`, `95e5eac2` | `TurnLoop` — единственный писатель `TurnLoopState` (view + outcome итерации) |
| Э2 | `cdf9fd9d`, `19c80a86` | банк узнаёт моменты хода (`StrategicTurnLifecycle`), stall у `AviationObligations` |
| Э3 | `87fe3cf9`, `4b4a4519` | `ProvisionNext` и `PassParking` в Provisioning, дифференциальный тест протокола |
| Э4 | `1a2643d8`, `2444710f` | `MissionPortfolio` (Missions), правило возвратов в Continuity (`LifecycleReturnPolicy` перенесён с `.meta`) |
| Э5 | `84ae9f61`, `e1bc54f5` | `DecisionFrame`, дифференциальный тест состояния, банковский тест через реальный путь retire |
| Э6 | `da9791b5`, `3569d9e6` | компоненты работ вместо замыканий `RunTurn`, очистка неиспользуемого кода, тест порядка вызовов |

## 2. Изменённые файлы

`git diff --shortstat 717871b8..HEAD`: 65 файлов, +5936 / −915. По областям: `Assets/Scripts` 37 файлов (+1789 / −900), `Assets/Editor` 17 (+2987 / −12), `Tools` 9 (+494), `Docs` 2.

Новые производственные типы (все обоснованы в evidence): `StrategicTurnLifecycle` (Strategy), `PassParking`, `ProvisioningSelectionOutcome` + partial `ProvisioningManager.Selection` (Provisioning), `MissionPortfolio` (Missions), `MissionContinuityLayer.ReturnDeferral` (Continuity), `DecisionFrame`, `StrategicReadmissionRunner`, `AdmissionIteration`, `TempoRound`, `ColdResidual`, `TurnPhaseState` (Orchestration). Перенесён: `LifecycleReturnPolicy` Orchestration → Continuity. Удалён: `TurnLoopWork`, `BuildMissionSet`, `OperationalFrame` / `RefreshOperationalFrame`, 14 локальных функций `RunTurn`.

## 3. Что устранено и что осталось необходимым

### 3.1 Устранено

| Вид | До (`717871b8`) | После (`3569d9e6`) |
|---|---|---|
| Доменные типы, на которые ссылалась Orchestration | `EconomyReservationLifecycle`, `OperationContinuationWindow`, `AviationObligationStallRegistry`, `CapabilityPoolExhaustionRegistry`, `ReconMissionPlanner`, `AggressionMissionLayer`, `EconomyMissionPlanner`, `DevelopmentMissionPlanner`, `AttackPreparationPriority`, `ProvisionFailure`, `ProvisioningResult`, `ProvisionDisposition`, `ThreatModel` (13) | ни одного (проверено сканом всей папки: executable-код без комментариев) |
| Прямые вызовы стадий банка из `RunTurn` | `Reconcile…` ×3, `ReleaseDeferredEconomyIncomeCover`, `Settle`, `SetMobilizationOpen`, `MarkStalled` | 4 именованных момента (`ObserveInitialForce`, `AfterMissionSettlement`, `BeforeFirstTempo`, `BeforeTempoSpend`) + `RecordSettledStep` у владельца обязательств |
| Протокол подбора миссии (`while`, два бюджета realloc, парковка) | в `RunAdmissionIteration` | `ProvisioningManager.ProvisionNext` + `PassParking` |
| Сборка и оценка набора миссий | `BuildMissionSet` (4 планировщика, `EffectiveValue`, приоритет Attack preparation) | `MissionPortfolio.Build` |
| Правило ожидания возвратов | инлайн в `RunTurn` (4 вызова Continuity) | `MissionContinuityLayer.DeferReturnsBeforeTempo` |
| Писатели `TurnLoopState` | тела работ (9 мест записи) + `TurnLoop` | только `TurnLoop` (скан исходников) |
| Писатели ссылок кадра | 6 локалей + флаг свежести, ≈25 мест присваивания в 14 локальных функциях | только `DecisionFrame` (17 операций) |
| Делегаты и замыкания, через которые работы получали общее состояние `RunTurn` | `TurnLoopWork` — 7 делегатов, 14 локальных функций, `reentryStateChanged`, `stepTriggers` как переменные-замыкания | 0 локальных функций; в `RunTurn` остались 2 обычных callback исполнителей (`formedWing`, `recallChanged`) |
| `AiStrategyV2Pipeline.cs` | 1224 строки; 84 внешних типа из 14 папок | 440 строк; 41 внешний тип из 10 папок |
| Знание порядка обновления кадра в телах работ | распределено по телам | в операциях `DecisionFrame` |

### 3.2 Осталось необходимым (по ТЗ §3) и новые остатки

| Связь | Почему |
|---|---|
| Orchestration → `StrategicManager.FulfillDemands/UseSurplus` и 4 момента хода | пакеты Phase A/B; оркестратор выбирает момент |
| Orchestration → `ResourceAllocator`/`AllocationSession`, executors, `ProvisionNext` | одна конкуренция миссий; исполнение шага |
| Orchestration → `WorldAnalysis` (через `DecisionFrame`) | наблюдение после мутации — граница шага |
| Orchestration → Continuity ledger / `SettleStep` | порядок «публикация → ledger → settle» закреплён ТЗ |
| Housekeeping → Reaction → `UseSurplus` | вне объёма; порядок хвоста хода закреплён тестом |
| Статические реестры хода (`StrategicInterruptRegistry`, ledger, `WorldDeltaLifecycle`, `StrategicTempoBudget`) | единственные владельцы фактов, ревизий, резервов и бюджета |
| `RadarValueScale` в cold, `AttackForceReadiness` в `DevelopmentAdmission` | необходимые зависимости (ключ допуска — у владельца оси) |
| Ключи допуска `Development/Aggression/EconomyAdmission` в Orchestration | ТЗ оставляет; перенос policy owners — отдельное расширение |
| Ворота `StrategicReadmission.Decide` получают 3 делегата; `TypedTriggerFanOut.Split` — лямбду готовности строителя | нужны для ленивости; это не замыкания `RunTurn`, но и не прямые вызовы из ТЗ |
| `ReactionRoundExecutor` повторяет рецепт кадра и цикл повторного подбора | вне объёма; общий протокол с `ProvisionNext`/`DecisionFrame` — возможное продолжение |

Ожидаемо (ТЗ): **число связей между папками не уменьшилось** — 203 → 203; часть операций поменяла вызывающего (`Strategy → State`, `Recon → State`, `Missions → Strategy/Diagnostics`, `Continuity → Analysis/Provisioning`, `Provisioning → Allocation/State/Diagnostics`). `Orchestration` целиком: 127 → 122 внешних типа (13 ушли, 8 типов нового контракта добавились). Цель — что каждое знание принадлежит одному владельцу, а не число стрелок.

## 4. Типовые расширения: до и после

Правка — файлы / контракты, которые надо менять; «новая механика» — то, что остаётся необходимым.

| Этап | Новый вид работы | Новый факт | Новое доменное последствие |
|---|---|---|---|
| Э1 | счётчик no-progress: `TurnLoop` + 3 тела работ → `NoProgressAfter` / `ApplyIterationOutcome` в `TurnLoop.cs` | producer/consumer не меняются | банковские правила прежние |
| Э2 | новое удержание на прежнем событии: `Pipeline` + Strategy/State → `StrategicTurnLifecycle` / `EconomyReservationLifecycle` | авиационный исход: ветка stall в `RunTurn` → `AviationObligations` (Recon) | новая стадия резервации: вызовы в `Pipeline` → реакция Strategy на прежнее событие |
| Э3 | новая disposition provisioning: `while` в `Pipeline` + Provisioning (+State) → `ProvisioningManager.Selection` (+ реестр State); потребитель исхода не меняется | новое доказательство разблокировки пула: фильтр/реестр в `Pipeline` → реестр State + Provisioning | новый отказ/парковка: схема журнала `ProvisionEvent` стабильна |
| Э4 | новое семейство миссий: `BuildMissionSet` + планировщик → `MissionPortfolio` + планировщик (исполнитель и continuity нового вида нужны по-прежнему) | новый множитель ценности: рецепт в `Pipeline` → Missions | срочное исключение возврата: `Pipeline` + политика → Continuity; `ReturnsMayWait` прежний |
| Э5 | новый читатель кадра: доступ к замыканию + протокол свежести → свойства `DecisionFrame` | новая составляющая кадра: присваивания + проверка флага → поле/шаг `DecisionFrame` + реальный producer/consumer | retire/rekey по-прежнему Continuity |
| Э6 | новый этап хода: enum / `Phase` / ветка / делегат / closure / проводка → enum / `Phase` / ветка + интерфейс работы + компонент (одна строка сборки) | новый global reason: producer → typed mapping → consumer; тела работ неизменны | новое последствие на прежнем событии — у Strategy / Continuity / Recon |

Если контрольное изменение требует новой ветки в Orchestration, этап не доказал локализацию; изменение порядка хода или настоящий новый producer/consumer — закономерная правка. «Новая миссия меняется в одном уровне» не утверждается: её исполнение и continuity остаются.

## 5. Банк: численное сравнение на одинаковых фикстурах

Фикстуры написаны до правок кода своих этапов, ожидания заданы правилами резервов; трассы `golden/*.jsonl` на коде каждого этапа **идентичны** (`compare_traces.py`, 4 фикстуры; проверялись на коде каждого этапа начиная с появления фикстуры и после очистки Э6). Значения — резервы в ledger в точке записи (до/после каждой операции):

| Фикстура | Точка | AP | H | E | M | T | Строк |
|---|---|---|---|---|---|---|---|
| S1 (завершение Economy) | старт хода | 0 | 0 | 0 | 0 | 0 | 0 |
| | после резервов двух владельцев (4 + 3 AP) | 7 | 2 | 2 | 3 | 1 | 6 |
| | после перехода владельца А «не успевает» (снят только его AP) | 3 | 2 | 2 | 3 | 1 | 5 |
| | повторный refresh (идемпотентно) | 3 | 2 | 2 | 3 | 1 | 5 |
| | конец хода / начало следующего | 0 | 0 | 0 | 0 | 0 | 0 |
| S9 (retire владельца) | после резервов двух владельцев | 6 | 0 | 1 | 2 | 0 | 4 |
| | после снятия владельца А | 2 | 0 | 1 | 0 | 0 | 2 |
| | следующий ход другого/того же игрока | 0 | 0 | 0 | 0 | 0 | 0 |
| S4 (ожидающий возврат; spendable AP = 6) | возврат ждёт, funded только задача | spendable 6 → 4 | — | — | — | — | 0 |
| | после первого раунда: возврат и задача | spendable → 2 | — | — | — | — | 0 |
| E5 (кадр, реальный retire) | старт: операция A (M 3) и чужая (E 2) | 0 | 0 | 2 | 3 | 0 | 2 |
| | refresh: A снята, чужая остаётся | 0 | 0 | 2 | 0 | 0 | 1 |
| | та же ревизия, новая устаревшая C (H 1) → refresh снимает C | 0 | 0 | 2 | 0 | 0 | 1 |
| | конец хода / начало следующего | 0 | 0 | 0 | 0 | 0 | 0 |

Дополнительно: тесты `AiProvisioningSelectionParityTests` (1500 сценариев) сверяют состояние аллокатора после выбора (repriced floors, rejected, locked claim, `ApClaimed`) с исходным циклом; `AiMissionPortfolioTests` — цепочку `DeferReturnsBeforeTempo → BindFunding → Pack` на реальном ledger.

**Не покрыто без движка:** rollback канонической операции без расхода и без committed receipt, transfer/rekey на реальном мире, Phase B после Reaction, совместные Economy + Attack preparation + Reaction (S8), реальные `Provision*` и `SpendAuthority` — это Unity-фикстуры. Численное «до/после каждого guard» на реальном мире не выполнено.

## 6. Кеши: писатель, инвалидация, чтение

| Состояние | Писатель | Инвалидация / обновление | Момент чтения | Проверка |
|---|---|---|---|---|
| Ссылки кадра (`Snapshot`, objectives, intents, commitments, demands) | `DecisionFrame` (единственный) | операции кадра в прежних точках; пропуска «по ревизии» нет | компоненты читают `_frame.X` в момент использования | скан; дифференциальный тест на 3000 прогулок; тест потока данных |
| Кредит свежести | `DecisionFrame` | выдаётся при изменившемся повторном допуске / cold, тратится `PrepareAdmission` | начало итерации | дифференциальный тест |
| Pending-факты | реестр через `AiTurnSession` | снимок → fan-out всем → consume только снятого | `StepTriggerSequence` | интеграционный тест на реальной сессии (rebase 1 пара, остальные 2) |
| Исчерпание пула / RetryNextTurn | `CapabilityPoolExhaustionRegistry` через `PassParking` и `ProvisionNext` | `BeginTurn`/`EndTurn` в `AiTurnSession`; множество прохода — новый `PassParking` на `OpenPass` | фильтр следующего допуска, `CanAttempt` | дифференциальный тест (1500 сценариев, мутации) |
| `LastWait` (между ходами), `LastProtectedTurn` (ход) | `DeferReturnsBeforeTempo` | `ClearAll` при старте матча | следующий ход / `ReconcileAfterTurn` | дифференциальный тест (1500 сценариев) |
| `AttemptId` | `MissionPortfolio.Build` | новый `V2TraceScope` на ход | диагностика | совпадает со старым методом, включая второй admission |
| Estimate-кеши (`WorthIt`), `PoolCache`, `KnowledgeVersion`, PathingVersion, WorldDelta | прежние владельцы | прежние scope | `WarmEstimates` перед каждым операционным refresh (тест порядка) | этапы их не меняли |

Равенство ревизий не доказывает содержимого ключа кеша; чтения реестров проверялись по значениям, а не по номерам версий. Семантика «та же `KnowledgeVersion`, изменились HP / карта / рука» (S5) — Unity-фикстура; кадр решений по номерам ревизий не принимает.

## 7. Внутриходовой цикл и последовательности работ

| Требование | Подтверждение |
|---|---|
| Начальный Phase A до `TurnLoop`; повторный допуск и cold — отдельные протоколы | порядок вызовов `RunTurn`, `EachWorkAndTheTailOfTheTurnKeepTheirOrderOfCalls` |
| Tempo → Cold, cold один раз; изменивший мир cold открывает проход при `Stage = Done` | `AiTurnLoopTests`: транскрипция baseline, 20 000 сценариев, hand-fixed трассы |
| `TerminalForce` на каждом закрытии прохода, включая bounded/no-progress | те же тесты (счётчики на границах, `boundedMax`/`boundedNoProgress`) |
| Пары: rebase 1, recovery / миссия / раунд 2; факт первого reentry сохраняется | `AiStepTriggerSequenceTests`, `S2_Triggers_*`, интеграционный тест на реальной сессии |
| Срочные возвраты (ActiveDefence, tactical retreat, угроза дому) обходят ожидание; `LastWait` исключает два хода подряд | `AiLifecycleReturnPolicyTests`, `S4_Returns_*`, дифференциальный тест правила |
| Миссия публикует наблюдение до ledger; авиация без ledger/settle/observer | тест порядка (шаг миссии / шаг авиации), текстовый diff при переносе |
| `SettleAfterTurn → Housekeeping/Reaction → Phase B → AuditTurnEnd → CompleteReservations`; Reaction внутри `RunTurn` | тест порядка хвоста; код Housekeeping/Reaction не менялся |
| Carried reservation читается в момент использования | `TheCarriedReservationIsReadAtEveryUseNeverCached` |
| Yield 8 мс и разворачивание вложенных `IEnumerator` | порог и пейсинг перенесены без изменений (`AdmissionIteration`), драйвер разворачивает вложенные корутины без лишнего кадра |

## 8. Проверки

**Выполнено (доступными средствами):**

| Проверка | Результат |
|---|---|
| Managed-прогон (net472 NUnitLite под Mono из Unity, старые Unity DLL и заглушки) | исходный baseline `717871b8` пересоздан: 2123 теста, 1649 прошло, 474 упало (совпал с первоначальным). Финал: **2186 тестов, 1710 прошло, 475 упало**, 1 Inconclusive; регрессий 0, новых прошедших 61; 475-й упавший — тест `OnlyTheClosingOfTheOrdinaryPassesSettlesTheContinuationWindow`, которому нужен движок (`PlayerRoot`) |
| Compile (`Tools/ai-verify/compile_check.sh` + сравнение наборов) | 28 = 28 ошибок, новых 0 (все вне `Scripts/Ai`) |
| Прямые чтения ресурсов (`AiRawResourceReadRatchetTests`) | сообщение идентично baseline (единственное нарушение — прежнее, `ActiveDefenceExecutor.cs`) |
| Дифференциальные тесты | цикл 20 000 сценариев; `ProvisionNext` 1500 сценариев; правило возвратов 1500; кадр 3000 прогулок (~50 тыс. сравнений); портфель против транскрипции |
| Мутации | в каждом этапе проверена чувствительность тестов: Э1 — 2; Э2 — 3; Э3 — 5 + 9 + 3; Э4 — 7 + 3; Э5 — 7 + 3; Э6 — 5 + 4. Не пойманных мутаций в итоговых наборах нет (в процессе были найдены непойманные — пропуск Attack preparation в портфеле, `if (true)` в проводке возвратов, граница `<=` пакета Scout — и закрыты отдельными тестами) |
| Банковские трассы и проверка границ | `S1`, `S4`, `S9`, `E5_frame_bank` идентичны золотым на всех этапах; `check_boundaries.py` ok; самопроверка инструментов (5 негативных фикстур) проходит |
| Матрица зависимостей | воспроизведена историческая 203; метод исправлен (выражения интерполированных строк — код) |

**Не выполнено:**

- Unity EditMode (полный набор) и PlayMode; сравнение 474 тестов, падающих в managed-прогоне, с baseline **в Unity** — статус у владельца.
- Нативные партии на итоговом коде; ветки cold, rebase, stall, Phase B после Reaction, `PhaseBStateChanged`, срочные возвраты, лимиты — не наблюдались (решение владельца: не блокируют).
- Unity-фикстуры S3 (реальные `Provision*`), S5 (кеши на реальном мире, `ResolveActive` при той же ревизии), S6, S7 (реальные тела), S8 (совместные Economy + Attack preparation + Reaction, rollback), реальный вариант `ResolveActive` в тесте банка кадра (Inconclusive в managed).
- `AdmissionIteration.OpenPass`, `TempoRound.SettleBeforeFirstRound` в managed не исполняются (движок).
- `Tools/ai-verify/test_regress.sh` не запускался (нет `mono` в PATH): использован эквивалент через скрипты в `D:\aiv-work` (по решению владельца они остаются вне репозитория); часть их утрачена при работе (`ratchet.py`, `turnorder.py`) — заменены сравнением сообщения теста и перечнем выше.

## 9. Отклонения от ТЗ (с обоснованием)

| Отклонение | Обоснование |
|---|---|
| Нет отдельных `DecisionFrameView.cs`, `AdmissionIterationOutcome.cs` | свойства кадра — read-only доступ (`private set`, `IReadOnlyList`); тип `AdmissionIterationOutcome` — часть контракта цикла и лежит в `TurnLoop.cs`; `DecisionFrameView` был добавлен и удалён как неиспользуемый |
| Дополнительный `TurnPhaseState.cs`, интерфейсы-швы (`FrameServices`, `SelectionSteps`, `IStrategicReadmission`, `IAdmissionWork`/`ITempoWork`/`IColdWork`) | ТЗ допускает интерфейс «только для тестов»; без швов логику не проверить без движка |
| Делегатное ядро `StepTriggerSequence` и `TypedTriggerFanOut.Split(Func)` сохранены | на них стоят production-формы и прежние тесты |
| Recorder пишет только то, что фикстура читает из ledger | полный цикл `RunTurn` в managed недоступен; нативные логи нужных полей не содержат и численным сравнением не названы |
| Знание рецепта кадра — один класс **в пределах `RunTurn`**; Reaction повторяет его | вне объёма этапа; зафиксировано как продолжение |

## 10. Итог относительно критерия успеха

Доменные расширения теперь меняют своего владельца (Strategy / Provisioning / Missions / Continuity / Recon / `DecisionFrame` / компонент работы), а устойчивый контракт потребителя (`TurnLoop` ↔ интерфейсы работ, исходы итерации, события банка) остаётся прежним. Банковские трассы идентичны на всех этапах, дифференциальные тесты порядка и состояния проходят, мёртвый код удалён. Перенос в helper или делегат результатом не считался; число межпапочных связей не уменьшено и не заявлялось.

**Не доказано** (и не заявляется): сохранение поведения на реальном мире и совместимость в Unity. Это остаётся открытой приёмкой владельца.
