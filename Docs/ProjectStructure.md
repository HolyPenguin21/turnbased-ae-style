# Структура файлов проекта

Ассеты сгруппированы по назначению. Имена файлов, классов и пространства имён
сохранены. При переносе ассета его `.meta` переносится вместе с ним.

| Папка | Назначение |
| --- | --- |
| `Assets/Textures/UI/Buttons` | Кнопки, включая действия панели хекса |
| `Assets/Textures/UI/Panels` | Панели, включая панели армии и хекса |
| `Assets/Textures/UI/Icons` | UI-иконки и иконки действий |
| `Assets/Textures/UI/Indicators` | Индикаторы состояния |
| `Assets/Textures/UI/Backgrounds` | Фоны интерфейса |
| `Assets/Textures/UI/Cards`, `Tokens`, `Decorations` | Элементы карточек, кубики и декор |
| `Assets/Textures/Game/General/Armies`, `Bases`, `Citadel`, `Events` | Изображения объектов карты |
| `Assets/Textures/Game/General/Flags`, `Factions`, `Icons`, `MapMarkers` | Флаги, логотипы, иконки карты и маркеры |
| `Assets/Audio/Music`, `Effects`, `Mixers` | Музыка, звуки и микшеры |
| `Assets/Prefabs/UI/Buttons`, `Panels`, `Cards`, `Tokens`, `Rows` | Переиспользуемые UI-префабы по типу |
| `Assets/Prefabs/UI/ArmyRadio`, `BattleScreen`, `Campaign`, `Collection` | Связанные элементы конкретного экрана или функции |
| `Assets/Scripts/UI` | UI-скрипты в подпапках по типу или функции |
| `Assets/Editor/Drawers` | Инспекторные отрисовщики |
| `Assets/Editor/Builders` | Инструменты сборки UI в редакторе |
| `Assets/Editor/Tools` | Аудиты, калибровка и отчёты |
| `Assets/Editor/Tests` | Тесты, сгруппированные по подсистемам |

## Resources

Ассеты, загружаемые через `Resources.Load`, остаются внутри `Assets/Resources`.
UI-ресурсы лежат в `UI/Buttons`, `UI/Panels`, `UI/Controls`, `UI/Backgrounds` и
`UI/Decorations`, затем разделяются по экрану (`Campaign`, `Collection`, `Rewards`).
Фоны планеты лежат в `Campaign/Backgrounds`, облака — в `Campaign/Effects`,
шейдер поверхности — в `Shaders/CampaignSurface.shader`.

При новом переносе нужно обновить строковые пути `Resources.Load` и
`AssetDatabase.LoadAssetAtPath`, пути в инструментах проверки и документации.
Поиск UI-скриптов и редакторских тестов должен учитывать вложенные папки.
UI-скрипты остаются вне `Editor`, редакторские инструменты и тесты — внутри неё.

## Проверки переноса

- Исходные GUID и содержимое перенесённых `.meta` сохраняются.
- Изображения, аудио, сцены, префабы и микшер используют исходные Git blobs.
- Новые папки получают собственные `.meta` с уникальными GUID.
- Ссылки по GUID сохраняются; строковые пути обновляются вместе с переносом.
- Окончательная проверка импорта и EditMode-тестов выполняется в Unity 6000.5.4f1.
