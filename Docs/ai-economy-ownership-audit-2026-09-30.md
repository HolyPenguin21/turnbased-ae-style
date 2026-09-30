# Economy AI V2: независимый аудит owners и исправления

База: актуальный remote `master`, `d686cf4d5ca70da156e1ad7721f7bf9e2f88ec6d`.
Рабочая ветка: `audit/economy-rule-ownership`. Исходные checkout не изменялись;
для работы создан отдельный worktree. Merge и push не выполнялись.

## Flow и обязанности

| Этап | Owner | Обязанность |
|---|---|---|
| Факты | `WorldAnalysis` + существующий `SafeStepPathing` | Публикует маршруты, угрозы, стоимость возврата, состояние и runtime ID состава, доступность extraction container. |
| Решение | `DemandLayer.AssessEconomyArmy` | Один выбор сохраняемых/добавляемых escorts; safety остаётся в `EconomyRosterSafe`. |
| Mission | `EconomyMissionPlanner` | Выбирает исполняемый текущий stage из snapshot и фиксированного builder. |
| AP projection | `DemandLayer.EconomyCurrentStageAp` | Разовая подготовка + непогашенная активация при travel + build/follow-up при completion. |
| Резервы | `InfrastructureFulfillment` + существующий ledger | Защищает H/E/M/T проекта; owner-scoped promotion/downgrade completion/deferred. |
| Подготовка | `ProvisioningManager` | Проверяет runtime ID, параметры состава и transfer preflight; материализует решение Demand, не оптимизирует escort повторно. |
| Действие | `TaskExecutor` + существующие gameplay executors | Применяет pinned transfers/extraction, выполняет шаг; не выбирает новый состав/home. |
| Completion | `MissionOutcomeLedger` | Проверяет фактическое достижение objective. |
| Lifecycle | `MissionContinuityLayer` | Хранит site lease, идентичность build obligation, retarget и bounded retry. |

## Итог по гипотезам

Имена файлов ниже относительны `Assets/Scripts/Ai/V2/`, если не указано иное.
Все новые regression tests находятся в `Assets/Editor/AiEconomyOwnershipTests.cs`.

| Гипотеза | Подтверждена? | Причина | Старые owners | Новый canonical owner | Изменённые файлы | Tests |
|---|---|---|---|---|---|---|
| A: состав escort | Да | Demand и Provisioning независимо перебирали subsets; tie-break и допустимость выгрузки/подкрепления различались. | `AssessEconomyArmy`/`MinimumSafeEconomyEscortIndices`; `PlanEconomyArmyLightening`/`SelectEconomyEscort` | Выбор — Demand; `MaterializeEconomyRoster` только проверяет и переводит pinned indices в реальные units. | `Strategy/Demand/DemandLayer.Economy.cs`, `Provisioning/ProvisioningManager.EconomyCompletion.cs`, `Analysis/WorldSnapshot.cs`, `Analysis/WorldAnalysis.Self.cs`, `Analysis/WorldAnalysis.Economy.cs` | Дешёвый/дорогой escort; step movement; capacity; route threat; loan protection; full garrison; применение pinned roster; stale runtime ID. |
| B: home/return policy | Частично | Initial collector home использовал safe path cost, recovery — hex distance. Глобальный combat selector имеет другую игровую цель. | `BuildEconomy` initial loop; `SelectEconomyRecoveryTarget`; отдельный combat `SelectReturnBase` | `SelectEconomyHome`: достижимые snapshot witnesses, минимальная safe path cost, threat/citadel/coordinates tie-break. Combat policy оставлена отдельно. | `Analysis/WorldAnalysis.Economy.cs`, `Analysis/WorldAnalysis.Self.cs`, `Analysis/WorldSnapshot.cs`, `Continuity/MissionContinuityLayer.cs` | Initial/reselect одинаковы; длинный путь к геометрически близкой базе; lost home; временная блокировка своей базы. |
| C: fallback pathfinding | Да | Demand конструировал приблизительные routes из расстояний без blockers/threats. В Analysis также были геометрические return fallbacks. | `WorldAnalysis.EconomyBuilderRoutes`; `DemandLayer.SnapshotFallbackRoutes` | Только Analysis/`SafeStepPathing`. Нет witness — нет структурного builder route; недостижимость не превращается в дешёвое расстояние. | `Strategy/Demand/DemandLayer.Economy.cs`, `Analysis/WorldAnalysis.Economy.cs` | Missing route при близкой цели; witnessed detour сохраняет настоящий cost. |
| D: current-stage AP | Частично | Требования Planner не учитывали preparation transfer AP. Extraction AP был вписан в recurring activation и мог повторно учитываться в multi-turn assignment. Но assignment/current-stage/live costing — действительно разные уровни. | `EconomyMissionPlanner.Requirements`; `EconomyMissionClaimedAp`; `EstimateEconomyAssignmentAp` | Общий `EconomyCurrentStageAp`; snapshot/live adapters подают факты одного stage. Full assignment остаётся отдельным; extraction — разовая подготовка. | `Missions/EconomyMissionPlanner.cs`, `Strategy/Demand/DemandLayer.Economy.cs`, `Provisioning/ProvisioningManager.EconomyCompletion.cs` | Activated-builder transfer AP; completion build/follow-up; multi-turn без ранних build ресурсов; MP после reinforcement; extraction считается один раз. |
| E: movement provisioning | Да, по механике | ReturnBuilder и collection/return collector дублировали pinned resolution, claims, path, activation, envelope/pool checks и ProvisionedMission. | `ProvisionEconomyRecovery`; `ProvisionMobileCollection` | `ProvisionEconomyTransport`; task eligibility, live shelter validation и arrival semantics остаются в role wrappers. | `Provisioning/ProvisioningManager.Economy.cs` | Существующие return/collection admission tests; полный differential gate. |
| F: ReturnCollector lifecycle | Да | Потеря Base A сразу прекращала collector intent; builder мог retarget. ProvisionFailure TargetInvalidated также мог прекратить collector до свежего ResolveActive. | Два отдельных блока `ResolveActive`; return-builder-only reconciliation guard | Общий retarget в `ResolveActive`; обе return-задачи переживают lost-home failure до свежего resolve. Прежний ограниченный collector retry сохранён. | `Continuity/MissionContinuityLayer.cs` | Симметричные lost home/last reachable home для обоих kinds; collector lost-home outcome; существующий B3 bounded retry. |
| G: build commitment predicates | Частично | Demand проверял resource type и mover, Phase A — другую комбинацию card/kind. Site lease и exact obligation не являются одним вопросом. | `HasActiveEconomyBuildIntent`; `IsActiveBaseCommitment`; `IsCommittedEconomyBuild` | `MatchesEconomyBuild` + `HasEconomyBuildCommitment` в Continuity. Coarse site-lease predicates оставлены. | `Strategy/Demand/DemandLayer.Economy.cs`, `Strategy/StrategicPhaseA.cs`, `Continuity/MissionContinuityLayer.cs` | Demand/Phase A согласны по resource/card; suspended obligation; существующие build-site ownership tests. |
| H: Combinations | Да | Две локальные реализации обслуживали два optimizer. | Demand; Provisioning | Единственная реализация в Demand; live subset enumeration удалён. | `Provisioning/ProvisioningManager.EconomyCompletion.cs` | Escort regressions и проверка consumers. |

## Что намеренно не объединено

- `SelectReturnBase`/`KeepOrReselectHome` для combat предпочитает активную рабочую базу.
  Economy выбирает ближайшее достижимое shelter: это две политики с разным смыслом.
- `EstimateEconomyAssignmentAp` оценивает всю доставку и возврат; Mission Requirements
  и live claimed AP относятся к текущему stage. У них общий stage primitive, а не общий
  смысл всей оценки.
- `EconomyRosterSafe` — predicate безопасности, а не optimizer состава.
- `HoldsEconomyBuildSite`/`CanGrantEconomyBuildSite` — lease и arbitration;
  `MatchesEconomyBuild` — идентичность obligation. Resource/card matching не заменяет lease.
- `EconomyObjectiveSatisfied` — завершение objective, не удержание target.
- `UsefulMarginalIncomeGain` и `UsefulRetainedIncomeGain` оценивают разные базы дохода:
  retained исключает собственный уже существующий contribution.
- Ограниченный retry ReturnCollector сохранён. Его повторные contended outcomes не могут
  удерживать actor бесконечно. Общий retarget не означает одинаковые stall thresholds.

## Игровые результаты

1. Если два безопасных escorts отличаются AP, выбор Demand сохраняется при подготовке.
   Provisioning не заменяет его более сильным escort собственной эвристикой.
2. Escort, неспособный оплатить terrain step выбранного witnessed route, не выбирается.
   Capacity builder и место для unload в garrison учитываются до funding.
3. У activated builder стоимость reinforcement transfer входит в AP текущего stage.
   Remaining MP добавляемого escort может перенести build на следующий ход.
4. Нет маршрутного witness — нет фиктивного дешёвого builder по прямой дистанции.
5. Collector потерял Base A: следующая свежая проверка выбирает достижимую Base B по той
   же policy, что initial home. Нет достижимой замены — intent прекращается.
6. Собственная home всё ещё существует, но путь временно заблокирован: return intent
   сохраняется с прежним bounded retry. Потеря ownership и временный NoExecutableStep
   остаются разными фактами.

## Reservations и cache

Проверен путь `accepted demand → deferred build → durable intent → completion envelope
→ execution → completion/failure/retarget → release`.

- Единственный writer остаётся `InfrastructureFulfillment.ReserveEconomyCost`.
- `EconomyDeferredBuild` защищает H/E/M/T; ранний multi-turn stage не получает build AP.
- `EconomyBuildCompletion` promotion/downgrade owner-scoped. Резервы второго проекта
  сохраняются при изменении или release первого. Новый ledger не создавался.
- `EconomyMissionPlanner.OwnerKey`/existing stable keys не изменены; build-site lease
  сохраняет независимых builders на разных hexes.
- Hero prerequisite, direct builder, extraction Shell/Host/Create и loan используют
  существующие admission и gameplay transactions.
- Demand больше не читает live ArmyRegistry для garrison assessment: Analysis публикует
  extraction feasibility/cost, используя существующий resolver.
- Snapshot публикует unit identities, activation costs, max/remaining MP, ledger coverage
  и roster protection. Provisioning проверяет immutable choice по live identities и
  параметрам, затем использует `ArmyActions.CanTransferMembers`.
- Execution по-прежнему применяет pinned composition; stale transaction не запускает
  второй optimizer. Extraction выполняется отдельным atomic step с последующей re-admission.
- `EconomyAssessmentCache` ограничен конкретным WorldSnapshot; новая scan/refresh создаёт
  новый snapshot и новую assessment. Обновлён соответствующий regression fixture.
- Pipeline после atomic step вызывает `RefreshStrategicKnowledge`, публикует observation
  delta, reconciles outcome и completion reservations. Это сохраняет fresh decision pass
  после transfer, extraction, movement и build.
- SafeStepPathing caches зависят от `HexMap.PathingVersion`, player route-memory version,
  movement и owned-base set. Альтернативный pathfinder/cache не добавлен.

## Проверка

- Сборка всей `Assets` и EditMode tests вне Unity: **0 ошибок**, **0 предупреждений**.
  Использованы существующие Unity reference DLLs и test-run stubs. Временный stub
  `UnityEditor.MenuItemAttribute` добавлен только в verification copy, поскольку в
  актуальном master есть editor command, не покрытый repository stubs.
- Новые regression cases: **25/25 прошли** в managed runner.
- Полный differential run: baseline **618 passed / 207 failed**, candidate
  **647 passed / 203 failed**, **0 skipped**; **ни один ранее проходивший тест не упал**.
- Оба runs получили одинаковые временные managed adaptations: qualified Mathf направлен
  в существующий managed Mathf stub; null comparisons отсутствующих Unity catalog/map/
  vision-controller объектов заменены reference comparisons. Production source не менялся
  ради этих adaptations. Они позволяют проверять чистую математику и lifecycle без native engine.
- После тестов отдельно собраны актуальные source copies без этих дополнительных managed
  adaptations (кроме existing net472 API compatibility patches и editor/test stubs).
- Native Unity EditMode/PlayMode и реальные игровые партии **не запускались**. Полный suite
  здесь не зелёный: оставшиеся baseline/environment failures нельзя считать Unity acceptance.
- Raw-resource-read ratchet прошёл, `git diff --check` прошёл.
- Recon/Aggression/Development/Attack policy и execution файлы не изменены. В общих snapshot
  файлах добавлены Economy facts; existing combat return topology/ranking оставлены прежними.

Изменённые старые test fixtures дополнены новым Analysis route witness, сохранив assertions
об ownership, retarget и snapshot lifetime. Реальные игровые контракты не ослаблены.

## Повторная проверка: исполнение, банк и кеши

Повторная проверка прежней реализации выявила и исправила четыре проблемы:

1. `ProvisionEconomy` проверял первый шаг исходного состава, хотя Demand уже выбрал разгрузку/подкрепление. Теперь проверяется pinned projected roster. `PlanEconomyCompletion` также считает safe path cost и движение по этому составу; live-проверка займа включает разовую стоимость подготовки. Подготовка по-прежнему выполняется отдельным шагом с новой admission перед движением.
2. `ReturnCollector` на границе stall limit мог завершиться после `TargetInvalidated` до пересмотра потерянного дома. Такой результат теперь передаётся fresh `ResolveActive`, который либо выбирает достижимую свою базу, либо завершает возврат. Блокировка пути/нехватка способности сохраняют прежний ограниченный retry contract.
3. `EconomyAssessmentCache` выдавал изменяемый объект самого кеша. Теперь читатель получает отдельное решение и копии списков индексов/маршрута. Ссылки на `ArmySnapshot` остаются фактами неизменяемого snapshot; обновление игрового состояния требует нового snapshot, как и раньше.
4. Точный ключ кеша `WorthIt` не включал используемые симуляцией `BattleUnit.IsHero`, `HeroFate`, `IsSummoned`. Seed не заменяет точный ключ: новый regression test строит разные составы с одинаковым seed и воспроизводит ошибочный cache hit в прежней версии. Поля включены в ключ; key version увеличена; completeness guards теперь проходят.

Дополнительно maximum step cost использует каноническую минимальную стоимость 1, если terrain entry отсутствует, вместо публикации нулевого ограничения.

### Резервирование через банк

Проверены `StrategicResourceReservationLedger`, `TurnResourceBook`, `StrategicSpendability`, owner-aware funding allocator, promotion при provisioning и освобождение при завершении/отмене.

- Один владелец может использовать свой резерв, но не резерв другой миссии или реакции.
- Deferred build удерживает H/E/M/T и не удерживает AP. Completion promotion/downgrade заменяет строки своего владельца; другие владельцы сохраняют резерв.
- Allocator учитывает уже выданные суммы владельцу, чтобы не вычитать те же единицы повторно как draw и outstanding hold. Derived continuation AP принадлежит operational funding, поэтому allocator намеренно читает ledger claims.
- Provisioning проверяет live physical spendability и публикует completion hold до следующей миссии. Его raw AP remaining вместе с funded envelope и session claims не является независимым кошельком.
- Изменение переданного объекта не является записью в банк: Upsert копирует значения. Инспекция Rows возвращает отдельные строки.
- Добавлены проверки немедленного чтения после Upsert/update/release для AP, Human, Energy, Materials, Tech. Spendability не кеширует остаток банка.

### Чтение, запись и инвалидирование кешей

Проверены границы snapshot refresh, assessment keys, `SafeStepPathing`, player route-memory revisions и `WorthIt`.

- Assessment key разделяет snapshot, цель, маршрут, actor facts, build AP и include-return; reader больше не изменяет сохранённое решение.
- Route cache разделяет игрока, from/target и movement cap. Изменение карты инвалидируется `PathingVersion`; память препятствий — player revision и сравнение blocker sets. Clear меняет epoch; новая база меняет ключ reverse return field. `FindSafePath` возвращает отдельный mutable path, сохраняя кешированный witness.
- Strategic/operational refresh пересобирает Self/Economy; смена знания также пересобирает Known. После atomic mutation выполняется fresh admission.
- WorthIt сохраняет собственный массив полного ключа и возвращает value-type estimate; кеш ограничен AI scope.

### Результаты повторной проверки

- Компиляция production source и всех EditMode tests вне Unity: 0 ошибок.
- Одинаковый managed NUnit runner и одинаковые адаптации для прежней и исправленной версии; новые регрессионные тесты присутствуют в обеих.
- Прежняя версия: 861 тест, 653 passed, 208 failed. Исправленная: 861 тест, 658 passed, 203 failed. **Новых регрессий среди ранее проходивших тестов: 0.** Пять ранее падавших проверок исправлены (две Economy, collision и два cache completeness guards).
- Банк: **19/19**; WorthIt cache: **11/11**; player route-cache isolation: **4/4**; Economy ownership: **28 managed passed**, ещё 2 новых integration cases требуют native Unity.
- Полный прогон не является зелёным Unity EditMode suite: здесь нет native Unity engine, а baseline содержит и другие прежние падения. Новые интеграционные проверки projected movement `(0)` и `(1)` компилируются, но останавливаются при создании `GameObject`; их результат должен быть подтверждён в Unity.
- Push/merge не выполнялись.
