# recon-cooldown-sim

Standalone acceptance harness for the **AI Strategy V2 mission-cooldown contract**. It pins the startup-recon regression where `NoMoverExists` used to mark Explore objectives as structural failures, causing two turns of `OnCooldown` even after `StrategicManager` created a scout.

Like the other `Tools/*-sim` harnesses, it `ProjectReference`s the Unity-generated `Assembly-CSharp.csproj` and exercises the pure model/allocator/continuity seams without a scene or coroutine.

## Run

```text
cd Tools/recon-cooldown-sim
dotnet run -c Debug
```

Exit code `0` means all scenarios passed.

## Scenarios

| # | Scenario | Contract |
|---|---|---|
| 01 | Fresh Explore has no mover | `NoMoverExists` is `RetryNextTurn`, ledger outcome is `Blocked`, never structural, no target cooldown |
| 02 | Started Surveil loses its mover | intent survives as `CapabilityUnavailable`, stall does not age, target is not poisoned; it reactivates next turn |
| 03 | Surveil has no observation vantage | genuine structural failure still starts the configured two-turn recon cooldown and records reason/start/until metadata |
| 04 | Raid assembly is structurally infeasible | the single continuity owner applies the Raid-specific three-turn cooldown, not the generic recon duration |
| 05 | Structural provisioning failure occurs during re-pack | `AllocationSession` rejects it only for the current turn; persistent cooldown appears only after final ledger reconciliation |
| 06 | T1 Explore fails because no scout exists | the same `StableMissionKey` is fundable again on T2; direct regression for the observed startup deadlock |
| 07 | Every uncovered Recon objective is on structural cooldown | `DemandLayer` creates no replacement `ScoutCapability` demand |
| 08 | One of two Recon objectives is blocked | Demand sizes capability from the one runnable job only (`DesiredAmount == 1`) |

The existing `mission-selection-sim` still protects bounded re-pack/fallback, capacity and conflict behavior; `commitment-sim` still protects multi-turn intent/commitment behavior and `NoObservationVantage` retirement.
