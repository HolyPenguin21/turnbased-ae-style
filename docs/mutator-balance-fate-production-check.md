# Мутагенные уровни и проверка производственного скора

Дата: 2026-10-19. База: `c2b67694d5a516be266c8382f35c4f0d9e05e1da`.
Масштаб: согласованное изменение баланса данных и проверки; игровой код AI не изменён.

## Согласованный баланс

- Сложность 3: Dermal Plating, Metabolic Overdrive, Hunter Glands, Chameleon Tissue,
  Reinforced Skeleton. Эффекты и цены сохранены: первые четыре стоят 2 ресурса,
  Reinforced Skeleton — 3.
- Reactive Marrow и Pain Suppression: сложность снижена с 5 до 4, эффекты и цена
  H1 E1 сохранены.
- Сложность 5: Hyper-Regeneration (H3 T1), Enhanced Senses (H1 E1 T1),
  Wanderer Strain (H1 E1 T1). Эффекты и цены сохранены.
- Survivor Strain остаётся сложностью 6; его баланс в этой задаче не менялся.

| Геройский геном | Сложность | H | E | M | T | Эффект |
|---|---:|---:|---:|---:|---:|---|
| Fortunate Genome | 4 | 1 | 1 | 0 | 2 | Fate +2 |
| Ghost Genome | 4 | 1 | 2 | 0 | 1 | Fate +1, Stealth4 |
| Hunter Genome | 4 | 2 | 1 | 0 | 1 | Fate +1, r1s4 |
| Reflex Genome | 4 | 2 | 0 | 0 | 2 | Fate +1, RapidReaction |

У Ghost удалён Move +1. У всех четырёх суммарная цена 4; дальнейшее предложение
сделать всем по одной технологии не применялось. Итоговые количества мутагенов
по сложности: 3 — 5 карт, 4 — 11, 5 — 3, 6 — 1.

## Применение Fate и актуальность оценки

1. EquipmentSystem.ApplyAttachments обновляет Fate/FateMax с сохранением уже
   потраченного Fate; существующие TryAttach являются платёжным входом.
2. NonCombatPlayExecutor сообщает StateChanged и публикует WorldDeltaLifecycle;
   TempoActionExecutor.ExecuteNonCombatSurplus вызывает RefreshOperationalState.
3. WorldAnalysis заново строит Self и Development. GenerationSource.Enumerate
   получает вероятность через EstimateSuccessChance от текущего героя.
4. OperatorForecastFacts включает живой Fate источника; RecipientFacts включает
   Fate/FateMax и идентификатор Mutator. Fingerprint и RecipientVerdictKey
   меняются, поэтому прежняя оценка не переиспользуется после усиления.
5. MaterializationCandidateBuilder повторно разрешает живой GenerationSource
   перед карточным отбором и вызывает тот же ScoreGeneratedEquipmentUpgrade,
   что DevelopmentOpportunityEvaluator. Оценка не фиксирует старый шанс.

Оплата не менялась: проверка источника использует StrategicSpendability;
прогноз Fate использует SpendableStockpile/ForecastActionPoints. План READY
финансирует попытку целиком, а последующее применение учитывается отдельно.
Изменение Fate не даёт разрешения расходовать чужие резервы.

## Проверка достижимости сложности 5

Семь новых параметризованных случаев читают настоящие определения из каталога,
применяют Fortunate Genome к Vector Vance / Nova Sterling и считают READY-план
через MaterializationPlanFactory и StrategicCardEvaluator. Шанс сложности 5
при Fate 3 → 5: 0.36328125 → 0.623046875.

Условия: один указанный получатель в руке, пустая оставшаяся колода, 20 AP,
засвидетельствованная нагрузка 2 AP, по 30 свободных ресурсов и доход по 3.
Противники также взяты из реальных определений. Это свидетели достижимости,
а не обещание изготовления в любом состоянии партии.

| Фракция / мутаген | Получатель / противник | Скор до | Скор после |
|---|---|---:|---:|
| Concord / Hyper-Regeneration | Light Infantry / Deadeye | 0.0611 | 0.3751 |
| Concord / Enhanced Senses | Light Infantry / Deadeye | 0.0759 | 0.3729 |
| Concord / Wanderer Strain | Atlas / Ash Runner | 0.1193 | 0.4535 |
| Ashen / Hyper-Regeneration | Deadeye / AT Cannon | 0.5041 | 1.1380 |
| Ashen / Enhanced Senses | Deadeye / AT Cannon | 0.5217 | 1.1405 |
| Ashen / Wanderer Strain | Deadeye / Light Infantry | 0.1998 | 0.5884 |
| Concord / Wanderer Strain | Light Infantry / Iron Husk | -0.0631 | 0.1345 |

Порог devEvMargin = 0.05. Последний случай проверяет переход через порог именно
благодаря Fate. Все случаи также проверяют изменение ключей, отсутствие расхода
ресурсов при чтении и отказ повторно оценивать неизменившегося получателя как
полезное улучшение. При недостаточной выгоде дорогой мутаген может остаться
невыгодным даже после +Fate; коэффициенты ради гарантированной покупки не менялись.

## Проверки и ограничения

- Оба unity-yaml-verify проходят. Семантическое сравнение с базой допускает
  изменения только шести перечисленных мутагенов; остальные данные совпадают.
- Синхронизированы устаревшие ожидания MutatorContentTests с уже существующим
  каталогом и новым балансом. Это не дополнительные изменения эффектов карт.
- Внешняя проверка на Mono/reference DLL с одинаковыми адаптерами на неизменной
  базе и рабочей версии: 240 случаев / 187 pass → 247 / 208 pass. Новые 7/7
  проходят, исправлены 14 устаревших проверок; ни один исходный pass не потерян.
- Внешний прогон не зелёный: сохраняются 36 ошибок среды/движка и 3 исходных
  падения (ConsumedEnergyScarcityChangesFinalScoreWithoutChangingChainCost,
  OnlyANonCommanderHeroOfALedFieldArmyIsDetachedForDelivery,
  GeneratedOperatorStepKeepsTheChallengeUndiscountedAndExcludesTheLaterDeployment).
- Внешняя компиляция тестовой сборки без диагностики на базе и рабочей версии
  не заменяет Unity: использованы reference DLL, заглушки и адаптеры только во
  временных копиях. Игровые файлы ими не патчились.
- Полный EditMode suite, импорт ассетов, живое применение/оплата и повторный выбор
  производства в том же ходу остаются проверками в Unity 6000.5.4f1.
