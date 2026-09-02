# recon-ownership-sim

Standalone acceptance harness for the **Recon ownership / accounting invariants** exposed by the
`AiDebug(20260902-082556)` live-log review.

Like the other `Tools/*-sim` harnesses it `ProjectReference`s the Unity-generated
`Assembly-CSharp.csproj` and exercises the pure model / continuity / classification seams without
a scene or coroutine.

## Run

```text
cd Tools/recon-ownership-sim
dotnet run -c Release
```

Exit code `0` means all scenarios passed.

## Scenarios

| # | Scenario | Contract |
|---|---|---|
| A | One durable role per physical scout | A fresh opportunistic scout outcome for a mover that already owns a durable Explore role **re-focuses** that one role (`TryAbsorbIntoExistingActorRole`); never a second durable intent for the same actor. `CreatedTurn` / accumulated AP+steps carry across. |
| B | Stale duplicate cannot inflate the actor count | Two physical movers resolve to two durable intents even when a duplicate outcome was fed for one of them; distinct active scout actors stays `2`, never `3/4`. |
| C | Event / battle interruption is a ProductiveStop | `HexEventStarted` / `BattleStarted` **after** useful movement classify as `ProductiveStop`, not `Failed`; the durable Recon role is kept, not retired. |
| D | Already combat-locked is Blocked | `BattleStarted` with `BlockedBeforeMovement` and zero progress classifies as `Blocked` (recoverable), not `Failed`; the durable role is not destroyed. |
| F | Local Explore out-ranks equally informative distant | `ReconObjectiveEvaluator.BuildExplore` gives a nearer frontier a higher `BaseValue` than an equally / more informative distant one while nearby unknown remains; distant work still keeps a materially non-zero value (soft preference, not a leash). `HomeDistance` folds in the Citadel explicitly. |
| G | ReconOnly isolates missions, not hand management | `AllowSurplusPreparation` is `true` in `ReconOnly` (Phase B runs); non-Recon strategic **demands** are still suppressed. |

## Covered in-editor instead

Scenarios that need a live `HexMap` / `VisionSystem` / aviation stack are exercised by playtest,
matching the rest of the `Tools/` suite:

* **E** — impossible `Refresh` vs executable `Explore` assignment contention (needs the real
  `ProvisioningManager` assignment solver + a map for `VisitHexTask.FindNextSafeStep`).
* **F (live step)** — `ReconGroundStepPlanner` home-pressure step ranking (needs a map +
  `VisionSystem`).
* **H** — a full AirRecon sortie + return.
* **I** — 10–20 turn long run.
