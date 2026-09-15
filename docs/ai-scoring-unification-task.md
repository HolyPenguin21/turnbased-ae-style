# Задача: унификация системы оценки (Value) между Economy/Base/Raid/Recon

## Контекст

В `Assets/Scripts/Ai/V2` у каждой стратегической оси (Economy-extraction, Economy-base,
Aggression/Raid, Recon) — своя формула `Value = Польза − Цена`, написанная независимо. Формулы
похожи по духу, но НЕ используют общий код и НЕ сравнимы по шкале: например, у extraction сырой
Value обычно 80-100, у FoundBase — от -20 до +10, у Raid — 20-40 (уже с дисконтом), у Recon —
20-40 (тоже с дисконтом). Из-за этого в общей очереди на движение/AP (которая сортирует все
миссии по этому числу — `AiStrategyV2Pipeline.BuildMissionSet`, `ResourceAllocator`) Economy
систематически душит Raid/Recon/FoundBase, независимо от реальной срочности.

Разбор проведён в текущей сессии (см. переписку, зафиксировано в памяти
`project_ai_v2_foundbase_economy_fixes.md`, round 18-20) через чтение
`Logs/AiDebug.log` + код. Формулы ниже — 100% то, что сейчас реально в коде (не гипотеза).

Владелец проекта прошёлся по полному списку параметров и принял решения по каждому — они и есть
ТЗ этой задачи. Все решения ниже — обязательны к исполнению; если что-то не помещается в
"объединить"/"удалить" однозначно, СТОП и уточнить у владельца, а не додумывать.

## Метод работы (как во всех предыдущих раундах этой темы — соблюдать строго)

1. Меняем ОДИН блок за раз (см. порядок ниже), не всё скопом.
2. После каждого блока: `dotnet build Assembly-CSharp.csproj` и
   `dotnet build Assembly-CSharp-Editor.csproj` — обязательно 0 errors / 0 warnings.
3. Юнит-тесты (`Assets/Editor/AiEconomyDecisionTests.cs` и аналоги) не запускаются в этом
   окружении (нет Unity test runner) — только компиляция гарантируется агентом; логику по
   возможности проверить чтением, playtest делает владелец в Unity.
4. Ничего не коммитить/не пушить без явной просьбы.
5. Не трогать: Development-ось (`DevelopmentOpportunityEvaluator.cs`, её EV-формула уже
   благополучна и не входит в это ТЗ), информационные термы Recon (Info gain/Refresh
   staleness/direction — помечены "ок", не трогать), военные термы (defense bonus, win chance —
   помечены "ок"), инфраструктурные термы (Airfield, GlobalEffect — помечены "ок").
6. НЕ добавлять "десайр-множитель оси" (аналог `aggRaid`/`localSubDesire`) в Economy — владелец
   явно решил, что этим уже управляет радар желаний на другом уровне, дублировать не нужно.

---

## Блок 1 — Экономическая польза (Extraction + Base)

Файлы: `Assets/Scripts/Ai/V2/Strategy/Demand/DemandLayer.Economy.cs` (extraction, функция
`EconomyDemands`, строки ~35-125, и `ScoreEconomySite` строки 785-799),
`Assets/Scripts/Ai/V2/Evaluation/Cards/StrategicCardEvaluator.cs` (`ScoreBaseSite`, строки
1554-1577), `Assets/Scripts/Ai/V2/Foundation/AiConfigV2.Economy.cs` (константы).

Текущее:
- Extraction: `economySiteDeficitValue(60)×resourcePriority + economySiteIncomeGainValue(10)×gain
  + economySiteBaseSynergyValue(14)×network + economySiteClusterValue(6)×cluster +
  economyExtractionPaybackValue(8)×paybackScore`.
- Base: `economyBaseHexYieldValue(10)×hexYield + economyBaseAirfieldValue(8)×airfield +
  economyBaseForwardProgressValue(8)×forward + economyBaseCorridorAlignmentValue(8)×corridor +
  economyBaseSpacingValue(8)×spacing + economyBaseDefenseBonusValue(4)×defense +
  economyBaseGlobalEffectValue(6)×global`, минус `extractionLossPenalty` если база сносит свой
  extractor.

Решения владельца:
1. **`economySiteDeficitValue`**: снизить с 60 до ~10 (владелец не уверен в точном числе — взять
   10 как стартовое, вес не должен доминировать над остальными термами; при необходимости
   подстроить после playtest, но НЕ возвращать к порядку 60).
2. **Кластер соседних месторождений (`economySiteClusterValue`/`EconomyResourceClusterValue`)** —
   удалить термин полностью из формулы extraction. Функцию `EconomyResourceClusterValue` в
   `WorldAnalysis.Economy.cs` можно оставить неиспользуемой или удалить, если больше нигде не
   вызывается — проверить usages перед удалением.
3. **Потеря дохода при сносе extractor'а под базу (`extractionLossPenalty`,
   `economyBaseExtractionLossPenalty`, `site.ConvertsOwnedExtractionSite`,
   `site.LostExtractionIncome`)** — удалить весь этот терм из `ScoreBaseSite`. Проверить, не
   ломает ли это другую логику (например, само решение "можно ли вообще сносить занятый extractor
   ради базы" должно остаться игровым правилом в другом месте, если оно есть — здесь удаляется
   только ценовой терм, не сама механика сноса).
4. **Урожай хекса (Base's `hexYield`) и маржинальный доход (Extraction's `gain`) — объединить.**
   Это два разных способа посчитать "сколько ресурса реально соберёт эта постройка с этого
   хекса" — они должны быть ОДНОЙ функцией, вызываемой из обоих мест. Спроектировать:
   ```
   float EconomicYieldValue(WorldSnapshot snap, HexCoord hex, CardDefinition builderDef)
   ```
   которая по карте-строителю (extractor ЛИБО base-карта — обе имеют `grantedAbilities` с
   Collect-способностями) и текущему `standing.DeficitScore`/приоритету считает единое
   "сколько ресурса реально даст этот hex под эту конкретную карту". Заменить оба текущих места
   расчёта (`BaseHexYieldValue` в `StrategicCardEvaluator.cs` и inline-расчёт `gain`/`resourcePriority`
   в `DemandLayer.Economy.cs`) на вызов этой общей функции. Вес (сейчас 10 у обоих по счастливой
   случайности) сделать ОДНОЙ именованной константой, а не двумя разными.
5. **Синергия сети баз (`economySiteBaseSynergyValue`, Extraction) / Разнесённость от своих баз
   (`economyBaseSpacingValue`, Base) / Близость к дому (Recon Explore) / Близость к базе (Recon
   Refresh/Surveil)** — см. Блок 4 ниже (это стратегическая категория, не экономическая, туда и
   переносится unification). Здесь, в Блоке 1, только: удалить `economyBaseSpacingValue`/
   `site.SpacingScore` термин из `ScoreBaseSite` совсем (владелец пометил "удалить" отдельно от
   объединения synergy/proximity — spacing именно убирается, а не сливается).

Итоговая ожидаемая структура после Блока 1:
- Extraction benefit = `EconomicYieldValue(...) × сниженный deficit-вес + payback`.
- Base benefit = `EconomicYieldValue(...) (тот же вызов) + airfield + forward + corridor +
  defense + global` (spacing и extractionLoss убраны, synergy перенесена в Блок 4).

---

## Блок 2 — Единая "цена карты" (AP + ресурсы) — для ВСЕХ осей

Файлы: новый общий модуль, например `Assets/Scripts/Ai/V2/Evaluation/ScoringPricing.cs` (создать),
плюс правки в `DemandLayer.Economy.cs`, `StrategicCardEvaluator.cs`,
`Assets/Scripts/Ai/V2/Strategy/Objectives/AggressionObjectiveEvaluator.cs`,
`Assets/Scripts/Ai/V2/Missions/AggressionMissionPlanner.cs`,
`Assets/Scripts/Ai/V2/Strategy/Objectives/ReconObjectiveEvaluator.cs`,
`Assets/Scripts/Ai/V2/Missions/ReconMissionPlanner.cs`, константы в
`AiConfigV2.Economy.cs`/`AiConfigV2.Aggression.cs`/`AiConfigV2.Recon.cs`.

Текущее:
- Extraction: `apCost×economyBuildApPenalty(4) + resourceCostSum×economyBuildResourcePenalty(1.5)`
  внутри `ScoreEconomySite`.
- Base: `card.ApCost×economyBuildApPenalty(4) + resourceCostSum×economyBuildResourcePenalty(1.5)`
  (`StrategicCardEvaluator.cs:1567-1568`, `IntrinsicBuildCost`) — уже те же веса, но отдельный
  инлайн-расчёт.
- Raid: НЕТ вычитания цены — вместо этого мультипликативный `apEfficiency = 1/(1+0.12×(ap-1))`
  (`AggressionMissionPlanner.cs:313`).
- Recon: цены карты нет вообще (Explore/Refresh/Surveil ничего не разыгрывают).
- Dev: свой отдельный вычет `apCost`/`dynamicResourceCost` в EV — этот блок его не трогает
  (уже похож на целевой вид), но если по ходу работы станет очевидно, что Dev тоже может
  вызывать общую функцию цены карты без риска — сделать это тоже, иначе оставить как есть.

Решения владельца:
1. Создать одну общую функцию:
   ```
   float CardPricePenalty(float apCost, ResourceCost resourceCost)
       => apCost * ScoringWeights.CardApPenalty + ResourceCostSum(resourceCost) * ScoringWeights.CardResourcePenalty;
   ```
   (веса = текущие 4 и 1.5, если не будет причины менять после калибровки).
2. Extraction и Base — оба вызывают эту функцию вместо своих инлайн-копий ("объединить").
3. Raid — заменить мультипликативный `apEfficiency` на вычитание через ТУ ЖЕ
   `CardPricePenalty(missionApCost, missionResourceCost)` (для Raid "карта" — это фактически
   `RaidCostModel.Build(...)`'s `ApDesired`/ресурсная часть, если она есть; уточнить у
   `RaidCostModel`, есть ли там ресурсная составляющая, если нет — передавать `resourceCost=null`
   и штрафовать только по AP). Убрать `economyBuildApPenalty`-style константу дублирования —
   использовать общий вес.
4. Recon — владелец пометил "это всё в стоимость карты", то есть даже при отсутствии
   разыгрываемой карты, AP на перемещение скаута должно проходить через ту же общую функцию
   (`CardPricePenalty(travelApCost, null)` либо явно ноль для карты и ненулевое AP — согласовать
   при реализации, куда именно физически подставить AP разведки: вероятно, через уже
   существующий ETA/движение).
5. Development — "AP + динамический ресурс, судя по всему авиация" — это наблюдение владельца,
   не команда на изменение. Оставить Dev как есть, но если разработчик увидит, что Dev's
   apCost/dynamicResourceCost можно тривиально завести на ту же `CardPricePenalty` без риска —
   сделать; если нет — не трогать и явно написать в отчёте, что оставлено как есть и почему.

---

## Блок 3 — Единая "цена доставки/пути" — для ВСЕХ осей

Файлы: те же, что в Блоке 2.

Текущее:
- Extraction: `economyBuildApPenalty(4)×deliveryApCost + economySiteTravelPenalty(0.8)×travel`.
- Base: `economyBaseDeliveryApPenalty(1.5)×deliveryApCost + economySiteTravelPenalty(0.8)×travel`
  (ставка AP-доставки у базы НАМЕРЕННО была снижена в предыдущем раунде — round 16 — с 4 до 1.5;
  владелец теперь просит объединить — уточнить при реализации: объединять на каком именно
  значении, 4 или 1.5, или дать вопрос владельцу вслух перед тем как менять постфактум откалиброванное
  число).
- Raid: нет отдельного travel-терма — целиком внутри `apEfficiency` (который в Блоке 2 убирается).
- Recon: нет отдельного travel-терма вообще.

Решения владельца:
1. Одна общая функция:
   ```
   float DeliveryPenalty(float extraApBeyondCardCost, float travelHexes)
       => extraApBeyondCardCost * ScoringWeights.DeliveryApPenalty + travelHexes * ScoringWeights.TravelPenalty;
   ```
2. Extraction и Base вызывают одну и ту же функцию с одним и тем же весом
   `DeliveryApPenalty` — **ВАЖНО: перед унификацией явно спросить владельца**, брать ли вес 4
   (extraction) или 1.5 (base, откалибровано в round 16 намеренно под "база — редкая дорогая
   инвестиция, путь не должен убивать её непропорционально"). Не решать самостоятельно — это
   содержательный игровой вопрос, не техническая деталь.
3. Raid и Recon — владелец: "объединить с путь" — то есть завести оценку пути Raid/Recon через
   ту же `DeliveryPenalty`/travel-часть (Raid уже вычисляет `eta`/маршрут в `RaidCostModel`,
   Recon — через `ETA`/travel в объекте миссии). Добавить явный вычет travel-хексов по общему
   весу `TravelPenalty`, а не оставлять его неявным внутри чего-то другого.

---

## Блок 4 — Единая "стратегическая близость к своей территории"

Файлы: `DemandLayer.Economy.cs` (`EconomyBaseNetworkSynergy` вызов), `WorldAnalysis.Economy.cs`
(`EconomyBaseNetworkSynergy`, строки 545-552), `ReconObjectiveEvaluator.cs` (`homeProximity` в
`BuildExplore`, `proximity`/`Proximity()` в `BuildRefresh`/`BuildSurveil`).

Текущее — три независимые реализации "1 минус нормализованная дистанция до чего-то своего":
- Extraction: `EconomyBaseNetworkSynergy` — дистанция до ближайшей своей базы, рампа
  `economyBaseFoundScanRadius`.
- Recon Explore: `homeProximity` — дистанция до ближайшего "дома" (стартовый Citadel + базы),
  рампа `scoutProximityRampLo(2)..scoutExploreProximityRampHi(7)`.
- Recon Refresh/Surveil: `proximity` — дистанция до ближайшей базы, другая рампа
  (`scoutProximityRampLo..scoutProximityRampHi`, общая, не Explore-specific).

Решение владельца: объединить все три в одну функцию:
```
float ProximityToOwnTerritory(WorldSnapshot snap, HexCoord target, int rampLo, int rampHi)
```
единая реализация дистанции (взять из `EconomyBaseNetworkSynergy` или Recon — что чище), рампы
(`rampLo`/`rampHi`) остаются параметрами вызова, потому что у Explore/Refresh/Extraction сейчас
разные значения рамп и это осознанный выбор ("экономика радиусом до 3-4 хексов", "Explore
радиусом до 7") — не сводить к одному числу рамп, только к одной формуле кривой.

Не входит в объединение (владелец не пометил): "Ценность самой цели рейда" и "Близость цели
рейда к своей базе" (Raid) — оставлены без изменений в этом раунде, НО когда общая функция будет
готова, разумно предложить владельцу отдельным вопросом (не решать самостоятельно), не перевести
ли и Raid's proximity-term на неё же для консистентности — это ЗА рамками текущего ТЗ, только
зафиксировать вопрос в финальном отчёте.

---

## Блок 5 — Единая "цена упущенной выгоды занятого мувера"

Файлы: `DemandLayer.Economy.cs` (`EconomyMissionOpportunityCost`, строки 714-723,
`EconomyDonorStructurallyEligible`, строки 725+), `AggressionObjectiveEvaluator.cs`/
`AggressionMissionPlanner.cs`, `ReconObjectiveEvaluator.cs`/`ReconMissionPlanner.cs`.

Текущее: только у Extraction есть этот терм — `economyLoanContinuationLoss(20)`, **и он уже равен
0, если у героя нет активного назначения** (`EconomyMissionOpportunityCost`, строка 720:
`assignment == null || assignment.Kind == MissionKind.Economy ? 0f : ...`). Владелец сформулировал
принцип ("если карта/мувер уже занят другой задачей — играет роль опп.кост, если простаивает —
0") — эта логика уже реализована здесь один в один, служит образцом.

Решения владельца ("оставить только это" = убрать все прочие приближения opportunity-cost, если
где-то есть другие версии, и распространить ИМЕННО эту логику везде):
1. Вынести в общую функцию:
   ```
   float MoverOpportunityCost(MissionIntent currentAssignment, MissionKind consumingAxis)
       => currentAssignment == null || currentAssignment.Kind == consumingAxis
           ? 0f
           : ScoringWeights.MoverDiversionLoss; // = economyLoanContinuationLoss, переименовать нейтрально
   ```
2. Base — проверить фактическое применение `heroCost`/opportunity в `AddBaseCandidates`
   (`DemandLayer.Economy.cs`, вокруг строки 868 `EconomyMissionOpportunityCost` уже вызывается для
   base candidates тоже — проверить, что это ТА ЖЕ функция, не отдельная копия; если отдельная —
   заменить на общую).
3. Raid и Recon — сейчас у них ВООБЩЕ нет такого терма (доступность мувера проверяется отдельно,
   не как штраф в Value). Добавить вызов той же `MoverOpportunityCost` в их формулы Value, чтобы
   занятый другой осью мувер честно штрафовался, а простаивающий — нет, единообразно со всеми
   осями.

---

## Блок 6 — Единая "цена риска/угрозы" (без изменений в Recon/Raid, кроме подтверждения)

Файлы: `DemandLayer.Economy.cs`, `StrategicCardEvaluator.cs`.

Текущее: Extraction и Base оба используют `economySiteThreatPenalty(18)×exposure`, но как две
раздельные инлайн-строчки с одной и той же константой.

Решение владельца: "объединить" — вынести в общую функцию
`float ThreatPenalty(float exposure) => exposure * ScoringWeights.ThreatPenalty;` и вызывать из
обоих мест. Recon's риск обнаружения (стелс-множитель) остаётся как есть — НЕ трогать, владелец
не просил объединять его с этим (у него другая природа — мультипликативный, не аддитивный, и
про другое — обнаружение своего скаута, а не угроза постройке). Raid — владелец подтвердил, что
отдельный риск-терм ему НЕ нужен (риск уже зашит в `liveFeas`/шанс победы) — ничего не добавлять.

---

## Что явно НЕ делать в этой задаче

- Не добавлять десайр-множитель оси (`aggRaid`/`localSubDesire`-аналог) в Economy.
- Не трогать Development axis формулу (EV = p×G×persistence - A_total - ...).
- Не трогать информационные термы Recon (info gain, staleness, direction pressure, strategic
  relevance) и военные термы (defense bonus, win chance) — они помечены "ок" владельцем.
- Не трогать инфраструктурные термы (Airfield, GlobalEffect) — помечены "ок".
- Не менять `economyBaseDeliveryApPenalty` vs `economyBuildApPenalty` унификацию (Блок 3, п.2)
  без явного вопроса владельцу — это откалиброванное ранее число (round 16), трогать наугад
  нельзя.
- Не коммитить и не пушить.

## Порядок выполнения и отчётность

Блоки 1→6 по порядку (каждый — отдельная точка проверки build 0/0). После каждого блока —
короткая запись: что изменено (файл:строка), какой была формула, какой стала, почему (со ссылкой
на пункт этого документа). В конце — сводный отчёт по всем 6 блокам + отдельный список открытых
вопросов, которые требуют решения владельца (минимум: вес `DeliveryApPenalty` при унификации
Блок 3 п.2, стоит ли расширять proximity-функцию Блока 4 на Raid).
