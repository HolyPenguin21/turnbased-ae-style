# ActiveDefence: реализация и границы подтверждения

## 1. Исходная версия и статус

Исходный и повторно проверенный master: `5cc271837bb7b474ca7e171633fa3c96affe407a`.
Рабочая ветка: `fix/active-defence-asset-regroup`. Merge/push в master не выполнялись.
Версия проекта: Unity `6000.5.4f1`.

Изменения реализованы и проверены доступными средствами. **Полная приёмка по ТЗ не завершена:** настоящий Unity Test Runner, банковские проверки фактического исполнения всех шести переходов и AI-only партия недоступны в этой среде. Ни диагностический запуск, ни компиляция с reference DLL не заменяют эти проверки. Игровой лог `D:\Unity\Project\My_project\Logs\AiDebug.log` недоступен; новый игровой лог не создан. Исторические логи других запусков не используются как доказательство этой реализации.

Изменённые файлы:

- `Assets/Scripts/Ai/V2/Strategy/Objectives/ActiveDefenceObjectiveEvaluator.cs`
- `Assets/Scripts/Ai/V2/Analysis/ForceNeedModel.cs`
- `Assets/Scripts/Ai/V2/Missions/AggressionMissionPlanner.ActiveDefence.cs`
- `Assets/Scripts/Ai/V2/Strategy/Demand/AggressionDemandEvaluator.ActiveDefence.cs`
- `Assets/Scripts/Ai/V2/Strategy/Demand/AxisDemand.cs`
- `Assets/Scripts/Ai/V2/Materialization/MaterializationDeliveryPolicy.cs`
- `Assets/Scripts/Ai/V2/Continuity/MissionContinuityLayer.ActiveDefence.cs`
- `Assets/Scripts/Ai/V2/Continuity/MissionIntent.Models.cs`
- `Assets/Scripts/Ai/V2/Orchestration/AiStrategyV2Pipeline.AggressionAdmission.cs`
- `Assets/Scripts/Ai/V2/Provisioning/ActiveDefenceProvisioner.cs`
- `Assets/Scripts/Ai/V2/Execution/ActiveDefenceExecutor.cs`
- `Assets/Editor/AiActiveDefenceTests.cs`
- `Assets/Editor/AiAggressionOwnershipRegressionTests.cs`
- `Assets/Editor/AiSecondaryBaseStrategicNodeTests.cs`
- `Assets/Editor/ArmyMovementLifecyclePlayModeTests.cs`
- Этот отчёт и `Docs/active-defence-verification.json`.

## 2. Аудит владельцев и call graph

До редактирования восстановлена исходная версия файлов с проверкой Git blob SHA. Проверены AGENTS.md, Tools/ai-verify, базовые тесты и вызывающие/потребляющие стороны решений.

`WorldAnalysis.Scan/RefreshOperationalState/RefreshStrategicKnowledge → BuildThreat → Enumerate → AssessResponse`.
Последний используется Mission Planner, Demand, Continuity Regroup и финальной проверкой Provisioning. Planner переводит ответ в предложения, Demand — только Shortage в FieldCombatPower. `ActorCommitments.FromIntents / MissionActorPolicy / GroundCombatActorEligibility` дают существующий контракт владения. Затем `ResourceAllocator → ProvisioningSession → ActiveDefenceProvisioner → ActiveDefenceExecutor → GroundCombatLegStep.Transit → WorldDelta/refresh → MissionOutcomeLedger → MissionContinuityLayer → MissionLeaseBook`.

Изучены TurnResourceBook, StrategicResourceReservationLedger, StrategicSpendability, LifecycleReturnPolicy и ReservationInvariants. Ни один из них не переписан. Продолжение Return не получило нового независимого AP-hold.

## 3. Корни проблем и изменения

| Причина | Изменение у существующего владельца |
|---|---|
| Facility мог победить Base при ранжировании одного врага | Enumerate фильтрует собственные Base/Citadel **до** группировки. Владение подтверждается Self.BaseHexes, память здания недостаточна. Честность контакта, CanDamage, значимость и TaskScore сохранены. |
| Facility увеличивал оборонительный резерв | DefensiveReserveForThreats оставляет Base/Citadel до группировки, один враг учитывается один раз. Общий ThreatModel не меняется. |
| Общая цитадель подменяла объект защиты | RegroupHex равен ProtectedAssetHex. Planner переносит именно этот гекс. Потеря владения запрещает продолжение. |
| Геометрическая достижимость подменяла своевременную готовность | ArrivalEta использует SafeStepPathing с Combat-профилем, как Transit; реальные стоимости входа пакуются в текущий и максимальный MP. Будущий ETA должен быть строго меньше EnemyEta. Неизвестный ETA не считается немедленным нападением. В synthetic snapshot без Map требуется явное ReachableOwnBaseHexes. |
| Суммарная мощность вызывала лишние движения | Сначала существующий WorthIt.EstimateSequential проверяет гарантированных текущих защитников. Asset_holds запрещает усиление. Затем детерминированно добавляются своевременные армии: ETA, AP активации, мощность, ID. Сбор прекращается на пороге, лишние ранее выбранные участники удаляются локальной проверкой. Сумма power не заменяет вероятность удержания. |
| Нулевая выборочная прибавка одной слабой армии могла отсечь полезный состав | Такая армия не отбрасывается до оценки совместного состава: несколько проигрывающих защитников способны совместно измотать атакующего. Используется тот же симулятор. |
| Claimed Attack создавал ложный Defer и прогноз удержания | Capable fallback исключает committed. Гарантированная защита исключает полевые армии независимых операций. Постоянный гарнизон остаётся. Свои точные Regroup legs открываются только для переоценки той же обороны; остальные владельцы проверяются каноническим ActorCommitments. |
| Shortage возвращал безопасные армии | В Movers попадают только свободные полевые армии с актуальной опасной Army-threat. SafeWithdrawalBase ограничивает существующую политику собственными достижимыми безопасными базами, глобальная AiReturnBasePolicy не меняется. |
| Любой Return продолжался до прибытия | Добавлен ReturnPurpose: SafeWithdrawal=0 для старых payload, RegroupForAsset=1 для новых усилений. Phase Intercept=0/Return=1/AirSupport=2 сохранены. Continuity отдельно проверяет потребность в усилении и актуальную опасность позиции отступающего. |
| Смена состояния могла пропустить повторный admission | Fingerprint включает purpose, защищаемый объект, ReturnHex, EnemyEta и MapPathingVersion. Нового кеша нет. |
| Производство могло материализовать заведомо опоздавшего защитника | Demand переносит deadline; существующий MaterializationDeliveryPolicy проверяет маршрут и MP проектируемого получателя только для ActiveDefence. Для EnemyEta<=0 срочный demand не создаётся. Общий стратегический ForceNeed сохраняется. |

Regroup отменяется без обратного физического перемещения при исчезновении угрозы, достаточной защите без этого участника, опоздании, потере маршрута или базы. При неизменных фактах состав и направление детерминированы. Временное Suspended/PoolExhausted либо CapabilityUnavailable не превращает собственный Regroup leg в чужую недоступную силу: сначала выполняется переоценка, затем обычное ResumeTransientSuspension.

SafeWithdrawal проверяет угрозу текущему положению, независимо от первоначального enemy ID. Потерянный home может быть заменён только безопасной собственной базой с обычным rekey. Identity Return остаётся actor ID + destination. Освобождение происходит существующим retire/rekey и MissionLease, без отдельного менеджера.

Свободный будущий Attack host остаётся общим кандидатом. PendingPreparationHost и MoverOpportunityCost применены также к новым Return предложениям; автоматического приоритета обороны нет. TryServeActiveDefence и фазы Attack не изменены.

## 4. Банк: полученное доказательство и пробелы

В диагностике реально прошли `ReturnApEnvelope_UsesOneCurrentActivationAndNeverFutureOrPhysicalCosts`:

| Состояние | Physical AP входа | Требование текущего шага | Выданный AP envelope | H/E/M/T движения |
|---|---:|---:|---:|---:|
| Новая активация | 5 | 2 | 2 | 0/0/0/0 |
| Уже активирована | 5 | 0 | 0 | 0/0/0/0 |
| Недостаточно AP | 1 | 2 | Не финансируется | 0/0/0/0 |

Это **allocator**, а не измерение списания executor. Тесты реального выполнения добавлены в существующий PlayMode fixture: первый Regroup через два гекса, уже активированный Regroup, Withdrawal, отказ при устаревшем малом envelope. Они компилируются, но не выполнены. Executor использует существующий Transit и только добавляет диагностические AP before/spent/after; стоимость боя и активации не переписана.

Обязательная таблица переходов — отсутствие измерений явно отмечено:

| Переход | AP до | AP зарезервировано | AP фактически потрачено | AP после | Остаточные claims |
|---|---|---|---|---|---|
| Fresh Intercept | Не измерено | Не измерено | Не измерено | Не измерено | Нужен Unity-run |
| Fresh Regroup | Не измерено | Только allocator отдельно: 2 | Не измерено | Не измерено | Нужен полный execution/settlement |
| Continue Regroup | Не измерено | Только activated allocator отдельно: 0 | Не измерено | Не измерено | Нужен Unity-run через новый ход |
| Cancel Regroup | Live bank не измерен | Live bank не измерен | Executor не запускался | Live bank не измерен | Модель: actor claim снят, resource claims пусты |
| Shortage Withdrawal | Не измерено | Не измерено | Не измерено | Не измерено | Нужен Unity-run |
| Retire on Arrival | Не измерено | Не измерено | Не измерено | Не измерено | Intent завершён в continuity тесте; live settlement не измерен |

Значения AP исполнения намеренно не подставлены из ожидаемых assert. Прямой PlayMode helper проверяет allocator→provisioner→executor, но не полный MissionOutcomeLedger/lease settlement: claims проверяются отдельными модельными тестами, это остаётся интеграционным пробелом.

`CancellationAndRekey_ReleaseOnlyTheirOwnLeaseAndPreserveEconomySaving` и `WithdrawalLostHome_RekeysOnceWithoutKeepingOldActorOrResourceOwner` прошли: старый owner после rekey пуст, новый имеет ровно claim actor #7; после отмены claim отсутствует, ресурсные claims пусты; чужой Economy reserve Materials=4 сохранён. После снятия Economy reserve AssertClearAtTurnEnd и список ReservationInvariants не дают нарушений. Это реальные результаты моделей, не доказательство всей партии.

B-01..04 покрыты allocator и добавленными native execution тестами, но требуют Unity-run. B-05..06 подтверждены на lifecycle/lease модели. B-07..09 частично подтверждены canonical ownership, Attack Gather/Assault и Economy reserve моделью. B-10 подтверждён модельным boundary; целая партия и все перечисленные runtime-инварианты ещё не проверены.

## 5. Кеширование: матрица фактов

| Факт | Источник записи → инвалидация / новый state | Потребитель | Проверка |
|---|---|---|---|
| Позиция армии | Transit → WorldDelta → RefreshOperationalState → новый Self | ArrivalEta, AssessResponse, fingerprint | SnapshotAndPlayerIsolation_NoDefenceResponseCacheNeedsReset и existing WorldDelta suites; native movement pending |
| Текущий MP | Армейская активация/движение → operational refresh | ETA и готовность | ReinforcementTiming, SnapshotAndPlayerIsolation_NoDefenceResponseCacheNeedsReset |
| Attack claim создан/снят | MissionIntentState/MissionLease → ActorCommitments заново; intents fingerprint | Pool, direct capable, гарантированные защитники | Gather/Assault, AvailableFutureAttackHost_StillCompetesAndReleasedClaimIsReevaluated |
| Владелец базы | Мир зданий → WorldDelta/pathing version → новый Self.BaseHexes | Enumerate, Continuity, Provisioning | FacilityAndSeveralBases, lost-base continuity |
| Контакт исчез/ETA изменился | Наблюдение/AiMapMemory → KnowledgeVersion → strategic snapshot/Threat | Enumerate, AssessResponse, Continuity | Threat-vanished и SnapshotAndPlayerIsolation_NoDefenceResponseCacheNeedsReset |
| ReturnPurpose | Создание payload/Continuity → intent state + fingerprint | Own regroup eligibility, lifecycle | AdmissionFingerprint, withdrawal/regroup cases |
| Return destination | Continuity safe-home rekey → registry/lease + fingerprint | Planner, Provisioning, executor | WithdrawalLostHome, CancellationAndRekey |
| Маршрут / проходимость | HexMap/WorldDelta → MapPathingVersion; существующий SafeStepPathing | ETA, финальный next step | Fingerprint прошёл; настоящий terrain-route test требует Unity |
| ForceNeed | Новый WorldSnapshot → новый ConditionalWeakTable key | JustifiedForceNeed | ForceNeedCache_NewSnapshotsChangeDefensiveNeedWithoutMutatingOldResult |
| Игрок | Observer и отдельный snapshot/intent registry/leases | Все расчёты | SnapshotAndPlayerIsolation_NoDefenceResponseCacheNeedsReset + turn/persistent/cache suites |

Никаких прямых записей в ThreatModel, ForceNeed cache или позиции армий evaluator не делает. Решение AssessResponse не кешируется; claims пересчитываются канонической фабрикой. Изменение фактов требует существующего refresh, а не изменения уже использованного snapshot. Результат ForceNeed остаётся привязанным к экземпляру. Новых ручных сбросов кеша не добавлено. Полная runtime-последовательность refresh после реального действия остаётся предметом Unity-интеграции.

C-01..10 и C-12 имеют модельные/существующие проверки; C-11 проверен компиляцией настоящего terrain-route теста, но не исполнением. Результаты смежных cache/world/turn suites поштучно приведены в JSON: engine-bound failures не объявлены прошедшими.

## 6. Выполненные проверки

- Скомпилированы все доступные C# проекта Roslyn с Unity reference DLL и verification-only stubs, включая тела добавленных PlayMode tests при UNITY_6000_3_OR_NEWER. Ошибок C# нет. Это не Unity build.
- Обычный .NET reflection harness исходной версии: 1182 passed / 700 failed из 1882. Текущая версия: **1183 passed / 726 failed из 1909**. Пять старых тестов стали обращаться к native Object equality через новый гарантированный HoldChance. Поэтому обычный differential **не объявляется зелёным**.
- Диагностический harness обеих версий с одинаковыми null-reference bridges: baseline **1379 passed / 503 failed**, current **1405 passed / 504 failed**, всего 1909. **0 регрессий среди прежних проходящих test IDs**. ActiveDefence: **39 passed / 1 engine-bound unavailable**. Единственный недоступный AD тест создаёт настоящий Unity GameObject/HexMap.
- Добавлено 27 test cases относительно исходного общего количества; четыре coroutine PlayMode сценария считаются отдельно и не запускались reflection harness.
- Тесты содержат Facility filtering/dedup/lost ownership, точную secondary/citadel destination и соседний hex, ETA/MP/unknown ETA, достаточный гарнизон, один/два необходимых подкрепления, power без viability, Attack claims/hold, free host, отмену Regroup, safe withdrawal, прибытие, rekey, устойчивую повторную оценку, lease/Economy invariants, materialization deadline и fingerprint.

Диагностические bridges существуют **только в verification copies**, не в Assets ветки: проверки настоящего null заменены ReferenceEquals в WorthIt, AiV2Util, MissionContinuityLayer, VisionSystem и AiMapMemory, чтобы избежать отсутствующих native Object internal calls на .NET. Две UI FindObjectsByType сигнатуры и одна PlayMode UI сигнатура адаптированы лишь под старые reference DLL. Managed Mathf stub имеет compile-only SmoothDamp. Боевой алгоритм WorthIt не подменён; однако destroyed Unity object semantics и игровые MonoBehaviour этим запуском не проверяются. Reflection harness выполняет Test/TestCase + SetUp/TearDown, не UnityTest и не весь Unity runner lifecycle.

## 7. Диагностика партии

Добавлены компактные записи AssessResponse: turn/player/enemy, protected asset, enemy ETA, hold chance, free actor ID/MP/AP, committed IDs, выбранные подкрепления и ETA, destination/decision/reason. Используется существующее подавление повторов AiDebugLog. Continuity пишет причины отмены; executor — результат и реальный AP before/spent/after. Полные массивы snapshot не печатаются.

**Реальные игровые строки отсутствуют.** Для завершения приёмки нужен запуск AI-only с AiDebug в Unity и анализ маршрутов/AP/claims по логам. Выводов об отсутствии осцилляции в реальной партии, сохранности бюджета всей партии и полном покрытии всех B/C сценариев отчёт не делает.

## 8. Что сохранено

Не изменены Attack evaluator/planner/мобилизация/Gather/Assault/RecoveryReturn/пороги/TryServeActiveDefence, Raid и Recon, общий WorldAnalysis.Threat, WorthIt и правила боя, SafeStepPathing и алгоритмы движения, TurnResourceBook, StrategicResourceReservationLedger, ResourceAllocator, MissionLease, ProvisioningSession, глобальный AiReturnBasePolicy и архитектура Pipeline. В общем admission partial добавлены только значимые поля fingerprint. В общем materialization policy добавлен только ActiveDefence-gated deadline check; остальные consumers идут прежним путём.

Отдельного threat evaluator, resource manager, reservation system, army owner или cache нет. Все физические действия остаются у executor/Transit. AirSupport получает только отфильтрованные objectives; авиационный бой, выносливость и возврат не переписаны.

## 9. Остаток для полной приёмки

1. Запустить полный EditMode и PlayMode в Unity 6000.5.4f1; проверить реальные destroyed-object и map lifecycle semantics.
2. Получить измеренную шестистрочную таблицу банковских переходов через полный pipeline и settlement, включая Fresh Intercept, Continue на следующем ходу, конфликт двух миссий, параллельную Economy и конец хода.
3. Выполнить AI-only партию с AiDebug, сверить фактические маршруты, AP, отмены, refresh и invariants.
4. До этих результатов статус — реализация для проверки, **не полностью принятая задача по разделам 16/18/20 ТЗ**.
