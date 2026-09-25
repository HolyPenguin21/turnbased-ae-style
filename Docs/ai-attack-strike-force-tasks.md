# AI V2 — «ударный кулак» (Attack): состояние и задача на следующую сессию

Документ — рабочая задача для продолжения. Привязка к именам классов и методов, без номеров строк.
Норматив — `Assets/Scripts/Ai/V2/ARCHITECTURE.md`, дерево кеша — `docs/ai-v2-cache-audit.md`.

---

## 0. Правила работы (от пользователя, обязательны)

- Перед любым ответом или анализом сверяться с текущим кодом. Логи из git не использовать.
- Реализация — только после одобрения. Перед каждым шагом: перечитать код → показать план → получить «да» → делать.
- У каждого правила один метод-владелец. Дублей классов и методов быть не должно.
- Существующую логику не ломать. Правка остаётся в классе и слое, которые этим владеют. Архитектура не растёт вглубь: расширяем существующие методы.
- Для каждой проблемы искать корень, а не ставить заплатку.
- Результат агентов перепроверять самому.
- Тесты в Unity гоняет пользователь, это не блокирует сдачу. Перед сдачей собирать **обе** сборки:
  `dotnet build Assembly-CSharp.csproj -nologo -v q` и `dotnet build Assembly-CSharp-Editor.csproj -nologo -v q`.
- Перед пушем перепроверить себя ещё раз.
- Не трогать чужие незакоммиченные файлы: `Assets/Scenes/Game.unity`, `Assets/Scripts/UI/CardUI.cs`, `Assets/Textures/UI/Panel_Hand_02.png`.
- Python-скрипты правок класть в scratchpad и запускать с `python -X utf8`.

---

## 1. Где мы

| Ветка | Статус | Что внутри |
|---|---|---|
| `fix/ai-v2-final-audit-20260925` | запушена | исправления аудита, F7 Gather, S3/S5 |
| `refactor/ai-v2-ground-combat-lifecycle` | запушена, от аудита | S4: `GroundCombatLegs`, `GroundCombatLegStep`, `IGroundCombatOperation` |
| `feature/ai-attack-strike-force` | запушена, от refactor (refactor влит) | шаги 1–3 кулака + фикс тестов |

Коммиты `feature/ai-attack-strike-force`:
- `d7efd6e1` **шаг 1a.** Правило командира в бою:
  - `ArmyData.Commander` (первый герой армии) даёт вместимость, инициативу и Fate.
  - `BattleTurnOrder` и UI читают `Commander`; `FindHero` удалён.
  - `WorthIt`: `SideCommander` (инициатива ко всем бойцам стороны; дуэль Fate на каждый обмен по политике `FateDuelAi`), `DefendingArmy`, `EstimateSequential` (многоармейный гекс: самый сильный защитник первым, раны переносятся, Fate восполняется).
  - Агрегатный оценщик удалён.
  - Память хранит командира врага и охраны.
- `1c9538e2` **шаг 1b.** Модель противника `IReadOnlyList<WorthIt.DefendingArmy>` протянута через ядро наземного боя, Attack/Raid/ActiveDefence, угрозу, Housekeeping, карты и эскорт экономики:
  - `AiV2Util.KnownOpposition` и `AttackObjectiveEvaluator.KnownSiteOpposition`;
  - `GroundCombatFeasibility.Clears(attackers, attackerCommander, opposition, …)` — единственная перегрузка.
- `63129123` **шаг 2.** Одна оценка командира — `HeroRoleEvaluator.ProjectCommand` + `CompareCandidates`:
  - порядок сравнения: бой строя под героем → слоты тел → роль и лидерство → CR → Fate → ключ;
  - используется в `GroundCombatDonorPolicy.PickAttachableHero` и в Housekeeping (перестановка командира, выбор героя со скамейки, `CommanderMismatch`, `HasCommanderChoice`);
  - единая формула ценности тела — `WorthIt.CombatValue`.
- `b3b09d54` — editor-тесты под новые сигнатуры.
- `a85d8196` — исправления по перепроверке R1–R5 (раздел 2). Обе сборки 0/0.

В игре ничего из этого ещё не проверялось. Проверяет пользователь.

---

## 2. Результат перепроверки реализованного (25.09)

### Подтверждено кодом

- **Дуэль Fate в `WorthIt.ResolveExchange` повторяет `BattleAttackPopupUI.RunDuel`/`RunAiTurn`:**
  - защитник ходит первым, стороны чередуются;
  - решение принимает `FateDuelAi.ShouldSpendFate`;
  - перебрасывается первый промах (как `RerollOneMiss`); неудачный переброс заканчивает ход стороны;
  - без Fate поток RNG тот же, что раньше (`RollDice` ≡ `RollSuccesses`).
- **Бонус гекса** (`WorthIt.HexDefenseBonus`) в реальном бою получает любая защищающаяся армия на гексе (`GetDisplayedDefenseBonus`: `owner == _defender`). Поэтому угроза базе, посчитанная против всех своих армий на гексе, — верная модель.
- **`BattleInitiator.FindEnemyAt`** учитывает командира защитника, только если атакующий его видит. Housekeeping воспроизводит это через `TargetableUnitKeys`.
- **Пустой состав у наблюдения врага** означает и нулевые суммы: `Defenders`/`DefenseSum` строятся из одного набора видимых не-героев. Поэтому удаление запасного агрегатного пути в `ReconReactionPolicy` поведение не меняет.
- **Лишняя перестановка командира не опасна:** если новый командир не вмещает армию, Housekeeping отбрасывает такое состояние через `legality`. Этот ключ в `Outcome` стоит раньше `commandWaste`, так что переполнения армии не будет.

### Найдено и исправлено — шаг 0, коммит `a85d8196` (обе сборки 0/0)

По R5 выбран вариант «вычисляемое свойство».

| # | Приоритет | Где | Дефект | Исправление (один владелец) |
|---|---|---|---|---|
| R1 | P2 | `ArmyReorganizationCandidates.BestCommander` | Лучшим может выйти герой, под которым текущий состав не влезает в вместимость (выигрыш боя ≥ 0.05 при меньшем CR). Тогда перестановку режет `legality`, а законный второй кандидат не предлагается вовсе. `CommanderMismatch` остаётся 1 на каждом ходу, и `WorthPlanning` запускает проход впустую | В `BestCommander` рассматривать только героев, при которых роль стоит законно: `ReorgViability.Capacity` переставленного ростера ≥ `units.Count`. Метод один, его используют и кандидаты, и оценка |
| R2 | P3 | `WorldAnalysis.Threat.MakeCheatContact`, `ObservationToArmySnapshot` (+ `AiReconMemory.ReconObservation`), `MaterializationDeliveryPolicy` (проекция героя-строителя) | Эти снимки создаются без `Commander`. Угроза от чит-контакта и от исторического контакта считается без его командира. Эскорт экономики для ещё не выложенного героя считается без самого героя | Чит-контакт: копировать `source.Commander`. `ReconObservation` командира не хранит (сейчас: суммы, `Defenders`, AA, recce, `IsGarrison`) — завести поле `Commander`, заполнять в `AiReconMemory.Observe` из наблюдения и копировать в `ObservationToArmySnapshot`. Проекция героя: `new SideCommander(def.initiative, def.fate)` из карты героя |
| R3 | P3 | `WorthIt.EstimateSequential` | Баффы Берсерка (Attack/Defense) переносятся в следующий бой, а игра их откатывает (`BattleScreenUI.RevertBerserkStacks`) | Между боями восстанавливать Attack/Defense выживших из baseline по индексу, HP оставлять (не фильтровать список, а сбрасывать поля) |
| R4 | P3 | тексты | Шапка `WorthIt` описывает удалённый агрегатный оценщик (`SimulateExchangeMargin`, суммы). Перед `HexDefenseBonus` висит сиротский комментарий про `Score`/`MeetsWinChance`. Устарели имя `Outcome.CommandCapacityWaste` и строка причины перестановки «promote highest-capacity hero» | Переписать шапку `WorthIt`, удалить сиротский блок, переименовать в `CommanderMismatch`, исправить строку причины |
| R5 | P3 | `AttackObjective` | Хранит и `Opposition`, и производный `Defenders` (+`DefenderCount`) как изменяемые поля — два источника одного факта | Сделать `Defenders` вычисляемым (`WorthIt.UnitsOf(Opposition)`) или оставить осознанно. Спросить пользователя |

### Сознательные приближения (не дефекты, записать в ARCHITECTURE при случае)

- **Порядок боёв в `EstimateSequential`** фиксируется один раз, против свежего атакующего. Реальный `FindEnemyAt` перевыбирает самого сильного против уже раненого.
- **`CompareCandidates`** с эпсилоном нетранзитивен, но вход у всех вызывающих упорядочен детерминированно, поэтому результат стабилен.
- **Перестановка командира теперь зависит от сильнейшей угрозы группы** (`CommandContext`). Если угроза меняется ход от хода, командир может переключаться. Перестановка бесплатна по AP, но при игровом тесте это стоит смотреть.

---

## 3. Кеш (по дереву `docs/ai-v2-cache-audit.md`)

- **Новых статических хранилищ нет.**
- **Класс C, `AiMapMemory`:** `EnemySighting.Commander` и `GuardStrength.Commander` пишутся только в `OnVisibilityChanged` (доменное событие), ключ по игроку. `KnowledgeVersion` там поднимается безусловно, поэтому смена командира доходит до `WorldSnapshot.Known`. Остаточный риск: враг переставил командира, не двигаясь, и это не вызвало события видимости. Такая память обновится со следующим наблюдением — это обычная «видимость с памятью».
- **Класс A, кадр хода:**
  - `ArmySnapshot.Commander` добавлено. `ArmySnapshot.BestHeroCommandRating` переименовано: это потенциал, а не текущая вместимость.
  - `StrategicAssetSnapshot.Opposition` заменил `Defense`/`Defenders`.
  - `SameCombatCapability` включает `Commander`: смена командира публикуется как фактическая дельта наблюдения. Это верно.
- **Класс B:** ключ `GroundCombatAdmissionRegistry` не менялся (объект предложения), меняется только содержимое допуска.
- **Вывод:** инварианты I1–I6 не затронуты, дерево в силе.

---

## 4. Резервирование ресурсов

- **Код резервирования ветка не трогала:** `StrategicResourceReservationLedger`, `AxisBudgetLedger`, `ApSpent`, `Upsert`/`Release*`.
- **Влияние только косвенное, через существующие пути резервирования:**
  - сколько тел собирается или передаётся в сборке, Gather, подкреплении и refit рейда — отсюда AP на передачи и активации;
  - размер эскорта экономики — теперь с командиром строителя и командиром угрозы.
- **Перестановка командира в Housekeeping** стоит 0 AP. Выполняется после Phase B, лизинги (`IsLeased`) учитываются как раньше.

---

## 5. Влияние на остальные оси (что смотреть в игре)

| Ось / система | Что изменилось |
|---|---|
| Бой (и для человека) | Инициатива и Fate стороны идут от командира — первого героя армии, а не от первого героя, найденного на сетке |
| Aggression / Raid | Шанс победы учитывает командиров обеих сторон и дуэль Fate. Своя армия с героем стала «сильнее», вражеская с героем — тоже |
| Attack | Гекс с несколькими армиями — последовательные бои. Защищённые базы стали честно труднее |
| ActiveDefence | Перехват учитывает командира контакта |
| Defence / угроза | Базу защищают все свои армии на гексе (было — только гарнизон), поэтому угроза базе ниже. Угроза от вражеской армии с героем выше |
| Economy | Эскорт строителя считается с героем-строителем (нужно меньше тел) и с командиром угрозы (нужно больше) |
| Recon | Решения «бежать или атаковать» у разведчика учитывают командира врага |
| Cards / Phase B | Ценность экипировки считается против угроз с их командирами. Ценность карты героя (`StrategicCardEvaluator.HeroLeadershipFit`/`HeroCommandMarginalValue`) пока **не** на единой оценке командира |
| Housekeeping | Армия с 2+ героями попадает в планирование каждый ход; командира выбирает бой против сильнейшей угрозы группы. Каждая оценка состояния прогоняет Monte-Carlo на героя — смотреть время хода |
| Aviation | Авиаподдержка рейда (`RaidProvisioner.ProvisionAirSupport`, `RaidRecoveryPlanner.ProjectAirSupport`) — «до/после» с командиром цели |

---

## 6. Оставшиеся шаги кулака

Согласованная модель (от пользователя):
- **Attack тянет всё:** один кулак на пике силы одной армии, остальные линии его кормят.
- **Готовность — не булево значение:**
  - Сборка = Fist / P_field (≈ 90%);
  - Развёртка = P_field / P_deck (≈ 80%);
  - плюс «запас усиления».
- **Шанс победы** — слагаемое скора с полом 0.2; ниже пола сидим и обороняемся.
- **Доноры** — свободные армии и армии Raid/ActiveDefence в любой момент. Цена донора — ценность брошенной операции. После передачи донор уходит домой.
- **Хост, герой и точка сбора** выбираются по стоимости AP; хост идёт навстречу, только если так дешевле.
- **После захвата:** гарнизон из руки, иначе раненый или самый слабый боец кулака; дальше по скору — давить или перегруппироваться.
- **Цитадель:** её координаты — чит-якорь, её защитники — только из разведки.

### Шаг 3 — меры силы (Analysis) — СДЕЛАНО

Решения пользователя: шкала — `AiPower` (одна составленная наземная армия, `ComposeStack`); авиация в наземные меры не входит, только в запас.

**Где:** `WorldAnalysis.BuildForceMeasures` (один проход, вложенные пулы) → `SelfSnapshot`:
- `FieldPotential` — **P_field**: только карта (армии + гарнизоны, без авиации и пленных), лимит — CR героев на карте;
- `BestStackPotential` — карта + рука (комментарий «+ колода» был неверен, исправлен);
- `TotalMilitaryPotential` — **P_deck**: + колода;
- `FistPower` — **Fist**: `max EffectiveArmyPower` среди `IsStructuralRaidActor`;
- `StartPotential` — **P_start**: `State/ForceBaselineRegistry` (класс C, ключ по игроку). Пишет только пайплайн сразу после первого `Scan`, `Analysis` читает; до записи = P_deck. Сброс в `CitadelSetupController`;
- `Reserve` (`ForceReserve`): `Units` (юниты руки/колоды под лимитом карты), `Hero` (герои руки/колоды и их вместимость), `Equipment` (на каждую карту экипировки — лучший `Combat` из `StrategicCardEvaluator.EquipmentDeltaParts` на свободном законном наземном носителе, один носитель на предмет), `Aviation` (Σ `BasePower` карт авиации). P_field + Units + Hero = P_deck;
- `CommandHeroes` — все герои (карта / рука / колода) как `HeroRoleEvaluator.HeroProfile`.

**`HeroRoleEvaluator`:** общее ядро по статам; перегрузки `CombatLeadershipScore`/`HasSupportVocation`/`Classify` для карты героя; `HeroProfile` + `Profile(UnitData|CardDefinition)`; `Candidate(HeroProfile, projection)`; `CommandProjection.Roster`.

**`CombatOpportunityAnalyzer`:** «собираемый» ростер строится под лучшим командиром (карта + рука) на каждую цель — `ProjectCommand` + `CompareCandidates` (`BestAssembly`). Тела ранжируются `WorthIt.CombatValue` (локальный `ProfilePower` удалён).

**Лог:** строка `self.power` — `pField fist bestStack pDeck pStart reserve units/hero/equip/air`. Потери P_start/P_deck — только в лог.

**Влияние на нынешних читателей:** `BestStackPotential`/`TotalMilitaryPotential` больше не включают авиацию и пленных (`AttackObjectiveEvaluator`, `DesireEvaluators`, `TempoCandidateProvider`, `StrategicEffectRegistry`).

**Следующий коммит (одобрен):** ценность карты героя в Phase B (`StrategicCardEvaluator.HeroCommandMarginalValue`) — на `HeroRoleEvaluator.ProjectCommand` через `Profile(CardDefinition)`.

### Шаг 4 — скор атаки и готовность — СДЕЛАНО

- **Готовность** — `AttackObjectiveEvaluator.Readiness(SelfSnapshot)`: Сборка = Fist / P_field (рампа 0.5…0.9), Развёртка = P_field / (P_deck + запас экипировки) (рампа 0.4…0.8), произведение. Заменила `potentialSaturation` в том же слоте `MilitaryTargetRelevance` (× `attackReadinessScoreWeight` 0.45), только Base/Citadel. Авиация в готовность не входит.
- **Пол шанса победы Attack 0.2** — один владелец `GroundCombatAdmissionPolicy.AttackWinChanceFloor` (`AiConfigV2.attackMinViableWinChance`), один для свежей и продолжающейся операции (он ниже пола продолжения 0.4). Выше пола шанс — слагаемое `WinChance` скора (`WithResponse`). Места: `AppendAttack` (Assault), `TryAppendFreshAttackGather`, Continuity (`ResolveAttackGather`, `AttackPrimaryClearsTarget`), `GroundCombatAdmissionRegistry.RecordAttack` (+ пин инкумбента с порогом лейна).
- **Повторная проверка в транзакции** — корень: `GroundCombatAssemblyPlan.WinChanceGate` (порог, по которому план допущен, пишет `Plan`); `GroundCombatAssaultTransactionRunner.Run` перепроверяет по нему. Выбор порога назначенного штурма — `GroundCombatAdmissionPolicy.AssaultGate`. Это заодно исправило Raid: инкумбент, допущенный по 0.4, больше не перепроверяется по 0.65.
- **Префильтр мощности** (`GroundCombatFeasibility`, калиброван на 0.65) применяется только к порогам ≥ 0.65: ниже в калибровке встречалась победа до 0.24.
- **Demand:** связанная операция просит поддержку, пока основной ниже уверенного порога 0.65 (была константа рейда, теперь `FreshStartWinChanceGate`). Новое `AppendUnboundAttackDemand`: лучшая Base/Citadel без операции, если никто не берёт её даже по полу и рука реально усиливает кулак (`BestStackPotential > FieldPotential`) → `FieldCombatPower` (форма `Any`, `TargetHex` = кулак). Экипировка из руки уже разыгрывается Phase B по оценке против угроз — отдельного вида потребности не заводилось.
- **Не тронуто:** `AttackTacticalOpportunity` (попутный удар по армии, не цель Attack) остаётся на свежем пороге рейда.

### Шаг 5 — планировщик кулака (вырастает из `PlanGather`)

**Основа:** `GroundCombatAssemblyPlanner.PlanGather`/`PlanGatherForHost`. Хост сейчас — минимум суммарного AP, поддержка — по приросту шанса на AP, без лимитов.

**Изменения:**
- Цель — пик (P_field), а не «минимум, чтобы пройти порог».
- Командир — `HeroRoleEvaluator`. При необходимости смена командира: перестановка (`ArmyData.TryReorderCommander`) или передача героя.
- Точка сбора выбирается по стоимости AP (сейчас точка — хост).
- Доноры дополнительно из армий Raid/ActiveDefence; цена донора — ценность брошенной операции (`TaskScore` его интента).
- Донор после передачи идёт домой: есть `ReleaseAttackSupport` + `SupportReturn`.

**Вопрос к пользователю:** снимать донора с Raid/ActiveDefence через Continuity (ретайр интента) или через арбитраж `ActorCommitments` (TaskScore)? Рекомендация: арбитраж, без новых путей освобождения.

### Шаг 6 — разведка при кулаке (Recon Escort)

**Связи Attack → Recon сейчас нет.** Recon знает только `ReconObjectiveKind { Explore, Refresh, Surveil, AirSweep }`.

**План:**
- Attack публикует потребность в обзоре цели или маршрута.
- Recon закрывает её существующим `Refresh`/`Surveil` на `FocusHex` кулака: ближайший свободный разведчик, иначе разыграть карту разведчика, иначе атака идёт без эскорта.
- Новый вид цели разведки — только если `Refresh`/`Surveil` не подходят. Сначала сверить с `ReconObjectiveEvaluator`.

### Шаг 7 — кампания после захвата

**Сейчас:** `MissionContinuityLayer.Attack` при `AttackTargetStatus.Captured` пишет COMPLETE; армия стоит, claim отпускается в Housekeeping.

**Добавить:**
- **Гарнизон** на взятую базу: из руки (через Demand/Phase A), иначе раненый или самый слабый боец кулака (Housekeeping или отдельная нога).
- **Давить или перегруппироваться** — решает скор следующего `AttackObjective` против отдыха и доращивания.
- **Цитадель:** сейчас `TrySelectStrategicDirection(..., allowTrueWorldFallback: false)`, и цитадель попадает в цели Attack только из известных зданий. Нужно дать координаты через чит-якорь; неразведанные защитники — запрос в Recon (шаг 6).

### Затем — авиация в поддержку атаки

**Образец:** как авиацию подключали к Recon:
- `ReconAirReservationPrepass`;
- `AviationSortieReservationEvaluator` (Combat отложен);
- `ReconAirReservation`.

**Для атаки:** удар по защитникам цели перед штурмом, «до/после» через `AviationCombatEstimator.EstimateAirStrike` + `WorthIt`. Для рейда это уже сделано: `RaidProvisioner.ProvisionAirSupport`, `RaidRecoveryPlanner.ProjectAirSupport`. Взять тот же механизм, не второй.

---

## 7. Побочные хвосты

- **Тесты в запушенных ветках — СДЕЛАНО.** `a9139613` в `fix/…` (только `AiAttackLaneTests.cs`), `fix` влит в `refactor` (`96c6439f`), `refactor` — в `feature` (`3d2d6533`, содержимое не изменилось). Все три ветки запушены, в каждой обе сборки 0/0.
- **Два цикла дуэли Fate.** Дуэль в UI (`BattleAttackPopupUI.RunDuel`) и в оценке (`WorthIt.ResolveExchange`) — два цикла одного порядка ходов; решение у них общее (`FateDuelAi`). Вынести порядок в `FateDuelAi` — отдельным шагом, по желанию.
- **Открыто из аудита V2:** выравнивание Raid/Attack. Смена фазы (исполнитель против outcome), потеря поддержки (сразу против через проход), `HexEventOccurred` на ногах возврата.

---

## 8. Порядок на следующую сессию

1. Шаг 0 (R1–R5) сделан — `a85d8196`.
2. Тесты в запушенных ветках и пуш — сделано.
3. Шаг 3 — сделано. Следом — Phase B: ценность карты героя через `ProjectCommand` (отдельный коммит).
4. Дальше по одному шагу 4 → 5 → 6 → 7, затем авиация. Каждый шаг — отдельный коммит, обе сборки 0/0.
