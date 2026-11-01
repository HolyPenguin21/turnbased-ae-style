# Пакет 5: восстановление, проверка и интеграция

Исходная ревизия: b1e80d44f74d3e8bf8b68524811e437cef3cc374. Реализация 5.1–5.4 ранее прошла проверку в этом чате, но не была закоммичена; среда удалила checkout и временные результаты. Изменения восстановлены по зафиксированным операциям и проверены заново, с раздельными логическими коммитами. Новые GUID не имели опубликованных ссылок. Исторические результаты не выдаются за повторный запуск.

## Результаты повторной проверки

- 29/29 полных production file/application/capture/restore/resume сценариев, включая повторную загрузку, AP/resources/hand/movement/AA snapshots, все malformed/content/identity preflight отказы до остановки живого матча, backup-only DTO/delete и скрытые operation blockers.
- 30/30 файловых, strict JSON, fingerprint и hand-operation проверок.
- 5/5 dialog delete/reload/positions/error handling, с исключённым native draw/focus.
- Broad NUnit Core/Map/Combat/Cards/Turns: исходная база 1066 (649 passed, 417 nonpassing), восстановленный код 1078 (661 passed, 417 nonpassing); потерянных baseline passing cases нет.
- Среди текущих nonpassing — 415 native/reference/content ошибок и 2 invalid authored-content кейса. Прежний inventory assertion исправлен; новый hidden-popup native test не может исполняться без Unity. Полный список: non-ai-package5-nonpassing-tests.tsv.
- Общий all-Assets legacy-reference compile: 20 прежних diagnostics на базе и текущем коде, 0 новых. Полный runtime binding проверяется отдельно без этих старых editor API ограничений.

## Найденные вторичные связи

Draft и runtime roster разделены; rejected preparation больше не очищает живой campaign/map context. Hand creation принадлежит сессии, камера и world/provider assembly инициализируются явно. Eliminated slot не повторяет end effects предыдущего игрока. Busy факты учитывают модель BlueprintQuota, незавершённое событие, roster drag и rename независимо от скрытия панели. Exit инвалидирует callbacks до отмены и до очистки buildings/армий. Новый prepared request может повторно собрать ту же native scene, обычный повторный component enable не сбрасывает мир. Encounter weak-table имеет reset owner. Старые private test callers мигрированы к StrategicTurnEffects/ArmyActions без dead forwarding methods.

## Unity и совместимость

Unity 6000.5.4f1 отсутствует. Эти результаты — managed/reference проверки, не запуск Unity. Нужны полный EditMode/Extended, реальные New→exit→new/direct Game/repeated restore, popup/animation cancellation, reconstruction failures на world/knowledge/presentation/resume, UI slots/overwrite/delete/last-save/Continue и наблюдение двух viewer с native markers.

Схема сохранений v1, serialized DTO, IDs и canonical references сохранены. Условия content/future rejection и AtomicJsonFile recovery не ослаблены. Скрипты исключены из authored content fingerprint существующим алгоритмом. Политики ИИ не менялись; в AI tests мигрирован только income consumer. Более поздние изменения владельца в master сохраняются при merge.

## Команды

Задать DOTNET/DOTNET_BIN и NUGET_PACKAGES, затем выполнить Tools/match-save-verify/integration-run.py, Tools/package5-verify/compile_all.py, Tools/save-preparation-verify/run.py --binding --base b1e80d44f74d3e8bf8b68524811e437cef3cc374, Tools/match-save-verify/run-files.py, Tools/save-ui-verify/run_dialog.py, Tools/save-ui-verify/run_continue.py. Tools/package5-verify/run_tests.py принимает --runtime (disposable integration workspace), --work, и --base для tests/fingerprint helper исходной ревизии. ReferenceAdapter меняет только несовместимые флаги placeholder methods в временных DLL; native API не реализуются.
