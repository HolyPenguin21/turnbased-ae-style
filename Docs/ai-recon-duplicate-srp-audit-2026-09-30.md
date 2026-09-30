# Recon duplicate/SRP audit — 2026-09-30

Base revision: `d5f535637a22ebe7f427360faf148f503627474a` (`master` at checkout).
Working branch: `refactor/recon-duplicate-srp-audit`.

Implementation and audit are ready for review. Full Unity EditMode verification is still pending;
this report does not mark the task's all-tests-pass Definition of Done as met.

## Follow-up review: cache reads/writes and resource bank

The follow-up expands the initial behavior-preserving consolidation with targeted correctness fixes.
Rule costs, risk weights, AP/Energy prices, endurance and memory lifetimes are unchanged.

| Area | Finding | Correction / checked boundary |
|---|---|---|
| Remaining lookup wrapper | ProvisioningManager still forwarded live resolution, including Recon callers | Removed the wrapper; the shared partial's callers use AiV2Util directly |
| Frozen intel write | Capture could overwrite an already-published player/turn/revision identity | Capture is write-once for that identity; a changed observation uses a new revision |
| Frozen intel read | IReadOnlyDictionary exposed a mutable Dictionary | Published copies are detached ReadOnlyDictionary values; live Snapshot is also read-only |
| Empty-hex observation | IntelAge could change without any object-content difference, leaving KnowledgeVersion unchanged | Changed observation stamps invalidate the existing player-scoped knowledge version; repeated same-turn stamps do not |
| Visited/visible map facts | Visiting an already-visible empty hex did not change content or IntelAge, so MapKnowledge could remain stale | The existing VisionSystem change event invalidates knowledge explicitly; content-only notifications retain equality-based invalidation |
| Snapshot capture order | Stamping visibility can now advance the version | Scan and RefreshStrategicKnowledge stamp before selecting the frozen identity; Observe's repeat stamp is idempotent |
| Bank write ownership | The ledger retained the caller's mutable reservation object and normalized deferred AP by mutating it | Upsert stores its own row and normalizes a local amount; inspection reads were already detached |
| Optional stealth | Discretionary stealth used raw AP minus mission claims, bypassing other bank holds | Policy input and final slack use StrategicSpendability's free AP, while mandatory mission costs remain protected |
| Repeated provisioning acknowledgement | A keyed success remained single but cumulative current/next-turn AP/Energy grew on every registration | A successful mission is pinned and acknowledged only once per session |

Reviewed the funding → funded assignment → provisioning → execution → replan chain. ResourceAllocator
nets owner-aware ledger holds against funded/locked Economy draws, and repricing reports both the
AP and Energy shortfall. Air admission deducts same-pass Energy claims from strategic spendability,
and tracks next-turn AP/Energy promises separately. Provisioning does not charge world resources;
execution does. After execution the pipeline refreshes facts and creates the next planning cycle.
Mandatory aviation recovery is settled before card play, rather than held a second time by this bank.
Owner release, upsert, expiry, turn/player isolation, deferred/completion priority and owner-draw
netting are covered by the existing bank tests plus the added regression cases.

Reviewed SafeStepPathing's read/write boundaries: cache identity includes map/pathing version,
player-scoped blocker-memory revision, route endpoints and movement limit; returned route witnesses
are copied. IntelAge/map-visit invalidation intentionally advances strategic knowledge without
invalidating the blocker cache. No route-cache ownership change was needed.

Four failures were first reproduced in the managed harness: same-revision overwrite, mutable
published read, missing empty-hex observation invalidation, and reservation request aliasing. All
four pass after the fixes. Eight cases were added in the existing cache/bank fixtures; all 22
cache/bank cases pass. The ownership fixture also asserts that optional stealth calls the bank seam.

Final reference compilation and managed test compilation succeed (zero errors); git diff --check
passes. Full managed differential run: 776 cases, 524 passes, 252 failures, no skips. All 490
passing master-baseline cases remain passing; 33 of the 37 added cases pass. The previously skipped
source-read ratchet now runs and passes. The same 248 pre-existing harness failures remain, plus the
four new native-Unity recovery cases from the initial consolidation. Full Unity verification below
remains required; the managed result is not an all-green Unity test run.

## Findings and ownership changes

| Rule | Before | After | Authoritative owner |
|---|---|---|---|
| Raw detector risk | Formula in risk model, ground step planner and reaction policy | One normalization/count/radius formula; live and frozen inputs remain distinct | `ScoutRiskModel` |
| Known sighting army IDs | Separate ground/air loops, plus LINQ sets in threat analysis and active defence | One projection into a deduplicated set, preserving ArmyId 0 | `AiV2Util.KnownArmyIds` |
| Live army resolution in Recon | Three thin forwarding methods and four direct registry lookups | Consumers call the existing resolver | `AiV2Util.ResolveArmy` |
| Structural ground scout shape | Same predicate in capacity sizing and two mover enumerators | One predicate; MP, trim, claims and stealth gates remain at their respective horizons | `ScoutMoverSelector.IsGroundScout` |
| Current-movement ETA arithmetic | Copied between pair pricing and vantage ranking | Shared arithmetic; each caller retains its fallback movement budget | `ScoutCostModel.TravelTurns` |
| Same-turn return to a specific landing | Independent proof in StepDirector and return planner | Same owned-airfield/capacity/path/movement proof | `AiAirSortiePlanner.CanReturnThisTurnTo` |
| Return path cost and first cell | Physical path queries in StepDirector | Queries and first-cell extraction in route owner | `AiAirSortiePlanner` |
| Hold reopening | Independent condition in scoring projection and live director | Shared read-only phase rule; writes still follow execution | `ReconAirSortieLifecycle.PhaseAfterHold` |
| Effective air patrol mode | Repeated durable-mode precedence in projection and director | Shared precedence | `AirReconModePolicy.EffectiveMode` |
| Normalized enemy sector concentration | Independent counting/normalization in ground direction and air anchors | Shared sanitized sector distribution | `ReconDirectionModel.EnemyConcentration` |
| First honestly known foreign citadel | Repeated selection in direction and air anchors | Shared query, preserving each caller's observer resolution | `ReconDirectionModel.KnownEnemyCitadel` |
| Objective completion / waypoint continuation | Already centralized; distinct execution wrappers | Centralized rules retained, wrappers retained | `ScoutObjectiveEvaluator` |
| Multi-turn recovery/endurance | Route owner delegating to shared aviation domain | Retained; no Recon endurance formula introduced | `AiAirSortiePlanner` → `AviationRange` / `AviationRules` |

## Confirmed call chains

### Ground

`ReconObjectiveEvaluator` → `ReconMissionPlanner` → funded assignment
→ `ReconAssignmentPlanner.BuildCandidates` → `ScoutMoverSelector` / `ScoutCostModel`
/ `SurveilVantageSelector` / `SafeStepPathing` → `ReconGroundExecutor`
→ `ReconGroundStepPlanner` / `ReconReactionPolicy` → canonical gameplay movement.

Raw detection consumers:

- objective scoring and vantage ranking → `ScoutRiskModel.DetectorRisk(snapshot, hex)`;
- ground step scoring/lookahead and reaction/flee/evade → `ScoutRiskModel.DetectorRiskLive(player, hex)`;
- optional-stealth leg evaluation → `ScoutRiskModel.DetectorRisk(honestSightings, hex)`.

The former live near-memory query included neutrals and ownerless encounters, then explicitly
filtered them. `AllKnownEnemySightings` performs exactly that owner filtering before the shared
`CountDetectors` applies the radius/CanDetectStealthAt gates. Frozen callers keep their frozen
EnemySightings inputs. No consumer switches from frozen strategic knowledge to live memory.

The deleted live normalization used `max(1, norm)` and the canonical helper uses `max(0.0001, norm)`.
The current config is the constant `scoutDetectionRiskNorm = 2`, so these are numerically identical.
The canonical helper is now the only formula if that constant changes in a future balance task.

An objective's DetectionRisk slot is not always raw detector risk: Explore/Refresh may suppress
it when there is no field-army exposure; Explore retains its frontier detector floor; Surveil
retains its confidence-based floor. Those score policies were deliberately preserved.

### Aviation

Capacity: `ReconAssignmentPlanner.MeasureAirCapacity`
→ `AirActorProgressesAnObjective` → `ReconAirReservationPrepass.EvaluateAirStructuralFeasibility`
→ `ReconAirStepPlanner.Pick` → `AiAirSortiePlanner.TryPlanSortie/TryPlanMultiTurnSortie`.

Assignment: `ReconAssignmentPlanner.AppendAirCandidates`
→ the same structural prepass and step planner, anchored at the concrete mission target.

Execution: provisioned actor → `AirReconPlanner.Plan` (live actor validation)
→ `ReconAirExecutor` → `AirReconStepDirector.PlanStep`
→ shared step/sortie/recovery planner → canonical Move/Strike calls.

Return: `AirReconStepDirector.PickReturnStep`
→ `AiAirSortiePlanner.TryReplan` or `TryReplanMultiTurnReturn`
→ hysteresis policy using `CanReturnThisTurnTo` and `ReturnPathCostOrMax`
→ `FirstRouteStep`. Hysteresis remains in StepDirector. It retains its existing same-turn
viability condition even when evaluating a multi-turn return candidate; changing that policy
would be a separate behavior change. TryReplan obtains feasibility and cost from one path search.

The shared physical primitives below the AI owner remain legitimate domain responsibilities:
owned airfields/path costs in AviationRules; endurance simulation in AviationRange;
pathfinding in HexPathfinder. They were not copied into new Recon utilities.

## Similar methods intentionally kept separate

| Similar zones | Reason to retain the separation |
|---|---|
| `ReconCapacitySnapshot.Build` and `ReconAssignmentPlanner.MeasureCapacity` | Strategic desired/structural supply versus executable actor↔job matching, including path/vantage and joint quotas. Only their identical actor-shape predicate was shared. |
| Ground completion wrapper and Air finalization/stale-no-op wrappers | Ground records movement/stealth and checks whether the actual actor is still solo Recce. Air processes per-mission results, skipped actors and stale no-ops. Both dispatch completion and waypoint semantics through ScoutObjectiveEvaluator. AirSweep remains unsatisfied by observation. |
| `AiReconIntelMemory`, `ReconIntelSnapshotRegistry`, `AiReconMemory` | Live per-hex observation stamps, immutable per-player/turn/knowledge-revision copies, and historical enemy-contact records are different data and lifetimes. |
| Live and frozen `TryGetIntelAge` | They read different stores/horizons; age subtraction and nonnegative clamping are simple value arithmetic, not a second staleness/lifetime policy. |
| Ground and air `OtherSectorClaims` | Ground counts a durable strategic heading; air requires an active HasClaim and a sector measured from launch. Both exclude absent armies, but ownership and reference frames differ. Air step scoring actually uses current positions of assigned Recon actors from the citadel, plus provisional wedge claims. |
| Ground trail and air sortie trail | Ground uses a bounded history and immediate-reversal marker across steps; air uses a full sortie trail from launch until landing. |
| `IsExposed` and `CountDetectors` | Field-army ability to engage versus stealth detection. A garrison can detect without exposing a scout to a roaming threat. |
| `StealthReadyThisTurn`, `CanServeStealth`, structural stealth probe, arrival-hidden check | Current entry opportunity, durable capability, structural inventory and guaranteed hidden arrival answer different questions. |
| Air capacity fallback budget count and structural witness | Loose raw-stockpile upper bound versus useful target-specific executable routes. Strategic sortie funding remains solely at provisioning. |
| `Pick` and `PickFromStorage` | Already-formed mission actor versus undeployed-card valuation. Both delegate route feasibility to the same owner and share BuildChoice/scoring. Storage valuation never creates a Recon actor. |
| Air destination footprint and whole-route information usefulness | Footprint aggregates never-observed count and observed-only average age; route scoring aggregates corridor information and novelty. Shared staleness thresholds are already owned by ReconIntelSnapshotRegistry. |
| Four task score builders | Different TaskScore slots and purposes; merging them would obscure task semantics or risk changing weights. |
| Assignment eligibility facade and typed failure/result wrappers | Existing API boundaries and diagnostics, not independent implementations of a rule. |
| Historical memory's liveIds loop | Collects IDs while writing observed contact payloads in a single pass; it is not an independent standalone projection helper. |

## Preserved behavior and cache boundaries

- Four kinds remain Explore, Refresh, Surveil and AirSweep.
- Explore completes by physical ground visitation. Aviation cannot be assigned to it.
- Refresh/Surveil remain ordinary ground jobs. Surveil uses a safe vantage or honest re-sighting.
- Attack observation needs still map to Refresh for stale observed targets and Explore for never-observed targets.
- AirSweep remains an existing-wing task, completing through sortie return/landing rather than anchor visibility.
- No TaskScore weights, desire coefficients, concurrency limits, AP/Energy rules, reaction thresholds,
  opportunistic strikes, TurnsWithoutRefuel or attack movement behavior changed.
- Visibility/content events still update live intel; Observe freezes the corresponding knowledge revision.
  Player/turn/revision keys, copy ownership, session clearing and historical expiry remain intact.
- No new cache, global utility class, mission family or parallel architecture was introduced.
- Sector sanitization still emits no hidden army IDs, exact positions, roster, strength or AA information.
  Encounter order of occupied sectors and first-known-citadel selection are preserved.

## Verification

Two compilations were run: current sources against UnityEngine reference assemblies, and the
NUnitLite/Mono test harness. Both compile successfully. The reference-assembly check excludes
native editor drawers unavailable in the harness; it is not a Unity editor compilation.

Differential test baseline is the SHA above. Final full managed run:

| Result | Baseline | Changed tree |
|---|---:|---:|
| Total cases | 739 | 768 |
| Passed | 490 | 515 |
| Failed | 248 | 252 |
| Skipped | 1 | 1 |
| Previously passing cases now failing/missing | — | **0** |

New fixture `AiReconRuleOwnershipTests`: **29 cases** — **25 pass** in Mono;
**4 require Unity** and stop in GameObject's native constructor outside the engine.
The pre-existing failing cases remain failing in the managed harness. Most are native engine
initialization failures; the broader baseline also contains existing assertion failures.
No claim is made that all pre-existing failures are engine limitations or that the full suite passes.

New coverage:

- numerical live/frozen/enumerable detector parity for 0/1/2/3 detectors;
- neutral/ownerless exclusion, distant sightings, risk clamp;
- discovery identity deduplication including ArmyId 0;
- structural scout shape (non-scout, prison, aircraft, empty and spent mover);
- ETA parity between pair pricing and vantage ranking;
- Hold same-turn/fresh-turn/deadline and durable-mode precedence;
- first route cell/null/single-cell path;
- shared sanitized enemy concentration and first honest foreign citadel;
- compiled-call dependency assertions for actual planner/reaction/executor, capacity/assignment,
  lifecycle, direction and completion owners;
- Unity recovery parity for available, full, captured and no-longer-reachable old landing.

The existing Recon tests, including attack-observation, separation, continuity payload,
waypoint role, detector risk and frozen snapshot ownership tests, were retained.
`git diff --check` passes. A cross-file exact-window scan of all scoped sources found no remaining
six-statement identical business blocks; this was a supplementary check, not the semantic proof.

### Remaining Unity verification

Run the complete EditMode suite in the Unity version used by the project before merging.
In particular run the four new ReturnPlannerAndDirector cases and the existing air lifecycle,
stealth admission, trim eligibility, landing capacity, multi-turn endurance and observation tests.
Then exercise the supplied gameplay cases: physical Explore, stale Refresh, Attack observation
need, Surveil vantage/re-sighting, AirSweep/Strike/Return/Landing, changed landing capacity,
continued flight after attack and player-isolated live→frozen intel refresh.

## Audit inventory

The production scope below was surveyed through rule searches, implementation/call-chain review,
and comparison of responsibility/knowledge horizons (37 files). Complementary callers reviewed
include AiV2Util, AiMapMemory, WorldAnalysis.Threat, ActiveDefenceObjectiveEvaluator,
MissionRevalidator/Continuity/Outcome completion callers and shared aviation primitives.

- `Analysis/ReconCapacitySnapshot.cs`
- `Analysis/ReconDirectionModel.cs`
- `Execution/ReconAirExecutor.cs`
- `Execution/ReconGroundExecutor.cs`
- `Foundation/ReconScoutKinds.cs`
- `Missions/ReconMissionPlanner.cs`
- `Missions/SurveilVantageSelector.cs`
- `Reaction/ReconReactionPolicy.cs`
- `Recon/AiAirSortiePlanner.cs`
- `Recon/AirReconPlanner.cs`
- `Recon/AirReconRouteCandidate.cs`
- `Recon/AirReconStepDirector.cs`
- `Recon/AviationObligations.cs`
- `Recon/AviationSortieReservationEvaluator.cs`
- `Recon/ReconAirCapacityPolicy.cs`
- `Recon/ReconAirEnergyPolicy.cs`
- `Recon/ReconAirReservation.cs`
- `Recon/ReconAirSortieState.cs`
- `Recon/ReconAirStepPlanner.cs`
- `Recon/ReconAssignmentPlanner.cs`
- `Recon/ReconConcurrencyPolicy.cs`
- `Recon/ReconGroundStepPlanner.cs`
- `Recon/ReconPatrolState.cs`
- `Recon/ScoutCostModel.cs`
- `Recon/ScoutExecutionSafety.cs`
- `Recon/ScoutMoverSelector.cs`
- `Recon/ScoutOptionalStealthPolicy.cs`
- `Recon/ScoutRiskModel.cs`
- `State/AiReconIntelMemory.cs`
- `State/AiReconMemory.cs`
- `State/ReconIntelSnapshotRegistry.cs`
- `State/ScoutTrailRegistry.cs`
- `Strategy/Demand/DemandLayer.Recon.cs`
- `Strategy/Objectives/ReconObjectiveEvaluator.cs`
- `Strategy/Objectives/ScoutCapabilityContext.cs`
- `Strategy/Objectives/ScoutCapabilityQuality.cs`
- `Strategy/Objectives/ScoutObjectiveEvaluator.cs`
