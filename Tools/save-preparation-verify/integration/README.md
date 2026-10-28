# Проверка полной логической цепочки restore в памяти

```bash
DOTNET=/path/to/dotnet python3 Tools/save-preparation-verify/integration/run.py
```

Требуется установленный .NET SDK 8+ с reference pack. Запуск напрямую через Roslyn;
NuGet restore, Unity DLL, сеть и tactical save-файлы не используются. Комбинированный
Library-сценарий создает изолированный временный collection profile на диске и удаляет
его после проверки; реальные пользовательские данные не читаются.
Выход ненулевой при ошибке компиляции или проверки. Временная сборка удаляется после запуска.

## Что исполняется

В одну сборку без изменений включаются настоящие:

- `GameSession.cs` и `GameSession.Restore.cs`, `IMatchRestoreSource`, `MatchRestoreCoordinator`,
  `MatchRestoreScope`, `MatchIdentityState` и `MatchStateInventory`;
- `CitadelSetupController.Restore.cs`: тот же `TryRestorePreparedMatch`, который вызывает
  сцена, с настоящим `MatchStateInventory.EndMatch` при исключении;
- `GameTurnController.Persistence.cs` и `GameTurnState`: именно production `ResumeGame`,
  `CanSaveMatch`, `CaptureTurnState`, а не их тестовые реализации;
- владельцы world/knowledge из соседнего world harness: armies, units, activation,
  movement/transport/carriers, roots/economy, fog, stealth, human visual memory, AA,
  delayed battles, events и permanent hex bonuses;
- `CollectionService`, `CollectionProfileStore`, `AtomicJsonFile`, `CollectionProfile`
  и `DeckRules`: настоящая receipt/ownership/atomic-profile логика; JSON — существующий
  managed adapter `Tools/campaign-verify/PortableJson.cs`;
- `PlayerHandData`, `AiHandData` с настоящим registry, `CardData`, `CardState`,
  `DeckDraw`, `MatchLoadout`, `TacticHand`;
- `HexSelectionController.Persistence.cs`, `MapResourceDisplay` и `MapEventDisplay`.

`Program.cs` содержит тестовый in-memory источник `DetachedSource`. Он получает факты
через настоящие capture API и отделяет player/entity references от исходного мира:
сохраняет ID, DTO и authored definitions; события преобразуются в примитивный DTO.
После staging источник создает единые словари entities с сохраненными ID и передает
их настоящим restore API. Это пример логической границы будущего decoder, не файловый
формат, не готовый serializer и не реализация сохранения игры. `NewPreparedSource`
глубоко копирует изменяемые DTO списки и создает новые players/hands/resolver dictionaries
для каждого восстановления одного saved payload, не переиспользуя live restored graph.

Happy path проходит `TryPrepareRestore` → настоящий scene helper → настоящий inventory
reset → source world/knowledge/presentation → настоящий coordinator validation/display
refresh → настоящий `ResumeGame` → настоящий `CompleteRestore`.

## Одиннадцать исполняемых сценариев

1. Полная цепочка: неизменные match ID/reward eligibility, player references, round,
   order/index, completed clocks, greetings и pending messages; ровно один финальный
   turn/presentation bind после quiet scope. AP, stock, contribution order, movement
   spent сверх reduced maximum, AA prompt и stealth toggle/expiry не обновляются.
   Удаленный из армии оплаченный юнит остается канонической ссылкой в activation ledger;
   отдельный пассажир сохраняет transport links и индекс для последующего cleanup;
   carrier containers повторно не создаются. Human/AI hand сохраняют порядок, overflow,
   duplicates/order draw pool, card IDs/attachments, acquisition age и mutation version.
   Сохраняются pending event reward/error/guard flags и hex bonuses. Замороженное
   историческое sighting отличается от скрытого нынешнего мира; настоящее presentation
   import/coverage/export сохраняет старые ключи appearance. Настоящие displays читают
   уже восстановленные bonus/event и knowledge. Новые identities выше saved highwater.
2. Реальная неразрешимая activation ID в detached DTO отклоняется до reset; предыдущие
   staged session и live registry остаются прежними.
3–5. Исключение после частичного world, knowledge или presentation restore: настоящий
   scene helper вызывает настоящий `EndMatch`, очищаются session/world/root/hand/event/
   bonus/fog/human-memory/stealth/AA registries; scope освобожден; последующий restore успешен.
6. После presentation источник намеренно нарушает immutable-source контракт и портит
   human-turn index: исключение бросает настоящий `ResumeGame.Validate`, затем настоящий
   helper очищает частично собранный мир.
7. Knowledge без frozen presentation отклоняется настоящим coverage validator coordinator.
8. Изменение staged request во время resume event отклоняется настоящим `CompleteRestore`.
9. Уже установленный setup error идет через настоящий failure cleanup до configuration.
10. Один saved payload восстанавливается успешно, затем live hand/AP/movement/activation/
    event/bonus/fog/stealth намеренно изменяются. Новый prepared source из того же payload
    проходит настоящий reset/restore; проверяются исходные факты и новые канонические
    player/hand/unit/army objects. Saved hand DTO не разделяет изменяемые списки с requests.
11. Комбинированный Library receipt + tactical restore. Настоящие `CollectionService`,
    `DeckRules`, `CollectionProfileStore` и `AtomicJsonFile` сохраняют первую permanent copy
    и точную receipt. После настоящего tactical reset/restore исходного pre-claim payload
    прежнего hand award нет, тот же collection service/receipt сохранен. Настоящий receipt
    replay возвращает исходную definition; настоящий `PlayerHandData.TryAddCard` добавляет
    ровно одну tactical card, permanent owned остается 1. Restore/replay повторяется еще раз;
    проверяется реальная запись profile на диске и новый `CollectionService` после Load.
    Здесь исполняются transaction и hand owner boundary, а native popup/event interaction нет.

## Где стоят адаптеры и чего это не доказывает

`PortableAdapters.cs` находится только в `Tools` и никогда не входит в Unity/player build.
Native `GameObject`/component/renderer lifetime, sprite hierarchy/pose/material cloning,
remembered marker positioning, map layout/texture authoring validation, config/catalogs,
UI и остальная часть turn controller адаптированы. `InitializeMatchPresentation` только
считает финальный bind; его native UI/subscriptions не проверяются. Настоящий persistence
код remembered visuals проверяет структуру/import/coverage, но frozen native appearance
представлено тестовым значением — пиксели/цвета/спрайты этим запуском не доказаны.
`ConfigureWorldSystems` адаптирован и вызывает настоящий inventory reset и vision config.
Таблица inventory и порядок ее reset вызовов настоящие; unrelated AI owners/diagnostics
в этом harness имеют no-op adapters. Настоящий aggregate AI roundtrip проверяется отдельно
в `../ai`; этот запуск не считается доказательством его state restore.

Неожиданные new-game deck/profile initialization, random draw/deal, carrier allocation,
production, `BeginGame`, `BeginPlayerTurn`, `BeginNewTurn`, income/AP/movement refill
в доступных adapter entrypoints бросают исключение. Сам `ResumeGame` не заменен.
Cleanup доказывает доступность реальных logical registries после сбоя; уничтожение
native scene objects, UI/coroutines и освобождение GPU assets требует Unity fixture.

Campaign context здесь намеренно null; ненулевой context adapter отклоняет. Collection
receipt replay и atomic profile I/O в комбинированном сценарии настоящие; authored catalogs
задаются тестовыми value DTO, `ProgressionContext` — adapter с настоящим service object и
запретом неожиданного Initialize. `PortableJson` использует System.Text.Json, поэтому
совместимость native `UnityEngine.JsonUtility` требует Unity. Повторные tactical hand award
добавляются явным вызовом настоящего `TryAddCard`, не native Library popup. Новых reward/
receipt алгоритмов в Tools нет. Настоящая Unity scene, generated map, authored asset
references, UI event flow и native serialization этим .NET запуском не исполняются.

## Зафиксированный результат

На текущей рабочей ревизии: **11 integration checks, 0 failures** (.NET 8).
Перед добавлением настоящего scene helper harness дал RED: единственная ошибка компиляции
`CS0103 TryRestorePreparedMatch does not exist`. После включения production partial все
одиннадцать сценариев проходят. Production runtime defect этим запуском не обнаружен.
