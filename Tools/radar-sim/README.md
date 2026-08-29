# radar-sim

Standalone acceptance harness for **Strategy V2 build-order step 3** — the desire evaluators
(`Assets/Scripts/Ai/V2/DesireEvaluators.cs` + `CombatOpportunityAnalyzer.cs`).

Same pattern as `Tools/stealth-sim`: a console `Exe` that `ProjectReference`s the Unity-generated
`Assembly-CSharp.csproj`, so it always runs the *current* game code. Only the pure model layer is
exercised — no `MonoBehaviour`, scene, or coroutine code.

## Run

```
cd Tools/radar-sim
dotnet run -c Debug
```

Exit code `0` = all scenarios passed. Each line prints the normalised radar plus the
`DesireBreakdown` sub-terms for eyeballing.

## What it checks

Each scenario hand-builds a `WorldSnapshot` POCO for a curated position and asserts the radar
tilts the right way. Numbers are first-pass; the harness pins **behaviour**, not magnitudes.

| # | position | asserts |
|---|----------|---------|
| 01 | fog at start, opponents known to exist, no sightings | Recon is the dominant axis; exploration ≈ 1; enemyBlindness fires |
| 02 | strong hero-led army + one weak neutral 2 hexes away | Aggression leads; driven by `raidOpportunity` > `warPressure`; viability gate passes |
| 03 | ten unbeatable (brick-wall) neutrals | `opportunity` == 0, gate never passes, `raidOpportunity` strictly below the beatable-target case |
| 04 | `BestStackPotential` ≈ `TotalMilitaryPotential`, calm, no targets | Aggression via `warPressure`, not `raidOpportunity`; `potentialSaturation` > 0.6 |
| 05 | scenario-02 position + `Threat.UnderSiege` | the same beatable target no longer lifts Aggression; `MilitaryThreat` ≥ 0.9 |
| 06 | an *observed* enemy contact gets weaker across two turns | `momentum` well above neutral |
| 07 | our own `TotalPower` drops hard across two turns | `momentum` well below neutral |
| 08 | a real threat on a base | `RequiredDefensiveReserve` ≈ contactPower × 1.3; `surplus` collapses |
| 09 | never seen the enemy (`EnemyKnownStrength` 0) | `relativeEdge` sits at neutral 0.5, not maxed |
| 10 | dark hexes remain but none reachable on foot (`ExplorableUnknownFrac` == 0) | `exploration` ≈ 0; Recon held up only by `surveillance` |
