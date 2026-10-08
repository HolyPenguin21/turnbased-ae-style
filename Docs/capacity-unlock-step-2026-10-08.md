# Самостоятельный шаг CapacityUnlock — 2026-10-08

Ветка `feat/capacity-unlock-step` (от `feat/equipment-calibration-2026-10-08`, чтобы сохранить параллельную калибровку Equipment/Mutator; её формулы, Supply и RepeatFactor не менялись).
Масштаб: изменение поведения в существующей архитектуре + локальное устранение дублирования игровой операции.
**Unity не запускалась.** Новые тесты на реестрах и банке требуют рантайма Unity (`PlayerRoot`/`PlayerSetupData` недоступны в локальном mono-раннере, как и существующий `AiProductionStagedPreparationTests`).

## Проблема и результат
На заполненной своей базе (Intel + Lab/Factory) следующая площадка Research/Production требовала составного действия «улучшить базу + поставить площадку» (4 AP, 2/2/1/2). Теперь:

* `DevelopmentPreparationKind.CapacityUnlock` (индекс 2, `Facility=0` и `Operator=1` не менялись) — самостоятельный шаг: покупается только ближайшая ступень (2 AP, 1/1/1/1 по `GameConfig`), карта площадки остаётся в руке как структурный witness, не расходуется.
* Установка площадки после успеха — **новое действие** на обновлённом мире, платит только свою цену; в том же ходу, если бюджет позволяет и она выигрывает арбитраж, иначе позже. Искусственной задержки нет.
* Оператор и подготовка места остаются равноправными вариантами (выбор лучшего peer-шага по `PreparationRank` не менялся).

## Единственные владельцы
| Решение | Владелец |
|---|---|
| Допуск (структура, окно, банк) | `DevelopmentOpportunityEvaluator.AddPreparation` / `PreparationFacts` |
| Цена и оценка шага | `DevelopmentPreparationScorer.CapacityUnlock` = `nonCombatFacilityValue − ToCardScore(tier.apCost) − StrategicResourceCostValue(tier.cost)` (без второго коэффициента) |
| Банк / свободные ресурсы | `TurnResourceBook` через `StrategicSpendability` (default authority, без Economy bypass, без новых резервов) |
| Оркестрация | `StrategicPhaseA` (арбитраж с radar один раз) и `StrategicPhaseB` (MaintenanceSpend) |
| Игровая операция (платёж + `Level++` + Defense/Resistance + уведомление) | `InfrastructureActions.CanUpgradeBase/TryUpgradeBase` — их же использует `BaseViewerModalUI.UpgradeBase` и `StrategicMaintenancePolicy.ExecuteCapacityUpgrade` |
| Версия мира / повторная оценка | `WorldDeltaLifecycle` (`CommitMutation` + `Publish(Infrastructure \| Capability)`) → типизированный reentry пайплайна |

## Что изменено
* `DevelopmentOpportunity`: `PreparationCapacityTier` теперь только у CapacityUnlock (и равен всей цене шага), `PreparationFacilityCard` у него — witness, добавлен `PreparationExpectedLevel` (идентичность плана).
* Перечисление: полная база + карта в руке + tier существует → один кандидат CapacityUnlock (один следующий tier на site), Facility не перечисляется до открытия слота. Карта только в колоде — шаг не создаётся. Свободный слот → только Facility. Нет оператора/каталога/ступеней → нет шага. Headroom считается по `tier.cost`, все четыре ресурса.
* `InfrastructureFulfillment`: `BuildCapacityUnlockCandidate` заново выводит всё из живого мира (та же база, owner/IsBase/не contested, слот закрыт, уровень и tier те же, witness в руке с нужной способностью, окно открыто, структура через `PreparationFacts`, счёт > ε); устаревший план возвращает «нет кандидата», ничего не платит и не переключается на другую базу/карту/ступень. Путь установки площадки больше не покупает ступень (`PlaceFacilityAfterOptionalUpgrade` остался только для Economy global source).
* Результат: `InfraFulfillResult.CapacityUnlocked` (`Built=false`, `CardPlayed=false`, `Created=false`, `NeedsReplan=true`, `Succeeded=true`). Phase A закрывает свой demand, не увеличивает `CardsPlayed`/`InfrastructureBuilt`/`RecordFacilityBuilt`, не запускает Economy builder recovery.
* Trace: `V2InfraWorldStamp` видит `Level`, открытые слоты, Defense; `CheckCapacityUnlock` проверяет «ровно +1 уровень и больше ничего»; откат остальных отказов проверяется как раньше.
* Phase B: тот же шаг — fully priced `StrategicSpendCandidate` (`FullyPriced`), `marginalResCost` для него не вычитается; перед платежом живая `Revalidate`. Generic upgrade (не Research/Production Facility) и ремонт сохранили прежнюю схему «utility − marginal».
* Наблюдение за инфраструктурой (`WorldAnalysis.Observation`) теперь учитывает число свободных слотов базы: покупка уровня видна, даже если Defense не изменился. `DevelopmentAdmissionFacts` уже содержал Level + флаг свободного слота.

## Банк
* Платёж = ровно `tier.apCost` и `tier.cost`, по default authority; чужие claims (EconomyDeferred/Completion, Reaction, continuation) вычитаются из spendable и не трогаются. Новых долгих резервов и второго debit AP нет (`PhaseAApBudget.UnreservedBalance` читает живой PlayerRoot).
* Phase A оборачивает платёж в `ReservationInvariants.BeginSpend/EndSpend`.
* Цена будущей установки в admission/score не участвует и никуда не публикуется как дефицит.

## Проверки
| Что | Результат |
|---|---|
| Компиляция (локальный раннер, mono) | 0 ошибок |
| Обычный набор | 1333 passed (база 1312), регрессий нет, кроме двух намеренно удалённых тестов Supply |
| С патчем Unity-null | 1501 passed (база 1480), регрессий нет |
| `AiCapacityUnlockScoreTests` (3, чистая арифметика) | проходят: первая ступень 2 AP + 1/1/1/1 даёт положительный score; дорогие ступени ниже; без clamp |
| `AiCapacityUnlockTests` (18: B01–B07, B11, B13, B15–B17, R01–R05, примитив, Phase B, trace) | **написаны, в локальном раннере не исполняются** (нужен Unity) |
| `git diff --check` | чисто |

## Что обязательно проверить в Unity
1. Полный EditMode набор + `AiCapacityUnlockTests`.
2. AI-only прогон: заполненная цитадель Intel + Lab, Factory в руке, оператор есть; в логе должны быть два отдельных платежа (`strat.A infra … capacity unlocked`, затем `built`), без `CardsPlayed` на первом шаге и без `CapacityUnlockLeak`.
3. Одновременные Economy/Reaction/continuation holds и пауза на границе действия: чужие строки ledger не меняются.
4. Человеческий `UpgradeBase` в UI: прежние условия/цены/Defense/Resistance, обновление заголовка и сетки.

## Ограничения
* Реентри в том же ходу опирается на существующий типизированный пайплайн (Publish `Infrastructure|Capability` → Development reentry с fingerprint); без игрового прогона это не подтверждено.
* При высокой scarcity ресурсов score ступени может стать ≤ ε и шаг законно не допускается (это оценка, не запрет); третья ступень (6 AP, 4/4/4/4) на нейтральной шкале убыточна.
* B08/B09/B12/B18–B24, R06–R18 и C-матрица покрыты только частично или логикой общих владельцев; отдельных тестов на них нет.
