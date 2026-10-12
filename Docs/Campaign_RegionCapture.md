# Визуализация захвата региона

Проект: HolyPenguin21/turnbased-ae-style. Исходная база реализации: `3c85539e2fdd3f72c8314f6754b11903a40e0380`. Интеграция после организации папок: master `fb7826ed775469c5aaf6940c54b1e333bb9c6f0b`.
Масштаб: локальная доработка presentation и очистка прежней вспышки захвата. Правила кампании и схема сейва не изменены.

## Cause analysis и уточнения ТЗ

1. Authoritative смена владельца уже централизована в `CampaignRules.ApplyRecordedResult`.
   Во всём актуальном `Assets/Scripts` остальные присваивания `RegionState.OwnerFaction` относятся только к генерации начального мира.
   Единственный вызывающий слой результата — `CampaignController.ApplyResult`; его `Transact` сначала сохраняет новый снимок и затем публикует его как текущее состояние.
2. Manual flow: `CollectionRewardUI` → `CampaignMatchBridge.RecordFinalResult` → `RecordManualResult`;
   после подтверждения награды `RewardDismissed`/`RecoverReward` → `AcknowledgeReward` → `ApplyResult`.
   Владелец может смениться в сцене Game до появления CampaignUI. Прямая подписка только CampaignUI потеряла бы этот захват при смене сцены.
3. Fast flow: `TakeAiTurn`/восстановление подготовленной операции → `ResolveFast` → `ApplyResult`.
4. Map flow: `CampaignUI.Render/RefreshMap` → `CampaignMapView.Refresh` → `CampaignRegionGraphic.Refresh` → существующий `CampaignSurface.shader`.
   Старый эффект зависел от refresh, времени изменения цвета и флага результата: оттенок менялся мгновенно, а затем кратко осветлялся. Refresh при загрузке мог заново инициировать вспышку.
5. Точки боя на планетарной карте нет; операция уже содержит SourceRegionId и TargetRegionId, а регионы — общие ломаные границы. Они достаточны для вычисления старта; новую игровую геометрию создавать не нужно.
6. Обычный AI vs AI flow уже показывает окно результата и ждёт явного Continue. Доработка сохраняет этот порядок: сначала эффект на видимой карте, затем существующее окно, затем Continue и следующий ход.

## Реализация

`RegionOwnershipChanged` публикуется ровно после успешной ownership-транзакции и только при отличии старого владельца от нового.
Payload содержит CampaignId, OperationId, SourceRegionId, TargetRegionId, PreviousOwner и NewOwner. Повторные ApplyResult/ack/callback блокируются существующим OwnershipApplied/receipt guard.

CampaignMatchBridge хранит один непрочитанный live receipt в памяти. Он переживает возврат Game → Campaign, проверяется по идентичности текущей подтверждённой операции и забирается один раз.
Receipt не находится в CampaignState, не записывается в save и не восстанавливается из истории. Новая кампания и runtime reset очищают его; другой/устаревший результат отбрасывается.
Повторная загрузка уже показанного/начатого эффекта не запускает его заново. Если загруженная операция ещё не применяла ownership и применяется при recovery, это новый факт смены владельца и нормальный триггер.

Старт: ближайшая к видимому центру атакующего региона общая сторона. Точка смещается немного внутрь целевого региона по нормали.
Проверяется принадлежность исходному и проецированному полигону; учитываются вогнутость и существующее преобразование карты. При отсутствии подходящей стороны/контекста используется VisibleCenter.

Заливка: существующий terrain shader интерполирует старый и новый faction tint внутри радиальной маски с мягким краем.
Маска измеряется в согласованных координатах viewport с поправкой на его aspect ratio, поэтому волна остаётся круглой при изменении размера и Canvas batching.
Флаг reveal передаётся только поверхностными вершинами целевого региона в свободном компоненте UV2. Соседние регионы, контуры, маркеры, облака и панели не участвуют в эффекте.
Начальный и конечный прогресс имеют точные значения старого/нового оттенка. Радиус достигает каждой вершины отображаемого региона.
Обновляется существующий shared material и его действующая stencil-копия: карта уже находится под Mask/RectMask2D.
Новых материалов на захват нет; вершины не перестраиваются каждый кадр.

Штамп: небольшой повёрнутый прямоугольный отпечаток CAPTURED в цветах текущего campaign UI, с pop/scale и fade.
Один root, текст и четыре тонкие линии создаются при Build карты и переиспользуются для следующих захватов; raycast отключён. Центр штампа рассчитывается один раз на захват.

Flow: ShowResult забирает live receipt и откладывает открытие resultWindow до завершения эффекта.
CampaignUI не принимает hover/click/ESC и не продолжает AI step в этот короткий интервал; ownership уже сохранён, боковая информация и статистика показывают нового владельца.
После эффекта вызывается существующий Render и ShowResult. Finish/OnDisable/OnDestroy снимают флаг reveal; ошибка UI также отменяет presentation.
Время используется unscaled, поэтому timeScale=0 не блокирует завершение.

Параметры редактируются в компоненте CampaignMapView на объекте карты: Capture → SpreadSeconds, SpreadEasing, StampDelay, StampSeconds, StampPopFraction, StampStartScale, StampAlpha, StampSize, StampRotation, RevealFeather.
По умолчанию: 1.275 s spread + .08 s delay + .65 s stamp, всего около 2.005 s. Значения длительности ограничены; отдельное аудио не добавлено.
Сцену/префаб вручную перепривязывать или пересобирать Campaign screen не требуется: локальный компонент эффекта подключается при обычном Build карты.

## Изменённые файлы

| Файл | Назначение |
| --- | --- |
| Assets/Scripts/Campaign/CampaignController.cs | Post-save событие реальной смены владельца |
| Assets/Scripts/Campaign/CampaignRegionOwnershipChange.cs | Несериализуемый payload и проверка актуальности receipt |
| Assets/Scripts/Campaign/CampaignMatchBridge.cs | Однократная передача события между сценами, корректная подписка при смене контроллера |
| Assets/Scripts/Campaign/CampaignMapView.cs | Serialized настройки, запуск/отмена presentation; refresh только отображает состояние |
| Assets/Scripts/Campaign/CampaignRegionGraphic.cs | UV-флаг целевой заливки; удаление старого refresh-driven flash |
| Assets/Scripts/Campaign/CampaignCaptureEffect.cs | Общая граница, радиальный reveal, reusable stamp, конечный жизненный цикл |
| Assets/Scripts/Campaign/CampaignUI.cs | Ожидание эффекта перед resultWindow и блокировка интеракции |
| Assets/Resources/Shaders/CampaignSurface.shader | Reveal старого/нового tint с сохранением ландшафта и существующих масок |
| Assets/Editor/Tests/Campaign/CampaignCaptureTests.cs | Тесты события, handoff, save failure, manual/AI outcomes, геометрии, shader и lifecycle |
| Assets/Editor/Tests/Campaign/CampaignVisualTests.cs | Обновление вызовов очищенного Refresh API |

Архив содержит .meta с сохранением существующих GUID; новые скрипты и тест имеют новые GUID.

## Проверки и границы подтверждения

Проверка компиляции всего текущего Assets/Scripts и четырёх campaign test fixtures на доступных Unity reference DLL/verification stubs: исходная база 0 ошибок, итоговый код 0 ошибок.
Это не native сборка Unity 6000.5.4f1.

Новые backend/geometry проверки: 9/9 PASS. Используются фактические C# методы с управляемыми Vector2/Rect/JSON адаптерами только в временной проверочной копии:

- победа игрока, подтверждение награды, сохранение до публикации;
- поражение и ничья без смены владельца/события;
- повторный callback, ApplyResult и reward ack без двойного события;
- ошибка записи ownership-транзакции: нет события и смены снимка; retry публикует один раз;
- ручной захват до появления CampaignUI, смена контроллера, однократное потребление;
- уже применённый загруженный результат не публикуется повторно;
- реальные победа и поражение AI vs AI fast resolver;
- несколько независимых операций/сидов;
- все соседние пары карт для seeds 1/37/94: старт внутри двух геометрических пространств, близость к границе, достижение каждого vertex при landscape/portrait viewport; fallback центра.

Общий portable прогон существующих campaign backend tests вместе с новыми: 68 PASS, 6 FAIL из 74.
Все шесть падений воспроизведены на неизменённой базовой ревизии в тех же пяти методах/шести test cases:
AiOperationAndHistoryAcceptEmptyMatchIdFromUnityJson; ImpossiblePendingOperationPhaseIsRejected (fast in Game);
MissingMatchIdDoesNotRouteToCampaignOrAcknowledgeAnything (два aiPending cases);
RewardAcknowledgedBeforeCaptureRecoversOnce (fast); S07_S11_FastResultAndNotificationReplayWithoutReroll.
Ошибка — Sequence contains no matching element при поиске тестовой AI→AI соседней пары на заданной карте: First не находит пару.
Это предшествующее изменение тестовых данных/генератора, не регрессия capture presentation; игровой генератор и старые fixtures в этой задаче не менялись.
Новый AI capture тест допускает корректные карты без общей границы двух AI фракций и выбирает следующий seed.
Из пройденных существующих проверок отдельно подтверждены 1000 геометрических seeds, full-campaign victory/defeat, сохранение/восстановление, receipt idempotency и fast resolver.
Выборочный baseline прогон тех же методов: 6 PASS, те же 6 FAIL. Полный общий suite нельзя считать зелёным.

Native UI/GL тесты добавлены и компилируются, но здесь не запускались: точные shader endpoints, частичный reveal, невовлечённый соседний mesh, обновление stencil-копии и UV-флага, отключение компонента и finite timeout.
Ручные восемь сценариев приёмки в реальной campaign scene, визуальное соответствие референсу, окно наград/результата и GC profiler остаются проверками в Unity.
Не утверждается, что в этой среде запускались настоящий tactical match, native JsonUtility, GPU shader, scene reload или mouse input.

## Локальный рефакторинг

Ownership-логику объединять не понадобилось: она уже имела единственный вход.
Удалён прежний flash/changedTime/Tick из region/map refresh. У Refresh убран неиспользуемый capture-параметр, обновлены все найденные вызовы.
У bridge централизована установка контроллера для подписки/отписки. Новая единственная точка проигрывания presentation — CampaignUI.ShowResult.
Нет изменений экономики, AI V2 бюджетов/банка, боевых правил, turn order или схемы save.

## Проверка после обновления проекта

Получить актуальный master из GitHub. Реализация учитывает организацию каталогов ресурсов и Editor tests.
Открыть Campaign в Unity 6000.5.4f1, выполнить CampaignCaptureTests, CampaignCaptureVisualTests и существующие campaign fixtures, пройти ручные сценарии ТЗ.
Изменения публикуются в GitHub и объединяются с master по отдельному поручению владельца. При интеграции сохранены новые папки ресурсов и тестов из организации проекта; существующие GUID не изменяются.
