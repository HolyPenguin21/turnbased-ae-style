# commitment-sim

Standalone acceptance harness for **Strategy V2 build-order step 7** — Mission Continuity
(`Assets/Scripts/Ai/V2/MissionIntent.cs`, `ScoutObjectiveEvaluator.cs`, and the step-7 hooks in
`ReconMissionPlanner` / `ResourceAllocator`).

Same pattern as `Tools/radar-sim` and `Tools/stealth-sim`: a console `Exe` that
`ProjectReference`s the Unity-generated `Assembly-CSharp.csproj`, so it always runs the *current*
game code. Only the pure model layer is exercised — no `MonoBehaviour`, scene, or coroutine.
Execution / provisioning against live `ArmyData` cannot run headless, so the scripted turns feed
`MissionTurnOutcome` facts (built through the real `MissionOutcomeLedger`) into
`MissionContinuityLayer.ReconcileAfterTurn`, exactly as the pipeline does.

## Run

```
cd Tools/commitment-sim
dotnet run -c Debug
```

Exit code `0` = all scenarios passed.

## What it checks

| # | scenario | asserts |
|---|----------|---------|
| 01 | canonical: Surveil starts, RCN collapses next turn, RCN recovers | intent is created `Soft`; survives the collapse as a funded `Commitment` while a fresh mission at the same RCN slice is deferred; identity (`IntentKey`) stays fixed though the focus hex moves; completes and retires on re-observation |
| 02 | Surveil executes but makes no movement and no stealth entry | no intent created (continuation is *earned* by moving, never by spending AP) |
| 03 | Surveil enters required stealth, first step blocked | intent created `Soft` — a stealth STATE change is progress |
| 04 | in-flight Surveil, then `TargetSatisfied` (another scout re-observed it) | intent retired as complete, no cooldown |
| 05 | in-flight Surveil, then `NoObservationVantage` (structural) | intent retired **and** the attempt key put on the allocator reject cooldown |
| 06 | Explore progress → intent kept with `Funding == None`; retarget hysteresis | a marginally-better fresh frontier hex does NOT flip the heading; a hex past the `commitmentRetargetMargin` does |
| 07 | Soft commitment while `UnderSiege` | `ResolveActive` suspends it (`Siege`), binds no funding; it re-activates when the siege lifts |
| 08 | reprice: provision fails on pass 1 (`EnvelopeTooSmall`), succeeds on pass 2, then executes | the ledger reports the FINAL state (`ProductiveStop`), never the stale intermediate failure |
