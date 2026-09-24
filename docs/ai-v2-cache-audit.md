# AI V2 — аудит кеша: кто пишет, кто читает, в каком порядке

Аудит проведён по состоянию `master` = `375b5ce9` (рабочее дерево чистое), лог партии
`Logs/AiDebug.log` от 2026-09-23 (16 ходов × 2 ИИ). Привязка к именам классов и методов,
без номеров строк — чтобы параллельные правки не ломали документ.

Нормативная база — `Assets/Scripts/Ai/V2/ARCHITECTURE.md`.

---

## 1. Что считается кешем

Три класса узлов с разными контрактами. Смешивать их нельзя: у них разный жизненный цикл
и разные требования к свежести.

| Класс | Контракт | Узлы |
|---|---|---|
| **A. Кадр хода** | строится целиком, дальше только читается; замена целиком, не мутация | `WorldSnapshot` (+ `Self`, `Known`, `MapKnowledge`, `TrueWorld`, `Economy`, `Development`, `Threat`) |
| **B. Реестры хода** | живут один ход, обязаны умирать на выходе | `AxisBudgetLedger`, `StrategicResourceReservationLedger`, `ActorCommitments`, `CapabilityInventory`, `StrategicTempoBudget`, `CapabilityPoolExhaustionRegistry`, `GroundCombatAdmissionRegistry`, `StrategicInterruptRegistry`, `StrategicCapabilityLeaseRegistry`, `AiAllocatorStateRegistry`, `V2StateVersion` |
| **C. Память игрока** | переживает ход, обязана быть изолирована по игроку | `AiMapMemory`, `AiReconIntelMemory`, `AiReconMemory`, `ReconIntelSnapshotRegistry`, `ScoutTrailRegistry`, `ReconPatrolStateRegistry`, `ReconAirSortieRegistry`, `AirReconCoverageRegistry`, `ReconCapacityDeficitRegistry`, `AirSortieRegistry`, `ResourceStarvationRegistry`, `MissionIntentRegistry`/`MissionIntentState`, `AiRadarStateRegistry`, `InitiativeAnalyticsHistory` |

Объём: 18 основных узлов, ~140 методов API, ~510 обращений из кода
(`AiMapMemory` 251, `StrategicInterruptRegistry` 47, `MissionIntentRegistry` 44,
`StrategicResourceReservationLedger` 43, `AirSortieRegistry` 39, остальные 2–24).

---

## 2. Спина: фактический порядок хода (`Pipeline.RunTurn`)

```
 [T0]  V2TurnActivityTelemetry.Begin
       CapabilityPoolExhaustionRegistry.BeginTurn          W:B
       StrategicResourceReservationLedger.BeginTurn        W:B
       AiV2Trace.BeginMain
 [T1]  WorldAnalysis.Scan ────────────────────────────────► W:A  (строит кадр)
         ├─ BuildSelf / BuildDevelopment / BuildKnown
         ├─ AiReconMemory.Observe ────────────────────────► W:C  (!! запись из Analysis)
         │     ├─ AiReconIntelMemory.ObserveCurrentVisibility   W:C
         │     └─ ReconIntelSnapshotRegistry.Capture            W:C  «заморозка»
         ├─ BuildTrueWorld / BuildMapKnowledge / BuildEconomy / BuildThreat
 [T2]  StrategyLayer.Evaluate (desires) + ApplyRadarScope       RW:C (AiRadarState)
 [T3]  ReconObjectiveEvaluator.Enumerate          R:A,C    «одна энумерация на ход»
       AggressionObjectiveEvaluator.Enumerate     R:A,C
 [T4]  MissionContinuityLayer.ResolveActive       RW:C (MissionIntentRegistry)
       AiStrategyV2Scope.ApplyIntentScope         W:C
       ActorCommitments.FromIntents               W:B  (производная от интентов)
 [T5]  DemandLayer.Generate                       R:A,B,C + W:C (ResourceStarvation decay)
 [T6]  AxisBudgetLedger.Create                    W:B
 [T7]  StrategicManager.FulfillDemands (Phase A)  RW:B,C
         ├─ StrategicCapabilityLeaseRegistry.Mark                    W:B
         └─ MaterializationDiagnostics → ResourceStarvation.Record   W:C
 ┌─[L] MID-TURN LOOP (до maxMidTurnStepsPerTurn) ───────────────────────────────┐
 │ [L1] WorldAnalysis.RefreshStrategicKnowledge  ► W:A + повторно W:C (Observe) │
 │ [L2] ReconObjectiveEvaluator.Enumerate  (заново)                             │
 │ [L3] MissionContinuityLayer.ResolveActive → ActorCommitments.FromIntents     │
 │ [L4] DemandLayer.Generate (заново) → StrategicPhaseA (re-admission)          │
 │ [L5] BuildMissionSet → ResourceAllocator.BeginTurn → GroundCombatAdmission   │
 │ [L6] ProvisioningManager.PreparePass → CapabilityPoolExhaustion.MarkExhausted│
 │ [L7] TaskExecutor.ExecuteStep → V2StateVersion.Bump → Continuity.ReconcileStep│
 └──────────────────────────────────────────────────────────────────────────────┘
 [T8]  MissionContinuityLayer.ReconcileAfterTurn      W:C
 [T9]  терминальный AirRecon (Provision → Execute)    W:C (AirSortie/ReconAirSortie)
 [T10] StrategicManager.UseSurplus (Phase B)          RW:B
 [T11] HousekeepingManager.RunHousekeeping
         ├─ Run(...)                                  R:B (IsLeased)
         └─ StrategicCapabilityLeaseRegistry.Clear    W:B
 [T12] StrategicResourceReservationLedger.AssertClearAtTurnEnd   (teardown)
       InitiativeAnalyticsHistory.Record              W:C
```

Ключевая особенность: **кадр `WorldSnapshot` внутри хода пересобирается**
(`RefreshStrategicKnowledge` → `RefreshOperationalState`, а при смене
`AiMapMemory.KnowledgeVersionFor` — полный `BuildKnown`). Кадр не мутируется, а
**заменяется новым объектом**, с явной проверкой владельца и номера хода. Это правильный
паттерн и главная причина, по которой система в целом устойчива.

---

## 3. Дерево кеша: W (запись) / R (чтение) по фазам

```
════════ КЛАСС A — КАДР ХОДА ════════

WorldSnapshot
  W ← Analysis/WorldAnalysis.Scan                       [T1]  полная сборка
  W ← Analysis/WorldAnalysis.RefreshStrategicKnowledge  [L1]  при бампе KnowledgeVersion
  W ← Analysis/WorldAnalysis.RefreshOperationalState    [L1]  иначе (Self/TrueWorld/Economy/Threat)
  R ← ВСЕ слои (Strategy, Missions, Allocation, Provisioning, Execution, Continuity, Housekeeping)
  ✓ неизменяем: каждая пересборка — новый объект; Observer + TurnNumber сверяются

════════ КЛАСС B — РЕЕСТРЫ ХОДА ════════

AxisBudgetLedger            W: Create[T6], ReserveFollowup, Debit
                            R: Balance / Initial / UnreservedBalance / DiscreteAdmissionBudget
StrategicResourceReservationLedger
                            W: BeginTurn[T0], Upsert, ReleaseByReason, ReleaseByOwner,
                               ReplaceReasonOwner, ExpireStage, AssertClearAtTurnEnd[T12]
                            R: Active, Spendable*, HasReason, CompletionOwners
                            владелец: State/ ; пишут Strategy/PhaseA, Provisioning, Continuity
ActorCommitments            W: FromIntents[T4,L3] (пересоздание), Claim
                            R: DemandLayer, CapabilityInventory, ReusableArmySelector,
                               Missions/AggressionMissionLayer, Housekeeping
CapabilityInventory         W: Build (производная от snapshot + ActorCommitments)
                            R: DemandLayer
CapabilityPoolExhaustionRegistry
                            W: BeginTurn[T0], MarkExhausted[L6], DeferNoExecutableStep,
                               RevalidateAndClearIfRecovered, BeginRound (ТОЛЬКО Reaction)
                            R: CanAttempt[L6], ProvenPoolWideUnable
GroundCombatAdmissionRegistry
                            W: Record / RecordAttack / RecordReinforcement / RecordActiveDefence [L5]
                            R: TryGet, PairHasDistinctAssignment, EligibleIds
                            ключ: сам объект MissionProposal (ConditionalWeakTable), не игрок
StrategicCapabilityLeaseRegistry
                            W: Mark[T7] (Strategy/PhaseA), Clear[T11] (Housekeeping)
                            R: IsLeased ← Housekeeping/ArmyReorgAnalyzer,
                                          Materialization/MaterializationCandidateBuilder
StrategicInterruptRegistry  W: CaptureTurnContext, Mark, Merge, Consume, Clear (Reaction)
                            R: Peek, HasPending*, TargetIds, Version
StrategicTempoBudget        W: For (ленивый сброс по ходу), RecordAction, RecordGenerationAttempt
V2StateVersion              W: Bump ← Execution/*, Provisioning, Materialization, PhaseB
                            R: IsCurrent ← TaskExecutor (протухание плана), PhaseB (парковка)
                            ГЛОБАЛЬНЫЙ (не по игроку), монотонный

════════ КЛАСС C — ПАМЯТЬ ИГРОКА ════════

AiMapMemory  (251 обращение — крупнейший узел)
  W ← ДОМЕННЫЕ СОБЫТИЯ, вне пайплайна ИИ:
        VisionSystem.VisibilityChanged      → OnVisibilityChanged
        VisionSystem.VisibleContentChanged, StealthSystem.StealthChanged,
        HexEventRegistry.EventConsumed
  W ← OnTurnStarted, MarkScoutDanger, RecordAirReconTarget, MarkRaidPlanRejected
  R ← Analysis 65 | Execution 35 | Strategy/Objectives 32 | Recon 24 | Provisioning 16
      Demand 12 | Reaction 11 | Missions 9 | Orchestration 6 | Continuity 4 | Evaluation 4
  ✓ всё keyed по PlayerSetupData; читается только своя память

AiReconIntelMemory       W: ObserveCurrentVisibility[T1,L1]  R: TryGetIntelAge, Snapshot
                         живой слой — для тактического исполнения
ReconIntelSnapshotRegistry
                         W: Capture[T1,L1]                   R: TryGetIntelAge, LastObservedFor,
                                                                StalePressure
                         задумана как «замороженная копия на ход» — см. дефект C-1
AiReconMemory            W: Observe[T1,L1]                   R: Historical
ScoutTrailRegistry       W: RecordStep (Execution)           R: IsImmediateReversal, RecentTrailHits
ReconPatrolStateRegistry W: GetOrCreate, MarkProgress, Retire R: TryGet, OtherSectorClaims
ReconAirSortieRegistry / AirReconCoverageRegistry / AirSortieRegistry / ReconCapacityDeficitRegistry
                         W/R: Recon + Provisioning + Execution — четыре узла об одном (см. S-2)
ResourceStarvationRegistry
                         W: RecordVerifiedBlock ← Diagnostics[T7,L4],
                            DecayOncePerTurn ← DemandLayer
                         R: Pressure ← WorldAnalysis.Economy, DemandLayer.Economy
                                       (через TaskScoreEvaluator.ResourcePriority)
MissionIntentRegistry/State
                         W: Put, Remove, GetOrCreate, Mark*, Record* ← Continuity (владелец)
                         R: TryGet, Is*Suppressed ← Demand, Missions, Provisioning
AiRadarStateRegistry     W/R: StrategyLayer.Evaluate (сглаживание desire)
InitiativeAnalyticsHistory  W: Record[T12]  R: Initiative/PreTurnCapacityAnalysis
```

---

## 4. Проверка инвариантов

| # | Инвариант | Вердикт |
|---|---|---|
| I1 | Один владелец записи на узел | ⚠️ частично (A-1, S-3) |
| I2 | Изоляция по игроку, knowledge-scoped чтение | ✅ держится |
| I3 | Read-after-write (читается актуальное) | ✅ держится |
| I4 | Write-before-read / заморозка кадра | ✅ держится (первоначальная претензия C-1 снята после проверки читателей) |
| I5 | Жизненный цикл, идемпотентность записи | ❌ нарушен в `ResourceStarvationRegistry` (C-2); латентно было в `StrategicCapabilityLeaseRegistry` (B-1 — исправлено) |
| I6 | Нет дублей одного факта | ⚠️ шесть узлов об «занятости актёра», четыре об авиации (S-1, S-2) |

### I2 — изоляция игроков: проверено поимённо

Все статические изменяемые хранилища в `Assets/Scripts/Ai/V2/**` перечислены и проверены.
Ключ `PlayerSetupData` стоит везде, кроме четырёх, и каждое исключение безопасно по построению:

* `V2StateVersion` — глобальный монотонный `int`. Используется только для сравнения
  «план построен на версии X, текущая версия всё ещё X». Монотонность означает, что чужой
  бамп может лишь **инвалидировать** план, никогда — ложно подтвердить. Ходы последовательны,
  поэтому ложной валидации не возникает.
* `InitiativeOutcomeEvaluator.PmfByPool` — кеш распределения кубиков по размеру пула.
  Чистая функция от аргумента, от игрока не зависит.
* `StrategicEffectRegistry.ByAbility` — неизменяемая таблица соответствий.
* `GroundCombatAdmissionRegistry` — ключ `ConditionalWeakTable<MissionProposal, …>`; предложения
  создаются заново каждый раунд и принадлежат одному игроку.

Чтений чужой памяти не обнаружено. Сброс между партиями выполняется в
`Setup/CitadelSetupController` (17 вызовов `Clear`; `AiReconMemory.Clear` каскадно чистит ещё
8 recon-узлов). Порядок корректен: `AiMapMemory.Clear` → `EnsureSubscribed` **до** первого
`Register`, который уже может дёрнуть `VisibilityChanged`.

### I3 — свежесть чтения

Держится за счёт трёх механизмов, и это сильная сторона системы:

1. **Замена кадра, а не мутация.** `RefreshStrategicKnowledge` возвращает новый `WorldSnapshot`;
   старый объект не переиспользуется. Проверки `ReferenceEquals(prev.Observer, player)` и
   `prev.TurnNumber != ctx.TurnNumber` не дают использовать чужой/прошлоходовой кадр.
2. **Версия знания как гейт.** `AiMapMemory.KnowledgeVersionFor(player)` решает, нужна ли
   дорогая пересборка `Known`/`MapKnowledge` или достаточно оперативной части.
3. **Ленивый сброс по номеру хода** — единый идиом `if (state.Turn != turn) state = new …`,
   применён в `CapabilityPoolExhaustionRegistry`, `StrategicTempoBudget`,
   `StrategicCapabilityLeaseRegistry`, `ReconIntelSnapshotRegistry` (на чтении),
   `AiAllocatorStateRegistry`. Большинство узлов за счёт этого самозаживляются.

После каждого шага исполнения пересобираются: кадр → recon-объективы → активные интенты →
`ActorCommitments` → деманды. То есть производные класса B не переживают шаг, который мог
их обесценить.

---

## 5. Дефекты

### C-1 — СНЯТО ПОСЛЕ ПРОВЕРКИ ЧИТАТЕЛЕЙ (был P1, оказался расхождением документации)

Первоначальный вывод: `ReconIntelSnapshotRegistry` объявляет себя копией, замороженной
«once per player/turn», а `Capture` безусловно перезаписывает запись и вызывается из
`RefreshStrategicKnowledge`, т.е. на каждом раунде mid-turn loop со сменой `KnowledgeVersion`.
Предлагалось заморозить запись строго один раз за ход.

**Проверка читателей показала, что это сломало бы логику, и что наблюдаемого вреда нет.**

1. `ReconObjectiveEvaluator.RefreshAt` и `BuildRefreshObjectives` отбрасывают гекс, чей
   `age < scoutSurveilStaleTurnsLo`, а `ScoutObjectiveEvaluator` определяет по тому же возрасту,
   удовлетворён ли durable Refresh-интент. Оба читают именно эту копию. При строгой заморозке
   разведчик, только что обновивший гекс, не увидел бы этого до конца хода — ИИ переоткрывал бы
   уже выполненную работу и не закрывал бы интент. Это была бы регрессия, а не фикс.
2. Приписанный эффект «10–12 подряд `Scout(Refresh)` за ход» оказался неверно прочитанным логом:
   `[Loop] step=` — это ОДИН шаг перемещения, а не миссия. T16 Mordak: `actor=#13` 8 шагов Explore,
   `actor=#17` 3 шага Explore, `actor=#21` 8 шагов Refresh, серия заканчивается `stop=OutOfMovement`.
   Это нормальный ход трёх разведчиков по своим MP, портфель внутри хода не колебался.

Фактический дефект здесь — только текст контракта: комментарий узла обещал стабильность, которой
нет и быть не должно. Комментарий заменён на честное описание («копия на ревизию знания, а не на
ход»), с указанием, какие читатели зависят от внутриходовой свежести и что именно узел гарантирует
(изоляция от живой тактической памяти + гейт `Turn == snapshot.TurnNumber` на чтении).

Инвариант I4 по этому узлу переоценён как ✅ выполненный: гейт по ходу на чтении есть,
внутриходовая перезапись — часть контракта, а не его нарушение.

### C-2 (P1, НЕ ТРОГАЛ — это F4 у параллельного агента). `ResourceStarvationRegistry` — давление растёт от числа раундов цикла, а не от дефицита

```
StrategicPhaseA «no feasible useful chain»
   └─► MaterializationDiagnostics ─► RecordVerifiedBlock ─► AddPressure(+starvationHitGain 0.34, clamp01)

затухание: DecayOncePerTurn (×0.6) — ОДИН раз за ход (гейт по номеру хода есть)
запись:    гейта по ходу НЕТ → столько раз, сколько отказов за ход

в логе за один ход: 10…31 идентичный отклонённый ECO-деманд
   ⇒ 3 отказа насыщают pressure до 1.0
   ⇒ TaskScoreEvaluator.ResourcePriority = Max(…, clamp01(pressure), …) = 1.0
```

**Наблюдаемый эффект:** T1 Orlan, без единого изменения в мире: `D02 … priority=0,5 val 8,5`,
через два отказа — `D04 … priority=1 val 10,0`. Счётчик раундов внутреннего цикла протекает
в ось Economy. Дальше: T6 Mordak 19 демандов, T9 Mordak 31, T10 13, T12 17 — все с priority,
задранным до потолка.

**Корень:** асимметрия жизненного цикла внутри одного узла — затухание идемпотентно по ходу,
запись нет.

**Где чинить:** `State/ResourceStarvationRegistry` — учёт один раз на `(игрок, ресурс, ход)`;
повторные свидетельства в том же ходу только уточняют `CurrentBlocks` (побеждает более сильное,
как сейчас), но не добавляют давление. `Diagnostics/` и `Strategy/` не трогаются.

**Побочно:** `starvationEconomyTrigger`, `starvationEconomyValueBonus` (+35 на шкале, где
значения TaskScore 7–15) и `starvationResidualPreservationMax` — мёртвые константы, не читаются
нигде. Их надо удалить, чтобы они не вернулись в расчёт «по инерции».

### B-1 (P2, латентный) — ИСПРАВЛЕНО. `StrategicCapabilityLeaseRegistry.IsLeased` без гейта по ходу

```
Mark(player, turn, …)    — гейт есть: state.Turn != turn → новое состояние
Clear(player, turn)      — гейт есть: удаляет только если state.Turn == turn
IsLeased(player, armyId) — ГЕЙТА НЕТ: читает ArmyIds независимо от state.Turn
```

Сегодня это не стреляет: `HousekeepingManager.RunHousekeeping` вызывает `Clear` в конце каждого
хода, ранних выходов до этой точки нет, порядок «`Run(...)` → `Clear`» правильный (лизинг
переживает ровно ту фазу, ради которой создан). Но читатели `IsLeased` —
`Materialization/MaterializationCandidateBuilder` (Phase A, **начало** хода, до первого `Mark`)
и `ArmyReorgAnalyzer`. Любой ранний выход выше `Clear` оставит прошлоходовой лизинг живым, и
Phase A исключит армию из кандидатов без причины.

**Сделано:** `StrategicCapabilityLeaseRegistry.IsLeased` принимает `turn` и сверяет
`state.Turn == turn` — теми же воротами, что `Mark` и `Clear`. `turn` протянут до обоих читателей:
`ArmyReorgAnalyzer.Analyze` берёт его из `snapshot?.TurnNumber ?? ctx?.TurnNumber ?? 0` и передаёт
через `BuildContainer`/`ClassifyRole`; `MaterializationCandidateBuilder.IsProtectedFromPhaseBSurplus`
получает `snap.TurnNumber` от обоих своих вызовов. Обновлены два теста в `Assets/Editor`
(`AiCollectorCapabilityDeliveryTests`, `AiEconomyDecisionTests`) — теперь они проверяют и гейт по ходу.
Инвариант стал структурным, а не следствием удачного порядка вызовов.

### A-1 (P3, консистентность). `Analysis/` пишет в память игрока

`WorldAnalysis.Scan` и `RefreshStrategicKnowledge` вызывают `AiReconMemory.Observe`, который
пишет в три узла класса C. `ARCHITECTURE.md` определяет `Analysis/` как *«Read-only world scan
and derived facts»*.

Это осознанный «observation seam», задокументированный в самом `AiReconMemory`. Запись
идемпотентна (перезапись по `ArmyId`, не накопление), функционального дефекта нет. Но формально
слой нарушает собственное определение, и именно через этот шов приехал дефект C-1. Решение —
не переносить код, а зафиксировать в `ARCHITECTURE.md` исключение явным пунктом: `Analysis/`
владеет ровно одним швом наблюдения и пишет только в recon-память наблюдателя.

### Проверено и НЕ является дефектом

* `CapabilityPoolExhaustionRegistry.BeginRound` не вызывается из основного цикла — это
  задокументированное решение (спец. §7): раунд сам по себе не меняет способности, снятие метки
  доказывается пер-пулово через `RevalidateAndClearIfRecovered`, который `CanAttempt` вызывает
  на каждом обращении.
* `DeferredMissions` чистится только на границе хода — тоже осознанно («mission deferred until
  next turn»). Асимметрия с пуловой веткой есть, но она намеренная.
* `V2StateVersion` без сброса между партиями — безопасно, счётчик монотонный.
* `StrategicPhaseB.parkedAt` — локальная переменная метода, не статика, межигроковой утечки нет.

---

## 6. Оценка системы и что можно упростить

### Что сделано хорошо

* **Кадр заменяется, а не мутируется**, с проверкой владельца и хода. Это снимает целый класс
  ошибок «прочитал половину старого состояния».
* **Версия знания** (`AiMapMemory.KnowledgeVersionFor`) как единственный гейт дорогой
  пересборки — дёшево и честно.
* **Единый идиом ленивого сброса по номеру хода** в шести узлах: реестры самозаживляются,
  забытый `Clear` не превращается в утечку между ходами.
* **Изоляция игроков держится без исключений** — при 14 узлах кросс-турновой памяти и 510
  обращениях это не само собой разумеется.
* **Разделение «живая память / замороженная копия»** (`AiReconIntelMemory` ↔
  `ReconIntelSnapshotRegistry`) — правильная идея; сломано только её исполнение (C-1).

### S-1. Шесть узлов отвечают на вопрос «занят ли актёр»

```
ActorCommitments                   — занят durable-интентом
CapabilityInventory                — производная: сколько свободной ёмкости осталось
StrategicCapabilityLeaseRegistry   — защищён от housekeeping / переиспользования
GroundCombatAdmissionRegistry      — какие армии рассматривались под конкретное предложение
MaterializationReservation         — зарезервирован под цепочку материализации
StrategicResourceReservationLedger — его AP/ресурсы зарезервированы под владельца + причину
```

Формально дублей нет: каждый отвечает на свой вопрос, три из шести — чистые производные.
Но вызывающему приходится спрашивать по очереди у нескольких, и именно из этой развилки растёт
находка F1 аудита механик (Attack видит только «не занятые по `ActorCommitments`» и не имеет
доступа к арбитражу ценности). Предложение — не сливать узлы, а ввести один фасад-читатель
поверх них (`ActorAvailability.For(snap, armyId)` → структура флагов с причиной), чтобы новый
потребитель физически не мог забыть один из шести источников. Узлы-владельцы остаются как есть.

### S-2. Четыре узла состояния авиаразведки

`AirSortieRegistry` (в `State/`), `ReconAirSortieRegistry` и `AirReconCoverageRegistry`
(оба в `Recon/ReconAirSortieState.cs`), `ReconCapacityDeficitRegistry` (в `Analysis/`).
Здесь смысл действительно частично пересекается (кто в воздухе / что покрыто / чего не хватает),
и три из четырёх живут вне `State/`, которому по `ARCHITECTURE.md` принадлежат реестры.
Кандидат на консолидацию — но только после возврата текущих фиксов: узлы активно читаются
пайплайном авиаразведки.

### S-3. Десять реестров живут вне `State/`

`AiAllocatorStateRegistry` (`Allocation/`), `ReconCapacityDeficitRegistry` (`Analysis/`),
`MissionIntentState` (`Continuity/`), `StrategicCapabilityLeaseRegistry` (`Housekeeping/`),
`InitiativeAnalyticsHistory` (`Initiative/`), `ReconPatrolStateRegistry`,
`ReconAirSortieRegistry`, `AirReconCoverageRegistry` (`Recon/`), `AiRadarStateRegistry`
(`Strategy/Desire/`) — плюс четыре телеметрических в `Diagnostics/` (эти легитимны: `Diagnostics/`
владеет своей телеметрией по нормативу).

Функционального риска нет, но правило «`State/` владеет реестрами» сегодня описывает меньше
половины реестров. Либо переносить, либо уточнить норматив: реестр живёт рядом со своим
единственным владельцем, а `State/` — для тех, у кого владельцев несколько. Второе честнее
отражает фактическую архитектуру и не требует правок кода.

---

## 7. Что перепроверить после возврата фиксов

| Узел | Почему |
|---|---|
| `ResourceStarvationRegistry` | дефект C-2 — фикс меняет семантику записи |
| `MissionIntentRegistry` / `MissionContinuityLayer.AdvanceRaidPhase` | F1 меняет ре-ориентацию рейда |
| `ActorCommitments` | F1 добавляет читателя со стороны Attack-деманда |
