# stealth-sim

Standalone acceptance harness for the **parameterized Recce + individual Stealth**
mechanic. Runs the 20 spec scenarios against the real game logic and prints `PASS`/`FAIL`
(non‑zero exit on any failure).

```sh
dotnet run --project Tools/stealth-sim/stealth-sim.csproj
```

CLAUDE.md's "no test framework" rule stands for the project at large — this one program is
the project owner's sanctioned exception for verifying this feature.

## How it works

`stealth-sim.csproj` `ProjectReference`s the Unity‑generated `Assembly-CSharp.csproj`
(gitignored, regenerated from the Editor — see CLAUDE.md), so it always exercises the
current game code, never a stale copy. If that file doesn't exist yet, open the project in
Unity once (**Assets → Open C# Project**) to generate it.

Only the pure model layer is touched — `UnitAbilities`, `AbilityParams`, `StealthSystem`,
`VisionSystem`, `ArmyRegistry`, `BuildingRegistry`, `BattleInitiator`,
`AviationCombatPresenter.FindAirStrikeTargetsAt`, `AiMapMemory`, `ChallengeResolver`. No
`MonoBehaviour`, coroutine or scene code runs.

Determinism: `StealthSystem.ChallengeRoller` (a seam over `ChallengeResolver.Resolve`),
`CompletedTurnsProvider` and `TerrainMoveCostProvider` are pointed at in‑harness stubs so
each scenario pins exact dice / turn‑serial / terrain values.

## Scenario coverage (spec §10)

| # | Scenario |
|---|----------|
| 1 | No legacy `Recce`: no const, not in `UnitAbilities.All`, no `recceRadius/recceStrength`, no `ArmyData.HasRecce` |
| 2 | `r1s0` raises vision radius to 1 but its 0 spot pool can't detect a Stealth4 unit one hex away |
| 3 | `r1s4/r1s5/r1s6` bring spot pools 4/5/6 |
| 4 | An ordinary source has 1 die in its own hex only, 0 adjacent |
| 5 | Stealth4 hide dice on move‑cost 1/2/3 hexes = 4/5/6 |
| 6 | Equal successes ⇒ no detection; strictly more spot successes ⇒ detection |
| 7 | Two Recce sources ⇒ **max** spot pool, never the sum |
| 8 | A co‑located ordinary enemy gets 1 die only — no automatic reveal of a hidden neighbour |
| 9 | Inspecting / menu‑style reads (`IsHiddenFrom`, `SpotPoolAgainst`, `TargetableMembersFor`, `FindEnemyAt`) roll no challenge |
| 10 | `RunChecksForArrival` / `RunChecksForNewVisionSource` / `RunChecksAfterHiddenUnitAction` each roll exactly one challenge per (unit, observer) pair even with several observer sources |
| 11 | A fully‑hidden army is not a contact target, can't initiate contact, and doesn't block the mover |
| 12 | Mixed army: the visible member is engageable/targetable, the hidden member is off the roster |
| 13 | A hidden unit neither holds a base (it's captured over its head) nor captures one (a fully‑hidden mover can't) |
| 14 | `FindAirStrikeTargetsAt` skips a hidden‑undetected unit, includes it once detected |
| 15 | A detection lasts through the end of the observer's next own turn (`CompletedTurnsFor` serial), then lapses |
| 16 | The owner gets no signal (still just sees their own unit, no notice, no "who detected me" API); the **detector alone** gets one turn-start notice naming the unit and its `(col, row)`, drained on read |
| 17 | A detected hidden unit is a valid concrete target; `ExitStealth` (what the directed‑action paths call) reveals it and clears its detection table |
| 18 | `AiMapMemory` never records a hidden‑undetected enemy as a current sighting; records it once detected |
| 19 | An AI solo scout carrying Stealth4 satisfies the pre‑move auto‑stealth gate (`IsSoloRecce` + `CanEnterStealth`) |
| 20 | Entry is gated (Stealth4, not already hidden); a voluntary exit is an unconditional free primitive |

Scenarios **17, 19, 20** are verified at the decision‑gate level: the AP charge and the
directed‑action reveal live in coroutine/`MonoBehaviour` call sites
(`AiTurnController.MoveArmyRoutine`, `BattleScreenUI`, `ArmyViewerModalUI`) that can't run
headless — the harness asserts the primitives and gates those call sites depend on.
