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

## 5a. Перепроверка шагов 3–7 (25.09, вторая)

### Реализация — найдено и исправлено

| # | Где | Дефект | Исправление |
|---|---|---|---|
| F1 | `HeroRoleEvaluator.BestCommanderFor` | Комментарий обещал, что текущий командир выигрывает любую ничью; на деле — только полную ничью всех ключей | Комментарий исправлен |
| F2 | `StrategicCardEvaluator.HeroCommandMarginalValue` | Проекция «до» для армии с героем не учитывала, что новый герой тоже займёт место: +1 место, сравнение смещено против новичка | `otherHeroes = DestHeroCount` |
| F3 | Housekeeping, заполнение гарнизона (2b) | `GarrisonDeficit` старше `Legality`, поэтому оценка приняла бы ход, после которого кулак нежизнеспособен — кулак мог «стечь» в гарнизон | Кандидат строится, только если армия остаётся жизнеспособной без бойца |
| F4 | `GroundCombatAssemblyPlan.WinChanceGate` | Порог ставил только `Plan` (обёрткой), планы `PlanForArmyAtThreshold`/`TryAssembleForHost` несли 0 | Порог пишется там, где план строится против порога; обёртка удалена |
| F5 | `AppendAttackDemands` (связанная операция) | Запрос шёл при шансе < 0.65, а Reinforcement — только < 0.2. `TryHandoffGroundCombatSupport` привязывал поддержку, Continuity тут же возвращал Assault, `SupportArmyId` оставался висеть, запрос дальше «удовлетворён» | Один порог — `AttackWinChanceFloor` |
| F6 | `AppendUnboundAttackDemand` | Не проверял сбор из нескольких армий — мог просить карты, когда цель уже берётся Gather | Добавлена проверка `PlanGather` |
| F7 | `PlanGatherForHost` | Командир выбирался по составу хоста, а транзакция — по собранному; ответы могли разойтись | Командир выбирается для пикового состава (`BestCommanderFor(..., prospectiveBodies)` = тела пула) |
| F9 | `AttackObjectiveEvaluator.ObservationNeeds` | Recon-цели замораживаются до `ResolveActive`: захваченная цель ещё «живая» | Фильтр `EvaluateTarget == Continue` |
| F10 | Транзакция штурма, перестановка командира | Мутация мира без подъёма `V2StateVersion`/`StateChanged`, если не было передач | `GroundCombatAssaultOutcome.CommanderReordered` → `ProvisioningResult.Ok(..., otherMutation)` |

Замечено, не дефект: `TryEnemyCitadelAnchor` теперь пропускает выбывших игроков — это меняет и якорь `AirSweep` (раньше мог указать на цитадель выбывшего).

### Резервация ресурсов

- **Новых писателей резервов нет.** `StrategicResourceReservationLedger`, `ApBudgetLedger`, лизинги не менялись.
- **Новые запросы** (`FieldCombatPower` для несвязанной цели — форма `Any`; для гарнизона — новая форма `Garrison`) идут через тот же портфель Phase A и тот же AP-пул. `DeliveryShape` читается только в `MaterializationDeliveryPolicy` и в проверке подкрепления (`IndependentFieldArmy`) — новая форма в чужую ветку не попадает; после доставки армии получают обычный лизинг (`StrategicCapabilityLeaseRegistry.Mark`).
- **Акторы:** доноры, идущие домой, держатся `ActorCommitments` через `GroundCombatLegs.HeldGroundSupportArmyIds`; тот же список читают отпечатки допуска Economy/Development — смена доноров их корректно перезапускает.
- **Стоимость (F8, в игру):** нога донора домой — активация каждый ход (раньше донор стоял бесплатно). Сбор до пика берёт больше доноров → больше AP на ходьбу. Перестановка командира — 0 AP.
- **Повторная проверка штурма** больше не строже допуска: Raid-инкумбент, допущенный по 0.4, теперь реально выполняется (раньше транзакция отклоняла его по 0.65).

### Кеш (по дереву `docs/ai-v2-cache-audit.md`)

- **Класс C:** `ForceBaselineRegistry` — один писатель (пайплайн после первого скана), ключ по игроку, сброс в `CitadelSetupController`; `Analysis` только читает (A-1 не повторяется). `AttackIntent.GatherReturns` — пишет только Continuity (`AdvanceIntent`, `ResolveGatherReturns`, отсечка исхода в `ReconcileOutcome`); намерения не сохраняются в сейв.
- **Класс A:** новые поля `SelfSnapshot`/`ArmySnapshot.HeroCount` строятся в `BuildSelf` каждого скана; читатели только снимок.
- **Живые чтения реестра намерений** (`ObservationNeeds` из Recon) — тот же шов, что уже у `Analysis`/Housekeeping; порядок «Recon до ResolveActive» закрыт F9.
- **Версия состояния:** единственная новая мутация (перестановка командира) версионирована (F10).
- **Класс B:** ключ `GroundCombatAdmissionRegistry` не менялся.
- Вывод: инварианты I1–I6 в силе.

## 5b. Перепроверка после авиации, доноров, дуэли и передачи героя (25.09, третья)

### Реализация
- Код авиации, выравнивания, доноров, дуэли и передачи героя перечитан; эквивалентность Raid после выноса авиации подтверждена построчно.
- **Найдено и исправлено — AP передачи не резервировались (дефект старше кулака).** На шаге передачи (`atRendezvous`) допуск ноги (`GroundCombatLegChecks.ValidateReinforcement`) закладывал только активацию поддержки, а сама передача платит активацию каждого новичка армии, уже действовавшей в этом ходу — тела в основную и, после обменов, вытесненных в донора (донор к этому моменту обычно уже прошёл путь). Эти AP тратились сверх конверта у Raid и Attack. Теперь решение передачи одно — `GroundCombatReinforcement.PlanHandoff` (передача героя → заполнение свободных мест → обмен «свежий на слабейшего»), его стоимость `HandoffApCost` входит в AP ноги, а исполнитель (`TaskExecutor.ApplyReinforcementHandoff`) выполняет ровно этот план одной `ArmyActions.TransferMembersAtomic` (проверка — `ArmyActions.CanExchangeMembers`, та же, что у передачи). Обмен «один на один» Raid — частный случай, поведение то же.
- Удалён мёртвый фасад `RaidProvisioner.SparableSupportBodies`.
- Замечено, не менялось: следующий ход полёта привязанного крыла (Raid и Attack) не защищён `StrategicSpendability` — защищены только обязательства посадки; так было и у Raid.

### Резервация
- Новых писателей резервов нет; резервы по владельцу держит только Economy, поэтому снятие Raid/ActiveDefence-донора ничего не оставляет висеть.
- Нога авиаподдержки Attack несёт AP и Energy активации крыла, провижинг проверяет тратимую Energy; сироты-вылеты получают защищённую активацию как обязательство посадки.
- AP передачи теперь заложены (см. выше).

### Кеш
- Новые записи класса C только в Continuity (поля намерений) и одна новая мутация `AirSortieRegistry` из Continuity (`ReleaseOrphanStrikes`) — внесено в `docs/ai-v2-cache-audit.md`.
- Все мутации мира версионированы: передача/обмен — `CombatChanged` + `V2StateVersion.Bump` в исполнителе (как было), перестановка командира — `otherMutation` в `ProvisioningResult.Ok`.

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
- **Demand:** связанная операция просит поддержку, когда основной не проходит пол атаки — тот же порог, на котором машина фаз переходит в Reinforcement (один владелец; иначе доставленная армия привязывалась и тут же терялась, см. перепроверку, F5). Новое `AppendUnboundAttackDemand`: лучшая Base/Citadel без операции, если её не берёт ни одна армия, ни пакет на клетке, ни сбор (`PlanGather`) даже по полу, и рука реально усиливает кулак (`BestStackPotential > FieldPotential`) → `FieldCombatPower` (форма `Any`, `TargetHex` = кулак). Экипировка из руки уже разыгрывается Phase B по оценке против угроз — отдельного вида потребности не заводилось.
- **Не тронуто:** `AttackTacticalOpportunity` (попутный удар по армии, не цель Attack) остаётся на свежем пороге рейда.

### Шаг 5 — планировщик кулака — СДЕЛАНО ЧАСТИЧНО

**Сделано:**
- **Сбор до пика.** `PlanGatherForHost`: ниже порога берётся любая улучшающая поддержка (лучший прирост шанса на AP); выше порога — только пока поддержка добавляет ≥ `attackGatherMinWinGain` (0.05, один прогон Monte-Carlo = 0.04). Хост по-прежнему — минимум суммарного AP.
- **Выход из Gather.** Хост идёт на штурм, когда все запланированные доноры отданы и он проходит пол атаки (или раньше, если он уже проходит, а нога застряла: `StallTurns > 0`). Раньше — сразу, как только проходил порог.
- **Прямой штурм против Gather.** Для свежей цели, которую уже берёт одна армия, `AppendAttack` считает и Gather до пика; предлагается то, у чего выше `TaskScore` (шанс против ETA/AP).
- **Командир.** `HeroRoleEvaluator.BestCommanderFor(members, …)` — лучший законный командир живой армии для конкретного боя. Им считается проекция Gather (командир и вместимость), и `GroundCombatAssaultTransactionRunner.Run` переставляет его перед маршем (`TryReorderCommander`, 0 AP; общая транзакция — это правило действует и для Raid).
- **Донор уходит домой.** Новая нога `AttackMissionPhase.GatherReturn` (по одной на донора, `AttackIntent.GatherReturns`): после попытки передачи остаток донора идёт на базу (`SelectReturnBase`), держится как поддержка операции (`GroundCombatLegs.HeldGroundSupportArmyIds`), отпускается по прибытии, потере или отсутствии базы. Исход ноги донора не трогает жизненный цикл намерения (`ReconcileOutcome` возвращается сразу; неудача ноги — отпуск донора). Точки: исполнитель, провижинер, ключ аллокатора, ревалидатор, леджер исходов, политика допуска (ноги не конкурируют), `GroundCombatLegs`.

**Остальное:**
- **Доноры из Raid/ActiveDefence — СДЕЛАНО (арбитраж, решение пользователя).** `GroundCombatDonorPolicy.BorrowableDonorApPrices`: основная армия активной Raid/ActiveDefence продаётся сбору по цене брошенной операции — `MissionIntent.LastIntrinsicValue` (последний допущенный ненулевой `TaskScore`, пишет Continuity) в AP-эквиваленте по ставке `taskScoreReactivationApWeight`; операция неизвестной ценности не продаётся. `PlanGather` берёт купленных только в поддержку (не хостом) и добавляет цену к их AP, поэтому `TaskScore` сбора несёт потерю и аллокатор арбитрирует. Головная нога свежего сбора — только свободная армия (купленная ещё держится своей операцией); когда намерение Attack создано, `ResolveActive` снимает Raid/ActiveDefence, чья армия в `GatherSupportArmyIds` (до ремонта сирот — одолженный ActiveDefence рейд возобновится и будет снят тем же правилом). Тот же список покупаемых — в пере-плане Gather и в проверке запроса для несвязанной цели.
- **Хост навстречу — не нужно (решение пользователя):** хост стоит; если он всё же движется, поддержка его догоняет — нога Gather/Reinforcement каждый ход идёт к текущей клетке хоста.
- **Передача героя от донора к хосту — СДЕЛАНО.** Одно правило `GroundCombatReinforcement.CommandHandover`: полевой герой поддержки (не SupportOperator) переходит, если вместе с хостом он — лучший командир этого боя (`HeroRoleEvaluator.BestCommanderFor` с телами, которые придут), а донор остаётся непустым и законным по вместимости. Им пользуются: проекция сбора (`PlanGatherForHost`: командир и вместимость по герою, донор героя остаётся в плане), удержание донора в плане (Continuity), допуск ноги (`ImprovesOdds(..., allowCommandHandover)` — только Attack) и передача (`TaskExecutor.ApplyReinforcementHandoff` с боем объекта). Сама передача — одна атомарная `ArmyActions.TransferMembersAtomic(..., promoteToCommander)`: вместимость проверяется под командой героя, герой встаёт первым (как нулевая по AP перестановка `TryReorderCommander`). **Обмены:** если у хоста нет места для самого героя, герой меняется на самого раненого, затем слабейшего бойца хоста; под командой героя свободные места занимают лучшие тела донора, а каждое следующее тело донора, которое сильнее самого слабого оставшегося бойца хоста, меняется на него. Всё это — одна атомарная `ArmyActions.TransferMembersAtomic(..., promoteToCommander, displaced)`: обе армии проверяются по конечному составу, новичок в уже активированной армии платит активацию. Донор не опустошается и должен вмещать то, что оставил и получил, без героя — иначе этот герой не передаётся. Проекция сбора учитывает обмен героя на бойца хоста. Если передача с героем отклонена — прежняя передача одних тел (со своим обменом «один на один»). Raid не включает: его проекции героев не видят.

### Шаг 6 — разведка при кулаке — СДЕЛАНО

- **Публикация:** `AttackObjectiveEvaluator.ObservationNeeds(snap)` — клетки целей живых операций Attack в фазах Gather / Assault / Reinforcement (читает `MissionIntentRegistry`, как уже делают `Analysis` и Housekeeping).
- **Закрытие:** `ReconObjectiveEvaluator` строит по каждой потребности обычный Refresh той же идентичности (`AttackNeedRefresh`, заменяет общий Refresh этой клетки): обзор старше `attackIntelMaxAgeTurns` (1) → `Staleness` = 1 и `StrategicRelevance` = 1. То же правило в `RefreshAt` (переоценка идущего Refresh-интента), чтобы он не угас раньше операции.
- Нового вида цели разведки нет. «Ближайший разведчик → разыграть карту разведчика → без эскорта» — это существующие назначение Recon (`ReconAssignmentPlanner`) и Demand `ScoutCapability`. Порог 1 совпадает с общим порогом устаревания (возраст ≥ 2), так что завершение Refresh работает как раньше; меняется только ценность.
- Защитники на цели — армии-контакты — по-прежнему идут через Surveil.

### Шаг 7 — кампания после захвата — СДЕЛАНО

- **Гарнизон из руки.** `AggressionDemandEvaluator.AppendHeldBaseGarrisonDemands`: своя база, на которой стоит наша полевая армия, а гарнизон ниже нормы (`secureBaseMinNonHeroUnits` / цитадель) → `FieldCombatPower` с новой формой доставки `CapabilityDeliveryShape.Garrison` (`MaterializationDeliveryPolicy`: только `DeploymentKind.Garrison` на `TargetHex`). Захват даёт инвалидацию `Infrastructure`, которая перезапускает Aggression внутри хода, поэтому рука получает шанс раньше Housekeeping.
- **Иначе — боец кулака.** Housekeeping (`ArmyReorganizationCandidates`, п. 2b): гарнизон ниже нормы берёт у жизнеспособной армии на той же клетке одного бойца — самого раненого, затем самого слабого, не героя. Решает существующий ключ `GarrisonDeficit`. **Правило общее:** оно действует на любой своей базе со свободной армией (занятые операцией армии не отдают — `CanDonate`/`IsCommitted`). Смотреть в игре.
- **Давить или перегруппироваться.** Отдельного кода не нужно: после захвата намерение завершается, армия свободна, и следующая цель Attack соревнуется по скору, где готовность (шаг 4) снижает скор, пока кулак не собран.
- **Цитадель через чит-якорь.** `WorldAnalysis.TryEnemyCitadelAnchor` вынесен из `TryAirSweepAnchor` (один владелец). Когда игрок не знает ни одной враждебной Base/Citadel, `AttackObjectiveEvaluator.ObservationNeeds` публикует якорь; Recon закрывает его Refresh или, если клетку никогда не видели, обычным Explore. Цель Attack появляется только из `Known.Buildings` после обзора — неразведанные защитники не допускаются.

### Авиация в поддержку атаки — СДЕЛАНО

**Один механизм на Raid и Attack** — `Missions/GroundCombat/GroundCombatAirSupport` (+ `GroundCombatLegStep.AirStrikeSortie`), вынесен из Raid без изменения его поведения:
- `Options` — свободные крылья, оценка удара (`AviationCombatEstimator.EstimateAirStrike`), второй удар для вертолётов, база посадки, AP/Energy. Выжившие раскладываются обратно по армиям (`AfterStrike`, новые `AirStrikeEstimate.SurvivorSourceIndices`), так что многоармейный объект остаётся последовательными боями. Шанс «до/после» считает сама полоса (`winAgainst`): Raid — прежним чтением, Attack — `EstimateSequential` основной армии против объекта с бонусом гекса.
- `LegRequirements`, `SortieEta` (= `AiV2Util.TurnsToCover`, общее правило с наземными переходами рейда), `SortieLive`.
- Провижинг: `TryResolveWing` (крыло и его вылет) → проверки цели полосы → `TryFinishWing` (маршрут, ценность удара, конверт AP/Energy, тратимая Energy).
- Шаг полёта: `GroundCombatLegStep.AirStrikeSortie` (был `TaskExecutor.RunRaidAirSupportStep`). `ExecutionResult.RaidAirSupportStrikeSucceeded` → `AirSupportStrikeSucceeded`.
- Raid оставил за собой: цель-армию нейтралов, `AirStrikePolicy.RaidSupport` (минимум 1 выживший), свою оценку восстановления (`PlanScore`) и сравнение вариантов.

**Attack:**
- Боковая нога `AttackMissionPhase.AirSupport` (как `GatherReturn`: не фаза операции, исход не трогает жизненный цикл, не конкурирует с ногами той же операции).
- **Привязка** (Continuity, `ResolveAttackAirSupport`): операция в Assault; удар крыла приходится на 1..`attackAirSupportLeadTurns` (1) хода раньше прибытия основной армии — штурм в тот же ход не опережает удар; обзор объекта свежий (`attackIntelMaxAgeTurns`); прирост шанса ≥ `attackAirSupportMinWinGain` (0.05). Из вариантов — лучший итоговый шанс, затем ETA, AP, Energy. Крыло, уже летящее по любому вылету, не берётся.
- **Удар** — `AirStrikePolicy.Standard` по всем защитникам объекта (захват делает наземный штурм).
- **Отпуск:** после посадки, если не взлетело к следующему ходу, если перестало быть крылом, при провале ноги. Пока вылет в воздухе, крыло не отпускается.
- **Сироты:** `GroundCombatAirSupport.ReleaseOrphanStrikes` в начале/конце `ResolveActive` — ударный вылет, крыло которого не держит ни одна операция (`GroundCombatLegs.HeldAirSupportArmyId`), становится существующим обязательством посадки (`Rebase` домой, `AviationRebasePlanner.FindMandatoryContinuations`; его активацию уже бережёт `StrategicSpendability`). Это закрывает и прежнюю дыру Raid: крыло, отпущенное в воздухе, больше не остаётся висеть.
- Резервация: нога несёт AP и Energy активации крыла (`LegRequirements`), провижинг проверяет тратимую Energy; удержание — `ActorCommitments` через `HeldAirSupportArmyId`. Кеш: поля намерения пишет только Continuity; живое чтение `AirSortieRegistry` — тот же шов, что у Raid.

## 7. Побочные хвосты

- **Тесты в запушенных ветках — СДЕЛАНО.** `a9139613` в `fix/…` (только `AiAttackLaneTests.cs`), `fix` влит в `refactor` (`96c6439f`), `refactor` — в `feature` (`3d2d6533`, содержимое не изменилось). Все три ветки запушены, в каждой обе сборки 0/0.
- **Два цикла дуэли Fate — СДЕЛАНО.** Очерёдность дуэли (защитник первым, чередование, трата возвращает ход сопернику) — один владелец `Game.Combat.FateDuelOrder` (структура, без выделений в Monte-Carlo) в `FateDuelAi.cs`; её используют `BattleAttackPopupUI.RunDuel` и `WorthIt.ResolveExchange`. Решение о трате по-прежнему `FateDuelAi.ShouldSpendFate`; ход человека и анимации остались в UI.
- **Аудит V2, выравнивание Raid/Attack:**
  - **Потеря поддержки — исправлено.** Для Attack леджер классифицировал `MoverLost`/`TargetInvalidated` ноги поддержки как `Failed`, и `ReconcileOutcome` снимал всю операцию (например, погиб донор Gather в пути). Теперь, как у Raid, это `Blocked`, а поддержку чистит следующий проход `ResolveActive` (`MissionOutcomeLedger.Classify`).
  - **`HexEventOccurred` — исправлено.** Шаги Attack (штурм, конвой/Gather, возврат) теперь, как Raid, останавливаются с `HexEventStarted`, а в штурме событие гекса считается физическим началом операции.
  - **Смена фазы (исполнитель против outcome) — открыто.** Raid меняет фазу прямыми вызовами Continuity из исполнителя (`CompleteRaidReinforcement`, `BeginRaidSupportReturn`), Attack — через outcome в `AdvanceIntent`. Это архитектурное выравнивание одной модели на две полосы; поведение сейчас корректно в обеих, менять без отдельного решения не стал.

---

## 8. Порядок на следующую сессию

1. Шаг 0 (R1–R5) сделан — `a85d8196`.
2. Тесты в запушенных ветках и пуш — сделано.
3. Шаг 3 — сделано. Следом — Phase B: ценность карты героя через `ProjectCommand` (отдельный коммит).
4. Дальше по одному шагу 4 → 5 → 6 → 7, затем авиация. Каждый шаг — отдельный коммит, обе сборки 0/0.
