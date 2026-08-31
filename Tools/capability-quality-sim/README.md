# capability-quality-sim

Standalone acceptance harness for the **Strategy V2 "Capability Quality, Contextual Scout
Decisions & Terminal AP Spending"** task:

* `Assets/Scripts/Ai/V2/ScoutCapabilityQuality.cs` — the pure Scout capability-quality evaluator
* `Assets/Scripts/Ai/V2/CapabilityQualityEvaluator.cs` — the seam ScorePlanA calls
* `Assets/Scripts/Ai/V2/ScoutCapabilityContext.cs` — the mission context a Recon demand carries
* `Assets/Scripts/Ai/V2/ScoutOptionalStealthPolicy.cs` — the optional (non-Required) stealth Enter/Skip decision
* `Assets/Scripts/Ai/V2/AiConfigV2.cs` + `StrategicManager.cs` — Phase-B terminal draw + retired reserves

Same pattern as `Tools/mission-selection-sim`: a console `Exe` that `ProjectReference`s the
Unity-generated `Assembly-CSharp.csproj`, so it always runs the *current* game code. Only the
pure model layer is exercised — no `MonoBehaviour`, scene, or coroutine. The full Phase-A/Phase-B
loop and live execution against `ArmyData` cannot run headless; those are exercised in-game.

## Run

```
cd Tools/capability-quality-sim
dotnet run -c Debug
```

Exit code `0` = all scenarios passed.

## What it checks

| # | scenario | asserts |
|---|----------|---------|
| 01 | dark-map mobility | far focus + dark map → Move 3 outranks Move 2 |
| 02 | short target | one-hex focus → Move 3 ≈ Move 2 (no material mobility bonus) |
| 03 | vision marginal value | radius 2 beats radius 1 on a dense dark frontier; barely beats it near fully-explored terrain |
| 04 | spot irrelevant | `r2s0` > `r1s6` on a plain Explore; spot term ≈ 0 without a detection context |
| 05 | spot valuable | detection context → spot strength earns real quality and `r1s6` catches / passes `r2s0` |
| 06 | Preferred stealth | never a hard gate; a modest option value when safe, materially more when the leg is risky |
| 07 | activation AP | higher activation AP lowers scout quality; the drag term is negative |
| 08 | scarce Hero | acute hero scarcity → negative opportunity cost → the weaker Unit scout wins |
| 09 | abundant Hero | no opportunity cost; the faster Hero scout wins on merit |
| 10 | no overpay | a luxury scout for a trivial nearby objective stays ~neutral; its extra activation AP still drags |
| 11 | determinism | identical inputs → identical multiplier |
| 16 | optional stealth safe | negligible leg risk → SKIP |
| 17 | optional stealth risky | dangerous leg + AP headroom → ENTER, protection > opportunity |
| 18 | AP opportunity cost | same risk ENTERs with headroom but SKIPs when the 1 AP is the difference between drawing and not |
| 19 | optional stealth guards | already hidden → SKIP; transition unaffordable → SKIP |
| 20 | config reconciliation | `maxTerminalDrawsPerTurn` is a real bound; the old speculative Phase-B reserves are gone |
