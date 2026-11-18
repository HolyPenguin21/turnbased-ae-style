# Проверка кешей и переносимые измерения

`run.py` компилирует все runtime-исходники с выбранными настоящими NUnit fixtures.
Использует существующие адаптеры из `Tools/match-save-verify/audit-world/native-run.py`
и дополнительные явно указанные в скрипте замены только в рабочих копиях.
Ни один адаптер не попадает в `Assets`.

Нужны Python 3.12+, .NET SDK 8, зависимости из `Tools/ai-verify`:
UnityEngine.Modules 2021.3.33, Unity3D.UnityEngine.UI 2020.3.21, NUnit/NUnitLite 3.14.0.
Переменные `DOTNET` и `NUGET_PACKAGES` должны указывать на установленные зависимости.
`--work` — отдельная временная папка вне репозитория, которую runner вправе пересоздавать.

```bash
export DOTNET=/path/to/dotnet
export NUGET_PACKAGES=/path/to/packages

# База использует исходники, тесты и каталоги указанной Git-ревизии.
python Tools/cache-verify/run.py --work /tmp/cache-base --baseline ab66ad1
python Tools/cache-verify/run.py --work /tmp/cache-current

# Сравнивайте общие тесты по полным именам. В каждом каталоге есть result.xml.
python Tools/cache-verify/run.py --work /tmp/cache-focused \
  --where 'class == Game.EditorTests.AiActionPriceCacheTests'

# Измерения выполнять ПОСЛЕДОВАТЕЛЬНО, без параллельных сборок/тестов.
python Tools/cache-verify/run.py --work /tmp/cache-perf-base --performance --baseline ab66ad1
python Tools/cache-verify/run.py --work /tmp/cache-perf-current --performance

# Проверка снизу вверх: callbacks, ключи, scopes, одинаковый маршрут,
# граница ёмкости и удержание после EndMatch.
python Tools/cache-verify/run.py --work /tmp/cache-layers --bottom-up
python Tools/cache-verify/run.py --work /tmp/cache-layers-base --bottom-up --baseline ab66ad1
```

Runtime компилируется с оптимизациями; tiered JIT отключён одинаково для обеих
версий. Для sparse checkout runner подготавливает четыре настоящих карточных
каталога во временном каталоге. `CachePerformanceProbe.cs` выводит `CACHE_BENCH`:
название, число вызовов в окне, медиана микросекунд и байт на вызов по семи окнам.
Подготовка snapshot, делегатов и прогрев выполняются вне измеряемого интервала.
Cold scarcity использует заранее созданный новый snapshot на каждый вызов.
Cold grid включает одну запись SetTerrainAt и пересборку. Cold route использует
разные конечные хексы; его маршрут не равен по длине фиксированному warm-сценарию.
Один вызов таймера и счётчика allocations на окно не вычитается.

`CACHE_COUNTS` для WorthIt относится к последнему окну; aviation — ко всему
performance-тесту (счётчики накопительные), equipment — к последнему окну после
одного первоначального вычисления. Delta-проверка измеряет служебный lookup,
а не стоимость настоящего вычисления equipment utility. Замеры не имеют порогов
pass/fail и не доказывают скорость или GC-паузы на целевой платформе.

Ограничения: старые reference DLL и .NET 8 не заменяют Unity 6000.5.4f1/Mono/IL2CPP.
Native object lifetime, камера, Input System, rendering и authored asset resolution
полностью здесь не проверяются. В выбранном наборе есть известные недоступные
сценарии и отдельный recorded-baseline failure; не обнуляйте baseline ради зелёного
результата. Сравнивайте конкретные test-case имена и причины сбоев.

`--bottom-up` компилирует дополнительный `BottomUpCacheProbe.cs` и запускает только
его. `CACHE_SAMPLE` содержит все семь окон: scenario, window, calls, us/call,
bytes/call и число Gen0 collections за окно. Подготовка/reflection/counters обходов
списков не входят в timed lookup; отдельные `CACHE_READS`, `CACHE_CAPACITY`,
`CACHE_KEY_SIZE`, `CACHE_LIFETIME` — диагностические проходы вне таймера.
Флаг несовместим с `--performance`; game runtime при измерении не изменяется.

Живой resource callback вызывает настоящий StrategicSpendability/TurnResourceBook.
Карточный сценарий использует четыре положительных компонента цены и не содержит
доказанного starvation block. Development key-сценарий содержит 64 карты колоды и
0 либо 6 snapshot-армий по 12 одинаковых профилей. Для шести армий дополнительно
измеряется геометрия карты из 469 хексов. Live recipients, facilities, sightings
и active missions в этом сценарии отсутствуют. Замер изолирует сборку ключа.

Route miss очищает только существующий словарь маршрутов перед поиском того же
24-шагового пути (Clear включён во время); grid и знания остаются прогретыми.
Witness измеряется отдельно: он намеренно копируется. `ScopeTiming` переключается
отдельно, поскольку в настоящем AI runner сбор диагностик включён.
Capacity-проверка читает настоящую memo-таблицу через reflection вне timed-секции.
Lifetime-проверка вызывает настоящий `MatchStateInventory.EndMatch`, но не моделирует
native scene unloading или размер native объектов.

В Unity запустите `HoverCacheUnityTests` (пять correctness-сценариев).
Отдельно, явно выбрав тест, запустите
`Game.EditorTests.HoverCacheUnityTests.MeasureIdleHoverCacheAgainstDirectLookup`:
это Explicit native-замер cached/direct пути с настоящими камерой и мышью.
Он выводит `NATIVE_HOVER_BENCH`, не задаёт машинозависимых assertions скорости.
Затем выполните полный EditMode-набор и обычный игровой сценарий.
