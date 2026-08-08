# Механики оригинального Armageddon Empires — статус в ремейке

Сверка по мануалу (`AE Manual.pdf`) против текущего кода. Легенда:

- `[x]` — сделано (или сознательно отклонено — тогда зачёркнуто и написано почему)
- `[ ]` — ещё не сделано (частично или полностью) — это и есть рабочий план

Каждый пункт: **что в мануале** — статус, что именно есть/чего нет в коде.

---

## 1. Фракции и их бонусы (pg. 2)

- [ ] Только одна фракция реально существует — `Faction.cs`: `IronConcord, Random, None`. Machine Empire / Xenopods / Free Mutants не заведены вообще (ни юнитов, ни карт, ни бонусов).
- [ ] Пофракционные бонусы (CR+1 у Empire of Man, AP-стоимость драка карты −1 у Xenopods, hand size +1 у Machine Empire, supply range +1 у Mutants) — не заведены (супплая как системы вообще нет, см. ниже).

## 2. Getting Started / пре-игра (pg. 3)

- [x] ~~Lay Tiles (расстановка гекс-тайлов игроком перед стартом)~~ — не реализовано и не в скоупе: карта чисто процедурная (`HexMapGenerator`), тайлов-в-колоде как концепции нет.
- [x] Basic Challenge / Ground Combat — см. п. 20 (ядро есть).

## 3. Victory Conditions (pg. 9)

- [x] Победа/поражение — упрощённая версия: не "карта Stronghold с HQ-способностью можно перенести", а фиксированная стартовая цитадель. `PlayerSetupData.IsEliminated`, `GameTurnController.EliminatePlayer`/`StartingCitadelLost` — потеря цитадели даёт игроку до начала СВОЕГО следующего хода на отбитие, иначе элиминация. См. `[[project_armageddon_win_condition]]`.
- [ ] Relocate HQ (перенос HQ-способности на другую facility) — не реализовано, см. п. 17.

## 4. Dice and Challenges — ядро (pg. 9, 21)

- [x] Базовый dice-challenge (attacker pool vs defender pool, разница = урон, Defender's Prerogative/re-roll через Fate) — есть, `ChallengeResolver`/`ChallengeResult`, используется во всех реализованных челленджах.

## 5. The Player Deck / колодостроение (pg. 9)

- [ ] Конструктор колоды (min/max карт, лимит очков карт/тайлов, 275 pts и т.п.) — нет. Карты — фиксированный каталог на фракцию (`CardCatalog_IronConcord.asset`), не CCG-метагейм.

## 6. Card Types (pg. 9-14)

- [x] Hero / Unit / Facility карты — есть (`CardType.cs`, `UnitData`, `BuildingData`).
- [ ] Attack Cards (aircraft/helo/missile, отдельный тип с Air-to-Air/Ground/WMD) — типа карты нет вообще.
- [ ] Attachments / Munitions / Enhancements как отдельные категории карт — не проверялось детально, но по каталогу похоже не заведены как отдельная система (только базовые Hero/Unit/Facility).
- [x] Tactic Cards — как отдельный тип есть в `CardType.cs`, но см. п. 12 (боевой tactics-hand задел есть, начинка — нет).

## 7. The Map — терраин и спецхексы (pg. 14-15)

- [x] Terrain Type / Movement Cost / Terrain Bonus (defenseModifier) — есть, `TerrainTypeEntry`.
- [x] Resources (Human/Materials/Energy/Tech, сбор через collector-facility) — упрощённая версия сделана: цитадель даёт 1/тип базово, герой строит facility для полного охвата гекса. См. `[[project_armageddon_resource_extraction]]`.
- [ ] Independents (Join/Bargain/Fight третья сторона на гексах) — не реализовано, ни одного упоминания в коде.
- [ ] Discoveries (золотые звёзды: resource cache / bonus card) — не реализовано.
- [ ] Salvage (шанс подобрать attachment/munition с убитого юнита) — не реализовано.

## 8. Action Points (pg. 15)

- [x] AP от выигрыша инициативы (покупка доп. кубиков) — есть, `InitiativeBuyPanelUI`/`BuyDiceRowUI`/`TurnOrderResolver`.
- [ ] Бонус AP за пленных героев (+3 AP/пленник) — не проверялось прицельно, но раз пленники (`IsPrison`/`TryImprison`) есть — вероятно стоит перепроверить отдельно, не включаю в готовые без проверки.

## 9. Turn Sequence (pg. 15-16)

- [x] Костяк раунда/хода — `GameTurnController.cs` (701 строка), гоняет AP/спавн-хинты/бои.
- [ ] Regeneration-способность юнитов (полное восстановление HP между раундами боя) — не найдено в коде.
- [ ] Observation Checks в начале раунда — см. п. 18 (Fog of War не реализован).

## 10. Armies (pg. 16-17)

- [x] Command Rating / штраф за превышение CR — упрощено: **жёсткий кап** без штрафа −1/2 юнита сверх CR (сознательный отказ от soft-cap+penalty, см. `[[project_armageddon_army_mechanic]]`).
- [ ] Максимум 8 unit-карт / 1 герой на армию — фиксированных капов такого рода в `ArmyData.cs` не найдено (только capacity по CR).
- [ ] Максимум 8 армий на гекс — не проверено/не найдено.
- [ ] AP-стоимость движения = 1 за каждого героя/юнита в армии — сейчас `ActivationApCost` суммирует стоимость по каждому члену (похоже, но не 1:1 с мануалом — надо сверить формулу отдельно).
- [x] ~~Air Assault (прыжок армии в гекс игнорируя террейн/control/supply)~~ — не реализовано, нигде в коде.
- [x] ~~Experience (Green/Hardened/Veteran/Shock/Elite, бонус Fate, штрафы за добавление/потерю юнита)~~ — **отклонено** владельцем проекта. `ArmyViewerModalUI` жёстко пишет "Experience: Green" как заглушку — так и останется.
- [x] ~~Prestige Points / Legendary army~~ — **отклонено** владельцем проекта (комментарий в коде: "Prestige is omitted entirely rather than stubbed").
- [x] ~~Battle Honors~~ — **отклонено** владельцем проекта. Заглушка "Battle Honors: —" так и останется, ничего не считается.
- [x] Retreat (отход к ближайшему Barracks-гексу, garrison никогда не отступает) — сделано, соответствует мануалу. `BattleScreenUI.Retreat.cs`.

## 11. Bases (pg. 17-18)

- [x] Присоны/пленники (передача в ближайшую in-supply базу, освобождение при элиминации владельца) — сделано, `ArmyData.IsPrison`, `TryImprison`, `GameTurnController.ReleasePrisoners`.
- [ ] Лимит facility-слотов по уровню outpost/stronghold (level = N слотов) — не проверялось прицельно.

## 12. Card Hand / Tactics Hand (pg. 18)

- [x] Card Hand (рука юнитов/facility) — есть, `CardHandUI`.
- [ ] Отдельная Tactics Hand с картами-тактиками, которые создаёт герой-Tactician у Academy — только UI-заготовка (`BattleHandUI`), начинки (создание карт героем) нет.

## 13. Playing Cards — правила деплоя (pg. 18-19)

- [x] Базовый деплой юнита/facility в гекс с цитаделью/аутпостом — есть (`CitadelSetupController`, `BuildingRegistry`).
- [ ] Attack Cards деплой/дальность/response-cycle — нет (нет самого типа карт, см. п. 6).

## 14. Control (pg. 19)

- [x] Control-приоритет non-stealthed combat-capable армии — базово есть.
- [ ] Contested hex (два non-combat-capable войска на гексе) / weak control от hero-only армии — по заметкам агента не найдено отдельной реализации этих нюансов.

## 15. Siege (pg. 19-20)

- [x] ~~Отдельный Siege Challenge с besieged-статусом, "Break Siege"/"Assault Defenses", cut-off supply~~ — **сознательно отклонено** владельцем проекта. Вместо этого: "атака гекса со зданием" встроена прямо в обычный Ground Combat (terrain `defenseModifier` + `Base`-tagged здание своим Defense добавляются в тот же ролл). См. `CLAUDE.md`/`[[project_combat_design_decisions]]`.

## 16. Supply (pg. 20)

- [x] ~~Supply range от Stronghold/Outpost, штраф −3/−2 не-в-supply, движение капается до 1 гекса~~ — **сознательно отклонено**, ключевое слово "Supply" отсутствует в коде вообще ни разу, включая комментарии.

## 17. Relocate HQ (pg. 20)

- [ ] Перенос HQ-способности на другую facility за AP+ресурсы — не реализовано.

## 18. Observation / Fog of War (pg. 20-21)

- [ ] **Fog of War целиком отсутствует** — ни Observation Checks, ни Recce, ни detection dice pool. Комментарий в коде прямо говорит "Observation/Recce doesn't exist in this project yet." Это ровно та задача, которую мы недавно начали обсуждать и отменили в этом же чате — она остаётся открытой.

## 19. Stealth и Camouflage (pg. 21)

- [ ] Stealth (армия из всех stealth-capable карт скрывается, не держит control) — не реализовано, `BattleInitiator.cs`: "Stealth doesn't exist yet in this project."
- [x] ~~Camouflage (facility-level снижение детекта)~~ — **отклонено** владельцем проекта.

## 20. Basic Challenge Mechanics (pg. 21-22)

- [x] Ядро (attacker/defender dice pool, Defender's Prerogative, Accept, Fate re-roll) — сделано, это фундамент всей боёвки.

## 21. Special Operations Challenges (pg. 22-23)

- [ ] Assassination Challenge (отдельный от Capture Kill, с "Assassin X" способностью) — не реализовано.
- [ ] Sabotage Challenge (урон facility через "Saboteur X") — не реализовано.
- [ ] Espionage Challenge (Active/Passive, ER-таблицы) — не реализовано.

## 22. Capture Kill Challenges (pg. 24)

- [x] Реализовано (2026-08-08, коммит `6f62cd2`) — `CaptureKillOutcome`, `BattleAttackPopupUI.BeginCaptureKill`. Отличие от мануала: исход определяется **чисто по разнице успехов** (Escaped/Killed/Captured), а не по мануальному отдельному capture-threshold — явное решение владельца проекта.

## 23. Air Combat Challenges (pg. 24-25)

- [ ] Air-to-Air / Ground-to-Air (Anti-Air, Anti-Missile) / Air-to-Ground / WMD (Hydrogen Bomb, Grav Warhead) — ничего не реализовано, нет самого типа Attack Card.

## 24. Minefield Attack Challenges (pg. 26)

- [ ] Minefield facility + Engineer-взаимодействие — не реализовано.

## 25. Research (Card Creation) Challenges (pg. 26)

- [ ] Tactician / Technologist / Geneticist — создание карт через dice-challenge против порога — не реализовано вообще.

## 26. Army Battle Mechanics Overview (pg. 26-28)

### Initiating Battle
- [x] Один из 7 способов инициации (non-stealthed vs non-stealthed) — базовый случай сделан.
- [ ] Остальные 6 вариантов (через stealth, siege-sally, break-siege) — неприменимо/не сделано, т.к. Stealth и Siege-challenge не существуют.
- [x] Delay Attack (отложить бой до конца хода) — сделано, `DelayedBattleRegistry`/`PendingBattle`, плюс докрутка: army нельзя задвоить в pending-бой (`IsHexPending`).
- [x] ~~Empty Armies Rule~~ (пустая армия на гексе со stronghold/outpost уничтожается при бое) — **отклонено** владельцем проекта, оставляем как есть.

### Battle Setup
- [x] ~~Ручной reorder очерёдности армий игроком (up/down arrows)~~ — **отклонено** владельцем проекта: автоматическое chaining (после победы следующая вражеская армия на гексе сразу вступает в бой без выбора игрока) остаётся окончательным решением, не временным фиксом.

### Tactical Battle Module
- [x] Setup/Ready/Actions/Targeting/Ground-to-Ground — ядро сделано (`BattleScreenUI` + partial-файлы, `BattleGrid`).
- [ ] Ряды: **не 2 ряда фронт/тыл переменной длины** как в мануале, а фиксированная 5-рядная сетка (`DefenderBackRow..AttackerBackRow`) — осознанное решение с одобрения владельца, не баг.
- [ ] Special Attack Modifiers: Multi-target, Double Attack, Area Attack, Breakthrough Attack, Sniper Attack — ни одного не найдено как unit-способность в коде.
- [ ] "Committed" состояние с чекмарк-иконкой + доп. раунд после Assault-атаки — не реализовано.

### Next Round / Retreat
- [x] Continue / Retreat Army / Retreat All / Retreat Challenge — сделано.
- [x] Garrisons никогда не отступают — сделано.
- [ ] Trickster (герой отступает до первого раунда, штраф к atk-пулу преследователя) — не проверялось, вероятно не сделано (Trickster нигде не встречался в проверке).

### Battle Results
- [x] Победитель получает control гекса + здания — сделано (`BuildingRegistry.CaptureOrDestroy`).

## 27. Repairing Damage / Healing (pg. 28)

- [ ] Лечение героя/юнита за AP+ресурсы — не найдено (`RepairBase()` для facility Structure Points существует, но недостижим на практике, т.к. по коду "nothing can currently damage a building" вне боя). Heal для юнитов/героев — 0 совпадений в коде.

## 28. Appendix — каталог спецспособностей (pg. 39-51)

Не разбирал пункт-в-пункт (это ~150 отдельных способностей карт) — отдельная задача каталогизации, если понадобится. Сейчас существует только каталог карт Iron Concord (`CardCatalog_IronConcord.asset`); остальные 3 фракции не заведены (см. п. 1).

- [x] "Passive Skills" (`CardDefinition.passiveSkillIds`, был неиспользуемым placeholder-полем) — удалено (2026-08-09). Единственный список способностей карты теперь — `grantedAbilities`, работает и для Hero/Unit, и для Base карт.
- [x] Unit Special Abilities (Hero/Unit-способности, pg. 40-41) — система заведена: `Game.Cards.UnitAbilities` (теги) + `Assets/Cards/UnitAbilityCatalog.asset` (тюнинг магнитуд + справочник всех тегов, включая уже реализованные Base-теги вроде Barracks). Реализовано 5 из ~60: **Critical Damage (x2)**, **Ceramic Armor -1**, **Berserk**, **Rapid Reaction**, **Shock Attack** (упрощён под текущую модель очереди хода — просто убирает цель из оставшейся очереди раунда, если она ещё не ходила, вместо отдельного "committed"-флага). Остальные ~55 способностей из мануала не заведены — добавляются по мере необходимости тем же способом (тег в `UnitAbilities` + эффект в месте применения + запись в `UnitAbilityCatalog.knownAbilities`).

---

## Что уже отложено и зафиксировано в памяти (не переоткрывать заново)

- **Fog of War** (п. 18) — только что обсуждали и **отменили** запуск в этом чате; дизайн (два слоя, непрерывный шейдер, RenderTexture-маска) уже продуман, ждёт явного запроса на возврат.
- **Empty Armies Rule**, **player-ordered multi-army Battle Setup**, **Camouflage**, **Experience/Prestige/Battle Honors** (пп. 10, 19, 26) — были STILL OPEN, теперь **окончательно отклонены** владельцем проекта (2026-08-09) — не переоткрывать без явного запроса.
- **ML-детектор паттернов** и прочие пункты из трейдинг-бота — вне скоупа этого чеклиста (не Unity).

## Как этим пользоваться

Каждый `[ ]` — кандидат в план работ. Проект осознанно НЕ пытается быть 1:1 портом мануала (Supply, Siege-как-Challenge, soft-cap-penalty, 2-рядная сетка боя — всё сознательно упрощено/отклонено с одобрения владельца, см. пометки `~~зачёркнуто~~`). Прежде чем брать в работу любой `[ ]`, стоит уточнить: это "хотим добавить" или "сознательно не портируем" — часть пунктов (Deck-building, Independents/Discoveries, полный каталог Attack Cards, 3 недостающие фракции) может оказаться вне скоупа ремейка в принципе, а не просто "ещё не сделано".
