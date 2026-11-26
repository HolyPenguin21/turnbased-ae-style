# Структура файлов проекта

Ассеты сгруппированы по назначению. Имена файлов, классов и пространства имён
сохранены. При переносе ассета его `.meta` переносится вместе с ним.

| Папка | Назначение |
| --- | --- |
| `Assets/Textures/UI/Buttons` | Кнопки, включая действия панели хекса |
| `Assets/Textures/UI/Panels` | Панели, включая панели армии и хекса |
| `Assets/Textures/UI/Icons` | UI-иконки и иконки действий |
| `Assets/Textures/UI/Controls`, `Decorations`, `Effects` | Элементы управления, декор и атлас эффектов боя |
| `Assets/Textures/Game/General/Armies`, `Citadel`, `Events` | Изображения объектов карты |
| `Assets/Textures/Game/General/Flags`, `Factions`, `Icons` | Флаги, логотипы и иконки карты |
| `Assets/Textures/Campaign` | Фоны, эффекты и элементы экрана кампании |
| `Assets/Shaders` | Шейдеры |
| `Assets/Audio/Music`, `Effects`, `Mixers` | Музыка, звуки и микшеры |
| `Assets/Prefabs/UI/Buttons`, `Panels`, `Cards`, `Tokens`, `Rows` | Переиспользуемые UI-префабы по типу |
| `Assets/Prefabs/UI/ArmyRadio`, `BattleScreen`, `Campaign`, `Collection` | Связанные элементы конкретного экрана или функции |
| `Assets/Scripts/UI` | UI-скрипты в подпапках по типу или функции |
| `Assets/Editor/Drawers` | Инспекторные отрисовщики |
| `Assets/Editor/Tools` | Аудиты, калибровка и отчёты |
| `Assets/Editor/Tests` | Тесты, сгруппированные по подсистемам |

## Resources

Код загружает ассеты по строковому ключу через `GameAssetCatalog.Load`
(`Assets/Resources/GameAssetCatalog.asset`). Ключи повторяют прежние пути `Resources`
без расширения; соответствие «папка → префикс ключа» задаёт `GameAssetCatalogBuilder`
(например, `Assets/Textures/Campaign` → `Campaign`, `Assets/Shaders` → `Shaders`).
Через `Resources.Load` напрямую загружаются только сам каталог, `MatchSaveAssetManifest`
и локальные атласы `ArmyDestruction`.

При новом переносе нужно обновить корни в `GameAssetCatalogBuilder`, строковые пути
`AssetDatabase.LoadAssetAtPath`, пути в инструментах проверки и документации.
Поиск UI-скриптов и редакторских тестов должен учитывать вложенные папки.
UI-скрипты остаются вне `Editor`, редакторские инструменты и тесты — внутри неё.

## Проверки переноса

- Исходные GUID и содержимое перенесённых `.meta` сохраняются.
- Изображения, аудио, сцены, префабы и микшер используют исходные Git blobs.
- Новые папки получают собственные `.meta` с уникальными GUID.
- Ссылки по GUID сохраняются; строковые пути обновляются вместе с переносом.
- Окончательная проверка импорта и EditMode-тестов выполняется в Unity 6000.5.4f1.
