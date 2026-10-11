# Planetary Campaign — направление наступления

Реализация направления атаки. База изменений: master `795b851b6210c2cdd08681c3ccbad7ccda2b36e4` (эффект захвата #178). Первоначально проверен master `fb7826ed775469c5aaf6940c54b1e333bb9c6f0b`; после обновления master изменения совмещены с capture presentation, базовые файлы повторно прочитаны через GitHub и сохранены отдельно для дифференциальной проверки.

## Что переиспользовано

Гекс-стрелка: `Assets/Scripts/Map/MoveArrowMarker.cs`. Вызывается из `HexSelectionController.Movement.cs:ShowPathArrow`. Точки берутся из `HexPath.Hexes` через `HexMap.HexToWorld`, путь при необходимости обрезается по существующим правилам видимости/контакта. `MoveArrowStyle` находится в `Assets/Scripts/Styles/MoveArrowStyle.cs`, реальные настройки — `Assets/Config/GameConfig.asset:moveArrowStyle`.

Это процедурный XZ mesh, без текстуры и без префаба. `BuildCurve` строит Hermite-маршрут; две точки дают прямой отрезок, несколько — непрерывную кривую. `BuildArrowMesh` сшивает сужающийся хвост и triangulated concave arrowhead, обводку и продольную vertex alpha. Видимость переключается через `Show`/`Hide`; объект создаётся один раз и переиспользуется, отдельного пула нет. Размер задан в мировых координатах, поэтому при отдалении камеры стрелка визуально уменьшается. Длина зависит от arc length маршрута, направление — от tangent; отдельного camera scale multiplier нет.

Шейдер гекс-стрелки — `Assets/Resources/Shaders/MoveArrowGlow.shader`. Материалы создаются `MoveArrowMarker.Initialize`. Прямое использование world MeshRenderer в Canvas не подходит: нужны UI stencil/RectMask2D и правильный порядок модальных окон. Шейдер MoveArrowGlow этих UI-масок не поддерживает. Поэтому переиспользуется геометрия, профиль и alpha, а Canvas использует штатный MaskableGraphic/UI material. Новых декоративных ассетов, текстур или шейдеров нет.

Рендерер-независимые методы вынесены из MoveArrowMarker в `ArrowMeshGeometry`. Гекс-стрелка вызывает те же методы с прежними параметрами; её badges, цвета, sorting, path selection, жизненный цикл и правила движения не менялись. 288 сравнений старых и новых fill/outline meshes дали одинаковые вершины, alpha и индексы треугольников, включая многоточечные маршруты и разные стили.

CampaignArrowGraphic существовал до этой задачи, но строил независимую простую геометрию и постоянно включался из Refresh для pending operation. Эта геометрия удалена. Теперь компонент берёт общий силуэт, один раз кэширует mesh arrays и при отображении переводит XZ в Canvas XY. Shaft растягивается по расстоянию, наконечник сохраняет масштаб; reveal обрезает треугольники вдоль направления, не перестраивая профиль. При коротком пути размер ограничивается, inset не может перевернуть стрелку.

## Интеграция

Человек: ConfirmAttack → кнопка Choose Deck → существующий Controller.BeginAttack → единственный UI-вход PresentConfirmedAttack → стрелка → OpenDeckSelection → существующий PrepareMatch → Game scene.

AI: существующий TakeAiTurn по-прежнему выбирает атакующую фракцию/source/target, сохраняет PendingOperation. Новый optional `resolveImmediately=false` используется только CampaignUI. По умолчанию метод полностью сохраняет прежний немедленный flow. UI показывает уже выбранную операцию; после завершения стрелки Recover открывает защитную колоду для AI→человек либо вызывает существующий ResolveFast для AI→AI. ActiveDeckReference берётся из сохранённого состояния обеих фракций, как при первоначальном выборе AI. Дополнительного RNG/выбора цели нет.

Источник координат — кэшированные отображаемые центры CampaignRegionGraphic и его уже построенный projected polygon. BoundaryFractionTowards находит первый выход из реального контура в сторону соседнего центра; отступ учитывает этот размер и clearance маркера. CampaignMapView передаёт только координаты, размеры и цвет. Каждый rebuild использует текущий RectTransform.rect, поэтому растяжение/изменение окна не оставляет старые pixel endpoints.

Ожидание использует существующий CampaignUI.Update presentation gate; coroutine и задержек в стратегической логике нет. Identity guard проверяет текущий контроллер и OperationId перед продолжением. Refresh, загрузка, resume и ShowResult не запускают стрелку. На OnDisable обоих UI компонентов эффект сбрасывается; повторное включение CampaignUI восстанавливает штатный pending flow без повторной стрелки. Ошибка освобождает presentation gate и открывает штатное окно ошибки.

Интеграция с новым capture: стрелка → бой/quick resolve → существующий результат → Continue → существующие spread/stamp → существующий Controller.Continue. До этого commit #178 запускал capture перед результатом; запуск перенесён из ShowResult в ContinueResult согласно этому ТЗ. CaptureEffect, shader, ownership event и live receipt не дублируются и не изменены. Только после завершения capture UI вызывает стратегическое продолжение. Поражение/ничья не имеют ownership receipt и проходят Continue штатно. Последний захват проходит ту же очередь перед CampaignFinished.

Схема save, bank/resources, боевые правила, победитель, ownership transaction, turn order и цель атаки не изменены. Подписок на новые события нет.

## Файлы

| Путь | Изменение |
|---|---|
| Assets/Scripts/Map/ArrowMeshGeometry.cs + .meta | Единственный общий построитель существующего силуэта/кривой/обводки/alpha |
| Assets/Scripts/Map/MoveArrowMarker.cs | Делегирует построение общему классу; прежняя гекс-логика сохранена |
| Assets/Scripts/Campaign/CampaignArrowGraphic.cs | Canvas адаптер, кэш геометрии, draw/hold/fade, clipping, размеры и Inspector |
| Assets/Scripts/Campaign/CampaignRegionGraphic.cs | Кэш projected marker и измерение boundary fraction без копии полигона на каждый эффект |
| Assets/Scripts/Campaign/CampaignMapView.cs | Сериализованный reusable graphic; явный PlayAttack; Refresh больше не рисует pending operation |
| Assets/Scripts/Campaign/CampaignUI.cs | Единственный launch после подтверждения; UI wait; recovery/cancellation; согласование результата и capture |
| Assets/Scripts/Campaign/CampaignController.cs | Optional defer resolution без изменения selection/RNG; default обратно совместим |
| Assets/Scenes/Campaign.unity | Дочерний AttackDirection под Regions и ссылка из map; layer 5, stretch anchors, raycast off |
| Assets/Editor/Builders/CampaignScreenBuilder.cs | При пересборке экрана также создаёт и связывает этот дочерний объект |
| Assets/Editor/Tests/Campaign/CampaignAttackDirectionTests.cs + .meta | Native geometry/lifecycle/save/deferred-flow regressions |
| Docs/Campaign_AttackDirection.md | Этот отчёт и инструкции |

Масштаб: локальный визуальный компонент и небольшой перенос общего mesh builder. Это не новая стратегическая архитектура.

## Inspector

Открыть `Campaign.unity`, найти `PlanetaryCampaign/CampaignConsole/PlanetSurface/Regions/AttackDirection`, компонент CampaignArrowGraphic.

| Параметр | По умолчанию | Значение |
|---|---:|---|
| Draw Seconds | 0.35 | Постепенная прорисовка |
| Hold Seconds | 0.80 | Удержание |
| Fade Seconds | 0.35 | Растворение |
| Visual Scale | 28 | Перевод размеров общего профиля в reference pixels Canvas |
| Marker Clearance | 24 | Минимальный отступ от центров, ограничивается для коротких переходов |
| Region Inset Fraction | 0.25 | Доля расстояния от центра до настоящей границы региона |
| Attack Tint | приглушённый кирпично-красный #AD473D, alpha 0.92 | Основной цвет |
| Faction Tint Weight | 0 | Примесь цвета атакующей фракции отключена по запросу владельца: стрелка красная для всех фракций |
| Pulse Amount | 0.06 | Амплитуда alpha-пульса; 0 отключает |
| Pulse Frequency | 1.5 | Частота пульса |

Профиль наконечника, tail width, outline и продольное затухание берутся из существующего GameConfig.moveArrowStyle. Его изменение влияет на обе карты; собственный campaign scale не меняет гекс-стрелку. Кэш профиля строится при открытии карты; после изменения общего профиля карту следует переоткрыть. Параметры времени/цвета/scale доступны на scene graphic.

Фактический существующий Campaign CanvasScaler в master имеет referenceResolution **1920×1080**, в отличие от 1280×720, указанного в ТЗ. Он не менялся ради этого эффекта. Координаты строятся в локальном RectTransform, поэтому решение работает и с 1280×720 referenceResolution; размеры полей измеряются в текущих reference pixels. Окно 1920×1080 отображается без дополнительного Canvas.

## Выполненные проверки

- Differential C# compile исходного актуального master и final Assets/Scripts + CampaignSystemTests/CampaignVisualTests/CampaignCollectionIntegrationTests/CampaignCaptureTests/новых tests: 0 errors / 0 new errors. Это старые Unity reference assemblies с изолированными API stubs, не native build Unity 6000.
- Оба unity-yaml-verify для сцены: 441 referenced / 446 defined, 0 missing/duplicate/overflow, 0 type mismatches.
- Фактические C# meshes: 288 legacy comparisons, 4320 viewport/frame samples на 48-регионной карте, три viewport размера, направления, draw/hold/fade; всё прошло.
- Managed diagnostic: 1000 прогретых Play/SetFrame/mesh cycles — 0 новых управляемых байт. Нет Unity Profiler measurement; внутренняя работа Canvas/engine и начальная аллокация кэша не измерялись.
- Два новых backend теста: PASS. Проверены AI→AI и AI→human selection, одинаковые immediate/deferred source/target/Outcome/Roll/RandomSequence/ownership/history на 30 seeds; запись pending operation переживает load без повторного расхода RNG.
- Первоначальный общий backend suite: 59 PASS / 6 FAIL в старых fixtures, затем runner остановился на попытке native ScriptableObject в новом тесте. Для новых backend fixtures применена отдельная disposable адаптация только catalog native wrappers/logging; их authored-key/starting-deck/controller/resolver logic сохранена. Никаких этих stubs нет в Assets.
- Повторная проверка соответствующих 20 case executions на актуальном master baseline и final: в обоих 14 PASS / те же 6 FAIL. Причина шести: `Sequence contains no matching element`, старый First ищет отсутствующую AI→AI соседнюю пару на фиксированной карте. Это не новый сбой стрелки. Общий suite **не зелёный**; старые fixtures не менялись.

Native Unity Editor/PlayMode/GPU tests и ручные tactical matches здесь **не запускались**: Unity 6000.5.4f1 недоступна. Compilation, portable tests и CPU mesh diagnostics не доказывают native JsonUtility, shader/stencil, scene transitions, физический ввод или фактический FPS.

## Все сценарии ТЗ

| № | Сценарий | Полученные подтверждения | Что остаётся в Unity |
|---:|---|---|---|
| 1 | Игрок→AI | BeginAttack/launch/selection chain audited; existing manual save/result tests PASS | Клик Confirm, 1 стрелка, затем выбор колоды и бой |
| 2 | AI→игрок | Новый тест покрывает manual AI operation с теми же source/target | Стрелка перед defence window |
| 3 | AI→AI | Deferred не имеет result до resolve; immediate/deferred результат/RNG/ownership совпадают | Визуально подтвердить окно появляется после исчезновения стрелки |
| 4 | Атакующий проигрывает | Arrow запускается до вычисления Outcome; не зависит от исхода | Проиграть/получить defender victory, без capture |
| 5 | Победа | Ownership/result backend regressions совпали; capture event сохранён | Result → Continue → spread → stamp → следующий ход |
| 6 | Последовательные атаки | Cached graphic reuse, 1000 cycles, Hide, 4320 samples; no event subscriptions added | Несколько реальных sequential operations, нет наложений |
| 7 | Загрузка | Pending save test PASS; launch отсутствует в Recover/Refresh; native regression добавлен | Load в PreparingBattle/ShowingResult, без ложной стрелки |
| 8 | Направления | Horizontal/vertical/diagonal и reversed направления проверены кодом/мешами; 3 визуальных примера | Проверить native rendering |
| 9 | Разрешение | Три размеров viewport, каждый rebuild использует актуальный rect; native resize test добавлен | Изменить размер окна прямо во время draw/hold |
| 10 | Выход/закрытие | OnDisable сбрасывает обе presentation gates/graphics; identity guard предотвращает старое продолжение | Выход в Menu во время эффекта, повторное открытие |
| 11 | Гекс-карта | 288 точных mesh comparisons, caller/material/style flow сохранён | Показ movement/attack arrows и badges на настоящей гекс-карте |
| 12 | Последний регион | Continue вызывается после capture; backend completion flow неизменён | Последний захват, финальная сводка без блокировки |

Native регрессии: CampaignAttackDirectionGeometryTests и CampaignAttackDirectionFlowTests; дополнительно полный EditMode suite проекта, CampaignCaptureVisualTests и ручная матрица выше. Tests задают реальные различия поведения/геометрии, а не только проверяют присутствие строк в исходнике.

## Визуальные примеры

`Campaign_AttackDirection_1.png`, `_2.png`, `_3.png`, общий `Campaign_AttackDirection_Examples.png` и `Campaign_AttackDirection_Animation.gif` включены в архив. Это растеризация **фактического CampaignArrowGraphic.OnPopulateMesh** с конфигурацией GameConfig и scene defaults, а не нарисованная новая стрелка. Фон — существующее превью `Docs/Campaign_TransparentFrames.png`, проверенное по master blob hash; текущие полигоны и центры заново сгенерированы из того же seed `0x345703EE`, viewport 1352×744, позиция (62,130). Три source/target пары смежны и принадлежат разным фракциям. Full-resolution views сохранены отдельно.

Все изображения явно подписаны CPU composite, **не Unity screenshot**. Старые подписи/sidebar/выделение на исходном проектном превью не являются состоянием показанных новых операций. Native cloud compositing, GPU blending, stencil и текстовые метрики нужно проверить в редакторе.

## Применение и публикация

В архиве — только файлы этой задачи по структуре проекта и unified patch относительно указанного master. Существующие .meta/GUID не меняются. `git apply --check Campaign_AttackDirection.patch` проверяет пригодность patch; при обновлении master перед применением перечитать актуальные изменённые файлы и совместить изменения, не перезаписывая чужую работу. Новые .meta идут вместе со скриптами.

Публикация в GitHub и merge в master разрешены владельцем после просмотра результата. Native проверка в Unity остаётся обязательной, как описано выше.
