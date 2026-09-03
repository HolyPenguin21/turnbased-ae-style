# AI Strategy V2 — Recon / Aviation / Strategic-Manager task set

Durable copy of the 7 task specs handed down for the V2 AI. One task per work session.
Edits go **directly into the main V2 path** (`aiStrategyV2Enabled` is already the only AI
path — no per-task sub-flag). **No sim tests** — build must stay clean on both Unity
assemblies; the user play-tests each task in Unity.

## Progress tracker

| ID | Title | Status |
|---|---|---|
| AI-INTEL-01 | Explicit Observation / Ground Visit semantics | DONE — semantic core already existed (Explore/Refresh/Surveil = GroundVisit/ObservationFreshness split, `AiReconIntelMemory` Observed≠Visited, air `LogVisitedInvariant`). Only behavioural gap closed: `ReconObjectiveEvaluator.BuildExplore` now age-discounts the info term of an already-observed frontier cell (`scoutExploreObservedInfoDiscountFloor`, floored + recovers over `scoutSurveilStaleTurnsLo..Hi`); `homeProximity` / Refresh path untouched. Both assemblies build clean. NOT play-tested. |
| AI-RECON-02 | Unified Recon Capacity model | DONE — new `ReconCapacitySnapshot` (Assets/Scripts/Ai/V2/ReconCapacitySnapshot.cs): actor-id sets for observation capacity (ground scout on obs lane / idle-usable ground scout + `ReadyAirObservationActors` + `AirborneObservationActors` (durable ReconAssignment) + `PlannedAirObservationActors` (funded AiTaskKind.AirRecon, not launched)) and a ground-only `GroundTraversalActors` set. `ObservationDeficit` folds in ready/airborne/funded aviation + idle ground scouts left after ground-traversal takes its first claim on the shared idle pool; `GroundTraversalDeficit` is air-blind. `DemandLayer.ReconDemands` now splits runnable objectives Explore↔(Refresh/Surveil), builds the snapshot, and only emits a `ScoutCapability` CREATE when the matching deficit has persisted (`ReconCapacityDeficitRegistry`, `AiConfigV2.reconCapacityDeficitPersistTurns=1`). Stealth still checked independently (aviation can't sub a stealth ground scout). HardCap ceiling still bounds concurrent ground scouts. Registry cleared in `AiReconMemory.Clear`. Both assemblies build clean. NOT play-tested. |
| AI-RECON-01 | Actor-aware Recon planning & reservation | DONE — `9b43ffa` first pass, `103e2c1` review-fix 1, `5979411` review-fix 2, `f894fca` review-fix 3, `9c0215d` review-fix 4, `1c1e489` review-fix 5, `eeb21d7` review-fix 6 (generic ActorCommitments sweep no longer re-hard-excludes a deliberately-freed cooldown-incumbent scout). Ground lane: `ReconActorReservationContext { ReservedActorIds, JobToActor, ActorToJob, HardExcluded, JobBlock }` + per-class/per-stealth/global room, lives across the whole re-pack loop. Unmatched jobs stay as unreserved proposals (`DeferReason.ReconActorUnreserved`, gated in BOTH the fresh AND the commitment loop). On a budget defer / lane-full / conflict / provisioning miss the re-pack loop calls `ReconActorReservationPlanner.Rematch` which `Release`s the actor + its concurrency slot AND records WHY (`ReconJobBlock.BudgetInfeasibleThisTurn` / `RejectedThisTurn`) so `AssignPass` re-assigns the freed scout to a job that CAN still be admitted instead of re-pinning the same infeasible one (the pricey-Surveil-strands-the-scout loop the earlier pass had). Incumbent seeding per-job. Bound actor → `MissionRequirements` re-priced to that actor's exact `ScoutCostModel.PairCost`. Room: `Generic{Obs,Ground}` from `ReconCapacitySnapshot` (desired − active − pinned-air for Obs), `Stealth{Obs,Ground}` sized directly from stealth-filtered runnable objectives (fixes the regress where a fresh stealth Surveil was starved by a 0 generic room), plus a shared `GlobalGroundActorRoom = HardCap − active`. Air lane: `ReconAirReservationPrepass` (BEFORE DemandLayer+Phase A) now (a) only pins launch slots the AIR-01 route scorer would actually fly (`ReconAirStepPlanner.Pick` / `PickFromStorage` + useful-score + `ReconAirEnergyPolicy` + `CanAffordLaunch`), not merely physically-launchable aircraft (the remaining phantom-capacity path); (b) protection is lifted RIGHT BEFORE `TaskExecutor`'s terminal air fallback (`onBeforeAirFallback` hook) so the reservation no longer blocks its own launch; (c) `ProtectedAp` is debited from the allocator's GLOBAL commitment pool too, not just the radar ledger, so a Hard raid can't grab the sortie's AP. `ProvisioningManager.PreparePass` now takes the full `ReconActorReservationContext.ReservedActorIds` (not just funded siblings) so a fallback rematch never poaches a scout bound to a deferred sibling. Concrete air actor/subset binding into the executor is left to AIR-01/AIR-02 (executor re-derives the same deterministic subset). Both assemblies build clean (0/0). NOT play-tested. |
| AI-AIR-01 | Strategic air-recon target & route selection | DONE — new `AirReconRouteCandidate.cs` (Assets/Scripts/Ai/V2/): (1) `AirReconAnchorModel.Build` forms the sortie DIRECTION first from strategic landmarks in priority order — sanitized enemy concentration (one base unit per TrueWorld army, normalised), enemy Citadel (known focus, else real *sector* as a hidden bias at `airReconCitadelHiddenConfidence`), own facility perimeters whose IntelAge ≥ `airReconFacilityStaleAgeMin` (+ probable-approach sector toward nearest known threat), enemy↔asset corridors (midpoint sector), stale known-sighting refresh, and unknown frontier last/weakly (`airReconAnchorFrontierWeight`). Emits an `AirReconAnchorSet` (priority-ordered anchors + peak-normalised per-sector pressure + Citadel sector/confidence + stale-facility hex list). Cheat = direction only; no hex is marked observed. (2) `AirReconRouteScorer.Score` scores a candidate first step for its PROVEN WHOLE ROUTE (`Sortie.Outbound/ReturnPath` or `MultiTurnSortie.PathToAction/PathFromActionToLanding`, capped at `airReconRouteObservationMaxHexes`): additive `InformationGain + StaleIntelRefreshValue` (destination footprint, unchanged basis) `+ EnemyInterest` (blended sector pressure) `+ EnemyCitadelDirectionValue + FriendlyFacilityCoverValue + RouteObservationValue` (Σ per-hex never-observed/stale usefulness along the route, geometric `airReconRouteObservationDecay`, + neighbour ring) `+ CombatOpportunityValue` (route within 1 of an HONESTLY-known sighting) `− TravelCost − ActivationCost − RecoveryRisk` (extra turns + unlanded ends + KNOWN-AA-adjacent route hexes) `− RedundancyPenalty` (recent-air-observed route hexes via `AiMapMemory.WasAirReconnedWithin` + outbound-trail hug + coverage-sector divisor). Hard rules §5: reject if entire positive side ≤ `airReconStrategicValueFloor` ("only value is GroundVisited==false"), or ≥ `airReconRedundancyRecentObsRejectFrac` of informative route hexes are recent air observations. `ReconAirStepPlanner.BuildChoice` now returns `StepChoice?` and delegates all scoring to the scorer (drops the old `ReconDirectionSnapshot` path); rejected candidates are logged `[Recon][Air][Route] DROP` and excluded. `ReconAirExecutor` stamps `AiMapMemory.RecordAirReconTarget` on every observed step + storage launch (V2 never calls `ContinueSortie`, the only V1 stamper, so the redundancy data was previously empty). New config block in `AiConfigV2` (`airRecon*` route/anchor tunables, first-pass). **Review round 1 (P0+2×P1):** (P0 phantom-route) the scorer no longer credits `RouteObservationValue`/`FriendlyFacilityCoverValue`/`CombatOpportunityValue` from the hypothetical RETURN path the one-step executor discards and re-plans — it scores only the committed forward corridor (`OutboundHexes`); the return path feeds `RecoveryRisk` (KNOWN-AA proximity) + `TravelCost` only. (P1 redundancy-blind) recent-air-coverage overlap is now a metric independent of current usefulness, counted over EVERY corridor hex (a just-refreshed reflight was invisible before because its usefulness had dropped to ~0); hard reject compares `recentOverlap / corridorHexes` ≥ `airReconRedundancyRecentObsRejectFrac`; `ReconAirExecutor` now stamps the whole observed vision FOOTPRINT (`StampObservedFootprint`), not just the wing's centre hex. (P1 sector deconfliction) `OtherSectorClaims` now counts ground scouts in the wedge (`CountGroundReconActorsInSector`) as well as other air sorties, is populated for storage launches too (was always 0 without a `sortieState`), and adds a HARD reject when the wedge already holds ≥ `airReconSectorAdequateCoverage` (=2) recon actors AND the corridor's raw observation novelty ≤ `airReconSectorCoveredNoveltyFloor`; the soft divisor still applies from the first claim. **Review round 2 (P0/P1 + P1):** (self-block) `AirReconCoverageRegistry` replaces the identity-less `AiMapMemory.AirReconTargets` for V2 — every footprint stamp is tagged with a per-sortie `SortieId` (new field on `ReconAirSortieState`, monotonic), and `RecentlyCoveredByOther` excludes the querying sortie's own stamps. Without this an r1-Recce aircraft (`armyVisionRadius 0` + Recce r1 → footprint = hex + 6 neighbours) hard-rejected every follow-on adjacent step at 100% self-overlap and stalled after one step (also broke AI-AIR-02 continuation); a different sortie — incl. a second wing the same turn — still counts. (sector frames) all three claim sources unified: candidate wedge, other air sorties and ground scouts are now every `ReconDirectionModel.Sector(ourCitadel, liveArmyHex)` for any army holding a live `ReconAssignment` (`CountAssignedReconActorsInWedge` over `ArmyRegistry`) — one origin, live positions, no `WorldSnapshot` staleness, idle Recce (no assignment) excluded. The scorer's `EnemyInterest` / `EnemyCitadelDirectionValue` step-sector read moved to the same Citadel frame the anchor `SectorPressure` dict is built in. **Review round 3 (3×P1 — reservation-prepass ↔ executor parity):** (a) `SlotWouldFly` probed `ReconAirStepPlanner.Pick` with no `sortieState`, so an airborne wing's continuation scored its own previous-turn footprint as another sortie's coverage and the prepass could rate it "not usable capacity" while the executor flew it — new explicit `AirReconScoringContext` (`ExcludeSortieId` + `ProvisionalWedgeClaims`) is threaded into `Pick`/`PickFromStorage`/`BuildChoice`, and the prepass fills `ExcludeSortieId` from the wing's live `ReconAirSortieState.SortieId`. (b) `AirReconCoverageRegistry` was last-writer-wins per hex (`(turn, sortieId)`), so sortie B footprinting a hex sortie A just swept erased A's evidence and B then excluded its own id → hex looked un-covered; now stored per sortie (`hex → sortieId → lastTurn`, stale sources pruned lazily) so "a different sortie still counts" holds. (c) the prepass reserved air slots without any provisional sector claim, so a second reserved launch was scored as if the first didn't exist → `GuaranteedObservationLanes` that collapsed in execution; each accepted LAUNCH slot's chosen wedge (from our Citadel) is now recorded and fed into the next `SlotWouldFly` probe via the context (airborne wings exempt — already counted live). **Review round 4 (2×P1 — remaining scoring-parity gaps):** (a) `SlotWouldFly` still passed `sortieState = null`, so the scorer's trail-overlap penalty (`-0.30` for any adjacent step off a hex already in the trail) and lateral bonus never applied in the probe though they do in the executor — a continuing Outbound wing scored ~0.30 higher in planning than in execution (`MinimumUsefulScore` is only 0.15). New `ProjectScoringSortie` builds a read-only projection of the sortie state the executor will pass `Pick` (turn-start Hold-reopen / must-recover phase resolution mirrored WITHOUT `BeginTurn()`; a ready standalone wing gets the same fresh Outbound state seeded at its hex), and a projected `Hold` that would end the turn aloft returns "not capacity". (b) the prepass used one global `ReconMode` for every probe; the executor lets a durable `ReconAssignment.Mode` win for an already-assigned wing (Explore↔Refresh flips the `InformationGain` / `StaleIntelRefreshValue` weighting 1.0↔0.2). `SlotWouldFly` now resolves mode per slot exactly as the executor does. `BuildChoice` trusts a passed context's `ExcludeSortieId` verbatim (the prepass sets it deliberately per slot). **Review round 5 (2×P1):** (a) a `Hold`- or `Return`-phase projected wing is no longer counted as `ReservedAirborneWings` (Observation supply): once a sortie is Return-bound the executor ignores the AIR-01 forward `Pick` and flies `PickReturnStep` toward the airfield, so a strategic forward hex clearing `MinimumUsefulScore` in the probe reserved `ObservationDeficit` relief the executor never delivers — `SlotWouldFly` bails on both phases (the wing still holds its air-actor slot + recovery AP/Energy reservation, just not observation capacity). (b) `TrailAdjacency` now excludes the wing's current hex: `ReconAirSortieRegistry.GetOrCreate` seeds `Trail = [launchHex]`, so a ready standalone wing's first step off its own airfield was scored as "hugging the outbound trail" (`-0.30`) while a storage launch — scored with no sortie state — paid nothing, flipping admission for the same aircraft/airfield/target near `MinimumUsefulScore`. Real anti-retrace shaping (proximity to earlier trail hexes) is unaffected. Both assemblies build clean (0/0). NOT play-tested. |
| AI-AIR-02 | Two-turn airborne recon/strike planning | DONE — the spec's `AirSortiePlan` is folded into the existing `ReconAirSortieState` (no duplicate type): new `LaunchTurn` / `AirborneTurnIndex` / `LastProcessedTurn` / `MissionMode` (`ReconAirMissionMode` Recon/Strike/ReconStrike) / `MustRecoverThisTurn` / `LastDecisionReason`, plus `BeginTurn(turn)` that bumps the airborne-turn counter once per AI turn and reports the first call of a new turn. New `ReconAirPhase.Hold` (aloft on purpose, ending this turn here, re-decide next turn). New shared primitive `AiAviationSupport.CanSafelyEndTurnAirborne(air,map,owner)` = `SafeUnlandedEndsRemaining >= 1` AND a recovery plan exists now (same-turn `TryReplan` OR multi-turn `TryReplanMultiTurnReturn`) AND next turn's mandatory return still fits (same-turn route ⇒ trivially safe; multi-turn-only ⇒ `safeEnds-1 >= RequiredUnlandedEnds`); a plane (SafeUnlandedEndsRemaining==0) always fails it, so its single-turn boomerang is untouched. `ReconAirExecutor.RunActor`: (1) each decision re-derives `canRemainAirborne` + `mustRecoverThisTurn` (`AirborneTurnIndex >= 1 && !canRemainAirborne` — never on the launch turn, so same-turn boomerang logic still governs planes); (2) the Outbound `return_reserve` MP-reserve pivot is SUPPRESSED while `canRemainAirborne && !mustRecoverThisTurn` — the helicopter presses on with its whole first turn instead of reserving MP to fly home turn 1; `marginal_gain` (information saturation) still pivots; (3) `mustRecoverThisTurn` forces Outbound→Return at turn start (stranded/exhausted wing goes home); (4) `Hold` set on a prior turn re-opens (→ Outbound, or → Return if must-recover) with fresh MP; a `Hold` set earlier the same turn ends the sortie's turn aloft; (5) `TryOpportunisticAirStrike` no longer force-sets Return — after a favourable strike, if `CanSafelyEndTurnAirborne` still holds it sets `Hold` (second-strike window re-evaluated next turn — never forced), else Return. All safety still delegates to `ReconAirStepPlanner`/`AiAviationSupport` (every accepted step already carries a proven full round trip). **Review (3×P1 + P2) @ `659ba79`:** (P1 recovery feasibility) `CanSafelyEndTurnAirborne` double-counted this turn's EndTurn — `TryReplanMultiTurnReturn` already simulates from NOW and only succeeds when every unlanded end it needs (this turn's included) fits the live `SafeUnlandedEndsRemaining` margin, so `safeEnds-1 >= RequiredUnlandedEnds` subtracted it twice and rejected valid windows (1-turn-endurance heli: out turn N, home turn N+1). Now `HasValue` ⇒ return true; plus an explicit `CurrentMovement==0` branch (post-strike) that `TrySimulateHexSequence` can't express ("end turn here, return next turn on fresh MP"), verified via `CanStrikeNextTurnAndLand`. (P1 Hold strike re-eval) the Hold re-open now runs `TryOpportunisticAirStrike` at the CURRENT hex before moving off — per-turn attack availability has refreshed, target may still be here, second strike still only an option; previously it converted Hold→Outbound/Return and only struck AFTER stepping away. (P1 turn-index drift) `AirborneTurnIndex` incrementing counter → `AirborneTurnsElapsed(currentTurn) = currentTurn - LaunchTurn`; `LaunchTurn` set authoritatively at storage launch (survives an all-MP-spent launch step that skips `RunActor`), conservative `currentTurn-1` for a resumed airborne wing with no state; a ready wing that never left its airfield retires its half-init sortie state; `ReconAirReservation.ProjectScoringSortie` uses the same arithmetic. (P2 telemetry) added the missing `Hold->Return reason=must_recover` log line. **Review round 2 (P1 — wrong recovery lifecycle) @ `6a28eff`:** the round-1 fix still proved the wrong thing — `TryReplan` / `TryReplanMultiTurnReturn` spend THIS turn's remaining `CurrentMovement` toward home on their first simulated turn, but a `Hold` freezes the wing where it is, so that movement never happens (planning↔execution mismatch — the wing could pick Hold from a hex it could only recover from by flying MP it then never flew; more dangerous false-positive than the old false-negative). Renamed `CanSafelyEndTurnAirborne` → **`CanEndTurnHereAndRecover`**, rewritten to prove the actual Hold lifecycle: `SafeUnlandedEndsRemaining >= 1` (this turn's airborne EndTurn legal), then from the CURRENT hex a route to a capacity-OK / no-new-known-AA owned airfield that lands within `SafeUnlandedEndsRemaining - 1` more unlanded ends, each future turn simulated with the group's refreshed `EffectiveMoveMax` (reuses `TrySimulateHexSequence` with `firstTurnMovement = EffectiveMoveMax`, `safeRemaining = margin - 1`). Handles 2+-turn-endurance recovery (which `CanStrikeNextTurnAndLand` alone couldn't). Net effect: outbound wings push out exactly to "within N fresh-turns of an airfield" then pivot; the post-strike Hold is only taken from a genuinely recoverable position. All 3 call sites updated. Both assemblies build clean (0/0). NOT play-tested. |
| AI-MGR-01 | Strategic hand/card evaluator | DONE (not committed) — new `StrategicCardEvaluator.cs` (Assets/Scripts/Ai/V2/): `IntendedRole` (full 13-value enum; Scout / CombatBody / ForceGrowth / EquipmentUpgrade / Support / Hold carry real signal, AA/AT/MobileCombat/Aviation/CapabilitySpecialist/Economy/Development declared but neutral), `StrategicUseScoreBreakdown` (15 named terms), `StrategicCardUseCandidate` (`NetScore = TotalUseScore − HoldValue`). `ScoreForDemand` (Phase A) and `ScoreSurplus` (Phase B, one card → several Card×Role candidates, best NetScore + winning role) are now the SOLE scoring path: `MaterializationCandidateBuilder.ScorePlanA` / `SurplusUtility` are thin wrappers, and `SurplusCombatReadinessUtility` / `EquipmentUpgradeUtility` / `SurplusScarcity` / `GarrisonSaturationPenalty` / `ScarcityOpportunityCost` (+ helpers) moved into the evaluator. Hero card CLASS adds no flat bonus/penalty — `HeroLeadershipScore(def)` mirrors `HeroRoleEvaluator.CombatLeadershipScore` off the definition; the only hero cost is `AlternativeUseValue` when a scarce hero is spent off its best use. `BaselineForceReadiness` (radar-demand-independent: fielded power / combat-actor count / capability coverage vs game stage + economy + known enemy) feeds `ForceGrowthValue` (an ordinary body scores > 0 at AGG=0/DEF=0) AND `DemandLayer.BaselineForceReadinessDemands` — ONE low-`Value` FieldCombatPower demand charged to Defence, suppressed when an Aggression/Defence combat demand already exists, when Need < `baselineReadinessDemandMinNeed`, or when free field power + actors already suffice. New `AiConfigV2` section (`forceGrowthValueWeight`, `capabilityGapValue`, `baselineReadiness*`, `hero*Fit*`, `hold*`, …), all first-pass. `MaterializationPlan.UseBreakdown` / `UseRole` added (diag only). Both Unity assemblies build clean (0/0). NOT play-tested. (Pre-existing unrelated failure in capability-quality-sim test 16 `ScoutOptionalStealthPolicy` — outside MGR-01 scope, from the uncommitted AIR work.) |
| AI-MGR-02 | End-of-turn tempo spending | DONE — pushed (`3f0c55d`). New `StrategicResourceReservation.cs` (Assets/Scripts/Ai/V2/): `StrategicResourceReservation` (Owner / Reason / Resource / Amount / ExpirationStage) + `StrategicResourceReservationLedger` (per-player, turn-keyed like `StrategicInterruptRegistry`): `Reserve` / `Active` / `SpendableAp` (= Total − Σ ActiveReservations) / `ReleaseByReason` (immediate release for a Suppressed/NoAction/Invalidated/Skipped owner) / `ExpireStage` / `BeginTurn`. `StrategicReactionPass` gains `CanStrategicReactionPassRun` (false in ReconOnly scope / no map) + `HasActionableStrategicReaction` (pending invalidation AND a resolvable hand). `StrategicManager.UseSurplus` §4 fix: the old blanket `if (HasPendingDiscovery) { preserve N AP; return; }` is replaced by `ReactionPassWillReserve(player,ctx)` = `HasPendingDiscovery && CanStrategicReactionPassRun && HasActionableStrategicReaction` — only then is an EXPLICIT `StrategicResourceReservation` (owner=StrategicReactionPass, AP, expire=EndOfReaction) placed and Phase B returns; otherwise Phase B logs "NOT preserving AP" and continues. The non-combat-surplus gate (was `!HasPendingDiscovery`) now uses `!ReactionPassWillReserve`, so ReconOnly / non-actionable interrupts no longer suppress non-combat + terminal draws. `StrategicReactionPass.ExecuteIfPending`: a genuinely-run round `ExpireStage(EndOfReaction)`; a scope-suppressed round deliberately LEAVES the reservation. `HousekeepingManager`: after `ExecuteIfPending`, if the pass did not run and `ReleaseByReason(StrategicReactionPass)` freed a stranded reservation, it re-runs `StrategicManager.UseSurplus` once (`AiConfigV2.maxEndOfTurnTempoReruns=1`) so the freed AP is offered to Play/Draw again the same turn — closes the "10 AP stranded because ReconOnly suppressed the reaction pass" bug. `StrategicResourceReservationLedger.BeginTurn` wired into `Pipeline.RunTurn`. New csproj `<Compile>` entry. Both Unity assemblies build clean (0/0). NOT play-tested. |

Suggested order (dependency-driven): INTEL-01 → RECON-02 → RECON-01 → AIR-01 → AIR-02,
then MGR-01 → MGR-02 (MGR track is file-independent from recon/air except `StrategicManager`).

### Relevant existing code (as of commit 94c1d5b)

- Air: `ReconAirExecutor`, `ReconAirStepPlanner`, `ReconAirSortieState`/`Registry` (phases
  Outbound/Turning/Return/Landing, army-id keyed, retired on land/loss), `ReconAirEnergyPolicy`,
  `ReconReactionPolicy`, `ReconDirectionModel` (sanitized TrueWorld direction: sectors +
  known enemy Citadel + own-asset watch dirs), `ScoutRouteCostEvaluator`, `AiAviationSupport`.
- Recon planning: `DemandLayer.ReconDemands`, `ReconConcurrencyPolicy` (DesiredTotal/HardCap),
  `ReconMissionPlanner`, `ScoutMoverSelector`, `ScoutAdmissionRegistry`, `ProvisioningManager`
  (`PreparePass` injective mover→mission assignment), `ActorCommitments`, `ReconAssignment`,
  `MissionContinuityLayer`, `MissionAdmissionPolicy`.
- Intel: `AiReconIntelMemory`, `AiMapMemory`, `AiReconMemory`, `ReconObjectiveEvaluator`,
  `ReconIntelSnapshotRegistry`, `LogVisitedInvariant`.
- Manager: `StrategicManager` (Phase A / Phase B / RunNonCombatSurplus / RunTerminalDraws /
  UseSurplus), `NonCombatCardPlayer.BestPlay`, `MaterializationCandidateBuilder`,
  `HeroRoleEvaluator`, `CapabilityInventory`, `CapabilityQualityEvaluator`, `AiCardCost`,
  `AiResourceReservation`.

---

## AI-AIR-01 — Strategic Air Recon Target & Route Selection

Work in: `ReconAirExecutor`, related target selection / route planning, `AiAviationSupport`,
Recon demand / intel data.

### Problem

Air Recon must not fly to a hex just because `GroundVisited == false` or the hex is
unexplored. Aviation occupies nothing physically and must not be used as a cheap substitute
for ground exploration. A sortie must have a strategically justified direction, and its value
must be scored for the **whole route**, not just the final hex.

### Implement

Introduce a dedicated air-route candidate object, e.g.:

```
AirReconRouteCandidate
{
    ObjectiveHex
    Route
    StrategicAnchor
    InformationGain
    StaleIntelRefreshValue
    EnemyInterest
    FriendlyFacilityCoverValue
    EnemyCitadelDirectionValue
    RouteObservationValue
    CombatOpportunityValue
    TravelCost
    ActivationCost
    RecoveryRisk
    RedundancyPenalty
    TotalScore
}
```

**1. Form the StrategicAnchor first.** Air Recon takes its direction from strategic
landmarks. Priority sources:

- **Enemy concentration** — known concentration; probable concentration; hidden
  concentration from omniscient state.
- **Enemy Citadel** — known Citadel; if not formally discovered yet, its real position may
  be used as a hidden directional bias.
- **Friendly production/resource facilities** — own extracting facilities; sectors adjacent
  to them; probable enemy approach directions; hexes with stale data around important
  facilities.
- **Enemy movement corridors** — the space between enemy Citadel / enemy armies / own
  valuable objects; approaches to the front; potential offensive routes.
- **Intel refresh** — strategically important hexes with stale information.
- **Unknown frontier** — use only after more meaningful directions, or when information gain
  is genuinely high.

**2. Cheat knowledge only for choosing direction.** Allowed: hidden enemy army at sector X
→ raises `EnemyInterest(X)` → aviation more often routes that way. Forbidden: hidden enemy
at X → AI intel automatically treats it as discovered. Real knowledge appears only after
actual observation.

**3. Score the whole route.** For each route compute
`RouteObservationValue = Σ usefulness of hexes actually observed en route`. A route
`Airfield → stale area → enemy approach → facility perimeter → objective` may beat a shorter
direct route. Weigh especially: new observations; stale-intel refresh; crossing a probable
enemy corridor; chance to spot the enemy; potential attack position.

**4. Composite score** (weights need not be equal, keep the components separate):

```
TotalScore =
    InformationGain
  + StaleIntelRefreshValue
  + EnemyInterest
  + EnemyCitadelDirectionValue
  + FriendlyFacilityCoverValue
  + RouteObservationValue
  + CombatOpportunityValue
  - TravelCost
  - ActivationCost
  - RecoveryRisk
  - RedundancyPenalty
```

**5. Hard rules.** Reject an air route if: its only value is `GroundVisited == false`; it
almost entirely repeats a recently completed air observation; the same sector is already
adequately covered by another assigned Recon actor; sortie cost exceeds its
informational/strategic value; no valid recovery plan can be kept.

**6. Recon demand link.** Air Recon requests task types `Observation`, `IntelRefresh`,
`StrategicSurveillance` — never `GroundVisit`, `GroundPresence`, `Occupation`, `Capture`.

---

## AI-AIR-02 — Two-Turn Airborne Recon/Strike Planning

Work in: `ReconAirExecutor`, sortie continuation/return decision, `AiAviationSupport`,
existing aviation state.

### Problem

Today the observed behaviour is effectively `Launch → Recon → objective → return_reserve →
base` in a single turn. For a helicopter this is too conservative. Its ability to stay
airborne two turns should create a tactical window, not just extra theoretical range.

### Implement persistent sortie state

```
AirSortiePlan
{
    SortieId
    ActorId
    LaunchTurn
    AirborneTurnIndex

    HomeHex
    Objective
    MissionMode          // Recon | Strike | ReconStrike (minimum)

    RemainingRoute
    RecoveryPlan

    MaxAirborneTurnEnds
    MustRecoverThisTurn

    LastDecisionReason
}
```

Do not duplicate existing aviation gameplay rules. Use the existing state/rules:
`TurnsWithoutRefuel`, `ConsecutiveUnlandedEnds`, `HasAirAttackedThisTurn`, actual
landing/refuel feasibility, actual movement/attack state.

### Sortie decision loop

On each air decision: observe current state → `EvaluateAttack`, `EvaluateContinueRecon`,
`EvaluateHoldAirborne`, `EvaluateReturn`. After every move or intel change, re-plan with
live state.

**First sortie turn.** If the helicopter can still safely survive EndTurn, do **not**
automatically reserve movement to return home this turn. Instead:

```
CanSafelyRemainAirborne =
    endurance allows EndTurn
    AND a realistic recovery plan exists
    AND next turn allows the mandatory return
```

If yes, return_reserve must not force the sortie to end.

**Enemy encounter.** When a suitable target appears on route, compare `AttackUtility`,
`ContinueReconUtility`, `HoldAirborneUtility`, `ReturnUtility`. If attack is favourable:

```
Turn N:   Recon → enemy discovered → Attack → EndTurn airborne
Turn N+1: per-turn attack availability refreshes → re-evaluate target
          → possible second Attack → Recover
```

Do not force a second attack. It is only an option — it happens only if a fresh state
evaluation still rates it favourable.

**Mandatory return.** `MustRecoverThisTurn` is driven by the real endurance deadline:

```
MustRecoverThisTurn =
    one more airborne EndTurn after this turn would be illegal
    OR after continuing, no valid recovery route would remain
```

Only then does return become a hard priority.

**Recovery planning.** The plan must consider not only the home hex but actual valid
recovery/landing positions if the game supports them. On each sortie continuation, check
that after the intended action a legal recovery path to exhaustion still remains; if not,
exclude that action.

**Core invariant.** The helicopter must not return the same turn just because it has MP to
return. It returns when returning now is strategically better, OR when endurance/recovery
safety requires it.

---

## AI-MGR-01 — Strategic Hand/Card Evaluator

Work in: alongside `StrategicManager`; the evaluator must be used by both Phase A and
Phase B. This is the key manager task.

### Goal

Stop treating a card only as "fits / doesn't fit the current demand" and move to
`whole hand × all reasonable uses of each card × current and future strategic situation`.
The new evaluator becomes the shared source of the decision: how useful is it for the AI to
play a given card now, for what purpose, or why is it better held.

### 1. Score Card × IntendedUse

```
StrategicCardUseCandidate
{
    Card
    IntendedRole
    TargetContext
    ResourceCost
    Preconditions
    ImmediateActionsUnlocked
    ScoreBreakdown
    TotalUseScore
    HoldValue
}
```

One card yields several candidates, e.g. `Nora × Scout`, `Nora × CombatSupport`,
`Nora × Hold` — not `Nora = hero → preserve`.

**Intended roles** — not hard-bound to card class; derived from capabilities. Minimum set:
`Scout, CombatBody, MobileCombat, AntiArmor, AntiAir, Aviation, Support,
CapabilitySpecialist, Economy, Development, EquipmentUpgrade, ForceGrowth, Hold`. Other real
mechanical roles must reach the evaluator via a capability/provider model, not a giant
switch over card names.

### 2. Score breakdown — computed independently per Card × IntendedUse

`RoleFit, ImmediateTempo, NextTurnPotential, CapabilityGapValue, ForceGrowthValue,
ThreatResponseValue, ResourceEfficiency, SynergyValue, Deployability, ScarcityValue,
RedundancyPenalty, AlternativeUseValue, HoldValue, ResourcePressureBenefit,
HandPressureBenefit`.

- **RoleFit** — how well the card's real characteristics fit this role. For Scout, actual
  scout/recon capabilities, mobility and related properties — this is why Nora naturally
  gets a high Scout score. The Hero category by itself adds neither a HeroBonus nor a
  HeroPenalty.
- **ImmediateTempo** — what the AI gains before the end of this turn (ready mover appeared;
  capability closed; a mission became doable; available combat force grew; equipment can be
  attached right now; an economy card produces its effect immediately).
- **NextTurnPotential** — what the card practically opens next turn (another active army;
  extra capability; offensive/defensive potential; a recon actor; aviation readiness; a
  prepared combination).
- **CapabilityGapValue** — if the AI currently lacks Recon / AA / AT / air capability /
  combat body / …, a card closing that deficit gets a substantial bonus.
- **ForceGrowthValue** — critical for ordinary units. Even with `AGG = 0` and `DEF = 0` an
  ordinary combat unit must not score utility ≈ 0 just because there is no combat mission
  right now. Value it as a contribution to standing force / future mission capacity /
  combat mass / next-turn readiness.
- **ThreatResponseValue** — may use normal AI intel; when the model allows, omniscient enemy
  composition as a strategic bias (e.g. a large armor group raises the value of future
  AntiArmor capability). A hidden army still must not become visible intel automatically.
- **SynergyValue** — combination with already-played units; equipment for a suitable carrier;
  presence of a prerequisite; generated cards / production synergies; a future combination
  with another card in hand.
- **RedundancyPenalty** — if a capability is already heavily saturated (4 Scouts + another
  mediocre Scout) the extra one is worth less. A strong specialised Scout may still beat an
  ordinary combat use.
- **Deployability** — a card must not have high ImmediateTempo if it actually cannot be
  played now: missing prerequisite; no suitable equipment target; no slot/limit;
  deployment creates an actor that still can't act.
- **ScarcityValue** — unique capabilities raise the value of holding the card. Not "Hero as
  a class" but a rare capability / rare counter / rare interaction.
- **AlternativeUseValue** — if one card is excellent for several directions at once, using it
  in one place has an opportunity cost. Replaces `heroOpp` with a general
  `AlternativeRoleValue`. For Nora, if Scout is genuinely her best use, this cost may
  correctly be near zero.

### 3. Score HoldValue separately

```
HoldValue =
    UniqueFutureRole
  + NearTermExpectedDemand
  + ScarcityValue
  + ComboPreservation
  - HandPressure
  - ResourcePressure
  - LostTempo
```

The AI may deliberately decide not to play a card now — but as the result of an evaluation,
not the absence of a current demand.

### 4. Baseline Force Readiness inside the evaluator

Add a radar-demand-independent signal `BaselineForceReadinessNeed`. It does not mean
"prepare to attack"; it means the AI state must continuously maintain a reasonable potential
for future tasks. Compute from at least: current field-army strength; available combat mass;
number of combat-capable actors; capability coverage; game stage; economy; enemy
strength/development; current hand; already-prepared units. It creates utility for
`CombatBody, AA, AT, MobileCombat, Support, …` even at `AGG = 0`. This is what fixes the
current case where an ordinary unit sits in hand because no current Recon mission requested
it. It only decides that a card is worth materialising — not which army/garrison it goes
into.

### 5. All card types go through one evaluator

No separate fully independent heuristic for Unit / Hero / Aviation / Equipment / Generated
Card / Economy-Development. Let the categories supply available intended uses / capabilities
/ costs / prerequisites / synergies, and compute strategic value with a shared model.
Important for later Equipment and Generated-Card integration.

### Review follow-up (2026-09-04) — 8 findings

Base impl `e2d76ee`. Review found the evaluator was not yet truly unified. Fix commits:
`32ddfa6` (P0.1 pt1), plus the batch below.

1. **P0.1 — non-unified card types.** `StrategicCardEvaluator.ScoreNonCombat` (+`NonCombatRole`)
   scores Aviation / Base / Facility / standalone Equipment on the SAME
   `StrategicUseScoreBreakdown` / `NetScore` band as every Unit/Hero chain; `NonCombatCardPlayer`
   drops the fixed 55/45/40/24 scale and picks the equipment host by carrier power (name only as
   tie-break) — "rename unit → different carrier" is gone; `StrategicManager` non-combat lane
   gated on the shared `surplusUtilityThreshold`. **Deferred (needs executor work, not scoring):**
   generated *non-combat* cards — `BestSurplus` still skips `gd.isAviation` because
   `MaterializationExecutor` only bodies unit/hero chains; a generate→aviation/base deploy path is
   its own task. Phase A infra stays a pre-pass **by design** (charged to its own axis entitlement,
   so it never contends for a Unit demand's AP); the `*CandidateProvider` rename is pure churn now
   that scoring is unified and was skipped.
2. **P0.2 — Hold as a real Phase A competitor.** `BestForDemand` now returns `null` (keep card in
   hand, demand stays residual) when the best chain's play score does not beat `HoldValue` AND the
   demand is soft (`Value < stratHoldBeatsPlayMaxDemandValue`). An urgent demand (real threat /
   raid gap) is never vetoed by Hold. Logged `strat.A hold — …`.
3. **P1.3 — phantom card capacity.** `BestForDemand` takes `excludeCards` / `excludeGenKeys`;
   `StrategicManager.FulfillDemands` runs a card-instance deconfliction BEFORE arbitration —
   highest-priority demand claims its best chain's hand cards / generation source first, a lower
   demand whose best chain collides re-picks the best chain that avoids the claimed instances (one
   bounded re-pick each). Full max-weight bipartite assignment (prefer the globally-higher total
   when the top demand should take its cheap fallback) is a further refinement; this closes the
   common failure trace.
4. **P1.4 — evaluator score is final.** One `SumTotal(breakdown)`; every factor priced once:
   AP + resource + chain-step in `ResourceEfficiency` only, generation chance in `Deployability`
   only, placement (incl. the Phase-B garrison-surplus correction, folded in via
   `SurplusPlacementBonus`) in `ImmediateTempo` only. `MaterializationPlan.Score` is now a plain
   field; `BestSurplus` no longer re-subtracts attach/generation penalties or re-applies the
   success-chance multiplier. `SurplusAdmissionPolicy` stays the single stranded-AP *admission
   threshold* layer (not a score edit) — documented boundary.
5. **P1.5 — Phase A/B parity.** One `RoleFit(role, …)` path used by both phases; Phase B Scout
   runs the same `CapabilityQualityEvaluator` profile via a neutral synthetic scout demand.
   `surplusHeroVersatility`/`surplusUnitVersatility` dropped — versatility is now
   `RoleVersatility(roles)` = value per *real viable role* beyond the first, identical for a Hero
   and a Unit with the same role count.
6. **P1.6 — real Hold / AlternativeUse terms.** `AlternativeUseValue` on the Phase-B winner =
   `altUseForegoneFraction × max(next-best role score, HoldValue)` plus the scarce-body floor.
   `NearTermExpectedDemand` in `HoldValue` fires for a specialist counter (AntiAir/…) whose
   triggering threat is already visible.
7. **P1.7 — readiness signal.** `BaselineForceReadiness` now takes the hand (a strong hand
   Unit/Hero counts as prepared standing force, `baselineReadinessHandBody*`), `HasScout` counts
   toward coverage, and `Need` is priced ONCE — the baseline `AxisDemand.Value` is a flat low
   constant (was `× Need`, which triple-counted through `Value × Plan.Score`); `CapabilityGapValue`
   is binary, not `× Need`. Per-capability AA/AT/Air/Mobile vector still needs deployed-force
   composition data the snapshot lacks — `HasAir` added, the rest noted.
8. **P2.8 — no synthetic armor.** `ThreatResponseValue` drops the `× 0.35` ground-power→armor
   synthesis. AntiAir works off the real `IsAir` classification; AntiArmor contributes 0 until the
   snapshot carries enemy unit composition.

### Review round 2 (fixes on top of `4385dc6`)

- **P1.6 Hold double-count** — `AlternativeUseValue` on the Phase-B winner is now the next-best
  *PLAY* role only; Hold is priced exclusively in `NetScore`. And `HoldValue` is a property of the
  CARD: `win.HoldValue = max over all viable roles` (an AA-capable unit played as CombatBody still
  carries its "keep as a rare AA counter" hold value).
- **P0.2 net ordering + urgency in the equation** — Phase A now RANKS candidates by the
  opportunity-adjusted net decision value `Score − HoldValue + UrgencyBonus(demand.Value)` and
  plays only when it is `> 0`. So a plan with a lower play score but a much lower hold value wins,
  and the reverse trap (top-by-raw-score gets Hold-vetoed while a great B is never considered) is
  gone. The hard `Value ≥ 40` switch is replaced by `stratHoldUrgency*` — a real threat lifts
  every net value; a soft baseline demand adds ~nothing.
- **P1.5 Phase A parity for real** — one `RoleFit(role, …)` path used by BOTH phases. Phase A no
  longer relies on `CapabilityQualityEvaluator.QualityMultiplier` returning 1f for every non-Scout
  role: a commandRating-10 hero now out-fits a commandRating-2 hero in a Phase A Hero demand
  (`HeroLeadershipFit` + AiPower marginal readiness).
- **P1.3 scarcity-aware assignment** — the instance deconfliction now orders by *fewest
  collision-free alternatives first* (mirrors the recon actor-reservation pattern): a demand whose
  best chain has NO alternative claims its cards before a higher-priority demand that has a
  fallback, so both get satisfied (`D1{A,B}` + `D2{A}` → `D2→A`, `D1→B`). Full max-weight
  assignment is still a further refinement.
- **Standalone Equipment real delta** — `EquipmentUpgradeUtilityFor(equipDef, UnitData host)` runs
  `EquipmentSystem.Predict` against the live carrier's stats; `NonCombatCardPlayer.BestEquipmentHost`
  picks the `(equipment, host)` pair that maximises that predicted before/after delta, and the
  same delta is the RoleFit — not the carrier's raw power. A +Range trinket and a +Attack/+Defense
  item on the same unit now score differently.
- **P0.1 aviation bypass removed** — the Phase-B non-combat threshold gate no longer excepts
  Aviation, and the dedicated aviation slot is gated on the evaluator score. The evaluator, not
  the card type, decides whether a stored aircraft is worth playing.

### Review round 3 (fixes on top of `0b440a8`)

- **P0 — DecisionScore reaches cross-demand arbitration.** `MaterializationCandidateBuilder`
  returns `DemandCandidate {Plan, FollowupAp, PlayScore, HoldValue, DecisionScore}` — `DecisionScore
  = Play − Hold + UrgencyBonus(demand.Value)` computed ONCE. `PhaseACandidate` carries it;
  `ArbitrationScore` is now just `c.DecisionScore` (the old `demand.Value × raw Play` is gone —
  urgency is the single place demand.Value enters the decision).
- **P1.3 — top-K + bounded injective assignment.** `TopForDemand` hands the manager up to
  `phaseATopK` scored chains per demand; `BestInjectiveAssignment` picks one collision-free chain
  per demand maximising total `DecisionScore`, so `D1{A,B} + D2{A}` resolves to `D1→B, D2→A`, and
  the multi-card case (`D1{A+E1, A+E2}`, `D2{B+E1}`) resolves to `D1→A+E2, D2→B+E1` because both
  equipment variants are in D1's top-K. Branching `(K+1)^activeDemands`, trivial.
- **P1.6a — Hold is not a pseudo play role.** `DeriveRoles` no longer emits `IntendedRole.Hold`;
  `ScoreSurplus` iterates only real play roles, so Hold can never be `scored[0]` and get executed,
  and `secondBestPlay` can never be Hold. Hold is priced only via `CardHoldValue` / `NetScore`.
- **P1.6b — one card-level HoldValue for both phases.** `CardHoldValue(plan, viableRoles, …)` =
  max reason-to-hold across every viable role. Phase A now uses it too (was `HoldValue(plan,
  currentDemandRole)` — an AA-capable card serving a FieldCombatPower demand missed its "keep as a
  rare AA counter" value).
- **P1.7 — Phase A is hand-aware.** `ScoreForDemand` passes `snap.Self.Hand` to
  `BaselineForceReadiness.Evaluate` (was the hand-blind overload). The hand analysis uses
  EFFECTIVE abilities (`EquipmentSystem.EffectiveAbilities` — card + attached equipment).
- **P1.7 — real coverage vector.** `ArmySnapshot` gains `HasAntiArmorUnit / HasSupportUnit /
  HasMobileUnit`, derived DYNAMICALLY from member abilities (`Hyperkinetic` / `ApBonus`·`Researcher`·
  `Assembler` / `moveMax`) in `WorldAnalysis.ToArmySnapshot`. `BaselineForceReadiness` exposes
  `{HasAntiAir, HasAntiArmor, HasMobile, HasSupport}`; `SurplusCapabilityGap` gives a real gap
  bonus for closing an AA/AT/Mobile/Support hole (AA/AT only when the matching enemy threat is
  actually present).

~~Still open (architectural, own tasks): P0.1 full Phase-B single-candidate-set; P0.1 generated
non-combat cards.~~ Both closed by review round 4 findings 9a / 9b (below). Remaining P0.1 residue:
**Phase A infra generation** (generating a Base/Facility to satisfy a Phase-A EconomicInfrastructure
demand — the infra pre-pass is deliberately left as-is) and generated standalone **Equipment** with
no in-hand host (the `GenerateAttachDeploy` chain covers generated equipment WITH a host).

### Review round 4 (fixes on top of `72d95a2`)

Findings 1–9 done (build clean on both assemblies; capability-quality-sim 71/1, radar-sim 9/1,
housekeeping-sim 55/0 — the fails are pre-existing on `72d95a2`. commitment-sim / mission-selection-sim
also fail identically on clean HEAD, unrelated).

- **1 — DecisionScore is the final arbiter.** `StrategicManager.BestInjectiveAssignment` now models
  the shared per-turn generation attempt + the AP / H-E-M-T pools, so the portfolio it returns is
  JOINTLY feasible, not just card-disjoint. The `selected` pick then ranks purely on
  `ArbitrationScore` (= `DecisionScore`); the hidden `CapabilityResourcePriority` (Hero 3 / Scout 2
  / Field 1 / Garrison 0) pre-sort and `ConsumesResourceNeededByHigherPriorityDemand` /
  `ConsumesTraitRequiredByOtherFeasibleDemand` heuristics are deleted — a Hero chain can no longer
  veto a higher-DecisionScore Field chain by "protecting" a resource.
- **2 — top-K over consumption SIGNATURES.** `MaterializationCandidateBuilder.TopForDemand` dedups
  `ranked` by `ConsumptionSignature` (StableKey minus its trailing `Deploy.Key` segment — the lead
  segments already encode chain kind / capability / base-card hand index / equipment hand index /
  generation CardKey) before the K-cut, so `A@army1, A@army2, A@garrison` no longer eat every slot
  and a fallback card B survives into the injective assignment (`D1{A,B}+D2{A}` → `D1→B, D2→A`).
- **3 — no phantom generation / resource capacity in the assignment.** (folded into finding 1)
  `BestInjectiveAssignment` tracks `genUsed` vs `genAttemptsRemaining` (= `maxGenerationActions
  PerTurn − Reservation.GenerationAttemptsUsed`) and running AP + per-resource totals vs
  `AiResourceReservation.Available`, rejecting any branch that overspends the joint pool.
- **5 — AntiArmor Hold works.** `ThreatResponseValue` handles BOTH counter roles off omniscient
  `TrueWorld` composition (real `IsAir` for AntiAir, a real `Armored`-tagged member for AntiArmor);
  `EnemyArmorPresent` deleted, `SurplusCapabilityGap` AntiArmor uses the same primitive. So a rare
  AntiArmor card with a visible enemy armour group and no other AT now carries a real
  `NearTermExpectedDemand` hold value.
- **6 — Hold formula completed.** `HoldValue` adds spec §3's missing `ComboPreservation`
  (`holdComboPreservationValue` — a still-unattached Equipment card in hand that legally fits this
  body raises the BARE variant's hold value) and `ResourcePressure` (`holdResourcePressurePenalty`
  × `EconomicSecurity` — a secure economy lowers the value of hoarding by holding; proxy until the
  snapshot carries per-resource cap data).
- **7 — generator ≠ phantom card.** `HoldValue` returns 0 for a GENERATED-deployable plan
  (`GeneratedBaseDef != null && BaseCardInHand == null`): declining the chain preserves the
  generator option + resources + the turn's Challenge, not a scarce physical card, and the play
  score already carries the generation step penalty + success-chance discount.
- **8.1 — coverage flags before the recce short-circuit.** `BaselineForceReadiness.Evaluate`'s hand
  scan reads AA/AT/Support/Mobile flags for a card BEFORE `continue`-ing on recce, so a Scout+AntiAir
  card counts as AA coverage (parity with `DeriveRoles`, which gives it both roles). recce still only
  excludes the card from `handReadyBodies`.
- **8.2 — effective stats for hand bodies.** New `AiPower.EffectiveLine(def, attachedGrant)` folds
  an already-attached equipment's STATS (not just abilities) via `EquipmentSystem.Predict`; the hand
  scan uses its `BasePower` for the readiness power floor and its `MoveMax` for mobile coverage.
- **8.3 — Mobile Hero parity.** `WorldAnalysis.ToArmySnapshot` `HasMobileUnit` no longer excludes
  heroes (`DeriveRoles` offers MobileCombat to any non-recce body with the moveMax, heroes
  included), so a deployed mobile hero closes the mobile-coverage hole.
- **9a — Phase B is ONE per-iteration ranked pick.** `UseSurplus` no longer runs the whole
  materialization-surplus loop and THEN the whole non-combat loop. One loop: each iteration
  `ComputeMatDecision` (the mat lane's per-iteration verdict, factored out — admissible chain +
  utility + any operational residual, or a not-admissible verdict with its defer log) is ranked
  against `NonCombatCardPlayer.BestPlay` on the SAME NetScore band; the higher one executes
  (an operational strategic residual is must-do and always wins), then state refreshes. The
  separate `RunNonCombatSurplus` + its `surplusNonCombatReservedActions` reserved slots are gone;
  a single `RunDedicatedAviationSlot` is the only guaranteed extra (a stored aircraft the shared
  budget did not reach, still evaluator-score-gated, skipped if an Aviation card already went out
  in-loop). `surplusNonCombatReservedActions` marked DEPRECATED in `AiConfigV2`.
- **9b — generated non-combat cards.** `MaterializationExecutor.TryGenerate` factored out (the R/P
  mint step: eligibility, hand slot, afford, Research reveal, ResourceCost-only, probabilistic
  Challenge, mint into hand). `NonCombatCardPlayer.BestPlay` takes an optional
  `MaterializationReservation`; when present it enumerates `GenerationSource` steps whose minted
  card is Aviation / Base / Facility, scores each on the same `ScoreNonCombat` NetScore band via a
  pre-mint stand-in `CardData`, discounted by the Challenge success chance
  (`stratChainGenerationChanceFloor` lerp) + `stratChainGenerationStepPenalty`. `NonCombatCardPlayer`
  per-card logic extracted into `BuildPlayFor` (reused by real + generated paths); `Execute` mints
  via `TryGenerate` then re-resolves the placement against the real instance. `StrategicManager`
  records the generation attempt + telemetry on a successful generated non-combat play, so
  `maxGenerationActionsPerTurn` still bounds it. Generated **Equipment** stays with the
  materialization `GenerateAttachDeploy` chain; **Phase A** infra generation is still out of scope
  (the infra pre-pass is deliberately left as-is).

### Review round 4 follow-up (fixes on top of `b6e4b80`)

- **P1 — generated non-combat resource score.** The pre-mint stand-in's
  `EffectivePlayResourceCost` is null (correct for an already-paid R/P card), so `ScoreNonCombat`
  saw a `generate → Base(8 Tech)` as resource-free while `TryGenerate` really pays that cost, and
  the caller then post-multiplied by the success chance / subtracted the step penalty (evaluator
  not authoritative). `ScoreNonCombat` now takes an optional `GenerationStep`: it folds the
  `generation.CardDef.resourceCost` into `resSum`, prices `Deployability = -(1 - genChance)` and
  the `stratChainGenerationStepPenalty` into `ResourceEfficiency`, and returns the final comparable
  `NetScore`. `NonCombatCardPlayer` drops the external `score * chance - penalty`.
- **P1 — generated non-combat partial-failure lifecycle.** `NonCombatCardPlayer.Execute` returns a
  `NonCombatExecuteResult { Played, StateChanged, GenerationAttempted, Generated, ApSpent,
  FailReason }` instead of a bare `bool`. A lost Challenge (resources spent / Researcher revealed)
  and a mint-then-deploy-fail (card kept in hand) now propagate `StateChanged` + the spent
  generation attempt to `StrategicManager`, which sets `result.StateChanged`, calls
  `RecordGenerationAttempt` (no retry), bumps `GeneratedCardAttempts` (and `…Succeeded` only if the
  mint won), refreshes the snapshot, and then stops — parity with a failed materialization chain.

### Review round 4 P1 ARCH — capability/effect descriptor layer (DONE)

New `StrategicEffectRegistry.cs` (Assets/Scripts/Ai/V2/) — the ONE place ability/stat → strategic
value knowledge lives. `StrategicEffect { Role, BaseFit, Context, CountsAsCoverage }` +
`StrategicEffectContext` (Flat / RecurringResource / EnemyThreatScaled live; TargetDensity /
ExpectedSustain / EligibleAllies / FreeBattleSlots stubbed with full signatures — a stub returns
`BaseFit` except FreeBattleSlots returns 0 so a future Summon never fabricates phantom capacity).
`RoleCoverage` — an `IntendedRole` bitmask replacing the 4 `Has*` bools on `ArmySnapshot` /
`BaselineForceReadiness`.

- `ByAbility` table: `AntiAir→AntiArmor role EnemyThreatScaled`, `Hyperkinetic→AntiArmor
  EnemyThreatScaled`, `ApBonus→Support RecurringResource`, `Researcher/Assembler→Support Flat`, all
  `coverage:true`; + stat-derived `MobileCombat` (non-recce, `moveMax≥mobileCombatMoveMax`).
- `Resolve` / `Roles` / `RoleFit` / `CoverageOf` / `HasContext` / `ContextualValue`.
- New `EnemyThreatModel` (same file): `CounterDemandFactor` / `ThreatPresent` — the enemy-side
  counterpart, scans `TrueWorld.EnemyArmies` for `IsAir` / `Armored`-tagged members. A new enemy
  threat type is one branch here.
- Wired: `StrategicCardEvaluator.DeriveRoles` (ability block → `StrategicEffectRegistry.Roles`),
  `RoleFit` Support case + `default` (→ registry; `SupportRoleFit` deleted), `ScoreSurplusRole`
  recurringAp (→ `HasContext(RecurringResource)`), `ThreatResponseValue` (→ `EnemyThreatModel`,
  `ArmyHasArmoredMember` deleted), `BaselineForceReadiness.Evaluate` (deployed + hand coverage →
  `RoleCoverage` via registry; `Has*` are now properties off `Coverage`),
  `WorldAnalysis.ToArmySnapshot` (→ `StrategicCoverageOf` per member).
- Deliberate boundaries kept as direct ability checks: recce/stealth (`AbilityParams`), the
  Research/Production operator vocation (`HeroHasSupportVocation`). `ArmySnapshot.HasAntiAir` kept
  as its own field (dual-use: own AA-counter unit / enemy AA-gun danger read by
  `AirReconRouteCandidate`).
- Behaviour delta: the ApBonus Support role-fit is now scaled by economy insecurity
  (`effectRecurringFloor=0.40 .. 1.0 × surplusRecurringApIncomeBonus`) instead of a flat constant;
  everything else is value-parity.

**Acceptance met:** a new skill/effect descriptor is one `ByAbility` row (+ a `ContextualValue`
branch if it needs a new context) — no edit to `StrategicManager` or the `StrategicCardEvaluator`
role switch.

Both Unity assemblies build clean (0/0). capability-quality-sim 71/1, radar-sim 9/1,
housekeeping-sim 55/0 (fails pre-existing).

### Review round 4 P1 ARCH follow-up (fixes on top of `3b87ce7`)

- **P1 — registry now reaches EVERY role, per breakdown axis.** The old `RoleFit` switch routed
  only `Support` through the registry; `CombatBody / ForceGrowth / MobileCombat / AntiArmor /
  AntiAir` bypassed it, so a future `Splash→CombatBody` / `Regen→CombatBody` / `Summon→ForceGrowth`
  would add a role that already exists → zero score delta. New `EffectField` enum + `StrategicEffect.
  Field` + `EffectContribution { RoleFit, ImmediateTempo, ThreatResponse, CapabilityGap,
  ForceGrowth, Synergy }`; `StrategicEffectRegistry.Contributions(role, …)` returns the distributed
  value. `RoleFit` → `RoleFitCore` (non-ability part only); `ScoreForDemand` / `ScoreSurplusRole`
  add `ec.*` to each `bd.*` term ONCE. `ThreatResponseValue` (helper) deleted — `bd.ThreatResponseValue
  = ec.ThreatResponse`; the Hold/gap "is a threat present" checks use `EnemyThreatModel.ThreatPresent`.
  Parity: AntiAir/AntiArmor `BaseFit = threatResponseValueWeight` so the ThreatResponse term is
  unchanged.
- **P1 — `EffectContextData` → `EffectEvaluationContext`.** Now carries `Plan`, `EnemyContactCount`,
  `ProjectedHitPoints`, `EligibleAllyCount`, `FreeBattleSlots` (-1 = unknown). The previously
  BaseFit-only stub contexts are now live scalers: `TargetDensity` × enemy-contact density
  (`effectTargetDensityNorm`), `ExpectedSustain` × projected HP (`effectSustainHpNorm`),
  `EligibleAllies` × dest-army non-hero count (`effectAuraAllyNorm`). `FreeBattleSlots` still returns
  0 until real battle-cell data is threaded (no phantom Summon capacity) — but reads a real context
  field, so wiring the data source later is a one-liner.
- **P1 — equipment MoveMax parity.** `DeriveRoles` (and the registry `RoleFit`) took the bare
  `def.moveMax` while readiness used `AiPower.EffectiveLine(...).MoveMax` — a `+2 MoveMax` trinket
  crossing the mobile threshold made readiness say `HasMobile` while the evaluator dropped the
  MobileCombat role. New `EffectiveMoveMax(plan)` (card + attached equipment via
  `EquipmentSystem.Predict`) is used by role derivation, coverage AND scoring.
- **P2 — generated non-combat phantom Hold.** `ScoreNonCombat` returned `holdLostTempoPenalty * 0.5`
  as `HoldValue` regardless of generation, so `generate → Base/Facility/Aviation` carried a
  reason-to-hold a card that does not exist yet. Now `generation != null ⇒ HoldValue = 0` (same
  class of fix already applied to generated Unit/Hero).
- **P2 — Support Hero double-count.** The registry priced `Researcher / Assembler → heroSupportFitValue`,
  then `RoleFit`'s Support case ALSO added `HeroSupportFit(...)` = `heroSupportFitValue` again for a
  Researcher/Assembler hero (Unit ×1, Hero ×2). `HeroSupportFit` deleted; core Support fit is 0,
  the registry prices the ability once for Unit and Hero alike.
- Behaviour deltas (documented): ApBonus Support role-fit scaled by economy insecurity
  (`effectRecurringFloor` 0.40–1.0); MobileCombat role gains `effectMobileBaseFit` (0.20) from the
  registry; Researcher/Assembler hero Support fit halved (double-count removed). Everything else
  value-parity. Both assemblies build clean (0/0); sims unchanged (71/1, 9/1, 55/0).

### Review round 4 P1 ARCH — context-model follow-up (fixes on top of `58b27ae`)

Pure architecture — no current ability uses these contexts, so zero behaviour delta today; sims
unchanged. Makes the contexts a real *Card × IntendedUse* signal instead of a global proxy.

- **TargetDensity is DESTINATION-LOCAL.** `EffectEvaluationContext.EnemyContactCount` (global
  `TrueWorld.EnemyArmies.Count`) → `LocalEnemyArmies` = enemy armies within
  `effectTargetDensityRadius` (=3) hexes of `plan.Deploy.Hex`. A Splash unit sent to a frontline
  cluster and one sent to a quiet rear army now score differently; no plan/hex ⇒ 0, never a
  fabricated global utility.
- **ExpectedSustain reads the PROJECTED line.** `AiPower.EffectiveCardLine` → `ProjectedStrategicLine`
  now carries the full projected stat block (Attack/Defense/Resistance/Range/HitPoints/MoveMax/
  Initiative/CommandRating/Fate/ActivationApCost + EffectiveAbilities), computed once via
  `EquipmentSystem.Predict`. `EffectEvaluationContext.ProjectedHitPoints` comes from it, so a
  `+10 HP` trinket feeding a Regeneration effect is scored for HP 15, not the bare 5 — same
  planning/execution entity readiness and role derivation already use.
- **EligibleAllies is per-effect.** `StrategicEffect` gains an optional
  `Func<UnitData,bool> EligiblePredicate` (null ⇒ any non-hero). `EffectEvaluationContext` exposes
  `DestArmyMembers` + `CountEligibleAllies(predicate)`; the `EligibleAllies` branch counts only the
  allies THAT aura benefits. An Armored aura vs a Ranged aura vs a generic "+X to all" each supply
  their own predicate in the `ByAbility` row — no registry-wide "generic allies" count.

### Review round 4 P1 ARCH — projection composition follow-up (fixes on top of `3285cf5`)

- **`AiPower.ProjectMaterialization(plan)` — the ONE authoritative projection.** The effect context
  built `EffectiveLine(baseDef, plannedGrant)` and missed equipment ALREADY attached to
  `BaseCardInHand` (a Direct deploy of a pre-equipped card → planning saw HP 5, execution
  materializes HP 15). `EffectiveLine` now takes `params EquipmentGrant[]` and composes grants in
  order (`EquipmentSystem.Predict` chained); new `ProjectMaterialization` = base def + already-
  attached (`BaseCardInHand.Equipment`) + plan-attached (`EquipmentInHand` / `GeneratedEquipmentDef`).
  `EffectEvaluationContext.ProjectedLine`, `EffectiveMoveMax`, and (already) readiness all read it —
  one entity for planning and execution.
- **`FreeBattleSlots` = POST-materialization capacity.** `EffectEvaluationContext` now computes
  `dest.Capacity - dest.Members.Count - 1` for an ExistingArmy / Garrison dest (the `-1` is the
  plan's own primary body — the capacity a Summon would ACTUALLY have, not the pre-materialization
  count); `NewArmy` / `ReusableShell` stays `-1` (unknown, no post-spawn army) ⇒ Summon scores 0.
  Contract documented on the field. Still inert until a Summon mechanic exists and its
  capacity rule (normal battle slot vs its own pool) is known.

Both Unity assemblies build clean (0/0) on `0cd7177` — sim harnesses could not be run because a
concurrent unrelated in-progress edit to `HexSelectionController.cs` was breaking the shared
`Assembly-CSharp.csproj` at the time; the AI/V2 changes were verified to compile in isolation.

### Review round 4 P1 ARCH — snapshot-only destination + projected synthetic capacity (on `22aab7e`)

- **`FreeBattleSlots` for NewArmy / ReusableShell is now PROJECTED, not 0.** Returning 0 for a
  synthetic destination removed the phantom capacity but created phantom *incapacity* — the same
  Summon unit scored `> 0` into an ExistingArmy with spare slots and `0` into a fresh NewArmy with
  spare slots. `ResolveDestination` now projects: hero primary ⇒ its `commandRating`, else the
  heroless field base (`ArmyData.ComputeCapacity(∅, false)`), minus 1 for the primary body; a
  ReusableShell whose snapshot is present uses its (0-member) `Capacity`.
- **The effect context reads the SNAPSHOT, never live `ArmyData`.** New `ArmySnapshot.Capacity` /
  `OccupiedBattleSlots` / `FreeBattleSlots` frozen in `WorldAnalysis.ToArmySnapshot`.
  `EffectEvaluationContext.ResolveDestination` looks the recipient army up by id in
  `snap.Self.Armies` (stale ⇒ -1), so one `MaterializationPlan.Score` can't mix two world states.
  `DestArmyMembers` / the aura `EligiblePredicate` moved from live `UnitData` to the snapshot's
  `WorthIt.DefenderProfile` (the ArmoredAura example predicate becomes
  `p => p.TypeTags.Contains(UnitTypeTag.Armored)`).

### Review round 4 P1 ARCH follow-up — Hero no longer fabricates phantom capacity (both builds 0/0)

- **`ResolveDestination` used `Math.Max(nominalCapacity, heroCommandRating)`; the executor
  REPLACES.** `ArmyActions.DeployUnitFromCard` / `CardPlayExecutor.CanFitAfterDeploy` set
  `projectedCapacity = def.commandRating` **only for the FIRST hero** in the roster (not a max), and
  a subsequent hero is appended after the existing commander with no auto `TryReorderCommander`, so
  capacity does not move at all. The planner's `Math.Max` therefore invented free slots: Hero A
  (CR 2, 1 member, cap 2) + incoming Hero B (CR 5) → planner saw `max(2,5) − 2 − 1 = 2` free, real
  execution keeps cap 2 → `0` free. Symmetric first-hero case: garrison cap 4, 2 members, incoming
  hero CR 3 → planner `max(4,3) − 2 − 1 = 1`, execution `3 − 3 = 0`.
- **New canonical `CardPlayExecutor.ProjectedCapacityAfterDeploy(nominalCapacity, targetHasHero,
  incoming)`** — the ONE place the "a hero rewrites capacity iff it's the first hero" rule lives for
  the V2 path. `CanFitAfterDeploy` now delegates to it; `ResolveDestination` uses it for
  ExistingArmy / Garrison (`a.HasHero`) and for the synthetic NewArmy / ReusableShell path
  (`targetHasHero: false`, `nominalCap` = shell snapshot `Capacity` when present else the heroless
  field-army value — no more `Math.Max(heroCr, nominalCap)`). Planning and preflight can no longer
  drift. Inert until `UnitAbilities.Summon` is wired into `ByAbility`, but `FreeBattleSlots` is the
  canonical projected-capacity context so it must match execution now.

### Final Closure Fixes (§1–§5) — both builds 0/0, no sim (per project rule)

- **§1 — no V1 planner in the MGR-01 call graph.** The only V1-planner decision reference under
  `Assets/Scripts/Ai/V2` was `NonCombatCardPlayer` → `AiManagementPlanner.FindAviationPlacement`.
  Ported verbatim as `PlacementRules.TryFindAviationPlacement(snapshot, player, root, card, out hex,
  out reason)` — a pure FEASIBILITY query ("which owned airfield can this card physically deposit at
  now?") over canonical gameplay APIs only (`AiCardCost` = thin wrapper over
  `ArmyActions.EffectiveDeployApCost` / `card.EffectivePlayResourceCost`; the shared
  `AiAviationSupport.OwnedAirfieldHexes` primitive; `AviationRules.FreeAirfieldCapacity`). WHETHER
  an aviation card is worth playing stays entirely with `StrategicCardEvaluator` / Phase-B.
  Call-graph audit: the remaining `AiTurnController.*` references in V2 (`MoveArmyRoutine`,
  `OwnGarrisonHexes`, `CanIssueMoveNow`, `NearestOwnGarrisonHex`, `GarrisonHexFor`) are
  movement-execution / registry-read primitives used by the recon & task executors, **not** the
  MGR-01 path and not decision logic; `AiDefencePlanner.IsUnderSiege` (WorldAnalysis) is a shared
  siege predicate. No `AiManagementPlanner` / `AiScoutPlanner` / `AiAggressionPlanner` /
  `AiDevelopmentPlanner` / `AiEconomyPlanner` / `AiStrategyDirector` / `AiOperationPlanner` /
  `AiTurnController.Decide` reference remains in the strategic-manager card lane.
- **§2 — dedicated Aviation slot deleted.** `RunDedicatedAviationSlot`, `aviationPlayedInLoop`, and
  the post-loop `if (cleanStop && !aviationPlayedInLoop …)` execution are gone. An aviation card is
  an ordinary `NonCombatPlay` competing in the one ranked Phase-B loop on the same NetScore band as
  every other card. Hard invariant restored: no card-play / materialization action beyond
  `maxSurplusActionsPerTurn`; terminal draws stay a separate bounded mechanism.
  `surplusNonCombatReservedActions` comment updated (already unused).
- **§3 — `StrategicEffect` is a generic descriptor.** New neutral-default data on the struct:
  `Scope` (`EffectScope`), `Magnitude`, `Probability`, `Timing` (`EffectTiming`), `DurationRounds`,
  `CapacityRequirement`, `Stacking` (`EffectStacking`); `EligiblePredicate` doubles as the generic
  TargetFilter (works for both aura directions). `ContextualValue` folds `Magnitude × Probability`
  into every context (1×1 ⇒ every existing row unchanged) and upgrades the four contextual scalers:
  - §3.1 AoE `TargetDensity` ← `LocalEnemyBodies` (sum of enemy **unit** counts within radius),
    not army count. `effectAoeBodiesNorm`.
  - §3.2 Regen `ExpectedSustain` ← `ExpectedCombatRounds` (enemy-vs-own power proxy near the deploy,
    ≥1, capped) × HP factor. Duration is the primary driver. `effectCombatRoundsPowerRatio`,
    `effectCombatRoundsMax`, `effectSustainRoundsNorm`.
  - §3.3 Aura both directions: candidate→army stays `EligibleAllies` + predicate; army→candidate is
    new `EffectEvaluationContext.IncomingAuraSynergy` — priced from `ArmySnapshot.AllyAuraEffects`
    (own-army members' registry effects whose context is `EligibleAllies`, populated in
    `WorldAnalysis.ToArmySnapshot`) against the incoming candidate's `AiPower.ToDefenderProfile`,
    folded once into `EffectContribution.Synergy` (no evaluator switch edit).
  - §3.4 Summon `FreeBattleSlots` ← `min(free, CapacityRequirement) / CapacityRequirement` (0 slots
    ⇒ 0; a 3-body summon with 1 free slot ⇒ 1/3), never full BaseFit. `coverage:false` on the
    example row ⇒ never becomes standing `BaselineForceReadiness`.
  All target effects (Splash / Regenerate / ArmoredAura / Summon) remain **unwired comments** — §3
  is inert until a row is added; the `ByAbility` example block shows the new descriptor fields.
  §3.5 acceptance already held (`Contributions()` distributes by `EffectField`; `Roles()` feeds
  `DeriveRoles`) and still does.
- **§4 — no RecurringResource double-count.** `ScoreSurplusRole`'s `HasContext(RecurringResource) →
  recurringAp` flat add is removed. The `ApBonus` registry row is now TWO explicit contributions —
  `EffectField.RoleFit` (long-term support value) + `EffectField.ImmediateTempo` (tempo value,
  `surplusRecurringApTempoBonus`) — both `RecurringResource`-scaled, both reaching Phase A and
  Phase B identically. Net: the tempo term is counted once (was RoleFit-scaled + flat) and Phase A
  gains the same (previously missing) tempo term. The evaluator no longer knows the context is
  special.
- **§5 — regression check (reasoned, sims deleted):** DecisionScore stays the final arbiter (no
  card-type / trait / resource priority reintroduced — §2 *removed* one). Top-K dedup / joint
  assignment / Hold terms / generated-card `HoldValue = 0` / generated-non-combat single economics /
  partial-failure `NonCombatExecuteResult` / Hero projected capacity — all untouched. §3 is fully
  inert (defaults 1×1, no aura rows, `IncomingAuraSynergy ≡ 0`); §4 is the intended de-dup, not a
  regression.

### Final Closure follow-up (review on top of the §1–§5 batch) — both builds 0/0, tests skipped per project rule

- **§P1 — Phase-B is now literally highest-score-wins (residual boolean bypass removed).** The
  `doMat` condition dropped `|| mat.Residual != null`. Instead `ComputeMatDecision` folds the
  residual demand's urgency into the compared score: `dec.Utility = pick.utility +
  ResidualUrgencyBonus(residual.Value)` (the same `UrgencyBonus` ramp Phase A folds into
  DecisionScore, exposed public). A 2.0 residual Unit only beats a 4.0 Aviation if its
  `demand.Value` earns > 2.0 of urgency. Residual admission (skipping `effThreshold`) is unchanged —
  only the *arbitration* stopped being a hard priority.
- **§P1 — descriptor semantics genuinely participate (were decoration).**
  - `Magnitude`/`Probability` ctor bug fixed: `magnitude <= 0 ? 1 : …` / `probability <= 0 ? 1 : …`
    turned an explicit **0** into 100 %. Now `Magnitude = Max(0, magnitude)`, `Probability =
    Clamp01(probability)` — the *default arg* is still `1f`, an explicit 0 means 0.
  - `Timing`: new `TimingFactor` — a `DuringCombat` effect is worth `effectNoCombatTimingFloor`
    (0.25) where no fight is expected at the deploy (`ExpectedCombatRounds <= 1`); `Persistent` /
    `OneShot` unaffected. Folded into `magP` for every context. Neutral (1) for every existing row.
  - `DurationRounds`: Regen `ExpectedSustain` now caps usable rounds at `min(ExpectedCombatRounds,
    DurationRounds)` (0 = permanent) — a 1-round regen ≠ a 99-round regen. Summon `FreeBattleSlots`
    multiplies by a duration factor (`DurationRounds / effectSummonDurationNorm`; 0 = permanent).
  - `Scope`: `TargetDensity` now selects its affected-body population by scope —
    `EnemiesNearDeploy` → `LocalEnemyBodies` (splash), `DestArmy` → eligible friendly bodies (buff
    nova), `SelfBody` → 1.
  - `Stacking`: new `StackedTotal(policy, per, count)` — `Unique` counts one copy, `Diminishing`
    decays by `effectStackingDiminishFactor` per extra, `Stack` sums. Applied in `Contributions`
    (card's own duplicated effects, grouped by descriptor identity) and in `IncomingAuraSynergy`
    (three identical Unique auras = one aura's worth). Single-instance effects (every normal card)
    are byte-identical.
- **§P1 — AoE/Regen context reads fog-honest KNOWN sightings, not CHEAT TrueWorld.**
  `EffectEvaluationContext` builds `LocalEnemyArmies` / `LocalEnemyBodies` / `enemyPowerNear` from
  `snap.Known.EnemySightings` (`Hex` / `MemberCount` / `DefenseSum + AttackSum`). A never-scouted
  enemy army can no longer move a Splash / Regeneration score. TrueWorld cheat stays confined to
  the sanctioned places (`EnemyThreatModel`, WorldAnalysis threat loop).
- **§P2 — aura marginal-composition holes closed.**
  - candidate→army now sees HERO allies: new `ArmySnapshot.MembersWithHeroes` (all members incl.
    heroes as `DefenderProfile`); `ResolveDestination` points `DestArmyMembers` at it. `Members`
    stays non-hero for WorthIt combat estimates.
  - army→candidate uses the **projected** profile (equipment-adjusted stats + effective abilities +
    base-def type tags), not the bare `CardDefinition`; and applies `Stacking` (see above).
- **§P2 — RecurringResource collapsed to ONE contribution.** The second `EffectField.ImmediateTempo`
  `ApBonus` row (and `surplusRecurringApTempoBonus`) is removed. Recurring AP "pays back every
  following turn" — a sustained Support-capability value (`EffectField.RoleFit`,
  `RecurringResource`-scaled), not a distinct present-turn tempo term. Phase B no longer has the old
  flat `recurringAp` either.

---

## AI-MGR-02 — End-of-Turn Tempo Spending

Work in: `StrategicManager` Phase B / `UseSurplus` and the stretch before terminal draw /
end of AI turn.

### Goal

When active-army work is done, the AI should try to convert free resources into future
tempo. Not necessarily spend everything — but every saved resource must lose or win against
a real alternative.

### 1. EndTurn action selection

After the main execution, build candidates: `PlayCard(CardUseCandidate)`, `DrawCard`,
`ExistingStrategicSpendAction`, `HoldResources`, `EndTurn`. Each `PlayCard` comes from the
new `StrategicCardEvaluator`.

```
EndTurnActionValue =
    ImmediateTempo
  + NextTurnPotential
  + CapabilityGrowth
  + ForceGrowth
  + ResourcePressureRelief
  + HandPressureRelief
  - OpportunityCost
  - ResourceCost
  - ReservationCost
```

### 2. Take several actions in sequence

Not one `UseSurplus()` call but a bounded planning loop:

```
while legal useful action exists:
    rebuild state
    evaluate candidates
    choose best candidate
    execute
    refresh hand/resources/world state
```

Stop when `Hold/EndTurn >= best actionable spend`. So the AI can play a unit → state
changed → play equipment → state changed → draw → end turn, if that is genuinely the best
order.

### 3. Resource pressure

A resource does not always have the same hold value. Compute pressure:

- **AP** — if AP is lost / devalued at end of turn: very high SpendPressure.
- **Resource near cap** (`current ≈ cap`): high SpendPressure, because future income is
  otherwise lost.
- **Rare persistent resource**: low SpendPressure + high HoldValue if there is no worthy
  use.
- **Hand pressure** — if the hand is nearly full: raise the value of useful materialisation;
  weigh the risk of losing a future draw; don't make a pointless draw into a full hand.

### 4. Fix the current reaction-reservation problem

This must become impossible:

```
StrategicManager.UseSurplus  → preserve 10 AP for strategic reaction
StrategicReactionPass         → suppressed because ReconOnly
EndTurn                       → 10 AP stranded
```

Before reserving, check `CanStrategicReactionPassRun(scope, state)` AND
`HasActionableStrategicReaction(state)`. If either is false, do not reserve.

**Explicit reservations.** Instead of a hidden "AP unavailable":

```
StrategicResourceReservation
{
    Owner
    Reason
    ResourceType
    Amount
    ExpirationStage
}

SpendableResource = TotalResource - ActiveReservations
```

**Reservation lifecycle.** If a pass is Suppressed / NoAction / Invalidated / Skipped, the
reservation releases immediately, and EndTurn Tempo Spending runs again in the same turn.
This also removes the case where a planner says "AP/Energy unavailable" while end telemetry
shows the resource stayed unspent. A resource must either be really spent, have an active
reservation with owner+reason, or be free for Phase B.

### 5. Use cheat/global strategic information

The end-turn evaluator may use full information to estimate future potential (enemy strong
armor concentration → AntiArmor card gains future value; enemy air force becoming strong →
AA gains value). Allowed as a strategic decision bias. Must not automatically create normal
AI intel/contact.

---

## AI-RECON-01 — Actor-Aware Recon Planning & Reservation

Work in: `DemandLayer` → `ResourceAllocator` → `ProvisioningManager`, recon mission
construction/continuity.

### Problem

You cannot: create 3 jobs → fund 3 jobs → discover that two relied on the same mover →
`MoverContended`. Actor availability must be part of planning before funding.

### 1. Deduplicate Recon jobs first

```
ReconJobKey { Requirement, IntentType, ObjectiveHexOrRegion, TargetId }
```

Two identical `Scout(Explore 10,-4)` become one job if they relate to the same requirement.
Do not merge `Observe(10,-4)` with `GroundVisit(10,-4)` — different requirements.

### 2. Planning reservation context

```
ReconActorReservationContext { ReservedActorIds, ActorToJob, JobToActor }
```

Seed it first with: actors from mission continuity; actors already committed this turn;
other hard reservations.

### 3. Build a real candidate list per job

Not "actor theoretically has ScoutCapability" but
`Eligible = capability fits AND operational AND not reserved AND can actually execute the
requirement AND activation resources are spendable`. For an air actor also: air state;
sortie continuity; available energy; recovery feasibility.

### 4. Assign scarce jobs first

After strategic priority, order by `EligibleActorCount ASC`. If one job can be done by only
one specialist and another by five actors, the specialist-only job gets its actor first.

### 5. Reserve actor before funding

```
Generate → Deduplicate → Determine eligible actors → Match actor → Reserve actor
→ Reserve required budget → Fund mission → Provision
```

`ProvisioningManager` must receive an already actor-bound mission.

### 6. Recompute concurrency after each assignment

`RemainingReconDeficit` recomputes after each actor assignment. Don't spawn a second
parallel lane if the first assignment already met the required concurrency.

### 7. Invalidation handling

If an actor unexpectedly becomes unavailable after planning: release actor reservation;
release resource reservation; attempt rematch if useful. `MoverContended` stays as a
defensive runtime outcome for unexpected state change — not the normal way to detect a
planning conflict.

---

## AI-RECON-02 — Unified Recon Capacity Model

Work in: `DemandLayer`, Recon concurrency calculation, the data `StrategicManager` uses for
Scout preparation.

### Problem

You cannot count ground-scout capacity separately from air recon in another executor and
then materialise another Scout even though a ready helicopter can already cover an
observation lane. Equally, a helicopter is not a substitute for a ground scout's physical
visit.

### 1. ReconCapacitySnapshot

```
ReconCapacitySnapshot
{
    GroundObservationActors
    ReadyAirObservationActors
    AirborneObservationActors
    PlannedAirObservationActors

    GroundTraversalActors

    ActiveObservationLanes
    ReservedObservationLanes
    ActiveGroundTraversalLanes
    ReservedGroundTraversalLanes

    ObservationDeficit
    GroundTraversalDeficit
}
```

Use actor IDs / lane IDs, not just counts, so one actor can't be counted twice.

### 2. Observation capacity

Includes ground Scout; ready aviation; already-airborne recon actor; an air sortie already
reserved/planned — but only if the actor is really usable: `not otherwise committed AND
activation resources spendable AND movement/action state legal AND mission/recovery
feasible`.

### 3. Ground traversal capacity

Only actors that can actually do ground movement/visit. An air actor is never included.

### 4. Separate deficits

```
ObservationDeficit = max(0, DesiredObservationConcurrency - ActiveOrReservedObservationLanes)
GroundTraversalDeficit = max(0, DesiredGroundTraversalConcurrency
                                - ActiveOrReservedGroundTraversalLanes)
```

Example: desired observation = 3, with 2 ground scouts + 1 ready helicopter →
`ObservationDeficit = 0`, so no fourth Scout is created for observation. But desired ground
traversal = 2 with 1 ground scout → `GroundTraversalDeficit = 1`, and a helicopter does not
close it.

### 5. Planned air sortie already counts as capacity

Once a helicopter is `actor reserved + sortie funded`, that lane reduces unmet observation
concurrency — don't wait for the actual launch, or `DemandLayer` may create an extra recon
need between stages.

### 6. Only spendable AP/Energy

If `AP = 10, Energy = 1` but Energy is really reserved by another actionable mission, the
air actor may count as not ready. But a fictitious/scope-suppressed reservation must be
released via the shared reservation lifecycle, so the capacity model and `StrategicManager`
see the same resource availability.

### 7. StrategicManager receives a capacity deficit

Scout materialisation happens not because "Recon desire high" but because
`Recon requirement exists AND a persistent usable capacity deficit exists`.

---

## AI-INTEL-01 — Explicit Observation / Ground Visit Semantics

Work in: intel/world state, Explore job generation, recon mission requirements,
`ReconAirExecutor`, `AiAviationSupport.LaunchRoutine`.

Make this semantically explicit rather than continuing to rely on unwritten conventions.

### 1. Separate hex states

```
ObservationState  { LastObservedTurn, IntelAge, IntelFreshness }
GroundVisitState  { GroundVisited, LastGroundVisitTurn }
OccupationState   { ownership / occupation / capture }
```

One flag must not carry different meanings.

### 2. Separate Recon-job requirements

```
ReconRequirement { ObservationFreshness, GroundVisit, Occupation }
```

Capture separate from Occupation if needed. `ObservationFreshness` — air or ground.
`GroundVisit` — ground only. `Occupation`/`Capture` — only an actor/action that can actually
do it.

### 3. Air movement

Any aviation step refreshes `ObservationState`, never sets `GroundVisited`. Same for: the
first launch movement; subsequent `ReconAirExecutor` steps; the objective hex; the return
route; second-turn continuation. `AiAviationSupport.LaunchRoutine` and later air movement
must use the same observation path — no separate first tile accidentally going through
generic ground-enter bookkeeping.

### 4. Air Explore generation

Air Explore uses `NeedsObservation(hex)` — never observed OR intel too stale OR strategic
refresh value high enough. Not `!GroundVisited`. After the first successful air observation,
`GroundVisited` may stay false, but the hex must not become an air target again for that
reason alone.

### 5. Ground Explore

Ground jobs use `NeedsGroundVisit(hex)` when a physical visit has gameplay value. So it is
legal to have hex A `Observed = true, GroundVisited = false` with `AirRecon → no need`,
`GroundExplore → still valid`.

### 6. Job semantics flow through the whole pipeline

`ReconRequirement` must survive `Demand → Job → Mission → Provisioning → Executor →
Completion` so the lower layer knows exactly what counts as completing the mission. An air
executor must never report "GroundVisit job completed" because it merely flew through the
objective.

---

## Expected end-state interaction

```
World / Intel
        ↓
Recon requirements
    ├─ Observation
    └─ GroundVisit
        ↓
Unified Recon Capacity
        ↓
Actor-aware Recon planning
        ↓
Ground Scout            Air Recon
     ↓                     ↓
physical visit        strategic route scoring
                       ↓
                 persistent 2-turn sortie
                       ↓
               recon + optional strikes
```

```
World state + enemy/global strategic state + hand + resources + current capabilities
+ baseline force readiness + mission deficits
        ↓
Strategic Hand Evaluator
        ↓
Card × IntendedUse
        ↓
Phase A preparation  +  End-of-turn Tempo Spending
        ↓
Play / Draw / Hold / EndTurn
```

Key manager outcome: an ordinary combat unit no longer has to wait for an AGG mission to
have utility — it gets `ForceGrowthValue` / `NextTurnPotential` / `BaselineReadinessValue`;
Nora can still legitimately beat Hooded as a Scout when her real characteristics make her
the better scout. Where a played unit then goes, how armies merge, how many heroes a
garrison may hold, and stack composition remain a **separate layer** and are not mixed into
these tasks.
