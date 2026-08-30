# housekeeping-sim

Acceptance harness for **Strategy V2 build-order step 8C — HousekeepingManager / Army & Garrison
Reorganization**.

## What it covers

Drives the pure `ArmyReorganizationPlanner.Plan(LocalForceGroup)` directly with hand-built
container projections and asserts the lexicographic policy and its invariants:

- non-exempt singleton field armies are combined / absorbed / deposited into a legal same-hex
  arrangement;
- non-viable weak armies are absorbed whole (preferred) or seeded past the viability floor;
- a lone hero is **not** auto-exempt; a canonical solo Recce **is**;
- an emptied source `ArmyData` stays listed as a zero-member reusable shell — never deleted;
- a garrison is never raided below its canonical secure floor for cosmetic consolidation;
- a viable donor is never driven below viability just to help another army;
- an all-protected group and a healthy hex both produce a no-op plan;
- identical input state produces an identical plan (determinism).

Execution against live `ArmyData` (`ArmyReorgAnalyzer` + `HousekeepingExecutor`) cannot run
headless — it is exercised in-game.

## Run

```
dotnet run -c Debug --project Tools/housekeeping-sim
```

Exit code 0 = all pass. Same pattern as `Tools/mission-selection-sim` — a console Exe that
`ProjectReference`s the Unity-generated `Assembly-CSharp.csproj`, so it always runs the current
game code.
