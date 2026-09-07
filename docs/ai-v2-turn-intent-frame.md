# AI V2 — Turn Intent Frame (merge Strategy + Objective + Demand)

Working notes. Baseline: master `c4e255f`.

## Agreed principles

1. Do NOT extend the architecture.
   1.1. Rewrite the current implementation toward the goal (deletion-first; the diff is mostly red).
2. Blocks are atomic: one clear input, one clear output.
3. Blocks are encapsulated.
4. In-turn loop on new information — will need re-trigger conditions per task type
   (recon, aggression, economy, ...). Deferred; independent of the radar change.

## Decision: radar model #1a — IN PROGRESS

Radar SCALES objective value, it does NOT slice AP.
`EffectiveValue = BaseValue x scale(radar[axis])`, computed once, read everywhere.
AP is a single pool; the allocator is "sort by EffectiveValue, spend the pool top-down".

Two commits, each compiles on its own:

- **Commit 1 (pure deletion) — DONE, not yet Unity-built.**
  - `AxisBudgetLedger.cs` — internals collapsed to one scalar pool (`_pool` / `_initialPool`
    / `_followupReserved`). Per-axis method signatures KEPT (the `DesireAxis` arg is
    accepted and ignored) so `StrategicPhaseA`, `MaterializationFeasibility/CandidateBuilder`,
    `MaterializationDiagnostics`, `InfrastructureFulfillment` compile with zero edits and
    transparently read/charge the one pool. `DiscreteAdmissionBudget` -> pool.
    `CommitDiscreteFollowupBorrow` -> no-op (returns 0). `Create(float, Radar)` ->
    `Create(float)`.
  - `ResourceAllocator.cs` — removed `BudgetSlice`, `TentativeAllocation.Slices`,
    `FundedEntry.PerAxisDraw`, `Shares()`/`shareCache`, `lockedStrictByAxis`, per-axis
    `bottleneck`/`failedAxis`/`draws`/`allFit`, `_radar` field. `LockedAllocation.StrictDraw`
    dict -> `StrictAp` float. `FundedEntry` gains `StrictAp` float. `Pack()` now tracks one
    `float budget` (= `ledger.Balance()` net of locked strict); commitments and fresh
    missions draw straight from it; remainder = leftover of that one pool. `AxisOverdraft`
    field retained, always 0. `Shares()` -> `HasValidContribution()` (>0 check only).
    `AddBudgetDeferred` signature simplified (no axis). Sort keys still `BaseValue`.
    `BeginTurn`/`AllocationSession` keep the `radar` param (unused, signature-stable).
  - Call sites: `AiStrategyV2Pipeline.cs:493`, `ReactionRoundExecutor.cs:102` -> `Create(ap)`.
  - Behaviour now: greedy-by-BaseValue over one AP pool. Radar does not affect AP at all.
- **Commit 2 (radar re-enters at one point) — DONE, not yet Unity-built.**
  - `AiConfigV2.radarScaleFloor = 0.35f` (first-pass, tune in Unity).
  - `RadarValueScale` (new static, in `AiStrategyV2Pipeline.cs` after `Radar`):
    `For(radar, axis) = floor + (1-floor)*min(1, weight*axisCount)`; `For(radar, mission)` =
    contribution-weighted blend (collapses to the single axis for every real proposal).
  - `MissionProposal.EffectiveValue` (new field) = `BaseValue * RadarValueScale.For(radar, m)`,
    stamped once in `AiStrategyV2Pipeline.RunTurn` AND `ReactionRoundExecutor` right after
    AttemptId stamping.
  - `ResourceAllocator` cross-lane ranking now on `EffectiveValue` via `RankValue(m)` helper
    (falls back to `BaseValue` when unstamped — keeps bare sims working): None-lane queue
    order, k-way merge head pick + tie-break, `topUpOrder`, both commitment-starvation
    `minCommitVal` blocks. LogDump prints `base X eff Y`. Within-lane `AdmissionRank`
    unchanged (no double-count: EffectiveValue = axis-level radar weight; LocalAdmissionScore
    = finer type-level recon sub-desires).
  - **Placeholder removal (asked separately):** `DesireEvaluators` DEF/ECO/DEV raw desire
    `0.30` placeholder -> `0f`. `AiConfigV2.desirePlaceholderInactive` deleted.
    `AiStrategyV2Scope.Mode` `Full` -> `ReconDevelopment` (honest isolation: Recon + Development
    + StrategicManager/Housekeeping; DEF/ECO/AGG desire/demand/intents/missions dropped).
  - **DEFERRED within Commit 2 — `DemandLayer.Value` scaling.** Needs `radar` plumbed into
    `DemandLayer.Generate` (+ 2 call sites). No-op under the current `ReconDevelopment` scope
    (Phase A sees only Recon demands — a common scale factor doesn't reorder them; Development
    demand is a hardcoded `Value=45f` infra demand, radar-blind by design). Do when scope widens.

## Development — what exists vs not (clarification)

Development's FUNCTIONAL machinery IS implemented: `DemandLayer.DevelopmentDemands` (emits a
`DevelopmentInfrastructure` demand, `Value=45f`), Research/Production card generation
(`MaterializationExecutor` / `GenerationSource`), `AiDevelopmentPlanner` / `AiCardCost`,
`StrategicCardEvaluator` dev-card scoring, Phase A/B generation+attach+draw (commit `1c09a96`
+ R/P P0). What is NOT implemented: a **radar DesireEvaluator** for `DesireAxis.Development`
(was the 0.30 stub, now 0). By design Development runs "vне AP-budget" — through the Phase A/B
card lifecycle, not a radar-weighted AP share — so 0 radar weight does not disable it; its
demand still competes in Phase A at face value 45. A real Development desire signal is a
separate small task, only needed once EffectiveValue matters for dev-routed allocator work.

Not touched: WorldAnalysis, MissionContinuity, ProvisioningManager, TaskExecutor,
HousekeepingManager, Phase B (works off real root AP), Refresh.

## Goal

Collapse the hand-wired orchestrator sequence (`AiStrategyV2Pipeline.RunTurn` lines ~433–481)
into one module with one typed output. The data already flows in the right order; it is just
not encapsulated.

## Proposed shape

**Module output: `TurnIntentFrame { Radar, CommitmentState, ScoredObjectiveSet }`**

Internal order (matches what the orchestrator already does):

1. `Radar` — pure `WorldSnapshot -> Radar`. No commitment feedback. (axis weights, sum = 1)
2. `enumerate raw objectives` — pure `WorldSnapshot -> objectives`. Independent of Radar.
3. `ResolveActive` — stateful (reads persisted intent store + fresh snapshot) ->
   surviving intents, actor bindings, claimed army ids, released-this-turn actors.
4. tag every objective `Coverage = New | Continuation(intentId) | Blocked(reason) | Superseded`.
5. radar-scale the survivors (only if we adopt model #1 — see open decision 1).
6. `Demand` = projection: `objectives.Where(o => o.Coverage == New && !HaveCapability(o.Need))`.
   Not a stored/mutated list.

`CommitmentState` shape:

```
SurvivingIntents:    [{id, axis, kind, target, actorIds, expectedCompletionTurn, progressState}]
ActorCommitments:    actorId -> intentId
ClaimedArmyIds:      set
ReleasedThisResolve: [{actorId, reason}]   # freed by cancel/retarget, reusable THIS turn
```

`ScoredObjective` shape:

```
Axis, Target, Kind
BaseValue          # from WorldSnapshot, radar-blind
RadarScaledValue   # BaseValue * f(axis weight)  -- only if model #1
Coverage           # New | Continuation(intentId) | Blocked(reason) | Superseded
RequiredCapability # kind, amount, traits, targetHex  -- meaningful for New/Continuation
ContinuityBonus    # 0 for New; small for Continuation (commitment inertia lives HERE, not in Radar)
```

Keep the pure sub-function `WorldSnapshot -> (Radar, raw objectives)` separately testable even
though the public module is stateful.

## What currently does NOT match (the list to work through)

1. **No encapsulation.** 7 sequential orchestrator calls sharing locals; `reconObjectives`
   threaded as a naked `List<ReconObjective>` through ~8 call sites (ResolveActive,
   ActorCommitments.FromIntents, DemandLayer, StrategicPhaseA, ReconMissionPlanner, Phase B
   postCommitments, HousekeepingManager). Fix = extract to module + one output type. Low risk.

2. **No unified `ScoredObjectiveSet` / `Coverage` tag.** `ReconObjective` and
   `AggressionObjective` are separate types, `BaseValue` only, no New/Continuation/Blocked.
   "Already covered by an active intent" is re-derived independently in DemandLayer AND in
   ReconMissionPlanner (both take objectives + activeIntents and find the overlap themselves).
   That duplicate derivation is a latent bug source. Tag it once in ResolveActive.

3. **Demand is a stored mutable list, not a projection.** `List<AxisDemand>` carries state
   flags (`AxisDemand.IsPersistenceDeferred`) and Phase A marks entries satisfied/blocked
   in place. This is the phantom-demand surface. Make Demand a computed view over
   `ScoredObjectiveSet` + current inventory.

4. **Radar is not a pure function.** `AiStrategyV2Scope.ApplyRadarScope(assessment)`
   (Pipeline line ~435) post-mutates the radar for test focus scopes (ReconOnly etc.).
   Minor, but `WorldSnapshot -> Radar` is no longer clean.

5. **Objectives are NOT radar-scaled — conflicts with the stated mental model.**
   `BaseValue` is radar-blind; radar enters only downstream as AP slices in
   `AxisBudgetLedger` + `AxisContribution` in the allocator. That is "radar = budget only"
   (model #2), not "radar selects/scales objectives" (model #1). **Open decision — see below.**

6. **No mid-turn re-resolve.** `ResolveActive` runs once (line ~465), `ReconcileAfterTurn`
   once (~700); between them `TaskExecutor.Execute` runs every task with the enemy layers of
   `snapshot` frozen for the whole turn. Only crack of reactivity: `MissionRevalidator`
   bounded stale-Explore replacement. Reaction lives OUTSIDE this pass
   (`StrategicReactionPass` / `StrategicInterruptRegistry` / `ReactionRoundExecutor`).
   The design is turn-granular single-pass + a commitment layer bridging turns — NOT the
   "execute part -> observe -> replan" loop. Big separate question; the merge does not
   depend on resolving it.

7. **`CommitmentState` rebuilt 3x, not frozen once** (Pipeline ~470 from activeIntents,
   ~709 `postCommitments` from registry, consumed ~745). Reasonable (intents change), but
   the "frozen frame" claim is inaccurate here.

## Open decisions (settle before refactoring)

1. **Radar model #1 vs #2.** #1 = radar scales objective value
   (`effectiveValue = BaseValue * f(radar[axis])`, golden low-axis opportunities survive via
   high BaseValue, marginal low-axis objectives die). #2 = radar only sets AP budget
   downstream, objectives radar-blind (current code). Changes whether the frame outputs raw
   `BaseValue` or `RadarScaledValue`.

2. **Scope handling.** Where do `AiStrategyV2Scope` focus filters (ReconOnly etc.) apply
   relative to the pure sub-function — keep them as a post-filter on the frame output, or
   push into the module. (Keeps decision 4 clean if they stay outside the pure part.)

## Readable frame logging (done first, DONE)

`AiFrameLog` (Diagnostics/AiFrameLog.cs) — human-readable multi-line block per frozen-frame
stage into `AiDebugLog`, gated by `AiConfigV2.frameLogEnabled` (default true). Wired into
`Pipeline.RunTurn` after each stage: GAME STATE + WORLD ANALYSIS (after `WorldAnalysis.Scan`),
STRATEGY LAYER (after `StrategyLayer.Evaluate`), OBJECTIVES (after the two `Enumerate` calls),
MISSION CONTINUITY (after `ActorCommitments.FromIntents`). Diagnostics only — no decisions,
no state, every read null-guarded. NOT yet wired into `ReactionRoundExecutor` (reaction pass).

Field notes surfaced while writing it:
- `hand` line lists `CardData.Definition.displayName`.
- WORLD ANALYSIS "seen force" = `Known.EnemyKnownStrength` = Σ(DefenseSum + AttackSum) over
  remembered enemy sightings — the V1 WorthIt raw-stat scale, from map memory so it can be stale.
- STRATEGY "economic-runway" = `DesireVector.EconomicRunway` ∈ [0..1], an out-of-simplex
  modifier scalar (sibling of `MilitaryThreat`). 0 = broke/stalled, 1 = deep surplus.
  = SmoothStep(`EconomyStanding.EconomicSecurity`) = blend(AbsFloor, RelativePressure,
  BottleneckPressure). Logged expanded.

## Development as a full strategy axis (in progress)

Decisions locked: Development stays in **Phase A/B** (not a mission lane) — Phase A already runs
before mission planning so an upgrade lands in time for this turn's missions, and resolving the
R/P gamble in Phase A avoids the allocator<->provisioning desync a mission would create; the
analyzer reads the frozen Aggression/Recon objectives to weight the upgrade value toward cards
those objectives will use. "Worth it" = `EV = p*G - A_total` where `A_total` is the value of the
best alternative spend of the same resources+AP this turn (via
`MaterializationCandidateBuilder.DecisionScore`) — this replaces a separate surplus multiplier
and is the "upgrade vs play a new unit" comparison. Enemy-on-hex is an execution precondition in
Phase A (`facility_contested`), never a scoring/desire gate.

Steps:
1. **`DevelopmentReadiness` in the scan — DONE, not Unity-built.** `WorldSnapshot.Development`
   (+ `DevelopmentFacility` / `DevelopmentOffering` types). `WorldAnalysis.BuildDevelopment`
   enumerates own facilities (+ qualifying hero, `Contested` flag) and every catalog card
   passing facility-ability + hero + `CanAffordCard` + `AiConfig.developmentMinSuccessChance`
   (NOT the enemy-on-hex rule). `SurplusFraction` = worst-type spendable-over-2-turns-income
   (first pass). Wired into `Scan` / `RefreshOperationalState` / `RefreshStrategicKnowledge`.
   Logs: `dev.readiness ...` in LogSnapshot, `development: ...` in AiFrameLog.
2. **`DevelopmentEvaluator` (radar) — DONE, not Unity-built.** `StrategyLayer.DevelopmentDesire`
   (isolated static, reads only `snap.Development`): `rawDev = facilityGate * surplusRamp *
   offeringQuality * gain` — multiplicative, any missing prerequisite -> 0. `DesireBreakdown` +=
   `DevFacilityReady / DevSurplusFraction / DevOfferingQuality / DevBestSuccessChance /
   DevUpgradeTargets`. `desires.Raw[Development]` now `Smooth(rawDev)` instead of `0f`. Config
   `AiConfigV2.dev*` ramps. Logs: `desires — DEV raw ...` (LogDesires), `dev drivers:` (AiFrameLog).
   NOTE line trimmed to Defence/Economy. Effect on the ReconDevelopment test scope: radar becomes
   RCN+DEV split when `rawDev > 0` (was pinned RCN 1.00).
3. **`DevelopmentOpportunityEvaluator` (OBJECTIVE) — DONE, not Unity-built.** New file
   `Strategy/Objectives/DevelopmentOpportunityEvaluator.cs`. `Enumerate(snap, player, root,
   hand, aggObjectives)` (not pure — needs live CardData/UnitData, same exception as DemandLayer).
   Per `DevelopmentOffering`: if Equipment -> best legal recipient (hand Unit/Hero card via
   `EquipmentSystem.CanAttach` + real `AiPower.EffectiveLine` delta; on-map own units via
   `CanAttach` + **first-pass flat `devEquipGainFraction * UnitPower`** — TODO: real projection
   from UnitData); else -> `NewCard` (`ToPowerUnit(minted) * devNewCardUseFactor`).
   `EV = p*G - A_total - apCost`, `A_total = (1 - surplusRamp) * bestAffordableHandUnitPower`
   (NewCard -> 0), `BaseValue = clamp(0..100, devEvToBaseValue * EV)`, keep if `EV > devEvMargin`.
   `AiConfigV2.devEv* / devImportance*` — ALL first-pass, the "how R/P picks a card" review tunes
   them. Wired into pipeline (3e, ReconDevelopment scope) — logs `[AI][V2][Dev] objectives ...`
   but not yet consumed (steps 4-5).
4. **`DemandLayer` — DONE, not Unity-built.** `CapabilityKind.CardUpgrade` +
   `AxisDemand.DevOpportunity` (typed handle, like `ScoutContext`). `DevelopmentDemands` now: no
   facility -> the infra-gap demand (unchanged); facility ready -> one `CardUpgrade` demand per
   scored opportunity (`Value = op.BaseValue`, carries `op`). `Generate` takes
   `devOpportunities` (optional last param); pipeline passes it, ReactionRoundExecutor doesn't
   (null -> no dev upgrade demands in the reaction pass). Log
   `[AI][V2][Demand][Development] decision=UPGRADE count=N`. Phase A can't fulfill `CardUpgrade`
   until step 5 — demand shows in logs, then defers.
5. **Phase A execution — DONE, not Unity-built.** New file
   `Strategy/Demand/DevelopmentUpgradeFulfillment.cs` (mirrors `InfrastructureFulfillment`):
   `TryFulfill(snap, player, root, hand, ctx, demand, ledger)` -> `DevUpgradeResult`. Live gates
   (`IsEligible` = the `facility_contested`/enemy-on-hex check + `ActorStillQualifies` +
   `CanAffordCard` + `GenerationSource.FitsReservedAffordability` (now `internal`) +
   `EstimateSuccessChance >= developmentMinSuccessChance` + recipient still legal +
   axis-budget/live-AP for the attach). Execute: `ApplyResearchReveal` -> `PayCardCost`
   (irreversible) -> `RollChallenge(int.MaxValue)` -> on win `MintCard` + `EquipmentSystem.TryAttach`
   (hand `CardData`/on-map `UnitData`) or `hand.AddCard` (NewCard). Challenge costs no AP; the
   attach costs `Card.activationApCost`, charged to Development. `StrategicPhaseA` gets a
   CardUpgrade pre-pass (after the infra pre-pass), highest demand `Value` first, `ledger.Debit` +
   `StrategicPhaseResult` counters + snapshot refresh, logs `[AI][V2][Dev] CHALLENGE win|loss` /
   `SKIP reason`. `DemandLayer.Generate` now takes `radar` -> `DevelopmentDemands` scales the
   `CardUpgrade` demand `Value` by `RadarValueScale.For(radar, Development)` (the deferred
   Commit-2 scaling, done here for Development). Pipeline + ReactionRoundExecutor pass `radar`.

**Development axis is now end-to-end**: readiness (scan) -> desire (radar) -> EV opportunities
(objective) -> CardUpgrade demands -> Phase A Challenge/mint/attach.

### Review round 1 (card-selection) — DONE, not Unity-built

1. **Real `G` for on-map units.** `DevelopmentOpportunityEvaluator.OnMapEquipmentGain` — projected
   `AiPower.EffectiveLine(u.OriginatingCard, u.Equipment?.equipment, newGrant).BasePower` delta
   (same weighted line the hand-card path uses). Flat `devEquipGainFraction * UnitPower` only when
   `OriginatingCard` is null (minted / event units).
2. **`maxDevelopmentUpgradesPerTurn = 2`** — Development's own per-turn Challenge cap, SEPARATE
   from `maxGenerationActionsPerTurn` (the combat-generation cap). Each upgrade still calls
   `StrategicTempoBudget.RecordGenerationAttempt` for telemetry. The real limiter stays the
   resource stake via `FitsReservedAffordability`.
3. **NewCard dropped.** `DevelopmentOpportunityEvaluator` skips non-Equipment offerings
   (`if (!off.ProducesEquipment) continue;`); `DevRecipientKind.NewCard` removed. Non-equipment
   R/P mints stay with `GenerationSource` / `MaterializationCandidateBuilder`.
4. **Greedy re-scoring pre-pass.** `StrategicPhaseA` CardUpgrade pre-pass now: each round
   `DevelopmentOpportunityEvaluator.Rescore(op, snap, root, hand)` every remaining opportunity
   against the fresh snapshot (surplus / best-alternative shift as resources drain), drop those
   `<= devEvMargin`, sort by `BaseValue`, execute the best, `RefreshOperationalState`, repeat until
   the cap.

## Priority order

1. Decision 1 (radar model).
2. Items 1–3 (encapsulation + Coverage tag + Demand-as-projection) — the actual merge payload.
3. Item 6 (loop vs single-pass, and what `StrategicReactionPass` is for) — deferred, independent.

## Next step after this list

Trace how `ScoredObjectiveSet` reaches the Mission Layer: today objectives + fresh armies
combine into `MissionProposal` in `ReconMissionPlanner` / `AggressionMissionLayer`, and the
`Coverage` tag should drive whether the planner starts a new mission or continues an intent.
