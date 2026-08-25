# Aviation implementation plan

> Implements the approved design in `docs/superpowers/specs/2026-08-25-aviation-design.md`.
> This feature deliberately has no AI policy.  It exposes player-agnostic actions and queries so
> the next AI task uses the same movement, launch, landing, AA, and combat rules as a human.

## Constraints

* Work on `master`; preserve the six already-modified files that correspond to previously
  published fixes.
* No test project or automated test files: this Unity project verifies by build and Play Mode.
* Never add a second movement/combat pipeline.  `HexSelectionController.IssueMoveOrder` remains
  the shared entry point for human and future AI moves.
* A unit is aviation only when its card marks it as such.  Aviation may not share an army with
  heroes or ground units.

## 1. Add aviation data and small, explicit shared queries

**Files**

* Modify `Assets/Scripts/Cards/CardDefinition.cs`
* Modify `Assets/Scripts/Units/UnitData.cs`
* Modify `Assets/Scripts/Map/BuildingData.cs`
* Modify `Assets/Scripts/Map/ArmyData.cs`
* Modify `Assets/Scripts/Cards/UnitAbilities.cs` and `Assets/Scripts/Cards/UnitAbilityCatalog.cs`
* Add `Assets/Scripts/Aviation/AviationRules.cs`
* Add `Assets/Scripts/Aviation/AntiAirRules.cs`

Add serialized card fields, with neutral defaults for all existing cards:

```csharp
[Header("Aviation")]
public bool isAviation;
public int launchEnergyCost;
public int turnsWithoutRefuel;
public int airfieldCapacity;
```

`SpawnUnit` copies the unit fields into `UnitData`.  `SpawnBuilding` and starting-citadel setup
copy `airfieldCapacity` into `BuildingData`.  `airfieldCapacity` is read only on an owned,
Barracks-capable building; therefore future cards can provide an airfield merely by carrying the
Barracks ability and a positive capacity.

Add runtime-only state to `UnitData`:

```csharp
public bool IsAviation;
public int LaunchEnergyCost;
public int TurnsWithoutRefuel;
public int ConsecutiveUnlandedEnds;
public bool HasEmergencyFlightPenalty;
public bool HasAirAttackedThisTurn;
```

Add `ArmyData.IsAirfield` and `ArmyData.IsAirArmy`.  An airfield is an immobile owned container;
an air army is a mobile all-aviation formation.  Both are still `ArmyData`, so registry,
selection, repair UI, visual memory, and cleanup do not gain competing data models.

`AviationRules` is pure and is the only source for:

* `IsAviation(UnitData)`, `IsAirArmy(ArmyData)`, `IsAirfield(ArmyData)`;
* `IsOwnedAirfieldAt(owner, hex)`, `FindAirfieldAt(owner, hex)`, and free capacity;
* member compatibility and air-army/airfield capacity validation;
* effective unit/army MP (normal MP halved with `Mathf.FloorToInt` only after emergency);
* `MovementCost(army, terrainCost)` — `1` for air, existing terrain cost otherwise;
* reset-on-landing, end-turn crash/penalty outcomes, and deterministic slot-order landing.

Add ability `AA` with a magnitude in `UnitAbilityCatalog` (`aaRadius`, currently configured as
1 or 2 per unit/card capability).  Use a parser/helper such as `AntiAirRules.TryGetRadius`, so
future `AA1`/`AA2` tags are valid and UI/editor validation can remain one shared rule.

## 2. Create and maintain airfield containers through common actions

**Files**

* Modify `Assets/Scripts/Map/ArmyActions.cs`
* Modify `Assets/Scripts/Map/HexSelectionController.Factory.cs`
* Modify `Assets/Scripts/Setup/CitadelSetupController.cs`
* Modify `Assets/Scripts/Map/BuildingRegistry.cs`
* Modify `Assets/Scripts/Cards/CardHandUI.cs`
* Modify `Assets/Scripts/UI/ArmyViewerModalUI.cs`
* Add `Assets/Scripts/Aviation/AviationActions.cs`

`AviationActions` owns transactional actions, returning a result plus `failReason` just like
`ArmyActions`:

* `EnsureAirfield(building, hexSelection)` creates exactly one `IsAirfield` container only when
  an aviation card is deployed or lands there; it is not pre-created at every Barracks.
* `DeployAviationFromCard` permits a card from hand only to its owner's airfield-capable hex,
  checks airfield capacity, then spends normal card AP/resources and adds the aircraft to the
  airfield.
* `Launch` transfers selected compatible cards from an airfield to a new or existing air army,
  charges each aircraft's own AP plus its `LaunchEnergyCost` once for this launch, and leaves an
  airfield never over capacity.
* `TryLandAtEndTurn` transfers in current `Members` list order into the owned airfield until its
  card-configured capacity is full.  It resets only landed aircraft's endurance state.
* `ReturnStoredAircraftToDeck` removes stored cards, returns their originating `CardData` to the
  proper owner deck/discard destination already used for destroyed/returned cards, and removes
  the now-empty container.

Make `ArmyActions.TransferMember` reject a transfer that would mix air and non-air members,
reject prison/garrison/airfield movement targets where appropriate, and delegate compatibility
checks to `AviationRules`.  Normal army creation remains unchanged; launching is the only way a
mobile `IsAirArmy` is formed.  Existing repair methods continue to work because aircraft remain
ordinary `UnitData` inside an `ArmyData` at an owned Barracks hex.

Subscribe to `BuildingRegistry.VisualStateChanged` / `BuildingDestroyed` from the aviation
service.  On capture or destruction, empty the previous owner's airfield on that hex into their
deck; do not touch an airborne army above that hex.

## 3. Make the existing movement pipeline wait for per-step aviation resolution

**Files**

* Modify `Assets/Scripts/Map/ArmyController.cs`
* Modify `Assets/Scripts/Map/HexSelectionController.Movement.cs`
* Modify `Assets/Scripts/Combat/BattleInitiator.cs`
* Add `Assets/Scripts/Aviation/AviationStepResolver.cs`

Extend `ArmyController.MoveAlong` with one optional coroutine callback after each completed
step.  The routine yields it before deciding whether to continue, so an event, AA decision, or
air challenge pauses the exact same animated army instead of starting nested movement.  Existing
ground callers pass no resolver and keep their behavior.

Replace direct terrain-cost calculations in path preview and actual move validation with
`AviationRules.MovementCost`.  Air still consumes one MP per entered hex and uses the minimum
effective member MP.  Pathfinding keeps map adjacency and fog/event step behavior but does not
apply terrain cost or stop at ground-enemy contact for an `IsAirArmy`.

`AviationStepResolver` is the map/UI adapter, deliberately separate from pure rules:

1. after every entered hex, collect and resolve AA reactions;
2. if the air army survives, detect actual enemy content now present on that hex;
3. run sequential air challenges when a target exists;
4. stop only if destroyed/out of MP; otherwise permit the next route hex.

Ground `IssueMoveOrder` calls the same resolver solely for AA opportunity checks when a ground
army carrying AA moves while seeing an enemy air army in its radius.  The AA shot is then consumed
normally, so it cannot fire again at the following owner-turn entry.  `BattleInitiator` explicitly
excludes `IsAirArmy` / `IsAirfield` from ordinary strategic ground contact and battle-screen
participants.

## 4. Resolve AA and air strikes through the existing challenge popup

**Files**

* Add `Assets/Scripts/Aviation/AntiAirState.cs`
* Add `Assets/Scripts/Aviation/AviationCombatPresenter.cs`
* Modify `Assets/Scripts/UI/BattleAttackPopupUI.cs`
* Modify `Assets/Scripts/Turns/GameTurnController.cs`

`AntiAirState` is a turn-scoped registry keyed by `(aaUnit, airArmy.Id)`.  It records that an
air army has entered that AA unit's radius during its owner's current turn even when the player
chooses Skip.  A fired AA unit cannot fire again until its owner's next turn; skipped AA remains
ready for another air army.  `ResetForOwner` runs from `ReplenishMoveForOwner`.

`AntiAirRules.CollectEntryReactions` returns a stable ordered list: hex distance, then army Id,
then member slot index.  It does not consult human FOW, so a hidden AA correctly fires before an
air strike when the owner can only see its own AA hex.  The presenter shows each reaction in
order; human gets Attack/Skip, AI always attacks.

`AviationCombatPresenter` queues `BattleAttackPopupUI.Begin` calls and resumes the movement
coroutine only from the existing resolve callback.  It provides two challenge forms:

* **AA:** attacker dice pool is `AA unit Attack * 2`; target is a random surviving aircraft;
  it consumes the AA shot only after Attack, not Skip.
* **Air strike:** each surviving aircraft that has not attacked this turn attacks once, in army
  slot order; each picks a random eligible unit from all enemy ground armies/garrison on that
  hex.  Heroes defend with `FateMax`.  A targetless remembered-FOW hex produces no popup.
  Against an unlanded enemy air army, choose a random aircraft; no dice doubling.

Add optional explicit attacker/defender dice-pool overrides to `BattleAttackPopupUI.Begin` rather
than modifying `UnitData.Attack` temporarily.  That keeps normal ground combat unchanged and
makes AA's double dice a local, auditable input.  Air strike never launches `BattleScreenUI`,
never captures an empty building, and never commits the attacker to a ground battle.

At every owner turn start, reset `HasAirAttackedThisTurn` for that owner's aircraft and AA
availability.  Immediately before advancing to the next player, process each current player's
air armies: attempt landing only if they end on their own airfield hex, then apply endurance per
individual unlanded aircraft: after more unlanded ends than `TurnsWithoutRefuel`, first apply
50% current-HP damage and set emergency; at the next such end destroy it.  Landing restores the
following turn's normal MP but never HP.

## 5. Integrate rendering, selection, modal ordering, FOW, and cleanup

**Files**

* Modify `Assets/Scripts/Cards/FactionCardCatalog.cs` and each faction catalog asset
* Modify `Assets/Scripts/Map/HexSelectionController.cs`
* Modify `Assets/Scripts/Map/HexObjectLayout.cs` / marker creation code
* Modify `Assets/Scripts/Map/HumanVisualMemory.cs`
* Modify `Assets/Scripts/UI/ArmyViewerModalUI.cs` and its row/card helpers
* Modify relevant prefabs/scenes only to wire the distinct air-army icon and AA choice UI

Add an optional faction `airArmyIcon`, falling back to the existing army icon until assets are
assigned.  Air armies get this icon and are centered according to normal on-hex stack layout;
airfields and prisons never get an independent map marker.  Keep existing FOW-centering behavior
when the air army/building is hidden.

Selection and modal entry use a single ordered container list:

1. Prison
2. Airfield
3. Garrison
4. Mobile ground/air armies

Show the first eight entries directly; reveal navigation arrows only when a ninth exists.  The
airfield view is movable only through aviation launch actions.  Snapshot `IsAirfield`/`IsAirArmy`
state in human visual memory so last-seen markers and modal labels remain correct.

On unit death or crash, remove empty mobile air armies and their marker using the same
`DeleteArmyIfEmptied` path.  Do not delete an empty airfield merely because it is empty if it is
still a valid owned airfield created for future deployment; delete it when the building is lost.

## 6. Manual verification and integration commits

Build is unavailable in this environment if `dotnet`/the project file are absent, so do not
claim a successful build without actual output.  Before each commit inspect the diff and avoid
the pre-existing six modified files unless the aviation change must touch them.

In Unity Play Mode verify:

1. an aircraft can only be played into an owned Barracks hex; capacity prevents overflow;
2. launch charges every aircraft AP + energy once, produces an air icon, permits terrain-agnostic
   one-MP steps, and rejects ground/hero mixing;
3. crossing a Barracks does not land; turn-end landing is slot ordered; excess aircraft remain
   airborne and receive the correct fuel outcome;
4. plane 0 / helicopter 1 endurance timelines, 50% current-HP damage, half MP rounded down,
   second overdue destruction, and repair after landing;
5. air strike runs one challenge per eligible aircraft, then permits continued movement;
6. AA range, ordered multiple prompts, skip/re-entry suppression, AI attack, doubled AA dice,
   air-to-air non-doubled attack, and ground-AA movement opportunity;
7. captured/destroyed base returns only stored aircraft cards; airborne forces remain;
8. FOW remembered target behavior, hidden-AA-before-strike behavior, modal order/eight-entry
   navigation, icons, and no normal BattleScreen or building capture from an air strike.

Commit in reviewable layers: data/rules; containers/UI/deployment; movement/combat/AA; visuals and
manual verification fixes.  Each commit includes the repository's required co-author trailer.
