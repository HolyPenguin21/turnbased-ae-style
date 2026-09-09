# AI Strategy V2 Economy Axis Design

This document records the approved design supplied by the project owner for the
`feature/ai-v2-economy-axis` implementation. The complete acceptance contract is
the attached task specification, "AI Strategy V2 — полноценная ветка Economy".

## Architecture

Economy is a horizontal peer of Recon and Development inside the existing V2
pipeline. `WorldAnalysis` owns frozen economic facts, `DesireEvaluators` owns
axis intensity, `DemandLayer` owns extraction/Base objective selection,
`EconomyMissionPlanner` owns hero-delivery mission creation, and the existing
allocator, provisioning manager, task executor, continuity registry and bounded
mid-turn loop carry the task. `InfrastructureFulfillment` and
`BuildingPlayExecutor` remain the route to the authoritative
`InfrastructureActions` mutations.

No Economy manager, pipeline, snapshot, AP pool, pathfinder, resource ledger,
building executor, housekeeping branch, or reaction-loop owner may be added.
The sole new production class is `EconomyMissionPlanner`; related enums/data
belong in their existing owner files.

## Behavioral contract

- Economic pressure is resource-specific and combines own income, living
  opponent median, hand need, discounted remaining-deck need, operational
  reservations, spendable stock, runway and repeated resource starvation.
- Economy desire uses the existing Radar smoothing and normalization path and
  remains latent (not zero) when no actionable site/Base opportunity exists.
- Extraction and Base candidates are evaluated before deterministic sorting;
  coordinates are final tie-breakers only.
- A hero already at the target builds directly through Phase A. Otherwise an
  Economy mission performs at most one movement step per execution call and
  returns to local Economy re-admission after observation refresh.
- Free hero armies may keep multi-turn Economy missions. A Soft Recon or
  not-started Soft Raid actor may be borrowed only when move plus build can
  complete in the current turn and the configured loan net-value hysteresis is
  exceeded. Hard, critical recovery/surveillance/defence/combat and already
  borrowed actors are ineligible.
- Donor intent identity is retained and suspended without ageing, stall or reap.
  Every Economy terminal path restores donor ownership at the actor's current
  hex and releases claims and owner-scoped build-follow-up reservations.
- `StrategicResourceReservationLedger` remains the only strategic ledger;
  Economy reservations expire within the current turn and owner-aware
  affordability excludes only the mission's own key.
- Typed Economy re-entry consumes only Economy-relevant invalidations or local
  continuation after a settled Economy step. `StrategicReactionPass` remains an
  end-of-turn/bounded external-interrupt safety net.
- Runtime focus becomes `ReconEconomyDevelopment` only after the complete
  vertical slice is wired.

## Verification contract

The implementation must cover the 41 Editor-test scenarios from the approved
task: analysis/desire, site and Base scoring, actor borrowing/restoration,
reservation isolation and cleanup, bounded execution/re-entry, exhaustive enum
handling and leak/no-progress guards. The feature branch is pushed and a PR is
opened against `master`; merge requires a separate owner instruction.
