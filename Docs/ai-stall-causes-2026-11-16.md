# AI: причины пата в матче 2026-11-16 (Cassia vs Rurik, T20–T29)

Лог: `Logs/AiDebug.log` 2026-11-16 12:01. После T23 между игроками не было ни одного боя. Продолжение
`docs/ai-attack-roster-garrison-2026-11-16.md` (сильные бойцы в гарнизонах).

## С1. Носитель Disruption стоил меньше атаки, которую открывает

**Было.** Атака Rurik на (2,-1): Corpse Grip (сила 39) держал HOLD `awaiting_disruption_carrier` с T20
по T26, затем ушёл по `observation_wait_expired`. Единственный носитель, Hooded #17, всё это время
выполнял `Scout(Explore -5,4)` с ценностью 17.2. `Scout(DisruptStructure 2,-1)` стоила 13.1:
`AttackReadiness` — фиксированные 5.4. Правило перевода разведчика берёт задачу, только если
`ценность − ценность текущей роли > 0`.

**Исправление (Strategy/Objectives, `ReconObjectiveEvaluator`).** Если правило входа живой атаки
не выполняется именно из-за того, что даёт этот шаг (`HasAssaultDisruption` или
`HasAssaultObservation`), к члену `AttackReadiness` добавляется ценность атаки
(`MissionIntent.DisplacementValue`). Правило одно (`UnblockedAttackValue`), читают его
`DisruptAt` и Refresh для цели атаки.

## С2. Перспективой подкрепления считалось любое улучшение

**Было.** Атака Cassia на (1,1) с T25 по T29+ сидела в Reinforcement/SupportReturn на обменах по
одному бойцу (53 → 56 при цели 110). Каждая передача считалась прогрессом. Спрос на силу из руки 7 раз
получал `SATISFIED existing_free_army_available_as_support`.

**Исправление (Missions/GroundCombat, `GroundCombatAssemblyPlanner.ExistingSupportsReachGate`).**
Перспектива есть, только если существующие подкрепления вместе (потолок: сильнейшие тела хоста и
кандидатов в пределах наибольшей возможной команды) проходят порог победы или дают выгодный обмен
(для атаки). Правило читают:
- `AttackContinuity` — держать операцию или отходить;
- `GroundCombatDemandPolicy` — устраивает ли существующее подкрепление или нужна новая армия;
- `AggressionMissionPlanner.AppendAttackUnpinnedReinforcement` — привязывать ли подкрепление.

Штурм, который уже идёт, сохраняет свой порог отхода (`abort floor`).

## С3. Подготовка по старой разведке

**Решение владельца:** не менять. Последний шаг на цель и так ждёт свежей разведки и Disruption
(`AttackObjectiveEvaluator.AssaultEntryReady`).

## С4. «Жители» вражеской базы выпадали из оценки

**Было.** Правило `KnownOpposition.AddRememberedSiteResidents` читало `snap.Threat.Contacts`. Слой
`Threat` строится в скане последним, а в «живых» неполных снапшотах его нет совсем. Цитадель Cassia в
одном ходу оценивалась то как 6 / 28.7, то как 15 / 78.

**Исправление (Analysis + Foundation).** `KnownSnapshot.RememberedArmies` — история разведки,
читается в `BuildKnown`. Она — единственный источник и для LastKnown-контактов `BuildThreat`, и для
«жителей» в `KnownOpposition`.

Попутно убрано двойное начисление армии в `AttackTacticalOpportunity`: армия из `OnHex` и та же
армия из LastKnown через `OfEnemyArmy`.

## Проверка по следующему матчу (Korrin vs Ysolde, лог 2026-11-16 14:27)

**Что сработало.**
- С1: `DisruptStructure (-2,0)` в моменты блокировки стоит 25.8–29.6 (было бы 13.1). Носитель назначается,
  на T20 Korrin берёт базу с Disruption.
- Сильные бойцы выходят из гарнизона: Ysolde собрала атаку с Blister Juggernaut и Hank Mercer.
- Нарушений инвариантов и исключений нет.

**С5 (новое).** Правило Recon «кому нужен носитель» (`AssaultNeedsDisruption`) без живой операции
проверяло только силу (80% пика), а атака допускается и по возможности (win против известных
защитников). Армия #18 у Korrin (52.8 < 74.9, win 0.92) с T27 по T33 каждый ход ждала носителя
(`awaiting_disruption_carrier_before_approach`). Задача на носителя исчезла уже на T28. Операция так и не
создалась, поэтому таймаута тоже не было.

**Исправление (Strategy/Objectives, `ReconObjectiveEvaluator.FistMayAssault`).** Готовность кулака
считается по правилу атаки: `ForceReady(power, peak, win, opposition)` против цели без защиты здания
(`AttackSupportProjection.WithoutStructure`).

**Замечено, не исправлено.** У хоста подготовки Ysolde (#23) бойцы ходят по кругу (T29–T33): шаг Assemble
заполняет слоты любыми бойцами, которые поднимают силу, а Housekeeping (правило 0.8) освобождает эти слоты
под карты ростера из руки. Подготовка при этом медленно продвигается, потому что ждёт дорогие Colossus.
