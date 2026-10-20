# Hex explosion skills

| Ability tag | Maximum distinct targets | Attack tokens per challenge |
| --- | ---: | ---: |
| `Explosion3` | 3 | 10 |
| `Explosion5` | 5 | 10 |
| `Explosion10` | 10 | 10 |

These replace an ordinary attack. They do not add another primary shot. If several
explosion tags exist, the largest limit wins. Damage is the existing challenge's
success difference, with ordinary damage modifiers and armor, rather than guaranteed
10 HP loss. No balance/stat changes or new cards are assigned to existing catalogs.

## Configure in Unity

- Bomb equipment: add the desired explosion tag in **Equipment / Add Abilities**,
  and `SingleUse` in **Granted Abilities** (or Equipment / Add Abilities). Author
  its recipient restrictions and AP/resource costs through the existing fields.
- Disposable aircraft/missile: add `Explosion3`, `Explosion5` or `Explosion10`
  and `SingleUse` to the Unit card's **Granted Abilities**. Set its ordinary
  aviation, range, movement and launch costs through existing card fields.
- An explosion skill alone does not make either source disposable.

| Configuration | Completion after the whole series |
| --- | --- |
| Equipment: Explosion + SingleUse | Consume that equipment through the existing EquipmentSystem; retain the surviving host and Mutator. |
| Unit: Explosion + innate SingleUse | Remove the unit, including a series of misses. |
| Innate SingleUse unit carrying a SingleUse bomb | One series with the largest Explosion limit; consume the bomb and remove the unit. No second series. |
| Explosion without SingleUse | Reusable attack; no automatic consumption. |

SingleUse provenance matters: a marker on equipment never makes its host disposable,
and a marker supplied by a Mutator does not make either the host or ordinary equipment
disposable. Innate Explosion abilities remain after a bomb's equipment-only abilities
are removed. EquipmentSystem's existing slot recomposition remains the only equipment
consumption implementation.

The tags are in the existing ability dropdown, ability catalog descriptions and
GameConfig abbreviations. No scene/prefab wiring or image assets are required.

## Resolution

One uniformly random subset is selected at launch. Every living unit from every army
physically on the target hex is eligible, including allies, heroes, hidden units,
aircraft containers and event guards already in play. An innate SingleUse launcher is
excluded entirely: it gets no self-challenge and does not occupy a target slot. Other
SingleUse units remain eligible. A reusable host carrying a disposable bomb can still
be hit by its own explosion when present on the hex.
The building and each installed facility with positive structure points are separate
targets. Merely remembered/unspawned event guards are not physical occupants.
Ground encounters include both participating armies even while the attacker is still
indexed at its pre-contact map hex. Passing aircraft use their actual step hex.

Each selected target gets its own full existing tokens/Fate/tactic/result challenge,
in sequence. The title shows `EXPLOSION (i/N)`. A short pool produces fewer challenges;
the same occupant is never picked twice. Heroes defend with FateMax, ordinary units
with Defense and their existing ground-battle bonuses, structures with their own
Defense and Fate. Structure damage writes to structure points, never to a dummy unit
registry entry. The offensive profile is frozen at launch with Attack = 10.

Explosion challenges do not intercept through Bodyguard, create recursive Splash or
Scorcher hits, or trigger counterattacks. Ordinary per-hit damage modifiers, armor,
Berserk and Shock still apply. No offensive hero Fate is spent against an ally.
AI does not spend attacking tactics to increase damage to its own units.

The equipped bomb remains until the full series ends, including misses. Then only its
Equipment slot is cleared; a surviving host keeps its Mutator and ordinary state.
An innate `SingleUse` Unit is instead removed from its army after the full series.
The attack continues if the launcher was itself killed earlier in the series.
For a missile card, configure the ordinary Unit **Is Aviation** flag and add the desired
Explosion tag plus SingleUse to **Granted Abilities**. It uses existing airfield deployment
and takeoff rules. At the destination hex it performs the aviation strike, then disappears
after the final challenge. Passing through intermediate hexes does not detonate it. With
no eligible strike or no resolved challenge it is not expended simply by entering a hex.

Unit/turn-order removal uses the normal paths. A killed carrier's cargo cleanup is
deferred so selected aircraft cannot escape a launched blast by returning to hand.
Structure destruction and the existing starting-citadel defeat event are published
after the series, preventing a premature battle/match ending. Empty armies, mobile
airfields, transport links, observations and map stacking use existing cleanup paths.

## AI and accounting

Ground WorthIt and tactical lookahead model distinct victims on both sides, the fixed
10-die attack and one completion/consumption. Tactical projections own private copies.
Air forecasts include the attacking wing and supplied known collateral, keep wounds
across passes and stop reusing spent missiles. ExpectedFriendlyDamage is subtracted
from ExpectedDamage when ranking/admitting air support.

The common equipment recipient/development scorer uses full roster forecasts for area
weapons, including the armed/spent transition, rather than multiplying an ordinary
permanent weapon bonus by a target count. Live AI rejects a non-positive expected net
blast. Capture-raid air support does not fire area weapons because random damage cannot
guarantee the raid's minimum survivor. The chosen hex's other occupants are not shielded
by an exact-army support policy.
An AI also refuses a blast that could destroy its own known starting citadel; this
would eliminate the player, rather than merely trade away another damaged structure.
The air forecast uses the same positive-net launch gate as live AI, including a single
charge for sacrificing an innate missile. Per-target expected damage valuation is shared
with the ground forecast. Observed structures retain their own Defense and current Fate;
collateral Fate is spent across successive forecast passes rather than replenished.

The inexpensive power/coverage filters recognize the fixed 10-die attack. They are not
full area-damage prices; target count, casualties and consumption belong to the detailed
combat forecasts and the common equipment before/after scorer.

Forecasts use own snapshots and observed enemy profiles. Foreign facility-slot contents
remain private; hidden live rosters never enter an AI decision. This makes collateral
estimates necessarily incomplete where the AI lacks knowledge. Ground strategic models
retain their existing roster/geometry approximation; live execution resolves the actual
whole hex. Disposable-unit identity and collateral profiles participate in observation
comparisons, forecast/cache keys and development/retreat fingerprints.

No challenge in the series charges AP/resources, reserves money or refunds a destroyed
item/unit. Existing deployment, attachment, launch and admission/bank rules still own
those costs. Repeated production remains legitimate resupply.

## Verification

`ExplosionAttackTests` covers limits/distinct selection, allies, short pools, complete
series before consumption, misses, launcher self-destruction, armed modifiers, invalid
repeat hits, ground removals/reaction exclusion, friendly-fire valuation, ground/air
forecasts, equipment scoring and cache identity. It also covers both equipment marker
selectors, the simultaneous innate/equipment SingleUse case, Mutator provenance in live
and hand-card scoring, reusable explosions, unprofitable missile refusal and frozen
structure Fate. Engine-bound structure/UI integration
and complete EditMode tests must additionally run in Unity 6000.5.4f1.

In Unity, try a bomb-equipped aircraft and an innate disposable missile against a hex
with two armies, allies, a base and facilities. Repeat in Fast Resolve, hit a carrier,
destroy a starting citadel, and verify the last challenge is still shown before cleanup.
