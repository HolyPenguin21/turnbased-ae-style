# Single-use equipment

`SingleUse` is an equipment lifecycle marker. On an Equipment card with
`Attachment Slot = Equipment`, add `SingleUse` to `Granted Abilities`.
The Equipment payload's `Add Abilities` selector is also supported. Configure
the damage/stat/skill bonuses in the ordinary Equipment payload.

The marker is read from the installed item itself. An innate unit ability or
a Mutator carrying the same marker cannot destroy an ordinary item. Existing
cards are unchanged; this change does not assign the marker to any catalog item.

## Lifetime

The host retains the complete equipment effect for its first completed attack,
including damage modifiers, Bodyguard interception, Splash/Scorcher and Shock.
The item is consumed on a miss too. It is removed before an opposing counterattack.
A counterattack, an air strike and an AA shot each count as an attack by their actor.
Defending, movement, previews, Research/Production and Capture/Kill do not consume it.
Multiple secondary victims belong to the same attack; they do not cause additional
consumption. An unused item persists between rounds/battles/turns.

Consumption clears only the runtime Equipment slot. It never destroys a shared
CardDefinition, modifies the collection, returns a hand card, charges AP/resources
or refunds their cost. The free slot accepts another item under normal attach rules.
Aircraft returning to hand carry their current slots, so a spent bomb does not return.

EquipmentSystem rebuilds the host from its original attachment baseline and remaining
Mutator. Overrides and removed/replaced abilities are restored by that composition,
while existing wounds, spent movement/Fate and temporary combat changes are retained.
The baseline survives an empty slot so re-arming during a battle cannot bake Drill or
Berserk into permanent stats. Living hosts remain alive if losing a maximum-HP bonus
would otherwise reduce current HP to zero, following the existing attachment rules.

## AI

- EquipmentUpgradeValuation remains the shared recipient/creation scorer. Its combat
  horizon carries the armed/spent state and the surviving HP distribution. It prices
  the first shot's damage, lethality, secondary effects and avoided incoming attacks;
  subsequent contacts use the spent host. It does not divide a permanent weapon's
  combat value by a fixed number of attacks.
- Aviation offense prices one armed sortie and ordinary subsequent sorties. Concrete
  AA opportunities consume the missile on the first legal shot. Existing non-combat
  and aircraft-defense proxy values limit the equipment's benefit to the first
  expected activation; persistent changes from the other slot keep their horizon.
- WorthIt ground Monte Carlo, sequential battles, aviation strike series and BattleAi
  tactical lookahead all consume the item in their own projected state. Tactical
  lookahead owns private unit/ability copies and never burns live unit/army identities.
- Remembered enemy profiles retain immutable stat/ability transitions captured at
  observation. No hidden live item reference is retained. Estimate keys, development
  fingerprints and retreat witnesses include the transition, not just armed stats.
- Repeat production is legitimate resupply. Single-use outputs bypass permanent-item
  repeat damping; occupied slots, pending hand items, source-attempt limits, investment
  gates, chain costs, portfolio arbitration and resource reservations still apply.

## Verification

`SingleUseEquipmentTests` covers misses, repeated shots, defense, previews, collateral,
Shock, counterattacks, retained Mutators/shared abilities, resource/max-stat history,
re-arming with transient bonuses, immutable enemy profiles, cache keys, ground/air/tactical
forecasts, creation valuation and resupply. `AttachmentLifecycleTests` additionally
checks actual AP/resource balances and another operation's reserved Tech.

Run the complete EditMode suite in Unity 6000.5.4f1, then verify one armed aircraft
and one armed ground unit in both normal and Fast Resolve play. Confirm the equipment
icon disappears, the remaining stats/details refresh and an unused item survives.
The portable Mono/stub checks cannot validate native Unity UI/scene behavior.
