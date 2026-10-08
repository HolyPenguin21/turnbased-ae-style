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
`C = 3.66 · Σ P(alive)·урон/HP цели`, усреднение по профилям. Профили берутся с весом копий из `EquipmentTargets` (состав, без скрытых
координат); без состава — каталожный prior A3/D2/HP4/I1. Авиация исключается из наземного C.

Калибровка масштаба: на якорях Iron Concord отношение контрольного C к «сырой» модели 3.48 / 3.68 / 3.83 (Light/Medium/Heavy), среднее
3.66 → `equipCombatBodyScale`. 1.10 = `equipCombatCardScale` по ТЗ.

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
| Assault Rifle | 0.480→0.430 | 0.596→0.537 | 0.671→0.590 | | | |
| Heavy MG | 1.635→1.399 | 1.233→1.086 | 0.671→0.590 | | | |
| Ballistic Shield | 0.041→0.065 | 0.054→0.074 | 0.021→0.042 | | | |
| Mobility Harness | 0.096→0.096 | 0.112→0.113 | 0.129→0.129 | | | |
| Neural Accelerator | 0.284→0.311 | 0.297→0.323 | 0.173→0.220 | | | |
| Regenerative Culture | 0.030→0.057 | 0.034→0.062 | 0.038→0.071 | | | |
| Ceramic Vest | | | | 0.142→0.216 | 0.062→0.069 | 0.193→0.301 |
| Twin SMG | | | | 0.145→0.080 | 0.367→0.072 | −0.515→−0.389 |
| Optical Scope | | | | 0.561→0.476 | 1.888→1.987 | 1.138→0.914 |
| Reactive Marrow | | | | 0.114→0.150 | 0.059→0.054 | 0.159→0.231 |
| Survivor Strain | | | | 0.002→0.073 | −0.059→−0.044 | 0.021→0.145 |

Hunter Glands на Scout → 0.000 (как в ТЗ). Отклонения объяснены правилами, не подгонкой: нет учёта дальности ответа цели (в `DefenderProfile`
нет Range), вторичный сосед считается копией основной цели, Regeneration по среднему полю даёт ~2× к контрольной. Цены/Chance/S-колонки
референса здесь не воспроизводились (общая арифметика S не менялась и покрыта существующими тестами цепочек).

## 5. Производительность
Микробенч (mono, 16 профилей, 1000 различных состояний «после»): ~0.30 с до оптимизации → ~0.15 с после выноса независимых от контакта
вызовов; состояние «до» кешируется `ConditionalWeakTable` по экземпляру списка целей (жизнь = жизнь снимка/цели). Снимков Unity/IL2CPP нет.

## 6. Проверки
* Локальный раннер (`D:\aiv-work`, mono Unity): базовая ревизия 1312 passed → 1328 passed; обычный набор регрессий нет, кроме двух намеренно удалённых тестов Supply.
* С патчем Unity-null (`patchrun.sh`): 1480 → 1496 passed, регрессий нет (кроме тех же двух удалённых).
* Обновлены тесты, фиксировавшие старую таблицу: «нет противника» теперь даёт значение против каталожного prior; Regeneration зависит от
  летальности противника; Pyrokinetic без цели усредняет известные составы.
* Новые тесты: `AiEquipmentUtilityTests` (17), `OwnUtility_DoesNotDependOnDeckPlusHandSize`.

## 7. Не сделано / ограничения
* **Unity не запускалась**: ни EditMode, ни сценарии §14.4 (подготовка по ходам, Challenge-проигрыш, потеря площадки, rebuild Phase A…). Шаги: открыть проект в 6000.5.4f1, Test Runner → EditMode → весь набор; затем вручную оба четырёхходовых маршрута.
* Полный проход по 62 продуктам с таблицей по каждому хосту и три стартовые колоды в Unity не выполнялись (ассеты не загружаются вне редактора); выполнен только выборочный проход на 14 продуктах × 6 хостах Iron Concord (§4).
* Fate оператора: механизм `OperatorOutputs` есть, но владелец не заполняет его подтверждёнными выпусками — до этого значение 0 (старый флэт удалён по ТЗ).
* Fate командира: оставлен прежний адаптер, ΔWinChance из `WorthIt` не подключён.
* Авиационные хосты — прокси линейной таблицей; `AviationCombatEstimator` для статов не подключён.
* Известный маршрут (`RouteLength/ArmyActivationAp`) и `DetectionRelevance` поддержаны контекстом, но вызывающий код их пока не заполняет (используются прокси / 0).
* `EquipmentReserve` в `WorldAnalysis.Self` (без снапшота) оставлен на линейной оценке AiPower: это резервная мера силы в собственных единицах потребителя, без знаний о противнике.
* Тест-прогон не воспроизводит Приложение А по ценам/Chance/S и не содержит сравнения с наймом (S-таблицы).
