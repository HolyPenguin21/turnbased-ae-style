# AI V2 — аудит кеша: кто пишет, кто читает, в каком порядке

Аудит проведён по состоянию `master` = `375b5ce9`, лог партии
`Logs/AiDebug.log` от 2026-09-23 (16 ходов × 2 ИИ). Привязка к именам классов и методов,
без номеров строк — чтобы параллельные правки не ломали документ.

Нормативная база — `Assets/Scripts/Ai/V2/ARCHITECTURE.md`.

**Повторно сверено против `372dc253` (PR #108)** — см. раздел 7.

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
                            #108: Attack-интент претендует на primary и support; рейд в фазе
                            Return с CompletedTargetAwaitingFreshDecision НЕ претендует —
                            актёр возвращается в общий пул сразу после закрытия цели
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
                         #108: AddPressureOncePerTurn — одна прибавка на (игрок, ресурс, ход)
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
| I1 | Один владелец записи на узел | ✅ держится (три модели владения, см. ниже) |
| I2 | Изоляция по игроку, knowledge-scoped чтение | ✅ держится |
| I3 | Read-after-write (читается актуальное) | ✅ держится |
| I4 | Write-before-read / заморозка кадра | ✅ держится (первоначальная претензия C-1 снята после проверки читателей) |
| I5 | Жизненный цикл, идемпотентность записи | ❌ нарушен в `ResourceStarvationRegistry` (C-2); латентно было в `StrategicCapabilityLeaseRegistry` (B-1 — исправлено) |
| I6 | Нет дублей одного факта | ✅ дублей нет; вопрос только к именованию и размещению (S-1, S-2) |

### I1 — владение записью: три легитимные модели, а не одна

Проверены четыре узла с наибольшим числом обращений. Ни одного случая «пишет кто попало» нет —
есть три разные, внутренне последовательные модели владения:

**Единственный писатель.** `MissionIntentRegistry`/`MissionIntentState`: все настоящие мутаторы
(`Put`, `Remove`, `MarkReconActorTrimmed`, `Record*DeliveryFailure`, `TryConsumeReconLaneTrim`)
вызываются только из `Continuity/`. Остальные слои получают состояние через `GetOrCreate` и
читают `.All`/`TryGet`. Ровно то, что предписывает норматив.

**Многие писатели, но каждая запись owner-scoped.** `StrategicResourceReservationLedger`:
25 точек записи в семи слоях, и это контракт, а не расползание — каждое обязательство держит свои
строки, ключёванные владельцем и причиной. Все кросс-владельческие операции
(`ReleaseByOwner`, `ReleaseByReason`, `ReplaceReasonOwner`, `ExpireStage`) принимают и владельца
(или причину), и `turn`; чужие строки ими не задеваются. На выходе хода стоит детектор утечки —
`AssertClearAtTurnEnd` логирует `[ERROR] reservation leak` и принудительно чистит. В разобранной
партии он не сработал ни разу (0 срабатываний на 32 полухода), то есть владельцы честно
освобождают свои строки сами.

**Много производителей, один потребитель (почтовый ящик).** `StrategicInterruptRegistry`:
пишут `Analysis` (публикация фактических дельт шага — `PublishStepObservationDelta`), `Execution`
(что обнаружило/изменило выполненное действие) и `Strategy/PhaseB`; читает, потребляет и чистит
только `Reaction`. Единственный владелец жизненного цикла — потребитель, и это корректно.

Оговорка та же, что в A-1: табличка папок в `ARCHITECTURE.md` называет `Analysis/` read-only,
а таблица mid-turn-цикла в том же документе поручает ему «reports factual deltas» — то есть
запись в этот реестр. Противоречие в нормативе, не в коде.

### I6 — дублей нет; четыре «авиационных» узла оказались о разном

Первоначально я записал в дубли четвёрку `AirSortieRegistry` / `ReconAirSortieRegistry` /
`AirReconCoverageRegistry` / `ReconCapacityDeficitRegistry`. Проверка показала, что общего факта
они не хранят:

```
AirSortieRegistry          (State/)    физический вылет ЛЮБОГО назначения (Strike/Recon/Rebase):
                                       куда летит, где сядет; отсюда же учёт посадочных слотов
ReconAirSortieRegistry     (Recon/)    разведдуга ОДНОГО вылета: след, фаза, выбранная посадка;
                                       ретайрится при приземлении — воздушный аналог ReconPatrolState
AirReconCoverageRegistry   (Recon/)    гекс -> какие вылеты его уже покрыли (дедуп покрытия)
ReconCapacityDeficitRegistry (Analysis/) счётчики устойчивости дефицита ёмкости — к авиации
                                       отношения не имеет вообще, я сгруппировал его ошибочно
```

Пересечение есть только между первыми двумя, и оно разделено чисто: «физика полёта» против
«разведывательная дуга». Консолидировать нечего. Остаётся вопрос именования и размещения —
три из четырёх лежат вне `State/` (см. S-3), и по названиям неочевидно, кто из них о чём.

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

### C-2 (P1) — ИСПРАВЛЕНО в #108. `ResourceStarvationRegistry` — давление растёт от числа раундов цикла, а не от дефицита

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

**Сделано в #108:** `AddPressure` заменён на `AddPressureOncePerTurn` с ключом
`LastPressureHitTurn[resource]`: одна прибавка на `(игрок, ресурс, ход)`, повторные свидетельства
того же хода только уточняют `CurrentBlocks` через `StrongerThan`. Ровно то, что предлагалось.

Мелкое замечание на будущее: путь `RecordBlock` (не-verified) передаёт в качестве хода
`s.LastDecayTurn`. Он совпадает с текущим ходом, потому что `DemandLayer.Generate` вызывает
`DecayOncePerTurn` в начале каждого хода. Если этот вызов когда-нибудь уедет, дедупликация
начнёт ключеваться по устаревшему номеру и подавит легитимную прибавку следующего хода.
Продакшн-путь (`RecordVerifiedBlock`) получает настоящий `turn` и этой зависимости не имеет.

**Побочно (сделано здесь же):** `starvationEconomyTrigger`, `starvationEconomyValueBonus`
(+35 на шкале, где значения TaskScore 7–15) и `starvationResidualPreservationMax` удалены —
читателей у них не было, они описывали до-TaskScore'овый дизайн.

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

### S-2. Именование авиационных узлов (пересмотрено)

Первоначальная претензия «четыре узла об одном» снята — см. I6 выше, факты у них разные.
Остаётся эргономика: `AirSortieRegistry` и `ReconAirSortieRegistry` различаются одним словом в
имени, лежат в разных папках и хранят разные вещи. Достаточно переименования
(`AirSortieRegistry` → физический вылет, `ReconAirSortieRegistry` → разведдуга) и перечисления
в `ARCHITECTURE.md`; трогать код не нужно.

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

## 7. Повторная сверка после PR #108 (`372dc253`)

Три помеченных узла проверены заново. Новых статических хранилищ PR не вводит, поэтому дерево
разделов 2–3 остаётся в силе; изменились только отдельные рёбра.

| Узел | Что изменилось | Вердикт |
|---|---|---|
| `ResourceStarvationRegistry` | `AddPressureOncePerTurn`, ключ `LastPressureHitTurn` | ✅ C-2 закрыт, I5 выполняется |
| `MissionContinuityLayer.AdvanceRaidPhase` / `MissionIntentRegistry` | ре-ориентация на следующего нейтрала удалена; завершённая цель переводит интент в Return как fallback, ре-кей интента в этом пути исчез | ✅ W-рёбер стало меньше, churn ре-кея ушёл |
| `ActorCommitments` | Attack претендует на primary/support; рейд в Return с `CompletedTargetAwaitingFreshDecision` актёра не удерживает | ✅ актёр возвращается в общий пул сразу |

`StrategicCapabilityLeaseRegistry` (B-1) и `ReconIntelSnapshotRegistry` (C-1, контракт) правились
в этой ветке и пережили rebase на #108 без конфликтов; других пересечений с PR нет.

### Что осталось открытым после #108

**1. Асимметрия деманда Attack против Raid (остаток F1).** `Strategy/Demand/AggressionDemandEvaluator`
PR не трогал, и `AttackObjective` в нём по-прежнему не упоминается: цикл выбора `chosen` перебирает
только рейдовые `objectives`. Для НЕсвязанной Attack-цели деманд на `FieldCombatPower` не создаётся,
тогда как для рейдовой создаётся (`reason=free_field_power_below_requirement`).

Главную половину F1 PR закрыл: актёры больше не заперты в рейдовой «беговой дорожке», и после
каждой закрытой цели Attack честно конкурирует за освободившуюся армию через TaskScore. Но два
случая остаются:
* все рейды в полёте (цели не закрыты) несколько ходов подряд + ценная Attack-цель → `freePower=0`
  → REJECT без деманда;
* Attack-цели нужно БОЛЬШЕ силы, чем есть у любой свободной армии (защищённая база,
  `requiredPower` ~18) → оценщик не проходит, деманда на добор дефицита нет.

**2. Потолок разведки в проде упал с 3 до 2.** Это следствие снятия focus-режимов (F3), а не
самостоятельное решение: раньше `Mode = ReconAggressionEconomyDevelopment` давал
`IsFocusScoped == true`, и `ReconConcurrencyPolicy.HardCap` возвращал
`reconConcurrencyReconOnlyHardCap = 3`. Теперь путь один — `maxConcurrentReconExecutions = 2`.

Партия из лога шла как раз на трёх разведчиках и всё равно закончила 16-й ход с картой,
тёмной на 46–60 % (Mordak 0.46, Orlan 0.60; frontier 12–18 гексов). На двух разведчиках
исследование будет медленнее. Это вопрос баланса, а не корректности, и проверяется он только
игрой: если тёмная доля к 16-му ходу вырастет, `maxConcurrentReconExecutions` — та самая ручка,
которую надо поднимать осознанно, а не через возврат focus-режима.

---

## 8. Статус

Аудит кеша закрыт. Пройдено: инвентаризация узлов и их API, спина фаз хода, дерево W/R,
шесть инвариантов, повторная сверка против #108.

Найдено три дефекта: **B-1** (гейт по ходу в `IsLeased`) — исправлен здесь; **C-2**
(идемпотентность давления голода) — исправлен в #108; **C-1** — снят после проверки читателей,
остался как исправление лживого комментария. Инварианты I1–I6 выполняются.

Не сделано осознанно, вынесено за рамки: предложения S-1 (фасад доступности актёра),
S-2 (переименование авиационных узлов), S-3 (реестры вне `State/`) — это рефакторинг
эргономики, а не дефекты. Два открытых пункта из сверки с #108 (деманд Attack, потолок
разведки 3→2) относятся к аудиту механик, а не кеша, и разбираются отдельно.
