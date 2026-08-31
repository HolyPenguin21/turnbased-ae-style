# AI Strategy V2 — Capability Quality, Contextual Scout Decisions & Terminal AP Spending

## Description

Расширить AI Strategy V2 так, чтобы StrategicManager оценивал не только факт закрытия `AxisDemand`, стоимость chain и наличие требуемого trait, но также **качество получаемого capability в контексте работы, ради которой demand был создан**.

Основной user story:

- карта почти полностью закрыта;
- Recon создаёт demand на `ScoutCapability`;
- в руке доступны несколько разных Scout-карт;
- ресурсов достаточно только на одну;
- AI должен выбрать ту карту, которая даёт наибольшую ожидаемую Recon-ценность за доступные ресурсы;
- после deployment создаётся/используется армия;
- AP на её реальное использование остаётся зарезервированным;
- Scout выполняет Explore;
- необязательный Stealth используется только тогда, когда его ожидаемая защитная ценность оправдывает дополнительный AP;
- после выполнения всех реальных работ текущего хода оставшийся AP используется на draw, пока это легально и полезная более приоритетная работа отсутствует.

Решение должно быть **общим механизмом**, а не специальным правилом под конкретные карты или первый ход.

## Architectural goals

Сохранить текущую архитектуру:

WorldAnalysis  
→ Objectives  
→ Strategy / Radar  
→ DemandLayer  
→ AxisBudgetLedger  
→ StrategicManager Phase A  
→ RefreshOperationalState  
→ MissionLayer  
→ ResourceAllocator  
→ ProvisioningManager  
→ Task Execution  
→ MissionContinuity  
→ StrategicManager Phase B  
→ Housekeeping

Не добавлять новый глобальный planner.

Разделение ответственности должно остаться:

- `DemandLayer` — определяет **WHAT capability is missing**;
- `StrategicManager / MaterializationCandidateBuilder` — определяет **HOW best to materialize it**;
- `MissionLayer` — определяет конкретную работу;
- `ProvisioningManager` — назначает конкретного существующего actor;
- `TaskExecutor` — исполняет;
- Phase B — тратит только действительно оставшийся после основной работы capacity.

## Scope 1 — Capability Quality Model

### Problem

Сейчас `ScorePlanA()` в основном учитывает:

- TargetFit;
- AP cost;
- resource cost;
- preferred trait;
- placement;
- generation probability;
- chain complexity;
- scarcity penalty.

Этого недостаточно для выбора между несколькими картами одного capability.

Пример:

Scout A:
- Move 3
- Recce radius 1
- Stealth
- Hero
- AP 1
- H 1

Scout B:
- Move 2
- Recce radius 1
- no Stealth
- Unit
- AP 1
- H 1

Обе карты закрывают `ScoutCapability`.

Текущий scoring может выбрать A в основном из-за `PreferredTraits.Stealth`, но не понимает полноценно, что Move 3 способен дать больше информации за ту же activation cost.

Необходимо оценивать **expected capability utility**, а не только binary capability match.

## Scope 2 — Introduce capability-specific quality evaluation

Добавить отдельный pure evaluation seam, например:

`CapabilityQualityEvaluator`

или аналогичную структуру, соответствующую существующему стилю проекта.

Не смешивать все capability в одном огромном `ScorePlanA()`.

На первом этапе полноценно реализовать:

`ScoutCapabilityQuality`

Архитектура должна позволять позже добавить:

- FieldCombatPower quality;
- Hero quality;
- GarrisonCombatPower quality;
- AirRecon;
- specialised anti-armour / ranged / melee profiles.

## Scope 3 — Scout materialization quality

Для каждого feasible materialization plan, заканчивающегося `ScoutCapability`, получить projected Scout characteristics из конечной карты после generation/equipment projection.

Использовать реальные gameplay данные.

### Mobility

Использовать `CardDefinition.moveMax`.

Mobility должна оцениваться не как `Move 3 всегда на X лучше Move 2`, а как **marginal mission value**.

Примеры:

- если оба Scout достигают целевого frontier за этот ход — разница mobility мала;
- если Move 3 достигает objective за 1 turn, а Move 2 требует 2 turns — разница большая;
- на сильно закрытой карте дополнительный movement повышает ожидаемую ценность Explore follow-through;
- на почти полностью исследованной карте этот бонус должен уменьшаться.

Использовать тот же принцип ETA, что уже используется `ScoutCostModel`:

- hex distance;
- move budget;
- без отдельного альтернативного path estimator.

Не создавать второй несовместимый estimator.

### Vision

Получать Recce radius через `AbilityParams.GetBestRecceRadius(...)`.

Больший vision radius должен повышать Scout quality только в той мере, в которой он реально может дать дополнительную информацию.

Предпочтительно использовать `MapKnowledge`:

- visited set;
- total/all hexes;
- dark/unknown neighborhood;
- frontier context.

Не давать постоянный статический bonus `+X за каждый radius`.

### Spot strength

Получать через `AbilityParams.GetBestRecceSpotStrength(...)`.

Spot strength не должен давать большой generic Explore bonus.

Он должен иметь значение прежде всего когда:

- есть известная вероятность скрытых целей;
- Surveil objective связан с detection;
- strategic context говорит о необходимости поиска stealth units;
- target/detection context делает spotting релевантным.

Таким образом `r1s6` не должен автоматически побеждать `r2s0` на обычном раннем Explore только потому, что 6 > 0.

### Stealth

Сохранить существующее разделение:

- Required;
- Preferred;
- None.

`Required` остаётся hard feasibility gate.

`Preferred` становится частью quality model.

Stealth utility должна зависеть от контекста:

- detection risk;
- known enemy proximity;
- known enemy ability to detect;
- mission type;
- стратегической ценности самого Scout.

На безопасной ранней разведке Stealth-capable Scout может получить небольшой option-value bonus, но не должен автоматически выигрывать у значительно более дешёвого или более полезного Scout.

### Activation efficiency

Учитывать `activationApCost`.

Основная идея:

Scout с Move 3 за activation 1 AP может дать значительно больше exploration value/AP, чем Scout с Move 2 за тот же activation AP.

Но Scout Move 3 / activation 2 AP может проиграть Scout Move 2 / activation 1 AP, если дополнительная mobility не нужна для текущей работы.

Quality должна оцениваться вместе с существующей whole-chain стоимостью, а не заменять cost model.

## Scope 4 — Hero opportunity cost

Hero не должен автоматически получать положительный Scout bonus только потому, что это Hero.

В текущем gameplay Hero обладает реальной дополнительной стратегической ценностью:

- combat stats;
- Fate;
- CommandRating;
- возможность быть ядром полноценной армии.

Следовательно использование Hero как solo Recce имеет **opportunity cost**.

Использовать существующий `CapabilityInventory` / стратегический shortage context, а не создавать второй независимый scarcity model.

Примеры:

### Case A — Hero свободен

- нет Raid demand;
- нет shortage Hero;
- Hero Scout существенно быстрее обычного Scout;
- карта очень тёмная.

Hero может быть лучшим вариантом.

### Case B — Hero нужен для армии

- есть Aggression objective;
- `AvailableHeroes == 0`;
- разыгрывание Hero как solo Scout лишает AI возможности закрыть Raid/Hero requirement;
- обычный Scout достаточно хорош.

Обычный Scout должен получить преимущество.

### Case C — несколько Hero

Если Hero capability не scarce, opportunity cost должен падать.

### Important gameplay boundary

Не добавлять Hero bonus за «escape», «revive», «возвращение в руку» или другую recoverability, если такой gameplay mechanic реально не существует.

`Fate` сейчас является battle reroll resource и должен трактоваться именно так.

Если позже появится отдельная Hero recovery/escape mechanic, Capability Quality должен иметь возможность прочитать её через gameplay data, но не hardcode её заранее.

## Scope 5 — Generalized scarcity opportunity cost

Расширить существующую идею `ScarcityOpportunityCost`.

Сейчас она в основном защищает scarce Stealth.

Сделать её более общей, но не превращать в глобальный planning system.

Candidate должен получать penalty, когда закрытие одного demand потребляет capability, которое является существенно более ценным для другого уже известного shortage.

Минимальные случаи:

- unique Stealth source;
- scarce Hero;
- scarce high combat-power Hero;
- scarce equipment granting required trait;
- limited generator use.

Сохранить правило:

**Hard required current demand > speculative future preference.**

Scarcity — ranking penalty между feasible alternatives, а не произвольный hard reject, кроме уже существующих explicit reservations/claims.

## Scope 6 — Preserve partial demand fulfilment

Не ломать текущую incremental semantics.

Demand `ScoutCapability x2` может быть закрыт:

- одним Scout сейчас;
- residual `x1` остаётся;
- residual передаётся в Phase B.

Capability Quality применяется к каждому следующему materialization decision заново, после operational refresh и rebuild `CapabilityInventory`.

После первого Scout scarcity/context может измениться, поэтому второй candidate должен пересчитываться, а не использовать старый ranking.

## Scope 7 — Context propagation from Recon Demand

Текущий `AxisDemand` содержит capability/traits/target, но для более качественного выбора Scout может понадобиться небольшой объём mission-context данных.

Разрешается расширить `AxisDemand` typed context, но нельзя превращать DemandLayer в card selector.

Предпочтительное направление: добавить optional capability-specific context, например `ScoutCapabilityContext`, который может содержать факты:

- expected target kind;
- detection risk;
- exploration vs surveillance context;
- information-gain context;
- relevant target/focus hex.

Не передавать из DemandLayer:

- конкретную `CardData`;
- конкретное имя карты;
- готовый score карты;
- указание `play Hero X`.

Demand должен продолжать описывать **потребность**, а StrategicManager — выбирать реализацию.

## Scope 8 — Align materialization choice with later mover economics

После deployment тот же Scout будет оцениваться через:

- `ScoutMoverSelector`;
- `ScoutCostModel`.

Необходимо избежать ситуации:

Phase A: Scout A лучший.

После spawn Provisioning: Scout B был бы лучшим actor по совершенно другой формуле.

Общие параметры:

- activation AP;
- mobility;
- ETA;
- Stealth eligibility;

должны иметь совместимые semantics.

Не обязательно использовать буквально одну numeric formula, поскольку Phase A оценивает будущую карту, а Provisioning — уже существующего actor.

Но ordering не должен систематически противоречить друг другу.

## Scope 9 — Optional Stealth AP decision

### Current behaviour

Required Stealth уже имеет explicit AP reservation и hard execution requirement.

Это оставить.

Добавить отдельную политику для **optional** Stealth.

Примерный responsibility:

`ScoutOptionalStealthPolicy`

или эквивалентный pure evaluator.

Он должен принимать decision непосредственно перед первым потенциально рискованным movement leg.

### Optional Stealth inputs

Использовать только честно известные AI данные:

- known enemy positions;
- known threat;
- target detection risk;
- Scout strategic value;
- current Scout state;
- already hidden;
- AP remaining;
- AP cost of entering stealth;
- наличие ещё не выполненной профинансированной работы.

Не использовать TrueWorld для скрытых противников.

### Optional Stealth result

Решение:

- `Enter`;
- `Skip`;

с diagnostic breakdown.

Примеры:

`risk=0.08 protection=0.15 apOpportunity=0.45 -> SKIP`

`risk=0.72 protection=0.65 apOpportunity=0.20 -> ENTER`

## Scope 10 — AP opportunity cost for optional actions

Необходимо учитывать, что 1 AP может изменить доступность следующего действия.

Пример:

- AP = 3;
- Draw cost = 2;
- Stealth cost = 1.

После Stealth остаётся 2 AP → draw всё ещё возможен.

Opportunity cost Stealth относительно draw невелик.

Другой пример:

- AP = 2;
- Draw cost = 2.

Stealth за 1 AP уничтожит возможность draw.

При умеренном risk это должно уменьшать желание включать Stealth.

Не строить полноценный turn-wide minimax planner.

Достаточно bounded marginal comparison:

> ухудшает ли optional spend количество уже известных legal higher/later-value actions?

## Scope 11 — Terminal Phase-B draw

### Problem

`CardDrawExecutor` уже умеет independently выполнить legal draw.

Но сейчас Phase B вызывает draw в основном как hand cycling после успешного surplus deployment.

Добавить независимую terminal draw policy.

После того как Phase B не нашёл более полезной actionable materialization chain, если:

- residual strategic demand не имеет feasible action;
- нет другого admitted Phase-B materialization;
- mission execution уже закончено;
- late-turn explicit AP reservation отсутствует;
- housekeeping по текущему invariant не требует AP;
- hand имеет slot;
- deck не пуст;
- draw affordable;

AI должен иметь право самостоятельно выполнить draw.

## Scope 12 — Multiple terminal draws

Terminal draw должен повторяться bounded loop.

Пример:

AP 4  
Draw AP cost 2  
2 free hand slots  
deck >= 2

Ожидаемо:

Draw → AP 2  
Draw → AP 0

Остановиться при первом из условий:

- недостаточно AP;
- hand full;
- deck empty;
- достигнут safety action cap;
- появился более приоритетный actionable Phase-B action.

## Scope 13 — Phase-B action priority

Установить явный порядок:

1. executable residual strategic demand;
2. high-value proactive materialization;
3. terminal draw;
4. leave AP unused only when nothing legal remains.

Draw не должен опережать feasible residual demand.

## Scope 14 — Do not reserve AP for imaginary future work

Не возвращать старые magic fixed surplus reserves.

Phase B идёт после ordinary mission execution.

AP не переносится между ходами.

Следовательно terminal draw может расходовать весь действительно свободный AP, если никакой explicit late-turn subsystem reservation его не держит и действие legal.

Если позже появится AP-costing housekeeping или другой late stage, этот subsystem должен создать explicit reservation contract.

Не hardcode `always leave 2 AP`.

## Scope 15 — Reconcile obsolete surplus configuration

Проверить существующие:

- `surplusApReserve`;
- resource reserve constants;
- comments рядом с Phase B.

Текущая implementation уже движется в сторону real remaining capacity вместо speculative floors.

После реализации не должно оставаться config/comment semantics, которые противоречат фактическому поведению.

Удалить, deprecated-mark или переопределить unused tunables так, чтобы код и документация описывали одно и то же.

## Scope 16 — Candidate quality breakdown

Добавить диагностическую структуру, например `MaterializationQualityBreakdown`.

Минимально для Scout:

- base capability fit;
- target fit;
- mobility;
- ETA;
- vision/info utility;
- spot/detection utility;
- stealth utility;
- hero opportunity cost;
- scarcity opportunity cost;
- AP cost;
- resource cost;
- chain penalty;
- generation probability;
- final score.

Не обязательно хранить breakdown долго. Он может существовать только при evaluation/logging.

## Scope 17 — Logging

Добавить читаемый debug output для выбора между близкими кандидатами.

Не логировать сотни вариантов placement.

Логировать:

- selected candidate;
- runner-up, когда разница мала или существует несколько materially different candidates;
- compact breakdown.

Пример:

`strat.A quality Scout — Nora score 1.31 [move .28 vision .14 stealth .12 heroOpp -.05 cost -.18] > Ash Drifter 1.07 [move .12 vision .14 stealth 0 heroOpp 0 cost -.18]`

Optional stealth:

`scout stealth — SKIP risk .05 protect .10 opportunity .38 ap=4 drawSlots=2`

Terminal draw:

`strat.B terminal — no actionable residual/surplus, convert stranded AP to draw; ap 4->2 hand 6->7`

`strat.B terminal — draw; ap 2->0 hand 7->8`

## Cases the implementation must support

### Case 1 — Original user story

State:

- early game;
- map strongly unexplored;
- no meaningful known enemy threat;
- AP = 8;
- H = 1;
- two Scout cards affordable individually, but only one can be paid because H=1;
- Candidate A: Move 3 + Recce + Stealth, Hero;
- Candidate B: Move 2 + same Recce radius, no Stealth, Unit;
- no competing Hero shortage.

Expected:

- both candidates feasible;
- A receives higher Recon quality because mobility has real dark-map marginal value;
- Stealth contributes only modest option value because current risk is low;
- Hero has opportunity-cost penalty, but not enough to erase the material Recon advantage in this scenario;
- A selected;
- deploy chain preserves follow-up activation AP;
- Recon mission executes;
- optional Stealth is NOT activated while risk remains low;
- if 4 AP remain, draw cost=2, two hand slots exist and there is no better late work: draw twice and end at 0 AP.

### Case 2 — Hero needed elsewhere

Same Scout cards, but:

- viable/high-value Raid objective exists;
- Hero supply is scarce;
- ordinary Scout is sufficient for current Explore.

Expected:

Hero opportunity cost rises.

AI may correctly choose the weaker Unit Scout even though Hero has Move 3.

### Case 3 — Mobility does not matter

Target is one hex away.

Move 2 and Move 3 both reach target and reveal essentially the same relevant information this cycle.

Expected: Move 3 gets little/no extra marginal bonus. Cheaper / less strategically scarce candidate may win.

### Case 4 — Mobility changes ETA

Target requires:

- Move 3 Scout → ETA 1;
- Move 2 Scout → ETA 2.

Expected: large mobility advantage for Move 3 candidate.

### Case 5 — Vision vs movement

Scout A:
- Move 3;
- radius 1.

Scout B:
- Move 2;
- radius 2.

Dark dense frontier around target.

Expected: decision comes from estimated information utility, not hardcoded stat ordering. Depending on map geometry, B may correctly win.

### Case 6 — Spotting context

Scout A: `r2s0`

Scout B: `r1s6`

Normal Explore with no stealth threat: A should usually be preferred if its larger vision produces more information.

Surveil / stealth-detection context: B can become more valuable.

### Case 7 — Stealth Required

Mission context makes Stealth hard-required.

Non-stealth Scout is not feasible.

No amount of cheaper cost/mobility may bypass Required trait.

### Case 8 — Optional Stealth

Scout can enter stealth.

No enemy/detection pressure → SKIP.

Known dangerous contact / high detection-risk leg → ENTER if protection utility exceeds AP opportunity cost.

### Case 9 — Cheap sufficient Scout

Scout A:
- expensive;
- Move 4;
- radius 2.

Scout B:
- cheap;
- Move 2;
- radius 1.

Objective is trivial/nearby.

Expected: AI should not overpay for unused quality. B may win.

### Case 10 — Generated Scout

No suitable direct Scout in hand.

Generator can create a good Scout.

Expected: GenerateDeploy competes through the same quality model, with generation probability, resource cost and generation penalty still applied.

A generated superior Scout must not automatically beat a cheap sufficient direct chain.

### Case 11 — Equipment creates better capability

Base Scout + equipment produces Stealth or improved relevant capability.

Compare Direct cheap Scout vs AttachDeploy better Scout.

Equipment chain wins only if added expected utility justifies extra AP/resource/scarcity cost.

### Case 12 — Residual demand vs draw

After missions:

- AP 4;
- hand slot exists;
- draw cost 2;
- residual Scout demand feasible for 3–4 AP.

Expected: residual materialization evaluated first. Draw cannot steal its AP.

### Case 13 — No residual action

After missions:

- AP 4;
- no feasible demand;
- no worthwhile surplus;
- two free slots;
- deck available.

Expected: two terminal draws.

### Case 14 — Partial draw capacity

AP 3, draw cost 2.

Expected: one draw. Remaining AP 1 may remain unused unless another legal action exists.

### Case 15 — Full hand

AP 6, deck available, hand full, no legal card play.

Expected: no draw. AI must not overflow hand or discard implicitly.

## Affected areas

Expected primary files:

- `Assets/Scripts/Ai/V2/AxisDemand.cs`
- `Assets/Scripts/Ai/V2/DemandLayer.cs`
- `Assets/Scripts/Ai/V2/MaterializationCandidateBuilder.cs`
- `Assets/Scripts/Ai/V2/MaterializationPlan.cs`
- `Assets/Scripts/Ai/V2/CapabilityInventory.cs`
- `Assets/Scripts/Ai/V2/StrategicManager.cs`
- `Assets/Scripts/Ai/V2/CardDrawExecutor.cs`
- `Assets/Scripts/Ai/V2/ScoutCostModel.cs`
- `Assets/Scripts/Ai/V2/ScoutMoverSelector.cs`
- `Assets/Scripts/Ai/V2/TaskExecutor.cs`
- `Assets/Scripts/Ai/V2/AiConfigV2.cs`

Recommended new small focused types if needed:

- `CapabilityQualityEvaluator.cs`
- `ScoutCapabilityQuality.cs`
- `ScoutOptionalStealthPolicy.cs`

Exact split is implementation choice, but `MaterializationCandidateBuilder.cs` should not become a monolithic file containing every future capability-specific scoring rule.

## Non-goals

Do NOT:

- introduce Strategy V3;
- add a second global planner;
- make DemandLayer select named cards;
- simulate the complete future game;
- perform deep multi-turn search;
- use TrueWorld to decide Scout safety;
- hardcode specific card names;
- hardcode `Hero > Unit`;
- hardcode `Move 3 > Move 2`;
- hardcode `always Stealth`;
- hardcode `always draw at 4 AP`;
- revive fixed AP reserve without a real owning subsystem;
- change normal human gameplay rules;
- make AI-only versions of draw/deploy/stealth actions.

## Invariants

1. Demand describes capability shortage, not card choice.
2. RequiredTraits remain hard constraints.
3. PreferredTraits remain preferences.
4. Capability Quality ranks only feasible materialization chains.
5. Whole-chain AP/resource affordability remains authoritative.
6. Follow-up AP reservation remains authoritative.
7. Generation/equipment claims remain authoritative.
8. Partial demand fulfilment remains supported.
9. Phase B cannot retroactively create and execute an ordinary mission for the already-finished mission phase.
10. Terminal draw only uses genuinely leftover capacity.
11. Housekeeping remains zero-AP while its current invariant says so.
12. Every gameplay mutation still uses canonical executors.
13. Honest/fog-safe information boundaries remain unchanged.

## Acceptance Criteria

### AC1 — Context-sensitive Scout choice
Given multiple feasible Scout materialization chains, AI ranks them using actual projected mission capability, not only cost and trait presence.

### AC2 — Mobility quality
`moveMax` affects candidate quality through expected mission/ETA value. No unconditional raw-stat bonus.

### AC3 — Vision quality
Recce radius affects quality according to useful information it can reveal.

### AC4 — Spot quality
Recce spot strength affects quality only in relevant detection/surveillance contexts.

### AC5 — Stealth semantics
Required Stealth remains a hard gate. Preferred Stealth is contextual utility.

### AC6 — Hero opportunity cost
Hero Scout usage accounts for the strategic scarcity/value of Hero capability. Hero status alone is neither unconditional bonus nor rejection.

### AC7 — No invented recoverability
No AI bonus is granted for nonexistent Hero escape/revive/return mechanics.

### AC8 — Cost remains relevant
A higher-quality candidate can lose to a cheaper sufficient candidate when its extra quality has low marginal value.

### AC9 — Generated/equipped parity
Direct, AttachDeploy, GenerateDeploy and GenerateAttachDeploy candidates use the same final capability-quality semantics.

### AC10 — Re-evaluation after materialization
After every successful Phase-A chain and operational refresh, quality/scarcity is recalculated before selecting the next chain.

### AC11 — Provisioning consistency
Materialized Scout quality logic does not materially contradict the later Scout mover/cost semantics for AP, ETA, movement and Stealth.

### AC12 — Optional Stealth
Safe Explore does not spend Stealth AP merely because the ability exists. Risky mission may spend the AP when justified.

### AC13 — AP opportunity cost
Optional 1-AP spends account for whether they destroy another known legal later action such as draw.

### AC14 — Independent terminal draw
Phase B can draw even when no surplus card was played immediately before it.

### AC15 — Repeated terminal draw
With AP=4, DrawCost=2, >=2 free slots and deck>=2, AI performs two draws when no higher-value work remains.

### AC16 — Residual priority
A feasible unresolved strategic demand always receives consideration before terminal draw.

### AC17 — Hand/deck safety
Terminal draw never exceeds hand capacity, draws from empty deck, or spends unaffordable AP.

### AC18 — No speculative reserve
Phase B does not leave AP unused because of a fixed reserve that has no real late-turn owner.

### AC19 — Bounded execution
All Phase-A, Phase-B and draw loops remain explicitly bounded.

### AC20 — Diagnostics
Logs explain why one candidate beat another, why optional Stealth was entered/skipped, and why terminal draw started/stopped.

### AC21 — Determinism
Equal-score candidates preserve stable deterministic tie-breaking.

### AC22 — Existing Step 8B behaviour preserved
Generated Cards + Equipment AI, residual demand handling, discrete AP spillover and generator-use reservations continue to work.

## Validation scenarios

At minimum add deterministic tests around pure evaluation/policy methods for:

1. dark-map Move3 > Move2;
2. short target Move3 ≈ Move2;
3. vision radius marginal value;
4. spot strength irrelevant to generic Explore;
5. spot strength valuable in detection context;
6. Required Stealth rejects non-stealth;
7. Preferred Stealth does not become hard gate;
8. scarce Hero opportunity-cost reversal;
9. abundant Hero does not receive excessive penalty;
10. cheap sufficient direct vs expensive generated chain;
11. residual demand beats draw;
12. two terminal draws from 4 AP;
13. one draw from 3 AP;
14. no draw on full hand;
15. no draw on empty deck;
16. optional Stealth skipped in safe state;
17. optional Stealth selected in high-risk state;
18. optional Stealth penalty when it would eliminate an otherwise legal high-value draw.

Also run full existing V2 tests/regressions.

## Expected user-story trace

A successful baseline trace should conceptually look like:

`Demand — Recon needs ScoutCapability`

`strat.A quality — HeroScout > UnitScout because useful mobility/info + optional stealth value outweigh hero opportunity cost`

`strat.A — deploy HeroScout`

`followup 1ap reserved`

`mission — Scout Explore`

`provision — mover=<new scout>`

`scout stealth — SKIP reason=low risk`

`exec — Explore ...`

`strat.B — no executable residual / no worthwhile surplus`

`strat.B terminal — draw AP 4->2`

`strat.B terminal — draw AP 2->0`

`housekeeping — AP invariant ok`

## Definition of Done

Implementation is complete when the AI no longer answers only:

> “Which feasible card is cheapest / has the preferred trait?”

but can answer:

> “Which feasible materialization gives the highest useful capability for the actual strategic shortage, after accounting for mission value, cost and opportunity cost?”

and, after execution:

> “Is another optional AP spend actually worth more than preserving that AP for known remaining actions?”

and finally:

> “If no real work remains and AP cannot be carried forward, can I legally convert the stranded AP into future option value by drawing cards?”

without introducing a new global planner or breaking the existing Strategy V2 ownership boundaries.
