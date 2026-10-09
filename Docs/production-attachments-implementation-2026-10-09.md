# Equipment / Mutator: реализация после рефакторинга AI

Дата: 2026-10-09. Проверенная база: `5f06cb1ead2e6c33d676564a11238ed707c8012c`.
Исходная база анализа: `66bb3c34626814c98e953442a2ed7829f8e43cd7`; дополнительный
master-коммит удалял устаревшую документацию и не менял исполняемый код.
Ревизия реализации до добавления этого отчёта: `0fff5aec64d1c9f574210b8d56c7c9677b8b9a6d`.

Масштаб: изменения поведения в существующих владельцах и ровно 12 изменений
данных. Новых классов, менеджеров, осей, кешей и счётчиков версий нет.
TurnLoop, DecisionFrame, StrategicReadmissionRunner и новые компоненты работ
цикла сохранены. Слияние в master не выполнялось.

## Перенос PR #156 и дополнительные исправления

Перенесены поведения трёх коммитов `2268d64`, `0ffa905`, `19690be`:

- StrategicCardEvaluator оценивает исходы по отдельности:
  `p * price(successful full chain) + (1-p) * price(attempt only)`.
  Сначала применяется нелинейная цена ресурсов каждого исхода, затем вероятность.
  Штраф шагов учитывается один раз; ApCost/ResCost не уменьшаются вероятностью.
  В READY успешный исход включает цену отложенной установки, но текущий
  банковский этап по-прежнему оплачивает только создание.
- Generated attachment получает тот же RepeatFactor, что READY. Скидка касается
  нового изготовленного вложения; ранее купленный предмет из руки не получает её
  при изготовлении тела. Отрицательный EquipmentDelta сохраняется.
- MaterializationExecutor.TryGenerate пишет одну попытку после TryStartAttempt и
  до RollChallenge. Дублирующая запись в StrategicPhaseA удалена, счётчики и
  использованные ключи сохранены. Бесплатный проигрыш сообщает StateChanged.
- ScoutCostModel возвращает конкретную ExecutionHex и умеет требовать именно
  указанного Scout. StrategicCardEvaluator использует его маршрут и честный
  ScoutRiskModel; Refresh учитывает позицию наблюдения. Чтение не резервирует AP
  и не меняет назначений.
- Изменения старого AiStrategyV2Pipeline.DevelopmentAdmission перенесены в
  DevelopmentAdmission.Facts. Старый pipeline-файл не восстановлен. Ссылки
  перенесённого теста обновлены на DevelopmentAdmission.Facts.
- ArmyActions владеет ценой выкладывания по конечным способностям;
  CardCostRules делегирует ему, PlanFactory передаёт проекцию двух вложений.
  RapidReaction обнуляет выкладывание, сохраняя цену создания контейнера,
  изготовления, установки и ресурсы.

При дополнительной проверке закрыты разрывы перед необратимой оплатой:

- MaterializationFeasibility.PreflightIfExisting теперь проверяет также
  generated-цепочки и вызывается MaterializationExecutor до TryGenerate.
  Проверяются наличие тела/предмета в руке, совместимость, целевой слот,
  регистрация армии-получателя и текущая возможность размещения.
- CardPlayExecutor.PreflightGenerated использует тот же PreflightCore, сохраняя
  проверки владельца, канонического root, постройки, вместимости и зависимостей.
  Для ещё не изготовленного тела пропускается только наличие в руке и отдельный
  бюджет выкладывания: полный бюджет проверяет владелец цепочки. Пробная карта
  не создаётся, runtime identity и рука не изменяются простым чтением.
- DevelopmentUpgradeFulfillment передаёт действующий demand.SpendAuthority в
  банковский допуск и TryGenerate. Его staged-получатель проверяется до оплаты;
  успешный mint остаётся в руке для отдельно оплачиваемой установки.
- NonCombatCardPlayer.Execute повторно проверяет полную цену успешного этапа по
  SpendableAp/FitsSpendableResources до mint. Раньше непосредственный TryGenerate
  повторно защищал только ресурсы попытки; full-chain допуск оставался у выбора
  TempoCandidateProvider.
- DevelopmentAdmission.RecipientFacts содержит обе authored identity живого
  вложения: равные stats/занятость не скрывают смену предмета.

Коммиты реализации:

| Коммит | Содержание |
|---|---|
| `d001e714` | Интеграция PR #156, общий preflight, передача SpendAuthority |
| `17c1e2bc` | Только 12 изменений каталога и относящиеся к балансу проверки |
| `894dbb97` | Live full-budget guard non-combat и проверки исходов остальных generated-форм |
| `0fff5aec` | Обе live attachment identity в admission и целевой тест |

## Данные

Каталог до правок: blob `06e6ed7e55231b88906fcd40141f1d3e2ff2e862`.
После правок: blob `9e11e0d4` (полный SHA в сопутствующем JSON).
Проверка разобранного YAML против базы подтвердила ровно 12 изменений grant.
Все поля вне grant, hostKinds/hostTypeTags, removeAbilities/clearAbilityFamilies,
ключи, ссылки, типы, слоты, цены H/E/M/T, AP и Challenge совпадают с базой.
Всего опубликовано 62 вложения: 42 Equipment и 20 Mutator.

| Карта | Единственное изменение grant |
|---|---|
| Regenerative Culture | Attack add +1 |
| Hyper-Regeneration | добавить CeramicArmor |
| Survivor Strain | добавить HP add +2 |
| Neural Accelerator | Attack add +1 |
| Enhanced Senses | добавить CeramicArmor |
| Wanderer Strain | Attack add +1 |
| Chameleon Tissue | Initiative add +1 |
| Shock Rifle | Attack add +1 |
| Twin SMG | Attack add +1 → override 8 |
| AA Launcher | Attack add +1 |
| Nuclear Engine | Move add +1 → +2 |
| Assault Conversion Kit | HP add +2 |

Twin SMG применяется перед Mutator: Attack=8, затем Attack+1 даёт 9.
Survivor Strain не имел исходной HP-добавки; итоговая добавка ровно +2.
Проверки Mutator больше не выводят Challenge из суммы новых эффектов: это
отдельное сохранённое authored-поле. Никаких новых боевых статов героям нет.

Все 50 неизменённых вложений (полные ключи и результаты — в CSV):
Dermal Plating; Reactive Marrow; Reinforced Skeleton; Pain Suppression;
Adrenal Surge; Metabolic Overdrive; Predator Reflexes; Rapid Synapse;
Hunter Glands; Fortunate Genome; Ghost Genome; Hunter Genome; Reflex Genome;
Flamer; AT Launcher; Claws; Heavy MG; Plasma Gun; Ballistic Shield; Ceramic Vest;
Assault Rifle Kit; Marksman Rifle; Optical Scope; Mobility Harness; Shotgun;
Grenade Launcher; Incendiary Rifle; Rail Rifle; Portable Mortar; Recoil Cannon;
Armor Plate; Reinforced Chassis; Servo Actuators; Ceramic Plating; Reactive Armor;
Turbocharger; Artillery Cannon; AT VH Launcher; Double Barrel; Plasma Cannon;
Autocannon; HE Cannon; Flame Projector; Rail Cannon; Shock Projector; AA Mount;
Dozer Blade; Siege Ram; Spiked Ram; Mortar Rack.

## Банк: подтверждённая цепочка по коду

1. Enumeration/PlanFactory читают оба слота и каноническую проекцию, формируют
   физические ApCost/ResCost. Ожидаемая цена живёт только в оценщике.
2. MaterializationFeasibility.AddIfFeasibleA учитывает axis budget, follow-up AP,
   SpendableAp и FitsSpendableResources с demand.SpendAuthority. FilterSurplus
   использует ReservesOkAfterChain. PortfolioSolver сохраняет физическую цену.
3. Execute повторяет ReservesOkAfterChain с тем же authority и live preflight
   до изготовления. READY повторяет проверку своего creation-stage плана;
   non-combat повторяет полный бюджет своего этапа.
4. TryGenerate защищает ресурсы через GenerationSource → StrategicSpendability.
   TryStartAttempt повторно проверяет реального оператора, facility, offering,
   принадлежность root и оплату. Только после старта записывается история.
5. Проигрыш оставляет оплаченный stake, не устанавливает и не выкладывает карту.
   Успех mint → необходимые реально выполняемые шаги. Hand boundary удаляет
   предмет/тело только после успешного действия. READY установка — отдельный шаг.
6. Истинные расходы измеряются по PlayerRoot. Чужие резервы исключает
   TurnResourceBook.Free: `physical - outstanding claims`; MayDrawOn разрешает
   только собственный hold или действующее EconomyCompletesNow для deferred
   Economy. Эти владельцы и таблица полномочий не менялись.

В актуальной архитектуре обязательная авиация исполняется до карточных расходов
и ничего не держит в банке. Существующая защита непогашенной активации Hard
операций сохранена. Нельзя объяснять текущий банк старой схемой авиационных holds.
Это подтверждение трассировки кода; фактические суммы в партии не измерялись.

## Кеши: запись → следующее чтение

| Изменённый факт | Запись / stamp | Владелец ключа / снимка | Следующее чтение и проверка |
|---|---|---|---|
| Вложение, оба слота, stats/abilities | TryAttach/ApplyAttachments; enclosing StateChanged/StampAction | EquipmentSystem; RecipientFacts/Facts; DecisionFrame | Новый snapshot; all-62 projection и AttachmentLifecycle tests |
| HP/Fate current/max | EquipmentSystem сохраняет consumed delta | RecipientFacts; combat profile fingerprint | CurrentAfterAttachment / новая оценка; wounds/Fate tests |
| Конечные deploy/activation AP | ApplyAttachments / проектируемые abilities | ArmyActions; PlanFactory; live RecipientFacts | Full budget и следующее размещение; RapidReaction tests |
| Рука / runtime identity | MintCard → AddCard; RemoveCard после успеха | AiHandData.Version; RecipientFacts | Admission Fingerprint, fresh candidate enumeration |
| AP/stock и owner reserves | Canonical gameplay payment / существующий ledger lifecycle | StrategicSpendability → TurnResourceBook; PriceInputs/ApAffordability | Admission, portfolio и live guard с тем же authority |
| История и generator/card use keys | Одна RecordAttempt после TryStartAttempt; consumer counters | HistoryKey; MaterializationReservation/StrategicTempoBudget | RepeatFactor и Fingerprint; отказ не пишет историю, бесплатный проигрыш меняет состояние |
| Состав / позиция / назначения | Canonical gameplay endpoints, intent lifecycle | WorldAnalysis; DecisionFrame, Facts/claims/purposes | RefreshOperationalDecision → следующий consumer |
| Pathing, coverage, blockers, известные detectors, trimmed actors | Действующие map/knowledge/Recon owners | Facts.routeMap, knownTargets, trimmedScouts | ScoutCostModel/ScoutRiskModel; route invalidation tests |

Полная materialization-цепочка использует StampAction(StateChanged,
childAlreadyStamped): deployment уже stamp-ит свою мутацию, агрегат не добавляет
второй stamp. READY использует действующий CommitMutation; non-combat —
StampAction с проверкой текущей версии. После changed Phase A StrategicPhaseA
обновляет знания; StrategicReadmissionRunner.AcceptChangedReentry вызывает
DecisionFrame: knowledge → warm → recon → aggression facts/objectives → intents
→ actors. Затем публикуется observation delta и commit admission fingerprints.
Новых версий/глобального сброса кешей нет. Неизменённый read не пишет состояние.
Native exactly-once и фактическое чтение в партии остаются проверками Unity.

## A1–A20

Во всех строках «тест» означает наличие проверки, **не её успешный запуск**.
NUnit/EditMode в этой среде не выполнялись. Проверки по коду не заменяют Unity.

| ID | Результат / покрытие и ограничение |
|---|---|
| A1 | Добавлен EveryPublishedAttachmentProjectsTheLiveResultWithWoundsAndEitherOtherSlot: 62 authored items, реальные совместимые hosts, оба слота; требует NUnit |
| A2 | Существующий общий Utility; независимая численная проверка одинакового Defense payload в двух слотах прошла; native тесты не запускались |
| A3 | Enumeration и CanAttachPreview проверены; противоположный слот не запрещает кандидат. AttachmentSlotTests покрывает оба порядка/стоимость |
| A4 | Перенесён GeneratedAttachmentDeploymentUsesTheSameRepeatFactorAndHandItemsStayUnchanged; один владелец RecordAttempt; требует NUnit |
| A5 | Перенесён p=0/.5/1 attachment тест; добавлены generated body с/без hand item и non-combat; требует NUnit |
| A6 | Добавлены отказ полной materialization и non-combat цепочки при доступности только попытки; требует NUnit |
| A7 | Traced SpendAuthority → TurnResourceBook.MayDrawOn; существующие AiTurnResourceBookTests / AiStrategicSpendabilityApTests не запускались |
| A8 | Pre-payment source/slot/placement guard; добавлены 4 stale attachment cases; source eligibility остаётся TryStartAttempt; требует NUnit |
| A9 | Перенесены paid win/loss, rejection и free loss tests; history один раз и StateChanged по коду; требует NUnit/партии |
| A10 | Shared attempt transaction, hand boundary и staged READY прослежены; исполнение успешной полной цепочки в Unity не запускалось |
| A11 | Перенесены RapidReaction tests для обоих слотов, обычного/generated тела и innate; прочие затраты сохранены; требует NUnit |
| A12 | Shared Scout route с requirePreferredMover, ExecutionHex Refresh и honest risk; перенесены тесты preferred mover / vantage / fallback; требует NUnit |
| A13 | Facts содержит pathing/coverage/blockers/detectors/actors; route-key и attachment-identity тесты добавлены; требует NUnit |
| A14 | Добавлен authored Twin SMG → 8 → Mutator → 9, Range=1, wounds; requires NUnit |
| A15 | Existing CurrentAfterAttachment и consumed-state mechanics не менялись; общая wounds/Fate проверка и существующие slot tests; requires NUnit |
| A16 | Grant Move+2 и Aircraft compatibility сохранены; модель сохраняет авиационный speed proxy; настоящий sortie/авиационная оценка требует Unity |
| A17 | Prefilter разрешает Unit/Hero, capability проверяется после проекции; новая Unit Scout матрица 96 384 строк на каталог и Hero Scout 33 792 строки прошли |
| A18 | Отдельная модель known route дала 20 Reflex witnesses; нет фиксированного плюса по имени. Достижимость конкретной миссии в Unity не проверялась |
| A19 | Fortunate/Ghost не изменены/не исключены; остальные 50 data records идентичны. Численно ниши Fortunate/Ghost не найдены |
| A20 | YAML и сравнение parsed data прошли, ровно 12 grant-diff. UI читает канонические эффекты; фактический tooltip/render в Unity не проверялся |

## Новое сравнение всех 62 карт

Данные и коэффициенты обновлены из проверенной ревизии; старый запуск по
замороженным входам не выдавался за проверку новой базы. Содержательные строки
Utility, EquipmentSystem и правил Challenge совпали с расчётными исходниками
после нормализации окончания строк/концевой пустой строки. Новый actual-каталог
перепроектирован целиком, включая уже установленный противоположный предмет.

- До балансных правок: 45 READY-ниш; список совпал с исходным аудитом, без потерь
  и новых ниш только от интеграции.
- После: 57 READY = 42 Equipment + 15 Mutator. Все прежние 45 сохранены;
  приобретены ровно 12 изменённых карт.
- На каждом каталоге: 400 773 utility cases, 22 402 recipient/context groups,
  8 015 460 score evaluations; сравнение с доступными конкурентами и отказом,
  rich/typed/restricted budgets — полные owner-authorized средства модели.
- Дополнительные неизменённые ниши: Hunter Glands (new Unit Scout), Hunter Genome
  (new Hero Scout), Reflex Genome (known field route). Итог 60 из 62 в совокупности
  **разных ограниченных численных сценариев**, не в одном состоянии/партии.
- Повторно выполнены 10 независимых проверок: 286 baseline и 195 after-package
  winning witnesses перепроектированы и сопоставлены с конкурентами.
- Fortunate/Ghost доступны, выигрыш не подгонялся. Диагностическая чувствительность
  OperatorOutputs не внедрена; её counterfactual rows в JSON не являются игровыми
  нишами и не учитываются в числе 60.

Файлы рядом: `production-attachments-comparison-2026-10-09.csv` — все 62 строки
с U, p, конкурентом, margin, контекстом и полным доступным бюджетом;
`production-attachments-comparison-2026-10-09.json` — source blob SHA, результаты
и конкретные дополнительные witnesses. Метаданные некоторых архивных скриптов
имеют старый hardcoded revision; авторитетные фактические источники записаны в
верхнем source_blob_shas этого JSON.

Для воспроизведения использовать `Production_Balance_Readiness_62_2026-10-09.zip`:
развернуть две отдельные копии recalculation, заменить Assets/Cards и используемые
Assets/Scripts на blobs интеграционного коммита до баланса и final implementation,
затем в каждой выполнить `recalculate_postfix.py` и `choice_audit.py`.
Для дополнительных сценариев actual changed definitions передаются в
remaining_audit/joint12_effect_trial.json, затем выполняется `remaining_niches.py`;
на actual-catalog копии — `scout_genome_audit.py`. `verify_joint12.py` проверяет
winning witnesses. Не использовать старые CSV вместо нового пересчёта.

## Выполненные проверки и недоступные проверки

- Passed: parsed catalogue comparison, оба unity-yaml-verify, git diff --check.
- Passed: tree-sitter C# syntax tree всех изменённых C# файлов. Это не компиляция,
  binding/type checking или NUnit.
- Passed: указанные Python numerical runs и независимые witness checks.
- Passed: ai-v2-decoupling-verify/selftest.py; это самопроверка проверщиков,
  а не новый recorder trace реализации/партии.
- Не запускались: ai-verify compile baseline/current/test_regress, NUnit,
  Unity 6000.5.4f1 EditMode, загрузка каталога/tooltip, игровой E2E и партийные логи.
  В среде нет dotnet/Mono/Unity. setup.sh завершился с ошибками системных прав;
  повторная установка не завершилась и остановлена. Ложный baseline с нулём
  ошибок не создавался. Базовая ревизия закреплена для последующего сравнения.

Перед merge требуется полный EditMode suite и игровой сценарий: успех mint →
переоценка → установка/размещение, проигрыш → повторный выбор, refusal до оплаты,
owner reserve change, Scout route change и RapidReaction в том же ходу.
Проверить расходы/историю/версии и отображение 12 карточек.
Если master после указанной базы меняет цикл, повторно проверить точки
интеграции: StrategicPhaseA, DevelopmentUpgradeFulfillment, TempoActionExecutor,
StrategicReadmissionRunner и DecisionFrame; совместимость с будущей ревизией
не заявляется.

## Повторная проверка снизу вверх перед публикацией

По запросу владельца повторно прочитана цепочка: EquipmentSystem и ArmyActions →
CardCostRules → PlanFactory/Feasibility → MaterializationExecutor,
DevelopmentUpgradeFulfillment и NonCombatCardPlayer → StrategicCardEvaluator →
StrategicPhaseA/TempoActionExecutor → DevelopmentAdmission и DecisionFrame.
Отдельно проверены GenerationSource, StrategicSpendability/TurnResourceBook,
ScoutCostModel, ObservationVantageSelector, ScoutRiskModel и WorldDeltaLifecycle.
Новых дефектов в затронутой цепочке при этой проверке по коду не обнаружено.
Это вывод статического аудита, а не результат запуска игрового сценария.

Повторно выполнены: разбор синтаксиса всех 22 изменённых C# файлов; сравнение
разобранного каталога с базой (ровно 12 grant, прочие поля неизменны); оба YAML
верификатора; selftest инструментов decoupling; git diff --check; независимый
численный verify_joint12 (10 checks, 286 baseline / 195 after winning witnesses).
Все перечисленные проверки прошли. Компиляция и Unity/EditMode не запускались.

Владелец после этого запросил commit, merge в master и push. Эта команда
заменяет первоначальное ограничение на merge; перечисленные выше проверки
в Unity остаются необходимой последующей валидацией.
