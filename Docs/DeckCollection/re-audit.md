# Повторный аудит снизу вверх

Аудит кода опубликованного PR #159, исходный head `4c9c1639d2ee4f11244b3ddd682d0741e16bb375`. Это повторная проверка исходников/данных, **не доказательство компиляции или работы Unity UI**.

| Уровень | Проверено | Результат |
|---|---|---|
| Данные | 162 authoredKey, 151 collectible, системные исключения, все ссылки, сохранность старых combat fields и AI starter counts | Повторный read-only audit PASS; бюджеты 74/75/53 |
| Правила | Общая сумма Main + Equipment + Mutator, 100 очков, Owned и copy limit отдельно, чужая фракция, категории, unknown keys, удаление невалидных строк | Аудит исходников; тесты написаны, исполнение не подтверждено |
| Сохранение | Clone до изменения, atomic replacement/backup, ошибка записи до публикации состояния, unknown keys retained, future schema не затирается | Аудит исходников; усилена проверка структуры pending/claimed/selection |
| Runtime | Проверка до scene load и при формировании руки; отдельный snapshot/квота; AI vs AI без профиля; legacy fallback только без snapshot | Аудит исходников; добор остаётся DeckDraw |
| Производство | Canonical root, eligibility/catalog/actor/affordability до оплаты; pending duplicate и exhausted отклоняются; fail/cancel release, success расходует 1 | Единственная прежняя PayCardCost; токен не резервирует ресурсы |
| Награды | Победа до 5 разных / выбор до 2; поражение до 1; caps; persist-before-display; matchId; early exit / AI / draw | Найден и исправлен случай устаревшего pending offer |
| UI | Три колонки в 1024×768, input overlay, preview click/hover, modal ESC, dirty guard, setup transition, scrolling | Исправлены startup error и сброс scroll; реальный layout/фокус/игровой цикл не подтверждены |
| AI / ресурсы / cache | Новые слои не вызывают AI bank, reservations, миссии или tactical cache; профиль не меняет активный loadout | Аудит границ; игровые регрессии всё ещё требуют Unity |

## Исправления после повторной проверки

1. `CollectionRewardUI.Resume` теперь показывает ошибку загрузки и сообщение о восстановлении backup. Раньше Initialize failure при открытии меню мог остаться невидимым до входа в Collection.
2. `RewardService.AvailableOffers` использует текущий каталог/допуск/Owned/лимит для показа и подтверждения pending reward. Исходные offeredKeys сохраняются. Удалённые/исчерпанные предложения не заменяются случайными картами; UI сообщает, сколько недоступно. Если не осталось допустимых предложений, можно подтвердить пустой результат и продолжить. Новая выдача/старые Owned при этом не подменяются и не обрезаются.
3. `CollectionProfileStore.ValidateShape` отвергает duplicate offer/acquired/claimed identities, несовпадающий claimed receipt, неизвестный outcome и дубли выбора одной фракции. Невалидные составы колод и неизвестные существовавшие ключи по-прежнему сохраняются для исправления, не вызывая сброса коллекции.
4. `CollectionScreensUI.Draw` сохраняет normalized scroll всех колонок при изменении количества/сохранении. При смене фракции, категории или выбранной колоды позиция сбрасывается явно.

Добавлены четыре EditMode-регрессии: удалённое предложение без random substitution, исчерпанные после обновления лимиты, duplicate reward identities, защита future schema от fallback/overwrite. Всего 33 Collection tests и 1 новый transaction regression. **Не запускались.**

## Повторно выполнено

- `python Tools/deck-collection/verify_content.py --base HEAD` в локальном исходном connector snapshot: PASS (HEAD здесь соответствует исходным данным, не PR head).
- `verify_full.js` и `verify_types.js`: PASS для всех 9 изменённых YAML файлов.
- `git diff --check`: PASS.

## Что нельзя считать закрытым

Компиляция Unity 6000.5.4f1, полный EditMode suite, game loop, производственные анимации/cancel/Fate, UI layout и focus, сохранение/backup на целевой платформе не выполнялись. Стоимости остаются исходной фиксированной калибровкой: канонический attachment utility report и 18 strategy match comparisons ещё требуется выполнить в Unity. Набросок интерфейса — схема кодовой компоновки; изображения карт показаны заглушками, это не screenshot редактора.
