# AI V2 — финальный аудит механик, осей желаний и кешей (2026-09-27)

Источник: `Logs/AiDebug.log` (сессия 07:53–07:59, **Kryll = P5**, **Orlan = P0**, ходы 1–11),
код `master` @ `57aeead4` + фиксы F1/F2/F3 из этого аудита.
Уровни — по `Assets/Scripts/Ai/V2/ARCHITECTURE.md` (Orchestration / Analysis / Strategy
{Desire, Objectives, Demand, PhaseA, PhaseB} / Evaluation / Materialization / Missions /
Allocation / Provisioning / Execution / Continuity / Housekeeping / Reaction / State / Diagnostics).

Обозначения: `R[x]` — чтение кеша/реестра x, `W[x]` — запись, `RW[x]` — оба, `⟂` — гейт,
`↺` — возврат в цикл, `✗` — найденный пробел.

> Документ `ai-v2-final-audit-2026-09-26.md` §4 устарел: формула AGG там — `max(offensive,
> activeDefence)`. Текущий код (после `2709783a`, `2f6a4635`) — см. §4 ниже.

---

## 1. Ходы ИИ по логу

| Ход | Игрок | AP | Что сделал | Альтернативы | Оценка |
|---|---|---|---|---|---|
| T1 | Kryll | 10 | Nora Blackwell → скаут (3 AP); 4 шага Explore; Phase B: Ash Drifter ×2 в гарнизон, добор 2 | Экономики ещё нет (`no_legal_valuable_site`); ECO-герой не окупался | ✅ Recon→Economy. Гарнизон — излишек, но AP иначе сгорали |
| T1 | Orlan | 8 | Скаута нет (в руке нет Recce-карты: `match=0`); Phase B: Bolt Jax в гарнизон, добор 3 | Intel Center (+2 AP/ход) | ⚠️ Intel Center оценён 1.05 < Bolt Jax 1.63 — см. O1 (горизонт AP-эффекта). Сыгран в T2 |
| T2 | Kryll | 10 | Rusty Miller → ECO-строитель FoundBase (2,−2); разведка; добор 2 | Коллекторы/Hooded — нет подходящих карт/AP | ✅ |
| T2 | Orlan | 8 | RC Vehicle → скаут; Intel Center; добор 1 | AGG 20.6 — нет ресурсов на танки | ✅ |
| T3 | Orlan | 12 | Watcher Drone → скаут; 5 шагов разведки; добор 4 | Герои: `InsufficientSafeEscort` | ✅ |
| T3 | Kryll | 8 | Hooded → скаут; строитель #13 → Energy Extractor (5,0); Ash Drifter в гарнизон | FoundBase (2,−2) ждёт | ✅ Строитель выгодно отвлечён на ближнюю добычу |
| T4 | Orlan | 12 | Nadia Thorne → ECO (2,−3); **AA Crawler → армия #17 (занята ECO) → delivered 0**; Aldric Voss → ECO (−1,−2); добор 2 | AA Crawler NewArmy / сохранить карту; ActiveDefence D03 (18.2) остался без цепочки | ❌ **F2** — 1 AP + ресурсы без доставки |
| T4 | Kryll | 8 | Human Extractor (3,0); разведка; добор 3 | — | ✅ |
| T5 | Orlan | 12 | Light Infantry → AGG (4.85) → перехват #13; Human Extractor (−1,−2), Energy Extractor (2,−3) | — | ✅ |
| T5 | Kryll | 8 | Materials Extractor (3,−2); #12 (заём у разведки) идёт на FoundBase (2,−2) → отзыв `insufficient_safe_escort`; Scrapwing | — | ✅ Отзыв при появлении сил Orlan корректен |
| T6 | Orlan | 12 | Scout (карта) → RCN; 2 ReturnBuilder; разведка; Tessa Rourke (герой, NewArmy, излишек → Housekeeping свернул в гарнизон в тот же ход); Rusty Vulture | Гарнизон напрямую / добор | ✅ (излишек Phase B; по сути гарнизон, мелкая неэффективность) |
| T6 | Kryll | 8 | MT Rust Tank → AGG (9.43); ReturnBuilder; разведка | — | ✅ |
| T7 | Kryll | 10 | Elena Hayes → #24; Factory (DEV); Production-Challenge выигран, экипировка; перехват #15: по пути принял событие (3,−2), бой, **армия #24 потеряна** | Пропустить событие | ⚠️ Оба оценщика давали ~0.96; решение обосновано, исход — неудача/точность оценщика |
| T7 | Orlan | 12 | Medium Tank + Kessa Vrail → AGG; Raid #4 (бой); ActiveDefence #12 (бой); AirSweep; FoundBase-строитель | — | ✅ |
| T8 | Orlan | 14 | Возврат авиации; Tech Extractor (−1,−2); перехваты #12, #13 (бои); уничтожен сайт Kryll (3,0) | — | ✅ |
| T8 | Kryll | 8 | Scav Carrier + Vera Sterling → AGG (11.57); строитель #13 → (1,3); AirSweep | — | ✅ (но см. F1: спрос на (1,3) уже исчез) |
| T9 | Kryll | 10 | Возврат авиации; **строитель идёт к (1,3), спроса на стройку нет**; перехват #19; разведка; 0 карт | Карт к игре нет (Iri Vane не окупается) | ❌ **F1** — 2 AP/ход + резерв E/M на стройку, которая не будет выпущена |
| T9 | Orlan | 12 | Lira Sable → скаут; **Concord Base (2,−2)**; ActiveDefence; разведка | — | ✅ Ключевой ход партии |
| T10 | Orlan | 16 | Light Tank + Dorian Kesh → AGG (12.86); бой #27; разведка; Wasp | — | ✅ |
| T10 | Kryll | 8 | MT Rust Tank → AGG (12.66); строитель встал на (1,3) — **не строит**; перехват #15 **отложен** аллокатором | Перехват 23.8×0.68 < разведка ~10×1.64 | ❌ **F1** + ⚠️ **F3** (AGG-скейл под осадой 0.68) |
| T11 | Orlan | 16 | Attack Gather #22→#17 → Assault на Citadel Kryll; донор домой; разведка; Vector Vance гарнизон, Light Infantry → #18 | — | ✅ Корректная сборка кулака |
| T11 | Kryll | 8 | Строитель на (1,3): 4 шага `progress=0` → `bounded stop`; **перехват #15 назначен 4 раза, не исполнен**; Striker (без воздушных задач) | Перехват #15 | ❌ **F1** (застрявший коммит съел лимит no-progress) + ⚠️ O1 |

Итог: 22 полухода, 3 корневых дефекта (F1, F2, F3) исправлены, 4 наблюдения калибровки (O1–O4).

---

## 2. Пайплайн хода (Orchestration)

```
AiStrategyV2Pipeline.RunTurn(player)                                        [Orchestration]
│
├─ W[Telemetry] W[CapabilityPoolExhaustion.BeginTurn] W[ReservationLedger.BeginTurn]
├─ WorldAnalysis.Scan ─────────────────────────────────────────────────────── [Analysis]
│    R[AiMapMemory] R[AiReconIntelMemory] W[AiReconMemory.Observe → ReconIntelSnapshot]
│    Self · Known · MapKnowledge · TrueWorld · Economy · Threat(honest) · Development
├─ W[ForceBaselineRegistry] (1× за игру)
├─ StrategyLayer.Evaluate ─ RW[AiRadarState] (Smooth 1×/ход) → Radar ─────── [Strategy/Desire]
├─ Recon/Aggression/Attack/ActiveDefence Objectives ─────────────────────── [Strategy/Objectives]
├─ Continuity.ResolveActive ─ RW[MissionIntentRegistry] → ActorCommitments ── [Continuity]→[State]
├─ DemandLayer.Generate: Recon → Aggression → Economy → Development ──────── [Strategy/Demand]
│    W[ResourceStarvation.Decay] W[DevelopmentInvestmentGate] RW[ReconCapacityDeficit]
├─ ApBudgetLedger.Create(AP)
├─ Phase A: ECO holds → infra pre-pass → Portfolio → Materialization → Delivery ─ [PhaseA]
│
├─ ↺ ТИПИЗИРОВАННЫЙ ЦИКЛ (≤ maxMidTurnStepsPerTurn, ≤ maxMidTurnNoProgressCycles)
│    ├─ strategic re-admission (Economy/Development) ⟂ fingerprint unchanged
│    ├─ Missions.Propose → Allocation (EffectiveValue = Base × 4·w(axis)) ⟂ no funded
│    ├─ Provisioning (1 шаг) → Execution → W[V2StateVersion]
│    ├─ RefreshStrategicKnowledge → PublishStepObservationDelta → W[InterruptRegistry]
│    │    (EconomyDeliveryReady → ResourceSite|Actor: «строитель на гексе, Phase A — строй»)
│    ├─ Continuity.ReconcileStep
│    └─ progress? ⟂ noProgress ≥ 2 → bounded stop  ◄── F1: застрявший коммит срабатывал здесь
│
├─ management rounds: Phase B (draw / play / maintenance / rebase) ──────── [Strategy/PhaseB]
├─ cold-Radar residual
├─ ReconcileAfterTurn → Reaction ─────────────────────────────────────────── [Reaction]
├─ Housekeeping (0 AP) R[Lease] → Clear[Lease] ──────────────────────────── [Housekeeping]
└─ ReservationLedger.AssertClearAtTurnEnd; Telemetry
```

Выходы цикла: нет funded → stop; нет инвалидаций → stop; лимит шагов / no-progress → bounded
stop. Пробел: bounded stop общий для всех задач — одна «ждущая» задача без прогресса обрывает
исполнение остальных (T11 Kryll). После F1 основной источник такого «ждущего» шага устранён;
общий риск — O4.

---

## 3. Графы механик

### 3.1 Recon

```
Analysis: MapKnowledge.ExplorableUnknownFrac, Threat.Contacts(honest), ReconIntelSnapshot
   ├─ Desire RCN = wE·explore + wS·refresh + wB·blindness                   [Desire]
   ├─ ReconObjectiveEvaluator: Explore / Surveil / Refresh / AirSweep       [Objectives]
   └─ DemandLayer.Recon: jobs(raw→covered→runnable) → capacity              [Demand]
        ├─ supply=0           → PROMOTE zero_capacity_bootstrap → CREATE
        ├─ deficit=0          → DEFER usable_capacity_covers_all_lanes
        ├─ active ≥ hard cap  → DEFER concurrency_hard_cap
        ├─ stealth-дефицит    → CREATE insufficient_free_stealth_scouts
        └─ streak < N         → persistence-deferred (Phase A — только без альтернатив)
   Phase A: Scout-карта → NewArmy/ReusableShell → W[Lease Scout]            [PhaseA]
     ⟂ нет Recce-карты → «no feasible useful chain» (Orlan T1: match=0)
   Missions → Allocation → ReconAssignmentPlanner (1 актор / 1 работа)
     ⟂ MoverContended / NoExecutableStep / NoMoverExists → CapabilityPoolExhaustion
   Execution: ReconGround.Pick RW[ScoutTrail] RW[ReconPatrolState]; Air RW[AirSortie]
   Continuity: payload, re-focus, TrimSurplusReconLanes, IntentReapedIdle → cooldown
```
Лог: Acceptance fail=0 во всех 22 полуходах; отказы — `MoverContended`/`NoMoverExists`
(работ больше, чем скаутов). Пробелов нет.

### 3.2 Economy (BuildExtraction / FoundBase / Collector / ReturnBuilder)

```
Analysis.Economy: PerType(DeficitScore), ExtractionOpportunities{BuilderRoutes},
                  BaseOpportunities, EconomicSecurity, HasActionableOpportunity
   ├─ Desire ECO = (wMax·max + wMean·mean) × (actionable ? 1 : latent 0.25)
   └─ DemandLayer.EconomyDemands (extraction loop)                         [Demand]
        gain ≤ 0 ──────────────────────────────────────→ skip (физический факт)
        pinned = активный BuildExtraction-интент на сайте (committed)   ◄── F1 (перенесено вверх)
        usefulGain ≤ 0 ∧ ¬committed ──────────────────→ rejectedSurplus
        payback > max ∧ ¬committed ───────────────────→ rejectedPayback
        siteOnly ≤ 0 ∧ ¬committed ────────────────────→ rejectedStrategicValue
        value ≤ 0 ∧ ¬readyLossToNewHero ∧ ¬committed ─→ rejected
        → EconomicInfrastructure-спрос (builder = pinned) | Hero-prereq | new-hero alt
   Phase A:
     ProtectActiveEconomyBuild: W[Reservation EconomyDeferredBuild], MarkProtected
     infra pre-pass: TryFulfill(спрос) ⟂ строитель на гексе ⟂ AP/ресурсы → строит
     Hero-спрос → Materialization → FinalizeOperationalDelivery → BeginEconomyDelivery
   Missions: EconomyMissionPlanner — ведёт строителя ИЗ ИНТЕНТА (без экономики спроса)
   Execution: RunGroundTransportStep → на гексе: EconomyDeliveryReady → триггер Phase A
   Continuity: после постройки → BeginEconomyBuilderRecovery → ReturnBuilder
```
✗ до F1: движение (интент) и стройка (спрос) имели разных «хозяев» жизненного цикла —
интент жил, спрос исчезал при просевшей экономике ресурса. Для Base исключение `committed`
уже было (`AddBaseCandidates`: «must not be abandoned merely because marginal economics dipped»),
для extraction — нет.

### 3.3 Aggression (Raid / Attack / ActiveDefence)

```
Analysis: CombatOpportunityAnalyzer (нейтралы), AttackObjectiveEvaluator (базы/фасилити),
          Threat.Threats (honest) → ActiveDefence, DefensiveReserveForThreats
   ├─ Desire AGG = known_combat_activity ? (surplus+ecoGate+relEdge)/3 : 0      (F3: без siege damp)
   ├─ Objectives: Raid ACCEPT/REJECT (readyWin/asmWin/gate); Attack; ActiveDefence(severity)
   └─ DemandLayer.Aggression → FieldCombatPower                            [Demand]
        Raid NeedsPower (shape Any, «free field capability») / NeedsHero
        Raid|Attack bound primary shortage (IndependentFieldArmy)
        Attack unbound (Any, fist = незанятая армия)
        ActiveDefence regroup_exhausted/shortage (IndependentFieldArmy)
   Phase A: MaterializationDeliveryPolicy.AssessDemandOperationally (pre-play)   [Materialization]
     FieldCombatPower: Garrison → только гарнизон цели
                       Independent → получатель мобильный ∧ не занят
                       ExistingArmy ∧ получатель занят интентом → No           ◄── F2 (все формы)
     → играть карту → CapabilityDeliveryEvaluator (post-play: Δ RaidAvailableFieldPower,
       исключающий заклеймленные армии) → handoff / lease
   Missions: AggressionMissionPlanner (Raid / Attack Gather→Assault / ActiveDefence
             Intercept|Regroup|Retreat) — ось AGG, EffectiveValue = Base × 4·w(AGG)
             Raid/Attack интринсик: − CitadelThreatRisk (сдерживание наступления)  ◄── F3
             ActiveDefence интринсик: без CitadelThreatRisk
   Provisioning: GroundCombatAssaultTransaction / AttackProvisioner
   Execution: ground-combat kernel; события на пути — HexEventGuardEstimate
   Continuity: Raid phases / Attack Gather→Assault→GatherReturn / ActiveDefence END/Return
```
✗ F2: pre-play для формы `Any` не проверял занятость получателя, post-play мерил только
свободную силу → карта играется «в никуда».
✗ F3 (до фикса): ActiveDefence живёт на оси AGG, а AGG-желание при осаде умножалось на 0.2.

### 3.4 Development

```
Analysis.Development → Desire DEV = surplus × max(readyQ, latentQ) × gain
DevelopmentInvestmentGate.Observe (headroom ≥ 0.6 два хода подряд)  W[per player]
DevelopmentOpportunityEvaluator: READY upgrade | PREPARE facility/operator
  ⟂ window_closed(res) ⟂ no_facility_card ⟂ no_operator ⟂ no_valuable_output
Phase A: residual infra (Factory/Laboratory — после карт), GenerateAttachUpgrade
Missions: DevelopmentMissionPlanner → Provisioning (оператор на фасилити)
```
Лог: Kryll T7 — Factory построен последним (residual), Production-Challenge выигран,
экипировка прикреплена. Пробелов нет; связь с AGG — §4 (O3).

### 3.5 Phase B / Aviation / Tempo

```
StrategicPhaseB.UseSurplus RW[StrategicTempoBudget: total/draws/gen]
  witnessed = PhaseBWitnessedApWorkload(текущий ход)       ← O1: горизонт
  кандидаты: PlayMat | PlayNonCombat(Facility/Aviation/Equipment) | Draw | Maintenance | Rebase
    ⟂ spendable = AP − reservations ⟂ ClaimsEconomyBuildCard ⟂ scout portfolio saturated
  Aviation: RoleFit = 2.0 (+gap) + OperationalTask(service) − upkeep×marginalApUtil
```
Лог: добор — только без лучшей игры; hold лишних скаутов — корректно. Орлан T1 Intel Center и
Kryll T11 Striker — O1.

### 3.6 Housekeeping

```
HousekeepingManager.Run (конец хода, 0 AP) R[Lease(turn)] → fold/sort → Clear[Lease]
```
Лог: 45 записей, свёрток лизингованных армий нет. Пробелов нет.

---

## 4. Оси желаний: графы и попарное сопоставление

```
 RCN = clamp(wE·explore + wS·refresh + wB·blindness)
        │ открывает сайты                     │ даёт known_combat_activity, EnemyKnownStrength
        ▼                                     ▼
 ECO = (wMax·max + wMean·mean) × (actionable ? 1 : 0.25)      AGG = activity ? readiness : 0
        │ EconomicSecurity ───── ecoGate ──────────────────────▶ readiness=(surplus+ecoGate+relEdge)/3
        │ SurplusFraction                                        ▲ surplus = ramp((Total−Reserve)/Total)
        ▼                                                        │ Reserve ← Threat (honest)
                                               угроза дома → только TaskScore Raid/Attack (CitadelThreatRisk)
 DEV = surplus × max(readyQ, latentQ) × gain          (не читает AGG/ECO-потребность)   ◄ O3

 Radar = Normalize(Smooth(raw));  EffectiveValue = BaseValue × 4·w(axis)
 Радар заморожен на ход; mid-turn обновляются только lane-факты RCN и AGG (без ренормализации).
```

| Пара | Связь в коде | Принцип | Вердикт |
|---|---|---|---|
| RCN ↔ ECO | ECO-гейт `HasActionableOpportunity` (latent 0.25) ← открытые сайты | Recon → знания → Economy | ✅ (радар заморожен: эффект — со следующего хода, по дизайну) |
| RCN ↔ AGG | AGG = 0 без известной боевой активности; `relEdge` = 0.5 без разведданных | Recon → знания → Attack | ✅ |
| RCN ↔ DEV | прямой связи нет | нейтрально | ✅ |
| ECO ↔ AGG | `ecoGate = Lerp(0.5,1,EconomicSecurity)` в readiness; ECO-стройки и Raid/Attack несут `CitadelThreatRisk` | Economy → бюджет → Attack/Defence | ✅ (после F3). Остаток: резерв под угрозу снижает surplus — мягко, readiness не падает ниже ~⅓ |
| ECO ↔ DEV | DEV.surplus из ресурсного избытка; `DevelopmentInvestmentGate` по headroom | Economy → бюджет → Production | ✅ |
| AGG ↔ DEV | DEV не читает AGG-потребность | Production усиливает **обоснованную** потребность | ⚠️ O3: Kryll T8–T11 DEV 0.26–0.28 > AGG 0.15–0.24 при осаде |

Консистентность кода: все оси — один `Smooth` (0.4), одна `Radar.Normalize`, один
`RadarValueScale`; `RefreshAggressionOperationalFacts` повторяет формулы surplus/relEdge из
`Evaluate` (осознанная копия под замороженный радар; при правке весов — оба места).

Цепочка принципа по логу: Recon→Economy ✅ (T1–T3 разведка, T2–T5 экстракторы),
Economy→бюджет ✅ (Orlan AP 8→16, ресурсы), Attack/Defence→потребность ✅ после F3 (оборона
сохраняет вес оси, наступление сдерживается в своём TaskScore), Production→усиление ⚠️ (O3).

---

## 5. Дерево кешей: где читается и где пишется (для каждого игрока)

```
ИГРА ── сброс: CitadelSetupController → Clear/ClearAll (все decision-реестры ниже;
│        AiReconMemory.Clear каскадно: ReconIntel*, Patrol, AirSortie(Recon), Coverage,
│        ReconCapacityDeficit, ScoutTrail, ReconAcceptanceAudit)
│
├─ ПАМЯТЬ ИГРОКА (Dictionary<PlayerSetupData,…>, переживает ходы)
│  ├─ AiMapMemory {sightings, buildings, resources, eventGuards, dangerZones, versions}
│  │     W: VisionSystem.VisibilityChanged / StealthChanged / EventConsumed (вне пайплайна)
│  │     W: MarkScoutDanger, RecordAirReconTarget, MarkRaidPlanRejected   [Execution/Missions]
│  │     R: Analysis.Scan/Refresh(Known), SafeStepPathing.CaptureMemoryBlockers,
│  │        Objectives, TaskExecutor(ActiveDefence witness), Demand, Reaction
│  ├─ AiReconIntelMemory (живая) ─ W: Analysis.Observe  R: Execution(recon)
│  ├─ ReconIntelSnapshotRegistry (Player→Turn→KnowledgeVersion)
│  │     W: Analysis.Observe (каждая ревизия знаний)  R: Desire.RefreshPressure, Recon objectives
│  ├─ AiReconMemory ─ W: Analysis.Observe  R: Threat(Historical)
│  ├─ AiRadarState ─ RW: StrategyLayer.Evaluate (Smooth только при first-eval-this-turn)
│  ├─ ForceBaselineRegistry ─ W: pipeline (1×)  R: Analysis.Self
│  ├─ MissionIntentRegistry/State ─ W: только Continuity
│  │     R: ActorCommitments.FromIntents, DemandLayer.EconomyDemands(pinned)   ◄ F1 (R)
│  │        MaterializationDeliveryPolicy.IsConsumerPrimaryOrCommitted          ◄ F2 (R)
│  │        CapabilityDeliveryEvaluator, Provisioning, Missions
│  ├─ ResourceStarvationRegistry ─ W: Diagnostics(RecordVerifiedBlock, 1×/ресурс/ход),
│  │        Demand(DecayOncePerTurn)  R: TaskScoreEvaluator.ResourcePriority (Demand, Analysis)
│  ├─ DevelopmentInvestmentGate ─ W: Demand.Generate (Observe)  R: Dev objectives
│  ├─ ReconCapacityDeficitRegistry ─ RW: DemandLayer.Recon (streak)
│  ├─ ReconPatrolState / ScoutTrail / ReconAirSortie / AirReconCoverage / AirSortie
│  │        RW: Recon planners + Execution; AirSortie также Continuity (ReleaseOrphanStrikes)
│  ├─ AiAllocatorState (cooldowns) ─ RW: ResourceAllocator; R(Peek): AggressionDemandEvaluator
│  └─ InitiativeAnalyticsHistory ─ W: конец хода  R: PreTurnCapacityAnalysis
│
├─ ХОД (штамп turn; ленивый сброс `state.Turn != turn`)
│  ├─ StrategicResourceReservationLedger (owner+reason)
│  │     W: PhaseA holds (EconomyDeferredBuild), Provisioning (EconomyBuildCompletion),
│  │        Continuity (BeginEconomyDelivery), InfrastructureFulfillment.Reconcile*
│  │     R: StrategicSpendability (Allocation, PhaseB, TryFulfill)
│  │     Teardown: ExpireStage + AssertClearAtTurnEnd (в логе 0 утечек)
│  ├─ CapabilityPoolExhaustionRegistry ─ W: loop  R: CanAttempt (RevalidateAndClearIfRecovered)
│  ├─ StrategicInterruptRegistry ─ W: Analysis.PublishStepObservationDelta, Execution, PhaseB
│  │     R+Consume: TakeTypedTriggers
│  ├─ StrategicTempoBudget ─ RW: PhaseB (+GenerationUsed для PhaseA)
│  ├─ StrategicCapabilityLeaseRegistry ─ W: CapabilityDeliveryEvaluator (Mark)
│  │     R: MaterializationCandidateBuilder / ArmyReorgAnalyzer (IsLeased(turn)); Clear: Housekeeping
│  ├─ AviationObligationStallRegistry ─ W: loop  R: Spendability, air recovery
│  ├─ AiHandData ─ RW: CardPlay/Draw executors
│  └─ ApBudgetLedger / MaterializationReservation / ActorCommitments — локальные объекты хода
│        ActorCommitments: W FromIntents(ResolveActive) + Claim(EconomyPreferredBuilder в PhaseA)
│        R: Demand, CapabilityInventory, Materialization (Phase B protect), Missions
│
├─ СНАПШОТ (ConditionalWeakTable<WorldSnapshot,…> — живёт ровно один кадр)
│  └─ DemandLayer.EconomyAssessmentCache ─ RW: AssessEconomyArmy (через SelectEconomyBuilder)
│        ✓ Refresh* создаёт НОВЫЙ WorldSnapshot → устаревание исключено; F1 только читает
│
├─ ПРОЧИЕ СЛАБЫЕ
│  ├─ GroundCombatAdmissionRegistry (CWT<MissionProposal>) ─ W: Missions  R: Provisioning
│  └─ GenerationSource.HeroIdentities (CWT<UnitData>)
│
├─ МАРШРУТЫ: SafeStepPathing._playerCaches[player]
│     инвалидация: смена карты/PathingVersion → всё; RouteMemoryVersion → blockers
│     R: Economy routes, collector admission, Provisioning, Execution
│
└─ ДИАГНОСТИКА (не влияет на решения): AiDebugLog dedup, AiV2Trace, Telemetry, Audit
```

Проверка направлений: запись и чтение разнесены по владельцам; все реестры ключуются игроком;
межигроковых чтений нет; кадровые кеши привязаны к неизменяемому снапшоту. Фиксы F1/F2
добавляют только **чтения** `MissionIntentRegistry` своего игрока — новых записей и новых
кешей нет.

---

## 6. Находки

### F1 — committed-extraction терял спрос на стройку (ИСПРАВЛЕНО)

* **Уровень:** Strategy/Demand — `DemandLayer.EconomyDemands` (единственный источник
  EconomicInfrastructure-спроса; `InfrastructureFulfillment.TryFulfill` строит только по спросу).
* **Корень:** surplus/payback/value-отбраковка стояла **до** учёта активного интента. Движение
  строителя ведёт `EconomyMissionPlanner` из интента, резерв держит `ProtectActiveEconomyBuild`,
  а стройку может выпустить только этот спрос. Для Base исключение `committed` было; для
  extraction — нет.
* **Было (Kryll):** T7 выбран Materials Extractor (1,3) (priority 0.75) → T8 Materials в
  избытке (usefulGain 0.33 → `rejectedSurplus`) → T8–T10 строитель #13 идёт 3 хода, каждую
  итерацию резервируя E1/M1(+1 AP) → T10 встал на (1,3): «delivery ready» ×4, строить нечего →
  T11 4 шага `progress=0` → `bounded stop` → перехват #15 (назначен 4 раза) не исполнен.
* **Станет:** спрос на (1,3) выпускается с закреплённым строителем #13 → T10 первое же
  re-admission после шага на гекс строит экстрактор (1 AP, E1 M1) → строитель уходит домой
  (Recovery) → T11 цикл свободен, перехват #15 исполняется.
* **Параллельный функционал:** состояние «интент + спрос на тот же сайт» уже штатное (T7:
  D104 при активном интенте) — фикс только не даёт спросу исчезнуть; свежие сайты
  отбраковываются как раньше; `readyLossToNewHero` для committed по-прежнему выключен.
  Жизненный цикл отказа от стройки остаётся у Continuity (stall/idle reap).
* **Тест:** `AiEconomyDecisionTests.EconomyDemand_CommittedExtractionKeepsBuildDemandWhenSurplusDips`.

### F2 — FieldCombatPower-доставка в армию, занятую другим интентом (ИСПРАВЛЕНО)

* **Уровень:** Materialization — `MaterializationDeliveryPolicy.AssessDemandOperationally`
  (единственный владелец pre-play «доставит ли план capability»).
* **Корень:** проверка занятости получателя применялась только к форме
  `IndependentFieldArmy`. Все FieldCombatPower-спросы — это спрос на **свободную** силу, и
  post-play мера (`CapabilityInventory.RaidAvailableFieldPower`) исключает заклеймленные армии.
  Два гейта отвечали на один вопрос по-разному.
* **Было (Orlan T4):** Nadia Thorne доставлена в ECO → армия #17 заклеймлена → в следующем
  раунде Phase A для Raid D02 (форма Any) выбран «AA Crawler → ExistingArmy #17» →
  `delivered 0 … residual unchanged`: 1 AP и карта потрачены, D02 заблокирован.
* **Станет:** такой план отклоняется ещё при выборе (`committed_actor_adds_no_free_field_power`),
  портфель берёт реальную альтернативу (NewArmy/другой спрос) или сохраняет карту.
* **Дополнительно:** `IsConsumerPrimaryOrCommitted` теперь видит support-армии Attack
  (`GroundCombatLegs.HeldGroundSupportArmyIds` — тот же список, что клеймит `ActorCommitments`).
* **Параллельный функционал:** Garrison-форма не затронута; Phase B уже исключала защищённые
  армии при перечислении размещений; ReactionMaterializationSolver получает тот же ответ.
* **Тест:** `AiFieldPowerCommittedRecipientTests`.

### F3 — оборона под осадой получала минимальный вес радара (ИСПРАВЛЕНО)

* **Уровни:** Strategy/Desire — `StrategyLayer.Evaluate`; Strategy/Objectives —
  `AggressionObjectiveEvaluator.Build` (Raid), `AttackObjectiveEvaluator` (Attack);
  Missions — `AggressionMissionPlanner` (стейл-инкумбент Raid в тумане).
* **Корень:** `2f6a4635` убрал `max(threat, …)` из AGG, но оставил `× aggSiegeDamp (0.2)`.
  Радар масштабирует ось целиком, а на оси AGG живут и наступление, и ActiveDefence — осада
  резала оборону в ~5 раз.
* **Фикс:** угроза дома не двигает AGG-радар ни в одну сторону (`aggSiegeDamp` удалён вместе с
  единственным читателем). Сдерживание наступления — в TaskScore Raid/Attack через существующий
  слот `CitadelThreatRisk` (как у ECO-строек). `WithResponse` переносит слот во все ответы армий;
  ActiveDefence слот не получает. ARCHITECTURE.md (строка «Home threat in task scoring») обновлён.
* **Было (Kryll T10, citadel 0.78):** AGG raw 0.19, радар 0.17 → перехват 23.8 × 0.68 ≈ 16
  проиграл разведке ~10 × 1.64; Raid Army#5 12.1 × 0.68 ≈ 8.2.
* **Станет (оценка на тех же raw):** AGG raw ≈ 0.95, радар ≈ 0.37, скейл ≈ 1.5 → перехват
  ≈ 36 — первым в пуле; Raid (12.1 − 0.78·8 = 5.9) × 1.5 ≈ 8.8 — уровень наступления почти
  прежний; Attack Facility (16.7 − 6.2) × 1.5 ≈ 15.7 < перехвата.
* **Параллельный функционал:** Orlan (citadel 0, base-угроза выключена `BaseThreatRiskMax=0`) —
  без изменений. При severity≈1 свежий Raid может упасть ниже `raidObjectiveMinBaseValue` и не
  перечисляться — это и есть сдерживание. Идущие Hard-коммиты финансируются в strict-стадии
  независимо от ценности — не рвутся. Спрос на наступательную силу под осадой дешевеет
  (значение спроса = TaskScore объектива), спрос ActiveDefence — нет.
* **Тест:** `AiAttackObjectiveTests.Siege_DoesNotDampAggressionRadar_OffenceCarriesCitadelThreatRisk`.

### Наблюдения (без правок)

* **O1 — горизонт AP-эффектов.** Рекуррентные эффекты (+AP/ход, апкип авиации) оцениваются по
  witnessed-нагрузке *текущего* хода: Orlan T1 Intel Center 1.92 → 1.05 (saturation 0),
  Kryll T11 Striker 0.35 → 1.55 (апкип обнулён), при `witness=none, task=0`. Это документированный
  AI-MGR-дизайн; вопрос калибровки — брать ли многоходовую оценку нагрузки.
* **O2 — WARN BindFunding для ActiveDefence/Attack.** Планировщик сознательно ждёт
  (`incumbent_waits`, донор без MP) — пишется WARN. Только лог; тот же класс, что прошлый
  Economy-фикс `DeferredThisPass`.
* **O3 — DEV не зависит от обоснованной потребности.** DEV-желание = surplus × quality, без
  связи с AGG/ECO; до F3 при осаде DEV был выше AGG. После F3 AGG под осадой не падает, перекос
  снят; открытым остаётся только сам принцип «Production усиливает обоснованную потребность».
* **O4 — общий no-progress лимит цикла.** Одна задача без прогресса исчерпывает лимит для всех.
  После F1 основной источник устранён; при повторении — рассмотреть исключение задачи из
  повторного выбора в том же проходе.
* **Kryll T7** — потеря #24 на событии (3,−2) при согласованной оценке ~0.96 обоими оценщиками.
  Логического пробела нет; отдельно стоит сверить `HexEventGuardEstimate` (без бонуса гекса,
  герои охраны исключены из защитников) с рейдовым оценщиком — два оценщика одного боя.

---

## 7. Повторная проверка после фиксов

| Требование | Статус |
|---|---|
| Корень, не латка | F1: единый источник стройки получил то же committed-правило, что Base; F2: pre-play гейт приведён к post-play мере; F3: угроза дома — факт задачи (TaskScore), а не оси |
| Единственное место логики | EconomicInfrastructure-спрос — только `DemandLayer.EconomyDemands`; delivery — только `MaterializationDeliveryPolicy` |
| Без вертикального расширения | Новых классов/слоёв нет; правки внутри существующих методов |
| Ответственность слоёв | Demand решает «выпускать ли стройку», Materialization — «доставит ли план»; Continuity-жизненный цикл не тронут |
| Кеши | Только новые чтения `MissionIntentRegistry` своего игрока |
| Компиляция | `Assembly-CSharp` и `Assembly-CSharp-Editor` (с новым тестом) — 0 ошибок, 0 предупреждений |
| Тесты в Unity | На вашей стороне: `AiEconomyDecisionTests`, `AiFieldPowerCommittedRecipientTests`, `AiAttackObjectiveTests` + регресс AI-наборов |
