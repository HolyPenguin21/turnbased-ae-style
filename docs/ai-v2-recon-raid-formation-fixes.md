# AI Strategy V2 — Recon Stability, Army Organization & Raid Assembly Fixes

Reference failure log: `Logs/AiDebug.log` (a fresh headless-ish run reproduces every
confirmed problem through turn 9).

## 1. Confirmed reproductions (against `master` @ `de854aa`)

| Spec | Symptom in the reference log | Root cause found |
|---|---|---|
| §2 | `strat.B — residual bypass denied for … Miller Hayes: Garrison cannot operationally deliver Hero; evaluate as generic surplus` → `Direct cards[base="Miller Hayes" …] (ap 1, Garrison, delivered 0, …)`. Same for Rusty Miller, Elena Hayes. | `MaterializationCandidateBuilder.BestSurplus` offered a strategically-claimed Hero card a Garrison placement; `UseSurplus` then admitted it as generic surplus. |
| §3 / §9 | turn 8 `admit residual … FieldCombatPower … via Scrap Mortar … (NewArmy, delivered 4.93)` → same turn `housekeeping 2,-1 — moved Scrap Mortar #12->#2 (fold weak/singleton army into destination)` → turn 9 `[Lease] protect … FieldCombatPower army(s) [12]` (a turn late). | Phase B never called `StrategicCapabilityLeaseRegistry.Mark`; only Phase A did. |
| §7 | Heroes (Miller Hayes, Rusty Miller, Elena Hayes, …) pile into garrison #2; capacity stays low because the first-roster hero has a low CommandRating. | Housekeeping reorg had no commander-order objective. |
| §11 | turn 9 `Demand[Aggression] decision=CREATE … FieldCombatPower desired=1 reason=no_ready_free_army_clears_estimator freePower=26,2 … requiredPower=19,5`. | `RaidOperationalReadiness`: `needsPower = !executable`, `RequestedPower = max(1, deficit)`. |
| §12 | Heroes accumulate in the garrison; `RaidAssemblyPlanner` never uses them. | `TryAssembleForHost` donor pick excluded heroes; `CapabilityInventory.AvailableHeroes` counts only field heroes. |
| §13 | Every turn 3–9: `mission suppress — Raid(#7) reason=no_ready_raid_actor_after_phaseA` with `asmWin=1.00`. | Follows from §12 — `RaidAdmissionRegistry.Record` consults `RaidAssemblyPlanner.Plan`, which never became feasible. |
| §6 | `Intent(Explore 4,-2) created` … `Intent(Explore 4,-2) retired at turn start (objective no longer valid)` … immediately re-enumerated as a runnable Recon objective. Also `Intent(Explore 2,1)`. Visiting `(4,-2)` expanded the frontier 1→11. | `IsIntentStillValid` gated on `ExploreStillOpen > 0` (fresh-neighbour count); `ReconObjectiveEvaluator.Enumerate` took every frontier hex. |
| §4 | (no ordinary V2 recon-path `ExitStealth` — invariant only). | V2 `TaskExecutor` only calls `EnterStealth`; the sole voluntary AI reveal is the guarded V1 path in `AiTurnController`. |
| §5 | Scout re-treads explored ground between objectives (`routeMP` climbs while `visitsNow` drops). | `ScoutRouteCostEvaluator` had no retrace term. |

## 2. What each commit changed

- **§2/§3 (`5a8b33a`)** — `MaterializationCandidateBuilder.UnresolvedClaimFor` withholds a
  surplus card from any placement that cannot deliver a capability it is still strategically
  claimed by; the hero stays in hand. `StrategicManager.FinalizeOperationalDelivery` is the one
  post-delivery path (delivered measurement + Housekeeping lease) shared by Phase A and Phase B
  (and, through them, the bounded reaction pass).
- **§7 (`5329d00`)** — `ArmyData.TryReorderCommander` (canonical zero-AP roster reorder) +
  `PlannedTransfer.Reorder` + `Outcome.CommandCapacityWaste` ranked as a formation-quality term.
  A reorderable field/garrison container with ≥2 heroes and a strictly weaker current commander
  is fixed; equal CommandRating never churns.
- **§11 (`29d7c2c`)** — `RaidOperationalReadiness.NeedsPower` == real numeric deficit only;
  `NeedsAssembly` for a sufficient-power / no-legal-force organization gap; DemandLayer emits
  `decision=DEFER reason=assembly_gap` and no `FieldCombatPower` demand.
- **§8 (`f55934d`)** — `HeroRoleEvaluator` (CombatLeader / Flexible / SupportOperator) from
  canonical data only: CommandRating + the hero's own `AiPower` contribution (HP/Initiative/
  Resistance/Fate — **heroes carry no Attack/Defense**) + Researcher/Assembler vocation. Wired
  into the reorg analyzer (`ReorgUnit.HeroRole` / `HeroCombatLeadership`).
- **§12 (`fb7e48c`)** — `RaidAssemblyPlanner.TryAssembleForHost` may attach ONE eligible same-hex
  hero (garrison / lone-hero container) to a heroless host, preferring CombatLeader > Flexible >
  SupportOperator; `ProvisioningManager.Provision` permits exactly one hero transfer under the
  canonical `CanSpareGarrisonMember` / capacity / activation guards. §13 re-admission then
  follows automatically.
- **§9 (`b034197`)** — Housekeeping gives a heroless viable field formation a benched combat
  hero: direct move if there is room, otherwise a canonical hero-for-body **swap** (the
  "no room in either army" case the user flagged). Garrison allowed on one side of a swap for a
  hero-out / non-hero-in trade only. `Outcome.FormationDefect` ranks it above generic
  power/composition. Support heroes stay for support duty. The greedy loop leads formations
  round-robin (one per iteration).
- **§6 (`c4f019d`)** — `ScoutObjectiveEvaluator.IsExploreFocusRunnable` is THE Explore validity
  contract (on-map, unvisited, not hard-blocked). Used by `IsIntentStillValid`,
  `ReconObjectiveEvaluator.Enumerate` and `ExploreAt`. `FreshNeighbors == 0` is value, not
  invalidation.
- **§4/§5 (`8e2d8de`)** — the sole voluntary AI scout reveal now logs
  `ScoutStealthExit reason=<...>` through canonical `StealthSystem.ExitStealth`.
  `ScoutTrailRegistry` (bounded per-scout trail + just-left hex) feeds a three-tier
  `RetraceFactor` into `ScoutRouteCostEvaluator` — immediate reversal (strongest), recent trail,
  proportion-visited (light floor). Never a hard block.
- **§15/§16 (`45d7612`)** — `MaterializationDiagnostics.ExplainNoChain` runs the real
  operational-delivery + chain-resource gates and reports
  `postGate=operational-delivery-gate` / `opDeliver` / `resReject`. Housekeeping logs hero
  moves with `role=…` and unresolved `heroless formation #N unresolved reason=…` /
  `singleton #N protected reason=StrategicCapabilityLease/mission`.
- **§17 (`2dce089`)** — `ResourceStarvationRegistry`: bounded, once-per-turn-decaying pressure
  raised when AGG/RCN chains stall on an empty stock; consumed as ONE bounded
  `EconomicInfrastructure` demand (`reason=repeated_strategic_starvation`). Own state only.

## 3. Tests

Standalone sim harnesses (`Tools/*`, `dotnet run`):

- `capability-quality-sim` — **71 pass / 1 pre-existing fail** (`16 negligible known route risk`
  fails on `master` too). New: S21 (Phase B strategic hero claim), S22 (raid power vs assembly),
  S23 (hero role), S24 (Explore validity contract), S25 (scout trail retrace), S26 (starvation
  feedback).
- `housekeeping-sim` — **55 / 55**. New: S18–S21 (commander reorder / determinism / leased
  singleton), S22–S25 (hero-to-field formation, combat > support preference, support retained,
  garrison-security preserved).
- `stealth-sim` — 23 / 23 unchanged.
- `radar-sim` — 9 / 1 (`04` fails on `master` too, unrelated).
- `commitment-sim`, `mission-selection-sim`, `recon-cooldown-sim`, `recon-throughput-sim` —
  crash headless on `master` (Unity `FindObjectOfType` / `ECall` from `ScoutRouteCostEvaluator`
  / broken csproj); not regressed by this work.

`Assembly-CSharp.csproj` build: **0 warnings, 0 errors** after every commit. The csproj gains
`HeroRoleEvaluator.cs`, `ScoutTrailRegistry.cs`, `ResourceStarvationRegistry.cs` — Unity
regenerates the file, and it is gitignored, so these entries are local-only for headless builds.

## 4. §23 end-to-end regression — run in the editor

The multi-turn global-map AI simulation cannot run headless (the recon sims already crash on
`FindObjectOfType`). Reproduce in Unity with the reference-log map/seed and confirm the
corrected shape:

1. a known neutral raid target with `asmWin ≈ 1.00`, AGG high;
2. base garrison holds several heroes incl. ≥1 CombatLeader and ≥1 SupportOperator (Researcher/
   Assembler);
3. one or more heroless field bodies at/near the garrison hex.

Expected (was → now):

| Old | Corrected |
|---|---|
| combat heroes sit in garrison | highest-CommandRating hero commands the garrison; a CombatLeader is moved to a field formation |
| two-body field armies stay heroless for the whole run | a hero-for-body swap forms a hero-led field army |
| `FieldCombatPower desired=1 reason=no_ready_free_army_clears_estimator` while `freePower > requiredPower` | `Demand[Aggression] decision=DEFER reason=assembly_gap`; no fake demand |
| Phase B plays heroes into garrison `delivered 0` | `strat.B — hold …: hero card matches unresolved … keep in hand` |
| new Phase B force folded away same turn | `[Lease] protect operational …` fires **the same turn**; housekeeping leaves it |
| `mission suppress — Raid(#7) reason=no_ready_raid_actor_after_phaseA` forever | `raid mission — PROPOSE …` once a legal force is assemblable; Provisioning attaches the hero and the raid starts moving |
| `retire Explore (4,-2)` → `create Explore (4,-2)` same snapshot | neither line; the intent survives until `(4,-2)` is actually visited |

## 5. §24 telemetry to eyeball in a fresh debug run

- `[TaskExecutor…] exec … scout stealth … -> ENTER` when entering; **no** `ScoutStealthExit`
  line during ordinary Explore movement; a `ScoutStealthExit reason=capture_or_destroy_building`
  / `reason=pre_action_<Kind>` only immediately before a contact action.
- `housekeeping … commander reorder army #N -> <hero> capacity X->Y`.
- `housekeeping … moved/swapped <hero> … role=CombatLeader …` for garrison→field.
- `housekeeping … heroless formation #N unresolved reason=…` when it genuinely cannot fix one.
- `[Lease] protect operational <Cap> army(s) […]` on the SAME turn as the Phase B / reaction
  delivery.
- `Demand[Aggression] decision=DEFER reason=assembly_gap detail="…"` instead of a
  `FieldCombatPower desired=1` line when `freePower ≥ requiredPower`.
- `raid mission — PROPOSE …` and a subsequent `raid provision … OK host #N (+1 body from 1 donor)`.
- `strat.A — … postGate=operational-delivery-gate` (not `direct-passes-post-preflight`) when
  every placement fails the delivery gate.
- No new `[CHECK][ERROR]` and no `housekeeping AP invariant violated` lines.

## 6. Not done / follow-ups

- §23's deterministic in-editor scenario itself (needs a Unity test scene; the behavioural spec
  above is the acceptance contract).
- Deep in-engine integration coverage for §12 raid-hero attach and §9 garrison↔field swap —
  exercised through the user's normal Unity play loop.
