# Аудит чтения и записи кешей — 17.11.2026

Проверенная ревизия: `ab66ad13a644c0503d76a40336f3eea87dface41`, Unity `6000.5.4f1`.
FIX-01 опубликован: реализация `6dfcc6890253033e6bc6172a8b9b7a3f02f1e747`, merge в `master` — указанная ревизия.

## Результат и границы

Найдены два воспроизводимых нарушения эквивалентности прогретого и свежего расчёта
`ActionPrice.ScarcityProfile` и неполный ключ кеша выбора хекса под курсором.
Для первых двух подтверждён контракт метода; достижимость этих конкретных входов
обычными игровыми действиями в одном snapshot не доказана. Для выбора хекса
зависимость установлена по коду; воспроизведение в редакторе остаётся обязательным.
Критическая порча сохранений или общего боевого кеша этой проверкой не выявлена.

Производственный код в ходе аудита не менялся. Это отчёт о проверке, а не реализация
исправлений. Отдельно перечислены риски API, которые нельзя выдавать за уже
происходящие игровые ошибки.

Поиск охватывал все 724 C# файла `Assets/Scripts` и редакторскую часть; всего в
`Assets` — 1025 C# файлов на проверенной ревизии. Проверялись `cache`, `memo`,
`dirty`, версии, fingerprints, weak tables, статические коллекции и ленивые поля.
Совпадения поиска классифицированы: константные таблицы, рабочие буферы, владельцы
состояния и память наблюдателя не приравнивались автоматически к производным кешам.
Таблица ниже фиксирует проверенные семейства. Это анализ исходников и ограниченные
исполняемые проверки, а не доказательство всех возможных состояний проекта.

## Находки

### CACHE-01 — кеш хекса не учитывает viewport камеры

**Приоритет P2; подтверждено по зависимостям исходников, Unity-проверка не выполнена.**

`Assets/Scripts/Map/HexSelectionController.cs:516` — `RaycastHexCached()` сравнивает
только координату мыши, позицию камеры и `orthographicSize`.
Однако `RaycastHex(Vector2)` на строке 545 вызывает `Camera.ScreenPointToRay`,
результат которого также зависит от viewport и проекции камеры.

Сценарий: прогреть hover, оставить мышь неподвижной, изменить размер окна/Free Aspect
так, чтобы изменилось соответствие экранной координаты игровому миру, сохранив
позицию и zoom камеры. Кеш может вернуть прежний хекс до следующего движения мыши
или камеры. Ветви обработки левого и правого клика получают этот же `hoverCoord`,
поэтому проверить нужно также выбор и команды по клику, а не только подсветку.
`CardHandUI` вызывает публичный `RaycastHex` напрямую: эта ветвь кеш не использует.
Поворот камеры отдельно не заявлен ошибкой: камера проекта имеет фиксированный угол.

Исправление: учитывать актуальное преобразование экран → луч, например viewport и
матрицу проекции, либо отказаться от этого небольшого кеша после измерения стоимости.
Приёмка: неподвижный курсор + resize/fullscreen/Free Aspect; сравнение с прямым
`RaycastHex`, затем hover, выбор, правая кнопка и drop карты.

### CACHE-02 — округлённый ключ, точный расчёт цены ресурсов

**Приоритет P3; воспроизведён дефект контракта метода. Влияние на матч не измерено.**

`Assets/Scripts/Ai/V2/Evaluation/ActionPrice.cs:123–146`: `ProfileKey` округляет
каждый supply до сотых (`RoundToInt(supply * 100)`), а расчёт при промахе использует
исходные float. Различные допустимые входы попадают в одну запись.

При одной карте с Human=1, Energy=1, без дохода и неизменном Energy=1:

| Supply Human | Режим | Цена Human |
| --- | --- | ---: |
| 0.996 | Первое чтение | 1.00160325 |
| 1.004 | Тот же snapshot, кеш прогрет | 1.00160325 |
| 1.004 | Новый snapshot, свежий расчёт | 0.998403132 |

`ScarcityProfile` принимает `Func<ResourceType,float>`. Реальные потребители получают
такую функцию через `StrategicSpendability.SpendableAmount` → `TurnResourceBook.Free`:
это float-доступность за вычетом притязаний, без общего контракта округления до сотых.
Вместе с тем физические ресурсы и цены карт обычно целые. Проверка не установила
конкретную обычную игровую последовательность, создающую именно такой переход
физического остатка в рамках одного snapshot. Нельзя из этого теста заключать,
что торговля уже систематически принимает неверные решения.

Исправление: ключ по точным битам нормализованных float либо единая явно заданная
дискретизация и ключа, и вычисления. Приёмка: warm/cold равенство при изменении
доступности внутри одной сотой, в обоих порядках вызова и у границ доступности карты.

### CACHE-03 — содержание спроса карт отсутствует в ключе

**Приоритет P3; воспроизведён дефект при изменении модели без нового snapshot.**

`ActionPrice.cs:123–146` сохраняет число карт руки, свёртку RuntimeId и только число
карт колоды. `CardCosts` на строке 198 читает живые `EffectivePlayResourceCost` и
`CardDefinition.resourceCost`. `WorldAnalysis.Self.cs:105–106` сохраняет ссылки на
`hand.Hand` и `hand.RemainingDeck`, а не замороженную копию ресурсных цен.

Одна карта колоды, запас Human=5: после прогрева меняем её Human cost с 1 на 10,
сохраняя snapshot и размер колоды. Прогретый результат — **0.466666639**,
свежий — **1.26666665**. Кеш не замечает изменение.

Аналогичная неполнота ключа относится к замене карты колоды при прежнем количестве
и изменению эффективной цены карты руки при прежнем RuntimeId. При этом найденные
обычные пути создания Research/Production создают новый CardData, а restore
перестраивает состояние. Не доказано, что обычный ход игры редактирует уже
учтённую ресурсную цену в живом snapshot. Подтверждённый триггер теста — изменение
публичной mutable-модели/контента; это не доказанный сбой production-потока создания карт.

Исправление: замораживать ресурсные счета при построении snapshot либо включить
их точное содержание/надёжную ревизию в ключ. Простого добавления deck RuntimeId
недостаточно для изменения цены того же объекта.

## Горизонтальная проверка найденных областей

| Область | Проверенные связи и вывод |
| --- | --- |
| ActionPrice → карточная оценка | `StrategicCardEvaluator.ResourceCost` учитывает ту же таблицу цен и actor-aware spendable callback; ошибка кеша способна менять оценку, но сама не списывает ресурсы. |
| ActionPrice → развитие и строительство | Проверены `DevelopmentPreparationScorer`, `DevelopmentOpportunityEvaluator`, `InfrastructureFulfillment`; отдельную конкурирующую таблицу цен вводить не следует. |
| ActionPrice → trade | `TradeFunding` использует `Resources` и `ScarcityProfile` при оценке обменов/горизонта; фактическая допустимость обмена проверяется отдельным расчётом свободных ресурсов и оплаты. Неверная оценка не равна обходу проверки оплаты. |
| ActionPrice → резервы | `StrategicSpendability` читает живую книгу ресурсов; сама книга не кеширует ответ `Free`. Не надо лечить дефект цены сбросом действующих резервов или кредита уже начисленного дохода. |
| ActionPrice → прочие memo | `EquipmentDeltaCache`, recipient verdict, development admission имеют свои scopes и fingerprints; общей ошибки «везде ключ округлён до сотых» не установлено. |
| Cursor cache → команды/UI | Hover используется обработчиками мыши; drop карты идёт через прямой raycast. Требуется совместная проверка после resize. |

`ResourceAllocator.Fingerprint` тоже форматирует AP до двух знаков, но используется
для остановки повторных проходов (`Converged`), а не для выдачи сохранённой цены.
Без отдельного сценария преждевременной остановки это не включено как ещё один
доказанный дефект кеширования.

## Карта проверки: запись → чтение → инвалидирование

Пути в таблице относительны к `Assets/Scripts`, кроме явно указанных editor-файлов.
«Не найдено» означает результат анализа указанного контракта, а не гарантию отсутствия ошибок.

| Семейство / владельцы | Что проверено | Результат |
| --- | --- | --- |
| `Core/GameAssetCatalog` | ReplaceEntries, OnValidate, lazy lookup, вложенные массивы и результаты LoadAll | Замена атомарна; lookup сбрасывается; входы и выдаваемые массивы копируются. |
| Каталоги карт, faction/start/research/event/abilities | Чтение авторского контента, Active-каталог, повторное разрешение | Отдельный кеш изменяемых цен в этих каталогах не найден. |
| `Editor/Tools/MatchSaveAssetManifestBuilder` | InputStamp, генерация content fingerprint, вход в Play и build | Для build вызывается принудительный Rebuild. Риск редакторского stamp отмечен ниже. |
| `Saves/MatchSaveAssets`, `MatchSaveSchema` | Manifest, lazy schema hash, загрузка контента | Хеш схемы зависит от загруженной схемы типов; повторный файловый load не подменяется старым кешем файла. |
| `MatchSaveStore`, `AtomicJsonFile`, профиль и campaign stores | Load/save, temp/replace/backup, публикация in-memory состояния | Между чтениями дискового файла постоянный кеш результата не найден; запись предшествует публикации успешной транзакции. |
| `MatchWorldCodec`, `MatchAiCodec`, presentation codecs | Identity maps, ссылки и повторные операции restore | Контексты локальны операции; производные кеши не сериализуются вместо владельцев состояния. |
| `ProgressionContext`, `CollectionService`, `CampaignController` | Срок жизни сервиса, Changed, load/save и match reset | Профиль — владелец состояния между матчами. Его сохранение между матчами само по себе корректно. Mutable View — отдельная архитектурная тема. |
| `HexMap`, `HexPathfinder.GridFor` | SetData, SetTerrainAt, изменение площади погоды, ключ grid | Ключ — map identity + PathingVersion; допустимые записи данных и изменение площади погоды повышают версию. SetLayout хранит копию описания представления, не меняет terrain. |
| `HexPathfinder` search buffers | Аренда/возврат, generation stamps, reentrant callback | Рабочие массивы отделены от постоянного результата; вложенному поиску выдаётся отдельный буфер. |
| `Ai/SafeStepPathing` | Route/tree/base/return fields, profile, maxMovement, map и player knowledge | Ключи и invalidation разведены по профилям/игрокам. Публичный FindSafePath копирует Hexes; подозрение на утечку кешированного пути снято. |
| `AiMapMemory` | Observe/forget/building changes, per-player revisions, Clear/restore | Новая epoch не повторяет старую route revision; изменения знаний повышают нужные версии. Это также авторитетная память наблюдателя. |
| `HumanVisualMemory`, `ObservedArmy` | Изменения видимого содержания, final notifications, копии и restore | Снимки наблюдения отделены от скрытых live юнитов. FIX-01 закрывает преждевременную публикацию уничтожения facility. |
| `SandstormState`, `SandstormRules.ContinuationMemo` | Topology, шаг погоды, границы локального memo | Memo используется с одной topology в одном проходе; смена площади инвалидирует pathing через HexMap. |
| `WorldAnalysis`, `WorldSnapshot` | RefreshOperational/StrategicKnowledge, observer/turn/map/path/knowledge | Повторное использование знаний защищено версиями; исключение — живые счета карт в ActionPrice, CACHE-03. |
| `MapKnowledgeSnapshot.OnMapSet` | Ленивое построение и выдача множества | Работает при неизменности списка; mutable API — риск ниже. |
| `WorthIt.EstimateCache` | Полнота ключа, bitwise floats, scopes/leases, размер, cache on/off | Массив ключа копируется, проверяется полное равенство; старый lease не закрывает новую сессию. Equivalence-тесты проходят в переносимой среде. |
| `AviationCombatEstimator` | Синхронизация scope с WorthIt, параметры policy/fate/profiles, результаты | Scope согласован, результаты защищены read-only копиями. Native интеграция отдельно не запускалась. |
| `BattleSimulationKernel`, `WorthIt.Coverage` | Exchange memo и binomial distributions, выдача Distribution | Числовые ключи зависят от входных правил; публичная выдача копируется. Проверка всех комбинаций модификаторов проходит. |
| `InitiativeOutcomeEvaluator` | PMF по pool, локальный SolveTop2 memo | Чистые числовые зависимости; world invalidation не нужен. |
| `BattleEncounterCoordinator` | WeakTable текущего encounter, ResetForMatch | Таблица владеет активной сессией; это не кеш повторного расчёта старого боя. |
| `ForceNeedModel`, `CombatOpportunityAnalyzer` | Weak memo на snapshot, staged pool | Ограничены snapshot; pool использует зафиксированный PoolCards. |
| `EconomyBuilderSelection` | Assessment key, маршрут/армия/стоимость, выдаваемое решение | DetachedDecision защищает memo от caller repricing. |
| `ActiveDefenceObjectiveEvaluator`, `AttackMarchPolicy`, `ReconObjectiveEvaluator` | Snapshot scope, enemy/army/position/ETA/exclusions, frame enumeration | Неполнота ключа для текущих читаемых зависимостей не найдена. |
| `DevelopmentAdmission`, `ProfileRowCache` | Roster/benchmark fingerprints, content changes, field equality | Помимо идентичности есть сверка фактов; output modes разделены. |
| `EquipmentDeltaCache`, recipient evaluation | Player/turn, core/full keys, зависимые чтения и nested scope, reset | Проверки core/full invalidation проходят; match reset очищает владельцев. |
| `EquipmentTargetMemo`, `EquipmentEfficiency` | Scope snapshot/purpose, target/context keys, выдача результата | Текущие producers создают новые наборы целей. Предположение неизменности списка отмечено ниже. |
| `StrategicReadmission`, fingerprints admission | Повторный допуск в decision frame, доменные fingerprints | Это подавление повторной оценки в frame; не замена состояния мира старым результатом сохранения. |
| `TurnResourceBook`, `EconomyReservationLifecycle` | Free/Claims, owner authority, deferred income credit | Free пересчитывается. Income cover — факт начисленного дохода этого хода, его нельзя обновлять как прогноз при каждом новом здании. |
| AI intent/lease/recon/force registries | Владение состоянием, session reset, capture/restore через inventory | Сохраняемая память и обязательства не приравнивались к disposable memo; очистка всех словарей на mutation разрушила бы намерения. |
| `CampaignBattleResolver`, `CampaignRegionField` | Score fingerprint и выдача; геометрический index | Ключ score включает изменяемые боевые факты; geometry index живёт вместе с геометрией, а ownership обновляет представление отдельно. |
| `HexSelectionController` | Hover cache и его потребители | CACHE-01. |
| UI hand/collection/tutorial/radio | Dirty subscriptions, Changed, layout/resize, периодическое обновление | Дополнительный доказанный stale-cache сценарий не найден; требуется native UI-проход. |
| Map resource/event displays | Registry reads, RefreshAll, восстановление представления | Представление перестраивается после restore без повтора игровых наград/действий. |
| Audio/settings, PlayerPrefs | Владелец настроек, Store/Changed, чтение hint progress | In-memory settings — владелец после загрузки; отдельного скрытого кеша файлов не найдено. |
| `ArmyDestructionEffect.FrameCache` | Ключ sheet name, создание sprite frames, lifecycle | Контентный кеш на процесс; нет привязки к старой армии. Риск editor hot reload указан ниже. |
| Diagnostics, constant tables, local dictionaries | Dedup/timing, фразы/цвета, списки сборки результата | Отделены от gameplay кешей; постоянные таблицы не требуют world invalidation. |

## Риски API и существующий техдолг

- `MapKnowledgeSnapshot.OnMapSet()` возвращает изменяемый `ISet`, а пересборка
  отслеживает смену ссылки AllHexes, но не изменение списка на месте. Текущие
  найденные потребители читают набор. Живая порча внешним writer не установлена.
- `EquipmentEfficiency` использует identity списка целей. Это корректно при
  нынешнем способе построения новых наборов, но контракт неизменности нужно
  сохранить при будущих правках. Не следует добавлять revision без реального writer.
- Manifest InputStamp не кодирует версию алгоритма builder: изменение только
  генератора может потребовать ручного Rebuild для editor Play. Build принудительно
  перестраивает manifest. Проблема оптимизации широкого сканирования остаётся
  отдельным техдолгом и требует измерений.
- `ArmyDestructionEffect` не проверяет валидность закешированных generated Sprite.
  Для hot reload или отключения Domain Reload нужен native lifecycle-тест.
  Текущая настройка `m_EnterPlayModeOptions: 0` не отключает Domain Reload;
  дефект обычного повторного запуска на конфигурации проекта не подтверждён.
- Mutable profile/campaign View и ослабленный API TacticHand относятся к уже
  запланированным работам по владению состоянием. Повторно объявлять их новыми
  cache-багами или создавать параллельный registry не нужно.

## Исполняемые проверки

Полный выбранный запуск: **111 тестов, 98 passed, 13 failed**. Производственная
ревизия не менялась. Переносимый runner компилировал копии runtime-исходников,
используя старые Unity reference DLL, .NET 8/NUnitLite и адаптеры недоступных native
API. Конструирование HexMap и уничтожение объектов адаптировались только во
временных копиях fixture. Это не компиляция и не запуск Unity 6000.5.4f1.

| Fixture | Всего | Passed | Остальное |
| --- | ---: | ---: | --- |
| AiCombatCacheLifecycleTests | 7 | 7 | — |
| AiEconomyAdmissionFingerprintTests | 11 | 11 | — |
| AiEquipmentDeltaCacheTests | 4 | 4 | — |
| AiRouteCacheIsolationTests | 6 | 4 | 2 native/reference errors |
| WorthItEstimateCacheTests | 28 | 26 | 1 native error; 1 несовпадение записанного hash |
| ExchangeMemoAndProfileEqualityTests | 3 | 3 | — |
| MovementEnvironmentTests | 12 | 7 | 5 native/reference errors при геометрии/поиске |
| CacheAuditProbes | 3 | 1 | 2 намеренно выявленные warm/cold нарушения |
| AiPowerOptimizationTests, вспомогательная fixture | 5 | 5 | — |
| AttachmentSlotTests, вспомогательная fixture | 32 | 30 | 2 ошибки разрешения authored content |

Дополнительный отдельный запуск трёх probes дал те же два нарушения; проверка
защиты возвращаемого массива ScarcityProfile прошла. Это повторяемые ошибки
утверждений, а не исключения native API.

Семь route/movement ошибок — вызовы reference/native Unity методов, например
`UnityEngine.Object.op_Inequality` и конструирование геометрии. Ещё один native error —
создание engine yield instruction в CachedTurnRunner. Они не доказывают ошибку
игрового кеша, но соответствующие проверки остаются незавершёнными в этой среде.
Две content-ошибки — `Sequence contains no matching element` при поиске authored
attachments во вспомогательной fixture.

Отдельно не разрешён recorded baseline в `SimulationResults_MatchRecordedBaseline`:
ожидался `2B6723C199EBA2CE8524B5E2A042D58823448B491233C51885BABAE947DA29AC`,
получен `602C93BE7F2FFEDEE36DBAF4D552CF5DFEEDB424284469AD4C2CA0464607DFD0`.
Это не объявлено ни новым дефектом кеша, ни заведомо безвредным свойством среды.
Причину нужно установить в настоящем Unity, сохранив старый baseline.

Для опубликованного FIX-01 отдельно ранее прошли 96 доступных переносимых проверок;
полная нативная Unity-проверка также не запускалась. Общая reference-компиляция
всех Assets имела одинаковые 20 ошибок (17 уникальных нормализованных diagnostics)
на базе и после FIX-01; новых diagnostics не было. Это не заменяет успешный Unity build.

## Воспроизведение CACHE-02 и CACHE-03

Ниже тело диагностических EditMode-тестов. Его можно временно включить в тестовую
сборку проекта; на проверенной ревизии два equality assertions должны выявить дефект.
Тесты не добавлены в обычный набор Assets, чтобы аудит не менял игровую сборку.

```csharp
using System;
using System.Collections.Generic;
using Game.Ai.V2;
using Game.Cards;
using Game.Economy;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class CacheAuditProbes
    {
        private static WorldSnapshot Snapshot(IReadOnlyList<CardDefinition> deck,
            IReadOnlyList<CardData> hand = null) => new WorldSnapshot
        {
            Self = new SelfSnapshot
            {
                Deck = deck, Hand = hand ?? Array.Empty<CardData>(),
                Stockpile = new ResourceBundle { Human = 5 }
            }
        };

        [Test]
        public void SupplyChangeWithinOneRoundedKey()
        {
            var card = new CardDefinition
                { resourceCost = new ResourceCost { human = 1, energy = 1 } };
            var snap = Snapshot(Array.Empty<CardDefinition>(), new[] { new CardData(card) });
            float Supply(ResourceType t, float h) => t == ResourceType.Human ? h
                : t == ResourceType.Energy ? 1 : 0;
            ActionPrice.ScarcityProfile(snap, t => Supply(t, .996f));
            var warm = ActionPrice.ScarcityProfile(snap, t => Supply(t, 1.004f));
            var cold = ActionPrice.ScarcityProfile(
                Snapshot(Array.Empty<CardDefinition>(), snap.Self.Hand),
                t => Supply(t, 1.004f));
            Assert.That(warm, Is.EqualTo(cold));
        }

        [Test]
        public void DeckCostEditWithSameCount()
        {
            var card = new CardDefinition { resourceCost = new ResourceCost { human = 1 } };
            var snap = Snapshot(new[] { card });
            ActionPrice.ScarcityProfile(snap);
            card.resourceCost.human = 10;
            var warm = ActionPrice.ScarcityProfile(snap);
            var cold = ActionPrice.ScarcityProfile(Snapshot(new[] { card }));
            Assert.That(warm, Is.EqualTo(cold));
        }
    }
}
```

## Оставшаяся приёмка в Unity

1. Запустить полный EditMode набор в Unity 6000.5.4f1, включая перечисленные
   route/movement, CachedTurnRunner, authored-content и recorded-baseline проверки.
2. Воспроизвести CACHE-01 с неподвижной мышью и изменением viewport; проверить команды
   и drop карты. Проверить также rebuild/restore карты на прежнем компоненте.
3. Для CACHE-02/03 проверить warm/cold регрессии и фактические production-переходы
   резерва/карточных данных без подмены snapshot; установить реальную достижимость.
4. Повторить матч → выход → новый матч → load; сравнить холодные и прогретые
   маршруты, погоду, туман, боевые оценки и показанные маркеры.
