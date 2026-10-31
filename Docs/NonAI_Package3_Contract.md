# Пакет 3: контракт боя и challenge

База: `bf0a4ecf161fe6c98a129a674d488321a5e4eeef`, ветка `refactor/non-ai-package-3`.
Масштаб: изменение архитектуры владения командами и жизненным циклом. Политики выбора решений ИИ и баланс не изменяются. Public entrypoints сохранены через adapters.

## Engine commands

`BattleEngine` владеет actor, фазой, основным действием, реакциями и продвижением хода. `BattleState.Round`, `TurnIndex`, `RetreatingArmy` и список turn order доступны потребителям для чтения; изменяет их engine. Полученный `Reactions` не предоставляет изменяемый набор использованных реакций.

| Команда | Условие и результат |
| --- | --- |
| `StartRound` | Deployment или RoundReady; одна подготовка раунда, heal/Drill и реакции |
| `TryMoveUnit` / `TryPass` | Текущий допустимый actor в Acting; одно основное действие |
| `TryBeginAttack` | Проверяет actor/target/phase, возвращает request конкретного действия |
| `PrepareGroundAttack` | Request принадлежит этому engine и текущему действию; создаёт один plan |
| `ApplyAttackPlan` | Применяет именно этот plan один раз; затем обязательная реакция либо AwaitingAdvance |
| `AdvanceTurn` | Только AwaitingAdvance; не пропускает ожидающий counterattack |
| `CommitRetreat` / `CompleteRetreat` | Объявление до раунда, grace round противника, одно завершение отступления |
| `CompleteBattle` | Завершённый бой и разрешённая обязательная Capture/Kill очередь |

`ResolveAndApplyGroundAttack` и фиксированный `ApplyGroundAttack` остаются совместимыми входами, но используют ту же проверку действия, plan, реакции и завершение SingleUse. Counterattack не порождает следующий counterattack. Shock сохраняет существующее подавление реакции/хода.

## Снимок правил и результат атаки

`BattleRulesSnapshot` фиксирует AbilityMagnitudes, terrain и building defense при создании encounter. Реальный UI передаёт map в engine. Formation вычисляется по текущей grid при подготовке атаки. Ground explosion использует те же encounter magnitudes в session, применении и снятии временных Berserk stat changes.

`BattleAttackRequest` связывает отложенное решение с конкретным действием. `BattleAttackPlan` копирует принятые tokens/Fate, фиксирует Bodyguard, damage, effects и случайные collateral targets. Повторный prepare возвращает тот же plan и не расходует RNG; повторный apply возвращает `Applied=false`. Popup показывает этот результат без собственной Bodyguard projection.

`BattleAttackApplication` содержит факт применения, outcome, victim, damage, removals, secondary hits и ожидаемого counterattacker. Применённый standalone miss также имеет `Applied=true`. RawHit означает положительную разницу successes до ability modifiers; PositiveDamage означает damage > 0 после modifiers. Старое `HitProbability` сохраняет значение PositiveDamageProbability. Вероятности используют общую binomial primitive kernel.

## Challenge sessions и Fate

`BattleChallengeSession` владеет начальным roll, copied tokens, локальной Fate, tactics, defender-first decision windows, accept и cancellation. Повторный Roll не перебрасывает; повторный запрос незакрытого окна не переключает сторону; spend другой стороны отклоняется. Возвращаемые tokens и roll receipts не позволяют переписать session.

`ResearchProductionChallengeSession` владеет roll, local Fate, miss-first reroll/overflow, tactics и замороженным результатом Accept. `ResearchProductionSystem.RollChallenge` использует этот же механизм с прежней политикой расхода. Popup не записывает временную Fate в живого героя: accept/loss/cancel/close/disable/exception оставляют исходную Fate и сохраняют независимые внешние изменения.

`ProductionAttempt` из пакета 2 остаётся владельцем оплаты, квоты, CompleteToHand и выпуска карты. Popup передаёт только success/failure. Терминальный callback отделён до закрытия UI; повторное закрытие не завершает новую попытку. При вложенном Begin новое вложенное представление сохраняет владение, внешняя superseded R/P попытка получает false один раз. Ошибка callback не оставляет старый session или busy gate.

## Apply / Acknowledge encounter

`BattleEncounterCoordinator.Apply` выполняет aftermath через существующие операции мира пакета 1 и кэширует receipt до публикации completed notifications. Сюда относятся casualty cleanup, summons stripping, retreat, capture/building handover, prison и guards. `CompleteBattle` и `CompleteRetreat` вызывают Apply до последнего outcome OK. UI не является единственным владельцем этих последствий.

`TryAcknowledge` при необходимости вызывает Apply и выдаёт разрешение на continuation ровно один раз. Повторные Apply/Finalize/ack не пополняют Fate повторно, не дублируют захват, event reward либо следующий encounter. Старый `FinalizeEncounter` делегирует coordinator. Continuation использует существующие delayed-battle/event rules.

Winner определяется до stripping summons; после stripping нужен persistent survivor. Hero + summons сохраняет hero army, summons-only не оставляет survivor. Capture/Kill очередь строится из фактического terminal status. Ранний пустой запрос не кэшируется; устаревшие alive flags не обходят обязательного героя. `TryResolveCurrent(expectedTarget, ...)` отклоняет callback предыдущего героя.

Граница требования 3.3 — aftermath encounter. Исходный момент применения индивидуального hit и Capture/Kill после подтверждения их результата сохранён. Закрытие непринятого challenge отменяет его; это не новое автоматическое решение за игрока. Это уточнение принято при независимом ревью.

## Авиация

`AviationCombatOperation` владеет AA choice/fire/skip, расходом AA, targeting, damage, roster cleanup и Explosion/SingleUse disposal. Она выдаёт `AaDecisionRequest` и `ChallengeRequest`; пока обязательный request не закрыт, операция не проходит дальше. Повторные и поддельные callback не применяют damage.

`AviationCombatHeadlessAdapter` обслуживает те же requests и sessions. Presenter только показывает решения и результаты; при отсутствии popup принятый AA fire выполняется headlessly, а не превращается в skip. Сохранены public entrypoints, AirStrikeResult и HeadlessRng. AirStrikePolicy перемещён без изменения решений; в ai-types.json изменён только путь его исходника, не поля или save schema.

## Проверка восстановленной версии

Среда: полный runtime source компилируется в отдельном managed harness против legacy Unity reference DLL и внешних compatibility stubs. Это не доказательство сборки в текущем Unity.

| Проверка | База | Изменения |
| --- | --- | --- |
| Combat/Cards и WorldMutationContract/VisionContainer, без UnityNative | 648: 409 passed / 239 failed | 702: 463 passed / те же 239 failed |
| Отдельный повтор списка passing cases | — | 463 / 463 passed |
| WorldMutationContract/VisionContainer внутри общего прогона | 39 passed | 39 passed |
| Новые регрессии по имени и результату | — | 0 |

Неизменившиеся ошибки базы: 2 assertion failures, 235 ошибок reference/native/content среды, 2 invalid tests. Полный список и XML включены в контрольную копию. DeckCollectionTests требуют отдельных editor tools и не входят в эту выборку. Нативные Combat/Cards popup lifecycle fixtures компилируются в harness, но не запускались вне Unity; Extended movement fixture требует настоящего Unity runner. Исходные fixtures ProductionFatePreviewPlayModeTests и BattleHitLifecycleTests отсутствуют в этой базовой ревизии; вместо их восстановления добавлены актуальные session/ownership/popup проверки.

Новые сценарии проверяют прямой wrong actor/phase, one-shot plan/RNG, обязательный counterattack, grace round, Capture/Kill stale callbacks, Apply/ack порядок, frozen explosion magnitudes, R/P Fate terminal paths и авиационные fire/skip/roster результаты при одинаковых решениях и бросках. Legacy mechanics fixtures используют reflection-only BattleEngineTestDriver для искусственного порядка hit; реальные public commands проверяет отдельный BattleCommandOwnershipTests.

Независимое ревью выявило и повторно подтвердило устранение трёх ошибок: provisional Capture/Kill cache, explosion magnitudes вне snapshot и reentrant Begin с потерей ProductionAttempt completion. Существующие asset GUID/scene/prefab ссылки не менялись; новые scripts/tests имеют .meta.

Осталось выполнить в Unity 6000.5.4f1: полный EditMode suite, popup disable/reentry/exception и coroutine сценарии, UI wiring и интерактивный бой. Unity Editor в этой среде отсутствует. Результаты выше зафиксированы до публикации изменений в master.

Повторный bottom-up audit и исправления оставшихся границ: [NonAI_Package3_BottomUp_Audit.md](NonAI_Package3_BottomUp_Audit.md).
