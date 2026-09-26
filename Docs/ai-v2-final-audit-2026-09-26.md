# AI V2 — финальный аудит механик, осей желаний и кешей (2026-09-26)

Источник: `Logs/AiDebug.log` (сессия 09:42–09:44, Grimm = P0, Sable = P5, ходы 1–3),
код `master` @ `698379f4` + фикс F1 из этого аудита.
Уровни названы по `Assets/Scripts/Ai/V2/ARCHITECTURE.md` (Orchestration / Analysis / Strategy
{Desire, Objectives, Demand, PhaseA, PhaseB} / Materialization / Missions / Allocation /
Provisioning / Execution / Continuity / Housekeeping / Reaction / State / Diagnostics).

Обозначения в графах: `R[x]` — чтение кеша/реестра x, `W[x]` — запись, `RW[x]` — оба,
`⟂` — гейт (может остановить ветку), `↺` — возврат в цикл.

---

## 1. Ходы ИИ по логу

| Ход | Игрок | AP | Что сделал | Альтернативы | Оценка |
|---|---|---|---|---|---|
| T1 | Grimm | 10 | Lira Sable (3 AP, 1 H) → наземный скаут; 4 шага разведки; добор 3 карт (6 AP) | Экономики ещё не было (карта 99% тёмная, `no_legal_valuable_site`); героям под ECO нужен H=1, а H=0 | ✅ Корректно: Recon→Economy. Добор — единственный легальный расход |
| T1 | Sable | 10 | Hooded + Nora Blackwell → 2 скаута (дефицит 2 = hard cap 2); ECO(0,4) заблокирован NoMover | Тот же расклад, что у Grimm | ✅ Корректно |
| T2 | Grimm | 10 | Watcher Drone → скаут; Light Infantry → AGG (4.85 из 19.7 для Guard@-5,4); добор 2 | Герои: `NotCheaperThanReadyHero` (new=10 > ready=6), затем H=0 | ✅ Корректно |
| T2 | Sable | 8 | Vera Sterling → ECO-строитель; построен Energy Extractor (0,4); разведка | Прочие ECO-спросы: нет карт/AP | ✅ Корректно, первый экстрактор |
| T3 | Grimm | 10 | Nadia + AA Crawler → Cinderwolves; рейд на Guard@-4,1 (winChance 0.96), победа; Dorian → Energy Extractor (-4,4) | Kessa/Vector: H=0, потом не хватило AP | ✅ Корректно; 5 повторных RETURN — армия без хода, безвредно |
| T3 | Sable | 8 | **Iri Vane (3 AP) под ECO Hero @(4,0) → `delivered 0`**; Swarm вернулся домой; Housekeeping свернул обоих героев в гарнизон | Цепочка была осуществима; отказ дала пост-проверка из-за cheat-алерта | ❌ **F1** — 3 AP и карта потрачены без строителя |

---

## 2. Пайплайн хода (Orchestration → …)

```
AiStrategyV2Pipeline.RunTurn(player)                                   [Orchestration]
│
├─ W[V2TurnActivityTelemetry] W[CapabilityPoolExhaustion.BeginTurn] W[ReservationLedger.BeginTurn]
├─ WorldAnalysis.Scan ───────────────────────────────────────────────── [Analysis]
│    R[AiMapMemory] R[AiReconMemory] R[TrueWorld*] W[AiReconMemory.Observe]
│    Self · Known · MapKnowledge · Economy · Threat(Contacts/Assets/Threats) · Development
├─ W[ForceBaselineRegistry.RecordStart]  (однократно на игру)
├─ StrategyLayer.Evaluate ─────────────────────────────────────────────── [Strategy/Desire]
│    RW[AiRadarState] (сглаживание, loss-pulse, 1 раз за ход) → Radar (замороженный на ход)
├─ ReconObjectiveEvaluator.Enumerate / AggressionObjectiveEvaluator.Enumerate [Objectives]
├─ MissionContinuityLayer.ResolveActive ───────────────────────────────── [Continuity]
│    RW[MissionIntentRegistry] → activeIntents → ActorCommitments.FromIntents [State]
├─ DemandLayer.Generate  Recon → Aggression → Economy → Development ────── [Demand]
│    W[ResourceStarvation.Decay] W[DevelopmentInvestmentGate.Observe] RW[ReconCapacityDeficit]
├─ ApBudgetLedger.Create(AP)                                               [State]
├─ StrategicManager.FulfillDemands  (Phase A) ─────────────────────────── [Strategy/PhaseA]
│    ECO holds → infra pre-pass → PortfolioSolver → Materialization → Delivery/Lease
│    StateChanged? ── да ─→ RefreshStrategicKnowledge → Objectives → ResolveActive → Generate
│
├─ ↺ ТИПИЗИРОВАННЫЙ ЦИКЛ (≤ maxMidTurnStepsPerTurn)
│    TakeTypedTriggers ← R/W[StrategicInterruptRegistry]
│    ├─ strategic re-admission (Economy/Development) ⟂ fingerprint не изменился
│    │    → Phase A повторно (тот же ApBudgetLedger)
│    ├─ Missions: Recon/Aggression/Economy/Development planners → MissionProposal [Missions]
│    ├─ ResourceAllocator (RadarValueScale × BaseValue, AP pool) ───────── [Allocation]
│    │    RW[AiAllocatorState]  ⟂ «no funded typed mission» → выход из цикла
│    ├─ Provisioning.PreparePass / Provision ─────────────────────────── [Provisioning]
│    │    R[ActorCommitments] RW[ProvisioningSession.Claimed] W[CapabilityPoolExhaustion]
│    ├─ Execution: один шаг (TaskExecutor / ReconGround / ReconAir) ─── [Execution]
│    │    W[V2StateVersion.Bump]
│    ├─ RefreshStrategicKnowledge + CaptureStepObservation + PublishStepObservationDelta
│    │    W[StrategicInterruptRegistry] (типизированные инвалидации)
│    └─ ReconcileStep / ReconcileOutcome → MissionIntent ────────────── [Continuity]
│
├─ management rounds: Phase B (UseSurplus: draw / tempo / hold) ───────── [Strategy/PhaseB]
│    RW[StrategicTempoBudget]
├─ cold-Radar residual (оси с нулевым радаром, только если есть остаток)
├─ ReconcileAfterTurn → Reaction (StrategicReactionPass) ──────────────── [Reaction]
├─ HousekeepingManager.Run (0 AP, упаковка на одном гексе) ────────────── [Housekeeping]
│    R[StrategicCapabilityLeaseRegistry] (защита лизингом)
└─ ReservationLedger.ExpireStage / AssertClearAtTurnEnd; Telemetry.LogSummary
```

Проверка на пробелы: у каждой ветки цикла есть выход (нет funded → stop; нет инвалидации →
stop; лимит шагов/no-progress → bounded stop). Phase A повторно входит только через
fingerprint (нет повторного входа на собственные записи Economy — они исключены из ключа).

---

## 3. Графы механик

### 3.1 Recon (Scout)

```
Analysis: MapKnowledge.ExplorableUnknownFrac, Threat.Contacts(honest), ReconIntelSnapshot
   │
   ├─ Desire RCN = clamp(wE·explore + wS·refresh + wB·blindness)      [Desire]
   │     blindness: TrueWorld.Opponents fielded ∧ ни одного honest-контакта с позицией (санкц. cheat)
   ├─ ReconObjectiveEvaluator: Explore / Surveil / Refresh / AirSweep [Objectives]
   └─ DemandLayer.Recon                                                 [Demand]
         jobs(raw → covered → runnable) → capacity(desiredObs, desiredGround, hard cap)
         ├─ deficit=0 ─────────────────────────────→ DEFER usable_capacity_covers_all_lanes
         ├─ activeGround ≥ hard ───────────────────→ DEFER concurrency_hard_cap
         ├─ supply=0 ──────────────────────────────→ PROMOTE zero_capacity_bootstrap → CREATE
         └─ deficit>0, streak<N ───────────────────→ DEFER not_yet_persistent
               (эмитится «persistence-deferred» спрос: Phase A исполнит его только если нет
                альтернативной работы — сверка в PhaseA)
                    │
   Phase A: Scout-карта (Watcher Drone/Hooded/герой) → NewArmy → W[Lease ScoutCapability]
   Missions: ReconMissionPlanner → Allocation → ReconAssignmentPlanner (batch 1 актор/1 работа)
         ⟂ MoverContended / NoExecutableStep → CapabilityPoolExhaustion (до след. хода)
   Execution: ReconGroundExecutor.Pick (trail, detectorRisk, heading, coverage)
         R/W[ScoutTrailRegistry] RW[ReconPatrolState]
   Continuity: ApplyScoutPayload; waypoint met → re-focus, durable role сохраняется
   Air: только AirSweep; ReconAirCapacityPolicy + AirSortieRegistry + ObligationStall
```
Лог: 28/28 шагов ReconGround OK, «waypoint satisfied externally → re-focus» работает;
отказы — MoverContended (работ больше, чем скаутов) — ожидаемо. Пробелов не найдено.

### 3.2 Economy (BuildExtraction / FoundBase / Collector / ReturnBuilder)

```
Analysis.Economy: PerType(DeficitScore), ExtractionOpportunities{BuilderRoutes}, BaseOpportunities,
                  EconomicSecurity, HasActionableOpportunity
   │
   ├─ Desire ECO = (wMax·maxDeficit + wMean·meanDeficit) × (actionable ? 1 : latent)
   └─ DemandLayer.Economy                                                [Demand]
        per site: EconomicInfrastructure (+1fu) | CollectorCapability | Hero(prereq mobile_hero)
        builder = SelectEconomyBuilder → RankEconomyBuilders:
            EconomyBuilderCandidates ── CandidateRejection ⟂ ─────────────┐
               not_mobile_economy_builder / protected_assignment / claimed │
               / under_immediate_threat  ◄── EconomyBuilderUnderImmediateThreat (F1)
            → AssessEconomyArmy (RW[EconomyAssessmentCache per snapshot]) ⟂ Ineligible
            → loan gate (EconomyLoanAllowed) → сортировка
        нет готового → Hero-спрос (alternative=new_hero, EconomyReadyDeliveryCost)
   │
   Phase A:
     ECO hold (W[Reservation EconomyDeferredBuild]) → infra (строит, если строитель на гексе)
     Hero-спрос → Materialization → MaterializationDeliveryPolicy.AssessDemandOperationally
            (проекция армии → EconomyBuilderRoutes → AssessEconomyArmy → EconomyNewHeroWorthIt)
          → ИГРА КАРТЫ (необратимо)
          → CapabilityDeliveryEvaluator.FinalizeOperationalDelivery
               → EconomyDeliveryChoice → SelectEconomyBuilder (полный Rank, ВКЛ. CandidateRejection)
               ⟂ NOT_DELIVERED → residual не закрыт (AP потрачены)            ◄── F1/F2
               ✓ BeginEconomyDelivery → MissionIntent(Economy) + W[Reservation]
   Missions: EconomyMissionPlanner → Allocation → ProvisionEconomy (CandidateRejection live)
   Execution: RunGroundTransportStep → на гексе → Phase A infra строит (ap 1)
   Continuity: после постройки → RequiresEconomyBuilderRecovery (threatened?) → ReturnBuilder
```
Пробел F1/F2: до игры карты проверяется только `AssessEconomyArmy`, после — полный
`RankEconomyBuilders`. Два разных гейта на один вопрос «станет ли герой строителем».

### 3.3 Aggression (Raid / Attack / ActiveDefence / гарнизон)

```
Analysis: CombatOpportunityAnalyzer (нейтралы), AttackObjectiveEvaluator (враж. база/фасилити),
          Threat.Threats (honest → ActiveDefence; все → DefensiveReserve)
   │
   ├─ Desire AGG = max( offensive, activeDefence )
   │     offensive = hasKnownTarget ? clamp(max(raidOpp, war, attack)) × siegeDamp : 0
   │       raidOpp = opp·w + surplus·w + relEdge·w + momentum·w
   │       war     = surplus·w + ecoGate·w + relEdge·w      (ecoGate ← EconomicSecurity)
   │       attack  = attackOpp·w + war·w + surplus·w + relEdge·w
   │       surplus = ramp((TotalPower − DefensiveReserve)/TotalPower)
   │     activeDefence = max severity по honest+позиционным не-нейтральным контактам
   ├─ AggressionObjectiveEvaluator: ACCEPT/REJECT (readyWin, asmWin, gate, frozenGap)
   └─ DemandLayer.Aggression: FieldCombatPower (IndependentFieldArmy / Garrison shape)
   Phase A: юниты/герои → ExistingArmy/NewArmy → TryHandoffGroundCombatSupport
   Missions: AggressionMissionPlanner (Raid/Attack/ActiveDefence/Return) + GroundCombatAdmissionPolicy
   Allocation → Provisioning: GroundCombatAssaultTransaction / AssemblyTransaction
   Execution: raid step … BattleStarted
   Continuity: Raid phases; target completed → ABANDON → Return (fresh-decision fallback)
```
Лог (Grimm T3): подкрепление Cinderwolves → удар по Guard@-4,1 (winChance 0.96) → победа →
Return предлагается каждый шаг цикла, но у армии 0 MP → в конце `Blocked`, продолжение на
следующем ходу. Логически корректно; 5 повторных предложений — только накладные расходы.

### 3.4 Development (Production/Research)

```
Analysis.Development (readiness) ─→ Desire DEV = surplus × max(readyQ, latentQ) × gain
DevelopmentInvestmentGate.Observe (headroom ≥ threshold N ходов подряд)  W[per player]
DevelopmentOpportunityEvaluator.Enumerate: READY upgrade / PREPARE facility+operator
   ⟂ gate closed по ресурсу цепочки  ⟂ оператор под immediate threat (тот же гейт, что F1)
DemandLayer.Development → Phase A residual infra pass → DevelopmentMissionPlanner → Provisioning
```
В логе DEV = 0 всю игру (`no_facility_card`, `pathViable 0`) — ветка не активировалась.

### 3.5 Phase B (surplus / tempo)

```
StrategicPhaseB.UseSurplus  R/W[StrategicTempoBudget: total/draws/gen]
  кандидаты: Draw ⟂ AP<cost | Hold ⟂ «scout сверх физического портфеля» | Play surplus
  ⟂ spendable = AP − reservations (StrategicSpendability)
```
Лог: добор только при отсутствии полезной игры; hold лишних скаутов — корректно.

### 3.6 Housekeeping

```
HousekeepingManager.Run (конец хода, 0 AP)
  R[StrategicCapabilityLeaseRegistry] → защищённые армии не трогаются
  группа на гексе + benchmark (видимые враги, eta) → fold/sort → apInvariant
```
Лог (Sable T3): свернул #14 (Vera) и #17 (Iri Vane) в гарнизон при видимом враге eta 2.
Для #17 лизинга не было именно из-за F1 (NOT_DELIVERED ⇒ нет Continuity-владельца).

---

## 4. Оси желаний: графы и попарное сопоставление

```
          ┌──────────── Recon (знание) ─────────────┐
          │ explore + refresh + blindness            │
          ▼                                          ▼
  Economy.HasActionableOpportunity          Aggression.hasKnownCombatTarget
  (latent ×, пока сайтов нет)               (0, пока цели нет)
          │                                          ▲
          ▼                                          │
  Economy.EconomicSecurity ── ecoGate ──→ warPressure/attackPressure
          │                                          │
          ▼                                          ▼
  Development.surplus (ресурсный избыток)   DefensiveReserve → surplus(AGG)
          │
          ▼
  Development.quality (targets/offerings)  ← НЕ читает спрос AGG (см. п. 4, пара AGG↔DEV)

  Radar = normalize(smoothed raws);  EffectiveValue = BaseValue × 4·weight(axis)
  Radar заморожен на ход; mid-turn обновляются только lane-pressures Recon и Aggression.
```

| Пара | Связь в коде | Соответствие принципу | Замечание |
|---|---|---|---|
| RCN ↔ ECO | ECO гейт `HasActionableOpportunity`; Recon persistence-спрос уступает «альтернативной работе» | ✅ Recon → Economy | Радар заморожен: открытые в T1 сайты поднимают вес ECO только со следующего хода (T1: ECO 0.14/0.09). По дизайну (анти-осцилляция) |
| RCN ↔ AGG | offensive = 0 без известной цели; Attack публикует ObservationNeeds → Refresh | ✅ | ActiveDefence только honest+позиция — fog-honest |
| RCN ↔ DEV | ResourceSite-инвалидация будит обе оси | ✅ | — |
| ECO ↔ AGG | ecoGate в war/attack; DefensiveReserve (включая cheat Region) → surplus; займы строителя у Raid/Scout через `EconomyLoanAllowed` | ✅ Economy → бюджет → Attack | **F1**: cheat-Region алерт просачивался в per-actor гейт экономики — исправлено |
| ECO ↔ DEV | DevelopmentInvestmentGate по headroom ресурсов; оба используют один гейт угрозы строителя/оператора | ✅ | — |
| AGG ↔ DEV | DEV desire = surplus × quality(targets) | ⚠️ Частично | По принципу Production должна усиливать уже обоснованную AGG-потребность; сейчас связь только на уровне Objectives («ценность против известных угроз»), не на уровне desire. Решение зафиксировано в ARCHITECTURE («no live mission has to witness the need»); в логе DEV=0 — вреда не видно. Оставлено как открытый вопрос калибровки |

Консистентность кода осей: все четыре оси проходят один `Smooth`, одну `Radar.Normalize`,
один `RadarValueScale`; обновление mid-turn симметрично для Recon/AGG (оба не
ренормализуют радар). Дублирования формул desire не найдено (`RefreshAggressionLanePressures`
повторяет формулы Evaluate — это осознанная копия под замороженный momentum/warPressure;
при изменении весов править оба места).

---

## 5. Дерево кешей: где читается и где пишется (per player)

```
ИГРА (сброс: CitadelSetupController → *.Clear/ClearAll для всех реестров ниже)
│
├─ Перманентная память игрока (Dictionary<PlayerSetupData, …>)
│  ├─ AiMapMemory  {sightings, buildings, resourceHexes, guards, dangerZones, versions}
│  │     W: VisionSystem.VisibilityChanged → OnVisibilityChanged
│  │     R: WorldAnalysis.Scan/Refresh (Known), SafeStepPathing(blockers), Objectives
│  ├─ AiReconMemory / AiReconIntelMemory / ReconIntelSnapshotRegistry
│  │     W: WorldAnalysis.RefreshStrategicKnowledge → AiReconMemory.Observe
│  │     R: BuildThreat (Historical), Desire(RefreshPressure), Recon objectives
│  ├─ AiRadarState          RW: StrategyLayer.Evaluate (1×/ход, LastTurn-штамп)
│  ├─ ForceBaselineRegistry W: pipeline 1-й скан;  R: BuildForceMeasures
│  ├─ MissionIntentRegistry RW: Continuity (ResolveActive/Reconcile/BeginEconomyDelivery)
│  │                         R: ActorCommitments, Delivery, Provisioning
│  ├─ ResourceStarvation / DevelopmentInvestmentGate  W: DemandLayer.Generate (1×/ход)
│  ├─ ReconCapacityDeficitRegistry (streak) RW: DemandLayer.Recon
│  ├─ ReconPatrolState / ScoutTrail / ReconAirSortieState / AirSortie  RW: Recon exec
│  ├─ AiAllocatorState (reject cooldown)  RW: ResourceAllocator
│  └─ InitiativeAnalyticsHistory  RW: Initiative (только аналитика)
│
├─ Ход (штамп turn, BeginTurn в начале RunTurn)
│  ├─ StrategicResourceReservationLedger  W: PhaseA holds, BeginEconomyDelivery, Continuity
│  │        R: StrategicSpendability, Allocation.PhysicalAvailableFor, PhaseB
│  │        ExpireStage + AssertClearAtTurnEnd в конце хода
│  ├─ CapabilityPoolExhaustionRegistry  W: loop (MarkExhausted/DeferNoExecutableStep)
│  ├─ StrategicInterruptRegistry  W: PublishStepObservationDelta; R/Consume: TakeTypedTriggers
│  ├─ StrategicTempoBudget        RW: PhaseB
│  ├─ StrategicCapabilityLeaseRegistry  W: CapabilityDeliveryEvaluator; R/Clear: Housekeeping
│  ├─ AviationObligationStallRegistry   W: loop;  R: Spendability, air recovery
│  └─ ApBudgetLedger (локальный объект хода, не статический)
│
├─ Снапшот (ConditionalWeakTable<WorldSnapshot,…>) — живёт ровно один снапшот
│  └─ DemandLayer.EconomyAssessmentCache  RW: AssessEconomyArmy
│        Корректно: Refresh* всегда создаёт НОВЫЙ WorldSnapshot (проверено), Observer-проверка
│        исключает переиспользование чужого снапшота.
│
├─ Прочие слабые кеши
│  ├─ GroundCombatAdmissionRegistry (CWT<MissionProposal>) RW: Missions → Provisioning
│  └─ GenerationSource.HeroIdentities (CWT<UnitData>)
│
├─ Маршруты: SafeStepPathing._playerCaches[player]
│     инвалидация: смена карты/PathingVersion → всё; RouteMemoryVersion → пересчёт blockers,
│     пути/поля сбрасываются только при изменении множества blockers; hidden-путь не кешируется
│     R: Economy routes, collector admission, provisioning, execution
│
└─ Диагностика (не влияет на решения): AiDebugLog dedup, AiV2Trace, Telemetry, Audit
```

Вывод по кешам: запись и чтение разнесены по владельцам, все реестры ключуются игроком,
турн-штампы защищают от протекания между ходами, CWT-кеши привязаны к неизменяемому
снапшоту. **Исправление (после ревью):** в исходном аудите пропущено нарушение в
`ReconIntelSnapshotRegistry` — копия intel хранилась по ключу Player+Turn, хотя захватывается
на каждой ревизии знаний (`KnowledgeVersion`). Снапшот ревизии V10 после захвата V11 в том же
ходу читал данные V11. Исправлено: ключ Player → Turn → KnowledgeVersion, чтение строго по
`Observer`/`TurnNumber`/`KnowledgeVersion` без подстановки последней ревизии, прошлый ход
удаляется при первом захвате нового (§8).

---

## 6. Находки

### F1 — cheat-Region алерт блокировал экономического строителя (ИСПРАВЛЕНО)

* **Уровень:** Strategy/Demand — `DemandLayer.EconomyBuilderUnderImmediateThreat`
  (единственный канонический гейт; 6 потребителей: Demand CandidateRejection, Continuity
  recovery, ProvisionEconomy ×2, ProvisionDevelopment, DevelopmentOpportunityEvaluator).
* **Корень:** условие `!t.EnemyEta.HasValue || t.EnemyEta <= 1`. `EnemyEta == null` бывает
  только у Region-контакта (cheat, без позиции — инвариант `EnemyContactSnapshot`). Против
  незащищённого актива (соло-герой, HexDefense 0) такой контакт даёт severity
  0.5·(0.35+0.25+0.25/7+0.15) ≈ 0.39 ≥ 0.18 — в логе ровно `severity=0,39`
  (`[Defence][Reserve] enemy=regional_contact`). `UnderSiege` уже требовал `EnemyEta.HasValue`,
  экономический гейт был единственным исключением.
* **Было (Sable T3):** cheat-контакт (Grimm-скаут в радиусе 5) → после игры Iri Vane армия #17
  на (2,3) стала активом под «немедленной угрозой» → `builder_ranking_rejected_actor` →
  3 AP потрачены, спрос открыт, Housekeeping свернул героя в гарнизон.
* **Станет:** Region-алерт по-прежнему наполняет DefensiveReserve (AGG), но не пинит актёра;
  #17 проходит ранжирование → `BeginEconomyDelivery` → интент на (4,0) и резерв ресурсов.
  Честный враг с ETA ≤ 1 блокирует строителя как раньше.
* **Параллельный функционал:** Continuity больше не «спасает» строителя от невидимого врага;
  Provisioning/Development перестают отклонять акторов по cheat-сигналу — это
  выравнивание с fog-honest контрактом. Desire/Reserve/Siege не затронуты.
* **Тест:** `Assets/Editor/AiEconomyBuilderThreatGateTests.cs`.

### F3 — диагностика NOT_DELIVERED (ИСПРАВЛЕНО, только лог)

`CapabilityDeliveryEvaluator` печатал `builder_ranking_rejected_actor` без причины; теперь
добавляется ответ существующего `DemandLayer.EconomyBuilderCandidateRejection`
(`under_immediate_threat` / `claimed` / `ranking_rejected (suitability or loan gate)`).

### F2 — расхождение pre-play и post-play гейтов Economy Hero (ОТКРЫТО, нужен выбор)

* **Уровень:** Materialization — `MaterializationDeliveryPolicy.AssessDemandOperationally`
  (ветка Economy Hero) против Strategy/PhaseA — `CapabilityDeliveryEvaluator.EconomyDeliveryChoice`.
* **Суть:** до игры карты вызывается только `AssessEconomyArmy` для проекции; после — полный
  `RankEconomyBuilders` (CandidateRejection + suitability + loan). После F1 оставшаяся
  разница — `under_immediate_threat` по честному врагу с ETA ≤ 1 к новой армии на базе:
  проекция не является активом снапшота, поэтому до игры этого не видно.
* **Предложение:** в Materialization для проекции спрашивать тот же гейт по гексу развёртывания
  через публичный предикат DemandLayer (без нового скоринга), либо признать «угрозу новой
  армии» частью `AssessEconomyArmy` (одна точка на оба вызова). Второе надёжнее на длинной
  дистанции (один гейт — один ответ), но меняет Demand-слой; нужен ваш выбор.

### Наблюдения без правок
* AGG↔DEV: desire Development не читает AGG-потребность (см. §4).
* Radar заморожен на ход: ECO-вес в T1 остаётся низким даже после открытия сайтов.
* Raid Return повторно предлагается на каждом шаге при 0 MP — накладные расходы, не ошибка.

---

## 7. Реализовано после обсуждения (заменяет F1/F2)

* **Cheat-контакт удалён полностью** (Analysis): `MakeCheatContact`, cheat-ветка `BuildThreat`,
  `ContactSource`, `ContactKnowledge.Region/Unknown`, `RegionCenter/RegionRadius`,
  `PhysicalArmyId`, `threatConfidenceCheatRegion`, `etaUnknownContactPenalty`; мёртвый V1:
  `AiConfig.threatReactionRadius/patrolRadius/makeshiftScoutMinMembers`,
  `AiMapMemory.HasKnownEnemyWithin`, `AiArmyRoles.IsMakeshiftScoutCapable`.
  Все угрозы теперь только честные, у каждой есть позиция и ETA.
* **Единый свидетель угроз экономики** `WorldAnalysis.KnownThreatsAffectingEconomyRoute`:
  армии других игроков (не гарнизоны) ≤1 от маршрута или ≤2 от сайта → эскорт
  (`EconomyRosterSafe`). Нейтралы — только гейт «на сайте» (`KnownHostileAtHex`). Общий для
  строителя, эскорта, сборщика и восстановления строителя после постройки.
* **`ThreatExposure` удалён** (штраф по расстоянию без силы) вместе с
  `AxisDemand.EconomyThreatExposure`, `MobileCollectionOpportunity.ThreatExposure`,
  `BaseSiteValue.Exposure`, `mobileCollectionMaxThreatExposure`.
* **Гейт `EconomyBuilderUnderImmediateThreat` удалён** со всеми 6 вызовами.
* **TaskScore:** новые слоты `CitadelThreatRisk` и `BaseThreatRisk` (отдельно от
  `HexThreatRisk`), источник — `ThreatModel.CitadelThreatSeverity/BaseThreatSeverity`.
  Заполняются только задачами стройки (добыча, база, герой-строитель); сборщики — нет.
  `taskScoreBaseThreatRiskMax = 0` — угроза базе выключена для всех.
* **Открыто:** калибровка `taskScoreCitadelThreatRiskMax = 8`; снижение желания атаковать
  после удаления cheat-резерва — отдельной задачей.

## 8. Исправления после ревью задач (2026-09-26)

* **Air Recon: промежуточный свой аэродром завершал sortie.** `ReconAirExecutor.RunActorStepCore`
  завершал вылет на любом своём аэродроме после взлёта. Теперь правило в
  `ReconAirSortieLifecycle.CompletesAtAirfield`: аэродром + вылет + (фаза Return **или**
  `OutboundCapReached`). Второе условие обязательно: `PlanStep` не переводит крыло в Return,
  стоя на аэродроме, поэтому при лимите, выбранном ровно на аэродроме, крыло ушло бы дальше
  лимита. Это то же правило «разворот домой», что у `ReconAirReservation`.
* **`ReconIntelSnapshotRegistry`** — ключ ревизии знаний (см. §5).
* **`BindFunding` WARN.** Отсутствие proposal у Hard Economy intent, который планировщик
  намеренно пропустил (`EconomyMissionPlanner.DeferredThisPass`: `actor_no_movement_this_cycle`,
  `collector_holding_site`), теперь пишется как `DEFER`; настоящая пропажа — по-прежнему WARN.
