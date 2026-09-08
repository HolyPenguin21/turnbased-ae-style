# Housekeeping Force Packaging Design

**Date:** 2026-09-08  
**Scope:** AI Strategy V2 end-of-turn local force reorganisation and the canonical combat composition estimate it consumes.

## Goal

At the end of an AI turn, cards at one hex are packaged into armies that present a credible first defender under the real contact rules, without changing missions, selecting targets, spending AP/resources, or inventing a second combat-power model.

## Confirmed ownership

- `Game Systems / Combat / ChallengeResult` owns combat damage modifiers.
- `Game Systems / Combat / WorthIt` owns army-versus-army composition feasibility and win chance.
- `Game Systems / Combat / BattleInitiator` owns which one of several armies on a hex receives contact first.
- `Strategy V2 / Analysis / WorldSnapshot` owns honest and explicitly sanctioned cheat world facts.
- `Strategy V2 / Housekeeping / Analyzer` projects live local units and relevant enemy field compositions.
- `Strategy V2 / Housekeeping / Planner` chooses only same-hex, zero-cost roster packaging.
- `Game Systems / Map / ArmyActions` owns authoritative roster mutations, including atomic batch transfer.
- `Strategy V2 / Diagnostics` owns presentation of the resulting decision trail.

No new manager, vertical layer, persistent unit role, or duplicate combat formula is introduced.

## Canonical combat composition estimate

The legacy `Attack + Defense` contact ranking is removed. `WorthIt` receives complete per-unit combat abilities and applies the same modifier chain as live combat through `ChallengeResult`.

The profile must preserve current Attack, Defense, HP, Initiative, type tags, and effective abilities. Full-roster simulation must model:

- the individual damage threshold: a high-defense body cannot be defeated by pooling several individually insufficient attacks;
- CriticalDamage, Hyperkinetic, Pyrokinetic and CeramicArmor through the canonical damage modifier;
- ShockAttack by suppressing a not-yet-taken action after a damaging hit;
- Berserk by applying its battle-duration Attack gain and Defense loss after a damaging hit.

Non-combat abilities do not receive arbitrary combat value.

`BattleInitiator` ranks each contactable defender by the attacker's `WorthIt` battle estimate against the observer-visible roster. Lowest attacker win chance wins; ties prefer lower surviving attacker HP on a win, then higher attacker critical-after-win risk, then stable army id. Hidden defenders never influence pre-contact selection, but the selected army still reveals normally when the encounter commits.

## Sanctioned cheat benchmark

Housekeeping may read deployed enemy ground-field compositions from `WorldSnapshot.TrueWorld.EnemyArmies`, including hidden armies. It may not consume target identity for missions, objectives, movement, card spending, or map revelation.

Eligible benchmark armies are non-neutral enemy, non-garrison, non-prison, non-air, non-empty, combat-capable rosters already on the map. “Active” means deployed and combat-capable; `HasActivatedThisTurn` is deliberately ignored.

For each local force group and each enemy composition:

- estimate enemy ETA from the real enemy hex to the local group hex using its current movement budget;
- evaluate the enemy roster against the projected defender through `WorthIt`;
- combine matchup danger and ETA continuously; there is no viability threshold or pass/fail gate.

Visible and hidden armies use the same matchup calculation. Visibility is retained only for diagnostics. When no eligible enemy composition exists, Housekeeping falls back to canonical `AiPower` formation quality.

## Housekeeping ordering

Hard invariants remain ahead of combat packaging:

1. garrison floor and legality;
2. mission commitments and same-turn capability leases;
3. singleton/non-viable repair;
4. commander-capacity and protected support-operator constraints;
5. minimise the worst distance-weighted enemy success against the army that the real contact rule would expose first;
6. improve the next exposed formation from the remainder;
7. composition quality and operation count.

A legal merge is accepted for every measurable defensive improvement, even when the merged army remains weaker than the benchmark. There is no artificial `winChance >= X` gate.

## Support heroes

`HeroRoleEvaluator` remains the sole intrinsic CombatLeader/Flexible/SupportOperator classifier and adds mobility to combat suitability.

Facility compatibility remains solely in `ResearchProductionSystem`. During analysis, a Researcher/Assembler physically present on a compatible owned Facility hex is projected as an active development operator. Housekeeping keeps that unit in the local garrison and never donates it to a field formation. Task commitment remains stronger than Housekeeping. Only the operator count needed by the compatible facility modes is protected; surplus compatible heroes remain governed by their intrinsic role.

## Scout delivery lease

A successfully materialised Phase-B Scout capability leases the resulting army through the same turn's Housekeeping even when the surplus plan has no residual demand. The lease remains turn-local and is cleared after Housekeeping; no persistent role is written to `UnitData`.

## Atomic whole-fold

A whole-fold is one batch mutation owned by `ArmyActions`: validate the entire ordered set against projected capacity, ownership, same-hex, AP and source safety, then commit all transfers. Any failed precondition commits zero moves. Single transfers and swaps keep their existing APIs.

## Diagnostics

Normal logging keeps one FRAME snapshot and emits one Housekeeping before/decision/execution/after summary per touched hex. It includes selected defender, worst benchmark composition, visibility, ETA, enemy win chance, protected hero/scout reasons and unresolved defects. Per-unit transfer lines are emitted only by the existing verbose reorganisation option.

## Verification

The repository explicitly does not accept automated test files. Verification is:

1. `dotnet build Assembly-CSharp.csproj`;
2. static duplicate-owner search for `AttackSum + DefenseSum`, profile coverage copies and whole-fold loops;
3. Play Mode scenarios:
   - two medium armies merge when this lowers enemy success;
   - an individually unpierceable defender is selected over a larger superficial stat sum;
   - a hidden nearby field stack influences packaging but creates no mission or revealed map contact;
   - multiple bases produce different logged ETAs;
   - Phase-B Scout survives same-turn Housekeeping;
   - active facility operator remains in garrison;
   - injected whole-fold preflight failure leaves both rosters unchanged.
