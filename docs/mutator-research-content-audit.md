# Mutator Research: анализ и реализация 20 карт

## Результат и исходная ревизия

Итоговая база: `c413e7853ca3213414b02b44e01c71e89d4cfeaf`, актуальный master после завершения проверки второго attachment-slot. Первоначальный анализ был выполнен на `fb30afcbd51a633cf9169ac41064720ae49557f5`: тогда framework существовал только в feature/mutator-attachment-slot. Готовая зависимость была проверена и подключена в изолированной ветке. После её слияния владельцем в master рабочая ветка обновлена до итоговой базы. Незакоммиченные изменения другой рабочей копии не использовались.

Масштаб: добавление игрового контента и локальное исправление отображения. Архитектура и AI balance не меняются. 20 определений встроены в существующий Neutral catalog, а не созданы как отдельные ScriptableObject: именно такой формат использует CardDefinition. Все карты — CardType.Equipment, AttachmentSlot.Mutator, faction=None, явно требуют Bio. Первые 16 имеют единственный hostKind Unit, последние четыре — Hero.

Обнаружен блокирующий пробел исходного контента: у 16 героев Iron Concord / Ashen был только Hero (и иногда Support), без Bio. Владелец отдельно выбрал добавление Bio, после указания влияния тега на Pyrokinetic. Добавлен только этот тег, прежние теги сохранены. Восемь героев Vessels остаются Mechanical и не принимают Mutator. Это единственное согласованное изменение существующих игровых определений вне нового набора.

## Attachment framework и навыки

CardDefinition.attachmentSlot определяет назначение; Equipment=0 — прежний default, Mutator=1. CardData и UnitData хранят независимые Equipment и Mutator. EquipmentSystem.FitsHost / CanAttach совмещают обязательный Bio для Mutator с authored hostTypeTags/hostKinds и проверкой занятости выбранного слота. Общий EquipmentGrant интерпретирует clearAbilityFamilies → removeAbilities → addAbilities → additive/override stats. Каноническая композиция двух слотов, Project/PredictAttachment, применение к живому телу и перенос из руки уже принадлежат framework в master. Для нового контента core attachment logic не изменена.

| Эффект | Реальный формат | Значение / правило |
|---|---|---|
| Recce R1S4 | UnitAbilities.R1S4 = `r1s4` | AbilityParams.TryGetRecce извлекает radius=1, spotStrength=4; отдельного generic Recce ID нет. |
| Stealth | UnitAbilities.Stealth4 = `Stealth4` | Уровень маскировки 4, разбирается AbilityParams.TryGetStealthLevel. |
| RapidReaction | `RapidReaction` | Существующий gameplay обнуляет ActivationApCost. Reflex Genome не содержит statChanges, но этот эффект навыка действует и на героя. |
| Regeneration | `Regeneration` | Существующий end-turn эффект восстанавливает 1 HP в пределах максимума. |
| Fate | EquipmentStat.Fate = 9 | Fortunate Genome увеличивает Fate/FateMax на 1; CardDefinition.fate у самой Mutator-карты — независимая сложность Research. |

Recce и Stealth имеют независимые families, могут сосуществовать (в исходных картах обе встречаются вместе). RapidReaction не входит в эти families. Поэтому ни одна из 20 карт не очищает/удаляет навыки. Existing r1s6 не заменяется на более слабый r1s4: новый тег добавляется, существующий выбор максимальной силы наблюдения сохраняется. Авторская замена навыков остаётся доступной через прежний grant model, без нового engine.

Activation AP снизу ограничен 0. Defense/HP/Move/Initiative — существующим минимумом 1. Новые numeric grants исключительно additive; Attack/Range/Command/Resistance не затрагиваются. Данные обычных combat stats на Equipment-определении обнулены; meaningful changes находятся только в equipment.statChanges.

## Экономика до назначения новых значений

| Существующее сравнение | Difficulty (fate) | AP Create / minted attach | ResourceCost |
|---|---:|---:|---|
| Flamer / Heavy MG, Production | 3 | 1 / 1 | H2 E1 / E1 M1 T1 |
| Armor Plate, Production | 4 | 1 / 1 | E1 T1 |
| Double Barrel, Production | 6 | 1 / 1 | E2 M1 T4 |
| Plasma Gun, старый Research | 6 | 1 / 1 | E1 M1 T2 |
| Nuclear Engine, старый Research | 5 | 1 / 1 | E3 T4 |
| Plasma Cannon, старый Research | 5 | 1 / 1 | E4 M1 T4 |
| Простые Mutator: выбранный диапазон | 3–4 | 1 / 1 | H1 E1 T1–2 |
| Сильные сочетания / RapidReaction | 4–5 | 1 / 1 | H1 E1–2 T2–3 |

Таблица сравнения и выбранные bands были предоставлены до создания карточек. Использован существующий общий Research/Production диапазон 3–6, с меньшей сложностью физиологических эффектов, чем тяжёлого оружия. Ресурсы создания — 3–6 суммарно. Стоимость является исходным балансом, а не доказательством оптимального баланса игры.

У всех Mutator apCost=1, activationApCost=1. Общий ResearchProductionSystem сначала оплачивает apCost/resourceCost как stake. MintCard помечает экземпляр ResearchProductionCreated; последующее применение требует только activationApCost, без повторной оплаты ресурсов. Обычная неминтованная копия сохраняет обычный ResourceCost. Новый платёжный путь/банк/резерв не создавался.

## Фактический content

Значения следующей таблицы извлечены из конечного YAML. AP — создание / применение minted карты; H/E/M/T — Human/Energy/Materials/Tech. У всех slot=Mutator и удаляемые abilities/families пусты. Ready означает, что оба существующих sprite GUID подключены; native импорт проверяется отдельным EditMode тестом.

| Name | Authored key | Target | Slot | Stats | Added abilities | Removed | Difficulty | AP | Resources | Artwork |
|---|---|---|---|---|---|---|---:|---|---|---|
| Dermal Plating | `neutral.mutator.dermal-plating` | Bio Unit | Mutator | Defense 1 | - | - | 3 | 1 / 1 | H1 E1 T1 | Ready |
| Reactive Marrow | `neutral.mutator.reactive-marrow` | Bio Unit | Mutator | HP 2 | - | - | 3 | 1 / 1 | H1 E1 T1 | Ready |
| Reinforced Skeleton | `neutral.mutator.reinforced-skeleton` | Bio Unit | Mutator | Defense 1, HP 1, Initiative -1 | - | - | 4 | 1 / 1 | H1 E1 T2 | Ready |
| Pain Suppression | `neutral.mutator.pain-suppression` | Bio Unit | Mutator | HP 2, Move -1 | - | - | 3 | 1 / 1 | H1 E1 T1 | Ready |
| Regenerative Culture | `neutral.mutator.regenerative-culture` | Bio Unit | Mutator | - | Regeneration | - | 4 | 1 / 1 | H1 E1 T2 | Ready |
| Hyper-Regeneration | `neutral.mutator.hyper-regeneration` | Bio Unit | Mutator | HP 1, Move -1 | Regeneration | - | 5 | 1 / 1 | H1 E2 T2 | Ready |
| Survivor Strain | `neutral.mutator.survivor-strain` | Bio Unit | Mutator | Defense 1, Move -1 | Regeneration | - | 5 | 1 / 1 | H1 E2 T2 | Ready |
| Adrenal Surge | `neutral.mutator.adrenal-surge` | Bio Unit | Mutator | Move 1, Initiative 1, Defense -1 | - | - | 4 | 1 / 1 | H1 E1 T2 | Ready |
| Metabolic Overdrive | `neutral.mutator.metabolic-overdrive` | Bio Unit | Mutator | Move 1, Defense -1 | - | - | 3 | 1 / 1 | H1 E1 T1 | Ready |
| Predator Reflexes | `neutral.mutator.predator-reflexes` | Bio Unit | Mutator | Initiative 1, Defense 1, Move -1 | - | - | 4 | 1 / 1 | H1 E1 T2 | Ready |
| Neural Accelerator | `neutral.mutator.neural-accelerator` | Bio Unit | Mutator | Initiative 1, Activation AP -1 | - | - | 5 | 1 / 1 | H1 E2 T3 | Ready |
| Rapid Synapse | `neutral.mutator.rapid-synapse` | Bio Unit | Mutator | Defense -1 | RapidReaction | - | 4 | 1 / 1 | H1 E2 T2 | Ready |
| Hunter Glands | `neutral.mutator.hunter-glands` | Bio Unit | Mutator | Defense -1 | r1s4 | - | 3 | 1 / 1 | H1 E1 T1 | Ready |
| Enhanced Senses | `neutral.mutator.enhanced-senses` | Bio Unit | Mutator | Initiative 1, Move -1 | r1s4 | - | 4 | 1 / 1 | H1 E1 T2 | Ready |
| Wanderer Strain | `neutral.mutator.wanderer-strain` | Bio Unit | Mutator | Move 1, Defense -1 | r1s4 | - | 4 | 1 / 1 | H1 E1 T2 | Ready |
| Chameleon Tissue | `neutral.mutator.chameleon-tissue` | Bio Unit | Mutator | Move -1 | Stealth4 | - | 3 | 1 / 1 | H1 E1 T1 | Ready |
| Fortunate Genome | `neutral.mutator.fortunate-genome` | Bio Hero | Mutator | Fate 1 | - | - | 4 | 1 / 1 | H1 E1 T2 | Ready |
| Ghost Genome | `neutral.mutator.ghost-genome` | Bio Hero | Mutator | - | Stealth4 | - | 4 | 1 / 1 | H1 E1 T2 | Ready |
| Hunter Genome | `neutral.mutator.hunter-genome` | Bio Hero | Mutator | - | r1s4 | - | 4 | 1 / 1 | H1 E1 T2 | Ready |
| Reflex Genome | `neutral.mutator.reflex-genome` | Bio Hero | Mutator | - | RapidReaction | - | 5 | 1 / 1 | H1 E2 T3 | Ready |

## Research before / after

| Исходная запись | Итог |
|---|---|
| neutral.equipment.bio.plasma-gun | Убрана только из Research. Определение и прежние effects/cost/art сохранены в Neutral catalog. В Production не переносилась. |
| neutral.equipment.armored.plasma-cannon | Убрана только из Research. Определение сохранено. В Production не переносилась. |
| neutral.equipment.mechanical.nuclear-engine | Убрана только из Research. Определение сохранено. В Production не переносилась. |

Research содержит ровно 20 Mutator, factionRestriction=None. Ссылки на четыре исходных faction catalogs сохранены; новые ключи разрешаются через уже подключённый Neutral catalog. Нет правила Research=>Mutator в коде.

Production сохраняет семь прежних записей в том же порядке: Flamer, AT Launcher, Claws, Heavy MG, Armor Plate, Artillery Cannon, Double Barrel. Ни одна из 24 исходных Neutral definitions не изменена. Старые Equipment сохраняют default Equipment slot и grant data.

## Artwork mapping

Новые изображения не создавались и не переименовывались. Существующая convention — порядковый номер и цвет жидкости. GameCards путь: Assets/Textures/Units/0_Neutrals/GameCards/. DetailView путь: Assets/Textures/Units/0_Neutrals/DetailView/.

| Authored key | art filename | detailArt filename | Статус |
|---|---|---|---|
| `neutral.mutator.dermal-plating` | Mutator_01_Crimson.png | Mutator_01_Crimson_Full.png | Existing sprites wired |
| `neutral.mutator.reactive-marrow` | Mutator_02_Coral.png | Mutator_02_Coral_Full.png | Existing sprites wired |
| `neutral.mutator.reinforced-skeleton` | Mutator_03_CopperOrange.png | Mutator_03_CopperOrange_Full.png | Existing sprites wired |
| `neutral.mutator.pain-suppression` | Mutator_04_Amber.png | Mutator_04_Amber_Full.png | Existing sprites wired |
| `neutral.mutator.regenerative-culture` | Mutator_05_LemonYellow.png | Mutator_05_LemonYellow_Full.png | Existing sprites wired |
| `neutral.mutator.hyper-regeneration` | Mutator_06_Olive.png | Mutator_06_Olive_Full.png | Existing sprites wired |
| `neutral.mutator.survivor-strain` | Mutator_07_Graphite.png | Mutator_07_Graphite_Full.png | Existing sprites wired |
| `neutral.mutator.adrenal-surge` | Mutator_08_ForestGreen.png | Mutator_08_ForestGreen_Full.png | Existing sprites wired |
| `neutral.mutator.metabolic-overdrive` | Mutator_09_Jade.png | Mutator_09_Jade_Full.png | Existing sprites wired |
| `neutral.mutator.predator-reflexes` | Mutator_10_Mint.png | Mutator_10_Mint_Full.png | Existing sprites wired |
| `neutral.mutator.neural-accelerator` | Mutator_11_Turquoise.png | Mutator_11_Turquoise_Full.png | Existing sprites wired |
| `neutral.mutator.rapid-synapse` | Mutator_12_Cyan.png | Mutator_12_Cyan_Full.png | Existing sprites wired |
| `neutral.mutator.hunter-glands` | Mutator_13_SkyBlue.png | Mutator_13_SkyBlue_Full.png | Existing sprites wired |
| `neutral.mutator.enhanced-senses` | Mutator_14_Cobalt.png | Mutator_14_Cobalt_Full.png | Existing sprites wired |
| `neutral.mutator.wanderer-strain` | Mutator_15_Indigo.png | Mutator_15_Indigo_Full.png | Existing sprites wired |
| `neutral.mutator.chameleon-tissue` | Mutator_16_Lavender.png | Mutator_16_Lavender_Full.png | Existing sprites wired |
| `neutral.mutator.fortunate-genome` | Mutator_17_Amethyst.png | Mutator_17_Amethyst_Full.png | Existing sprites wired |
| `neutral.mutator.ghost-genome` | Mutator_18_Plum.png | Mutator_18_Plum_Full.png | Existing sprites wired |
| `neutral.mutator.hunter-genome` | Mutator_19_Magenta.png | Mutator_19_Magenta_Full.png | Existing sprites wired |
| `neutral.mutator.reflex-genome` | Mutator_20_Rose.png | Mutator_20_Rose_Full.png | Existing sprites wired |

## Изменённые файлы относительно итогового master

| Path | Изменение | Причина |
|---|---|---|
| Assets/Cards/Neutral/CardCatalog_Neutral.asset | Встроены 20 определений с grants, costs и art/detailArt GUID. | Реальный shared gameplay content; все прежние определения сохранены. |
| Assets/Cards/ResearchProductionCatalog.asset | Три физических Research outputs заменены 20 Mutator entries. | Content-driven Research, без изменений Production. |
| Assets/Cards/IronConcord/CardCatalog_IronConcord.asset | Bio добавлен восьми героям. | Согласованная применимость Hero Mutator к органическим героям. |
| Assets/Cards/TheAshen/CardCatalog_TheAshen.asset | Bio добавлен восьми героям. | Та же блокирующая проблема контента; остальные данные сохранены. |
| Assets/Scripts/UI/EquipmentCardText.cs | Общий plain-value формат; Initiative/Activation AP в тексте, host kind у Mutator; полное Description. | Эти эффекты не помещаются в пять Unit badges; прежний текст не сообщал Unit/Hero ограничение при Bio tag. |
| Assets/Scripts/UI/ResearchProductionModalUI.cs | DescribeCard вызывает общее Description для Equipment. | Панель результата не имеет stat badges, поэтому должна перечислять все эффекты. |
| Assets/Editor/MutatorContentTests.cs | 33 EditMode test cases читают actual content и проверяют grants/compatibility/projections/minting/UI. | Проверки требований и реальных hero targets; отдельный native import witness. |
| Assets/Editor/MutatorContentTests.cs.meta | GUID тестового файла. | Unity import identity. |
| docs/mutator-research-content-audit.md | Этот анализ, actual content/art mapping, результаты и ограничения проверок. | Полный reviewable отчёт. |

Реализация двух слотов уже содержится в итоговом master и не является повторной core-доработкой данного PR. Её полный audit: docs/mutator-attachment-slot-audit.md.

## UI и regression confirmation

Research selection использует прежний ResolveFor → SetupPreview. CardFace сообщает обязательный Bio, Unit/Hero, навыки и недостающие badge stats. Unit badges продолжают показывать Defense/HP/Move; Initiative и Activation AP — текстом. Hero Fate использует прежний Fate badge. Result detail теперь сообщает полный EffectSummary. Нет второго форматтера Mutator или lore-only descriptions.

Hand CardUI, ArmyUnitCardUI, BattleGridCellUI и Army detail используют подготовленные в master поля/двухслотовые previews. EquipmentArtToggle остаётся общим renderer. В коде прослежены пути к Research, свободной/attached карточке в руке, Army card/detail и Battle card. Новое позиционирование/полировка Unity UI не выполнялись. Interactive правильность и читаемость нужно проверить в редакторе.

ResearchProduction logic unchanged; Challenge unchanged; Production unchanged; Equipment slot/grants unchanged; Equipment evaluator unchanged; WorthIt unchanged; AI balance unchanged. Перечисленные механики сравниваются с итоговой базой c413e785. Новый набор использует те же generation/materialization/attachment calls; coefficients/thresholds не редактировались. Технические routing/cache исправления второго слота находятся в master, а не в этом content change.

## Проверки

- MutatorContentTests: **32 внешних cases passed**, один native Unity import/resolution/art test pending.
- Differential NUnitLite: baseline **813 passing cases**, current **845**; ни один baseline pass не потерян.
- Внешний полный запуск не является зелёным Unity suite: baseline имеет 461 native/stub/existing failures, current 462 (добавлен один native import witness). Сохраняются известные ограничения UnityEngine reference DLL / Mono. Они не объявляются успешно выполненными тестами.
- Reference compilation: 27 различных existing diagnostics на immutable upstream baseline и current, **0 новых**. Native Unity сборка не запускалась. Для совместимого внешнего тестового build использованы прежние временные копии net472/Canvas/Mathf stubs; repository gameplay sources и baseline не патчились ими.
- Полная YAML-структура разобрана: 20 definitions действительно находятся в cards, IDs/ключи уникальны, enum values/packed Bio/hostKinds верны, grants/costs/art GUID сверены с actual data. Метаданные обоих изображений на карту используют spriteMode=1.
- Все 24 прежние Neutral definitions, armyNamePool и ссылки catalogs сохранены. Production data идентичны upstream. У органических героев изменён только добавленный Bio; Vessels не изменён.
- verify_full.js и verify_types.js проходят для четырёх изменённых .asset. Проверки трёх inherited UI prefabs также проходили при первоначальном подключении framework; новых prefab edits здесь нет.
- git diff --check: clean.

## Оставшиеся проверки и работы

В Unity **6000.5.4f1** выполнить полный EditMode suite и UnityImportsActualDefinitionsAndResolvesAllPlayableFactions; открыть Research для трёх playable factions; проверить art/detailArt и читаемость Initiative/AP текста, применение к Bio Unit и органическим героям, отказ механическим, оба indicator previews в Hand/Army/Battle и расход creation stake + последующее attach AP.

Отдельными задачами остаются финальное визуальное позиционирование, AI evaluator audit на реальном наборе, gameplay balance iteration и удаление Resistance. Изображения уже подключены; их генерация/замена не входит в этот change. Слияние в master не выполнено.
