> **Обновление 2026-09-13:** пять безрисковых дублей уже слиты в `Assets/Scripts/Ai/V2/Foundation/AiV2Util.cs`
> (`Lex`, `ResolveArmy`, `CeilDiv`, `MinDist`, `KnownDefenders`) + `RaidProvisioner.Clears` переведён на
> общий `RaidCombatFeasibility.Clears` (D4) + три конструктора `MissionIntent` в `MissionContinuityLayer.cs`
> собраны через общий `NewIntent(...)`. Остальные находки ниже пока не тронуты.

# Отчёт: дублирование и взаимные «перебивания» логики в ИИ-слое (Strategy V2)

Проект: `D:\Unity\Project\My_project`. Режим: read-only анализ, правок не вносилось.
Объём анализа: 181 файл в `Assets/Scripts/Ai/**` (из 354 в проекте) + вызываемые наружу `Combat/WorthIt.cs`, `Map/ArmyData.cs`, `Aviation/*`, `Cards/CardCostRules.cs`.

Контекст задачи: планируется добавление веток Defence и (доработка) Aggression параллельно/поочерёдно с уже существующими Recon и Development. Цель анализа — найти дублирующиеся/похожие методы и точки конкуренции за общие ресурсы (AP, армии, авиацию, хексы/здания), чтобы новые ветки не столкнулись с существующей логикой.

---

## 0. Карта lane'ов «как есть»

**Ось (`DesireAxis`) и миссия (`MissionKind`) — это две РАЗНЫЕ, несимметричные системы.**

- `Assets/Scripts/Ai/V2/Orchestration/AiStrategyV2Pipeline.cs:164` — `enum DesireAxis { Recon, Aggression, Defence, Economy, Development }` — 5 осей.
- `Assets/Scripts/Ai/V2/Orchestration/AiStrategyV2Pipeline.cs:330` — `enum MissionKind { Scout, Raid, Economy }` — **3** вида миссий.
- `Assets/Scripts/Ai/V2/Missions/MissionAdmissionPolicy.cs:12` — `enum ExecutionLane { None, Recon, Aggression, Economy }` — **3** исполнительные полосы.

Полный сквозной путь «желание → цель → демэнд → миссия → provisioning → исполнение → continuity» существует только у **Recon, Aggression(Raid) и Economy**. У **Defence** есть ровно один кусок — генератор демэндов `DemandLayer.DefenceDemands` (`Assets/Scripts/Ai/V2/Strategy/Demand/DemandLayer.Defence.cs:26`) и второй — `BaselineForceReadinessDemands` (`Assets/Scripts/Ai/V2/Strategy/Demand/DemandLayer.Readiness.cs:29`). У **Development** — демэнды + `InfrastructureFulfillment`, без миссий.

При этом:
- `Assets/Scripts/Ai/V2/Strategy/Desire/DesireEvaluators.cs:251` — **`desires.Raw[DesireAxis.Defence] = 0f;`** с комментарием «Defence has no evaluator yet». Радар у Defence всегда холодный.
- Радар (`RadarValueScale.For`, `AiStrategyV2Pipeline.cs:293`) масштабирует **только `MissionProposal.EffectiveValue`** (`AiStrategyV2Pipeline.cs:1571`). На `AxisDemand.Value` радар **не влияет вообще**.

⇒ Defence-демэнды входят в арбитраж Phase A с «сырым» `Value` (до 100, `DemandLayer.Defence.cs:106`), не приглушённые холодным радаром, в то время как Recon/Aggression миссии приглушаются.

Пайплайн сборки миссий — `AiStrategyV2Pipeline.BuildMissionSet` (`Assets/Scripts/Ai/V2/Orchestration/AiStrategyV2Pipeline.cs:1545-1593`): три жёстко прописанных вызова `ReconMissionPlanner.Propose` / `AggressionMissionLayer.Propose` / `EconomyMissionPlanner.Propose`. Добавление Defence-миссии = четвёртая строка здесь + новый `MissionKind` + новая ветка в ~8 местах (раздел 5).

---

## 1. Группы похожих / дублирующихся методов

### A. Инъективное назначение «акторы ↔ задания» — 4 независимые реализации

| Файл:строка | Сигнатура | Что делает |
|---|---|---|
| `Assets/Scripts/Ai/V2/Recon/ReconAssignmentPlanner.cs:605` | `RecurseScout(...)` | Перебор инъективных назначений скаут-акторов |
| `Assets/Scripts/Ai/V2/Recon/ReconAssignmentPlanner.cs:750` | `ScoreScoutAssignment(...)` | Лекс-ключ (12+3n компонент) |
| `Assets/Scripts/Ai/V2/Recon/ReconAssignmentPlanner.cs:831` | `Lex(long[],long[])` | Лекс-сравнение |
| `Assets/Scripts/Ai/V2/Provisioning/ProvisioningManager.cs:398` | `RecurseRaid(...)` | Тот же перебор для Raid |
| `Assets/Scripts/Ai/V2/Provisioning/ProvisioningManager.cs:426` | `ScoreRaidAssignment(...)` | Лекс-ключ (6+n) |
| `Assets/Scripts/Ai/V2/Provisioning/ProvisioningManager.cs:464` | `Lex(long[],long[])` | Побайтово идентичен ReconAssignmentPlanner.Lex |
| `Assets/Scripts/Ai/V2/Strategy/PhaseA/MaterializationPortfolioSolver.cs:180` | `BestInjectiveAssignment(...)` | Третья реализация: демэнд ↔ материализационная цепочка |
| `Assets/Scripts/Ai/V2/Recon/ReconAssignmentPlanner.cs:1247/1253/1261` | `AddFlowEdge/UsedCapacity/MaxFlow` | Четвёртая: max-flow для Recon-ёмкости |

**Ключевое отличие:** `RecurseScout` содержит кумулятивные ограничения (airActorCap, groundActorCap, min-separation, airEnergyBudget), `RecurseRaid` — **ни одного**. `BestInjectiveAssignment` — единственный с совместным потреблением ресурсов.

### B. Тела provisioning'а по полосам — 5 копий одного скелета
`ProvisioningManager.cs:474` (Ground Scout), `:616` (Economy), `:1117` (EconomyRecovery), `:1194` (Air), `:1458` (RaidProvisioner.Provision) — общий 8-шаговый скелет (резолв актора → валидация цели → шаг пути → реальная цена → конверт → живой AP → Ok/Fail).

### C. Гейт «AP-конверт + живой остаток AP» — 5 почти буквальных копий
`ProvisioningManager.cs:573-582, 830-835, 1158-1164, 1294-1324, 1570-1577` — все читают `funded.Tentative.Ap` и `root.ActionPoints - session.ApClaimed`, без общего хелпера.

### D. Микро-хелперы, размноженные копипастой
`ResolveArmy` (4 копии), `N/F` форматтеры (≥8), `CeilDiv` (6), `Lex` (2), `MinDist` (2).

### E. Планировщики миссий — Recon vs Aggression структурно идентичны
`ReconMissionPlanner.cs` и `AggressionMissionPlanner.cs` — одинаковый набор `struct Candidate / AsIncumbent / Propose / ToCandidate / BuildProposal`. `EconomyMissionPlanner.cs:13` — третий вариант, написан иначе.

### F. «Защитники известной армии по id» — 3 копии
`AggressionMissionPlanner.cs:186`, `AggressionObjectiveEvaluator.cs:198`, `AggressionDemandEvaluator.cs:226` — идентичные тела; родственники `ProvisioningManager.cs:1664`, `TaskExecutor.cs:552`.

### G. Боевой гейт «наша пачка бьёт эту пачку» — 6 мест, разные пороги
`RaidCombatFeasibility.cs:11,15` (raidMinViableWinChance=0.65), `ProvisioningManager.cs:1644` (хардкод той же константы, без cover), `CombatOpportunityAnalyzer.cs:186` (opportunityMinViableWinChance=0.65), `WorldAnalysis.Threat.cs:141,182` (siegeEnemyWinChanceThreshold), `DemandLayer.Economy.cs:495` (легаси `AiConfig.defenceActiveWinChance`=0.6!), `HexEventGuardEstimate.cs:38` (хардкод 0.5).

### H. «Могу ли я это позволить с учётом резерваций» — 5+ несогласованных реализаций
Канон: `StrategicSpendability.cs:21,41,59`. Параллельно живут копии/обходы в `GenerationSource.cs:121`, `WorldAnalysis.Development.cs:133`, `PlacementRules.cs:57`+`CardPlayExecutor.cs:138` (только legacy!), `MaterializationDiagnostics.cs`, `AviationSortieReservationEvaluator.cs:129` и `ReconAirCapacityPolicy.cs:144` (сырой сток), `ProvisioningManager.cs:1320` (сырой сток).
**Важно:** `AiResourceReservation.V2ExtraReservation` (`AiResourceReservation.cs:24`) — хук объявлен, но **нигде в проекте не присваивается**.

### I. Правило вместимости армии — 4 зеркала
Канон: `ArmyData.cs:183` `ComputeCapacity`, `:210` `CanLeaveWithoutOvercrowding` (с исключением для `IsAirfield`). Копии: `ArmyCapacityRules.cs:15,23`, `ProjectedPhysicalState.cs:29,99` (дублирует константу `FieldBaseCapacity=2`), `ArmyReorgProfile.cs:203,221` (**без** исключения для IsAirfield — расхождение, см. D5).

### J–P (кратко, детали см. в файле-источнике анализа)
- **J.** Две воронки допуска материализации (Phase A `AddIfFeasibleA` vs Phase B `FilterSurplus`) с разными наборами проверок.
- **K.** Два метода «лучший нерешённый демэнд под способность» с разными источниками трейтов.
- **L.** Три параллельных пути скоринга карты (`ScoreForDemand`/`ScoreSurplus`/`ScoreNonCombat`) с асимметричными штрафами.
- **M.** Три копии конструктора `MissionIntent` (CreateIntent/CreateRaidIntent/CreateEconomyIntent).
- **N.** Две почти идентичные функции извлечения юнита из гарнизона (Economy/Scouting), с расхождением в источнике `ActorCommitments`.
- **O.** Четыре обёртки резервирования отложенных экономических ресурсов, всё жёстко названо `*Economy*`.
- **P.** Три параллельных switch по `CapabilityKind` («доставлена ли способность»), у каждого мягкий `default`.

---

## 2. Где поведение между копиями РАСХОДИТСЯ (дрейф логики) — самое важное

| # | Расхождение | Где |
|---|---|---|
| D1 | Порядок/тип отказа AP-гейта различается между 5 копиями; Economy теряет репрайс при нехватке живого AP; Air — единственный, кто поднимает Energy-floor | `ProvisioningManager.cs` (573, 830-855, 1294-1324, 1570-1597) |
| D2 | Эпсилон учитывается в Phase A (`FitsSpendableResources`), но не в Phase B (`ReservesOkAfterChain`) и не в `GenerationSource` | `StrategicSpendability.cs:51,72`, `GenerationSource.cs:137` |
| D3 | **Ни один файл `Ai/V2/Recon/`** не консультируется со `StrategicResourceReservationLedger` — авиация видит сырой Energy-сток, экономический hold для неё невидим | `Recon/AviationSortieReservationEvaluator.cs:129`, `Recon/ReconAirCapacityPolicy.cs:144` |
| D4 | Порог win-chance у рейда: инкумбент допускается на 0.40 (`RaidAssemblyPlanner`), а `RaidProvisioner.Clears` хардкодит 0.65 — мина при изменении сборки инкумбента | `RaidAssemblyPlanner.cs:47-106`, `ProvisioningManager.cs:1644-1657` |
| D5 | `CanLeaveWithoutOvercrowding` в `ArmyReorgProfile` потерял исключение для `IsAirfield`, которое есть в каноне `ArmyData` | `ArmyData.cs:210-216` vs `ArmyReorgProfile.cs:221-227` |
| D6 | `ProjectedCapacity` и `ComputeCapacity` расходятся при `CommandRating <= 0` | `ArmyData.cs:183` vs `ArmyCapacityRules.cs:18-20` |
| D7 | **`ScoreForDemand` (Phase A) применяет `GarrisonSaturationPenalty`, `ScoreSurplusRole` (Phase B) — нет.** Прямой удар по Defence, чей основной capability — GarrisonCombatPower | `StrategicCardEvaluator.cs:258 vs 401` |
| D8 | `AggressionDemandEvaluator` фильтрует по claimed-акторам/кулдаунам/readiness; `DemandLayer.DefenceDemands` — **ни одной** из этих проверок, сигнатура даже не принимает `ActorCommitments` | `AggressionDemandEvaluator.cs:59-215` vs `DemandLayer.Defence.cs:26-90` |
| D9 | Три разные формулы «сколько обороны нужно» (Aggression-радар ×1.30, Defence-демэнд ×1.15 без Facility, BaselineForceReadiness — третий подход) + мёртвые V1-константы `AiConfig.defenceActiveWinChance/defenceActiveAssemblyScore/defencePreemptScore`, которые выглядят как готовый тюнинг и могут быть подхвачены по ошибке | `DesireEvaluators.cs:470-492`, `DemandLayer.Defence.cs:63-90`, `AiConfig.cs:35,59-60` |
| D10 | Два метода «нерешённый демэнд» используют разные источники трейтов (`plan.ExpectedTraits` vs `TraitsOf(ProjectedAbilities)`) | `MaterializationCandidateBuilder.cs:146` vs `MaterializationFeasibility.cs:157,162` |
| D11 | `MissionAdmissionPolicy.Conflicts` **fail-open** для неизвестных пар видов миссий — нет кросс-лейновых конфликтов вообще (Raid и Economy-постройка на одном хексе не конфликтуют) | `MissionAdmissionPolicy.cs:68-93` |
| D12 | Phase A и Phase B фильтруют планы разными наборами проверок (бюджет оси и housekeeping-резерв — только Phase A; `CardPlayExecutor.Preflight` — только Phase B) | `MaterializationFeasibility.cs:89 vs 126` |
| D13 | Единственный стратегический допуск авиавылета конструктивно Recon-only, `combatUtility` — заглушка `const 0f` | `AviationSortieReservationEvaluator.cs:157-160` |
| D14 | `ActorCommitments.FromIntents` — switch с fallthrough в Scout-ветку для незнакомых `MissionKind`; новый `MissionKind.Defence` провалится в `IsSoloRecce`-проверку и **не получит claim** | `ActorCommitments.cs:57-125,186-210` |
| D15 | Детерминированный tie-break `ThenBy(d => (int)d.RequestingAxis)` даёt Recon(0) системное преимущество над Defence(2) при равном Value | `StrategicPhaseA.cs:469`, `MaterializationCandidateBuilder.cs:148`, `MaterializationFeasibility.cs:164` |

---

## 3. Точки конкуренции за общие ресурсы

- **AP** — 6 учётных поверхностей (`AxisBudgetLedger`, `ResourceAllocator`, `ProvisioningSession.ApClaimed`, `StrategicResourceReservationLedger`, `StrategicTempoBudget`, `root.ActionPoints`). Явный комментарий-предупреждение в коде: *«Do NOT also call Debit() from RegisterProvisionSuccess»* — граница держится соглашением, не механизмом. `housekeepingApReserve` вычитается независимо в 5 местах (сейчас = 0, поэтому не видно расхождения; станет ненулевой для Defence-реорга — разъедется).
- **Физические ресурсы (H/E/M/T)** — 6 параллельных «протекторов», из них рабочий (owner-aware, с TTL) только `StrategicResourceReservationLedger`, и у него закрытый enum причин из 3 значений — **Defence там нет места**.
- **Армии** — 4 реестра претензий (`ActorCommitments`, `ProvisioningSession.ClaimedArmyIds`, `StrategicCapabilityLeaseRegistry`, `RaidAdmissionRegistry`). `DemandLayer.DefenceDemands` не смотрит ни один.
- **Гарнизоны** — учёт мощи расщеплён: `CapabilityInventory.GarrisonCombatPower` глобальный скаляр (не по хексам, не вычитает claims), `DemandLayer.DefenceDemands` считает пер-хекс. Доставка способности (`MaterializationDeliveryPolicy`) **не сверяет `TargetHex`** — юнит, положенный в гарнизон любой другой базы, закрывает Defence-демэнд для конкретной цитадели. Это, пожалуй, самая опасная находка отчёта.
- **Авиация** — все общие реестры физически лежат в namespace `Recon`; единственный допуск вылета Recon-only; боевой вылет Aggression потребует либо втиснуться туда, либо завести второй независимый резерватор Energy.
- **Хексы/здания** — арбитра нет вообще, кроме внутриполосного `MissionAdmissionPolicy.Conflicts`.
- **Попытки генерации (Challenge)** — два счётчика, которые нужно обновлять синхронно вручную в 3 местах; забытое обновление в новой полосе тихо купит лишний Challenge.

---

## 4. Общие примитивы, которые Defence/Aggression ОБЯЗАНЫ переиспользовать (не копировать)

`AxisDemand`+`DemandLayer.Generate`, `StrategicPhaseA.FulfillDemands`, `MaterializationCandidateBuilder`, `MaterializationPortfolioSolver.{BestInjectiveAssignment,JointFeasibility}`, `MaterializationConsumptionState`/`ProjectedPhysicalState`, `CapabilityDeliveryEvaluator.FinalizeOperationalDelivery`, `MaterializationDeliveryPolicy.AssessDemandOperationally`, `CapabilityInventory.Build`, **`StrategicSpendability.{SpendableAmount,FitsSpendableResources,ReservesOkAfterChain}`**, `StrategicResourceReservationLedger`, `AxisBudgetLedger`, `ResourceAllocator`/`AllocationSession.Pack`, `ProvisionFailure`+`ProvisionDisposition`, `ProvisioningSession`, `ActorCommitments`, `StrategicCapabilityLeaseRegistry`, `AiArmyRoles.{CanSpareGarrisonMember,BestSparable*,IsSoloRecce,IsHeroLed}`, `ReusableArmySelector`, **`ArmyData.{ComputeCapacity,CanLeaveWithoutOvercrowding}`** (канон), `ArmyCapacityRules`, `WorthIt.{WinChance,CanDamageAll}`+`RaidCombatFeasibility.Clears`, `SafeStepPathing`, `MissionAdmissionPolicy.{LaneFor,Capacity,Conflicts,AdmissionRank}`, `MissionContinuityLayer`+`MissionIntent`, `StrategicEffectRegistry.Contributions`, `StrategicTempoBudget`, `AiV2Trace`.

---

## 5. Риск-лист — что сломается при добавлении Defence-ветки «по аналогии»

**P0 — блокирующие, решить до первой строки кода:**
1. **R1** — `DefenceDemands` не видит `ActorCommitments`: армия, уже заклеймленная под Raid/Economy, засчитывается Defence как «прикрытие». Фикс: расширить сигнатуру, фильтровать по незаклеймленным силам.
2. **R2** — `GarrisonCombatPower` доставляется без привязки к `TargetHex`: Defence-демэнд «усилить цитадель (5,3)» закрывается юнитом, положенным в гарнизон базы на другом конце карты. Фикс: добавить проверку хекса в `AssessDemandOperationally` + `DeliveredCapabilityAmount` per-hex.
3. **R3** — Новый `MissionKind.Defence` провалится в Scout fallthrough `ActorCommitments.FromIntents` и не получит claim на актора → актор будет виден всем прочим полосам как свободный. Фикс: реестр предикатов по `MissionKind` вместо switch.
4. **R4** — Defence-миссия требует правок минимум в 8 местах (enum, `BuildMissionSet`, `LaneFor`, `Conflicts`, `Provision`, `PreparePass`, `ExecuteStep`, `CreateIntent`) — каждое сегодня fail-silent. Рекомендация: вынести интерфейс `IMissionLane` и сделать диспетчеризацию табличной **до** написания Defence-кода.

**P1:**
5. **R5** — Defence-ось всегда холодная в радаре (`Raw[Defence]=0`), но `AxisDemand.Value` радар не масштабирует → Defence-демэнды сейчас сильнее «положенного» в арбитраже, а появившиеся Defence-миссии будут задавлены floor'ом радара. Нужен `DefenceDesire(...)` evaluator.
6. **R6** — Шестая копия AP-гейта в `ProvisionDefence` рискует повторить ошибку Economy (нет репрайса). Вынести общий `AdmitEnvelope(...)`.
7. **R7** — Нет способа выразить Defence-hold физических ресурсов: `StrategicReservationReason` — закрытый enum из 3 экономических значений. Обобщить до lane-агностичной причины.
8. **R8** — Боевая авиация (Aggression) заведёт второй независимый допуск вылета и будет конкурировать за Energy с Recon-допуском вслепую. Доделать существующую заглушку `combatUtility` в `AviationSortieReservationEvaluator`.

**P2:**
9. **R9** — Четвёртая несогласованная формула «сколько обороны нужно» + мёртвые V1-константы-приманки `AiConfig.defenceActive*` — легко подхватить по ошибке. Ввести единый `DefenceRequirementModel`, удалить мёртвые константы.
10. **R10** — Persistence-gate deferral (`IsPersistenceDeferred`) сейчас доступен только Recon-генератору, хотя механизм универсален — стоит использовать для фоновых Defence/Baseline демэндов.
11. **R11** — Tie-break `ThenBy((int)RequestingAxis)` даёт Recon системное преимущество над Defence при равном Value. Заменить на явную таблицу приоритетов.
12. **R12** — Housekeeping держит жёсткий инвариант «ноль AP», а Defence-реорг гарнизона по тревоге, скорее всего, будет стоить AP — заранее решить, живёт ли это в Housekeeping, Phase B Tempo или отдельной миссией (иначе появится третий «реорганизатор армий»).

---

## Три вещи, которые стоит сделать до написания первой строки Defence-лейна

1. Передать `ActorCommitments` в `DefenceDemands` и считать только незаклеймленные силы (R1).
2. Добавить проверку `TargetHex` для `GarrisonCombatPower` в `MaterializationDeliveryPolicy.AssessDemandOperationally` (R2) — иначе весь Defence-лейн будет «выполняться» не в том месте карты.
3. Сделать `MissionKind`-диспетчеризацию табличной в четырёх точках: `BuildMissionSet`, `Provision`, `ExecuteStep`, `ActorCommitments.FromIntents` (R3, R4) — иначе каждая новая полоса добавляет 8 fail-silent мест.
