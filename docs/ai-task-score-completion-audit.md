# AI V2 — unified TaskScore completion audit

Status: **NOT READY TO MERGE**. This file is an execution hand-off, not an assertion that the refactor is finished. Scope is the six world tasks: Extraction, Base, Raid, Explore, Refresh, Surveil. Development, Defence and Production are excluded from this migration.

## Changes actually committed in this continuation

- `bcfd19f`: `MissionAdmissionPolicy.AdmissionRank` no longer deducts Economy AP and distance a second time after the canonical TaskScore price/delivery.
- `db6adfa`: `EconomyMissionPlanner` gives fresh mission `BaseValue` the complete `AxisDemand.Value`, not the pre-delivery `EconomySiteValue`. Lifecycle urgency remains in `LocalAdmissionScore`, separate from intrinsic value. A matched refreshed incumbent also uses its full demand value; ReturnBuilder uses lifecycle priority instead of synthetic task merit.
- `9a66de3` and `bd25857`: introduce `Assets/Editor/AiUnifiedTaskScoreTests.cs` and its `.meta`.
- `a4dad4e`: a durable Raid pins its existing primary *before* combat projection and TaskScore costing, instead of scoring a cheaper free actor then substituting the preferred actor during funding. Fogged continuation now prices its known pinned actor. Unknown actor is logged as `none`, not a potentially real ID 0.
- `d3d470b`: regression test for the pinned Raid actor and ID 0.

## Unresolved defects — must fix before merge

### 1. Base economic need is incorrectly applied to unrelated production

File: `Assets/Scripts/Ai/V2/Strategy/Demand/DemandLayer.Economy.cs`, `AddBaseCandidates` and `BaseResourcePriority`.

Current: `economicGainFact = facts.HexYield` (sum of all actually collectable type gains), `basePriority = Max(ResourcePriority(type))`, then `TaskScoreEvaluator.EconomicHexBenefit(economicGainFact, basePriority)`. A tiny shortage-related gain can assign the *whole* 12-point deficit bonus to a large quantity of unrelated marginal income. This violates the original spec's resource-specific marginal gain condition.

Fix in the existing Economy demand owner; do not introduce a second scoring manager. Iterate each `ResourceType` the actual Base card can collect, obtain the same per-type marginal gain as `StrategicCardEvaluator.BaseCardMarginalGain` (currently private; expose narrowly or return the per-type breakdown from the existing card semantic owner), calculate the *physical* contribution once from total gain and a deficit contribution tied to each type's own marginal gain and `TaskScoreEvaluator.ResourcePriority`. Cap the **aggregate** deficit contribution at `taskScoreEconomicDeficitBonusMax` (12), rather than 12 per type. Keep `Payback` based on total actual marginal income. Preserve the existing `ExistingValueLoss` and zero bonus for no corresponding gain. Test a Base with Energy +0.1 (critical) and Human +1 (not needed) versus a Base with Energy +1 and Human +1: the tiny Energy component must not receive the full shortage bonus.

### 2. Base staging drops negative-value but useful candidates before urgency

File: the same `AddBaseCandidates` method.

Current `bool meaningful = siteOnlyScore.Value > allocatorSliceEpsilon || committed` is evaluated *before* `stagedBase = meaningfulDemands...` and before `MarkBaseExpansionCandidate(...)`. Consequently a strategically useful but costly non-committed site never stages, so urgency cannot admit it on later turns. This regresses the existing staging contract and original spec §48.

Fix only the predicate: `meaningful` should mean at least one positive **world benefit** (economic, payback, airfield, global, front, corridor, proximity where appropriate, terrain defence) or an existing commitment, *not* positive net intrinsic score. Keep cost/risk/loss negative in TaskScore and keep the `demand.Value + candidateUrgency >= economyBaseDemandMinValue` check at final admission. Ensure an empty/benefitless site is not staged solely because proximity is positive; use original `ReasonValue` semantics or an explicit set of meaningful physical/strategic contributions excluding generic proximity-only. Test a useful Base with net negative value receives staged urgency and can clear threshold on later turns; an empty candidate does not.

### 3. Continuity fallback still holds a legacy site-only value

`MissionContinuityLayer.BeginEconomyDelivery` records `EconomyIntent.BuildValue` from `demand.EconomySiteValue` (site merit). `EconomyMissionPlanner.Propose` now correctly uses `refreshed.Value` for an incumbent when a matching refreshed demand exists, but falls back to `e.BuildValue` when none does. That fallback is **not guaranteed to be a TaskScore.Value**. Also verify that the refreshed demand's selected builder matches the pinned commitment before transporting its full delivered price. Maintain one continuity owner and one scoring owner; do not let Missions reselect a builder or independently recalculate an entire alternative world score.

Fix the stored, immutable intrinsic TaskScore value/appropriate score provenance at the existing continuity hand-off; preserve `BuildValue` if it is used as an operational site-merit fact. On refresh, use the pinned builder's observed cost/delivery rather than another builder's cheaper route. Regression: committed builder A far from target, builder B now appears nearby; mission intrinsic, requirements and preferred actor must all agree, and an incumbent with no refreshed demand must not revert to site-only score.

## Validation/integration required

- Add/execute Base per-type and staging, Economy incumbent-pinned and missing-refreshed tests; the already-committed tests cover generic Fold, marginal=0, common pricing, fresh Economy transport, admission no double-charge, pinned Raid and ArmyId 0.
- Run Unity C# compilation for both Assembly-CSharp and Assembly-CSharp-Editor, then Editor tests. This environment has **no Unity or dotnet executable**, and the GitHub branch showed **no Actions runs**. Test files existing in Git do not imply passing results.
- Playtest saves with six simultaneous world-task kinds; compare raw facts, per-slot contributions, final intrinsic `TaskScore.Value`, axis-only `EffectiveValue`, selected/rejected and actual outcome. Check no fact counted in two slots and same physical price gives identical contribution across task families.
- Recompare `master` HEAD immediately before integration. `master` advanced during this continuation (from six to ten master-only commits), including gameplay prefabs, Raid lifecycle/tests and `docs/ai-scoring-unification-task.md`. Do not overwrite unrelated work or force-update master. Merge/rebase only after reconciliation and tests.

Completion criterion: every world-task intrinsic value, including resumed/continuing variants where meaningful, is explainable exclusively by world/objective facts → fixed TaskScore slots → one task-kind-independent `TaskScoreEvaluator.Fold` → Value, with continuity policy kept outside the fold.
