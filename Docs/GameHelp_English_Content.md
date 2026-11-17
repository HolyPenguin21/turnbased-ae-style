# Game Help - English Content

<!-- Generated from Assets/Config/GameHelpCatalog.asset; edit the asset, then run the exporter. -->

86 articles across 11 sections. English fallback text used by the in-game Help window.

The rules describe the current implementation. Costs that vary by card are read from the displayed card or action.

## Getting started

### Match objective, victory and defeat

Topic ID: `goal`

Win a match by becoming the last player who has not been eliminated. Your starting citadel is your critical objective; another base built later does not replace it.

Protect the starting citadel while expanding your economy and attacking enemy positions. If it is captured, recover it before the beginning of your next personal turn. Losing an ordinary army or a later-built base does not by itself eliminate you.

At the beginning of your turn, you are eliminated if your original citadel is still missing or belongs to someone else. Destruction of a starting citadel also triggers defeat. Eliminated players stop taking turns and receiving income. In local multiplayer, the remaining human players can continue after one human is defeated.

### Map controls and interface

Topic ID: `controls`

The strategic map is controlled through hex selection, army selection and contextual panels. Card details show the stats, abilities and costs relevant to the selected object.

Left-click a hex or army to inspect it. Select your mobile army and right-click a destination to issue a move order; inspect the route preview before confirming. Pan with WASD or arrow keys, drag with the middle mouse button, and use the mouse wheel to zoom. Drag cards to a legal hex or an appropriate army/base panel to play them.

Windows that require a decision block the map beneath them. In tactical combat, left-click inspects a card and right-click performs the highlighted action. Use the Help button for these instructions and the gear button for the match menu. Typing in a text field suppresses camera keyboard movement.

### Factions and citadels

Topic ID: `factions`

The playable factions are Iron Concord, The Ashen and The Vessels. A faction determines its available cards, starting citadel and deck choices.

Choose a faction and a compatible saved deck before starting a match or campaign operation. Compare the actual unit and ability entries in Collection rather than assuming that a faction name changes the shared combat rules.

All factions use the same resource, movement, initiative and challenge systems. Their cards provide the differences in stats and abilities. A citadel is also a base, but only the original starting citadel determines whether its owner is eliminated.

### Starting a match and map conditions

Topic ID: `setup`

A match starts with participant settings, faction/deck choices and generated map conditions. Campaign operations supply the target region's conditions instead of using an unrelated standalone map.

Choose the available player controls, factions, decks and map settings. During citadel setup, follow the highlighted selection and confirmation steps. Once the map is ready, confirm your first turn before issuing orders.

Local human and AI control are supported. Local human seats share the screen through turn handoffs; online player turns are not available yet. Terrain, resources and weather can differ between matches, so inspect the map rather than repeating a route from another game.

### Rounds and player turns

Topic ID: `rounds`

A round is a full cycle of active players, followed by the neutral turn slot. A personal turn is one player's opportunity to act within that cycle.

At each new round, income and initiative preparation run before the new player order is used. Confirm your personal turn, spend its resources and AP, then choose End Turn when ready. The order is rolled again for the next round.

A round number is not a promise that you will act before the same opponent again. A delayed battle is resolved before the next round begins. Weather advances on the round cycle, while unit movement and activation reset at the beginning of their owner's personal turn.

### Initiative and turn order

Topic ID: `initiative`

Initiative determines the strategic turn order for the new round and the base AP assigned to each active player. It is separate from the Initiative stat used inside tactical battles.

Before the round order is resolved, decide whether to buy extra initiative tokens. Players roll their pools and are ordered by successes, highest first. Tied groups roll again until their relative order is settled.

Each player receives 5 free tokens and may buy up to 5 additional tokens for that round. First place grants 12 base AP, second place 10, and every later place 8. In-play AP abilities and prisoners may add to this allocation. Eliminated players do not occupy a rank.

### Buying and rolling initiative tokens

Topic ID: `initiative-tokens`

Bonus initiative tokens improve your roll for the current round. They are paid for with resources, not AP, and must be bought again in later rounds.

Use the initiative purchase panel to contribute Human, Energy, Materials or Tech. A token can be paid for with a mixture of resource types. Complete its displayed price before moving on to the next token.

The five additional tokens cost 1, 2, 4, 8 and 16 resource units respectively, for a cumulative total of 31. The total pool cannot exceed 10 tokens including the 5 free ones. Extra tokens improve your chances; they do not guarantee first place.

### Action points and action costs

Topic ID: `action-points`

Action points (AP) pay for deployment, army creation and activation, construction, upgrades, repairs, card draws and research/production attempts. Movement points (MP) are a separate per-unit travel budget.

Read the displayed AP cost before committing an action. Reserve enough AP to activate an army after deploying or transferring its members. An aircraft sortie may also require Energy, independently of its MP route cost.

Base AP is replaced by the next round's initiative allocation, so unused AP is not a bank for later rounds. Resources remain a separate stock. A larger army generally costs more AP to activate because member activation costs are added together; Rapid Reaction reduces its carrier's contribution.

### Start and end of a turn

Topic ID: `turn-start-end`

Your turn opens with movement and activation refreshes and any required notices, reactions or confirmations. The map becomes available after those opening decisions are completed.

Confirm the turn notification, inspect threats and income, then deploy, build, trade or move. Before ending the turn, check aircraft landing/fuel, vulnerable buildings, unused AP and pending decisions. Use End Turn only when those choices are settled.

At turn end, overdue aircraft refuelling can cause damage, Regeneration heals eligible surviving carriers, and a turn without trades can improve your exchange rate. Temporary enemy-position memory can expire. Income is handled at the start of the round, rather than being granted again every time you confirm a turn.

## Cards and decks

### Card types and unit tags

Topic ID: `card-types`

Cards represent Heroes, Units, Bases, Facilities, Tactics and attachments. Equipment and Mutators use attachment rules, while Tactics belong to a separate match inventory.

Inspect a card's type and tags before choosing a target. Unit and Hero cards deploy army members; Base cards establish bases; Facility cards occupy an owned base's slots. Attachments go onto compatible cards or live units instead of directly onto terrain.

Bio, Mechanical and Armored tags affect matching damage abilities. Infantry, Vehicle, Mecha, Aircraft, Support and Hero tags can restrict attachment compatibility. A card can carry several tags at once. Aircraft also have separate storage, launch and fuel rules.

### Card stats and ability icons

Topic ID: `card-stats`

Attack and Defense determine token pools; Range controls legal targets; Hit Points measure survival; Initiative determines tactical order. Heroes also provide Command and Fate. AP and resource costs describe different payments.

Inspect the enlarged card or detail panel for full ability descriptions and current values. Check the army's commander, movement and capacity before adding another unit. For aircraft, also read launch Energy, airfield capacity and refuelling endurance where shown.

Attachments, temporary combat effects, weather and emergency flight can change effective values. A printed base value is not always the final value used for an action. The ability reference explains each tag. A building's displayed Fate is not a separate manual Siege action.

### Deck composition and limits

Topic ID: `deck`

A saved deck contains main cards plus Equipment and Mutator blueprints. It is built for one faction and is limited to 100 deck points across its selected cards.

In Collection, choose a faction and add owned cards with the plus controls. Balance army cards with buildings and the blueprints you want to manufacture. Save the deck and select it when starting a match or campaign operation.

You cannot use more copies than you own or exceed a card's copy limit. Each Hero and each attachment blueprint is limited to one copy per deck. A blueprint unlocks repeat attempts in the match; it is not an unlimited supply of ready-made cards in your starting hand. Main cards form the draw pool.

### Hand size, drawing and using cards

Topic ID: `hand`

The main hand starts with 7 cards and holds up to 10. It contains the cards you can deploy, build with or attach. Tactics use their own inventory and do not consume this capacity.

Use Draw during your confirmed turn to take a card from the remaining deck; the current draw cost is 2 AP. If the hand is full, play a legal card or apply an attachment to free a slot. Inspect and scroll the hand to find cards that are outside the visible row.

Drawing requires both a free slot and cards left in the deck. There is no general-purpose discard button in the current hand interface. Research/Production outputs and some event card rewards also need space, so leave room before attempting to receive a new card.

### Playing cards and deployment requirements

Topic ID: `play-card`

Playing a card validates the target, owner, AP, resources and capacity together. Merely dragging a card over a target does not reserve those costs or guarantee deployment.

Drag a ground Unit/Hero to your eligible deployment building, army or garrison. Deploy aircraft to your airfield storage first. Play a Base card on a legal hex occupied by your own hero army; play a Facility into an available slot of your own base. Use the displayed reason if a drop is rejected.

Ground deployment requires the building ability specified by the card; being near a base is not enough. The final roster must fit its capacity. Successfully produced attachments use their own later play cost and do not charge the already-paid production resource cost a second time.

### Tactic cards and challenge modifiers

Topic ID: `tactics`

Tactic cards modify challenge successes after the tokens are available. They can add successes, suppress opposing successes, or combine both effects according to the card.

During your decision in a supported challenge, select a playable tactic and click the input slot, or drag the card into that slot. Inspect the preview before Accept. You can replace the staged card or return it to the tactic hand before committing; a preview alone does not consume it.

Each side may commit one tactic in a challenge, and committing consumes it from the separate match inventory. A pure suppression card cannot be used against zero opposing successes. Tactics do not occupy main-hand slots. A staged preview may affect the shown result; Accept is the commitment step, not merely selecting the card.

### Equipment and attachment compatibility

Topic ID: `equipment`

Equipment is a permanent attachment that modifies a compatible Unit/Hero card or a deployed unit. Compatibility is defined by the attachment's allowed host kinds and tags.

Right-click the Equipment card in your hand, then left-click a compatible card in hand or a live unit in your open Army Viewer. Read the displayed cost and resulting stats. Use Escape or another right-click to cancel the pending selection.

A unit has a separate Equipment slot and Mutator slot. An occupied slot rejects another attachment of that kind; there is no general replacement action. Some Equipment has Single Use and is consumed after an attack. Equipment is a modifier of its host, not another independently moving army member.

### Mutators and biological upgrades

Topic ID: `mutators`

Mutators are biological attachments and use their own attachment slot. Their production path is Research, while ordinary Equipment is made through Production.

Include the desired Mutator blueprint in your saved deck. Bring a Researcher to an owned Research facility, complete its attempt and then attach the resulting card to an eligible host through the same right-click selection flow used by Equipment.

Host restrictions still apply; a biological description does not make every card compatible. A Mutator slot and Equipment slot are independent, so compatible upgrades of different kinds may coexist. Check the actual card effect for added abilities, stat changes and play costs.

## Resources and infrastructure

### Human, Energy, Materials and Tech

Topic ID: `resources`

Human, Energy, Materials and Tech are four separate resource stocks. Cards and actions can require a specific mixture; a large total stock does not compensate for a missing required type.

Read the resource bar and each action's cost. Build matching collectors on productive hexes, use in-play production abilities and claim event rewards to increase supply. If you have Trade access, exchange a surplus resource for a type you lack.

Resources are distinct from AP, movement and Fate. Initiative token purchases may mix resource types, but ordinary card costs still require their stated types. Aircraft launch Energy is an additional cost to account for when planning sorties.

### Income and resource yields

Topic ID: `income`

At the new round's income step, collectors harvest the effective yields of their hexes and in-play Produce abilities grant their resource bonuses. Income comes before initiative purchases for that round.

Inspect a visited hex's resource quantities before building. Add or upgrade a matching collector only when it can increase your harvest. Protect collection sites and watch hostile occupation, which can interfere with harvesting.

A collector contributes 1 plus its own upgrade level, but total collection is limited by available hex yield and the actual collection rules. Multiple collectors do not create unlimited output from one deposit. Produce abilities grant a separate fixed bonus; cards waiting in hand produce nothing.

### Trade and the exchange rate

Topic ID: `trade`

Trade converts one resource type into another during your own confirmed turn. A living in-play carrier of the Trade ability unlocks access; merely owning its card in hand does not.

Open Trade, choose the resource to give and the resource to receive, then inspect the before/after amounts. Confirm the exchange to pay the current price for 1 unit of the chosen resource. Repeat only if the new rate is acceptable. For example, a 2:1 exchange pays 2 of the given resource for 1 received; the next exchange costs 3:1.

The minimum rate is 2:1. Each completed exchange raises the price by 1. A personal turn with no trades lowers it by 1, down to 2:1. Exchanges cost no AP. The rate belongs to the player; switching between Trade carriers does not reset it.

### Founding bases and expansion

Topic ID: `bases`

A Base is an expansion building that can host Facilities and, when its abilities allow it, a garrison or airfield. Its placement is tied to a hero army on the destination hex.

Move an army containing your own hero onto a passable hex. Play a Base card there and pay its stated AP/resources. The destination must not contain an incompatible or hostile building or a controlling enemy army. An owned resource site can be absorbed when its existing facilities fit.

Construction is on the hero's hex, not at an arbitrary remote position. The current placement rule does not impose a separate base influence-radius requirement. A later-built Base does not become the original citadel for elimination purposes. Use the Base Viewer to inspect its slots and upgrades.

### Base upgrades and facility slots

Topic ID: `base-upgrades`

A new Base begins at level 1 with 2 open Facility slots. Each base upgrade opens one more slot, up to 5 slots at level 4.

Open your Base Viewer and choose Upgrade when the next tier is affordable. Check the displayed AP and resource price before confirming, then use the newly opened slot for a Facility.

The three current upgrade tiers cost 2, 4 and 6 AP, together with 1, 2 and 4 of each resource respectively. Base upgrades also update the base's defensive value. A resource site uses a different slot arrangement; upgrading an individual extractor is not the same as upgrading a Base.

### Facilities and construction requirements

Topic ID: `facilities`

Facilities add capabilities to an owned Base: examples include research, production and resource collection. A Facility occupies one of that Base's currently unlocked slots.

Inspect your Base Viewer for an empty, unlocked slot. Play a Facility card onto the base or its slot and pay the card's cost. If every unlocked slot is occupied, upgrade the Base before adding another Facility.

A Facility card cannot be placed into an ordinary army or on empty terrain through the Base-building flow. Ownership and slot availability are checked at the moment the card is played. A Research or Production facility still needs a matching hero on its hex before it can be used.

### Extraction sites and their upgrades

Topic ID: `extractors`

Extraction buildings harvest Human, Energy, Materials or Tech from their hex. They are built through the resource actions, rather than drawn as ordinary Facility cards.

Move an owned hero army onto a passable resource hex, inspect its yields and choose the matching resource-building action. Pay the displayed AP/resources. Upgrade a placed collector when the extra capacity can harvest more of the existing deposit.

Only one Facility with the same collection ability may be built on a building through this action. Construction is rejected when it adds no income or no slot is free. A standalone resource site has four available resource slots. Upgrades cannot increase a hex's resource yield; they only improve how much of that yield a collector can harvest.

### Garrisons and base defence

Topic ID: `garrisons`

A garrison is the stationary army associated with an eligible building. It receives deployed ground units and defends the location but cannot be issued an ordinary move order.

Open the garrison to inspect its roster. Keep defenders there, or create a field army and transfer selected members into it when you need a mobile force. Use a hero commander when the final roster requires greater capacity.

A hero-less garrison has nominal capacity 4, compared with 2 for a hero-less field army. A commander replaces that nominal limit with its Command rating. Aircraft use airfield storage, not the ground garrison. Garrisons cannot choose the normal retreat command.

### Building damage, repair and destruction

Topic ID: `building-damage`

Ordinary ground conquest is resolved by defeating or driving away the building's defenders. A Base or Citadel is normally captured intact; a standalone hostile resource site is destroyed when taken.

Clear or force away the defending army, then occupy the location with a force that can act. Watch for additional hostile defenders on the hex. Inspect damaged structures in the Base Viewer; its Repair action restores the managed Base's structure points.

There is no separate player-operated Siege challenge against a building's displayed Fate. Explosions can damage structures directly, including friendly structures. Hidden-only movers do not automatically seize buildings. Capturing an original citadel gives its owner a recovery window until that owner's next turn begins.

### Research and researchers

Topic ID: `research`

Research makes Mutator attachments from the blueprints available to your selected deck. It requires an owned building with a Research Facility and one of your non-prisoner Researcher heroes on that exact hex.

Bring the Researcher to the facility, open Research, choose an unlocked blueprint and review its difficulty and attempt cost. Leave a free main-hand slot. Start the attempt, use available Fate or a tactic if useful, then accept the result.

Choosing Research reveals the participating hidden Researcher; it does not reveal every member of the army. The attempt pays AP/resources even if the roll fails. A successful output enters the match hand. One deck blueprint permits repeated attempts, rather than being consumed after one success.

### Production and assemblers

Topic ID: `production`

Production makes Equipment from available deck blueprints. It requires an owned Production Facility and an Assembler hero belonging to you on the same hex.

Bring the Assembler to the facility, open Production and select an available blueprint. Review attempt AP, resources and required successes, keep a free hand slot, then start and resolve the challenge. Apply the completed attachment separately when ready.

Production uses the same success test as Research, but does not reveal a hidden acting hero merely for choosing Production. Starting an attempt spends its AP/resources even if it fails. Its resulting card does not pay the production resource cost again when later played.

### Research and production challenges

Topic ID: `production-check`

A Research/Production challenge rolls a token pool equal to the acting hero's maximum Fate stat. The blueprint's difficulty is a fixed number of required successes, not an opposing random roll.

Roll, compare successes with the displayed requirement, and spend the attempt's available Fate if it can help. One Fate rerolls a miss; if no misses remain but the target is still unmet, it can add another die. A supported tactic may change successes or difficulty. Accept to finish.

Success requires meeting or exceeding the required total; equality succeeds. Fate spending is local to this attempt and does not drain the live hero's combat Fate. Failed attempts do not refund their starting cost. A free hand slot and an available blueprint are required to start.

## Armies and movement

### Army composition, leader and capacity

Topic ID: `army-composition`

A ground army is a roster that moves and fights together. Its first hero is its commander; additional heroes do not add their Command ratings together.

Open the Army Viewer to check members, leader, current/max movement and capacity. Arrange the roster so the intended hero leads, and keep enough room before deploying or transferring more units. A commander's Command value replaces the ordinary capacity limit.

A field army without a hero has capacity 2; a garrison without a hero has capacity 4. Heroes count as members too. New additions must fit the resulting roster. A commander also supplies the army's battle Fate and its Initiative bonus. Aircraft cannot share a normal ground roster.

### Heroes and armies without a hero

Topic ID: `hero`

Heroes command armies and provide access to construction, Research, Production and other abilities. They are support pieces in ground battles rather than ordinary units in the acting queue.

Keep a hero with the force that needs its Command, Fate or specialist abilities. Small hero-less forces can still move, scout and fight if they contain combat-capable units. Protect an isolated hero with ordinary troops when possible.

A hero-less field army is limited to 2 members. A lone hero can be forced out through a Capture/Kill encounter even when the attacker cannot guarantee its capture. On a tactical grid, heroes deploy in their side's back row. An air strike can target a hero only when it is its army's sole living member.

### Creating and renaming armies

Topic ID: `army-creation`

Creating a mobile army gives selected units a separate roster and map marker. Naming an army changes its label, not its ownership, capacity or abilities.

Open your garrison and use Create Army, then transfer the desired members into the new force. You can also use supported drag/transfer flows to split a roster. Open a mobile army and choose Rename to edit its name.

The current army-creation cost is 2 AP, separate from deployment or activation. A newly created empty roster does not conjure units; populate it before using it. Empty mobile armies are cleaned up. Aircraft formation has its own airfield flow and cannot be mixed with ground units.

### Army activation and AP costs

Topic ID: `army-activation`

The first move order of a personal turn activates an army. Its AP cost is the sum of the activation costs of its members, with relevant abilities applied.

Inspect the army and route preview before moving. Keep enough AP for its first activation as well as MP for travel. For airborne units, also reserve any unpaid launch Energy. Further movement by the same already-covered members uses MP without charging the same army activation again.

A fresh unit joining an already-activated army can owe its own activation share immediately. Returning the same covered unit to that army in the same turn does not charge it twice. Activation tracking resets at the owner's next turn. Rapid Reaction contributes 0 AP for its carrier.

### Movement points and route costs

Topic ID: `movement`

An army's usable movement is constrained by its slowest effective member. MP pays for route steps, while AP pays for activation. A reachable destination may therefore still be unaffordable in AP.

Select the army and inspect the route arrow before right-clicking the destination. Compare the full path cost with current movement, and account for enemy contact and events that can stop the journey before its original endpoint.

Ground entry costs use the destination terrain, with a minimum of 1 MP. Aircraft normally pay 1 MP per transition regardless of terrain. Leaving a sandstorm hex adds 1 MP for either profile. Movement refreshes on the owner's turn; an attack can exhaust remaining strategic movement.

### Terrain, passability and defence

Topic ID: `terrain`

Terrain determines ground passability, movement cost, resource yields and a defending army's terrain protection. Different routes across the same distance can therefore have different costs.

Inspect a hex and the route preview before committing a slow army. Use low-cost routes when speed matters and defensible terrain when holding a position. Aircraft use their own movement profile instead of ground entry costs.

Impassable ground terrain stays impassable during a sandstorm. Terrain defense benefits the defending ground army, not every army sharing the hex. Sandstorms remove terrain and building defense bonuses for the encounter and reduce attack range where applicable. Hidden units may gain concealment from more costly terrain.

### Transferring, exchanging and splitting units

Topic ID: `transfer`

Transfers move members between your armies on the same hex. The final rosters must remain legal for capacity, commander order and ground/air container type.

Use the Army Viewer's army controls and unit-card drag flow to move or exchange selected members. Check which hero will lead each final army. Split units into a newly created field army when the current roster should remain stationary.

Joining an already-activated destination may charge the incoming unit's activation AP and unpaid aircraft launch costs where relevant. Moving a commander can lower the old roster's capacity, so a transfer may need a different grouping. Stored aircraft, carrier decks and ground garrisons are distinct destinations; they cannot accept every unit type.

### Infantry transport

Topic ID: `transport`

Transport helps slower Infantry move with a faster carrier in the same ground army. It changes the eligible passenger's effective movement; it does not create an aircraft or a separate embarkation inventory.

Place a Transport unit with compatible, slower Infantry and inspect the army's movement after the roster updates. Keep the carrier and its passenger together while travelling. Use enough carriers if several slow infantry members need assistance.

Each carrier supports one eligible non-hero Infantry unit. The carrier must be faster than the passenger. Aircraft, prisoners and summoned units are not passengers. The army is still limited by its slowest effective remaining member, so one transported passenger does not make every slow unit fast.

### Unit healing, repair and regeneration

Topic ID: `unit-repair`

Paid repair restores a wounded live unit or hero to maximum HP. It is available at one of your Bases or on a hex served by an eligible friendly ground Repair carrier.

Open the Army Viewer at a repair location, select a wounded unit and use its repair control. Inspect the price shown for that specific unit. Repair before the next battle or before risking another overdue aircraft refuelling step.

Each paid repair costs 1 AP plus its stored resource cost. That resource price is sampled once from half of the card's original resource units, rounded up, and stays fixed for that instance. Repair is distinct from Heal and Regeneration abilities. Dead units and prisoners cannot be restored with this action.

## Reconnaissance and weather

### Vision and fog of war

Topic ID: `fog`

Hex visibility and unit detection are different. A visible hex can still contain an individually hidden unit, while a previously seen hex can remain remembered after live sight is lost.

Move armies or use reconnaissance abilities to observe new territory. Inspect the map's visible and remembered states before assuming an old marker still represents a live position. Visit a hex with an appropriate ground force to learn the resource quantities shown there.

The current basic army vision radius is 0, and basic building vision radius is 1; reconnaissance can extend army vision. Seeing a hex can reveal resource types without revealing all quantities. Fog does not grant knowledge of later enemy moves, building changes or hidden army composition.

### Last observed enemy positions

Topic ID: `memory`

An enemy marker in fog can be a last-observed snapshot rather than a live army. Its position, roster and stats describe what you saw, not what is currently happening out of sight.

Use remembered positions to plan scouting, then confirm them with fresh vision before committing an attack. Inspect a remembered army as historical information; it is not a target that follows the enemy invisibly.

Moving enemy snapshots expire at the end of the observer's personal turn. A sighting made between turns can therefore remain until that player's next turn ends; one made during their own turn expires at that turn's end. Stationary neutral armies and buildings remain known until a fresh observation corrects them. Completely hidden armies are not remembered as if they had been seen.

### Reconnaissance range and detection

Topic ID: `scouting`

Reconnaissance tags combine extra hex vision with a spotting pool. The r1sX family adds 1 to army vision; X is the spotting strength used against hidden units.

Include a reconnaissance carrier in the army that needs a wider view. Move it near suspected hidden forces and let detection checks resolve when relevant sources or targets enter observation. Compare spotting strength with the enemy's concealment rather than relying on vision radius alone.

Use the best applicable reconnaissance radius and spotting pool, not the sum of every carrier. r1s0 extends ordinary vision but has no adjacent spotting dice. Ordinary sources can make a minimal check on their own hex; dedicated reconnaissance can check adjacent hexes. Detection needs strictly more successes than concealment.

### Stealth and detection challenges

Topic ID: `stealth`

Stealth belongs to individual units, not automatically to every member of their army. A hidden unit can occupy a visible hex without being personally detected by every opponent.

For an eligible Stealth carrier, use the stealth action and pay 1 AP to enter. Leaving stealth costs 0 AP. Reveal the carrier before taking its offensive actions; visible members of a mixed army can still act under their own rules.

A unit cannot repeatedly leave and re-enter stealth in the same personal turn. Hide tokens equal its stealth level plus the terrain move-cost bonus. Detection uses the strongest applicable observer pool, and a tie preserves stealth. Detection is personal to the observer and lasts through that observer's next turn; it does not reveal the unit to everyone.

### Tactical intelligence

Topic ID: `intelligence`

Tactical Intelligence unlocks an opponent information panel while you have an in-play carrier. The amount of information depends on the distance of your qualifying ground armies from the enemy's starting citadel.

Open the opponent intelligence view when access is available. Move an active field ground army closer to an enemy citadel to reveal more fields, while still respecting enemy defenses and fog.

At distance 3, 2, 1 and 0, the report reveals 4, 8, 12 and 16 fields respectively; distance 4 or more reveals none through proximity. Garrisons, prisons, aircraft and carrier containers are not the qualifying field observers. The revealed selection changes with your personal turn. This report does not replace live vision or stealth detection.

### Sandstorm lifecycle and movement

Topic ID: `sandstorms`

Weather conditions allow no storms, up to 1 storm or up to 2 storms. Each storm grows, changes its covered hexes, drifts and fades on the full-round cycle.

Inspect the current storm footprint when choosing movement and battle positions. Recheck it after a new round begins rather than assuming the previous route is still clear. Campaign region weather is part of the operation's conditions.

A storm lasts 6 active rounds including birth. Its target sizes by age are 1, 3, 5, 6, 5 and 3 hexes. Its centre can drift one hex or lull, with a connected footprint that changes shape. After disappearance, its slot rests for 2 rounds. Storms avoid the outer border and do not overlap each other.

### Sandstorms: movement, range and defence

Topic ID: `storm-effects`

A sandstorm changes route costs and battle protection. The costs apply to the actual covered hexes, so two equally long routes can become different in MP.

Budget an extra 1 MP every time a ground army or aircraft leaves a storm-covered hex. If you fight in a storm, check each unit's effective attack range and avoid relying on a building or terrain defense bonus.

The battle receives no terrain or building defense bonus in a storm. A positive attack range above 1 is reduced by 1; range 1 is not reduced below 1. These are situational effects, not permanent damage to card stats. The encounter keeps the weather snapshot taken when it begins until it ends.

## Battles and challenges

### Enemy contact and starting a battle

Topic ID: `contact`

Enemy contact occurs when opposing forces meet on a hex. The encounter depends on the actual participants: ordinary ground combat, a lone-hero Capture/Kill challenge, or aviation reactions may apply.

Select an attacking mobile army, preview its route and right-click the enemy location. When a contact choice appears, inspect both forces and the hex bonuses before choosing Fight or an available Delay option.

A force does not need to guarantee killing an isolated hero to gain ground: forcing it to retreat can expose a building. Fully hidden units follow stealth restrictions. Ground attack commitment can consume the army's remaining movement. A map hex may contain several distinct armies, so inspect what remains after a battle.

### Immediate and delayed battles

Topic ID: `battle-delay`

An available Delay choice postpones that hex's encounter until the queued battles are resolved before the next round begins. It does not cancel the confrontation or grant a fresh turn first.

Use Delay only when the remaining turns in the current round can improve your position. Bring eligible reinforcements if possible, then be ready to resolve the encounter before new-round income, initiative and weather progression.

Only one pending battle is stored for a hex. Armies reserved for a pending encounter cannot freely sign up for other battles. Do not assume that leaving the popup has removed the commitment. Saving is unavailable while an encounter remains unsettled.

### Battle deployment and formation

Topic ID: `deployment`

The tactical battlefield has 5 columns and 5 rows: each side has a back row and front row, separated by a shared neutral row. Arrangement precedes the acting rounds.

Drag your unit cards into legal deployment cells or swap their positions during arrangement. Place heroes in any column of your own back row. Put short-range troops where they can reach the enemy and support pieces where their abilities can help.

Ordinary ground combatants can deploy in either of their side's two rows; a hero cannot deploy in the front row. The neutral row is used by later tactical movement. Formation does not change which army a unit belongs to. Your own army's arrangement can be reused for later battles when its members remain available.

### Battle rounds, initiative and unit turns

Topic ID: `battle-rounds`

A tactical round gives each eligible ground combatant a turn in descending effective Initiative. A living commander adds its Initiative to its side's ordinary combatants.

Review the round-start order, then act with the highlighted unit. Choose a legal move, attack or Pass. Plan for enemy units that will act before your next combatant, and consider Shock Attack or protective positioning.

Heroes support their side but are not ordinary actors in the ground combat queue. Equal effective Initiative is resolved by a round-specific tie-break. Abilities such as Drill and Bodyguard refresh according to their round rules. Strategic AP is not paid again for every tactical click.

### Battle movement and attack range

Topic ID: `battle-movement`

A unit's tactical turn can be used to move one cell orthogonally, attack a legal enemy within range, or pass. A tactical move and an attack are separate alternatives for that acting turn.

Right-click a highlighted empty adjacent cell to move, or right-click a legal enemy to attack. Left-click a card to inspect it without issuing the action. Use the shared middle row to bring short-range units into contact.

Diagonal movement is not legal, but attack range uses square distance and can include diagonals. A target must be alive and hostile. Sandstorms reduce applicable attack range. The tactical grid is separate from strategic hex MP; spending a turn to move on the grid is not a map travel step.

### Challenge tokens, successes and ties

Topic ID: `challenges`

Challenges compare token successes. Each token has a 50% chance of success; more tokens improve the distribution of possible results but do not guarantee a hit.

Read both pools before rolling, then compare successes and any tactic modifiers. Decide whether to use Fate or a supported tactic before accepting. In ground combat, Attack supplies the attacking pool and Defense plus eligible defender bonuses supplies the defending pool.

Ground damage begins at max(0, attacking successes minus defending successes), so equality causes no base damage. Other challenge types use different thresholds: Research/Production succeeds on equality, Capture/Kill kills on equality, and stealth detection fails on equality. Read the specific topic before treating all ties the same. For example, 4 attacking successes against 2 defending successes give 2 base damage before abilities.

### Fate points, rerolls and the Fate duel

Topic ID: `fate`

The commanding hero supplies the army's combat Fate budget. Fate can reroll a failed token during a challenge; it does not guarantee that the reroll succeeds.

After the initial roll, use Spend when you want to retry a miss, or accept/pass when the current outcome is acceptable. The defender makes the first Fate decision, and the sides alternate while meaningful decisions remain.

Combat Fate remains spent for the rest of that battle and refills when the encounter ends, not at every tactical round or strategic turn. Each combat spend rerolls one miss rather than adding a guaranteed success. Research/Production uses its own local attempt budget and supports an extra die when its rules allow it.

### Attack, defence, damage and hit points

Topic ID: `damage`

Ground combat starts with the difference between attacking and defending successes. Only a positive result enters the normal damage-modifier chain.

Inspect target tags and defensive abilities before committing a hit. Use an appropriate damage specialist against Bio, Mechanical or Armored targets, and account for Ceramic Armor. Keep enough HP to survive return attacks and collateral effects.

The current modifier order is Critical Damage multiplication, matching Hyperkinetic/Pyrokinetic/Ionic bonuses, then Ceramic Armor reduction. A zero base hit is not turned into damage by those bonuses. Surviving units keep their wounds after battle; ending a fight does not fully heal ordinary HP. For example, 2 base damage becomes 4 with Critical Damage, then 3 against Ceramic Armor when no other modifiers apply.

### Building defence and Disruption

Topic ID: `building-defence`

The defending ground army can receive terrain defense and its own Base's building defense. The attacker does not receive those bonuses just because it stands on the same hex.

Inspect the encounter's defense summary. Bring a living ground Disruption carrier if you want to cancel a hostile Base/Citadel's building contribution. Alternatively, consider whether storm weather already removes that protection.

Disruption removes the building part only; ordinary terrain protection remains outside a storm. An eligible carrier already on the hex or arriving with the attacking army can provide the effect without being revealed merely by the rule. A sandstorm removes both parts, and an airborne defender receives neither hex bonus.

### Bodyguard and counterattacks

Topic ID: `reactions`

Bodyguard can redirect a primary ground hit onto an adjacent allied carrier. Counterattack can answer a close enemy attack after the victim survives. Formation determines whether these abilities are available.

Put Bodyguards orthogonally beside units you want to protect. Avoid making a close attack against a dangerous Counterattack unit unless you can survive the response, kill it, or use a damaging Shock Attack to suppress that response.

A Bodyguard intercepts once per tactical round and heroes cannot guard. Counterattack range includes diagonals within one cell. The intercepted hit uses the guard's own defensive abilities. A counterattack is not itself intercepted or countered, so these reactions do not create an endless chain.

### Explosions and single-use attacks

Topic ID: `explosions`

Explosion attacks replace a normal single-target attack with separate challenges against a random set of distinct occupants on the target hex. They can hit units and structures, including friendly ones.

Inspect the explosion size and the units/buildings sharing the location before launching. Keep valuable friendly armies and your citadel away from the potential target pool. Expect a separate defense/Fate resolution for each selected living target.

Explosion 3, 5 and 10 select up to that many targets, but each attack uses a 10-token offensive pool. The number in the ability is not its damage or dice count. The launcher is excluded from its own target pool. Single Use attachments or innate disposable units are spent at attack completion under their separate rules.

### Retreat and pursuit

Topic ID: `retreat`

Retreat gives up the current fight and risks pursuit before survivors relocate. It is a tactical decision, not a free teleport to a safe Base.

Choose Retreat when the encounter offers the decision, before the round's acting sequence is underway. Check which enemies will still act against the withdrawing force and whether a nearby destination is actually safe.

The ordinary retreat command is unavailable to garrisons. Survivors move to an adjacent passable hex, preferring a direction toward the nearest eligible owned Barracks; they do not jump directly to it. If no retreat destination exists the army is lost. The destination can trigger another contact, and abandoning a defended building can let it be captured or destroyed.

### Capturing or killing a lone hero

Topic ID: `capture-kill`

A force facing a hero without ordinary combatants can resolve a Capture/Kill challenge. The action can be useful even when it only forces the hero away from a valuable hex.

Bring combat-capable hunters into contact and inspect the challenge pools. Use Fate and a supported tactic where available, then accept the outcome. If the hero escapes, inspect the cleared position and any building control change.

The hunter pool is 1 plus half the number of non-hero units, rounded down. More hunter successes capture the hero, equal successes kill it, and fewer successes let it escape. A hero's escape does not mean it can continue blocking the same location unchanged; surviving lone heroes may be made to retreat.

### Prisoners, prison armies and release

Topic ID: `prisoners`

Captured heroes are held in the captor's prison army at its original citadel. Prisoners are not ordinary deployable or movable members.

Inspect the citadel's prison to review captured heroes. Protect prisoners as an economic advantage for the captor; they cannot be dragged into field armies, used as commanders or used as active ability sources.

Each captive contributes 2 AP to its captor's round allocation. Prison armies are immobile and their contents are read-only. When the captor is eliminated, prisoner release is resolved through the existing ownership/capacity rules. There is no separate general ransom or manual prison-transfer command.

### Battle outcome and destroyed armies

Topic ID: `battle-outcome`

A battle can end with one surviving side, a retreat, capture/death of isolated heroes, or both sides gone. The final surviving units return to the strategic situation with their actual remaining HP.

Acknowledge the result, inspect the hex again and check remaining armies, buildings, event guards and rewards. A victory over one army does not guarantee that every hostile force or event on the hex has been cleared.

Temporary combat stat changes are reverted and battle Fate refills. Summoned units are removed when their temporary role ends. Undefended Bases can change owner and resource sites can be destroyed. Event rewards require their own event resolution, and a newly exposed guard can begin another encounter.

## Aviation and air defence

### Airfields and aircraft capacity

Topic ID: `airfields`

Aircraft deploy into owned airfield storage before becoming a mobile air army. Airfield capacity is a real finite limit, distinct from a ground garrison's Command capacity.

Inspect a base or mobile carrier that provides aircraft capacity. Deploy aircraft to its airfield container, select the intended members and form a wing through the supported Army Viewer transfer/formation controls.

Check both the receiving container and the total storage at the hex. A full airfield can prevent deployment, transfer or landing. Aircraft do not occupy ordinary ground-garrison slots. Stored aircraft and a ready wing are different rosters even when they share the same map location.

### Mobile carriers and aircraft transfers

Topic ID: `carrier`

A living ground unit with Airfield capacity can act as a mobile aircraft carrier. Its stored aircraft and ready wing are separate aviation containers associated with that carrier.

Open the carrier's airfield/wing views, move compatible aircraft between available containers and select the exact group you want to send. Keep enough shared capacity to receive returning aircraft. Move the ground carrier with its army when repositioning the mobile base.

The carrier's airfield and ready deck share its physical capacity. A formed wing becomes independent when it actually activates for take-off. Ground troops cannot be put into aircraft containers. Losing or moving a carrier can change legal landing opportunities, so plan a fixed-airfield alternative when necessary.

### Air armies and sorties

Topic ID: `air-armies`

A mobile air army contains aircraft only. Forming the wing at an airfield is different from paying for take-off and flying its first route.

Select stored aircraft, form the intended wing and preview its destination. On first actual activation, ensure you can afford the members' AP activation plus any unpaid launch Energy. Keep the wing's slowest aircraft and fuel endurance in mind.

Forming the stack alone does not charge another independent launch payment. The common movement activation path pays the outstanding costs. Each aircraft can normally attack only once during its owner's turn. Fuel and landing are tracked for each aircraft, so a mixed-endurance wing must protect its least enduring members.

### Flight routes, range and return

Topic ID: `flight-route`

A safe flight plan includes the outbound path, enemy AA exposure and a legal landing/refuelling opportunity. Map distance alone is not the whole cost.

Preview the full route and keep enough usable MP for the next necessary leg. Aircraft pay 1 MP per normal transition and another 1 MP when leaving a storm hex. Choose a destination with owned landing capacity before fuel endurance runs out.

The slowest effective member limits the wing's movement. Terrain that blocks ground troops does not automatically have the same effect on aircraft. Taking off again after a completed landing is a new paid sortie. Landing/refuelling does not automatically restore HP, refill outgoing-turn MP or permit another attack that turn.

### Air strikes and target selection

Topic ID: `air-strikes`

An eligible aircraft can strike enemy occupants when its flight reaches the target hex. The normal attack selects a legal enemy unit from the available target pool; it is not a player-selected precision hit on any card.

Route a ready wing to the desired hostile location after checking fuel, return capacity and AA. Resolve any reaction and attack prompts, then inspect survivors before continuing the route or planning the next turn.

Each aircraft attacks at most once per owner turn. A hero is an ordinary air-strike target only when it is its army's sole living member. Normal air strikes use unit Defense, or a lone hero's Fate stat, without ground hex bonuses. Stationary repeat-strike support exists in the rules, but there is no separate human repeat-strike button in the current interface.

### Air defence range and reactions

Topic ID: `anti-air`

AA units and eligible Facilities can react to hostile airborne armies within their own anti-air radius. Range alone is insufficient: the AA owner must see the target hex and the relevant units must be targetable.

Position AA to cover likely flight routes and maintain vision of those hexes. When your reaction choice appears, choose Attack to fire or Skip to preserve the unit's shot for another wing. Reveal a hidden AA carrier before expecting it to fire.

An AA unit can fire once before its owner's next turn refresh. Attack and Skip both suppress another prompt for the same unit/wing pair in that cycle; Skip can still leave a shot for a different wing. Movement of either side and turn-opening checks can create opportunities. Aircraft in storage and carrier containers do not act as ordinary reactive AA units.

### Aircraft losses and recovery

Topic ID: `air-recovery`

Aircraft fuel endurance counts owner turn endings without a successful landing/refuel. It is separate from MP and HP. Reaching an owned airfield is useful only if landing capacity is available.

Bring aircraft to an owned fixed airfield or eligible carrier with free space before their endurance expires. Inspect end-turn reports for aircraft that could not land. Repair wounded survivors through a legal repair source; refuelling alone does not heal them.

Every overdue owner turn ending removes half of maximum HP, rounded up, and activates the emergency flight penalty. Usable movement is then halved, rounded down. Repeated overdue endings can destroy the aircraft. Successful landing clears the fuel/emergency state and ends the paid sortie, but does not refund its launch costs. For example, an aircraft with 5 maximum HP loses 3 HP at each overdue ending, even if its current HP is already low.

## Events and neutral forces

### Neutral armies and their turn

Topic ID: `neutral`

Neutral forces are separate hostile occupants or event guards, not another playable deck faction. A neutral turn slot follows the active players in the round sequence.

Inspect a neutral army before attacking and compare its actual roster with your force. Keep a route back to safety when approaching guarded events. Seeing a neutral marker does not automatically claim its location or reward.

An event guard can be created when the event is explored; unrelated neutral armies can occupy the same area. The neutral turn slot refreshes neutral-side movement/reaction state. Defeating an unrelated army does not bypass an unresolved event's Explore choice or its own guard.

### Exploring event hexes and their guards

Topic ID: `event-exploration`

Events are discovered by an eligible ground army reaching their hex. Choose Explore to resolve the event or Skip to leave it for later.

Read the event description and guard information before exploring. If it is guarded, defeat its guard; if it is clear, resolve the reward. Return to a previously skipped event with a suitable ground force when you are ready.

Aircraft do not claim ground events merely by flying over them. Choosing Explore reveals every hidden member of the visiting army and exhausts its remaining strategic movement, whether or not a guard is present. Skip preserves stealth. An event is consumed only when its reward is resolved; clearing an unrelated enemy does not automatically beat the event's guard.

### Event rewards: resources and cards

Topic ID: `rewards`

Events can grant resource bundles, match cards or special rewards. The selected event outcome determines what is available; a map picture alone is not a guarantee of a particular card.

Explore the event, defeat any required guard and read the reward report. Keep a free main-hand slot if you expect an ordinary card, and inspect the separate tactic inventory for Tactic rewards.

Resources go to the recipient player's stock. Ordinary event cards are match rewards and do not automatically become permanent Collection copies. Library events are the explicit exception for permanent card acquisition. Do not assume that a full hand can accept every optional match-card reward.

### Library: a card for the collection

Topic ID: `library`

The Library is a special human-player event that can add a random card permanently to Collection after its guard is resolved.

Reach the Library with an eligible ground army, choose Explore and defeat its guardians. Confirm the reward and inspect Collection for the new copy. Leave room in the main hand if you also want the match-card delivery when available.

The permanent Collection grant and a usable match card are distinct parts of the reward. The event draws from its allowed card candidates; it is not limited to a hand-picked card from your current deck. A Library is not claimed by an aircraft flyover. Ordinary resource/card events do not all share this permanent-reward rule.

### Abandoned command post: restoring a base

Topic ID: `command-post`

An Abandoned Command Post is a guarded event whose special reward restores a level-1 Base for the visiting player on that hex.

Explore with a ground army and defeat the event's guard. After the reward is applied, inspect the restored Base, its garrison and available slots before planning new deployments or construction.

This event reward does not spend a Base card, construction AP or construction resources. It is a newly restored ordinary Base, not a replacement original starting citadel. The event's prerequisites and clear-hex checks still apply; its free reward is not a general free Base-building action elsewhere.

## Campaign and collection

### Campaign map and region ownership

Topic ID: `campaign`

The campaign map tracks region ownership by faction. Campaign actions and strategic matches are separate layers: a match result is used to update the chosen regional operation.

Select regions to inspect their owner, neighboring regions and conditions. On your faction's campaign turn, choose a legal neighboring hostile region from a region you own, or use the available turn action when you do not want to attack.

A faction with no regions is eliminated from the campaign. The campaign finishes for the human faction when it is eliminated or becomes the only remaining faction. Regional turn order is determined on the campaign round cycle; it is not the initiative order inside an individual match.

### Attacking regions and choosing a deck

Topic ID: `region-attack`

A legal campaign attack starts in an owned region and targets an adjacent region belonging to another active faction. The operation carries its selected deck and conditions into the match.

Select an available attack, inspect its source/target and choose the saved faction deck you want to use. Read the confirmation before launching the offered manual or automatic resolution. Use the deck-selection marker to verify the active choice.

You cannot attack a non-neighbor, your own region or a target outside the current campaign action state. Only one operation can be pending at a time. A deck must satisfy its faction, ownership, copy and point rules. Editing a profile deck later does not rewrite the starting loadout of a match already in progress.

### Region terrain, resources and weather

Topic ID: `region-conditions`

A region has terrain, resource and weather conditions that shape its operation. Those conditions matter to route planning, economy and battle protection.

Inspect the target region before launching an attack. Choose a deck that can operate on its terrain and support its economy. In the match, confirm local resource quantities through ground exploration and inspect the actual generated layout.

The operation preserves its chosen region conditions and weather seed; it does not silently substitute a different weather profile when the scene opens. Clear regions have no storms, while storm profiles allow up to one or two. A regional summary is a planning guide, not a view of every hidden army or event.

### Campaign results and region capture

Topic ID: `campaign-result`

An attacker victory transfers the target region to the attacking faction. A defender victory or draw leaves its ownership unchanged.

Finish the operation and acknowledge the campaign result before selecting another action. Check the updated region owner and the battle history. If a result cannot be recorded, resolve the displayed error before dismissing the result flow.

Campaign ownership is updated once per recorded operation. Factions that lose their last region are eliminated. Leaving an unfinished match through the menu is not the same as winning and does not entitle you to a region. The result concerns the selected target region, not automatic ownership of neighboring territories.

### Collection and card copies

Topic ID: `collection`

Collection stores your permanent owned card copies and saved decks. A match hand is a temporary inventory; receiving a card during a match does not always add a permanent Collection copy.

Open Collection to inspect cards, owned counts, deck-point prices and allowed copies. Use its faction/category controls to find main cards, Equipment and Mutators. Save a valid deck and select it for future matches.

Including a copy in a deck does not consume it permanently; it limits what that loadout may use. Rewards and special Library grants can increase ownership. In-play deaths and ordinary match-card use are separate from permanent ownership. Some system/event-only cards are intentionally excluded from deck building.

### Creating and editing decks

Topic ID: `deck-editor`

The deck editor builds a saved loadout from owned cards. Main cards are drawn during a match; Equipment and Mutator entries unlock their respective manufacturing options.

Choose a faction, add or remove copies with the plus/minus controls, inspect the total points and save the deck. Select the saved deck when entering a match or campaign attack. Read warnings about missing heroes or Bases before deciding to play.

The cap is 100 points, with individual card-copy and ownership limits. Heroes and attachment blueprints are limited to one copy each. You can remove cards from a deck that has become invalid after an update. A saved match retains its frozen starting loadout rather than adopting later editor changes.

### Match rewards and collection progression

Topic ID: `match-rewards`

Eligible completed human matches can grant permanent Collection rewards. Rewards are recorded for the match, so loading or re-opening a result is not a new grant.

On victory, choose the required number of different cards from the available offer and confirm. A victory offers up to 5 candidates and normally lets you select 2; if fewer remain available, the choice shrinks accordingly. A defeat offers up to 1 eligible card and applies that grant once.

Reward candidates respect faction permission and the remaining Collection copy limits. A draw does not grant the normal win/loss reward. In a non-campaign local multiplayer match, the account receives one outcome: victory for the sole surviving human or defeat when the last human is eliminated. Earlier human eliminations do not each grant separate rewards.

## Menus and saving

### Saving, loading and slot management

Topic ID: `save-load`

Save and Load are available through the gear menu during a legal human-turn state. Saves use numbered slots and preserve the active match rather than starting a new random setup.

Open the menu, choose Save, select a slot and confirm. Overwriting an occupied slot requires confirmation. Use Load to choose an existing saved match; loading replaces current unsaved progress. Delete a slot only when you want to remove that saved match.

New slots can be added from the save window. Slot selection alone does not execute the action. A saved match keeps its own map, participants and starting loadouts. If a slot reports an error or incompatible content, read that message rather than assuming that opening it succeeded.

### When a match can be saved

Topic ID: `save-limits`

A match can be saved only during a confirmed human turn after its opening actions have completed, while the game is in a settled state.

Finish the encounter, wait for all armies to stop, accept pending reports, close decisions, and finish or cancel attachment and Research/Production operations. Reopen the gear menu and use Save once the displayed restriction is cleared.

Saving is unavailable during battles or unsettled encounters, movement, restore/loading, research/production transactions, pending decisions and card operations. It is also unavailable before turn confirmation, on an AI turn or after the match has ended. The save button uses the live conditions, not merely whether a window is visible.

### AI match spectator: pause and fast resolve

Topic ID: `spectator`

An AI-only match provides observer controls for watching play. The spectator can pause AI progress at an action boundary and use the available fast-resolution controls.

Use the playback/pause control to request a pause or continue observing. Open the gear menu or Help even while an AI turn is active. While paused for inspection, use the permitted map views; close the menu before returning to those controls.

A pause request does not interrupt a half-applied action. An open menu/help window holds AI at the same safe boundary but blocks gameplay inspection beneath it. Closing it releases that hold without cancelling a separately requested spectator pause. Fast resolution changes presentation speed, not the shared combat and resource rules.

### Sound settings and tutorial hints

Topic ID: `settings`

Options contains master volume, music enable/volume and tutorial-hint controls. These settings change presentation and guidance rather than unit stats or match balance.

Open Options from the appropriate menu, adjust the sliders/toggles and return with its Back control. Enable tutorial hints if you want contextual guidance for actions such as drawing, creating armies, attaching upgrades or production.

Already completed hints are tracked so they are not repeatedly presented as new actions. Turning sound or music off does not skip a gameplay decision. The in-match menu remains responsible for blocking map input while its settings window is open.

### Army radio reports

Topic ID: `radio`

Army radio messages provide brief contextual reports about observed situations, enemy strength and weather. They help draw attention to a situation without replacing the actual map and card information.

Read a report, then inspect the relevant army, route or hex before issuing orders. Treat comments about weak or strong enemies as an assessment of the available observation, and check a storm report against the current footprint.

Radio chatter does not spend AP, change the battle result or reveal every hidden enemy. Cosmetic messages can be withheld while a decision or battle window is active. Use actual unit details and legal-action feedback for precise costs and restrictions.

### Leaving a match and returning to the campaign

Topic ID: `exit`

The match menu returns you to the appropriate destination: Campaign for a campaign operation or Main Menu for a standalone match.

If you need to preserve the exact current state, create a legal save before leaving. Open the gear menu and choose the displayed destination. Acknowledge any result/reward flow when the game has already finished.

Leaving is not a substitute for saving or a victory result. Loading another slot also replaces unsaved progress. Campaign region capture is based on the recorded completed operation result; merely returning to the campaign does not award the target region.

## Ability reference

### Abilities

Topic ID: `all-abilities`

All abilities are collected on this page. Scroll down to read each effect, how to use it and its restrictions. Names match the ability reference; a listed ability is available to your army only when an actual card or attachment grants it.

#### Critical Damage

Damage of a successful attack x2.

Use the carrier against a target where a positive token difference is likely; improving the chance to hit is still necessary.

The current multiplier is 2. It applies before matching flat damage bonuses and before Ceramic Armor. A tied or losing attack has no positive base damage to double.

#### Ceramic Armor

-1 to all damage received.

Use this carrier to absorb small hits. Check its own HP and other defensive effects before relying on the reduction.

The current reduction is 1 after the normal offensive modifiers. It can reduce a small hit to zero. It does not increase Defense tokens or guarantee that a larger attack is harmless.

#### Berserk

Each hit taken in combat: +1 ATK, -1 DEF until the battle ends.

Keep the carrier alive through repeated hits to increase its attack potential, but watch the accompanying defense loss.

Each damaging hit adds 1 Attack and removes 1 Defense for this battle, subject to the defense floor. These temporary changes are reverted at encounter end; wounds remain.

#### Rapid Reaction

Deploys for 0 AP and moves in an army for 0 AP.

Deploy the carrier when you need to conserve AP, or include it in a mobile army to reduce that army's activation cost.

Its ground deployment and movement-activation AP contribution is 0. This does not waive resource prices, movement-point costs or another unit's activation cost. Check separate aircraft launch requirements where applicable.

#### Shock Attack

A damaging hit makes the target lose its pending turn this round and cancels its counterattack.

Use a damaging hit on an enemy whose pending action or close counterattack would be dangerous.

The target loses a still-pending turn for the current tactical round. Shock does not undo an action already taken and does not skip a future round. Counterattack suppression requires a damaging hit.

#### Hyperkinetic

+2 DMG vs Armored targets when the attack deals damage.

Inspect the target's Armored tag and select a suitable attack. The ability is useful after you have secured a positive base hit.

The current bonus is +2 damage against Armored targets. It does not create a hit from a zero or negative success difference. Other matching offensive modifiers and final armor reduction still apply.

#### Pyrokinetic

+2 DMG vs Bio targets when the attack deals damage.

Inspect the target's Bio tag before using the carrier as a damage specialist.

The current bonus is +2 damage against Bio targets after a positive base hit. It is not a universal bonus against every Infantry or Hero card; the actual Bio tag matters.

#### Splash

On a damaging hit, up to 2 random orthogonally adjacent units, allies included, take half the damage.

Inspect the four orthogonal neighbors of the primary target before attacking; friendly units can be among them.

A damaging primary hit can produce up to 2 random secondary hits. They use half the primary damage, rounded down, with the secondary victim's armor reduction. There is no separate attack roll for each collateral hit, and zero half-damage has no effect.

#### Scorcher

On a damaging hit, one random orthogonally adjacent unit takes half the damage if it is Bio.

Use the carrier when the primary target has nearby Bio units, while checking for friendly Bio neighbors.

One random orthogonally adjacent unit is selected; damage occurs only if that selected unit is Bio. Half primary damage is rounded down and the secondary victim's protection applies. This does not guarantee choosing a Bio neighbor just because one exists.

#### Raise The Rots

At battle start summons 2 extra units on its side while the grid has room. They fight for this battle only.

Use the carrier to add temporary bodies when the encounter starts and your side has available grid space.

The current summon count is 2 per carrier. These summoned units exist for the battle only and are removed when the encounter or their temporary participation ends. They do not become permanent Collection or hand cards.

#### Heal

Restores 1 HP to a random wounded Bio ally in its army: unit at the start of its combat turn, hero at the start of each round.

Keep wounded Bio allies in the same army. In combat, let the carrier reach its healing trigger rather than expecting a strategic repair button.

The current heal amount is 1 HP to a random eligible wounded Bio ally. A normal carrier triggers at the start of its combat turn; a hero carrier triggers at round start. Healing does not resurrect dead cards or replace paid full repair.

#### Transport

Raises the slowest Infantry in its army to the carrier's Move.

Combine the carrier with a slower eligible Infantry unit in the same ground army and inspect the resulting movement.

Each carrier supports one non-hero, non-aircraft, non-prisoner, non-summoned Infantry passenger. Existing valid pairs are retained where possible. Other slow members can still limit the army. It is a movement benefit, not an additional roster-capacity slot.

#### Repair

Enables paid repair for all own armies on its hex.

Move the ground mechanic to the same hex as your wounded force, then use the wounded unit's paid repair control in the Army Viewer.

The source must be a living friendly non-aircraft ground member outside prison/aircraft containers. It serves friendly armies on that hex without granting deployment rights. Each repair still costs 1 AP plus the target's stored resource price and restores its HP to full.

#### Disruption

On a hostile Base or Citadel hex, or arriving with an attacking army: cancels its building DEF bonus for the encounter. Does not reveal the carrier.

Bring a living eligible ground carrier into a hostile Base/Citadel encounter, or position it on the hostile building's hex.

The effect cancels the defender's building defense contribution, not its intrinsic unit Defense or ordinary terrain bonus. Presence is enough; reading this effect does not itself reveal a hidden carrier. Sandstorms can already remove the same building contribution.

#### Airfield

Mobile airfield: carries aircraft up to its Airfield Capacity.

Inspect the carrier's Airfield Capacity, store aircraft in its airfield container and form a wing when you need to launch.

Stored aircraft and the carrier's ready wing share physical capacity. The aircraft are separate from the ground carrier's army roster. A wing becomes independent on actual take-off; free landing capacity and fuel still matter.

#### Single Use

Equipment: consumed after an attack. Unit: expended after its attack.

Check whether the ability is innate on the Unit or supplied by an attachment before committing the attack.

A Single Use attachment is consumed after the launched attack. An innate disposable non-hero Unit is expended after its attack. A multi-target explosion is one launched attack, so consumption occurs at completion rather than once per victim. A miss does not make the spent attack free.

#### Explosion 3

Replaces the attack with 10-die challenges against up to 3 random distinct units or structures on the target hex, allies included.

Use the carrier only after inspecting all occupants and structures on the target hex; up to 3 distinct targets are selected randomly, including allies.

Every selected target faces a separate 10-token attack. The size number is the maximum target count, not damage or dice. The launcher is excluded. Structures can be damaged, and defensive abilities/Fate apply through the supported challenge. Single Use is a separate consumption rule.

#### Explosion 5

Replaces the attack with 10-die challenges against up to 5 random distinct units or structures on the target hex, allies included.

Use the carrier only after inspecting all occupants and structures on the target hex; up to 5 distinct targets are selected randomly, including allies.

Every selected target faces a separate 10-token attack. The size number is the maximum target count, not damage or dice. The launcher is excluded. Structures can be damaged, and defensive abilities/Fate apply through the supported challenge. Single Use is a separate consumption rule.

#### Explosion 10

Replaces the attack with 10-die challenges against up to 10 random distinct units or structures on the target hex, allies included.

Use the carrier only after inspecting all occupants and structures on the target hex; up to 10 distinct targets are selected randomly, including allies.

Every selected target faces a separate 10-token attack. The size number is the maximum target count, not damage or dice. The launcher is excluded. Structures can be damaged, and defensive abilities/Fate apply through the supported challenge. Single Use is a separate consumption rule.

#### Ionic

+2 DMG vs Mechanical targets when the attack deals damage.

Use the carrier against targets tagged Mechanical, and check whether the target has additional defensive abilities.

The current bonus is +2 damage after a positive base hit against Mechanical targets. A Mechanical classification is independent of the unit's chassis or faction name.

#### Bodyguard

Once per round takes a hit aimed at an adjacent ally instead. Heroes never guard.

Arrange the carrier orthogonally beside the allied card you want to protect. A diagonal neighbor does not qualify.

One guard intercepts at most once per tactical round. A hero cannot serve as a Bodyguard. If several guards qualify, the eligible one with the most current HP is used. Damage is recalculated against the guard's own abilities; counterattacks are not intercepted.

#### Counterattack

After surviving an attack from an adjacent cell (diagonals included), strikes the attacker back. Cancelled by Shock Attack.

Use the carrier to make close attacks costly for the opponent. Preserve enough HP to survive the primary attack.

The attacker must be within one cell, diagonals included. A damaging Shock Attack, a retreating army, a hero carrier or death prevents the response. The response is its own challenge but cannot start another counterattack or Bodyguard chain.

#### Drill

Each combat round: +1 ATK for that round to the own ground unit with the highest ATK.

Keep the carrier present at the beginning of a tactical round to strengthen a leading ground combatant.

Each carrier grants the current +1 Attack round bonus to an eligible own ground combatant with the highest Attack. Multiple carriers select different recipients when available. The bonus lasts for that round, not permanently.

#### r1s0

Army vision +1 hex. Cannot detect stealth units.

Place this carrier in the army that needs one additional hex of vision. Approach suspected hidden units to bring its spotting reach into play.

Its adjacent spotting pool is 0: it cannot detect hidden units on a neighboring hex merely by seeing that hex. A minimal same-hex observation can still exist under the ordinary source rule.

#### r1s4

Army vision +1 hex. 4 dice to detect stealth units on its hex and adjacent hexes.

Place this carrier in the army that needs one additional hex of vision. Approach suspected hidden units to bring its spotting reach into play.

Its adjacent spotting pool is 4. The best applicable source is used rather than adding all carriers together. Spotting needs strictly more successes than the hidden unit's concealment; a tie leaves it hidden.

#### r1s5

Army vision +1 hex. 5 dice to detect stealth units on its hex and adjacent hexes.

Place this carrier in the army that needs one additional hex of vision. Approach suspected hidden units to bring its spotting reach into play.

Its adjacent spotting pool is 5. The best applicable source is used rather than adding all carriers together. Spotting needs strictly more successes than the hidden unit's concealment; a tie leaves it hidden.

#### r1s6

Army vision +1 hex. 6 dice to detect stealth units on its hex and adjacent hexes.

Place this carrier in the army that needs one additional hex of vision. Approach suspected hidden units to bring its spotting reach into play.

Its adjacent spotting pool is 6. The best applicable source is used rather than adding all carriers together. Spotting needs strictly more successes than the hidden unit's concealment; a tie leaves it hidden.

#### Stealth 4

Can enter stealth for 1 AP: hides with 4 dice, +1 or +2 on difficult terrain.

Use the carrier's stealth action and pay 1 AP to hide; leave for 0 AP before taking offensive actions.

The base hide pool is 4, with a terrain bonus of move cost minus 1. Spotting must score strictly more successes to detect it. Leaving stealth uses the turn's toggle too, so you cannot immediately hide again. Availability of a tag in the reference does not guarantee that your selected deck contains a carrier.

#### AA

Intercepts enemy aircraft within the card's anti-air radius.

Maintain sight of likely enemy flight hexes and keep the carrier revealed. Choose Attack or Skip when a legal reaction is offered.

Use the card's actual anti-air radius. A unit fires once until its owner's next turn refresh; Attack or Skip prevents another prompt for the same wing in that cycle. A hidden AA carrier cannot fire just because its radius covers the target.

#### Ap Bonus

Adds 2 AP to the owner's round allocation for each eligible in-play carrier, on top of the initiative-rank allocation.

Deploy or build the carrier and keep it in play through the new round's AP allocation.

Each in-play source currently adds 2 AP, and multiple eligible sources stack. A card in hand or a captive in prison is not an ordinary Ap Bonus source. The grant is added to initiative AP; it is not a free AP gain each time you reopen a panel.

#### Trade

Unlocks resource exchange while in play. Starts at 2:1; each exchange increases the price by 1. An idle turn lowers it by 1, minimum 2:1. Costs no AP.

Keep a living carrier in play, open the Trade window during your confirmed turn and inspect the current exchange rate.

The player starts at a minimum 2:1 rate; each exchange worsens it by 1, and a turn without trades improves it by 1 down to the minimum. Trade costs no AP. Additional carriers unlock access but do not reset the shared rate.

#### Tactical Intelligence

Unlocks opponent intelligence while in play. Ground armies closer to an enemy citadel reveal more fields; fields change each turn.

Open opponent intelligence and move a qualifying field ground army nearer the opponent's original citadel to increase the visible report.

Distances 3, 2, 1 and 0 reveal 4, 8, 12 and 16 report fields. Garrisons and aircraft do not provide this proximity contribution. Report fields change with your personal turn and do not replace live fog/stealth rules.

#### Barracks

Accepts units deployed from hand and holds a garrison. Retreating armies head to the nearest own Barracks.

Use an owned eligible building to deploy the ground cards that require Barracks, and inspect its stationary garrison.

Ground deployment must satisfy the card's actual required building ability and final capacity. Retreat movement prefers a direction toward the nearest eligible own Barracks, not an instant teleport there. Aircraft require their separate airfield capacity.

#### Research

Facility: enables Research for a hero with Researcher on this hex.

Bring a friendly Researcher hero to this owned facility's hex, open Research and select an available deck blueprint.

Research requires both the facility capability and a valid acting hero. It spends the attempt's AP/resources and can reveal that participating Researcher. It does not manufacture every possible card just because the building exists.

#### Researcher

Hero can run Research at a Research Facility on its hex.

Move this hero onto the same hex as your Research Facility and choose a blueprint in the Research window.

The hero must be yours and not a prisoner. Choosing Research reveals that hero if hidden. The attempt rolls the hero's maximum Fate stat and spends a local Fate budget; its live combat Fate is unchanged.

#### Production

Facility: enables Production for a hero with Assembler on this hex.

Bring a friendly Assembler hero to the owned facility's hex, open Production and select an available Equipment blueprint.

A valid Facility, hero, free hand slot, unlocked blueprint and affordable attempt cost are all required. The facility does not automatically produce an item every round. A failed roll still spends the attempt cost.

#### Assembler

Hero can run Production at a Production Facility on its hex.

Position this hero on an owned Production Facility and use the Production window to begin an Equipment attempt.

The hero must be yours and not a prisoner. Production does not itself reveal a hidden acting hero. The same fixed-success difficulty test and local Fate-spending rules apply as in Research.

#### Collect Human

Collects Human from the carrier's hex under the collection rules. This uses available hex yield rather than creating an unlimited fixed resource bonus.

Place a matching collector on a visited hex with available Human yield and protect the location. Inspect the marginal income before constructing another collector or upgrading it.

Collection is resolved at the new-round income step. A Facility contributes 1 plus its own upgrade level and the hex yield caps the harvest. Buildings collect first; eligible units with the matching Collect tag then take up to 1 each from the remainder, only when their owner has no combat-capable enemy on the hex. This is not the unconditional Produce bonus.

#### Collect Energy

Collects Energy from the carrier's hex under the collection rules. This uses available hex yield rather than creating an unlimited fixed resource bonus.

Place a matching collector on a visited hex with available Energy yield and protect the location. Inspect the marginal income before constructing another collector or upgrading it.

Collection is resolved at the new-round income step. A Facility contributes 1 plus its own upgrade level and the hex yield caps the harvest. Buildings collect first; eligible units with the matching Collect tag then take up to 1 each from the remainder, only when their owner has no combat-capable enemy on the hex. This is not the unconditional Produce bonus.

#### Collect Materials

Collects Materials from the carrier's hex under the collection rules. This uses available hex yield rather than creating an unlimited fixed resource bonus.

Place a matching collector on a visited hex with available Materials yield and protect the location. Inspect the marginal income before constructing another collector or upgrading it.

Collection is resolved at the new-round income step. A Facility contributes 1 plus its own upgrade level and the hex yield caps the harvest. Buildings collect first; eligible units with the matching Collect tag then take up to 1 each from the remainder, only when their owner has no combat-capable enemy on the hex. This is not the unconditional Produce bonus.

#### Collect Tech

Collects Tech from the carrier's hex under the collection rules. This uses available hex yield rather than creating an unlimited fixed resource bonus.

Place a matching collector on a visited hex with available Tech yield and protect the location. Inspect the marginal income before constructing another collector or upgrading it.

Collection is resolved at the new-round income step. A Facility contributes 1 plus its own upgrade level and the hex yield caps the harvest. Buildings collect first; eligible units with the matching Collect tag then take up to 1 each from the remainder, only when their owner has no combat-capable enemy on the hex. This is not the unconditional Produce bonus.

#### Regeneration

Restores 1 HP (a Base: 1 structure point) at the end of the owner's turn.

Keep a surviving wounded carrier in play until the end of its owner's turn.

The current restoration is 1 HP for a living unit or 1 structure point for an eligible Base, never above maximum. Prisoners are excluded. This is an automatic end-turn effect, not a full repair action, and does not bring back a destroyed carrier.

#### Produce Human

Grants 1 Human at the round's income step while the carrier is in play. This is a fixed resource bonus, separate from harvesting a deposit.

Deploy or build the carrier and keep it in play to receive its Human contribution at the round's income step.

Each eligible in-play source grants 1 Human, separate from the hex's collection limit. Multiple sources stack. A card in hand or a prisoner does not provide this ordinary source bonus. It is not awarded again each time the player opens or confirms a turn panel.

#### Produce Energy

Grants 1 Energy at the round's income step while the carrier is in play. This is a fixed resource bonus, separate from harvesting a deposit.

Deploy or build the carrier and keep it in play to receive its Energy contribution at the round's income step.

Each eligible in-play source grants 1 Energy, separate from the hex's collection limit. Multiple sources stack. A card in hand or a prisoner does not provide this ordinary source bonus. It is not awarded again each time the player opens or confirms a turn panel.

#### Produce Materials

Grants 1 Materials at the round's income step while the carrier is in play. This is a fixed resource bonus, separate from harvesting a deposit.

Deploy or build the carrier and keep it in play to receive its Materials contribution at the round's income step.

Each eligible in-play source grants 1 Materials, separate from the hex's collection limit. Multiple sources stack. A card in hand or a prisoner does not provide this ordinary source bonus. It is not awarded again each time the player opens or confirms a turn panel.

#### Produce Tech

Grants 1 Tech at the round's income step while the carrier is in play. This is a fixed resource bonus, separate from harvesting a deposit.

Deploy or build the carrier and keep it in play to receive its Tech contribution at the round's income step.

Each eligible in-play source grants 1 Tech, separate from the hex's collection limit. Multiple sources stack. A card in hand or a prisoner does not provide this ordinary source bonus. It is not awarded again each time the player opens or confirms a turn panel.
