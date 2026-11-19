# Аудит блоков «Экономика» и «Защита» (2026-11-19)

Ревизия: ветка `fix/economy-escort-strike` (master 19b3c7d2 + изменения «заём на удар»).
Лог для калибровки: `Logs/AiDebug.log`, матч Halden vs Vex, 24 раунда (2026-11-19 11:14).
Код не менялся: это отчёт для решений владельца.

Классы правок:
- **A** — без изменения поведения (мёртвый код, точные дубли, перенос). Можно делать сразу.
- **B** — механический рефакторинг (разбиение крупных методов и файлов). Поведение не меняется, но правка большая.
- **C** — несоответствие или ошибка, меняет поведение. Нужно решение.
- **K** — калибровка скоров. Нужно решение.

## 1. Экономика

### 1.1 Анализ (`Analysis/WorldAnalysis.Economy.cs`, `.EconomyRoutes.cs`)

| # | Класс | Находка | Предложение |
|---|---|---|---|
| E1 | B | `BuildEconomy` — 430 строк, шесть задач в одном методе: проекция выезда, ресурсные показатели, сайты добычи, мобильный сбор, сайты баз, actionability. | Разбить на 6 приватных шагов в том же файле, вызываемых по порядку. |
| E2 | A | Выражение «сбор своих армий на хексе» записано дважды (строки 193 и 251). | Один helper. |
| E3 | A | `armiesCanCollect` всегда `true`: хексы с известным врагом уже отброшены выше (`KnownHostileAtHex` → `continue`). | Удалить переменную. |
| E4 | A | В цикле сайтов баз `!knownMapHexes.Contains(hex) \|\| !hasDirection` и `if (hasDirection)` всегда ложны/истинны: множество строится только при `hasDirection` и из `knownMapHexes`. | Удалить. |
| E5 | A | Формула «прирост сбора от коллектора» записана трижды: `MarginalSiteCollectionGains` (+1), цикл мобильного сбора (+capacity), `CollectorMarginalIncome` (+capacity без себя). | Одна функция (yield, building, others, capacity). |
| E6 | A | `IsForwardBaseCandidate` — в игре не вызывается (по комментарию перестал быть фильтром), остался только в тесте. | Удалить вместе с тестом. |
| E7 | C | `KnownHostileAtHex` не учитывает `EventGuards`, а `KnownNeutralOnlyAtHex` учитывает. Охраняемый событием хекс считается свободным для стройки и мобильного сбора. | Проверить, бывают ли охранники событий на ресурсных хексах. Если да — одно правило «хекс занят». |
| E8 | A | Анализ вызывает слой Continuity (`EconomyContinuity.SelectEconomyHome`) и Strategy (`DevelopmentOpportunityEvaluator.LivePreparationSite`) — зависимость вверх по слоям. | Перенести чистую `SelectEconomyHome` в Analysis/Foundation, Continuity вызывает её оттуда. |

### 1.2 Спрос и выбор строителя (`Strategy/Demand/DemandLayer.Economy*.cs`, `EconomyBuilderSelection.cs`)

| # | Класс | Находка | Предложение |
|---|---|---|---|
| E9 | B | `EconomyDemands` — 320 строк, `AddBaseCandidates` — 240, `AssessEconomyArmy` — 270. | Разбить: оценка сайта / отбор / эмиссия; в `AssessEconomyArmy` — три пути (извлечение из гарнизона, облегчение, подкрепление) отдельными методами. |
| E10 | A | Формула «ходов доставки» трижды: `EstimateEconomyAssignmentAp`, `EconomySupportBuilderAllowed`, `EconomyExtractionDeliveryAllowed`. Значения совпадают (проверено по ветвям). | Одна `DeliveryTurns(route)`. |
| E11 | A | Проекция маршрута под состав (AP активации, макс./текущее движение, `TotalAssignmentApCost`) собирается в `AssessEconomyArmy` трижды почти одинаково. | Один helper `ProjectRoute(route, bodies)`. |
| E12 | A | В `AssessEconomyArmy` гарнизон на хексе ищется дважды (`localGarrison` и `garrison`), повторная проверка `HasActivatedThisTurn` недостижима: `mayPrepare` её уже требует. | Удалить второй поиск. |
| E13 | A | `RankEconomyBuilders`: предикат «эта армия уже держит этот сайт» дважды, `ActiveAssignment(...)` до 4 раз на кандидата. | Вычислить один раз на кандидата. |
| E14 | C | Правило 2026-11-16 «эскорт — самый слабый из достаточных» есть в путях извлечения и подкрепления, но не в «облегчить на базе» (`MinimumSafeEconomyEscortIndices`: только AP, затем движение). Там может остаться сильнейший боец. | Добавить ту же связку «AP → движение → меньшая мощь» во все три пути: один отборщик подмножества вместо двух. |
| E15 | A | Порог безопасности эскорта читается из V1 `AiConfig.economyEscortMinWinChance` (0.6). Связано с задачей удаления V1. | Перенести в `AiConfigV2.Economy`. |

### 1.3 Миссии, обеспечение, Continuity

| # | Класс | Находка | Предложение |
|---|---|---|---|
| E16 | A | `EconomyMissionPlanner.Propose`: проверка «возвратная ветвь» (`ReturnBuilder \|\| ReturnCollector`) вручную трижды, при этом есть `MissionIntent.IsLifecycleLeg`. | Использовать его. |
| E17 | A | `EconomyMissionPlanner`: после `if (incumbent != null) continue;` все `incumbent?.…` ниже — мёртвый код (всегда null). | Удалить. |
| E18 | B | `ProvisionEconomy` — 420 строк, в нём ~70 строк диагностики только для FoundBase. | Вынести диагностику и сборку двух результатов (гарнизон / готовый герой) в отдельные методы. |
| E19 | A | `LivePreparationSite(...)` пересчитывается 3–4 раза на кандидата в обеспечении (отмечено ещё 2026-11-18). | Один раз на вызов. |
| E20 | A | Два разных метода `CandidateRejection` (Demand — по снимку, Provisioning — по живому миру) с одинаковым именем. | Переименовать провиженинговый в `LiveCandidateRejection`. |

## 2. Защита

| # | Класс | Находка | Предложение |
|---|---|---|---|
| D1 | B | `ActiveDefenceObjectiveEvaluator.cs` — 1500 строк, семь ответственностей. | Partial-файлы с тем же классом, API не меняется: `.Objectives` (Enumerate/EnumerateImmediate), `.Immediate` (AssessImmediate, ImmediateStrikeWith, LeavesForceExposed), `.Response` (AssessResponse, TryDirectResponse, Regroup), `.Timing` (CanReachNow, ArrivalEta, CanIntercept*), `.StrongholdPin` (IsPinnedStrongholdDefender, StrongholdStaysCovered, CanDefeatBeforeThreat*), `.Withdrawal` (NeedsSafeWithdrawal, SafeWithdrawalBase, BenchShelter), `.Score` (BuildActiveDefenceScore, AssetDefenceTerms, WithResponse). Существующий `.Sortie` остаётся. |
| D2 | A | `CanReachNow` повторяет проход по стоимостям шагов из `GroundArrivalEta`. | `CanReachNow` = `GroundArrivalEta(...) == 0` на том же профиле маршрута, после проверки на тестах тайминга. |
| D3 | C | «Успеть до угрозы» в двух вариантах: регруппировка к активу (`CanArriveBeforeThreat`) считает по самому дешёвому маршруту, перехват (`CanInterceptRosterBeforeThreat`) — по самому быстрому по ходам. Регруппировка может признать «не успевает», хотя быстрый маршрут успевает. | Одно правило — самый быстрый по ходам. Иначе подтвердить, что разница намеренная (в комментарии «preserve the reinforcement route contract»). |
| D4 | ок | Стратегическая защита охраняет только Base/Citadel — твоё решение (2026-11-13: добычу не охранять отдельными армиями). Немедленный удар теперь учитывает постройку (п.4 предыдущей задачи). | — |

## 3. Горизонтальная проверка

| Паттерн | Где проверено | Итог |
|---|---|---|
| 61 Цель операции теряется на переходе к возврату | Economy ReturnBuilder, Raid Return/Recovery, Attack RecoveryReturn | Закрыто общим займом (StrikeLoan). AD Return — намеренно не одалживается (отход). |
| 62 Класс актива без владельца в защите | ThreatModel ↔ все читатели | Facility есть в модели угроз; читают Immediate (теперь) и Raid/Attack (вражеские). Пробела больше нет. |
| 8 Значение вне общей шкалы | Все производители `TaskScore` | Найдено два нарушения (K1, K2). |
| 15 Одна потребность — два пути оценки | Тайминг защиты | D3. |
| 57 Одно правило руками дважды | Экономика | E2, E5, E10, E11, E13, E16. |
| Правило эскорта 2026-11-16 | Три пути выбора эскорта | E14. |

## 4. Калибровка скоров (данные лога 2026-11-19)

Медианы `TaskScore.Value` за матч (одна строка на задачу и ход):

| Задача | n | Медиана | Макс | Главные слоты (средние) |
|---|---|---|---|---|
| Scout DisruptStructure | 3 | 25.6 | 25.6 | AttackReadiness 16.2 (S1, намеренно), Relevance 6.0 |
| Attack | 9 | 11.5 | 22.0 | WinChance 8.6, FrontProgress 6.8, AttackReadiness 5.4; затраты 8.8 |
| Economy FoundBase | 7 | 10.9 | 13.8 | EconomicHexBenefit 12.4, Corridor 5.7; CardPrice 12.8 |
| Scout Explore | 259 | 10.8 | 17.4 | InfoGain 8.5, Relevance 3.4, ThreatDirection 3.4 |
| Scout Refresh | 112 | 9.9 | 15.7 | Staleness 6.6, AttackReadiness 5.4, ThreatDirection 3.8 |
| ActiveDefence Immediate | 3 | 8.7 | 9.8 | WinChance 11.7, **PreventedDamage 0.8** |
| Raid | 38 | 5.3 | 19.8 | WinChance 11.2, EventReward 7.7 |
| Economy BuildExtraction | 44 | 6.2 | 11.6 | EconomicHexBenefit 8.2, Payback 6.0; CardPrice 4.2, Delivery 4.1 |
| Development | 5 | 2.3 | 4.3 | **StrategicRelevance 7.3** (выше максимума 6) |

| # | Находка | Предложение |
|---|---|---|
| K1 | Development кладёт в `StrategicRelevance` карточную оценку через `CardScoreToTaskScore` (7.3) в обход ограничения слота (6). | Пропускать через `TaskScoreEvaluator.StrategicRelevance(...)` (клэмп), либо перенести ценность в слот, которому она соответствует. Итоговая оценка Development снизится на ~1.3. |
| K2 | Explore умножает `ThreatDirection` на `(1 + HomeThreat)`: значение до 8 при максимуме 4 (в логе 4.2). | Клэмп к максимуму после множителя, либо перенести «угрозу дому» в отдельный слот. |
| K3 | Немедленный удар по слабой цели стоит ~8.7 и проигрывает обычному исследованию (медиана 10.8). В логе на T10 перехват Swarm (#15) армией #23 был профинансирован, но разведка забрала #23 на исследование. `PreventedDamage` в среднем 0.8: мощь врага делится на `AttackPeak` своей армии. | С п.4 предыдущей задачи удар рядом с постройкой получает +6…+9. Для удара без угрожаемой постройки — решение: оставить (удар по чистому разведчику менее ценен) или поднять нижнюю границу `PreventedDamage` для врага, который виден у своей территории. |
| K4 | Стройка добычи (медиана 6.2) теряет ~8.3 на CardPrice+Delivery при выгоде ~14. Учёт AP активации верен; вопрос в доле. | Оставить без изменения до матча с новым займом: строитель теперь чаще занят ударом, и время экономики изменится. |
| K5 | `DetectionRisk` у Refresh бинарный (0 или 8): 264 из 267 записей — 0. Это следствие правила 2026-10-24 (`StealthThreat = IsExposed ? 1 : DetectorRisk`). | Не баг; оставить. |

## 5. Порядок работ (предложение)

1. **A** — все пункты одним коммитом после прогона тестов (поведение не меняется).
2. **B** — E1, E9, E18, D1 отдельными коммитами, у каждого — сравнение тестов база/ветка.
3. **C** — E7, E14, D3 — после решения по каждому.
4. **K** — K1, K2 (исправление шкалы), K3 — по решению; K4 — после нового матча.

Проверки к каждому этапу: компиляция ai-verify, полный набор ai-verify против базы, полный EditMode в Unity (worktree), source-scan тесты слоёв, реестр `MatchStateInventory`.

## 6. Реализация (ветка `refactor/economy-defence-audit`)

| # | Статус | Что сделано |
|---|---|---|
| E1 | сделано | `BuildEconomy` → `ProjectEconomyDepartures`, `BuildResourceStandings`, `ScanExtractionSites`, `ScanMobileCollection`, `ScanBaseSites`, `HasActionableEconomyOpportunity`; общие факты скана — `EconomySiteScan` |
| E2, E5 | сделано | `OwnCollectionCapacityAt`; прирост сбора — `IncomeProjection.MarginalArmyCollectionAtHex` (три копии → одна) |
| E3, E4, E6, E12, E17 | сделано | мёртвые условия и код удалены; `IsForwardBaseCandidate` удалён (тест направления сохранён без него) |
| E8 | сделано | `SelectEconomyHome` перенесён в `WorldAnalysis` (Analysis больше не зовёт Continuity) |
| E9 | сделано | `EconomyDemands` → `ExtractionCandidate` / `BaseCandidate` (один `EconomyRejections` вместо пяти `out`) → `SelectEconomyBuilds`; `AssessEconomyArmy` → `AssessGarrisonExtraction` / `AssessOwnRoster` / `AssessReinforcement` |
| E10, E11, E13 | сделано | `DeliveryTurns`, `ProjectRoute`; назначение и «закреплён здесь» считаются один раз на кандидата |
| E14 | сделано | один отбор эскорта `WeakestSufficientSubset` для трёх путей (меньше тел → меньше AP → быстрее → слабее) |
| E15 | сделано | `AiConfigV2.economyEscortMinWinChance` |
| E16 | сделано | `IsLifecycleLeg`; плюс горизонтально `EconomyTaskKinds.IsReturn` для мест, где есть только цель |
| E18 | сделано | диагностика FoundBase и сборка результатов вынесены (`TraceDurableEconomyMover`, `TraceFreshEconomyCandidates`, `GarrisonExtractionResult`, `CommittedBuilderUnavailable`, `DirectHeroResult`) |
| E19, E20 | сделано | площадка подготовки читается один раз на вызов; `LiveCandidateRejection` |
| E7 | сделано | `KnownHostileAtHex` учитывает охранников событий (и в живой проверке коллектора) |
| D1 | сделано | partial-файлы `.Immediate`, `.Response`, `.Timing`, `.StrongholdPin`, `.Withdrawal`, `.Score` |
| D2 | сделано | один проход маршрута `WalkGroundRoute`; `CanReachNow` = прибытие за 0 ходов |
| D3 | сделано | все сроки — по маршруту, которым армия реально идёт (`FindAffordableStep` ищет оптимальный по ходам): регруппировка, отход, подкрепление гарнизона, материализация защиты |
| K1, K2 | сделано | слоты Development/Explore в пределах своих максимумов |
| K3 | без изменений | удар у постройки уже получает её ценность (заём на удар); для чистого разведчика — оставлено |
| K4 | отложено | до матча с займом армии на удар |

### Горизонтальные находки при перепроверке

| Находка | Сделано |
|---|---|
| `ResponseEta` модели угроз считался полем стоимостей на каждую свою армию, решения его не читали; он лишь менял отпечаток повторного допуска | удалён (поле, параметр `Severity`, отпечаток) |
| `PlanEconomyCompletion` / `MaterializeEconomyRoster` на каждом кандидате пересобирали `ActorCommitments` и площадку подготовки | принимают их от вызывающего |
| Проверка «вид задачи — возврат» вручную в 5 местах | `EconomyTaskKinds.IsReturn` |
| Остальные ETA через деление (`CeilDiv`) | проверены: цена и грубый план, не сроки угроз — без изменений |

### Сравнение с логом 2026-11-19

| Изменение | Что меняется в этом матче |
|---|---|
| K2 | 209 из 767 оценок Explore выходили за предел; лучшая цель исследования меняется в 1 ходе (Vex T16: (3,3) → (4,-3)) |
| K1 | Development −1.3; Production (-2,-4) Vex T13–T15 и так ни разу не исполнился (`MoverContended`); Research (0,0) T21 остаётся положительным (2.3 → 1.0) |
| E7 | ни одна охраняемая событием клетка не была целью экономики — правило профилактическое |
| E14 | облегчений армии у базы в матче нет (единственная операция — добавление бойца) |
| D3 | стратегическая защита дала 7 ответов (2 Shortage); маршруты без карты из лога не пересчитать — проверка новым матчем |
