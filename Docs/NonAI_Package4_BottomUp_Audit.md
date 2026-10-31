# Пакет 4: повторная проверка снизу вверх

Дата: 2026-10-31. Ветка `refactor/non-ai-package-4`, исходная git-база `5f752169`. Повторная проверка выполнена по отдельному запросу владельца после первой реализации. Изменения поведения ограничены движением, наблюдением и связанными presentation/save consumers; политики выбора и бюджетирования ИИ не менялись.

## Найденные и исправленные пропуски

| Связь | Дефект | Исправление |
| --- | --- | --- |
| Operation → step/reaction | Второй Run либо внешний TryEnterNextStep/Settle могли обойти незавершённую реакцию и продвинуть армию/вызвать arrival | Единственный активный driver; raw stepping доступен только без Run и order effects |
| Turn expiry → наблюдение | После удаления памяти на конце предыдущего хода неподвижный видимый противник не попадал в память заново при неизменном обзоре | Replenishment публикует content mutations; turn owner явно повторно наблюдает весь текущий обзор после завершения мутаций |
| Потери в пути → bystander memory | Осталось прежнее подавление уведомлений для moving marker, хотя registry теперь уже содержит текущий legal hex | Roster/AA уведомляют о committed текущем hex; stealth cleanup также публикует финальную мутацию |
| Headless move → смерть/transport | Уничтоженная армия могла остаться пустой оболочкой на собственных Barracks; отлетающее headless крыло могло принудительно приземлиться при уходе carrier | Domain busy учитывается в empty-shell retention и landing eligibility |
| Cancel → presentation callbacks | Cancel вызывал успешный onSettled; старые handles/policy/cancellation hook могли затронуть приказ, созданный из Completed | Отдельный cancellation callback для reconciliation; старое владение освобождается до Completed; останавливаются захваченные старые handles; hooks не наследуются заменяющим приказом |
| Event Skip → AI wait/save | Синхронный Skip выдавал новый приказ, пока старый ещё busy; задержка на кадр оставляла окно преждевременного завершения AI waiter | Intent передаётся inline после settlement через controller continuation. Cancel/replacement удаляют intent; save busy включает pending continuation |
| Neutral everSeen → hidden live state | После первого обнаружения нейтральный marker/layout продолжали зависеть от live roster и существования армии под fog | Нейтралы тоже получают immutable payload/frozen appearance. Их прежняя долгая память сохраняется до повторного наблюдения; everSeen больше не разрешает live marker/layout |
| Neutral memory → event guards | При расширении neutral observations временный guard без marker создавал бы sighting без frozen appearance и блокировал save | Переиспользован `HexEventRegistry.IsEventGuardArmy`; guards исключены из map-memory query |
| Frozen marker → observer inspection | Observer inspection искал только live markers; нейтральный Last Seen после перехода на frozen representation нельзя было открыть | Human и observer paths используют общий remembered-marker hit test и ShowLastSeen с историческими siblings |

Основные владельцы исправлений: `ArmyMovementOperation`, `ArmyController`, `ArmyActions`, `ArmyRegistry`, `AviationCombatOperation`, `AviationActions.Carriers`, `GameTurnController`, `MapObservationSystem`, `HumanVisualMemory`, `HexSelectionController` (Movement/Events/Visuals). Изменены контракты взаимодействия, отдельной новой системы движения, vision либо encounters не создавалось.

Нейтральная долгая память сохраняет прежнюю игровую семантику «увиденное остаётся известно», но содержит последнее наблюдение. Capture/death/loss за fog не исправляют её без нового наблюдения. Старые сохранения, в которых frozen neutral payload отсутствует, не достраивают его из скрытого мира. Формат DTO/frozen-tree и отдельные viewer caches сохранены.

## Проверенные связи

| От нижнего уровня к потребителям | Проверка |
| --- | --- |
| Legal step / registry / MP | Копирование маршрута, adjacency/terrain, indexed position после каждого шага, activation только перед законным первым шагом, отмена до/после debit |
| Roster / transport / AA | Пересчёт surviving formation перед следующим шагом, утрата transport, смерть, действующий headless order, отмена подвешенного resolver |
| Lifecycle / presentation | Result один раз, disposal, disable/destroy, stale callbacks, замена из Completed, transform и policy нового приказа |
| Stealth / events / encounters | Resident reveal и movement entry используют существующий encounter entrypoint; Skip сохраняет authority и не возрождает cancelled order; guard не становится fog-memory army |
| Turn / visibility / observation | World facts обновляют память без input component; current visibility, visited, everSeen, air reveal и detection не смешиваются; turn expiry сопровождается новым допустимым наблюдением |
| Payload / frozen projection | Скрытые roster/owner/defense/art не перечитываются; разные viewer независимы; чтение/capture/restore не расходуют live IDs и не изменяют activation/transport/membership |
| Consumers | Save учитывает domain/presentation/continuation busy; AI waiter получает финальное продолжение маршрута; human и observer inspection читают исторические records под fog |

Независимое read-only ревью observation подтвердило проблему turn re-observation, затем выявило event-guard coverage и observer inspection при переводе нейтралов на snapshots. Независимое movement review выявило перенос cancellation hook при замене terminal operation; после исправления дополнительных подтверждённых дефектов reviewer не сообщил. Эти ревью не заменяют запуск native Unity.

## Воспроизведение и проверки

Исходные файлы до повторного аудита сохранены отдельно в `verification/audit-base` вне проекта. Baseline не заменялся исправленным кодом. Managed harness компилирует копию runtime source с Roslyn, legacy Unity reference DLL и внешними compatibility stubs; adapters не находятся в Assets. Для сопоставимого broad differential baseline и текущая версия используют одинаковый рабочий каталог с content проекта.

| Прогон | Результат |
| --- | --- |
| Baseline до повторного аудита, `audit-before-shared-content.xml` | 1011 cases: 775 passed, 236 failed |
| После интеграции, `audit-integrated-broad.xml` | 1019 cases: 783 passed, 236 failed |
| Отдельный явный passing list после последнего callback fix, `audit-explicit-passing.xml` | **783 / 783 passed**, процесс завершился с кодом 0 |
| Native lifecycle fixture, `audit-completed-native-compile-build.log` | Compile-only против reference API: код возврата 0; native execution не выполнялся |

Восемь новых managed cases сначала воспроизведены падающими assertions, затем прошли: два обхода driver/reaction, turn re-observation, headless death, три neutral-memory/layout случая и transient event guard. Логи `audit-leaf-red`, `audit-headless-death-red`, `audit-neutral-red`, `audit-neutral-layout-red`, `audit-guard-red` сохранены отдельно от green прогонов.

Один существующий query-test намеренно мигрирован с `HumanMemoryQueryIncludesAircraftButExcludesHiddenOwnNeutralAndContainerRosters` на `HumanMemoryQueryIncludesAircraftAndNeutralsButExcludesHiddenOwnAndContainerRosters`: наблюдаемые neutral map armies теперь сохраняются исторически, hidden/own/container roster всё ещё исключаются. С учётом этой явной миграции нет новых регрессий среди проходивших baseline cases и нет новых failed cases. Список всех 236 failed cases точно совпадает с первым широким прогоном, перечисленным в [NonAI_Package4_Verification.md](NonAI_Package4_Verification.md).

Из 236 непройденных cases 235 требуют отсутствующих native/content/reference возможностей. Один — прежний реальный `MatchStateInventoryTests.EveryStatefulStaticTypeIsClassified` для `BattleEncounterCoordinator.CurrentEncounters` и `ArmyDestructionEffect.FrameCache`. Эти старые пропуски не исправлялись в рамках аудита. Полный suite не объявляется зелёным.

## Native покрытие и оставшаяся проверка

Добавлено девять сценариев в `ArmyMovementLifecyclePlayModeTests`: настоящая observation subscription при disabled input, полный цикл ходов, casualty notification во время движения, cancel без успешного settlement, replacement из domain/controller cancellation, pending continuation/save busy, синхронный Skip через реальный AI waiter, cancel во время Skip, neutral hidden death/turn expiry/observer inspection. Старый native casualty-test больше не отражает удалённый `_currentHex`; он проверяет notifications на committed шаге для двух viewer. Старый native fuel-memory test теперь инициирует world notification, а не вызывает UI callback для записи памяти.

Unity **6000.5.4f1 отсутствует**. Нативные coroutine lifetime, renderer hit test/inspection, JsonUtility/frozen visual codec, HexMap/pathfinder fixtures и полный EditMode/Extended suite здесь не запускались. Compile-only native fixture не доказывает их прохождение или сборку проекта настоящим Unity. Требуется запуск полной проверки в редакторе и визуальных сценариев из [контракта](NonAI_Package4_Contract.md).

`git diff --check` завершился без ошибок. Существующие .meta/GUID, scenes, prefabs, authored assets и project settings не изменялись. На момент завершения аудита коммит, push и merge не выполнялись; публикация выполняется отдельным шагом по запросу владельца.
