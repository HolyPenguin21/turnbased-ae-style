# AI aviation — technical specification for Claude

## Status and scope

Aviation mechanics are complete and manually verified. This task adds **AI policy only**. Do not change aviation rules, combat, fuel, AA, UI, or introduce a second movement/combat pipeline.

The existing shared APIs remain authoritative:

- `AviationActions` for launch and airfield interactions;
- `AviationRules` for air-army identity, movement cost, owned airfields and capacity;
- `HexSelectionController.IssueMoveOrder` for all movement;
- `AviationCombatPresenter` for AA reactions and air strikes;
- `AviationTurnLifecycle` for end-turn landing/fuel resolution.

The implementation must use those APIs exactly as a human player does. It may add read-only AI helpers, but must not bypass AP, energy, capacity, movement, AA, combat, fog, or fuel checks.

## Architecture update

Extend the AI task catalog with two persistent tasks:

| Category | Task kind | Purpose |
|---|---|---|
| Aggression | `AirStrike` | Launch already-deployed aircraft, strike a known enemy target, and land safely. |
| Reconnaissance | `AirRecon` | When aggression has no actionable target and the economy is not resource-starved, fly toward the enemy to reveal information and land safely. |

Both tasks own an existing mobile `IsAirArmy` only. Aviation may never be borrowed by Raid, Defence, Economy, GarrisonReorg, or ground Recon tasks, and an air task may never recruit ground units or heroes.

### Card ownership boundary

**Only Management chooses and plays aviation cards from hand.** Neither `AirStrike` nor `AirRecon` reads the hand, selects a card, deploys a card, or decides where a new aircraft card is placed.

Management needs one aviation-aware placement branch: an aviation card may be placed only at an owned airfield-capable hex with capacity. It selects among every owned eligible airfield, including the citadel and all later Bases, via the same live availability/resource rules as other card placement. This branch only creates/stores the aircraft; it does not launch or assign an air task.

The new tasks begin only from aircraft already present at an airfield or from a valid existing mobile air army that ended its previous turn over an owned airfield.

## Shared safety invariant: complete sortie

A task must calculate a complete sortie before it launches or moves:

`start airfield -> action hex -> any owned airfield with free capacity`

“Any owned airfield” is deliberate. The route may start at the citadel and land at a Base, start at a Base and land at the citadel, or use any later owned Base. Do not hard-code a home citadel or return to the launch airfield.

The route uses aviation’s real one-MP-per-hex cost and current effective MP, and must re-check:

- air army still exists and is a valid air army;
- the start/landing building is still owned and airfield-capable;
- the landing destination has free capacity for the whole air army;
- launch AP and energy are affordable;
- the complete path is reachable in the same owner turn;
- normal map/path validity still holds.

If no complete safe sortie exists, do not launch. If a task has already launched and its plan becomes invalid before the next movement decision — target disappeared, landing base was captured/destroyed/full, path became impossible, or the army lost effective MP — it must prefer a newly reachable owned airfield. If none is reachable in the current turn, it must stop proposing voluntary aviation movement; never intentionally strand an aircraft to exploit endurance.

## Aggression task: AirStrike

### Candidate creation

`AirStrike` may consider a stored aircraft group at each owned airfield, or an existing air army currently over an owned airfield. It does not create or deploy cards. It evaluates known enemy armies/garrisons from `AiMapMemory`; the final shared aviation resolver still decides whether anything is actually present when the army enters the hex.

For each candidate, find a valid complete sortie to a target and a landing airfield. Rank candidates by:

1. a target whose estimated value/defence makes an air strike worthwhile;
2. expected target damage/value and number of ready aircraft;
3. lower known AA exposure along the route;
4. shorter total sortie distance;
5. lower AP/energy cost.

Known enemy air armies on ordinary hexes are valid according to the existing aviation rules. Stored aircraft in enemy airfields are never targets.

The task must not assume success, capture buildings, or use a separate combat estimator that disagrees with the existing challenge rules. AA and strike resolution remain entirely in `AviationCombatPresenter`.

### Execution and lifecycle

1. Launch via `AviationActions` only after the complete sortie is still feasible.
2. Move incrementally through the standard AI `MoveArmy` / `IssueMoveOrder` execution route.
3. Let every entered hex resolve normal AA and air-strike logic. AI AA already always attacks; do not add another AA decision path.
4. Re-evaluate target, aircraft roster, MP, and alternate landing airfields after every resolved step.
5. Once the attack is resolved, or the target no longer exists, fly to the selected/updated owned landing airfield.
6. Complete and remove the task only when the air army is on an owned airfield hex. End-turn landing/fuel remains owned by `AviationTurnLifecycle`, not this task.

If AA destroys the air army, normal empty-army cleanup applies and the task is removed.

### Arbitration

AirStrike is an Aggression candidate. It may run concurrently with ground `RaidWeakerArmy` tasks but the common arbiter still executes one decision per step.

Its ordinary travel/launch score should be above non-urgent economic/recon travel only when it has a worthwhile known target. It must remain below:

- an actual citadel/base emergency and Defence preemption;
- already committed mandatory resolution steps;
- a ground raid’s immediate combat/finish step where the existing priority ladder gives that step precedence.

Use named `AiConfig` constants; do not bury scoring literals in planners.

## Reconnaissance task: AirRecon

### Gating

AirRecon is a fallback, not a substitute for aggression. It is considered only when all conditions are true:

1. no actionable AirStrike candidate exists this step;
2. no higher-priority Aggression objective exists for the same arbitration state;
3. the player has free resources after current reservations and the proposed launch AP/energy is affordable;
4. a complete reconnaissance sortie can start and land at any owned airfield in the same turn;
5. AI memory has **not** observed an enemy army containing an AA-capable unit.

Condition 5 is global and conservative for v1: if the AI has seen enemy AA in any remembered enemy army, it does not propose AirRecon. Unknown AA is still normal fog risk; this gate only acts on information the AI actually has.

### Target and flight shape

AirRecon does not target a known enemy army for damage. It selects an unexplored or stale-information hex in the approximate direction of known enemy territory/citadel, constrained by a complete return route to any own airfield. It should favour forward information gain over lateral wandering, then shorter safe sortie distance.

The task launches, moves toward that recon hex using the shared movement pipeline, and turns toward the selected landing airfield early enough to finish the same turn there. If contact produces an ordinary air strike under the shared resolver, that is a consequence of movement, not a reason to reclassify the task into AirStrike mid-flight.

On new information, it may adjust the forward recon hex or landing base, but cannot extend the route past the safe-landing invariant. It completes on reaching an owned airfield hex; end-turn refuelling is still handled by `AviationTurnLifecycle`.

### Arbitration

AirRecon belongs to Reconnaissance and is deliberately lower than ordinary actionable aggression. It must not steal aircraft from AirStrike and must yield to Defence, committed task completion, and the existing higher-priority candidates already defined by the common arbiter.

## Required AI integration points

1. **Task model**
   - Add `AirStrike` and `AirRecon` to `AiTaskKind`.
   - Map them to Aggression and Reconnaissance in `AiTaskCatalog.CategoryOf`.
   - Store the mobile air army, action/target hex, selected landing hex, and enough state to distinguish outbound versus return flight. Do not persist stale capacity or resource assumptions.

2. **Decisions and dispatch**
   - Add dedicated action kinds/factories for launch and any task-specific state transition only where existing generic `MoveArmy` cannot express the action.
   - Keep normal flight steps as the existing shared `MoveArmy` route.
   - Wire execution through the existing common action APIs; do not manipulate `ArmyData.Members` directly.

3. **Management placement**
   - Update Management’s card-placement policy so aviation cards are selected and placed only there.
   - Evaluate all owned eligible airfields (citadel + every Base) and capacity, not only the starting garrison.
   - Management stops at successful storage; assignment, launch, and movement remain the two new tasks’ responsibility.

4. **AI memory and target reads**
   - Reuse `AiMapMemory` only; do not read hidden board state.
   - Add the minimum memory/read helper necessary to identify observed AA-capable enemy armies for the AirRecon gate.
   - Treat stale/removed sightings exactly as the existing memory lifecycle does.

5. **Planning helpers**
   - Add a small shared AI aviation helper if it prevents duplicate route/capacity logic between AirStrike and AirRecon.
   - The helper is read-only and computes launch candidates, route cost, landing eligibility, and safe fallback destinations from live shared rules.
   - Do not create an AI-specific flight, fuel, AA, or combat system.

6. **Tuning and diagnostics**
   - Add explicit `AiConfig` scores/thresholds for AirStrike, AirRecon, AA avoidance, and the resource-free gate.
   - Add concise `AiDebugLog` messages for: candidate rejection (especially no landing route), launch, target choice, landing-base choice, AA-based AirRecon suppression, route replan, and cancellation.
   - Keep per-step logs throttled enough to remain useful.

## Non-goals

- No AI card selection/deployment inside Aggression or Reconnaissance.
- No intentional endurance/emergency-fuel flights in v1.
- No “return only to citadel” rule.
- No omniscient avoidance of hidden AA.
- No aircraft participation in ground Raid/Defence composition.
- No change to human aviation UX, air combat, AA choices, fuel numbers, or landing mechanics.
- No automated test framework.

## Verification checklist

After Claude implements the task:

1. Run `dotnet build Assembly-CSharp.csproj` if the Unity-generated project file is available.
2. In Play Mode with a citadel and one later Base, verify Management places an aviation card only into a legal airfield and respects capacity.
3. Verify AirStrike can launch from the citadel, strike a known target, and land at the Base; then the reverse direction.
4. Verify a launch is rejected when neither airfield provides a full same-turn sortie/landing path.
5. Verify a lost/full target landing base triggers a replan to the other owned airfield; no voluntary stranded flight occurs.
6. Verify known AA reacts through the existing shared presenter and destroys/cancels safely when appropriate.
7. Verify AirRecon runs only with no actionable aggression target and free resources, flies toward enemy direction, and returns to either owned airfield.
8. Verify AirRecon is not proposed after AI has observed enemy AA in an enemy army.
9. Verify Defence emergencies and higher-priority committed work win arbiter decisions over aviation.
10. Review `AiDebug.log` for launch/route/landing/cancellation reasons and absence of repeated no-progress loops.
