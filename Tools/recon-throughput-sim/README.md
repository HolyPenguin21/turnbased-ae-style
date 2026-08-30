# AI V2 throughput regression harness

Focused acceptance harness for the post-log corrective pass around Recon throughput, stranded
capacity, and Aggression actor contention.

## Scenarios

1. Dark map + valuable second Recon objective may request two execution lanes.
2. A weak marginal Recon objective does not become a second-scout production target.
3. A mostly explored map does not buy a second scout just because hard K is 2.
4. One physical scout cannot back two separated Recon missions.
5. Two physical scouts may back two separated Recon missions.
6. Existing Recon spatial-conflict rules still apply even with two scouts.
7. Large safe AP/resource slack relaxes Phase-B's soft surplus threshold.
8. Near hard reserves, Phase-B keeps the configured conservative threshold.
9. High leftover AP is diagnosed as stranded/non-AP-limited, not initiative starvation.
10. Real work left at the AP floor is diagnosed as AP-limited.
11. One ready combat actor cannot back two Raid targets simultaneously.
12. Two ready combat actors can provide a distinct assignment for two Raid targets.
13. Raid actor contention is transient (`MoverContended/RetryNextTurn`) while genuine
    `AssemblyInfeasible` retains structural cooldown semantics.
14. A durable Hard Raid blocked by temporary actor contention is suspended without increasing
    `StallTurns` and without creating a target cooldown.

Funded Raid proposals are batch-matched to distinct ready actors in `ProvisioningManager` before
any individual Raid claims an army. The live Raid provisioning path then revalidates that actor and,
if needed, re-runs the same `RaidAssemblyPlanner` without same-turn actor claims. If the unrestricted
solve succeeds, the failure is actor contention and cannot create a target cooldown.

## Run

From a Unity-generated project checkout with `Assembly-CSharp.csproj` available:

```bash
cd Tools/recon-throughput-sim
dotnet run -c Debug
```

The project currently points `UnityManaged` at the same local Unity 6000.5.4f1 path convention as
the other AI V2 standalone harnesses; override that MSBuild property if Unity is installed elsewhere.