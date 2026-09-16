# AI V2 — unified TaskScore completion audit

Status: **CODE BLOCKERS ADDRESSED; UNITY VALIDATION PENDING — DO NOT MERGE YET.** This is the hand-off for the six migrated world tasks: Extraction, Base, Raid, Explore, Refresh and Surveil. Development, Defence and Production remain outside this migration.

## Implemented changes and ownership

- `bcfd19f`: `MissionAdmissionPolicy` stops deducting Economy AP and distance a second time after canonical TaskScore CardPrice and Delivery.
- `db6adfa`: fresh Economy proposals transport complete delivered `AxisDemand.Value` into `MissionProposal.BaseValue`. Economy wait urgency stays lane-local, separate from cross-axis intrinsic value.
- `9a66de3`/`bd25857`: introduce `Assets/Editor/AiUnifiedTaskScoreTests.cs` and its Unity `.meta`.
- `a4dad4e`/`d3d470b`: durable Raid projections use the pinned primary for viability and travel/AP costing; unknown actor is not encoded as the legitimate ArmyId 0. Regression coverage added.
- `43c571f`: in existing Base demand/card scoring owners, obtain marginal output separately for each resource the real Base card can collect, use the same `TaskScoreEvaluator.EconomicHexBenefit` physical scale with shortage tied to that *resource's own* positive gain, and cap aggregate deficit bonus at `taskScoreEconomicDeficitBonusMax`. Payback continues to use actual total marginal output, existing-value loss stays separate. `HasMeaningfulBaseBenefit` stages intrinsically useful bases despite temporarily negative net score, without promoting empty proximity-only sites. Two tests added.
- `9c2f919`: store nullable `EconomyIntent.IntrinsicValue` separately from operational `BuildValue` at the existing Continuity hand-off/intent creation and progress update. A durable Economy mission never falls back from full canonical intrinsic score to site merit; a missing historical score defaults to neutral zero rather than inventing value. Refreshed demand is accepted only for the same kind, site, resource, builder identity and compatible card. Existing pinned builder is supplied back to Economy Demand's existing builder-selection owner for Extraction and Base, so a cheaper newly arrived hero cannot silently donate its price or delivery to a different durable actor. Operational requirements use the same witnessed pinned route and actor. Two continuation/pinning regression tests added.

**Architecture:** no new vertical scoring or ownership layer. `TaskScoreEvaluator` remains the semantic value fold, `StrategicCardEvaluator` owns Base card's actual marginal yield, Economy Demand selects/costs the builder, Continuity owns persisted intent and pinned identity, Missions transport matching scored demand and requirements, Admission only applies non-intrinsic/lifecycle policy.

## Checks actually performed

- GitHub Actions [Base correction, run 35080801170](https://github.com/HolyPenguin21/turnbased-ae-style/actions/runs/35080801170): exact-scoped patch application, `git diff --check` and branch push succeeded.
- GitHub Actions [Economy correction, run 35081564498](https://github.com/HolyPenguin21/turnbased-ae-style/actions/runs/35081564498): exact-scoped patch application for five code/test files, `git diff --check`, scope check and branch push succeeded. Two initial workflow attempts failed before code changes due to patch/import transport errors; third run succeeded, and failed one-time workflows were removed from tracked files.
- GitHub Actions [read-only integration dry-run, run 35081881183](https://github.com/HolyPenguin21/turnbased-ae-style/actions/runs/35081881183): `git merge --no-commit --no-ff origin/master` merged the refactor cleanly against master `765336ee08cd8ca9c65e83b3e86eb4e966fdda13`, including auto-merge of `MissionContinuityLayer`; checked no unmerged paths and clean TaskScore source/test whitespace. No merge commit/push was made. An earlier overbroad whitespace check failed only on unrelated scene/texture `.meta` trailing whitespace incoming from master; the scoped rerun succeeded without editing those assets. The temporary dry-run workflow has been removed from the branch.

## Outstanding validation — mandatory before production merge

1. **Compile Unity game and Editor assemblies**, including `Assembly-CSharp.csproj` and `Assembly-CSharp-Editor.csproj`, at zero errors and zero warnings. GitHub runner checkout did not contain these Unity-generated project files; compilation was not run. Do not infer success from GitHub Actions green merge check.
2. **Execute EditMode regression tests** (`AiUnifiedTaskScoreTests` and existing Economy/Raid/Recon test suites) in Unity. Tests were added and reread but **not executed**; no Unity test runner/license available here. Validate actual end-to-end urgency staging across turns as well as the scoring predicate tests.
3. **Playtest simultaneous six-world-task scenarios** and compare TaskScore raw inputs, per-slot contributions, intrinsic Value, radar-scaled EffectiveValue, selection, real actor and outcome. Confirm no duplicate physical cost and no wrong-builder substitution.
4. **Refresh master HEAD immediately before integration**. The successful read-only dry-run covered master `765336ee` only. Do not overwrite unrelated scene/prefab/PNG/Raid lifecycle work or force-push.

Completion criterion: each world-task intrinsic value (including continuing missions where meaningful) is sourced from world/objective facts, canonical TaskScore slots and a single task-kind-independent Fold. Lifecycle priority remains outside intrinsic score. Code changes are implemented and recorded, but full validation and production merge remain pending.
