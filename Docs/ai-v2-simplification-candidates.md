# AI V2 — кандидаты на упрощение архитектуры

Составлено 2026-09-24 по итогам разбора лога `Logs/AiDebug.log` (5 ходов, Vashti/Cassia/Sable) и фикса
`e67ec92a` / merge `8083526c` (master). Это список задач, а не архитектурный документ: каждая задача —
отдельная сессия/ветка, одна за раз.

## Контекст

- Код: `Assets/Scripts/Ai/V2` — 198 файлов, ~60k строк. Карта слоёв и канонические точки —
  `Assets/Scripts/Ai/V2/ARCHITECTURE.md` (Folder taxonomy, Dependency direction, Canonical seams).
- Слои в целом разделены корректно; избыточность — в дублирующихся моделях одних и тех же понятий,
  многостадийных жизненных циклах, повторных проходах и истории ревью внутри кода.
- Последний фикс (`e67ec92a`): `StrategicSpendability.FitsSpendableForEconomyCompletion` — экономическая
  стройка, которая достраивается СЕЙЧАС, не считает чужие `EconomyDeferredBuild` старшими. Отложенный
  резерв теперь защищает H/E/M/T только от не-экономических трат (Phase B, карты, Reaction).

## Правила выполнения (обязательны)

- Перед любой правкой — сверка с текущим кодом; убедиться, что метод единственный для этой логики,
  нет дублей классов/методов.
- Решение изолировано в слое-владельце, архитектура не растёт вертикально, правки дополняют
  существующие методы. Уровни называть по `ARCHITECTURE.md`.
- Для каждой задачи: ASCII-граф логики и граф зависимостей, пройти оба в обе стороны до конца.
- Сначала шаг «без изменения поведения», изменения поведения — отдельным шагом с обоснованием.
- Тесты и прогоны в Unity делает пользователь, это не блокирует доставку. Сборка:
  `dotnet build Assembly-CSharp.csproj --no-incremental` должна быть 0/0.
- Перед реализацией — согласовать план с пользователем.

## Кандидаты

Риск: **низкий** — поведение не меняется; **средний** — меняется локально; **высокий** — меняет
арбитраж/порядок хода.

### C1. Мёртвый legacy-резерв `AiResourceReservation` — риск A: низкий, B: средний

Факт: `Game.Ai.AiResourceReservation.V2ExtraReservation` **нигде не присваивается** (только `Clear()`
ставит null). Значит `Available()` == сырой склад, а «legacy recon-air protected pool» из комментариев
`StrategicSpendability` не существует: ветка `legacy` в `SpendableAmount`/`SpendableWithRecovery`
ничего не вычитает.

Читатели: `StrategicSpendability.cs:97`, `CardPlayExecutor.cs:128`, `PlacementRules.cs:63`,
`AiAirSortiePlanner.cs:974` (Energy на вылет), `AiTurnController.cs:676` (Energy активации),
`PreTurnCapacityAnalysis.cs:146`, `CardCostRules.cs:14` (комментарий), `MaterializationDiagnostics`.

- **A (без изменения поведения):** убрать хук и понятие «legacy pool»; `CanAffordCardPlay` → одна
  игровая проверка стоимости (есть `CardCostRules`); исправить комментарии в `StrategicSpendability`.
  **Сделано 2026-09-24** (ветка `refactor/c1a-remove-ai-resource-reservation`): класс удалён,
  `CardCostRules.CanAffordPlay`, `SpendableWithRecovery(ownerAware, recovery)`.
- **B (поведение):** проверить стратегические решения, которые читают сырой склад в обход
  owner-aware ledger (`AiAirSortiePlanner:974`, `AiTurnController:676`): могут ли они потратить
  Energy, зарезервированную Reaction/Economy. Если да — перевести на `StrategicSpendability`.

### C2. `AxisBudgetLedger` — второй AP-пул с устаревшими аргументами — риск A: низкий, B: высокий

Факт (шапка `State/AxisBudgetLedger.cs`): после модели радара #1a пул один, аргумент `DesireAxis`
«accepted and ignored», удаление помечено как follow-up. Одновременно AP учитываются в
`ResourceAllocator` (свой пул/_lockedClaims), в `StrategicResourceReservationLedger` (AP-строки
Reaction/EconomyBuildCompletion) и в `ClaimedAp` Provisioning.

- **A:** удалить игнорируемые `DesireAxis`-параметры (вызовы: `Balance`×11, `ReservedFollowup`×7,
  `DiscreteAdmissionBudget`×3, `Debit`×3, …), логи оставить с осью как телеметрией.
- **B (только анализ сначала):** карта всех AP-учётов за ход и можно ли свести к одному пулу +
  ledger. Реализация — отдельным решением пользователя.
- **C2-A сделано 2026-09-24** (ветка `refactor/c2a-axis-ledger-drop-axis-args`): оси убраны,
  удалены мёртвые `Initial`/`CommitDiscreteFollowupBorrow`/`ApDebited`, класс переименован
  `AxisBudgetLedger` → `ApBudgetLedger` (исторические docs не правились).

### C3. Жизненный цикл резерва экономической стройки — риск средний/высокий

Писатели `EconomyDeferredBuild` / `EconomyBuildCompletion` (все через единственный
`InfrastructureFulfillment.ReserveEconomyCost`):
`ReserveDeferredEconomyResources` (first-sight деманд, one-turn horizon),
`…ForPendingHero`, `…ForActiveIntent` (StrategicPhaseA), `CapabilityDeliveryEvaluator:147`,
`ReconcileEconomyCompletionOwner` (downgrade), `ProvisioningManager.Economy:311/531` (completion).
Плюс четыре варианта подготовки стройки через `PlanEconomyCompletion`
(`ProvisioningManager.Economy:271/431/478/516`: direct / garrison-extraction / Shell/Host-tiers / loan)
и синтетические отрицательные id армии.

После `e67ec92a` отложенный резерв влияет только на не-экономические траты. Задача: для каждого
писателя доказать, нужен ли он ещё (какой случай без него сломается — на примере из лога), и свести
к минимуму: один писатель на owner из активных intent'ов + (если нужен) pending-hero.
Документ-предыстория: `docs/ai-economy-mover-materialization-decision-tree.md`.

### C4. Повторные проходы Phase A / DemandLayer.Generate внутри хода — риск средний

Лог 2026-09-24: ~86 проходов Phase A на 15 ходов (~5.7 за ход), ~29 management rounds (~2 за ход).
У Sable T4 проходы давали идентичный результат; есть строки
`strategic re-admission … changed=0`, после которых всё равно идёт management round.
Цель — не производительность, а детерминизм и меньше порядко-зависимых эффектов (строки резервов от
прошлого прохода). Затрагивает `Orchestration/AiStrategyV2Pipeline` — только анализ условий
перезапуска, реализация по согласованию.

### C5. История ревью в комментариях кода — риск низкий (поведение не меняется)

Около 200 строк с маркерами дат/раундов (`2026-09-14 review round 8 (P0)` и т.п.), 12 файлов с
`review round`/`(P0)`. Заменить на описание ДЕЙСТВУЮЩЕГО контракта; историю оставить в git.
Делать по папкам, отдельными коммитами, без изменения кода.
**Сделано 2026-09-24** (ветка `refactor/c5-review-history-comments`, 11 коммитов): все папки V2,
~270 блоков; шапка-«design record» `AiStrategyV2Pipeline` заменена сводкой инвариантов со ссылкой
на ARCHITECTURE.md; убраны ссылки на удалённые V1-типы и `FinishEconomyBuilder`. Только комментарии.

### C6. Объём диагностики — риск низкий (только `Diagnostics`/логи)

5 035 строк / 1.1 МБ на 5 ходов. Топ: `DemandLayer.Economy.AssessEconomyArmy` 597,
`StrategicPhaseA.FulfillDemands` 513, `AiStrategyV2Pipeline.RunTurn` 512, `DemandLayer.Generate` 460.
Убрать повторы идентичных строк между проходами одного хода (dedup по содержимому), не теряя
информации для разбора.
**Сделано 2026-09-24** (ветка `refactor/c6-log-dedup`): дедуп-память `AiDebugLog` сбрасывается на
каждый ход игрока (`AiV2Trace.BeginMain` → `ResetDedupScope`; раньше `WriteDeduped` глушил строки
между игроками и ходами); 16 повторяющихся строк Demand/Phase A/ReconAirCap переведены на
`WriteDeduped`; строки с trace-id (`demand —`, `strat.A … no feasible chain`, `strat.A infra`) —
на `WriteDedupedWithId`: повтор печатается как `Dnn = Dmm (same line as earlier this turn)`.
Оценка по логу 24.09: −13…22% объёма. Не трогалось: `RunTurn` loop-строки (нужны для C4).

## Рекомендуемый порядок

C1-A → C2-A → C5 → C6 (без изменения поведения) → C1-B → C3 (анализ, потом по писателю) → C4, C2-B.

## Открытые баги из того же лога (не упрощение, отдельные задачи)

3. Sable: Rusty Vulture выставлен на T2 с `objectiveCoverage=0` (role +2.60 без условия задачи),
   T3–T5 простаивает при `spareAir=1` и 5 runnable obs — в `ReconAssignmentPlanner.AssignFunded`
   нет воздушных кандидатов.
4. Cassia T5: Elena Hayes — только кандидаты NewArmy (3 AP), нет Garrison (1 AP); Housekeeping сразу
   atomic-fold в гарнизон → −2 AP.
5. Sable T3: Iron Reavers ReturnBuilder (5,-2)→(5,-4), затем к (2,-1); прямой путь 3 хекса дал бы
   постройку в тот же ход.
6. Loop: профинансированная commitment падает на provisioning с `RepriceThisTurn` →
   `admission stopped noProgress` → остальные миссии (разведка) не получают ход
   (`commitments starve fresh`).
