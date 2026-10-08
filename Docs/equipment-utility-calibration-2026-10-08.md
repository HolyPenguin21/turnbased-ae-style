# Калибровка оценки Equipment/Mutator и выбор получателя — 2026-10-08

Ветка `feat/equipment-calibration-2026-10-08`, база — master `833d3b50`. Изменение поведения AI V2 (не архитектуры).
Игровой баланс **не** подтверждён: Unity (6000.5.4f1) в этой среде не запускалась.

## 1. Единая полезность U

Владелец — `EquipmentEfficiency.Utility` (`EquipmentEfficiency.Utility.cs`). Результат — **знаковая** полезность в единицах
карточного скора за резервный горизонт в три будущих хода владельца:

```
U = 1.10·ΔC + U_AP + U_move + U_vision + U_detection + U_stealth + U_fate + U_AA
```

Ничего не обрезается и не берётся по модулю; потеря дальности, скорости, способности или защиты даёт отрицательный вклад.
В U нет цены, шанса Challenge, Repeat и Supply — ими владеют `GenerationStep`/`ResourceCost`/`ActionPrice`/`DevelopmentDiversity`.

### Единицы и граница API
* Внутренний результат `UtilityBreakdown` — карточный скор (за весь горизонт).
* `EquipmentDelta` хранит `U / equipmentUpgradePersistence`, `EquipmentUpgradeValue` умножает обратно: ровно один путь конвертации,
  `ExpectedGain/TacticalGain` ↔ `EquipmentDelta` сохраняют прежний множитель `combatPowerPerBodyEstimate` (round-trip проверяется
  существующим `SignedLossCannotBeErasedByLegacyMatchupWitness`). Persistence (3) не применяется к U повторно.
* Единственное место, где U меняет единицу — `ScoreEquipmentByEfficiency` (StrategicCardEvaluator).

### Боевая дельта ΔC
Модель контактов: три контакта владельца с «свежим» противником из списка профилей; HP хоста переносится (среднее поле),
`P(alive)` умножает каждый следующий контакт. Урон/летальность — `BattleSimulationKernel.ExpectedExchangeDamage` (точное
ожидание, без RNG/Fate, с модификаторами `ChallengeResult.ApplyAbilityModifiers`); летальность `P(D≥HP)=E[min(D,HP)]−E[min(D,HP−1)]`.
Учтено: порядок по инициативе (с командирской, равенство 50/50), ShockAttack подавляет ответ, Berserk усиливает ответ после попадания и
сбрасывается между контактами, Regeneration лечит 1 HP в конце хода только живого носителя, Splash/Scorcher — вторичный урон по
соседям (`SecondaryDamage`), дальность — доля геометрии (0.24/0.32/0.28/0.16) или известная дистанция. Полезный урон ограничен HP цели.
`C = 3.63 · Σ P(alive)·урон/HP цели`, усреднение по профилям. Профили берутся с весом копий из `EquipmentTargets` (состав, без скрытых
координат); без состава — каталожный prior A3/D2/HP4/I1. Авиация исключается из наземного C.

Калибровка масштаба: на якорях Iron Concord отношение контрольного C к «сырой» модели 3.48 / 3.68 / 3.83 (Light/Medium/Heavy), среднее
3.63 → `equipCombatBodyScale` (с учётом дальности ответа цели). 1.10 = `equipCombatCardScale` по ТЗ.

### Остальные компоненты
| Компонент | Формула |
|---|---|
| AP | `Signed(ToCardScore)(E[N]·(APдо−APпосле))`, E[N]=1.5; RapidReaction = эффективный AP 0 |
| Скорость | маршрут: `ArmyAP·(⌈L/vдо⌉−⌈L/vпосле⌉)`; без маршрута — прокси `0.075·0.15·Eref·Δv·3/max(1,vдо)`; v = min(хост, остальные); при маршруте E[N] уменьшается на число маршрутных активаций |
| Vision | `0.16·dark·(radius_after−radius_before)`, радиус — локальный максимум армии, dark=0.5 по умолчанию |
| Detection | `0.16·relevance·ΔP(spot>hide)`, relevance=0 без известной скрытой цели; `P(4,4)=0.36328125` |
| Stealth | `use·max(0, 0.9·max(0.35,risk) − ToCardScore(1))`; use=1 для разведчика, 0.25 резерв → 0.165 / 0.04125 |
| Fate командира | прежний весомый адаптер `ΔFate·0.5·ArmyAttack·equipCardValuePerE` |
| Fate оператора | `Σ ΔP·max(0,U_out−cost)` по **подтверждённым** выпускам (`OperatorOutputs`); пусто → 0 |
| AA | `1.10·` ожидаемый урон законной реакции по воздушной цели, дисконт за уже стреляющие AA армии; нет воздушной цели → 0 |

Убрано: `frailty 8/(D+HP)`, `0.5·Attack` за Stealth, `Attack×Init`, фиксированные множители способностей, деление на `1+carriers`,
`Supply` ×1…×15, флэт «Fate оператора 1.4E». Авиационные хосты остаются на прежней линейной таблице (`Delta`) как явный **прокси**.

## 2. Pair → S (выпуск и применение)
Существующая цепочка осталась: `BestEquipmentOpportunity` выбирает для каждого продукта лучшего получателя по U (цена/Chance/Repeat
одинаковы у всех получателей продукта), `ScoreGeneratedEquipmentUpgrade` считает
`S = p·U·Repeat − Cost(AP+ресурсы+p·attachment) − ChainStepPenalty(0.23)` и арбитраж сравнивает продукты по S вместе с наймом.
Готовая карта в руке (`NonCombatCardPlayer`) оценивается тем же U без Repeat, p и цены выпуска. `U≤0` не создаёт спроса, `PendingEquipmentCovers` сохранён.
Supply удалён из `EquipmentOpportunities`, `EquipmentUpgradeValue(MaterializationPlan)` и `NonCombatCardPlayer` (коммит `6346ff3a`).

## 3. Карта владельцев
| Эффект | Владелец |
|---|---|
| Статы, Hyperkinetic, Pyrokinetic, Critical, Ceramic, Shock, Berserk, Regeneration, Splash, Scorcher | `EquipmentEfficiency.Utility` через `BattleSimulationKernel` |
| ActivationAP, RapidReaction, скорость | `EquipmentEfficiency.Utility` + `ActionPrice` |
| Recce, Stealth, Detection | `EquipmentEfficiency.Utility` |
| AntiAir | `EquipmentEfficiency.Utility` (реакции по известным воздушным профилям) |
| Прочие эффекты (глобальный доход, ауры, призыв…) | `StrategicEffectRegistry` (не `IsPriced`) |
| Проекция/легальность | `EquipmentSystem` (не менялась) |

## 4. Проверка на якорях (локальный запуск, mono; ground-профили Iron Concord Starter, копии учтены)
U «ссылка из ТЗ (после) → реализация»:

| Продукт | Scout | Medium | Heavy | Light | Flamer | AT |
|---|---|---|---|---|---|---|
| Dermal Plating | Mut | 20 | -0.023 …   0.171 | AT Infantry | Rad Brute | 0 / 3 |
| Reactive Marrow | Mut | 20 | 0.000 …   0.212 | AT Infantry | Rad Brute | 1 / 0 |
| Reinforced Skeleton | Mut | 20 | -0.023 …   0.253 | AT Infantry | Rad Brute | 0 / 5 |
| Pain Suppression | Mut | 20 | -0.130 …   0.088 | AT Infantry | Ash Drifter | 0 / 46 |
| Regenerative Culture | Mut | 20 | 0.000 …   0.152 | Light Infantry | Rad Brute | 3 / 0 |
| Hyper-Regeneration | Mut | 20 | -0.130 …   0.097 | AT Infantry | Ash Drifter | 0 / 46 |
| Survivor Strain | Mut | 20 | -0.143 …   0.120 | AT Infantry | Heavy Infantry | 0 / 47 |
| Adrenal Surge | Mut | 20 | 0.042 …   0.247 | Light Infantry | Heavy Infantry | 0 / 0 |
| Metabolic Overdrive | Mut | 20 | -0.102 …   0.160 | Hooded | AT Infantry | 0 / 6 |
| Predator Reflexes | Mut | 20 | -0.210 …   0.171 | AT Infantry | Heavy Infantry | 0 / 46 |
| Neural Accelerator | Mut | 20 | 0.010 …   0.446 | AT Infantry | Hooded | 0 / 0 |
| Rapid Synapse | Mut | 20 | 0.000 …   0.254 | Rad Brute | AT Infantry | 3 / 1 |
| Hunter Glands | Mut | 20 | -0.145 …   0.109 | Rad Brute | AT Infantry | 6 / 12 |
| Enhanced Senses | Mut | 20 | -0.151 …   0.177 | AT Infantry | Hooded | 0 / 29 |
| Wanderer Strain | Mut | 20 | -0.022 …   0.227 | Rad Brute | AT Infantry | 0 / 1 |
| Chameleon Tissue | Mut | 20 | -0.160 …  -0.035 | Trapper | Hooded | 0 / 60 |
| Fortunate Genome | Mut | 16 | 0.150 …   0.150 | Dorian Kesh | Dorian Kesh | 0 / 0 |
| Ghost Genome | Mut | 16 | 0.000 …   0.041 | Dorian Kesh | Lira Sable | 6 / 0 |
| Hunter Genome | Mut | 16 | 0.000 …   0.080 | Dorian Kesh | Lira Sable | 6 / 0 |
| Reflex Genome | Mut | 16 | 0.000 …   0.225 | Dorian Kesh | Nadia Thorne | 3 / 0 |
| Flamer | Eq | 19 | -2.427 …   1.435 | Rad Brute | Ash Walker | 0 / 17 |
| AT Launcher | Eq | 19 | -0.663 …   2.165 | Rad Brute | Ash Walker | 0 / 3 |
| Claws | Eq | 19 | -2.380 …   0.463 | Rad Brute | Ash Walker | 0 / 41 |
| Heavy MG | Eq | 19 | -0.774 …   2.542 | Shard Wanderer | Ash Walker | 0 / 3 |
| Plasma Gun | Eq | 19 | 0.983 …   4.409 | Shard Wanderer | Ash Walker | 0 / 0 |
| Ballistic Shield | Eq | 19 | -0.023 …   0.171 | AT Infantry | Rad Brute | 0 / 3 |
| Ceramic Vest | Eq | 19 | -0.052 …   0.274 | AT Infantry | Heavy Infantry | 0 / 4 |
| Assault Rifle Kit | Eq | 19 | 0.150 …   1.400 | Ash Walker | Tech Scrapper | 0 / 0 |
| Marksman Rifle | Eq | 19 | -0.375 …   2.927 | Shard Wanderer | Ash Walker | 0 / 3 |
| Optical Scope | Eq | 19 | 0.263 …   2.438 | Flamer | Tech Scrapper | 0 / 0 |
| Mobility Harness | Eq | 19 | 0.080 …   0.160 | Hooded | Scrapper | 0 / 0 |
| Shotgun | Eq | 19 | -2.133 …   1.172 | Shard Wanderer | Ash Walker | 0 / 11 |
| Grenade Launcher | Eq | 19 | 0.000 …   2.924 | Rad Brute | Ash Walker | 3 / 0 |
| Shock Rifle | Eq | 19 | -1.472 …   1.319 | Rad Brute | Ash Walker | 0 / 6 |
| Incendiary Rifle | Eq | 19 | -1.480 …   2.524 | Rad Brute | Ash Walker | 11 / 4 |
| Rail Rifle | Eq | 19 | -1.398 …   1.453 | Rad Brute | Ash Walker | 3 / 3 |
| Twin SMG | Eq | 19 | -1.707 …   1.032 | Flamer | Ash Walker | 0 / 10 |
| Portable Mortar | Eq | 19 | 0.317 …   3.307 | Heavy Infantry | Tech Scrapper | 0 / 0 |
| Recoil Cannon | Eq | 19 | 0.358 …   3.743 | Shard Wanderer | Ash Walker | 0 / 0 |
| AA Launcher | Eq | 19 | 0.010 …   0.069 | Flamer | Scout | 0 / 0 |
| Armor Plate | Eq | 43 | 0.000 …   0.237 | Artillery Tank | Medium Tank | 11 / 0 |
| Reinforced Chassis | Eq | 43 | -0.228 …   0.512 | Scrap Mortar | Ash Howitzer | 0 / 92 |
| Servo Actuators | Eq | 43 | 0.051 …   0.295 | Ash Howitzer | Skimmer | 0 / 0 |
| Ceramic Plating | Eq | 43 | 0.000 …   0.515 | Scrap Mortar | Medium Tank | 16 / 0 |
| Reactive Armor | Eq | 43 | -0.241 …   0.319 | Scrap Mortar | Ash Howitzer | 0 / 90 |
| Nuclear Engine | Eq | 22 | 0.051 …   0.295 | Ash Howitzer | Skimmer | 0 / 0 |
| Turbocharger | Eq | 21 | 0.006 …   0.458 | Ash Howitzer | Artillery Tank | 0 / 0 |
| Artillery Cannon | Eq | 15 | -6.735 …   1.806 | Heavy Tank | Ash Howitzer | 0 / 6 |
| AT VH Launcher | Eq | 15 | -9.352 …   1.126 | Heavy Tank | Ash Howitzer | 0 / 12 |
| Double Barrel | Eq | 15 | 0.784 …  11.097 | Artillery Tank | Scav Carrier | 0 / 0 |
| Plasma Cannon | Eq | 15 | -9.021 …   3.737 | RC Vehicle | Ash Howitzer | 0 / 6 |
| Autocannon | Eq | 21 | -9.511 …   2.359 | Grave Engine | Ash Howitzer | 0 / 18 |
| HE Cannon | Eq | 21 | -6.526 …   3.865 | Grave Engine | Ash Howitzer | 3 / 10 |
| Flame Projector | Eq | 21 | -12.383 …   1.132 | Grave Engine | Ash Howitzer | 3 / 50 |
| Rail Cannon | Eq | 21 | -9.352 …   4.006 | Bastion Frame | Ash Howitzer | 0 / 9 |
| Shock Projector | Eq | 21 | 0.000 …   0.398 | Scrap Mortar | RC Vehicle | 16 / 0 |
| AA Mount | Eq | 21 | 0.000 …   0.190 | Artillery Tank | Crawler | 3 / 0 |
| Dozer Blade | Eq | 21 | -12.096 …   0.306 | Bastion Frame | Ash Howitzer | 0 / 54 |
| Siege Ram | Eq | 21 | -12.021 …   0.583 | Grave Engine | Ash Howitzer | 0 / 54 |
| Spiked Ram | Eq | 21 | -12.079 …   0.306 | Bastion Frame | Ash Howitzer | 0 / 54 |
| Mortar Rack | Eq | 21 | -2.893 …   4.066 | Heavy Tank | Ash Howitzer | 0 / 9 |
| Assault Conversion Kit | Eq | 21 | -12.304 …   0.013 | Grave Engine | Ash Howitzer | 3 / 54 |

Hunter Glands на Scout → 0.000 (как в ТЗ). Отклонения объяснены правилами, не подгонкой: дальность ответа цели теперь учтена (`DefenderProfile.Range`), вторичные получатели Splash/Scorcher берутся из реального боя или оцениваются по известному составу, Regeneration по среднему полю даёт ~2× к контрольной. Цены/Chance/S-колонки
референса здесь не воспроизводились (общая арифметика S не менялась и покрыта существующими тестами цепочек).

## 5. Производительность
Микробенч (mono, 16 профилей, 1000 различных состояний «после»): ~0.30 с до оптимизации → ~0.15 с после выноса независимых от контакта
вызовов; состояние «до» кешируется `ConditionalWeakTable` по экземпляру списка целей (жизнь = жизнь снимка/цели). Снимков Unity/IL2CPP нет.

## 6. Проверки
* Локальный раннер (`D:\aiv-work`, mono Unity): базовая ревизия 1312 passed → 1328 passed; обычный набор регрессий нет, кроме двух намеренно удалённых тестов Supply.
* С патчем Unity-null (`patchrun.sh`): 1480 → 1496 passed, регрессий нет (кроме тех же двух удалённых).
* Обновлены тесты, фиксировавшие старую таблицу: «нет противника» теперь даёт значение против каталожного prior; Regeneration зависит от
  летальности противника; Pyrokinetic без цели усредняет известные составы.
* Новые тесты: `AiEquipmentUtilityTests` (19), `OwnUtility_DoesNotDependOnDeckPlusHandSize`.

## 7. Не сделано / ограничения
* **Unity не запускалась**: ни EditMode, ни сценарии §14.4 (подготовка по ходам, Challenge-проигрыш, потеря площадки, rebuild Phase A…). Шаги: открыть проект в 6000.5.4f1, Test Runner → EditMode → весь набор; затем вручную оба четырёхходовых маршрута.
* Полный проход по 62 продуктам выполнен через настоящий код (`EquipmentSystem.CanAttachPreview` → `EquipmentDeltaParts` → `EquipmentUpgradeValue`) на карточных данных, считанных из `Assets/Cards/*.asset`, в локальном раннере — но **не в редакторе Unity** (см. §8).
* Fate оператора: по решению владельца — простая ценность `equipOperatorFateValue = 0.15` карточного скора за каждое очко Fate героя с Researcher/Assembler (раньше плоско ≈0.105). Если контекст передаст подтверждённые выпуски (`OperatorOutputs`), используется формула ТЗ §9.2 вместо этой константы. Fortunate Genome на оператора: +0.15.
* Fate командира: сохранён прежний владельческий адаптер (`ΔFate·0.5·ArmyAttack` в карточных единицах) — ТЗ §9.3 прямо требует сохранить его и не выдавать ΔWinChance за ΔC.
* Авиационные хосты: Attack и урон-способности считаются как авиаудар (тот же обмен ядра `BattleSimulationKernel`, без ответного огня, аналитически и одинаково для «до/после»; вылеты = `ExpectedActivations`); Defense/HP/скорость/AP остаются прокси линейной таблицей (огонь AA не моделируется и в `AviationCombatEstimator`).
* Известный маршрут: для живого хоста с целевым гексом миссии берётся `AiV2Util.TravelCost` (терраин) и сумма AP остальных членов армии; Move+AP считаются одной дельтой общей стоимости пути. Без цели/армии остаётся прокси.
* Покрытие карты для Vision — `MapKnowledge.ExplorableUnknownFrac` при наличии снимка. `DetectionRelevance` остаётся 0: существующий владелец разведки (`ScoutCapabilityContext.FromReconObjective`) сам никогда не объявляет скрытую цель (`DetectionRelevant=false`), а ТЗ запрещает выдумывать её.
* `EquipmentReserve` в `WorldAnalysis.Self` (без снапшота) теперь считает ту же формулу в reference-контексте и переводит в «боевые тела» на границе потребителя (см. 7b); эффекты, зависящие от мира (реестр, AA-контакты), в резерве равны 0.
* Тест-прогон не воспроизводит Приложение А по ценам/Chance/S и не содержит сравнения с наймом (S-таблицы).

## 7a. Дополнения 2026-10-08 (после ревью владельца)
* **Дальность ответа цели.** В `WorthIt.DefenderProfile` добавлено поле `Range` (-1 = неизвестно; параметр конструктора в конце, остальные вызовы не менялись). Источники передают дальность: живой юнит (`FromLiveUnit`), карта (`ProfileFromCard`, `AiPower.ToDefenderProfile`), охрана события и память (`AiMapMemory`, `HexEventGuardEstimate`), копии после экипировки/авиаудара. Симуляция боя поле не читает (в списке «не участвует в ключе кеша» `WorthIt.EstimateCache`); оценка Equipment читает его: на дистанции d хост бьёт, если его Range ≥ d, цель отвечает, если её Range ≥ d (неизвестная отвечает везде). Дистанции — доли 0.24/0.32/0.28/0.16 либо одна известная; обмен считается один раз, дистанции его только включают. `DefenderFingerprint` (допуск Development) учитывает Range. Масштаб `equipCombatBodyScale` пересчитан по якорям: 3.66 → 3.63.
* **Fate героя по роли.** Командирский Fate считается, если герой не Researcher/Assembler либо стоит во главе полевой (не гарнизонной) армии с бойцами; операторский (`equipOperatorFateValue`) — всегда для оператора. Раньше герой-оператор получал оба вклада всегда.

## 7b. Правки по ревью модели (2026-10-08)
1. **Shock и убийство — совместные исходы.** Убийство входит в попадание, поэтому ответ противника: без Shock — `1 − P(убийство)`; с Shock — `1 − P(попадание)` (ответ отменяет любой положительный урон). Раньше умножались `(1−kill)(1−hit)` и убитая масса вычиталась дважды (A8 против D2/HP4: 0.034 вместо 0.055). То же для ответа нашего хоста на Shock противника (`EquipmentEfficiency.AnswerWeights`).
2. **Berserk противника.** Пережив наше попадание, противник отвечает с Attack, поднятым на `BerserkAttackGain` (ответ без попадания — с обычным); при Shock такого ответа нет.
3. **AA — резерв против законных реакций.** Конкретные контакты (`AirContact`: известная воздушная армия, дистанция от армии носителя, видит ли владелец гекс, скрыт ли носитель, использована ли реакция, сколько носителей армии стреляют раньше по порядку `AntiAirRules`) платят только если реакция законна: `AntiAirRules.TryGetRadius` (теперь принимает и проектируемый список способностей), видимость, не скрыт, реакция не потрачена. Без конкретных контактов остаётся помеченная резервная оценка по известному воздушному составу (proxy), не выдаваемая за законный выстрел. Контакты берутся из `Known.EnemySightings` (честное знание), только для живого носителя.
4. **Вторичные получатели Splash/Scorcher.** Получатель — реальный сосед основной цели из её боя (`Battles`); Scorcher бьёт соседа только если он Bio (по тегу соседа, не основной цели), Splash — любого; урон считается по HP и защите соседа. Для неизвестных соседей: число (средний размер армии − 1) и их профили (известный состав, до шести по шагу) оцениваются отдельно.
5. **Одна формула для всех путей.** Ветка `snap == null` (линейная таблица, плоская цена добавленных способностей, ограничение ±1.5) удалена: Self-резерв считает ту же `EquipmentEfficiency.Utility` в явном reference-контексте (каталожный prior-противник, без миссии/армии, без мировых эффектов реестра) и переводит результат в «боевые тела» на своей границе (`WorldAnalysis.Self.ReserveBodies`: `Combat × persistence / 1.10 / equipReserveReferenceBodyC`, 1.23).

## 8. Полный каталог (62 продукта, локальный раннер)
Источник: `ResearchProductionCatalog.asset` (62 уникальных `cardKey`), каталоги трёх фракций и Neutral. Хосты — все Unit/Hero, прошедшие
`CanAttachPreview` (всего 89 хостов в каталогах); контексты — наземный состав каждой из трёх стартовых колод (Vessels / Ashen / Iron Concord) как
`TrueWorld.EnemyArmies`. Хост без аддитивных изменений (полное HP, нет миссии, нет армии). pairs=3939 nonfinite=0 пар «продукт × хост × колода»:
NaN/Infinity — 0, исключений — 0. Отрицательные значения — нормальный результат (потеря дальности/способности).

Колонка «0 / <0» — число пар с U≈0 и U<0 (по хостам × 3 колоды).

| Продукт | Слот | Допустимых хостов | U (мин … макс) | Лучший хост | Худший хост | 0 / <0 |
|---|---|---|---|---|---|---|
| Dermal Plating | Mut | 20 | -0.034 …   0.181 | AT Infantry | Heavy Infantry | 0 / 4 |
| Reactive Marrow | Mut | 20 | 0.000 …   0.242 | AT Infantry | Rad Brute | 1 / 0 |
| Reinforced Skeleton | Mut | 20 | -0.026 …   0.277 | AT Infantry | Heavy Infantry | 0 / 5 |
| Pain Suppression | Mut | 20 | -0.130 …   0.119 | AT Infantry | Ash Drifter | 0 / 44 |
| Regenerative Culture | Mut | 20 | 0.000 …   0.176 | Light Infantry | Rad Brute | 3 / 0 |
| Hyper-Regeneration | Mut | 20 | -0.130 …   0.125 | AT Infantry | Ash Drifter | 0 / 44 |
| Survivor Strain | Mut | 20 | -0.156 …   0.141 | AT Infantry | Heavy Infantry | 0 / 45 |
| Adrenal Surge | Mut | 20 | 0.020 …   0.259 | Light Infantry | Heavy Infantry | 0 / 0 |
| Metabolic Overdrive | Mut | 20 | -0.131 …   0.160 | Hooded | AT Infantry | 0 / 7 |
| Predator Reflexes | Mut | 20 | -0.232 …   0.197 | AT Infantry | Heavy Infantry | 0 / 44 |
| Neural Accelerator | Mut | 20 | 0.010 …   0.429 | AT Infantry | Hooded | 0 / 0 |
| Rapid Synapse | Mut | 20 | -0.030 …   0.255 | Heavy Infantry | AT Infantry | 3 / 1 |
| Hunter Glands | Mut | 20 | -0.175 …   0.110 | Heavy Infantry | AT Infantry | 6 / 13 |
| Enhanced Senses | Mut | 20 | -0.151 …   0.160 | AT Infantry | Hooded | 0 / 29 |
| Wanderer Strain | Mut | 20 | -0.051 …   0.240 | Heavy Infantry | AT Infantry | 0 / 1 |
| Chameleon Tissue | Mut | 20 | -0.160 …  -0.035 | Trapper | Hooded | 0 / 60 |
| Fortunate Genome | Mut | 16 | 0.150 …   0.150 | Dorian Kesh | Dorian Kesh | 0 / 0 |
| Ghost Genome | Mut | 16 | 0.000 …   0.041 | Dorian Kesh | Lira Sable | 6 / 0 |
| Hunter Genome | Mut | 16 | 0.000 …   0.080 | Dorian Kesh | Lira Sable | 6 / 0 |
| Reflex Genome | Mut | 16 | 0.000 …   0.225 | Dorian Kesh | Nadia Thorne | 3 / 0 |
| Flamer | Eq | 19 | -2.463 …   1.523 | Rad Brute | Ash Walker | 0 / 16 |
| AT Launcher | Eq | 19 | -0.684 …   2.197 | Rad Brute | Ash Walker | 0 / 3 |
| Claws | Eq | 19 | -2.415 …   0.465 | Rad Brute | Ash Walker | 0 / 41 |
| Heavy MG | Eq | 19 | -0.796 …   2.563 | Shard Wanderer | Ash Walker | 0 / 3 |
| Plasma Gun | Eq | 19 | 0.986 …   4.445 | Shard Wanderer | Ash Walker | 0 / 0 |
| Ballistic Shield | Eq | 19 | -0.034 …   0.181 | AT Infantry | Heavy Infantry | 0 / 4 |
| Ceramic Vest | Eq | 19 | -0.075 …   0.294 | AT Infantry | Heavy Infantry | 0 / 4 |
| Assault Rifle Kit | Eq | 19 | 0.150 …   1.417 | Ash Walker | Tech Scrapper | 0 / 0 |
| Marksman Rifle | Eq | 19 | -0.404 …   2.943 | Shard Wanderer | Ash Walker | 0 / 3 |
| Optical Scope | Eq | 19 | 0.243 …   2.550 | Flamer | Tech Scrapper | 0 / 0 |
| Mobility Harness | Eq | 19 | 0.080 …   0.160 | Hooded | Scrapper | 0 / 0 |
| Shotgun | Eq | 19 | -2.170 …   1.181 | Shard Wanderer | Ash Walker | 0 / 11 |
| Grenade Launcher | Eq | 19 | 0.000 …   3.011 | Rad Brute | Ash Walker | 3 / 0 |
| Shock Rifle | Eq | 19 | -1.500 …   1.353 | Rad Brute | Ash Walker | 0 / 6 |
| Incendiary Rifle | Eq | 19 | -1.507 …   2.576 | Rad Brute | Ash Walker | 11 / 4 |
| Rail Rifle | Eq | 19 | -1.425 …   1.474 | Rad Brute | Ash Walker | 3 / 3 |
| Twin SMG | Eq | 19 | -1.738 …   1.032 | Rad Brute | Ash Walker | 0 / 10 |
| Portable Mortar | Eq | 19 | 0.294 …   3.505 | Heavy Infantry | Tech Scrapper | 0 / 0 |
| Recoil Cannon | Eq | 19 | 0.373 …   3.765 | Shard Wanderer | Ash Walker | 0 / 0 |
| AA Launcher | Eq | 19 | 0.010 …   0.069 | Flamer | Scout | 0 / 0 |
| Armor Plate | Eq | 43 | 0.000 …   0.480 | Scrap Mortar | Medium Tank | 11 / 0 |
| Reinforced Chassis | Eq | 43 | -0.180 …   0.970 | Scrap Mortar | Leviathan | 0 / 82 |
| Servo Actuators | Eq | 43 | 0.051 …   0.295 | Ash Howitzer | Skimmer | 0 / 0 |
| Ceramic Plating | Eq | 43 | 0.000 …   1.002 | Scrap Mortar | Medium Tank | 16 / 0 |
| Reactive Armor | Eq | 43 | -0.202 …   0.738 | Scrap Mortar | Ash Howitzer | 0 / 85 |
| Nuclear Engine | Eq | 22 | 0.051 …   0.295 | Ash Howitzer | Skimmer | 0 / 0 |
| Turbocharger | Eq | 21 | -0.420 …   0.360 | Leviathan | Artillery Tank | 0 / 8 |
| Artillery Cannon | Eq | 15 | -6.955 …   1.821 | Heavy Tank | Ash Howitzer | 0 / 6 |
| AT VH Launcher | Eq | 15 | -9.548 …   1.135 | Heavy Tank | Ash Howitzer | 0 / 11 |
| Double Barrel | Eq | 15 | 0.769 …  11.201 | Artillery Tank | Scav Carrier | 0 / 0 |
| Plasma Cannon | Eq | 15 | -9.195 …   3.768 | RC Vehicle | Ash Howitzer | 0 / 6 |
| Autocannon | Eq | 21 | -9.698 …   2.378 | Grave Engine | Ash Howitzer | 0 / 18 |
| HE Cannon | Eq | 21 | -6.702 …   3.918 | Grave Engine | Ash Howitzer | 3 / 6 |
| Flame Projector | Eq | 21 | -12.527 …   1.222 | Grave Engine | Ash Howitzer | 3 / 50 |
| Rail Cannon | Eq | 21 | -9.548 …   4.039 | Bastion Frame | Ash Howitzer | 0 / 9 |
| Shock Projector | Eq | 21 | 0.000 …   0.652 | Scrap Mortar | RC Vehicle | 20 / 0 |
| AA Mount | Eq | 21 | 0.000 …   0.192 | Artillery Tank | Crawler | 3 / 0 |
| Dozer Blade | Eq | 21 | -12.214 …   0.309 | Bastion Frame | Ash Howitzer | 0 / 54 |
| Siege Ram | Eq | 21 | -12.224 …   0.589 | Grave Engine | Ash Howitzer | 0 / 54 |
| Spiked Ram | Eq | 21 | -12.259 …   0.309 | Bastion Frame | Ash Howitzer | 0 / 54 |
| Mortar Rack | Eq | 21 | -3.166 …   4.130 | Heavy Tank | Ash Howitzer | 0 / 9 |
| Assault Conversion Kit | Eq | 21 | -12.437 …   0.014 | Grave Engine | Ash Howitzer | 3 / 54 |

Наблюдения (после учёта дальности ответа цели): порядки величин совпадают с референсом Приложения В: Plasma Gun 1.01…4.41 (референс 2.03…5.11), Dozer Blade −12.1…0.31 (−10.1…0.35), Mobility Harness 0.080…0.160 — точно. Оборонительные предметы вернулись к референсу: Ceramic Plating макс 0.52 (0.47), Armor Plate 0.24 (0.23), Reactive Armor 0.32 (0.28), Ballistic Shield −0.02…0.17 (−0.03…0.10). Hero-карты (Genome) оцениваются только на героях. AA Launcher/Mount положительны, потому что в составе колод есть воздушные цели; без них было бы 0.
