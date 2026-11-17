# Справочник игры — заголовки на валидацию

Основание: `master` / `421f05e`, 17.11.2026. Всего разделов: 11, тем: 86.

Ниже полное оглавление. Русские названия способностей — рабочие переводы, рядом сохранены реальные теги. По разрешению владельца все статьи наполнены английскими инструкциями, сверенными с текущим кодом.

В игре английские заголовки и 86 статей (440 блоков текста и подзаголовков). Каталог содержит постоянные ID и ключи локализации. Изображения добавляются позже. Экспорт всех текстов: `Docs/GameHelp_English_Content.md`. Источники и уточнения: `Docs/GameHelp_Content_Sources.md`.

## Основы игры

Источники: Assets/Scripts/Setup; Assets/Scripts/Core/GameSession.cs; Assets/Scripts/Turns/GameTurnController.cs

| ID | Заголовок на утверждение | Текущий английский заголовок |
|---|---|---|
| `goal` | Цель матча, победа и поражение | Match objective, victory and defeat |
| `controls` | Управление картой и интерфейс | Map controls and interface |
| `factions` | Фракции и цитадели | Factions and citadels |
| `setup` | Начало матча и условия карты | Starting a match and map conditions |
| `rounds` | Раунды и ходы игроков | Rounds and player turns |
| `initiative` | Инициатива и порядок ходов | Initiative and turn order |
| `initiative-tokens` | Покупка и бросок жетонов инициативы | Buying and rolling initiative tokens |
| `action-points` | Очки действий (AP) и стоимость действий | Action points and action costs |
| `turn-start-end` | Начало и завершение хода | Start and end of a turn |

## Карты и колоды

Источники: Assets/Scripts/Cards; Assets/Scripts/UI/Cards; Assets/Scripts/Progression

| ID | Заголовок на утверждение | Текущий английский заголовок |
|---|---|---|
| `card-types` | Типы карт и теги юнитов | Card types and unit tags |
| `card-stats` | Характеристики карт и значки способностей | Card stats and ability icons |
| `deck` | Состав колоды и ограничения | Deck composition and limits |
| `hand` | Размер руки, добор и использование карт | Hand size, drawing and using cards |
| `play-card` | Розыгрыш карт и условия выставления | Playing cards and deployment requirements |
| `tactics` | Карты тактики и модификаторы проверок | Tactic cards and challenge modifiers |
| `equipment` | Экипировка и совместимость улучшений | Equipment and attachment compatibility |
| `mutators` | Мутагены и биологические улучшения | Mutators and biological upgrades |

## Ресурсы и инфраструктура

Источники: Assets/Scripts/Economy; Assets/Scripts/Map/InfrastructureActions.cs; Assets/Scripts/Map/BuildingRegistry.cs

| ID | Заголовок на утверждение | Текущий английский заголовок |
|---|---|---|
| `resources` | Ресурсы Human, Energy, Materials и Tech | Human, Energy, Materials and Tech |
| `income` | Доход и ресурсный потенциал гексов | Income and resource yields |
| `trade` | Торговля и обменный курс | Trade and the exchange rate |
| `bases` | Основание баз и расширение | Founding bases and expansion |
| `base-upgrades` | Улучшение баз и слоты построек | Base upgrades and facility slots |
| `facilities` | Постройки и требования к строительству | Facilities and construction requirements |
| `extractors` | Добывающие здания и их улучшение | Extraction sites and their upgrades |
| `garrisons` | Гарнизоны и оборона базы | Garrisons and base defence |
| `building-damage` | Урон, ремонт и разрушение зданий | Building damage, repair and destruction |
| `research` | Исследования и исследователи | Research and researchers |
| `production` | Производство и сборщики | Production and assemblers |
| `production-check` | Проверки исследований и производства | Research and production challenges |

## Армии и передвижение

Источники: Assets/Scripts/Map/ArmyData.cs; Assets/Scripts/Map/ArmyActions.cs; Assets/Scripts/Map/MovementRules.cs; Assets/Scripts/Units/UnitRepair.cs

| ID | Заголовок на утверждение | Текущий английский заголовок |
|---|---|---|
| `army-composition` | Состав армии, лидер и вместимость | Army composition, leader and capacity |
| `hero` | Герои и армии без героя | Heroes and armies without a hero |
| `army-creation` | Создание и переименование армий | Creating and renaming armies |
| `army-activation` | Активация армии и затраты AP | Army activation and AP costs |
| `movement` | Очки движения (MP) и стоимость маршрута | Movement points and route costs |
| `terrain` | Террейн, проходимость и защита | Terrain, passability and defence |
| `transfer` | Перевод, обмен и разделение юнитов | Transferring, exchanging and splitting units |
| `transport` | Перевозка пехоты | Infantry transport |
| `unit-repair` | Лечение, ремонт и регенерация юнитов | Unit healing, repair and regeneration |

## Разведка и погода

Источники: Assets/Scripts/Map/VisionSystem.cs; Assets/Scripts/Map/HumanVisualMemory.cs; Assets/Scripts/Map/StealthSystem.cs; Assets/Scripts/Map/SandstormRules.cs; Assets/Scripts/Core/TacticalIntelligence.cs

| ID | Заголовок на утверждение | Текущий английский заголовок |
|---|---|---|
| `fog` | Видимость и туман войны | Vision and fog of war |
| `memory` | Последние известные позиции противника | Last observed enemy positions |
| `scouting` | Дальность разведки и обнаружение | Reconnaissance range and detection |
| `stealth` | Скрытность и проверки обнаружения | Stealth and detection challenges |
| `intelligence` | Тактическая разведка о противниках | Tactical intelligence |
| `sandstorms` | Жизненный цикл и движение песчаных бурь | Sandstorm lifecycle and movement |
| `storm-effects` | Песчаная буря: движение, дальность и защита | Sandstorms: movement, range and defence |

## Бои и проверки

Источники: Assets/Scripts/Combat; Assets/Scripts/UI/BattleScreen

| ID | Заголовок на утверждение | Текущий английский заголовок |
|---|---|---|
| `contact` | Контакт с противником и начало боя | Enemy contact and starting a battle |
| `battle-delay` | Немедленные и отложенные бои | Immediate and delayed battles |
| `deployment` | Расстановка и построение перед боем | Battle deployment and formation |
| `battle-rounds` | Раунды боя, инициатива и ходы юнитов | Battle rounds, initiative and unit turns |
| `battle-movement` | Перемещение в бою и дальность атаки | Battle movement and attack range |
| `challenges` | Жетоны проверок, успехи и ничьи | Challenge tokens, successes and ties |
| `fate` | Очки судьбы, перебросы и поединок судьбы | Fate points, rerolls and the Fate duel |
| `damage` | Атака, защита, урон и здоровье | Attack, defence, damage and hit points |
| `building-defence` | Защита зданий и Disruption | Building defence and Disruption |
| `reactions` | Прикрытие союзников и контратаки | Bodyguard and counterattacks |
| `explosions` | Взрывы и одноразовые атаки | Explosions and single-use attacks |
| `retreat` | Отступление и преследование | Retreat and pursuit |
| `capture-kill` | Захват или убийство одиночного героя | Capturing or killing a lone hero |
| `prisoners` | Пленные, армии-тюрьмы и освобождение | Prisoners, prison armies and release |
| `battle-outcome` | Итоги боя и уничтожение армий | Battle outcome and destroyed armies |

## Авиация и ПВО

Источники: Assets/Scripts/Aviation

| ID | Заголовок на утверждение | Текущий английский заголовок |
|---|---|---|
| `airfields` | Аэродромы и вместимость авиации | Airfields and aircraft capacity |
| `carrier` | Мобильные носители и перевод авиации | Mobile carriers and aircraft transfers |
| `air-armies` | Воздушные армии и вылеты | Air armies and sorties |
| `flight-route` | Маршрут полёта, дальность и возвращение | Flight routes, range and return |
| `air-strikes` | Авиаудары и выбор целей | Air strikes and target selection |
| `anti-air` | Дальность ПВО и ответный огонь | Air defence range and reactions |
| `air-recovery` | Потери и восстановление авиации | Aircraft losses and recovery |

## События и нейтралы

Источники: Assets/Cards/EventCatalog.asset; Assets/Scripts/Map/HexSelectionController.Events.cs; Assets/Scripts/Turns/GameTurnController.cs

| ID | Заголовок на утверждение | Текущий английский заголовок |
|---|---|---|
| `neutral` | Нейтральные армии и их ход | Neutral armies and their turn |
| `event-exploration` | Исследование событий на карте и их охрана | Exploring event hexes and their guards |
| `rewards` | Награды событий: ресурсы и карты | Event rewards: resources and cards |
| `library` | Библиотека: карта в коллекцию | Library: a card for the collection |
| `command-post` | Заброшенный командный пункт: восстановление базы | Abandoned command post: restoring a base |

## Кампания и коллекция

Источники: Assets/Scripts/Campaign; Assets/Scripts/Progression; Assets/Scripts/UI/Collection

| ID | Заголовок на утверждение | Текущий английский заголовок |
|---|---|---|
| `campaign` | Карта кампании и владение регионами | Campaign map and region ownership |
| `region-attack` | Атака региона и выбор колоды | Attacking regions and choosing a deck |
| `region-conditions` | Террейн, ресурсы и погода региона | Region terrain, resources and weather |
| `campaign-result` | Результаты матча и захват региона | Campaign results and region capture |
| `collection` | Коллекция и экземпляры карт | Collection and card copies |
| `deck-editor` | Создание и редактирование колод | Creating and editing decks |
| `match-rewards` | Награды за матч и пополнение коллекции | Match rewards and collection progression |

## Меню и сохранения

Источники: Assets/Scripts/Saves; Assets/Scripts/UI/Panels; Assets/Scripts/UI/Tutorial; Assets/Scripts/UI/ArmyRadio; Assets/Scripts/Turns/GameTurnController.cs

| ID | Заголовок на утверждение | Текущий английский заголовок |
|---|---|---|
| `save-load` | Сохранение, загрузка и управление слотами | Saving, loading and slot management |
| `save-limits` | Когда можно сохранять матч | When a match can be saved |
| `spectator` | Наблюдение за матчем ИИ: пауза и быстрый расчёт | AI match spectator: pause and fast resolve |
| `settings` | Настройки звука и обучающие подсказки | Sound settings and tutorial hints |
| `radio` | Радиосообщения армий | Army radio reports |
| `exit` | Выход из матча и возвращение в кампанию | Leaving a match and returning to the campaign |

## Справочник способностей

Слева одна кнопка **Abilities**. Все 46 способностей размещены справа в одной статье с подзаголовками и общей прокруткой.

| ID | Заголовок на утверждение | Текущий английский заголовок |
|---|---|---|
| `all-abilities` | Все способности | Abilities |

Подзаголовки внутри общей статьи (не отдельные кнопки):

| Способность | Название для проверки | Сохранённая идентичность |
|---|---|---|
| Critical Damage | CriticalDamage — Критический урон | `ability-criticaldamage` |
| Ceramic Armor | CeramicArmor — Керамическая броня | `ability-ceramicarmor` |
| Berserk | Berserk — Берсерк | `ability-berserk` |
| Rapid Reaction | RapidReaction — Быстрая реакция | `ability-rapidreaction` |
| Shock Attack | ShockAttack — Шоковая атака | `ability-shockattack` |
| Hyperkinetic | Hyperkinetic — Гиперкинетика | `ability-hyperkinetic` |
| Pyrokinetic | Pyrokinetic — Пирокинетика | `ability-pyrokinetic` |
| Splash | Splash — Сопутствующий урон | `ability-splash` |
| Scorcher | Scorcher — Выжигатель | `ability-scorcher` |
| Raise The Rots | RaiseTheRots — Поднятие Rots | `ability-raisetherots` |
| Heal | Heal — Лечение | `ability-heal` |
| Transport | Transport — Транспорт | `ability-transport` |
| Repair | Repair — Ремонт | `ability-repair` |
| Disruption | Disruption — Подавление защиты | `ability-disruption` |
| Airfield | Airfield — Аэродром | `ability-airfield` |
| Single Use | SingleUse — Одноразовое применение | `ability-singleuse` |
| Explosion 3 | Explosion3 — Взрыв 3 | `ability-explosion3` |
| Explosion 5 | Explosion5 — Взрыв 5 | `ability-explosion5` |
| Explosion 10 | Explosion10 — Взрыв 10 | `ability-explosion10` |
| Ionic | Ionic — Ионный урон | `ability-ionic` |
| Bodyguard | Bodyguard — Телохранитель | `ability-bodyguard` |
| Counterattack | Counterattack — Контратака | `ability-counterattack` |
| Drill | Drill — Подготовка бойцов | `ability-drill` |
| r1s0 | r1s0 — Разведка r1s0 | `ability-r1s0` |
| r1s4 | r1s4 — Разведка r1s4 | `ability-r1s4` |
| r1s5 | r1s5 — Разведка r1s5 | `ability-r1s5` |
| r1s6 | r1s6 — Разведка r1s6 | `ability-r1s6` |
| Stealth 4 | Stealth4 — Скрытность 4 | `ability-stealth4` |
| AA | AA — Противовоздушная оборона | `ability-aa` |
| Ap Bonus | ApBonus — Бонус очков действий | `ability-apbonus` |
| Trade | Trade — Торговля | `ability-trade` |
| Tactical Intelligence | TacticalIntelligence — Тактическая разведка | `ability-tacticalintelligence` |
| Barracks | Barracks — Казармы | `ability-barracks` |
| Research | Research — Исследования | `ability-research` |
| Researcher | Researcher — Исследователь | `ability-researcher` |
| Production | Production — Производство | `ability-production` |
| Assembler | Assembler — Сборщик | `ability-assembler` |
| Collect Human | CollectHuman — Добыча Human | `ability-collecthuman` |
| Collect Energy | CollectEnergy — Добыча Energy | `ability-collectenergy` |
| Collect Materials | CollectMaterials — Добыча Materials | `ability-collectmaterials` |
| Collect Tech | CollectTech — Добыча Tech | `ability-collecttech` |
| Regeneration | Regeneration — Регенерация | `ability-regeneration` |
| Produce Human | ProduceHuman — Производство Human | `ability-producehuman` |
| Produce Energy | ProduceEnergy — Производство Energy | `ability-produceenergy` |
| Produce Materials | ProduceMaterials — Производство Materials | `ability-producematerials` |
| Produce Tech | ProduceTech — Производство Tech | `ability-producetech` |

## Границы покрытия

Отдельные названия карт, картинки, разновидности событий и комбинации террейна не превращены в отдельные статьи: это данные соответствующих каталогов. Их общие механики входят в оглавление. Все теги из `UnitAbilities.All` включены подзаголовками общей статьи Abilities, включая параметры разведки и добывающие способности. Их наличие в коде не означает наличие носителя в стартовых колодах; это будет отмечено в статьях.

Числовые правила, стоимости и ограничения не берутся из старых обсуждений. При наполнении каждой статьи они сверяются с актуальными правилами и конфигурацией. Новые или изменённые правила не вводятся этим окном.

## Подключение переводов и контента

`Assets/Config/GameHelpCatalog.asset` — единый источник порядка и заголовков. ID и ключи не переименовываются при переводе. Для подзаголовка добавляется блок `Heading`, для текста — `Text`, для изображения — `Image` со Sprite и необязательной локализованной подписью. Ключи новых блоков: `help.topic.<id>.block.<block-id>.text`. У перенесённых способностей сохранены прежние ключи `help.topic.ability-...`, чтобы группировка не сбросила будущие переводы.

`GameHelpPanelUI.SetTextResolver(Func<string, string>)` принимает поиск перевода по ключу; отсутствие перевода возвращает английский fallback. Сервис локализации вызывает этот метод при смене языка. Он же должен выбрать шрифт с нужными глифами через обычный механизм TextMeshPro. Выбор языка и перевод всего интерфейса — последующая задача.
