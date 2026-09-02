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
| AI-RECON-01 | Actor-aware Recon planning & reservation | DONE — pushed (`9b43ffa`). New `ReconActorReservationPlanner` (Assets/Scripts/Ai/V2/ReconActorReservationPlanner.cs) runs in the pipeline right after `MissionLayer.Propose` + `ApplyMissionScope`, before continuity `BindFunding` / `ResourceAllocator`. It (1) dedups Recon proposals by `ReconJobKey` (== `MissionIntentKey` — Requirement/SubKind + hex + tracked target; Explore(H) ≠ Refresh(H)); (2) seeds a reservation context from `ActorCommitments.ClaimedArmyIds` (continuity movers + this-turn claims) and counts distinct already-executing durable Recon actors; (3) builds a real eligible-actor list per job = `ScoutMoverSelector.Rank` (capability + operational + not reserved + stealth when required) ∩ first-step executability (`VisitHexTask.FindNextSafeStep` for Explore/Refresh, a reachable `SurveilVantageSelector` vantage for Surveil); (4) assigns scarce jobs first — `MissionAdmissionPolicy.AdmissionRank` DESC, then eligible-count ASC, then stable key; (5) reserves one actor per job, recomputing the still-unmet concurrency room (`ReconConcurrencyPolicy.DesiredTotal` − active) after each assignment; drops fresh proposals with no distinct actor or beyond the room, keeps durable incumbents even if unreserved; (6) stamps `MissionProposal.ReservedMoverArmyId` (new hard-binding field, distinct from the soft `PreferredMoverArmyId`). `ProvisioningManager.BuildExecutionCandidates` restricts a Recon mission's assignment to its reserved actor, rematching across free scouts only if it became ineligible by provisioning time — so `MoverContended` reverts to a defensive-only outcome (spec §7). `PrepareScoutAssignments`'s N-way injective solver + the bounded re-pack stay the final authority. Both assemblies build clean (0/0). NOT play-tested. |
| AI-AIR-01 | Strategic air-recon target & route selection | DONE — new `AirReconRouteCandidate.cs` (Assets/Scripts/Ai/V2/): (1) `AirReconAnchorModel.Build` forms the sortie DIRECTION first from strategic landmarks in priority order — sanitized enemy concentration (one base unit per TrueWorld army, normalised), enemy Citadel (known focus, else real *sector* as a hidden bias at `airReconCitadelHiddenConfidence`), own facility perimeters whose IntelAge ≥ `airReconFacilityStaleAgeMin` (+ probable-approach sector toward nearest known threat), enemy↔asset corridors (midpoint sector), stale known-sighting refresh, and unknown frontier last/weakly (`airReconAnchorFrontierWeight`). Emits an `AirReconAnchorSet` (priority-ordered anchors + peak-normalised per-sector pressure + Citadel sector/confidence + stale-facility hex list). Cheat = direction only; no hex is marked observed. (2) `AirReconRouteScorer.Score` scores a candidate first step for its PROVEN WHOLE ROUTE (`Sortie.Outbound/ReturnPath` or `MultiTurnSortie.PathToAction/PathFromActionToLanding`, capped at `airReconRouteObservationMaxHexes`): additive `InformationGain + StaleIntelRefreshValue` (destination footprint, unchanged basis) `+ EnemyInterest` (blended sector pressure) `+ EnemyCitadelDirectionValue + FriendlyFacilityCoverValue + RouteObservationValue` (Σ per-hex never-observed/stale usefulness along the route, geometric `airReconRouteObservationDecay`, + neighbour ring) `+ CombatOpportunityValue` (route within 1 of an HONESTLY-known sighting) `− TravelCost − ActivationCost − RecoveryRisk` (extra turns + unlanded ends + KNOWN-AA-adjacent route hexes) `− RedundancyPenalty` (recent-air-observed route hexes via `AiMapMemory.WasAirReconnedWithin` + outbound-trail hug + coverage-sector divisor). Hard rules §5: reject if entire positive side ≤ `airReconStrategicValueFloor` ("only value is GroundVisited==false"), or ≥ `airReconRedundancyRecentObsRejectFrac` of informative route hexes are recent air observations. `ReconAirStepPlanner.BuildChoice` now returns `StepChoice?` and delegates all scoring to the scorer (drops the old `ReconDirectionSnapshot` path); rejected candidates are logged `[Recon][Air][Route] DROP` and excluded. `ReconAirExecutor` stamps `AiMapMemory.RecordAirReconTarget` on every observed step + storage launch (V2 never calls `ContinueSortie`, the only V1 stamper, so the redundancy data was previously empty). New config block in `AiConfigV2` (`airRecon*` route/anchor tunables, first-pass). Both assemblies build clean (0/0). NOT play-tested. |
| AI-AIR-02 | Two-turn airborne recon/strike planning | DONE — the spec's `AirSortiePlan` is folded into the existing `ReconAirSortieState` (no duplicate type): new `LaunchTurn` / `AirborneTurnIndex` / `LastProcessedTurn` / `MissionMode` (`ReconAirMissionMode` Recon/Strike/ReconStrike) / `MustRecoverThisTurn` / `LastDecisionReason`, plus `BeginTurn(turn)` that bumps the airborne-turn counter once per AI turn and reports the first call of a new turn. New `ReconAirPhase.Hold` (aloft on purpose, ending this turn here, re-decide next turn). New shared primitive `AiAviationSupport.CanSafelyEndTurnAirborne(air,map,owner)` = `SafeUnlandedEndsRemaining >= 1` AND a recovery plan exists now (same-turn `TryReplan` OR multi-turn `TryReplanMultiTurnReturn`) AND next turn's mandatory return still fits (same-turn route ⇒ trivially safe; multi-turn-only ⇒ `safeEnds-1 >= RequiredUnlandedEnds`); a plane (SafeUnlandedEndsRemaining==0) always fails it, so its single-turn boomerang is untouched. `ReconAirExecutor.RunActor`: (1) each decision re-derives `canRemainAirborne` + `mustRecoverThisTurn` (`AirborneTurnIndex >= 1 && !canRemainAirborne` — never on the launch turn, so same-turn boomerang logic still governs planes); (2) the Outbound `return_reserve` MP-reserve pivot is SUPPRESSED while `canRemainAirborne && !mustRecoverThisTurn` — the helicopter presses on with its whole first turn instead of reserving MP to fly home turn 1; `marginal_gain` (information saturation) still pivots; (3) `mustRecoverThisTurn` forces Outbound→Return at turn start (stranded/exhausted wing goes home); (4) `Hold` set on a prior turn re-opens (→ Outbound, or → Return if must-recover) with fresh MP; a `Hold` set earlier the same turn ends the sortie's turn aloft; (5) `TryOpportunisticAirStrike` no longer force-sets Return — after a favourable strike, if `CanSafelyEndTurnAirborne` still holds it sets `Hold` (second-strike window re-evaluated next turn — never forced), else Return. All safety still delegates to `ReconAirStepPlanner`/`AiAviationSupport` (every accepted step already carries a proven full round trip). Both assemblies build clean (0/0). NOT play-tested. |
| AI-MGR-01 | Strategic hand/card evaluator | DONE (not committed) — new `StrategicCardEvaluator.cs` (Assets/Scripts/Ai/V2/): `IntendedRole` (full 13-value enum; Scout / CombatBody / ForceGrowth / EquipmentUpgrade / Support / Hold carry real signal, AA/AT/MobileCombat/Aviation/CapabilitySpecialist/Economy/Development declared but neutral), `StrategicUseScoreBreakdown` (15 named terms), `StrategicCardUseCandidate` (`NetScore = TotalUseScore − HoldValue`). `ScoreForDemand` (Phase A) and `ScoreSurplus` (Phase B, one card → several Card×Role candidates, best NetScore + winning role) are now the SOLE scoring path: `MaterializationCandidateBuilder.ScorePlanA` / `SurplusUtility` are thin wrappers, and `SurplusCombatReadinessUtility` / `EquipmentUpgradeUtility` / `SurplusScarcity` / `GarrisonSaturationPenalty` / `ScarcityOpportunityCost` (+ helpers) moved into the evaluator. Hero card CLASS adds no flat bonus/penalty — `HeroLeadershipScore(def)` mirrors `HeroRoleEvaluator.CombatLeadershipScore` off the definition; the only hero cost is `AlternativeUseValue` when a scarce hero is spent off its best use. `BaselineForceReadiness` (radar-demand-independent: fielded power / combat-actor count / capability coverage vs game stage + economy + known enemy) feeds `ForceGrowthValue` (an ordinary body scores > 0 at AGG=0/DEF=0) AND `DemandLayer.BaselineForceReadinessDemands` — ONE low-`Value` FieldCombatPower demand charged to Defence, suppressed when an Aggression/Defence combat demand already exists, when Need < `baselineReadinessDemandMinNeed`, or when free field power + actors already suffice. New `AiConfigV2` section (`forceGrowthValueWeight`, `capabilityGapValue`, `baselineReadiness*`, `hero*Fit*`, `hold*`, …), all first-pass. `MaterializationPlan.UseBreakdown` / `UseRole` added (diag only). Both Unity assemblies build clean (0/0). NOT play-tested. (Pre-existing unrelated failure in capability-quality-sim test 16 `ScoutOptionalStealthPolicy` — outside MGR-01 scope, from the uncommitted AIR work.) |
| AI-MGR-02 | End-of-turn tempo spending | NOT STARTED |

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
