# Именные колоды ИИ — 22 октября 2026

База: `502f14f277a90a9cdd8c2069ccc2074369b483ac` (`master`).
Масштаб: изменение состава колод и поведения настройки/старта матча. Существующие оси ИИ и правила оплаты производства сохранены.

## Правила

- 24 колоды, по одной на каждого из восьми героев трёх фракций. Лидер связан с колодой через неизменяемый `leaderCardKey`.
- Основные карты занимают 98–100 очков. Эквип и мутагены не занимают очки колоды ИИ; стоимость их производства в матче сохраняется.
- В каждой колоде 3 дешёвых наземных скаута, 1 дополнительный наземный разведчик, 1 база, Laboratory, Factory и Intel Center. Есть Researcher, Assembler и полевой командир с Command не ниже 6.
- Полный разрешённый набор чертежей: 48 эквипов и 20 мутагенов в каждой колоде; фракционные ограничения и совместимость с носителями остаются обязательными.
- В быстрой/новой игре первая опция выбора ИИ — Random. Конкретная колода выбирается из фракции игрока; случайная фракция разрешается до случайной колоды. Герой разыгрывается обычным способом.
- В кампании колода выбирается воспроизводимо по seed операции. При повторном запуске сохраняется выбранное имя; автобои и тестовый авторезолв используют тот же выбор.
- После размещения цитадели ИИ в кампании лидер бесплатно появляется в гарнизоне. Его единственная карта исключается до стартовой раздачи, включая прогноз потребностей до создания руки.
- Приветствие использует портрет лидера выбранной колоды. Тексты остаются фракционными.
- Специализация задаётся составом доступных карт; текущий ИИ продолжает выбирать миссии, командиров и производство по существующим оценкам полезности.

## Повторная проверка от исполнения к источникам

Найдены и исправлены три расхождения:

1. `GameSession.TryPrepareMatch` выбирал случайную колоду до проверки человеческой колоды. При отказе старта `Random` превращался в фиксированный выбор. Теперь выбор ИИ происходит после успешной проверки и сохранения выбора человека; неуспешная попытка не расходует случайный выбор колоды.
2. `WorldAnalysis.BuildDevelopment` запрашивал мутаторные цели, штатные и потенциальные результаты по фракции, обходя ограничения чертежей выбранной колоды. `StructuralPreparationBlock` также считал режим доступным по полному каталогу. Все эти чтения используют существующий `ResearchProductionSystem.OfferedCards` с конкретным игроком, как генерация и исполнение. Это предотвращает прогноз запрещённых улучшений и подготовку полностью закрытого режима.
3. `SpawnNeutralArmy` уведомлял наблюдателей регистрацией пустой армии до добавления бойцов. Лидер кампании уже может видеть соседний хекс, поэтому память ИИ могла сохранить пустой состав без следующего уведомления. Армия теперь регистрируется после заполнения; несозданные пустые армии не попадают в реестр.

Проверены цепочки: выбор в меню → подготовка матча → лидер/гарнизон → создание руки → прогноз потребностей до первого добора; seed и сохранённое имя операции → ручной бой/автобой/возобновление; выбранная колода → разрешённые чертежи → прогноз/генерация → проверка и оплата попытки; появление юнитов → наблюдение → память ИИ.

Бесплатный лидер создаётся до начала ходов и использования банка; выдача не открывает резервов и не списывает AP/ресурсы. Стоимость дальнейшего производства, обычного развёртывания и движения остаётся на стандартных платёжных границах. Новые снимки мира строятся после очистки состояний предыдущего матча и используют уже выбранную колоду и её ограничения чертежей.

После исправлений повторно прошли 443 существующих и 9 новых managed-проверок, регрессий относительно исходной ревизии нет. Полная проверка исходников со старыми Unity API-заглушками не добавила ошибок относительно сохранённого baseline. Добавлены отдельные Unity-регрессии для отказа запуска с Random/явной колодой, ограниченного прогноза производства и первого наблюдения полного нейтрального состава; здесь они не исполнялись из-за отсутствия Unity.

## Колоды

| Фракция | Лидер | Основные карты | Очки | Специализация |
|---|---|---:|---:|---|
| Concord | Aldric Voss | 33 | 100 | Авианосцы и авиация, умеренная тяжёлая поддержка |
| Concord | Kessa Vrail | 38 | 100 | Тяжёлая Bio-пехота и лечение |
| Concord | Vector Vance | 40 | 98 | Дешёвая Bio-пехота под мутагены |
| Concord | Dorian Kesh | 32 | 100 | Танковая колонна |
| Concord | Bolt Jax | 32 | 100 | Эквип и тяжёлые мехи |
| Concord | Nadia Thorne | 40 | 99 | Мобильные войска быстрого реагирования |
| Concord | Lira Sable | 38 | 100 | Разведка, скрытность и засады |
| Concord | Tessa Rourke | 31 | 100 | Экономика и осадная поддержка |
| Ashen | Miller Hayes | 33 | 100 | Материалы, бронетехника и артиллерия |
| Ashen | Hank Mercer | 34 | 99 | Штурмовая пехота с Drill |
| Ashen | Rusty Miller | 32 | 100 | Эквип и тяжёлые машины |
| Ashen | Nora Blackwell | 38 | 99 | Скрытные рейды |
| Ashen | Elena Hayes | 33 | 100 | Смешанное бронетанковое наступление |
| Ashen | Vera Sterling | 37 | 100 | Мобильная механизированная группа |
| Ashen | Nova Sterling | 38 | 99 | Bio-пехота под мутагены |
| Ashen | Iri Vane | 30 | 100 | Промышленность и тяжёлая осада |
| Vessels | Mara the Archivist | 34 | 100 | Энергетическое вооружение и авиация |
| Vessels | Edda the Conductor | 34 | 99 | Технологии и дальний точный огонь |
| Vessels | Thren the Scholar | 38 | 98 | Механическая стрелковая пехота и эквип |
| Vessels | Vale the Machinist | 30 | 100 | Тяжёлые мехи под эквип |
| Vessels | Orr the Keeper | 36 | 98 | Пехотная линия с Drill |
| Vessels | Ilex the Surveyor | 38 | 99 | Разведка и мобильная огневая поддержка |
| Vessels | Jol the Warden | 35 | 99 | Мобильная бронетанковая группа |
| Vessels | Nyx the Cartographer | 32 | 99 | Защищённая осадная группа |

## Проверки и ограничения

- 443 существующих managed-теста прошли на исходной ревизии и на изменениях, регрессий нет. Выборка включает резервирование ресурсов, ключи кэша боевых улучшений, производство и применение улучшений.
- 9 новых managed-тестов прошли: валидность всех 24 наборов, бюджет основных карт, тематические опорные юниты, исключение лидера из пула, воспроизводимость выбора, стабильное чтение колоды и ограничения чертежей в автобое.
- Дифференциальная проверка всех исходников со старыми Unity-заглушками: 74 одинаковые уникальные ошибки API в baseline и текущем состоянии (141 сообщение); новых ошибок нет. Это не подтверждение сборки в Unity 6000.5.4f1.
- Оба валидатора Unity YAML прошли для StartingDeckCatalog.asset. Игровые стартеры игроков сохранены без изменений.
- 7 тестов AiCombatCacheLifecycleTests не исполняются в этой среде: одинаковая ошибка UnityEngine.Object в SetUp на baseline и изменениях. Чтение/запись памяти и уведомления требуют повторной проверки в Unity.
- Добавлены EditMode-проверки выпадающего списка, бесплатного лидера и отсутствия повторного экземпляра, сохранения колоды при перезапуске боя и точного портрета в приветствии. Они требуют редактора Unity и здесь не запускались.
- Изображения юнитов недоступны для просмотра через текущий GitHub-коннектор. Составы подобраны по актуальным характеристикам, цене и способностям; визуальная сверка остаётся открытой.
- Все карты Вессел, включая героев, имеют Mechanical-природу. Текущие мутагены требуют Bio; поэтому в колоде Thren нет совместимых целей для Researcher. Его набор делает упор на стрелковую пехоту и эквип. Доступ к мутагенам сохранён; навыки, типы носителей и баланс карт не изменены.

## Основные карты каждой колоды

### Aldric Voss

Авианосцы и авиация, умеренная тяжёлая поддержка. 100 очков, 33 карт.

Aldric Voss ×1; Vector Vance ×1; Bolt Jax ×1; Tessa Rourke ×1; Ark Carrier ×2; Mosquito ×4; Rusty Vulture ×3; Storm Petrel ×2; Skydiver ×1; Medium Tank ×2; Crawler ×1; Heavy Tank ×1; Mule ×1; Light Infantry ×4; Scout ×3; RC Vehicle ×1; Concord Base ×1; Laboratory ×1; Factory ×1; Intel Center ×1.

### Kessa Vrail

Тяжёлая Bio-пехота и лечение. 100 очков, 38 карт.

Kessa Vrail ×1; Vector Vance ×1; Bolt Jax ×1; Tessa Rourke ×1; Nadia Thorne ×1; Heavy Infantry ×4; Trench Guardian ×4; Siege Grenadier ×3; AT Infantry ×3; Medium infantry ×3; Dust Medic ×1; Barracuda ×2; Crawler ×1; Light Infantry ×4; Scout ×3; RC Vehicle ×1; Concord Base ×1; Laboratory ×1; Factory ×1; Intel Center ×1.

### Vector Vance

Дешёвая Bio-пехота под мутагены. 98 очков, 40 карт.

Vector Vance ×1; Bolt Jax ×1; Kessa Vrail ×1; Nadia Thorne ×1; Tessa Rourke ×1; Light Infantry ×4; Medium infantry ×4; Hazard Pioneer ×4; AT Infantry ×3; Coil Marksman ×3; Flamer ×2; Heavy Infantry ×3; Sand Jackal ×2; Crawler ×1; Dust Medic ×1; Scout ×3; RC Vehicle ×1; Concord Base ×1; Laboratory ×1; Factory ×1; Intel Center ×1.

### Dorian Kesh

Танковая колонна. 100 очков, 32 карт.

Dorian Kesh ×1; Vector Vance ×1; Bolt Jax ×1; Lira Sable ×1; Light Tank ×4; Medium Tank ×4; Heavy Tank ×3; Needle Rail Tank ×2; Boatswain ×1; Roadwarden AA ×1; Mule ×1; Light Infantry ×4; Scout ×3; RC Vehicle ×1; Concord Base ×1; Laboratory ×1; Factory ×1; Intel Center ×1.

### Bolt Jax

Эквип и тяжёлые мехи. 100 очков, 32 карт.

Bolt Jax ×1; Vector Vance ×1; Aldric Voss ×1; Tessa Rourke ×1; Light Infantry ×4; Medium infantry ×3; Atlas ×3; Claimjumper ×3; Mantis ×3; Duelist ×1; Arsenal ×1; Crawler ×1; Mule ×1; Scout ×3; RC Vehicle ×1; Concord Base ×1; Laboratory ×1; Factory ×1; Intel Center ×1.

### Nadia Thorne

Мобильные войска быстрого реагирования. 99 очков, 40 карт.

Nadia Thorne ×1; Vector Vance ×1; Bolt Jax ×1; Tessa Rourke ×1; Lira Sable ×1; Jackal ×4; Grapeshot ×4; Medium infantry ×4; Light Tank ×4; Barracuda ×3; Roadwarden AA ×4; Mosquito ×2; Light Infantry ×2; Scout ×3; RC Vehicle ×1; Concord Base ×1; Laboratory ×1; Factory ×1; Intel Center ×1.

### Lira Sable

Разведка, скрытность и засады. 100 очков, 38 карт.

Lira Sable ×1; Vector Vance ×1; Bolt Jax ×1; Nadia Thorne ×1; Tessa Rourke ×1; Jackal ×4; Sandnought ×2; Medium infantry ×4; Coil Marksman ×4; AT Infantry ×3; Rusty Vulture ×3; Crawler ×1; Light Infantry ×4; Scout ×3; RC Vehicle ×1; Concord Base ×1; Laboratory ×1; Factory ×1; Intel Center ×1.

### Tessa Rourke

Экономика и осадная поддержка. 100 очков, 31 карт.

Tessa Rourke ×1; Vector Vance ×1; Bolt Jax ×1; Dorian Kesh ×1; Light Infantry ×3; Medium infantry ×2; Artillery Cannon ×2; Sand Organ ×3; Artillery Tank ×2; Pulse Cannon ×1; Heavy Tank ×3; Breakwater Crab ×1; Crawler ×1; Mule ×1; Scout ×3; RC Vehicle ×1; Concord Base ×1; Laboratory ×1; Factory ×1; Intel Center ×1.

### Miller Hayes

Материалы, бронетехника и артиллерия. 100 очков, 33 карт.

Miller Hayes ×1; Nova Sterling ×1; Rusty Miller ×1; Iri Vane ×1; Ash Runner ×3; Iron Husk ×3; Rust Tank ×4; Iron Maw ×3; Scrap Mortar ×3; Slag Lobber ×3; Rad Hunter ×1; Tow Rig ×1; Hooded ×3; Dust Runner ×1; Ashen Base ×1; Laboratory ×1; Factory ×1; Intel Center ×1.

### Hank Mercer

Штурмовая пехота с Drill. 99 очков, 34 карт.

Hank Mercer ×1; Nova Sterling ×1; Rusty Miller ×1; Iri Vane ×1; Ash Runner ×4; Iron Husk ×4; Scab Shield ×2; Rad Brute ×3; Furnace Bearer ×3; Rad Leaper ×3; Roadbreaker ×2; Rad Hunter ×1; Hooded ×3; Dust Runner ×1; Ashen Base ×1; Laboratory ×1; Factory ×1; Intel Center ×1.

### Rusty Miller

Эквип и тяжёлые машины. 100 очков, 32 карт.

Rusty Miller ×1; Nova Sterling ×1; Miller Hayes ×1; Iri Vane ×1; Ash Runner ×3; Bone Picker ×3; Ash Walker ×3; Scrap Locust ×3; Colossus ×3; Rivetstorm ×2; Scrap Mountain ×1; Rad Hunter ×1; Tow Rig ×1; Hooded ×3; Dust Runner ×1; Ashen Base ×1; Laboratory ×1; Factory ×1; Intel Center ×1.

### Nora Blackwell

Скрытные рейды. 99 очков, 38 карт.

Nora Blackwell ×1; Nova Sterling ×1; Rusty Miller ×1; Iri Vane ×1; Vera Sterling ×1; Ash Runner ×4; Cinder Stalker ×4; Deadeye ×4; Rad Leaper ×3; Rattletrap ×4; Flywheel ×3; Carrion ×2; Rad Hunter ×1; Hooded ×3; Dust Runner ×1; Ashen Base ×1; Laboratory ×1; Factory ×1; Intel Center ×1.

### Elena Hayes

Смешанное бронетанковое наступление. 100 очков, 33 карт.

Elena Hayes ×1; Nova Sterling ×1; Rusty Miller ×1; Iri Vane ×1; Ash Runner ×4; Iron Husk ×3; Rust Tank ×3; Boomstick ×3; Iron Maw ×3; Roadbreaker ×2; Rad Hunter ×1; Scrap Mortar ×1; Tow Rig ×1; Hooded ×3; Dust Runner ×1; Ashen Base ×1; Laboratory ×1; Factory ×1; Intel Center ×1.

### Vera Sterling

Мобильная механизированная группа. 100 очков, 37 карт.

Vera Sterling ×1; Nova Sterling ×1; Rusty Miller ×1; Iri Vane ×1; Nora Blackwell ×1; Ash Runner ×4; Iron Husk ×4; Rattletrap ×4; Flywheel ×3; Roadbreaker ×3; Scrapwing ×3; Rad Hunter ×1; Rad Leaper ×2; Hooded ×3; Dust Runner ×1; Ashen Base ×1; Laboratory ×1; Factory ×1; Intel Center ×1.

### Nova Sterling

Bio-пехота под мутагены. 99 очков, 38 карт.

Nova Sterling ×1; Rusty Miller ×1; Hank Mercer ×1; Iri Vane ×1; Ash Runner ×4; Bone Picker ×4; Iron Husk ×4; Rad Leaper ×3; Rad Brute ×3; Spore Drifter ×2; Scrap Shaman ×2; Scab Shield ×2; Roadbreaker ×1; Rad Hunter ×1; Hooded ×3; Dust Runner ×1; Ashen Base ×1; Laboratory ×1; Factory ×1; Intel Center ×1.

### Iri Vane

Промышленность и тяжёлая осада. 100 очков, 30 карт.

Iri Vane ×1; Nova Sterling ×1; Rusty Miller ×1; Miller Hayes ×1; Ash Runner ×4; Iron Husk ×2; Gravebell Mortar ×3; Ash Howitzer ×1; Dune Locomotive ×2; Grave Engine ×1; Walking Refinery ×1; Rad Hunter ×1; Tow Rig ×1; Rust Tank ×2; Hooded ×3; Dust Runner ×1; Ashen Base ×1; Laboratory ×1; Factory ×1; Intel Center ×1.

### Mara the Archivist

Энергетическое вооружение и авиация. 100 очков, 34 карт.

Mara the Archivist ×1; Thren the Scholar ×1; Vale the Machinist ×1; Nyx the Cartographer ×1; Vessel Rifleman ×4; Arc Cantor ×3; Prism Engine ×3; Censer Keeper ×2; Resonance Tank ×3; Halo Kite ×2; Moth Bomber ×3; Arc Sentry ×1; Mnemonic Carrier ×1; Whisper Scout ×3; Glass Runner ×1; Relay Base ×1; Laboratory ×1; Factory ×1; Intel Center ×1.

### Edda the Conductor

Технологии и дальний точный огонь. 99 очков, 34 карт.

Edda the Conductor ×1; Thren the Scholar ×1; Vale the Machinist ×1; Nyx the Cartographer ×1; Vessel Rifleman ×4; Spindle Gunner ×3; Lens Watcher ×3; Prism Lancer ×3; Lensbreaker ×3; Tuning Fork ×3; Arc Sentry ×1; Mnemonic Carrier ×1; Bell Tortoise ×1; Whisper Scout ×3; Glass Runner ×1; Relay Base ×1; Laboratory ×1; Factory ×1; Intel Center ×1.

### Thren the Scholar

Механическая стрелковая пехота и эквип. 98 очков, 38 карт.

Thren the Scholar ×1; Vale the Machinist ×1; Mara the Archivist ×1; Nyx the Cartographer ×1; Vessel Rifleman ×4; Spindle Gunner ×4; Tuning Blade ×4; Lens Watcher ×4; Arc Cantor ×3; Bulwark Vessel ×3; Censer Battery ×1; Mnemonic Carrier ×2; Arc Sentry ×1; Whisper Scout ×3; Glass Runner ×1; Relay Base ×1; Laboratory ×1; Factory ×1; Intel Center ×1.

### Vale the Machinist

Тяжёлые мехи под эквип. 100 очков, 30 карт.

Vale the Machinist ×1; Thren the Scholar ×1; Edda the Conductor ×1; Nyx the Cartographer ×1; Vessel Rifleman ×4; Pilgrim Strider ×2; Bastion Frame ×3; Siege Walker ×3; Heavy Sentinel ×3; Reliquary Crab ×1; Arc Sentry ×1; Mnemonic Carrier ×1; Whisper Scout ×3; Glass Runner ×1; Relay Base ×1; Laboratory ×1; Factory ×1; Intel Center ×1.

### Orr the Keeper

Пехотная линия с Drill. 98 очков, 36 карт.

Orr the Keeper ×1; Thren the Scholar ×1; Vale the Machinist ×1; Nyx the Cartographer ×1; Vessel Rifleman ×4; Tuning Blade ×4; Bulwark Vessel ×4; Bell Breacher ×3; Anchor Warden ×3; Chime Grenadier ×3; Mnemonic Carrier ×2; Arc Sentry ×1; Whisper Scout ×3; Glass Runner ×1; Relay Base ×1; Laboratory ×1; Factory ×1; Intel Center ×1.

### Ilex the Surveyor

Разведка и мобильная огневая поддержка. 99 очков, 38 карт.

Ilex the Surveyor ×1; Thren the Scholar ×1; Vale the Machinist ×1; Jol the Warden ×1; Nyx the Cartographer ×1; Vessel Rifleman ×3; Shard Wanderer ×4; Mantle Skirmisher ×4; Spindle Gunner ×3; Relay Carrier ×4; Pilgrim Strider ×3; Gyrocopter ×3; Arc Sentry ×1; Whisper Scout ×3; Glass Runner ×1; Relay Base ×1; Laboratory ×1; Factory ×1; Intel Center ×1.

### Jol the Warden

Мобильная бронетанковая группа. 99 очков, 35 карт.

Jol the Warden ×1; Thren the Scholar ×1; Vale the Machinist ×1; Nyx the Cartographer ×1; Vessel Rifleman ×4; Shard Wanderer ×4; Resonance Tank ×4; Duplex Crawler ×4; Halo Interceptor ×3; Chordbreaker ×2; Relay Carrier ×1; Arc Sentry ×1; Whisper Scout ×3; Glass Runner ×1; Relay Base ×1; Laboratory ×1; Factory ×1; Intel Center ×1.

### Nyx the Cartographer

Защищённая осадная группа. 99 очков, 32 карт.

Nyx the Cartographer ×1; Thren the Scholar ×1; Vale the Machinist ×1; Mara the Archivist ×1; Vessel Rifleman ×4; Bulwark Vessel ×2; Bell Tortoise ×3; Echo Mortar ×3; Harmonic Howitzer ×3; Counterweight Lobber ×1; Palisade Frame ×2; Arc Sentry ×1; Mnemonic Carrier ×1; Whisper Scout ×3; Glass Runner ×1; Relay Base ×1; Laboratory ×1; Factory ×1; Intel Center ×1.

