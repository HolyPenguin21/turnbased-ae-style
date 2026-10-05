# turnbased-ae-style

Проект пошаговой стратегической игры на Unity/C#.

## Запуск локально

1. Клонировать репозиторий:
   ```bash
   git clone https://github.com/HolyPenguin21/turnbased-ae-style.git
   cd turnbased-ae-style
   git switch master
   ```
2. Установить через Unity Hub редактор версии из [ProjectVersion.txt](ProjectSettings/ProjectVersion.txt). На момент добавления инструкции: **6000.5.4f1**.
3. Добавить в Unity Hub корень репозитория, содержащий Assets, Packages и ProjectSettings.
4. Открыть проект и дождаться импорта ассетов и восстановления пакетов из Packages.
5. Открыть [MainMenu](Assets/Scenes/MainMenu.unity) и нажать Play. Также в проекте есть сцена [Game](Assets/Scenes/Game.unity); обе включены в EditorBuildSettings.

Запуск в Unity при добавлении этой документации не проверялся. Если появляются ошибки, сохранить первый блок ошибок из Console и версию редактора для диагностики.

## Структура

| Путь | Назначение |
| --- | --- |
| Assets/ | Игровой код, сцены и ассеты |
| Assets/Editor/ | Инструменты редактора и EditMode тесты |
| Packages/ | Зависимости Unity |
| ProjectSettings/ | Настройки проекта и версия редактора |
| Docs/ и docs/ | Документация, аудиты и планы; регистр путей важен |
| Tools/ | Проверочные и вспомогательные инструменты вне Unity |
| AGENTS.md | Постоянные инструкции для работы агента с репозиторием |

## Проверка изменений

### В Unity
Использовать редактор версии проекта. Проверить компиляцию, выполнить EditMode тесты через Test Runner и воспроизвести затронутый игровой сценарий.

### AI в среде без Unity
Подробные требования и ограничения: [Tools/ai-verify/README.md](Tools/ai-verify/README.md).

До изменения кода сохранить SHA исходной ревизии и compile baseline:
```bash
git rev-parse HEAD
Tools/ai-verify/setup.sh
Tools/ai-verify/compile_check.sh --baseline
```

После изменений:
```bash
Tools/ai-verify/compile_check.sh
Tools/ai-verify/test_regress.sh <base-sha>
```

Заменить <base-sha> сохранённым SHA до изменений. setup.sh устанавливает системные зависимости; перед запуском прочитать его и README инструмента.

Это дифференциальные проверки с reference DLL и заглушками. Они не заменяют сборку и полный набор тестов в Unity. Сведения о количестве тестов и baseline в документации инструмента могут меняться.

### После ручного изменения сцен или префабов
Для каждого изменённого YAML файла выполнить обе проверки:
```bash
node Tools/unity-yaml-verify/verify_full.js Assets/Scenes/Game.unity
node Tools/unity-yaml-verify/verify_types.js Assets/Scenes/Game.unity
```

Заменить пример пути изменёнными файлами. Ограничения проверок: [Tools/unity-yaml-verify/README.md](Tools/unity-yaml-verify/README.md).

## Работа через ChatGPT и GitHub

- Постоянные правила репозитория находятся в [AGENTS.md](AGENTS.md); в начале задачи попросить агента прочитать его.
- В задаче указывать ожидаемое поведение, наблюдаемый сбой, шаги воспроизведения и целевую ветку.
- Для разных задач использовать отдельные чаты внутри проекта ChatGPT.
- Читать актуальные исходники из GitHub, а не полагаться на загруженные ранее копии.
- GitHub-коннектор позволяет читать и изменять файлы, но не запускает Unity. Результаты проверок в редакторе нужно получать в среде с установленным Unity.
- Коммит, push или merge указывать в задаче явно.
