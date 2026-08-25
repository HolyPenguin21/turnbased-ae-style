# Aviation Design

## Goal

Add deployable aircraft and helicopters as a separate army type with airfields, fuel/endurance, air strikes, and anti-air reactions. The follow-up AI work must use the same public rules and state; no AI-only shortcut is allowed.

## Scope and constraints

- Aviation is a new unit/card type. Aviation and ground/hero units cannot be in the same army.
- A barracks hex is currently an airfield. Future units may grant airfield capability on other hexes; the design must not hard-code citadels/bases beyond the initial configuration.
- A plane card can be played from hand only into an owned airfield.
- Airfield capacity comes from the building card configuration: citadel `8`, base `4`. The owner will set these values in data.
- A modal stack orders containers as: prison, airfield, garrison, ordinary armies. Show up to eight entries without navigation arrows; show arrows starting at nine entries.

## Airfield and air armies

- An airfield container exists only on a hex that currently contains aviation. It stores aviation cards and supports the existing repair flow/costs.
- Moving cards out of the airfield forms an air army. An air army has a dedicated icon and can contain any mix of planes and helicopters, but no hero or ground unit.
- Movement points are the minimum effective movement value of its members. Terrain move cost is ignored; every entered map hex costs one movement point.
- Aircraft are moved by the player one hex at a time, like a ground army. There is no separate multi-hex attack order.
- Passing through an owned airfield does not land the army. At end of the owner’s turn, an air army on an owned airfield hex lands automatically.
- Landing transfers aircraft in army slot order until capacity is full. Any remaining cards stay in the air army and are treated as unlanded.
- If a base/barracks is captured or destroyed, cards already in its airfield return to their owner’s deck. Air armies above that hex remain airborne and use ordinary end-turn rules.

## Activation, fuel, damage, and repair

- Launch cost is the sum of each member aircraft’s AP and Energy launch costs.
- Each aviation card has card-configured `TurnsWithoutRefuel`: plane `0`, helicopter `1`.
- Runtime state is per aviation unit: consecutive unlanded turn count and whether it already has the emergency fuel penalty.
- At end of owner turn, landing resets both values for every card that landed.
- For each card that remains airborne: if consecutive unlanded turns exceed its endurance and it has no emergency penalty, reduce its current HP by 50% and mark it emergency. Its next-turn movement is half of normal, rounded down.
- If an emergency aircraft again remains airborne at a later end of its owner’s turn, destroy it. Remove an empty air army.
- Landing clears the emergency movement penalty for the next turn but does not restore HP. Repair happens in the airfield via the existing repair rules.

## Air attacks

- Each aviation unit has `HasAirAttackedThisTurn`, reset at the start of its owner’s turn. This is per card, preventing re-launch/merge exploits.
- Entering a hex that contains one or more enemy ground armies or a garrison can start an air strike. A ground army remains a valid target even if it is a garrison.
- Resolve one `BattleAttackPopupUI` challenge per ready aircraft, sequentially. Each challenge chooses a random eligible unit from all enemy armies on that hex. A hero defends with `FateMax`.
- If no enemy army remains, end the sequence. The air army is never locked in strategic combat and can continue moving with remaining MP.
- An unlanded enemy air army on an ordinary hex is also a valid air-strike target. Resolve the same sequence with random aircraft targets and no doubled attack. Aircraft stored in an airfield cannot be targeted.
- A remembered fog target is allowed: the player can fly toward its remembered hex. On arrival, strike only if an enemy army is actually present; otherwise no challenge occurs and the player may continue moving.

## Anti-air ability

- Unit ability data includes AA radius (for example, `AA2` means radius two).
- An AA unit may make one actual AA attack until the start of its owner’s next turn. Skipping a reaction does not consume the shot.
- Track first entry per `(AA unit, air army)` for the current AA-owner turn. If that air army leaves and re-enters the radius after a skipped popup, do not show it again. A different air army may still cause a popup.
- When an enemy air army first enters an AA radius, ready AA units trigger in deterministic sequence. Humans receive an attack/skip popup; AI always attacks.
- AA attacks use `BattleAttackPopupUI`: attacker is the AA unit with doubled attack dice; defender is a random aircraft in the air army. The AA shot is consumed only after attacking.
- A ground army containing AA may also trigger this sequence while it moves if it can see an enemy air army over a normal hex inside its AA radius. Using that shot prevents later reactive AA until reset.
- Before an air strike is resolved, all AA reactions triggered by the movement step resolve first. This includes hidden AA whose own hex is visible but whose neighboring hexes were not visible to the air attacker. If AA destroys the air army, no strike follows.

## Non-goals

- No bombing of empty buildings or bases.
- No special air-vs-air battle screen; air strikes use the existing challenge popup.
- No fuzzy/AI-specific aviation logic in this implementation. The next task teaches the AI to use the same launch, movement, landing, fuel, and AA rules.
