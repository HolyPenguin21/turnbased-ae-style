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
| E | Surveil shares actor-exclusivity | Ownership is one durable role per physical mover **across Explore / Refresh / Surveil**. A mover with an Explore role that then produces a Surveil outcome keeps ONE intent, re-focused to Surveil, accumulated identity preserved. |
| F | Local Explore out-ranks equally informative distant | `ReconObjectiveEvaluator.BuildExplore` gives a nearer frontier a higher `BaseValue` than an equally / more informative distant one while nearby unknown remains; distant work still keeps a materially non-zero value (soft preference, not a leash). `HomeDistance` folds in the Citadel explicitly. |
| G | ReconOnly isolates missions, not hand management | `AllowSurplusPreparation` is `true` in `ReconOnly` (Phase B runs); non-Recon strategic **demands** are still suppressed. |
| G2 | Every card type has a Phase-B lane | `NonCombatCardPlayer.LaneFor` routes every `CardType`: Unit/Hero to the materialization chain, Aviation/Base/Facility/Equipment to the non-combat lane. Card type is never on its own a dead end. |

## Covered in-editor instead

Scenarios that need a live `HexMap` / `VisionSystem` / aviation / infrastructure stack are
exercised by playtest, matching the rest of the `Tools/` suite. `CardDefinition` is a plain class
but `NonCombatCardPlayer.Execute` and the materialization chain call `HexSelectionController` /
`InfrastructureActions` / `AviationActions`, which are Unity-runtime only.

* **E-contention** — impossible `Refresh` vs executable `Explore` assignment contention (needs the
  real `ProvisioningManager` assignment solver + a map for `VisitHexTask.FindNextSafeStep`).
* **F (live step)** — `ReconGroundStepPlanner` home-pressure step ranking (needs a map +
  `VisionSystem`).
* **G-mixed-hand** — deal a hand with a Unit, a Hero, an Aviation card, a Base/Facility card and
  an Equipment card, all with legal placement + resources, in `ReconOnly`. Expect: `UseSurplus`
  runs; the Unit/Hero go through the materialization chain; the `strat.B non-combat` log shows the
  Aviation card stored at an airfield, the Base/Facility built, the Equipment attached; any card
  left in hand has a `strat.B non-combat — still blocked [<card>:<gameplay reason>]` entry (no
  AP / no resources / no destination / no capacity / no host) — **never** `ReconOnly` or a
  card-type reason. Also check: total Phase-B plays (materialization + non-combat) never exceed
  `maxSurplusActionsPerTurn`; and if the materialization loop raises a strategic interrupt
  (`re-admit missions before further surplus spending`) the pass logs
  `strat.B non-combat — skipped: Phase B did not end cleanly` and spends no further AP that pass;
  and a terminal-draw that turns up a legal Aviation/Base/Equipment card stops drawing and fires
  `strategic interrupt — terminal draw changed the actionable hand`.
* **H** — airfield + ready aircraft + stale intel: observe one full `[Recon][Air]` sortie —
  launch → Outbound → Turning/Return → Landing → IntelAge refresh — with no lost aircraft and no
  retrace loop. When zero sorties fly, `[Recon][Air] fallback — <exit>: … skips=[…]` states why.
* **I** — 10–20 turn long run: no duplicate scout ownership, no `DuplicateReconActorIntent`
  CHECK, no impossible-Refresh actor theft, no event-triggered false failures, StrategicManager
  keeps consuming legal cards of every type, no reservation leak, no AP-accounting CHECK.
