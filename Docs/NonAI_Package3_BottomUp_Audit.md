# Пакет 3 — повторная проверка снизу вверх

База: `bf0a4ecf161fe6c98a129a674d488321a5e4eeef`. Проверялась ветка `refactor/non-ai-package-3` до публикации в master.

Проверены расчёты и операции → engine/session → coordinator → UI/headless adapters → movement, turn-start, target-only, delayed и chained callers. Три независимых source review охватили наземные команды, aftermath и challenge/авиацию. Подтверждённые проблемы исправлены; исправления прошли scoped повторные ревью. Исходные findings и повторные вердикты включены в архив.

## Найдено и исправлено

| Граница | Ошибка до исправления | Результат |
| --- | --- | --- |
| Engine → Explosion | Один action допускал обычный plan и отдельный blast; можно было нанести два hit | Вид действия закреплён в request, обычный plan для Explosion отклоняется |
| Explosion → owner | Optional `battle=null` обходил engine cleanup/phase и снимок правил | Engine-created blast хранит owner; Resolve/Complete используют его даже без аргумента, чужой blast отклоняется до мутации |
| Deployment → summons | Повторные вызовы, середина раунда и завершённый encounter позволяли создавать новые summons | Один запуск на участника, только Deployment; AI arrangement тоже ограничен Deployment |
| State → Apply | Вызов до создания engine завершал встречу двух живых армий | Stateless Apply проверяет terminal facts и обязательных героев; ранний acknowledge отклоняется |
| Capture/Kill → UI callback | Callback старого encounter мог захватить героя после замены экрана | Проверяются state и presentation generation; используется frozen engine magnitudes |
| Legacy finish → aftermath | Отдельная реализация повторно пополняла Fate и расходилась по survivor | Старый entrypoint стал adapter к единому receipt Apply; reverse hunter/target поддержан, старый pair не заменяет новый encounter |
| Air strike → consumers | До application выставлялся `Attacked=true`, отмена давала AI ложный success | Receipt выставляется после принятого application; момент оплаты/commit атаки сохранён |
| Movement → aviation cancellation | Остановка coroutine не гарантировала Dispose operation/Explosion | Явный cancellation bridge через текущий StepResolutionOutcome; iterator освобождается, текущий шаг останавливается |
| AA popup → caller | Parent-disable оставлял вечный WaitUntil; отмена turn-start пропускала остальные реакции | Отдельная cancellation, activeInHierarchy; живой turn-start caller ждёт headless drain оставшихся eligible AA |
| Existing PlayMode fixture → public API | Reflection вызывал удалённый private ResolveAirArmyStep | Проверка использует public ResolveStep; добавлена обработка пустого mover до классификации air/ground |

Дублирование aftermath в `FinalizeStandaloneCaptureKill` удалено. Убран повторный HandleBuildingOnArmyDefeat из CompleteBattle; уже Applied encounter не проходит повторный UI summon cleanup. Сохранены только совместимые entrypoint adapters и partial-разделение существующего BattleEngine. Новых параллельных реализаций расчёта R/P, Bodyguard или авиационной политики не добавлено. `Assets/Scripts/Ai` не изменялись.

Legacy lookup использует слабые привязки участников к текущему BattleState. Новый BattleState создаёт новую границу receipt; повторный legacy finish использует текущую. Обратный порядок ролей переставляет только поля returned receipt, без второго Apply. Несовпадающая текущая привязка любого участника отклоняет старый callback.

## Проверка

| Прогон | Результат |
| --- | --- |
| Исходная база, тот же managed harness | 648: 409 passed, 239 failed |
| Версия до этой перепроверки | 691: 452 passed, те же 239 failed |
| После исправлений | 702: 463 passed, те же 239 failed |
| Повтор всего passing списка | **463 / 463 passed** |
| WorldMutationContract + VisionContainer внутри прогона | **39 / 39 passed** |
| Новые регрессии к базе и к версии до audit | **0** |
| Existing asset GUID changes / whitespace errors | **0 / 0** |

Внешний harness компилирует полный runtime source и выбранные Combat/Cards/Map fixtures против legacy reference DLL и compatibility stubs. Это не сборка проекта в Unity. Общие 239 ошибок сверены по точным именам: 2 assertion failures, 235 native/reference/content errors, 2 invalid cases. Полный перечень находится в `verification/baseline-failures.md` архива.

Перед исправлениями воспроизведены RED для шести основных domain сценариев; затем для reverse/stale legacy context, отсутствующего optional owner и frozen magnitudes. В авиации воспроизведены false receipt и early-empty mover. XML RED/GREEN, финальный дифференциальный прогон и passing list сохранены в архиве.

Native тесты добавлены для stale Capture/Kill callback, parent/presenter disable, двух turn-start cancellation путей и реального CancelMovement после первого Explosion hit. Они **не запускались**: Unity Editor отсутствует. Combat/Cards native fixtures проверены компиляцией harness; изменённый Extended PlayMode fixture требует настоящего Unity test runner и не входит в этот managed compile/test subset.

## Оставшаяся проверка в Unity

Unity **6000.5.4f1**: полный EditMode и Extended/PlayMode suite, новые popup/coroutine/disable сценарии и интерактивная проверка затронутого боя. Отсутствие нативного прогона остаётся ограничением; source review и managed results его не заменяют.

Aftermath Apply/Acknowledge по-прежнему относится к encounter. Подтверждение отдельных hit сохраняет прежний момент application. Начало следующего encounter или event operation допускается после acknowledge; active encounter Apply владеет собственными последствиями и guard cleanup.
