# CompositeCardArt.ps1

Скрипт для ВСЕХ .png в указанной папке (батч, без рекурсии в подпапки):
1. накладывает альфу (прозрачность по краям) на сырую иллюстрацию — по параметрам ниже;
2. накладывает получившееся изображение на базу карты (`Card_Base.png`).

На каждое исходное изображение сохраняются ДВА результата:

| Выход | Что это | Имя файла |
|---|---|---|
| Output 1 (`-OutDir1`) | Низ затёрт (вытерт) под текст описания карты | как у исходника, без изменений |
| Output 2 (`-OutDir2`) | Полная иллюстрация, низ НЕ затирается — просто симметричное затухание сверху/снизу, как по краям | как у исходника + `_Full` |

## Рабочие папки

| Папка | Что там |
|---|---|
| `Assets\Textures\General\Card_Base.png` | База карты (рамка) — уже готовый файл, лежит здесь всегда, трогать/генерировать заново не нужно. Скрипт берёт его отсюда автоматически. |
| `Assets\Textures\Units\IronConcord\` | Сюда класть сырые (ещё без рамки) иллюстрации юнитов/героев после генерации в ComfyUI. |
| `Assets\Textures\Units\General\` | Сюда класть сырые иллюстрации facility (зданий), которые не привязаны к конкретной фракции. |
| `Assets\Textures\Units\IronConcord\GameCards\` | Сюда скрипт по умолчанию сохраняет Output 1 (карты юнитов/героев). |
| `Assets\Textures\Units\IronConcord\GameCards_Full\` | Сюда скрипт по умолчанию сохраняет Output 2 (та же папка, но `_Full`). |
| `Assets\Textures\Units\General\GameCards\` / `GameCards_Full\` | То же самое, но для facility — получится автоматически, если `-InputFolder` указывает на `General\`. |

## Параметры

| Параметр | Обязательный | По умолчанию | Что делает |
|---|---|---|---|
| `-InputFolder` | да | — | Папка с сырыми иллюстрациями (PNG). Обрабатываются ВСЕ .png прямо в этой папке (без подпапок, другие форматы игнорируются). |
| `-OutDir1` | нет | `<InputFolder>\GameCards` | Куда сохранять Output 1 (с затёртым низом). |
| `-OutDir2` | нет | `<InputFolder>\GameCards_Full` | Куда сохранять Output 2 (без затёртого низа). |
| `-SideFeatherPercent` | нет | `15` | Ширина затухания слева/справа, % от ширины. Одно значение — общее для обоих выходов. |
| `-TopFeatherPercent` | нет | `15` | Высота затухания сверху, % от высоты. Только для Output 1. |
| `-BottomFadeStartPercent` | нет | `50` | С какой высоты (%) начинается затухание низа — только для Output 1. |
| `-BottomFadeEndPercent` | нет | `72` | На какой высоте (%) низ становится полностью прозрачным (место под текст описания) — только для Output 1. |
| `-TopBottomFeatherPercent` | нет | `15` | Высота затухания сверху И снизу (то же значение для обоих краёв), % от высоты — только для Output 2. |

## Пример запроса

```powershell
powershell -ExecutionPolicy Bypass -File "CompositeCardArt.ps1" -InputFolder "d:\Unity\Project\My_project\Assets\Textures\Units\IronConcord" -SideFeatherPercent 15 -TopFeatherPercent 15 -BottomFadeStartPercent 50 -BottomFadeEndPercent 72 -TopBottomFeatherPercent 15
```

Со своими выходными папками:

```powershell
powershell -ExecutionPolicy Bypass -File "CompositeCardArt.ps1" -InputFolder "IronConcord" -OutDir1 "IronConcord\GameCards" -OutDir2 "IronConcord\GameCards_Full"
```

Полностью сторонние папки для входа и обоих выходов, с переопределением верхнего/симметричного затухания:

```powershell
powershell -ExecutionPolicy Bypass -File "CompositeCardArt.ps1" -InputFolder "d:\Unity\Project\My_project\Assets\Textures\Units\InputImages" -OutDir1 "d:\Unity\Project\My_project\Assets\Textures\Units\Output_GameCards" -OutDir2 "d:\Unity\Project\My_project\Assets\Textures\Units\Output_DetailView" -TopFeatherPercent 10 -TopBottomFeatherPercent 10
```

## Инструкция запуска (Windows 11)

1. Нажми `Win`, введи `PowerShell`, открой обычный **Windows PowerShell** (не нужно от
   администратора).
2. Перейди в папку со скриптом:
   ```powershell
   cd "D:\Unity\Project\My_project\Assets\Textures\Units"
   ```
3. Положи сырые иллюстрации из ComfyUI в `IronConcord\` (для юнитов/героев) или `General\`
   (для facility) — см. таблицу "Рабочие папки" выше. Можно положить сразу несколько — скрипт
   обработает все за один запуск.
4. Запусти скрипт, указав `-InputFolder` (пример команды — выше):
   ```powershell
   powershell -ExecutionPolicy Bypass -File "CompositeCardArt.ps1" -InputFolder "IronConcord"
   ```
5. Скрипт сразу печатает `Input folder: ...` и обе выходные папки, затем список найденных
   файлов, и дальше по каждому изображению — прогресс по шагам (`Feathering: 40% (row
   481/1216, ...)` и т.п.) — если строчки не бегут, значит реально завис, а не "молчит, но
   работает". Дождись строки `Done: ...` (с итоговым числом файлов) — готовые карты появятся
   в `GameCards\` и `GameCards_Full\` (или там, куда указал `-OutDir1`/`-OutDir2`).
6. Если Unity открыт, переключись на него — он сам увидит новые файлы и заимпортирует.
   Останется вручную выставить `Texture Type → Sprite (2D and UI)` и `Alpha Is Transparency` в
   инспекторе и подключить нужные карты (обычно из `GameCards\`, не `GameCards_Full\`) в каталог
   (`CardCatalog_IronConcord.asset`).
