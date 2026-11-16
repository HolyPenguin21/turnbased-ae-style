# AI: сильные бойцы застревают в гарнизонах, в атаку уходят слабые (2026-11-16)

Матч: `Logs/AiDebug.log` 2026-11-16 12:01 (Cassia vs Rurik, T1–T29), ревизия `7b87e1be`.
Статус: реализовано (Ф1–Ф3), см. раздел «Реализация».

## Симптом (лог)

| Ход | Гарнизон (хекс) | Остаётся в гарнизоне | Уходит в атаку |
|---|---|---|---|
| Rurik T25–29 | Цитадель (2,4), 73.2, состав неизменен | Boomstick, Blister Juggernaut, 2×Rust Tank, Rad Brute, Iron Husk | Thralls 54.7 (Tech Scrapper, Ash Runner, …) |
| Rurik T26–29 | База (1,1), 66–74 | Boomstick, 2×Roadbreaker, Iron Maw | Cold Blood 6.4 (Vera + Rad Hunter) |
| Cassia T27 | База (2,-1), 61.8, floor 45.2 | Sandnought (13.9) | Talon Corps: Mule + RC Vehicle (`Assemble ... transfers=[Mule<-#36,RC Vehicle<-#36]`) |

Механизм лучшего атакующего состава работает (`AttackContinuity.RefreshTargetRoster`):
- Rurik: `[hank-mercer*, iron-maw, boomstick, iron-maw, roadbreaker, roadbreaker]`. Недостающие позиции стоят в гарнизоне (1,1), где запас `spare=0/4`.
- Cassia: `[kessa-vrail*, sandnought×2, coil×3, atlas]`. Sandnought на T26 ушёл в гарнизон через `Refit window=GarrisonFloor ... swapped Medium infantry out for Sandnought`.

## Цепочка (кто решает, где стоит боец)

```
ВХОД В ГАРНИЗОН
 GarrisonDefenceFloor = max(share, ThreatFloor) − ResidentFieldPower      (AiArmyRoles)
   └► held_base_garrison demand (AggressionDemandEvaluator.Garrison)
        GarrisonHandGainCap: выигрыш по СИЛЬНЕЙШИМ картам руки
        Delivery: только DeploymentKind.Garrison
        └► AttackBaseRefitPolicy, окно GarrisonFloor: гарнизон полон →
           сильная карта ВНУТРЬ, слабейший боец НАРУЖУ в полевую армию   [П2]
 Housekeeping: fold полевых армий в гарнизон при угрозе (норма, решение владельца)

ВЫХОД ИЗ ГАРНИЗОНА
 AiArmyRoles.SpareableBodies (единственное правило)                       [П1]
   ├► FieldStrikePotential (ворота мобилизации)
   ├► AttackForcePool → StrikePool / AttackPeak / TargetRoster
   ├► ArmySnapshot.NonHeroSpareable → GarrisonDetachment (Secondary, AD sortie, Relief),
   │    EconomyBuilderSelection (эскорт)
   ├► CanSpareGarrisonMembers → AssembleSameHex (подготовка, Raid, штурм), провижнеры
   └► Housekeeping GarrisonMayRelease, PreparationBlockedSlots

ВОЗВРАТ ЛУЧШИХ В АТАКУ                                                    [П3]
 TargetRoster строится только из запаса → сильные тела гарнизона не попадают в план
 Assemble: только долив свободных слотов, обмена с гарнизоном нет
 Housekeeping: обмен тел только поле↔поле (4b); гарнизон участвует в обмене только героями;
   защищённый хост операции (ProtectedMissionArmy) не может быть стороной обмена (T05)
 0.8 «release non-roster body» работает только в фазе Gather (PreparationTargetKeys)
   и только если источник позиции «blocked», а это опять считается через запас
 Резерв ростера (PhaseAOperationSupport.LeavesRosterFunding) действует только в фазе Gather
```

## Корни

**П1. `SpareableBodies` выпускает самых слабых.**
- Комментарий метода говорит «уходят сильнейшие», код делает обратное.
- Шаг 1: фиксируется максимальное число уходящих тел.
- Шаг 2: максимизируется `released − shortfall`. Сила почти аддитивна, поэтому у любого набора, переходящего за floor, оценка одинаковая: `held − floor`. Решает тай-брейк «оставить сильнейшего».
- Итог: бюджет `held − floor` делится на максимальное число самых дешёвых тел.
- Python-копия алгоритма на составе Cassia T27 дала Mule+RC, как и в логе.
- Ветка `n > 10` явно оставляет сильнейших.
- Все 8 потребителей сортируют «сильнейшие первыми», но получают уже отфильтрованный список слабых. Их порядок фактически не работает.

**П2. Вход в гарнизон забирает сильнейшие карты руки.**
- `GarrisonHandGainCap` считает выигрыш по сильнейшим картам. Окно `GarrisonFloor` меняет сильную карту на слабейшего бойца гарнизона и выталкивает его в поле.
- `Excluded()` окна не учитывает позиции `TargetRoster`.
- Резерв ростера не действует вне фазы Gather. Так Sandnought ушёл в гарнизон на T26, когда Ashguard был в фазе Reinforcement.
- Это храповик: сильные входят, слабые выходят.

**П3. Возврат лучших в атаку не реализован ни на одном уровне.** См. блок «Возврат» в графе выше.

**Вторичное (решение владельца 2026-10-17).**
- Угроза засчитывается и Цитадели, и ближайшему гарнизону.
- У Rurik пороги 74 + 88 при силе противника примерно 70. Это усиливает П1, корнем не является.

## Предлагаемые исправления

**Ф1. Уровень Foundation/roles: `AiArmyRoles`.**
- Правило запаса отвечает на вопрос «может ли этот НАБОР уйти»: оставшиеся держат floor, хотя бы одно тело остаётся.
- Пообъектный флаг означает «это тело может уйти одно».
- Кого брать, решает потребитель:
  - Attack, Raid, вылазки: сильнейшие первыми (как уже написано в их коде);
  - эскорт Economy: дешёвые по AP.
- Многотельные планировщики проверяют набор кумулятивно, тем же правилом.
- Почему не «сильнейшие первыми» внутри правила: эскорт Economy начал бы забирать сильнейших.

**Ф2. Уровень Housekeeping/Missions: возврат лучших.**
- Обмен «боец хоста не из ростера ↔ позиция ростера в гарнизоне» на том же хексе.
- Условие: гарнизон после обмена держит `min(was, floor)`. Это существующее правило `GarrisonRosterKeepsFloor`; его нужно вынести в `AiArmyRoles` как единое правило обмена.
- Нужно решить, давать ли защищённому хосту атаки право отдавать тело в обмен (сейчас T05 запрещает) и во всех ли фазах атаки.

**Ф3. Уровень Materialization/Strategy.**
- Окно `GarrisonFloor` и спрос на гарнизон не берут карты позиций живого `TargetRoster`.
- Резерв ростера распространяется на фазы атаки после Gather, пока позиция не занята.

## Решения владельца

1. Исключение Vex T22 («небольшой недобор ради сильного бойца»). С правилом по наборам оно не нужно; предложение — убрать.
2. Ф2: может ли хост живой атаки менять тела с гарнизоном, и в каких фазах.
3. Порядок работ: Ф1 → Ф3 → Ф2.

## Реализация (решения владельца 2026-11-16: всё принято, исключение Vex T22 убрано)

**Ф1. Уровень Foundation/roles: `AiArmyRoles`.**
- `MayReleaseBodies` — единое правило отпуска НАБОРА: оставшиеся держат floor, одно тело остаётся.
- `SpareableBodies` — стратегический вид «сильнейшие первыми, пока набор можно отпустить». Его читают меры силы (`FieldStrikePotential`, `AttackForcePool`), источники ростера в Housekeeping и `IsStrikeSpareBody` (приоритет экипировки).
- `CanSpareGarrisonMembers` проверяет сам уходящий набор.
- Флаги `ArmySnapshot.NonHeroSpareable` означают «может уйти один». Многотельные планировщики проверяют набор через `GarrisonDetachment.MayRelease` на пороге из `SelfSnapshot.GarrisonReleaseFloors`. Порог лежит в `SelfSnapshot`, потому что тот не сохраняется. `ArmySnapshot` сохраняется кодеком, поэтому новых полей в него не добавлялось. Так переведены:
  - `AttackSecondaryPolicy`;
  - вылазка ActiveDefence;
  - `GarrisonReliefObjectiveEvaluator`;
  - `AttackPreparationPolicy.SpareGarrisonPower`;
  - `RaidRecoveryPlanner` (несколько тел подряд из одного гарнизона).
- Эскорт Economy при равных AP и скорости берёт более слабое тело.

**Ф2. Уровень Housekeeping.**
- `AiArmyRoles.KeepsFloorAfterExchange` и его живой двойник `GarrisonExchangeKeepsFloor` — единое правило обмена.
- Шаг 0.86 в `ArmyReorganizationCandidates`: хост подготовки меняет тело не из ростера на тело позиции ростера из гарнизона, 1-на-1 (`TrySwap(rosterExchange)`).
- `PreparationRosterGap` считает такой источник ожидающим (pending), но не поводом выпускать тело хоста в свободный контейнер.
- Исполнитель: `IsPreparationRosterExchange` + `CommonPreflight(rosterExchange)` + проверка пола в `PreflightSwap`.
- Охват: фаза Gather (контракт PreparationHost). В Reinforcement хост работает по контракту MovingOperation и в обмене не участвует.

**Ф3. Уровни Analysis + Materialization.**
- `StrikeRoster.IsReservedRosterCard` закрывает доставку в гарнизон (`MaterializationChainEnumerator` для формы Garrison и окно рефита `GarrisonFloor`). Действует для карт позиций живого `TargetRoster` в фазах Gather и Reinforcement.
- Спрос просто выбирает другую карту, а не блокируется.

**Не менялось (сознательно).**
- Экстренная оборона: решатель реакции на угрозу, защитник при основании базы.
- Порог угрозы, засчитываемый Цитадели и ближайшему гарнизону.
- Размер спроса `GarrisonHandGainCap`.
