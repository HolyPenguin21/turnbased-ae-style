# AI Strategy V2 — Economy Commitment and Builder Recovery Design

Date: 2026-09-09  
Status: approved in chat; implementation pending

## Purpose

This document amends the approved AI Strategy V2 Economy Axis design in two places:

1. protect a selected Economy build without freezing H/E/M/T several turns too early;
2. prevent a hero used as an Economy builder from being abandoned on an exposed extraction site after construction.

The change stays horizontal inside the existing V2 architecture. It adds no Economy manager, retreat
manager, pathfinder, resource ledger, execution loop, or Housekeeping movement branch.

## Verified current behavior and root causes

### Commitment gap

An Economy Base candidate carries an exact `EconomyBuildCard`, resource cost, target and builder
routes. When no builder exists, `DemandLayer.EconomyHeroPrerequisite` deliberately drops the build
card and resource cost. `StrategicPhaseA.CloneResidualDemand` also drops the Economy build payload.
Consequently, the Phase-B arbiter cannot know that the exact Base card is already committed to a
selected Economy objective.

After a builder exists, `InfrastructureFulfillment.ReserveDeferredEconomyResources` reserves the full
H/E/M/T vector regardless of route ETA. The transition is therefore discontinuous: no protection
while creating the builder, followed by a full resource freeze even when construction remains several
turns away.

### Recovery gap

`TaskExecutor.RunEconomyStep` only delivers a hero to the construction hex. When the building exists,
`MissionContinuityLayer.ReconcileOutcome` and `ResolveActive` retire the Economy intent and repay any
loan immediately. There is no post-build movement objective. Housekeeping only reorganizes armies on
one hex and must not own cross-hex evacuation. A non-Recon hero can therefore remain as a vulnerable,
expensive singleton at the completed extraction site.

## Architectural boundaries

The approved ownership tree remains:

- **Analysis** owns frozen world, route, threat and building facts.
- **Strategy / Demand** owns Economy objective choice and builder eligibility.
- **Strategy / Phase A** owns infrastructure admission and direct fulfillment.
- **Strategy / Phase B** owns the single late-turn card/spend arbitration set.
- **State / StrategicResourceReservation** remains the sole numeric reservation ledger.
- **Missions / EconomyMissionPlanner** owns Economy mission targets, including the selected recovery
  destination.
- **Provisioning** binds an existing hero actor to an Economy mission.
- **Continuity** owns durable Economy/loan lifecycle and the build-to-recovery transition.
- **Execution / TaskExecutor** owns all cross-hex Economy movement through `SafeStepPathing`.
- **Housekeeping** remains same-hex composition work only.

No new production class is required. Existing methods and data structures are extended.

## Staged Economy commitment

### Stage 1 — option claim

As soon as a Base objective is selected, the exact hand-card instance is retained on both the Hero
prerequisite and the unresolved residual demand. Its H/E/M/T cost is not reserved at this stage.

`MaterializationReservation` exposes one query that answers whether a concrete hand-card instance is
claimed by an unresolved Economy build. `TempoCandidateProvider`, the single Phase-B candidate-space
owner, rejects any PlayMat or PlayNonCombat candidate that consumes that exact instance.

The claim is identity-based, not definition-based. A second copy of the same Base definition remains
playable. Extraction construction has no hand build card and therefore creates no card claim.

The claim is rebuilt from current demands each turn and disappears when the objective is invalid,
completed, or no longer selected. It is not a second persistent ledger.

### Stage 2 — near-term resource reservation

`InfrastructureFulfillment.ReserveDeferredEconomyResources` remains the only writer of Economy
H/E/M/T reservations, but it reserves only when the best eligible route is:

- already on the construction target; or
- no farther than the matched builder's full per-turn movement budget.

This is a one-turn commitment horizon. A distant builder protects the exact Base card but leaves
H/E/M/T spendable. Once construction is reachable on the next normal movement turn, the existing
owner-tagged Economy reservation protects the complete build vector.

The ledger remains turn-scoped. Re-analysis recreates a still-valid near-term reservation. Completion,
invalidation and existing terminal cleanup release it through the same owner key.

## Builder recovery lifecycle

### Protected destination

A recovery destination must be an owned building whose remembered/current building facts identify it
as `IsBase` or `IsStartingCitadel`. A facility-only resource building is not a protected destination.

`EconomyMissionPlanner` selects one stable recovery hex when recovery begins. Candidates are ordered
by:

1. safe-path reachability;
2. safe-path cost;
3. lower current asset-threat severity;
4. Citadel before ordinary Base;
5. coordinates as deterministic final tie-breakers.

If the construction hex is already an owned Base/Citadel, recovery is immediately satisfied. This
covers a newly founded Base without an unnecessary return trip.

If no protected hex currently has a safe path, the recovery intent stays active and retains its actor.
It retries after the next snapshot instead of retiring the hero or allowing another mission to claim it.

### Recovery decision by actor role

After a Facility/Base build, Continuity applies this policy:

- an unassigned or non-Recon hero outside a protected building always enters recovery;
- a suspended Raid donor remains suspended, enters recovery, and is repaid only after safe arrival;
- a Recon donor at low threat is repaid immediately and resumes its durable Recon role;
- a Recon donor at high threat enters recovery first and remains suspended until safe arrival.

“High threat” uses the existing Economy builder immediate-threat policy, extended in its current owner
to cover both an urgent `AssetThreatSnapshot` and honest known-enemy proximity at the builder hex.
No second threat formula is added in Continuity.

### Existing mission completion

The Economy intent gains a recovery state/target within its existing objective payload. The original
build completion no longer implies unconditional retirement.

On build completion, Continuity either:

- retires immediately because the actor is already protected;
- repays a low-risk Recon loan and retires; or
- transitions and rekeys the same durable Economy intent to `ReturnBuilder`.

`EconomyMissionPlanner` emits the active recovery intent even though the original extraction/Base
demand is now satisfied. Recovery carries no build card, H/E/M/T requirement or build-follow-up AP.

`ProvisioningManager` must bind recovery to its existing preferred mover only. It must not borrow or
substitute another hero for the return trip.

`TaskExecutor.RunEconomyStep` reuses the current safe-step movement path for `ReturnBuilder`. At the
recovery hex it reports a genuine reached objective rather than requesting another infrastructure
follow-up. Continuity then retires the Economy intent and repays a suspended donor, if present.

### Direct Phase-A construction

A hero already on the target can build directly without an Economy delivery mission. This path must
produce the same recovery policy.

The existing Economy builder eligibility owner selects and records the deterministic on-target builder
for the infrastructure candidate. After the authoritative build succeeds and Phase A refreshes the
snapshot, it hands the completion fact and actor id to one Continuity transition method.

That method is idempotent:

- if a matching Economy intent exists, normal outcome reconciliation remains its owner;
- otherwise it creates exactly one `ReturnBuilder` intent when recovery is required;
- it suspends a matching Recon/Raid donor only when recovery must delay that donor;
- it creates nothing for a low-risk Recon actor or an already protected actor.

There is one recovery transition implementation shared by mission-delivered and direct builds.

## Failure and cleanup rules

- Missing/dead recovery actor retires the recovery intent and releases its owner-scoped reservation.
- Destroyed/captured recovery destination causes destination reselection from the latest snapshot.
- A blocked safe step keeps the intent and actor claim; it is not classified as successful completion.
- No-path recovery does not reserve build resources because construction is already complete.
- Original loan identity is preserved across recovery and repaid exactly once.
- Turn-end strategic reservation leak assertions remain unchanged.
- The reusable empty army shell remains at its current hex and is not moved by recovery. Housekeeping
  may later reuse or locally reorganize it under existing rules.

## Required code changes

Existing files only:

- `DemandLayer.cs`: retain exact Base card on Hero prerequisite; consolidate high-threat predicate;
  expose/reuse deterministic on-target Economy builder selection.
- `StrategicPhaseA.cs`: retain Economy build-card metadata in residuals; pass direct-build completion
  to Continuity after snapshot refresh.
- `MaterializationCandidateBuilder.cs`: add the unresolved Economy card-claim query to
  `MaterializationReservation`.
- `TempoCandidateProvider.cs`: structurally exclude candidates consuming a claimed exact card.
- `InfrastructureFulfillment.cs`: apply the one-turn numeric reservation threshold and report the
  selected direct builder.
- `AiStrategyV2Pipeline.cs`: extend existing Economy task/target data for recovery.
- `EconomyMissionPlanner.cs`: choose protected destination and emit durable recovery proposals.
- `ProvisioningManager.cs`: enforce preferred-actor-only recovery provisioning.
- `MissionIntent.cs`: own transition, rekeying, loan delay/repayment, destination invalidation and
  direct-build registration.
- `TaskExecutor.cs`: distinguish build delivery from recovery arrival.
- `AiEconomyDecisionTests.cs`: extend the existing approved Economy Editor-test suite; no new test
  framework or test class.

No Housekeeping production file changes are permitted for this feature.

## Verification scenarios

At minimum:

1. Hero prerequisite claims the selected Base-card instance without reserving its H/E/M/T.
2. Phase B cannot consume the claimed instance but can consume a second identical card instance.
3. A route beyond one full movement turn creates no H/E/M/T reservation.
4. An on-target or one-turn route creates the complete existing Economy reservation.
5. Completing or invalidating the objective removes both claim and numeric reservation.
6. A non-Recon hero completing extraction transitions to recovery.
7. A newly founded Base is already protected and creates no recovery trip.
8. Low-threat Recon loan is repaid immediately.
9. High-threat Recon loan remains suspended until protected arrival.
10. Raid/non-Recon loan remains suspended until protected arrival.
11. Direct Phase-A construction follows the same recovery policy exactly once.
12. Recovery uses the original actor, does not substitute another hero, and has no build resource cost.
13. Missing destination is reselected; temporary no-path retains the intent.
14. Arrival completes recovery, repays the donor once, and leaves Housekeeping to local garrison work.
15. Existing Economy, Recon, Raid, AP/resource reservation and materialization tests remain green.

Verification before integration is `dotnet build Assembly-CSharp.csproj` plus the targeted Editor tests
when the project runner is available. If the connector environment lacks the Unity-generated project
and runner, the limitation must be reported; successful compilation/tests must not be claimed.
