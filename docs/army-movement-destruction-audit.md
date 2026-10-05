# Жизненный цикл движения при уничтожении армии

Исходная ревизия: `c214cbb7bc397871ed8345bce7f68fe0c510e617` (актуальный master при начале работы 2026-10-05). Указанный в задании `440a51a6` уже не был HEAD. Источник — актуальные файлы через GitHub connector; прочитан корневой AGENTS.md.

Масштаб: локальное исправление жизненного цикла с уточнением терминальных результатов исполнителей. Новый менеджер, новый банк и новая архитектура не добавлены. Баланс, логирование и исходные логи не изменены. Merge в master не выполнялся.

## Причина и контракт

В `MoveArmyRoutine` предикат `WaitUntil` снова читает `army.Controller` каждый кадр. Проверка перед созданием ожидания не защищает последующие вызовы. После `DeleteArmyIfEmptied` поле равно null: повторный вызов `army.Controller.IsMoving` выбрасывает NullReferenceException. Весь `MoveArmyRoutine` и внешний исполнитель могут не дойти до строки после yield и штатного учёта результата.

Путь ПВО по актуальному коду: `ArmyController.MoveRoutine` → `AviationCombatPresenter.ResolveAirArmyStep` → `RunAaReaction` → удаление последних участников → `DeleteArmyIfEmptied` → Unregister / Destroy / очистка Controller → следующий вызов предиката AI. Исходный стек не определяет конкретную армию или причину её гибели.

До исправления завершение зависело от того, выполнит ли Unity хвост корутины на уничтожаемом объекте. В коде хвост вызывает callback, который делает `ArmyRegistry.MoveArmy`. Этот метод мог добавить уже снятую с регистрации армию, если callback всё же выполнялся. Возможность такого порядка в конкретной версии Unity **не подтверждена запуском**; исправление исключает её независимо от времени возобновления вложенных IEnumerator.

Контракт после исправления:

1. Контроллер владеет одним приказом от SettleThen до обычного завершения или явной отмены. Он хранит coroutine handles и callbacks этого приказа.
2. Обычное завершение однократно вызывает прежний callback прибытия; CurrentHex остаётся доступен во время callback. Затем очищаются IsMoving и временная политика удара.
3. Отмена снимает callbacks и IsMoving, останавливает движение и easing; она не вызывает прибытие. Живой приказ фиксирует частичный результат через callback отмены.
4. При удалении сначала сохраняется CurrentHex, затем движение явно отменяется. Для пустой армии callback отмены не выполняет перенос.
5. Контроллер отсоединяется, затем реестр одним удалением снимает армию из исходного индекса и записывает хекс гибели в ArmyData.Hex. Уведомления публикуются после этой транзакции. Destroy выполняется после неё.
6. Поздний MoveArmy для незарегистрированной армии — no-op. Callback прибытия дополнительно проверяет регистрацию, состав и принадлежность контроллера.
7. AI безопасно завершает ожидание при CLR-null и Unity destroyed-object null. Оно продолжает ждать открытый бой и возвращается в существующий исполнитель, который измеряет реальные расходы и сообщает MoverLost.
8. Dispose AI-приказа отменяет только выданный им ещё выполняющийся приказ. Его finally очищает политику удара и отписывает EncounterResolved.

Исключение постоянных контейнеров сохранено: неподвижные пустые оболочки у своего Barracks / Airfield продолжают существовать и публикуют изменение состава. Пустая движущаяся армия не становится такой оболочкой из-за устаревшего исходного Hex.

## До / после

| Случай | До | После |
| --- | --- | --- |
| Controller исчез между кадрами ожидания | Повторное разыменование null | Безопасное завершение ожидания |
| ПВО уничтожило все самолёты на промежуточном хексе | Хвост зависит от уничтожения GameObject; Hex может остаться исходным | Явная отмена и сохранённый хекс гибели |
| Поздний callback после Unregister | MoveArmy способен повторно добавить армию | MoveArmy переносит только зарегистрированные армии |
| Уведомление удаления | Предварительное уведомление ещё зарегистрированного пустого объекта; только исходный Hex | Уведомления исходного и фактического хекса после удаления |
| Действия прибытия погибшего исполнителя | Callback не проверяет регистрацию / состав | Отменённый callback не вызывается; поздний callback защищён |
| Наземный бой уничтожил исполнителя | В ряде веток BattleStarted / HexEventStarted имеет приоритет над гибелью | MoverLost с сохранёнными фактом боя, движением и расходами |
| Возврат Economy: гибель точно на цели | Достигнутый Hex мог перезаписать MoverLost в ReachedGoal | Гибель не считается успешной доставкой |
| Воздушная разведка: атомарный шаг закончился гибелью | FinalHex мог остаться исходным; StepCompleted до следующего прохода | Фактический FinalHex и MoverLost уже при завершении шага |
| Dispose / disable движения | Очистка зависит от хвоста корутины | Явная однократная отмена и очистка политики |
| Частичные потери / обычное движение / живое прерывание маршрута | Обычная обработка | Сохранена; проверки добавлены, запуск в Unity нужен |

## Изменённые файлы и ответственность

| Файл | Изменение |
| --- | --- |
| Assets/Scripts/Map/ArmyController.cs | Владелец завершения / отмены; handles easing и движения; однократные callbacks; OnDisable/OnDestroy; прекращение дальнейших шагов после отмены |
| Assets/Scripts/Map/HexSelectionController.Factory.cs | Порядок удаления, фактический хекс, отсоединение контроллера до уведомлений, сохранение неподвижных контейнеров |
| Assets/Scripts/Map/ArmyRegistry.cs | Проверка регистрации, атомарное удаление с терминальным Hex, идемпотентное удаление, запрет переноса снятого объекта |
| Assets/Scripts/Map/HexSelectionController.Movement.cs | Защита прибытия; запись частичного результата живой отмены через существующий MoveArmy |
| Assets/Scripts/Ai/AiTurnController.cs | Безопасное ожидание Unity-объекта; отмена принадлежащего этому вызову приказа при Dispose; cleanup политики / подписки |
| Assets/Scripts/Ai/V2/Execution/GroundCombatLegStep.cs | Удаление записи погибшей sortie; MoverLost при гибели на обратном полёте вместо StepCompleted |
| Assets/Scripts/Ai/V2/Execution/ReconAirExecutor.cs | Передача фактического EndHex; немедленное терминальное MoverLost; очистка только потерянного actor / sortie; запрет переобозначить гибель в удовлетворённую цель |
| Assets/Scripts/Ai/V2/State/AirSortieRegistry.cs | Очистка по ID погибшего исполнителя; существующий Remove(ArmyData) делегирует ей |
| Assets/Scripts/Ai/V2/Execution/RaidExecutor.cs | Потеря primary / support приоритетнее факта произошедшего боя или события |
| Assets/Scripts/Ai/V2/Execution/AttackExecutor.cs | Та же корректировка для Assault / Return / Reinforcement, с сохранением CombatChanged и diversion facts |
| Assets/Scripts/Ai/V2/Execution/ActiveDefenceExecutor.cs | MoverLost в завершившемся бою и Return; потеря исполнителя не объявляется достижением цели |
| Assets/Scripts/Ai/V2/Execution/TaskExecutor.cs | MoverLost в transport; Economy / Development не объявляют доставку после гибели; штатное AP / resource stamping и owner-scoped release сохранены |
| Assets/Editor/ArmyRemovalRegistryTests.cs (+ .meta) | 7 EditMode-проверок реестра, наблюдаемой / исторической памяти, sortie и owner-scoped банка |
| Assets/Editor/ArmyMovementLifecyclePlayModeTests.cs (+ .meta) | 14 coroutine-проверок с явным EnterPlayMode / ExitPlayMode; включены для Unity 6000.3+ и исключены из старой ai-verify среды |

## Банк и резервы

Проанализированы `ArmyActions.TryPayActivation`, `ArmyData.PendingActivation*`, `TaskExecutor.ExecuteMissionCore`, `ReconAirExecutor.RunActorStep`, `TurnResourceBook`, `StrategicResourceReservationLedger`, `AiAirSortiePlanner`, `AviationRebasePlanner`, `MissionOutcomeLedger`, `MissionContinuityLayer`, `InfrastructureFulfillment` и settled-step цикл Pipeline.

- Физические AP / Energy оплачиваются в beforeFirstStep через TryPayActivation после проверки легального оплачиваемого шага. Ground оплачивается один раз за ход; aircraft — один раз за sortie, по SortieLaunchPaid. Исправление не меняет этот механизм.
- Delete / Cancel / Unregister не добавляют ресурсы и не снимают повторно стоимость. Поэтому состоявшийся вылет не получает возврат из-за гибели, а отмена до первого шага не оплачивает его.
- Приказ движения выполняется на ArmyController, AI и его внешний executor продолжают выполняться на независимом runner. Безопасное ожидание даёт штатным строкам после yield измерить физический AP delta и ResourcesAfter; новый ledger расходов не нужен.
- TaskExecutor освобождает только Economy ReservationOwner на терминальном исходе. ReleaseByOwner идемпотентен. Общая формула свободных ресурсов остаётся Physical минус непогашенные owner-scoped claims.
- Sortie-запись — бронь посадочной вместимости / физический flight state; она не является отдельным кошельком ресурсов. Удаление записи по ID не трогает другую wing или наземную задачу.
- Потеря air support проходит через существующие support-local правила MissionOutcomeLedger / Continuity (Raid / Attack support не объявляют primary погибшим). ActiveDefence сохраняет свою существующую обработку роли. Остатки ресурсов освобождаются владельцами / стадиями обычного банка.
- Следующий settled-step проход повторно читает PlayerRoot, BuildSelf, reservations и executor bindings. Исправленные MoverLost и FinalHex не требуют отдельного финансового пути.

Это выводы по исходникам. End-to-end исполнение и отсутствие runtime зависания резервов **не подтверждены**: PlayMode и NUnit здесь не запущены. Новые тесты отдельно покрывают физическую стоимость запуска, расходы уничтоженного transport и idempotent owner release, а существующие reservation tests остаются обязательными.

## Кеширование: запись и чтение

Запись:

- Unregister удаляет из исходного bucket, пока ArmyData.Hex ещё равен этому origin, и только затем меняет Hex на deathHex. Оба уведомления видят уже отсутствующую армию; повторное удаление и поздний MoveArmy не публикуют её повторное появление.
- Публикация происходит через прежние RecomputeFor / NotifyContentChanged. Неподвижная сохраняемая оболочка публикует один content update. При штатном переносе прежний ArmyRelocated вызывается после полного re-key и уведомлений.
- AiMapMemory подписан на visible-content changes и обновляет KnowledgeVersion / RouteMemoryVersion по существующим правилам. Не добавлено чтение скрытых чужих live-армий.

Чтение:

- AiV2Util.ResolveArmy использует ArmyRegistry.AllForOwner: удалённый ID не разрешается. Пустые постоянные контейнеры намеренно сохраняют старую семантику.
- WorldAnalysis.RefreshOperationalState / RefreshStrategicKnowledge пересобирают Self через BuildSelf; удалённая армия отсутствует в новом снимке доступных сил.
- SafeStepPathing кеширует маршруты с observer RouteMemoryVersion и map PathingVersion; наблюдаемое удаление обновляет прежние ключи. Изменение собственного состава читается через текущего actor / профиль движения.
- WorthIt.EstimateCache сериализует полный фактический профиль состава / HP / способностей и прочие входы расчёта. Новый профиль после частичных потерь не совпадает со старым ключом. Старый immutable snapshot остаётся историческим снимком, а не изменяемым live-кешем.
- Исполнители публикуют актуальные StepsMoved / FinalHex / MoverLost; существующие bump и PublishStepObservationDelta возвращают управление settled-step переоценке. Отдельная версия состояния в Map-слое не введена.
- Контакт противника вне текущего наблюдения сохраняется как LastKnown по прежним правилам. Его гибель не становится скрытой live-подсказкой.

## Проверки и ограничения

Фактически выполнено:

- Повторное чтение актуальных исходников GitHub, полный поиск Unregister / Destroy controller / Controller = null / ожиданий IsMoving / MoveAlong / SettleThen.
- Повторный аудит изменённых цепочек до банка и чтения snapshots.
- Tree-sitter C# parse: все 14 новых / изменённых C# файлов без синтаксических ошибок. Это **не компиляция и не type checking**.
- `git diff --check`: без ошибок. Новые .meta сохранены; сцены, префабы, настройки и логи не изменены.

Не выполнено:

- Unity 6000.5.4f1 отсутствует; полный EditMode, PlayMode, визуальная партия и нативная семантика Destroy / остановки nested coroutines не проверены запуском.
- dotnet / mono отсутствуют. setup.sh не удалось подготовить среду (ошибка прав apt, последующая установка недоступна из-за lock). Compile baseline и managed NUnit regression gate получить не удалось.
- Попытка compile_check.sh --baseline напечатала `baseline errors: 0`, потому что pipeline инструмента фильтрует сообщение об отсутствии dotnet. Этот вывод **не считается валидным baseline или успешной сборкой**. Проверочные инструменты в репозитории не изменялись.

Добавленные проверки, ожидающие запуска:

- PlayMode: гибель в intermediate и final resolver; реальная ветка удаления ResolveAirArmyStep с внедрённым post-AA пустым составом; частичные потери; обычное ground движение и повторная активация; живое прерывание; удаление во время arrival callback; отмена до первого шага; свежий Self snapshot; ожидание открытого боя; Dispose и отписка; Economy MoverLost на цели с расходами и owner release; Recon cleanup; AI wait с сохранённой стоимостью вылета.
- EditMode: late MoveArmy; атомарные notifications обоих Hex; видимая память и route version; скрытая историческая память; обычный ArmyRelocated; изолированная очистка sortie; idempotent owner-scoped release / Free.

Обязательно перед merge в Unity:

1. Выполнить полный EditMode suite, затем ArmyMovementLifecyclePlayModeTests. Эти Editor-тесты сами входят в PlayMode.
2. Воспроизвести реальный бой ПВО с popup / dice: полное и частичное уничтожение, intermediate и final hex, запуск из собственного Barracks. Новые тесты внедряют результат потерь; они не симулируют полный UI / dice путь ПВО.
3. Реальный наземный бой с гибелью mover (включая event / capture-kill), проверить отсутствие последующих capture / selection / visual действий. Fixture моделирует deletion в callback; полная BattleScreenUI интеграция требует партии.
4. Проверить в партии однократные AP / Energy расходы, освобождение остатка резерва и сохранение primary / другой wing после гибели поддержки.
5. Проверить живую отмену во время AA popup, deferred Destroy, повторную выдачу приказа после отмены и чтение новых forces / routes на следующем проходе.

Исправление доступно для ревью, но подтверждения Unity-runtime пока нет.
