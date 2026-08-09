# CompositeCardArt.ps1

Скрипт:
1. накладывает альфу (прозрачность по краям) на сырую иллюстрацию — по параметрам ниже;
2. накладывает получившееся изображение на базу карты (`Card_Base.png`).

## Рабочие папки

| Папка | Что там |
|---|---|
| `Assets\Textures\General\Card_Base.png` | База карты (рамка) — уже готовый файл, лежит здесь всегда, трогать/генерировать заново не нужно. Скрипт берёт его отсюда автоматически. |
| `Assets\Textures\Units\IronConcord\` | Сюда класть сырые (ещё без рамки) иллюстрации юнитов/героев после генерации в ComfyUI. |
| `Assets\Textures\Units\General\` | Сюда класть сырые иллюстрации facility (зданий), которые не привязаны к конкретной фракции. |
| `Assets\Textures\Units\IronConcord\GameCards\` | Сюда скрипт сохраняет готовые карты юнитов/героев (папка по умолчанию для `-OutDir`). |
| `Assets\Textures\Units\General\GameCards\` | Готовые карты facility — для них при запуске указывай `-OutDir` на эту папку. |

## Параметры

| Параметр | Обязательный | По умолчанию | Что делает |
|---|---|---|---|
| `-ArtPath` | да | — | Путь к сырой иллюстрации (PNG). |
| `-OutName` | да | — | Имя итогового файла без расширения. |
| `-OutDir` | нет | `IronConcord\GameCards` | Куда сохранить результат. |
| `-SideFeatherPercent` | нет | `15` | Ширина затухания слева/справа, % от ширины. |
| `-TopFeatherPercent` | нет | `15` | Высота затухания сверху, % от высоты. |
| `-BottomFadeStartPercent` | нет | `50` | С какой высоты (%) начинается затухание низа. |
| `-BottomFadeEndPercent` | нет | `72` | На какой высоте (%) низ становится полностью прозрачным (место под текст описания). |

## Пример запроса

```powershell
powershell -ExecutionPolicy Bypass -File "CompositeCardArt.ps1" -ArtPath "d:\Unity\Project\My_project\Assets\Textures\Units\IronConcord\IC_ATInfantry_01.png" -OutName "IC_Card_ATInfantry_01" -SideFeatherPercent 15 -TopFeatherPercent 15 -BottomFadeStartPercent 50 -BottomFadeEndPercent 72
```

## Инструкция запуска (Windows 11)

1. Нажми `Win`, введи `PowerShell`, открой обычный **Windows PowerShell** (не нужно от
   администратора).
2. Перейди в папку со скриптом:
   ```powershell
   cd "D:\Unity\Project\My_project\Assets\Textures\Units"
   ```
3. Положи сырую иллюстрацию из ComfyUI в `IronConcord\` (для юнита/героя) или `General\`
   (для facility) — см. таблицу "Рабочие папки" выше.
4. Запусти скрипт, подставив свои `-ArtPath` и `-OutName` (пример команды — выше):
   ```powershell
   powershell -ExecutionPolicy Bypass -File "CompositeCardArt.ps1" -ArtPath "IronConcord\my_unit_00001.png" -OutName "IC_Card_Unit_Scout_01"
   ```
5. Скрипт сразу печатает `Output folder: ...` (полный путь, куда сохранит результат) и дальше
   показывает прогресс по шагам (`Feathering: 40% (row 481/1216, ...)` и т.п.) — если строчки не
   бегут, значит реально завис, а не "молчит, но работает". Дождись строки `Saved: ...` (тоже с
   полным путём) — готовая карта появится в `GameCards\` (или там, куда указал `-OutDir`).
6. Если Unity открыт, переключись на него — он сам увидит новый файл и заимпортирует. Останется
   вручную выставить `Texture Type → Sprite (2D and UI)` и `Alpha Is Transparency` в инспекторе
   и подключить карту в каталог (`CardCatalog_IronConcord.asset`).
