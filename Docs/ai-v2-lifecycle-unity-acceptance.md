# AI V2 lifecycle: приёмка в Unity на стороне владельца

Source delivery: PR #148, ветка `refactor/ai-v2-lifecycle-ownership`. Master не мержить автоматически. Зафиксировать точный SHA проверяемой feature-ветки в результатах.

Unity проекта: **6000.5.4f1 (d550df8bd089)**; Test Framework **1.7.0**. Проверки ниже здесь **не запускались**. По поручению владельца native Unity acceptance не блокирует доставку исходников.

## 1. Полный Editor suite

- [ ] Checkout feature-ветки; импорт проекта и компиляция без новых ошибок.
- [ ] В Test Runner выполнить весь AI EditMode suite, включая UnityTest и TestCaseSource. Не ограничивать запуск новыми fixtures.
- [ ] Сохранить NUnit XML, Editor.log и AiDebug.log с SHA и версией Unity. Использовать принятую в проекте папку Logs.
- [ ] При ошибке сравнить тот же тест на исходном master `706b1bbddee9abe4c4f052caea353bc3a790a332` и на текущем master; прежние failures отдельно от новых.

Минимум существующих fixtures: AiV2ArchitecturalRegressionTests, AiReservationInvariantsTests, AiEconomyReservationLifecycleTests, AiEconomyContinuityAuditTests, AiEconomyOwnershipTests, AiTurnResourceBookTests, AiStrategicSpendabilityApTests, AiRaidActorCommitmentTests, AiRaidIntentStateTests, AiAttackLaneTests, AiAggressionOwnershipRegressionTests, AiHousekeepingMissionContractTests, AiReconTrimEligibilityTests, AiReconContinuityPayloadTests, AiReconAirLifecycleTests, AiAviationSortieCycleTests, AiDevelopmentDecisionPathTests, AiDevelopmentReadmissionTests, AiCombatCacheLifecycleTests, AiRouteCacheIsolationTests, AiTaskScoreAllocatorRegressionTests.

Новые fixtures: AiTurnSessionIsolationTests, AiPersistentStateSeparationTests, AiMissionLeaseLifecycleTests, AiMissionStepResultTests, AiWorldDeltaTests, AiLifecycleParityTests, AiLifecycleParityMatrixTests, AiDomainTransitionParityTests, AiMissionStepPayloadTests, AiLifecycleIngressTests. Проверить все cases, включая frozen parity matrices и будущий незарегистрированный mission kind.

Доступный здесь managed gate: **1665 cases, 1140 passed / 525 неизменённых baseline failures**, все **156 добавленных cases passed**, без потери ранее проходивших случаев. Он не запускает native coroutine/asset проверки и не является успешным Unity suite.

## 2. Репрезентативные игровые сценарии

Для сравнения использовать одинаковые карту, начальное состояние, колоду, настройки и seed, если он предусмотрен проектом. Записать состояние intent, actor, bank, revision и pending facts на перечисленных переходах.

| Сценарий | Проверить цепочку | Критерий |
|---|---|---|
| Recon | назначение → движение → новые сведения → refresh → следующее решение → продолжение/завершение | новые сведения доступны следующему решению; durable role и waypoint завершаются по прежней политике |
| Economy FoundBase | demand → builder/escort → защита ресурсов → доставка → стройка → ReturnBuilder/completion | прежние расходы и порядок; завершённый owner освобождён; заёмный actor возвращён прежнему owner |
| Attack | mobilization → preparation host → demand → reinforcement/materialization → прежний threshold → march → assault | roster, readiness и targets совпадают; handoff не освобождает продолжающуюся кампанию |
| Raid | assault → reinforcement → handoff → support return → continuation/return | support и main ownership разделены; завершённая leg не убивает durable intent |
| ActiveDefence | intercept; отдельно return/regroup | прежнее решение и расходы; terminal operation освобождает только свои claims |
| Development | PREPARE → facility/operator → generated output → позднее attachment/consumption | прежние uniqueness/age/reconciliation; output сохраняется между ходами, пока domain rule требует |
| Production / Housekeeping | обычное производство; fold/swap/transfer/reorder claimed и свободных actors | прежние legal/illegal действия и расходы; ArmyMutationContract соблюдается; успешная мутация versioned, отказ no-op |
| Aviation | launch → strike/recon → return; rebase с гибелью wing на первом полёте; launch rollback | endurance/AP/Energy не меняются; каждая committed action учитывается один раз; отсутствие actor не теряет launch receipt; rollback не повышает revision |

## 3. Bank: отдельный проход

- [ ] Несколько одновременных Economy obligations: release/abort одного owner не меняет rows другого.
- [ ] Completion → deferred downgrade и deferred → completion upgrade; повторная заявка одного cost не создаёт дублирование.
- [ ] Abort, invalidation и same-turn retry: освобождённые ресурсы снова spendable там, где это предусмотрено прежней политикой.
- [ ] Phase A → allocator → materialization/provisioning → execution → Phase B → reaction: сверить physical stock, active protection, spendable stock и реальные расходы.
- [ ] Development staged production и Attack preparation продолжают пользоваться банком.
- [ ] End turn и next turn: AP hold не переживает ход; сохраняется только предусмотренная domain protection H/E/M/T.

Не считать deferred saving protection реальным debit: по существующему дизайну её requested amount может превышать stock. Этот refactor не вводит новый bank cap. Проверять прежние coverage/spendability правила, отсутствие двойного debit и чужого release.

## 4. Cache / revision: отдельный проход

Для movement, resource spend, card play, infrastructure, recon discovery, battle/event и Housekeeping пройти mutation → receipt → revision → dirty facts → refresh → next read.

- [ ] Committed transaction: revision +1; no-op/rollback: без повышения; child + aggregate не повышают дважды.
- [ ] Несколько отдельных действий внутри coroutine имеют отдельные receipts; поздняя action не скрыта старым StateVersionAfter.
- [ ] Historical WorldDeltas при normalization/settlement не публикуются повторно.
- [ ] WorldSnapshot, hand/resources, force/capability, enemy contact, infrastructure, route и combat cache читают актуальные facts.
- [ ] Причины и evidence invalidation сохраняются отдельно: consume Actor не уничтожает Capability/Resources/Hand/Infrastructure.
- [ ] Нет transaction scope, удерживаемого через coroutine yield.

## 5. Session / lifecycle / ownership

| Понятие | Authoritative owner |
|---|---|
| turn lifetime / Recon temporary state | AiTurnSession / её ReconTurnState |
| durable intent | MissionIntentState |
| Economy suppression / generated Development operators | соответствующий persistent domain owner |
| actor ownership | MissionLeaseBook; ActorCommitments — view |
| resources | MissionLeaseBook API, один ledger row store |
| operation identity | MissionIntentKey; ReservationOwner — его типизированная проекция |
| common disposition | MissionStepResult, определяемый один раз на result boundary |
| domain transitions | существующие domain partials MissionContinuityLayer |
| revision / fact publication | WorldDeltaLifecycle |

- [ ] Новый player/turn не видит прежние AP reservations, Recon used/trim actors и tentative pass claims.
- [ ] Durable intents, Economy suppression и Development output переживают смену хода по прежним правилам.
- [ ] Оборванная coroutine / следующий turn закрывает прежние session/pass handles.
- [ ] Progress/Waiting/Replan сохраняют законное durable ownership; операция после terminal transition не оставляет claims/rows.
- [ ] Completed leg, waypoint и campaign retirement различаются по прежним domain policies.
- [ ] Stale turn producer не заменяет current-turn pending facts и не меняет revision.

## Результат приёмки

Записать: feature SHA, Unity version, полные test counts и файлы XML/log, прошедшие сценарии, новые failures (если есть), baseline comparison и конкретный первый расходящийся lifecycle transition. До этой проверки native parity остаётся неподтверждённой; source delivery не задерживается.
