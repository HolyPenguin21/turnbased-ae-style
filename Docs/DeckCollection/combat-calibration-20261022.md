# Калибровка боевых юнитов Ashen и Vessels — 22 октября 2026

База: `master` / `f19d57975a3568acdb8f57290134bd8dc61e4e44`. Ориентир — согласованный каталог Конкорда после PR #222. Просмотрены 104 изображения, связанные с `detailArt`: 50 Ashen и 54 Vessels. Затем для каждого юнита сопоставлены характеристики, действующие навыки и стоимость.

Изменены только числовые параметры боевых карт и две ожидаемые стоимости стартеров в существующем тесте. Сохранены навыки и их порядок, типы/теги, принадлежность к авиации, вместимость аэродрома, идентификаторы, GUID, ограничения копий, состав колод, герои, здания, снаряжение и мутагены. Конкорд не изменён.

## Основания калибровки

- Простая пехота ориентируется на 3–4 атаки, 1–3 защиты и 4 HP; отдельные мутанты, тяжёлая силовая броня и дроны имеют обоснованные отличия. Обычные танки — 5–7 атаки, 3–4 защиты и 5–7 HP.
- Крупные мехи и сверхтяжёлые корпуса получают больше HP/огневой мощи и более высокую цену, обычно движение 1–2. Лёгкие шагоходы остаются отдельным мобильным классом.
- Дальнобойность, инициатива, типовой урон и поддержка учтены вместе с атакой. CriticalDamage, Hyperkinetic, Ionic, Pyrokinetic, CeramicArmor, Counterattack, Bodyguard, ShockAttack, Splash, Scorcher, RapidReaction и разведка сохранены.
- Regeneration — восстановление 1 HP на глобальной карте в конце хода владельца; это не лечение каждый раунд боя. RaiseTheRots у Scav Carrier создаёт двух Ash Drifter: цена носителя учитывает их, а бесплатная разыгрываемая пехота убрана.
- Авиация атакует случайную цель один раз за свой ход, без обычного ответного огня. AA использует удвоенную атаку. Полёт, запас топлива и разовая оплата запуска калибруются отдельно от наземной дальности. Ненулевой собственный Fate обычных самолётов убран, как у Конкорда.
- Проверка вероятностей использует реальные броски 50/50 и разность успехов: атака 3 против защиты 4 наносит урон в 22,66% случаев; атака 5 — в 50%; атака 7 — в 72,56%. Специалисты с бонусом урона всё равно должны сначала попасть.

## Колоды

Состав и количество карт сохранены. Стоимость игрока включает выбранные чертежи; ИИ отдельно использует существующий набор разрешённых чертежей производства.

| Фракция | Стартер игрока: было → стало | Основная колода ИИ после правок |
|---|---:|---:|
| Конкорд | 75 → 75 | 73 |
| Ashen | 67 → 81 | 79 |
| Vessels | 69 → 79 | 77 |

## Особенность существующего контента

У Dreadnought сохранён Airfield, но `isAviation = 1`, а `airfieldCapacity = 0`. Текущая механика поддерживает мобильный аэродром только на наземном юните с положительной вместимостью. Поэтому Dreadnought оценён как тяжёлая воздушная боевая платформа; цена не включает неработающий аэродром. Изменение его механики/навыка не входит в задачу. Аналогично Halo Interceptor и Iron Hedgehog не получили AA только из-за изображения: учтены их фактические списки навыков.

## Ashen: боевые параметры

А/З/HP — токены атаки / токены защиты / HP. Д/Р/И — движение на карте / дальность в тактическом бою / инициатива. Стрелка обозначает изменение.

| Юнит | Роль | А/З/HP: было → стало | Д/Р/И: было → стало | Навыки (сохранены) |
|---|---|---|---|---|
| Ash Drifter | Melee infantry / summon | 3/1/5 → **3/1/4** | 2/1/2 | Bodyguard, Counterattack |
| Ash Runner | Light rifle infantry | 3/1/4 | 3/2/1 → **3/2/2** | — |
| Bone Picker | Melee infantry | 2/2/5 → **3/2/4** | 2/1/2 | — |
| Hooded | Stealth scout | 2/1/4 | 2/2/2 → **3/2/2** | r1s4, RapidReaction, Stealth4 |
| Tech Scrapper | Collector / repair infantry | 2/1/3 → **3/1/4** | 2/1/1 → **2/2/2** | CollectTech, Repair |
| Scrap Locust | Light walker | 3/2/4 → **4/2/5** | 3/1/3 → **3/2/1** | — |
| Ash Crossbowman | Ranged infantry | 4/1/5 → **4/1/4** | 3/2/1 → **3/2/2** | — |
| Dust Runner | Recon vehicle | 3/2/4 → **4/2/5** | 4/2/1 | r1s5 |
| Fumehound | Assault beast | 3/2/6 → **5/2/6** | 3/1/2 | — |
| Iron Husk | Line infantry | 3/2/5 → **4/2/4** | 3/2/1 | — |
| Rad Leaper | Shock assault infantry | 4/1/5 → **4/2/4** | 3/1/2 → **3/1/3** | ShockAttack |
| Rattletrap | Rapid light vehicle | 3/1/4 → **4/1/4** | 3/2/2 → **4/2/2** | RapidReaction |
| Scab Shield | Shield infantry | 3/3/5 → **4/4/4** | 2/1/1 | Bodyguard |
| Spore Drifter | Area infantry | 3/2/5 → **4/2/4** | 2/2/1 → **2/1/2** | Scorcher |
| Carrion | Light recon helicopter | 4/1/3 → **3/1/3** | 6/1/2 | r1s0 |
| Cinder Stalker | Stealth infantry | 3/2/4 → **4/2/4** | 2/2/2 → **3/2/2** | Stealth4 |
| Deadeye | Sniper infantry | 4/1/3 → **5/1/4** | 3/3/2 → **2/3/2** | — |
| Rad Brute | Berserk mutant | 4/3/6 → **5/2/6** | 3/1/2 → **2/1/2** | Regeneration, Berserk |
| Scrap Shaman | Ionic infantry | 4/2/4 → **5/2/4** | 2/2/1 | Ionic |
| Rattle Gunner | Counterattack infantry | 4/2/5 → **4/2/4** | 2/2/1 | Counterattack |
| Scrap Mortar | Light mobile mortar | 5/1/3 → **5/1/4** | 3/3/2 → **2/3/2** | — |
| Furnace Bearer | Heavy flame infantry | 4/3/6 → **4/3/5** | 3/1/1 → **2/1/1** | Regeneration, Pyrokinetic, Scorcher |
| Ash Walker | Heavy infantry | 4/3/4 | 3/2/1 → **2/2/1** | — |
| Gravebell Mortar | Heavy mortar | 5/1/3 → **6/1/4** | 2/4/2 → **1/4/2** | Splash |
| Iron Hedgehog | Armored gun vehicle | 3/4/6 → **4/3/6** | 3/1/1 → **3/2/1** | — |
| Rad Hunter | AA scout vehicle | 4/2/4 → **5/2/5** | 3/2/1 | AA |
| Rivet Maw | Close-support tank | 4/3/5 → **5/3/6** | 3/1/1 → **2/2/1** | — |
| Roadbreaker | Transport tank | 4/3/5 → **5/3/6** | 3/2/1 | Transport |
| Rust Tank | Light tank | 5/3/5 | 3/2/1 | — |
| Blister Juggernaut | Heavy assault mutant | 4/3/8 → **5/3/7** | 2/1/1 | — |
| Furnace Tortoise | Flame tank | 4/2/5 → **5/3/6** | 3/1/2 → **2/1/1** | Pyrokinetic, Scorcher |
| Iron Maw | Heavy assault vehicle | 6/4/6 → **7/4/7** | 3/1/2 → **2/1/1** | — |
| Rust Breacher | Heavy breacher infantry | 5/3/6 → **5/4/5** | 3/1/1 → **2/1/1** | — |
| Scav Carrier | Summoning transport | 3/1/4 → **3/2/6** | 4/2/1 → **3/2/1** | RaiseTheRots, Transport |
| Scrapwing | Recon gunship | 5/2/5 | 3/2/2 → **6/2/2** | r1s4 |
| Cinder Tanker | Flame support truck | 4/1/5 → **5/2/6** | 3/2/1 → **2/2/1** | Pyrokinetic, Scorcher |
| Grave Engine | Guard mech | 5/4/7 → **5/4/8** | 3/1/1 → **1/1/1** | Bodyguard |
| Boomstick | Medium tank | 6/4/6 | 3/2/1 | — |
| Dune Locomotive | Heavy land train | 5/3/8 → **6/4/9** | 3/2/2 → **2/2/1** | — |
| Flywheel | Fast melee vehicle | 4/2/5 → **5/2/6** | 3/1/2 | — |
| Slag Lobber | Mobile lobber | 6/2/4 → **6/2/5** | 2/3/1 | — |
| Striker | Strike aircraft | 7/4/6 → **7/3/5** | 10/2/2 | r1s0 |
| Leviathan | Superheavy tank | 6/4/10 → **7/4/10** | 3/2/1 → **1/2/1** | — |
| Ash Howitzer | Shock artillery | 7/2/3 → **7/2/5** | 2/4/2 → **1/4/2** | ShockAttack, Splash |
| Scrap Mountain | Superheavy guard mech | 5/5/10 → **4/5/10** | 2/1/1 → **1/1/1** | Bodyguard, Counterattack |
| Carrion Mother | Berserk siege beast | 6/4/8 → **6/4/10** | 3/2/1 → **1/2/1** | ShockAttack, Berserk, Regeneration |
| Colossus | Heavy antiarmor walker | 6/2/4 → **7/3/8** | 3/3/2 → **1/3/2** | Hyperkinetic |
| Rivetstorm | Critical assault mech | 6/4/8 | 2/2/2 | CriticalDamage |
| Walking Refinery | Superheavy gun platform | 9/5/12 → **8/4/12** | 1/3/1 | — |
| Tow Rig | Repair truck | 2/4/5 → **3/4/6** | 2/1/1 | Repair |

### Ashen: стоимость

Ресурсы перечислены как люди / энергия / материалы / технологии. AP выставления — поле карты; действующий RapidReaction по-прежнему делает выставление и активацию бесплатными в AP.

| Юнит | AP выставления | AP активации | Ресурсы: было → стало | Очки колоды | Сопоставление / причина |
|---|---:|---:|---|---:|---|
| Ash Drifter | 1 → **2** | 1 | 0/0/0/0 → **1/0/1/0** | 1 → **2** | Hazard Pioneer; summon/guard value |
| Ash Runner | 1 | 1 | 1/0/0/0 | 1 | Light Infantry |
| Bone Picker | 1 | 1 | 1/0/0/0 | 1 | Light Infantry; range for defense |
| Hooded | 1 | 1 | 0/1/0/0 → **0/1/0/1** | 1 → **3** | Scout; existing RapidReaction premium |
| Tech Scrapper | 1 | 1 | 0/0/0/1 | 2 | Scrapper |
| Scrap Locust | 2 → **3** | 1 | 0/1/1/0 → **0/1/2/0** | 1 → **2** | Grapeshot |
| Ash Crossbowman | 2 | 1 | 1/0/1/0 | 2 | Medium Infantry; defense for range mobility |
| Dust Runner | 2 → **3** | 1 | 0/0/2/1 | 2 → **3** | RC Vehicle |
| Fumehound | 2 → **3** | 1 | 2/0/0/0 → **2/1/0/0** | 2 → **3** | Light assault vehicle; melee-only beast |
| Iron Husk | 2 | 1 | 1/0/1/0 | 2 | Medium Infantry |
| Rad Leaper | 3 | 1 | 1/1/1/1 → **1/1/1/0** | 2 → **3** | Hazard Pioneer; shock and initiative |
| Rattletrap | 2 | 1 | 1/0/1/0 → **0/1/2/0** | 2 | Jackal |
| Scab Shield | 3 → **4** | 1 | 1/0/1/0 → **1/0/2/1** | 2 → **3** | Trench Guardian |
| Spore Drifter | 2 → **3** | 1 | 1/1/0/1 → **1/1/1/0** | 2 → **3** | Flamer; lacks Pyrokinetic |
| Carrion | 3 → **2** | 1 | 0/1/1/0 | 1 → **2** | Mosquito; recon/endurance and paid launch |
| Cinder Stalker | 2 → **3** | 1 | 1/0/1/1 | 3 | Medium Infantry; stealth premium |
| Deadeye | 3 | 1 | 1/0/1/1 | 3 | AT Infantry; range instead of typed damage |
| Rad Brute | 4 | 1 | 1/2/0/1 → **2/1/0/1** | 3 → **4** | Atlas; melee/Berserk and global regeneration |
| Scrap Shaman | 3 → **4** | 1 | 1/1/0/1 → **1/1/1/1** | 3 | Coil Marksman |
| Rattle Gunner | 3 | 1 | 1/0/1/1 → **1/0/2/0** | 2 → **3** | Heavy Infantry; armor for counterattack |
| Scrap Mortar | 4 | 1 | 0/2/1/1 → **0/2/2/0** | 2 → **3** | Hammerback; lighter gun and armor |
| Furnace Bearer | 5 | 1 | 1/2/1/2 → **2/2/1/1** | 3 → **4** | Flamer; armor/HP/global regeneration premium |
| Ash Walker | 4 → **3** | 1 | 1/0/2/1 → **1/0/2/0** | 3 | Heavy Infantry |
| Gravebell Mortar | 5 → **6** | 1 | 0/3/1/3 → **0/3/2/2** | 3 → **5** | Artillery Tank; lighter Splash artillery |
| Iron Hedgehog | 4 | 1 | 0/2/2/0 → **0/1/3/0** | 3 | Light Tank; attack for HP; no AA skill |
| Rad Hunter | 3 → **4** | 1 | 0/2/0/1 → **0/2/1/2** | 3 → **4** | Roadwarden AA; lighter protection and price |
| Rivet Maw | 3 → **4** | 1 | 0/1/2/0 → **0/1/3/0** | 3 | Light Tank; movement for HP |
| Roadbreaker | 4 → **5** | 1 | 0/1/3/1 → **0/2/3/1** | 3 → **4** | Medium Tank; reduced stats for Transport |
| Rust Tank | 4 | 1 | 0/1/2/0 → **0/1/3/0** | 3 | Light Tank |
| Blister Juggernaut | 4 → **5** | 1 | 1/0/2/2 → **2/1/2/1** | 4 | Atlas; melee and attack/HP trade |
| Furnace Tortoise | 4 → **5** | 1 | 0/1/2/2 → **0/2/3/1** | 4 | Helios; close armored Pyrokinetic/Scorcher |
| Iron Maw | 5 → **6** | 1 | 0/2/2/2 → **0/2/4/1** | 4 → **5** | Heavy Tank; lacks CeramicArmor and range |
| Rust Breacher | 4 | 1 | 0/2/2/1 → **1/0/2/1** | 4 → **3** | Trench Guardian; no Bodyguard |
| Scav Carrier | 5 | 1 | 3/1/2/2 → **2/1/2/1** | 4 → **5** | Transport chassis plus two Ash Drifter summons |
| Scrapwing | 5 | 1 | 0/2/1/2 → **0/2/2/2** | 2 → **4** | Rusty Vulture |
| Cinder Tanker | 4 → **5** | 1 | 0/2/2/1 → **0/3/2/1** | 4 | Helios; Scorcher/HP for attack/range |
| Grave Engine | 5 → **6** | 1 | 0/2/2/3 → **0/2/4/2** | 4 → **5** | Breakwater Crab; heavy melee mech |
| Boomstick | 6 → **5** | 1 | 0/2/3/1 → **0/2/4/0** | 5 → **4** | Medium Tank |
| Dune Locomotive | 5 → **6** | 1 | 0/2/4/1 → **0/2/5/1** | 5 | Heavy Tank; HP for attack/CeramicArmor |
| Flywheel | 3 → **4** | 1 | 0/1/1/1 → **0/1/3/0** | 2 → **3** | Light Tank; melee speed/HP for armor |
| Slag Lobber | 5 | 1 | 0/2/2/2 → **0/2/3/1** | 3 → **4** | Hammerback; extra hull HP |
| Striker | 6 | 1 | 0/3/2/3 | 3 → **5** | Wasp; attack for defense/AA |
| Leviathan | 6 → **8** | 2 | 0/2/4/2 → **0/3/5/2** | 6 | Heavy Tank; heavy hull and activation cost |
| Ash Howitzer | 8 | 1 | 0/5/2/5 → **0/4/2/4** | 6 | Artillery Tank; ShockAttack for attack token |
| Scrap Mountain | 7 → **8** | 1 | 0/3/4/3 → **0/3/5/2** | 6 | Breakwater Crab; massive shield/counterattack |
| Carrion Mother | 8 | 1 | 2/2/3/3 → **2/3/4/2** | 4 → **7** | Duelist; ShockAttack/Berserk/global regeneration |
| Colossus | 6 → **8** | 1 | 0/3/2/4 → **0/3/4/3** | 5 → **6** | Pulse Cannon; large walker protection premium |
| Rivetstorm | 8 | 1 | 1/3/4/3 | 5 → **6** | Duelist |
| Walking Refinery | 10 → **9** | 1 → **2** | 2/4/6/3 → **2/3/6/3** | 5 → **7** | Shoal Destroyer; no AA discount |
| Tow Rig | 4 → **3** | 1 | 0/3/3/3 → **0/1/3/1** | 4 → **3** | Mule; HP for range |

## Vessels: боевые параметры

А/З/HP — токены атаки / токены защиты / HP. Д/Р/И — движение на карте / дальность в тактическом бою / инициатива. Стрелка обозначает изменение.

| Юнит | Роль | А/З/HP: было → стало | Д/Р/И: было → стало | Навыки (сохранены) |
|---|---|---|---|---|
| Whisper Scout | Stealth scout | 3/1/4 → **2/1/4** | 2/2/2 → **3/2/1** | r1s4, Stealth4 |
| Salvage Drone | Collector drone | 2/1/3 → **2/2/3** | 2/1/1 | CollectMaterials |
| Shard Wanderer | Rapid melee infantry | 2/2/4 → **3/2/4** | 2/1/2 → **3/1/2** | RapidReaction |
| Silt Pilgrim | Drill support infantry | 2/1/4 | 2/1/2 → **3/1/2** | Drill |
| Vessel Rifleman | Light rifle infantry | 3/1/4 | 3/2/1 → **3/2/2** | — |
| Arc Cantor | Antiarmor area infantry | 3/1/4 → **4/2/4** | 3/2/1 → **2/2/1** | Scorcher, Hyperkinetic |
| Bulwark Vessel | Shield infantry | 3/2/5 → **4/4/4** | 3/2/1 → **2/1/1** | Bodyguard |
| Glass Runner | Recon vehicle | 3/2/4 → **4/2/5** | 4/2/1 | r1s5 |
| Mantle Skirmisher | Critical skirmisher | 3/2/4 | 3/1/1 → **3/1/2** | CriticalDamage |
| Tuning Blade | Melee infantry | 4/2/4 → **5/2/4** | 3/1/1 → **3/1/2** | — |
| Lens Watcher | Sniper infantry | 3/1/3 → **5/1/4** | 3/3/2 → **2/3/2** | — |
| Relay Carrier | Recon transport | 3/1/4 → **3/2/5** | 3/2/1 → **4/2/1** | r1s5, Transport |
| Duplex Crawler | Light gun vehicle | 4/1/5 → **5/2/5** | 3/2/1 | — |
| Mirror Duelist | Counterattack duelist | 4/2/5 → **5/2/5** | 3/1/2 → **2/1/2** | Counterattack |
| Pilgrim Strider | Rapid light walker | 4/1/4 → **5/2/5** | 4/2/2 → **3/2/2** | RapidReaction |
| Spindle Gunner | Line gunner | 3/2/5 → **4/2/4** | 3/2/1 | — |
| Core Tender | Sturdy utility infantry | 3/2/5 | 3/2/1 → **2/1/1** | — |
| Arc Sentry | AA light walker | 4/2/4 → **4/3/5** | 3/2/1 → **2/2/1** | AA |
| Bastion Frame | Armored guard platform | 3/2/6 → **4/4/7** | 3/1/1 → **2/1/1** | CeramicArmor, Bodyguard |
| Halo Kite | Light attack drone | 4/1/3 → **3/2/3** | 6/2/2 | — |
| Harmonic Howitzer | Shock area artillery | 5/1/3 | 2/4/2 → **2/3/2** | ShockAttack, Splash |
| Resonance Tank | Counterattack medium tank | 5/3/6 → **6/3/6** | 3/2/1 | Counterattack |
| Bell Breacher | Heavy breacher infantry | 4/3/5 → **5/3/4** | 2/1/1 | — |
| Censer Keeper | Flame infantry | 4/3/5 → **4/3/4** | 2/1/2 | Pyrokinetic, Scorcher |
| Echo Mortar | Shock mobile mortar | 4/1/2 → **5/1/4** | 3/3/2 → **2/3/2** | ShockAttack |
| Anchor Warden | Shock heavy infantry | 4/3/5 → **5/3/4** | 2/1/1 | ShockAttack |
| Null Vessel | Heavy melee infantry | 5/3/5 | 3/1/2 → **2/1/2** | — |
| Chime Grenadier | Area heavy infantry | 4/3/5 → **5/3/4** | 2/2/1 | Splash |
| Choir Roller | Light tube artillery | 4/2/4 → **6/1/4** | 3/1/2 → **2/3/2** | — |
| Prism Engine | Antiarmor gun platform | 5/2/3 → **6/2/4** | 3/3/2 → **2/3/2** | Hyperkinetic |
| Prism Lancer | Antiarmor light walker | 4/2/3 → **5/2/4** | 3/3/2 → **2/3/2** | Hyperkinetic |
| Chime Suppressor | Heavy line infantry | 4/2/5 → **4/3/4** | 2/2/1 | — |
| Shear Vessel | Melee assault walker | 4/2/5 → **6/2/5** | 3/1/2 → **2/1/3** | — |
| Bell Tortoise | Heavy tank | 5/3/6 → **6/4/7** | 2/2/1 | — |
| Casket Ram | Armored ram | 5/3/6 → **6/4/8** | 3/1/1 → **2/1/1** | — |
| Monolith | Heavy walking mortar | 6/1/8 → **6/2/7** | 1/2/1 → **1/4/1** | — |
| Heavy Sentinel | Area support mech | 4/3/4 → **6/3/7** | 3/2/1 → **2/2/1** | Splash |
| Mnemonic Carrier | Repair transport | 3/2/5 → **3/3/6** | 3/2/1 | Repair, Transport |
| Gyrocopter | Recon gunship | 5/3/6 → **5/2/5** | 6/2/2 | r1s4 |
| Censer Battery | Heavy Drill support | 1/2/6 → **3/3/7** | 2/1/2 → **2/1/1** | Drill |
| Chord Tug | AA escort aircraft | 4/2/5 → **4/3/5** | 5/2/1 → **4/2/1** | AA |
| Chordbreaker | Heavy rail tank | 6/2/5 → **7/3/6** | 2/3/2 | — |
| Counterweight Lobber | Mobile lobber | 6/1/4 → **6/2/5** | 2/3/2 | — |
| Halo Interceptor | Gun vehicle | 5/3/6 | 3/2/1 | — |
| Lensbreaker | Heavy rail tank | 6/4/7 → **7/4/7** | 2/2/1 → **2/3/1** | — |
| Mnemonic Loom | Heavy gun mech | 7/3/8 → **8/3/8** | 2/3/1 → **1/3/1** | — |
| Siege Walker | Assault mech | 5/4/7 → **8/4/8** | 3/1/1 → **1/2/1** | — |
| Palisade Frame | Heavy shield mech | 4/5/10 → **5/5/10** | 2/2/1 → **1/2/1** | — |
| Moth Bomber | Strike aircraft | 5/3/6 → **7/3/6** | 8/2/2 | — |
| Reliquary Crab | Heavy melee mech | 5/4/7 → **8/4/8** | 2/1/1 → **1/1/1** | — |
| Tuning Fork | Long-range gun platform | 6/2/4 → **7/2/5** | 2/3/2 → **1/3/2** | — |
| Skimmer | Fast strike aircraft | 9/4/6 → **6/4/5** | 12/2/2 | r1s0 |
| Dreadnought | Heavy airborne fortress | 8/4/8 → **7/4/10** | 3/1/1 → **4/2/1** | Airfield |
| Pendulum Frame | Heavy assault mech | 9/5/12 → **8/4/10** | 2/1/1 → **1/2/1** | — |

### Vessels: стоимость

Ресурсы перечислены как люди / энергия / материалы / технологии. AP выставления — поле карты; действующий RapidReaction по-прежнему делает выставление и активацию бесплатными в AP.

| Юнит | AP выставления | AP активации | Ресурсы: было → стало | Очки колоды | Сопоставление / причина |
|---|---:|---:|---|---:|---|
| Whisper Scout | 1 | 1 | 0/1/0/0 | 2 | Scout |
| Salvage Drone | 1 | 1 | 0/0/1/0 | 1 | Scrapper; less combat/utility, no Repair |
| Shard Wanderer | 1 → **2** | 1 | 0/0/0/0 → **0/0/1/0** | 1 → **2** | Light Infantry; RapidReaction premium |
| Silt Pilgrim | 1 → **2** | 1 | 0/0/0/1 → **0/1/0/1** | 1 → **2** | Dust Medic; Drill instead of Heal |
| Vessel Rifleman | 1 | 1 | 0/0/1/0 | 1 | Light Infantry |
| Arc Cantor | 2 → **3** | 1 | 0/1/1/1 | 2 → **3** | AT Infantry; Scorcher for one attack token |
| Bulwark Vessel | 2 → **4** | 1 | 0/1/1/0 → **0/1/2/1** | 2 → **3** | Trench Guardian |
| Glass Runner | 2 → **3** | 1 | 0/0/2/0 → **0/0/2/1** | 2 → **3** | RC Vehicle |
| Mantle Skirmisher | 2 | 1 | 0/1/1/0 | 2 | Medium Infantry; critical melee for range/token |
| Tuning Blade | 2 → **3** | 2 → **1** | 0/0/1/1 → **0/0/2/1** | 2 | Medium Infantry; stronger melee and lower reach |
| Lens Watcher | 2 → **3** | 1 | 0/1/1/2 → **0/1/1/1** | 3 | Deadeye |
| Relay Carrier | 3 → **4** | 1 | 0/1/1/1 → **0/1/2/2** | 2 → **4** | Sand Jackal / RC Vehicle; combined utility |
| Duplex Crawler | 3 → **4** | 1 | 0/1/2/0 → **0/1/2/1** | 3 | Light Tank; less armor and different resource mix |
| Mirror Duelist | 3 → **4** | 1 | 0/2/1/1 → **0/1/2/1** | 3 | Hazard Pioneer; elite melee premium |
| Pilgrim Strider | 3 → **4** | 1 | 0/1/2/0 → **0/1/2/1** | 3 | Jackal; stronger light walker, less movement |
| Spindle Gunner | 3 → **2** | 1 | 0/1/2/0 → **0/0/1/1** | 3 → **2** | Medium Infantry |
| Core Tender | 3 → **2** | 1 | 0/0/1/2 → **0/0/1/1** | 2 | Medium Infantry; HP for attack/range; no innate Repair |
| Arc Sentry | 4 → **3** | 1 | 0/0/2/2 → **0/1/1/1** | 2 → **3** | Crawler; smaller walker |
| Bastion Frame | 4 → **6** | 1 | 0/2/1/1 → **0/2/3/2** | 2 → **5** | Breakwater Crab; CeramicArmor premium |
| Halo Kite | 4 → **2** | 1 | 0/0/2/2 → **0/1/0/1** | 2 → **1** | Mosquito; defense for paid launch |
| Harmonic Howitzer | 4 → **5** | 1 | 0/3/1/4 → **0/2/1/3** | 2 → **4** | Artillery Cannon; Splash premium |
| Resonance Tank | 4 → **5** | 1 | 0/1/2/1 → **0/2/2/2** | 2 → **4** | Medium Tank; counterattack for defense token |
| Bell Breacher | 4 → **3** | 1 | 0/2/2/0 → **0/1/2/0** | 3 | Heavy Infantry; attack for range |
| Censer Keeper | 4 | 1 | 0/2/1/1 | 3 | Flamer; armor premium |
| Echo Mortar | 4 | 1 | 0/2/1/2 → **0/2/1/1** | 2 → **3** | Artillery Cannon; tracked hull HP premium |
| Anchor Warden | 4 | 1 | 0/2/2/1 → **0/1/2/1** | 3 | Heavy Infantry; shock assault premium |
| Null Vessel | 4 | 1 | 0/1/2/1 | 3 | Rust Breacher; initiative for defense |
| Chime Grenadier | 4 | 1 | 0/2/2/2 → **0/1/2/2** | 4 | Siege Grenadier |
| Choir Roller | 4 | 1 | 0/1/2/0 → **0/2/1/2** | 4 → **3** | Sand Organ; hull HP for Splash |
| Prism Engine | 4 → **5** | 1 | 0/1/3/3 → **0/2/1/3** | 2 → **4** | Pulse Cannon; lighter beam gun |
| Prism Lancer | 5 → **4** | 1 | 0/2/1/1 → **0/1/1/2** | 3 | AT Cannon; walker HP/armor premium |
| Chime Suppressor | 4 → **3** | 1 | 0/1/1/1 → **0/1/2/0** | 3 | Heavy Infantry |
| Shear Vessel | 5 → **4** | 1 | 0/1/2/0 → **0/1/2/1** | 3 | Light assault vehicle; attack/initiative for reach |
| Bell Tortoise | 5 | 1 | 0/1/2/1 → **0/2/3/1** | 4 | Medium Tank; hull HP for movement |
| Casket Ram | 5 → **6** | 1 | 0/2/2/1 → **0/2/4/1** | 4 | Heavy Tank; melee hull without CeramicArmor |
| Monolith | 5 → **6** | 2 → **1** | 0/3/3/2 → **0/2/3/2** | 2 → **4** | Artillery Tank; lighter gun without Splash |
| Heavy Sentinel | 5 → **6** | 1 | 0/2/2/1 → **0/2/4/2** | 2 → **5** | Bulldog; HP for defense, heavier mech cost |
| Mnemonic Carrier | 5 → **4** | 1 | 0/2/2/2 → **0/1/2/2** | 3 → **4** | Mule / Barracuda; combined utility |
| Gyrocopter | 5 | 1 | 0/1/1/2 → **0/2/1/3** | 2 → **4** | Rusty Vulture; tech-heavy resource mix |
| Censer Battery | 5 | 1 | 0/2/3/1 | 6 → **4** | Boatswain; support protection for attack |
| Chord Tug | 5 → **4** | 1 | 0/2/1/2 → **0/2/1/3** | 4 | Sky Tug; armor for HP/recon |
| Chordbreaker | 6 | 1 | 0/2/2/3 → **0/2/4/2** | 5 | Needle Rail Tank; protection/gun for movement |
| Counterweight Lobber | 5 | 1 | 0/2/2/2 | 4 | Slag Lobber; technology-heavy resource mix |
| Halo Interceptor | 5 → **4** | 1 | 0/2/2/2 → **0/2/2/1** | 4 → **3** | Roadwarden AA; no AA skill discount |
| Lensbreaker | 6 → **7** | 1 | 0/2/4/2 | 6 → **5** | Heavy Tank; range for CeramicArmor |
| Mnemonic Loom | 7 → **8** | 1 | 0/3/4/3 → **0/3/5/3** | 6 | Arsenal; less defense and lower resource cost |
| Siege Walker | 7 | 1 | 0/3/2/2 → **0/3/4/3** | 2 → **5** | Mantis |
| Palisade Frame | 7 → **8** | 1 | 0/3/4/3 | 6 | Scrap Mountain; reach/attack without guard skills |
| Moth Bomber | 6 | 1 | 0/3/2/3 | 6 → **5** | Striker; HP for movement/recon; no area skill |
| Reliquary Crab | 6 → **7** | 1 | 0/1/3/2 → **0/2/4/2** | 5 | Mantis; cheaper melee-only hull |
| Tuning Fork | 6 | 1 | 0/3/1/4 → **0/2/2/3** | 5 → **4** | Pulse Cannon; no Hyperkinetic discount |
| Skimmer | 7 → **6** | 1 | 0/2/0/2 → **0/3/1/4** | 3 → **5** | Wasp; speed for AA/recon difference |
| Dreadnought | 8 | 1 → **2** | 0/3/3/4 → **1/3/4/3** | 8 → **7** | Skydiver; heavier armor/HP; Airfield inactive on aircraft |
| Pendulum Frame | 9 → **8** | 1 | 0/5/3/5 → **0/3/5/3** | 4 → **6** | Mantis; extra hull HP and cost |

## Авиация: дополнительные параметры

Без дозаправки — число разрешённых окончаний хода вне аэродрома. Значение 0 требует посадки в ход вылета. Энергия и AP запуска оплачиваются один раз за вылет.

| Юнит | Движение | Энергия запуска: было → стало | Без дозаправки: было → стало | AP запуска |
|---|---:|---:|---:|---:|
| Carrion | 6 | 1 | 0 → **1** | 1 |
| Scrapwing | 6 | 1 → **2** | 1 → **2** | 1 |
| Striker | 10 | 2 | 0 | 1 |
| Halo Kite | 6 | 1 | 0 | 1 |
| Gyrocopter | 6 | 1 → **2** | 1 → **2** | 1 |
| Chord Tug | 4 | 1 | 1 | 1 |
| Moth Bomber | 8 | 2 | 2 → **0** | 1 |
| Skimmer | 12 | 2 | 0 | 1 |
| Dreadnought | 4 | 3 | 5 → **2** | 2 |

## Проверки

- Оба штатных валидатора Unity YAML выполнены для обоих каталогов.
- Проверены все 104 спецификации, уникальность полей YAML и ключей карт, отсутствие изменений вне разрешённых полей и полное сохранение навыков/изображений.
- Проверены разрешение ссылок колод, лимиты копий, бюджет стартеров и равенство основного состава стартера и соответствующей колоды ИИ.
- Прослежены общие пути чтения характеристик, стоимости, резервов и кэша боевых оценок: новые значения берутся из тех же CardDefinition, отдельные значения для ИИ не добавлялись.
- Unity 6000.5.4f1 здесь недоступен. EditMode, импорт ассетов в редакторе и игровые прогоны не выполнялись. Это калибровка по изображениям, текущей механике и утверждённым аналогам, а не утверждение о доказанном соревновательном балансе.
