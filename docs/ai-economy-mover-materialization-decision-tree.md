# Economy mover materialization — decision tree (reusable analysis pattern)

## Review round 11 (2026-09-14) — atomic Economy preparation

| Issue in `ae96a80` | Root cause and correction |
|---|---|
| Extraction and composition shared one execution step | `TaskExecutor` now ends after extraction; Analysis observes the actor before preparation and movement are re-admitted. |
| Create-tier could leave a paid empty shell | `ArmyActions.CreateArmyWithMember` preflights the shared batch-transfer rules before AP spend/registration and commits the first member atomically. |
| Composition AP was underclaimed | Shared `ArmyActions.TransferMembersApCost` covers reinforcement and unload destinations; Execution subtracts actual composition AP before checking the remainder. |
| Direct preparation masqueraded as actor creation | `EconomyPrepared` is separate from `ActorMaterialized`; stale pre-mutation paths no longer claim mutation. |
| Donor-only preparation stopped the loop | The existing Analysis observation owner publishes Actor invalidation from `EconomyPrepared`. |

Responsibilities stay horizontal: Map/domain actions → Provisioning → Execution → Analysis →
Continuity. Aggression/Defence is unchanged. Unity compilation/play-test remains required because
the connector workspace had no generated `.csproj`, .NET/Mono compiler, or Unity editor.

## Review round 10 (2026-09-14) — extraction AP fix, rough-estimate lower bound, physical reservation
## timing, direct-army path unified into the same deferred-apply mechanism

| # | Issue | Fix |
|---|---|---|
| P0 | `ProvisionedMission.ClaimedAp = deferredPreparation.RealAp` dropped `GarrisonExtractionCandidate.ApCost` (CreateArmy's 2 AP, or a hero's late-join charge into an already-activated Shell/Host) entirely — for Create-tier, `remainingEnvelope = pm.ClaimedAp - spentSoFar` in Execution became `RealAp - 2`, so `prep.RealAp > remainingEnvelope` was ALWAYS true: extraction succeeded, hero was pulled out, then the mission reported "AP no longer available" and had to be re-found next admission pass every single time | `PlanEconomyCompletion` gained `alreadyCommittedApCost` (subtracted from `apEnvelope`/`apPoolRemaining` before any feasibility check — the direct-army path keeps passing the default 0). The deferred branch now sets `ClaimedAp = deferredPlan.ApCost + deferredPreparation.RealAp` — the TOTAL funded amount — so Execution's existing `remainingEnvelope` math is correct without any further change there. |
| P1 | The Provisioning-side rough pre-check (`plan.ApCost + Hero.ActivationApCost + Max(BuildApCost, MinimumFollowupAp)`) was not a true lower bound — it unconditionally added activation/build costs that the real plan might not actually incur (hero already at target, build not completable this stage) — so it could `continue` past (reject) a genuinely affordable candidate before `PlanEconomyCompletion` ever got to look | Reduced to `plan.ApCost` alone — the one cost that is unconditionally real regardless of hex/turn specifics. `PlanEconomyCompletion` remains the sole authority for everything else. |
| P1 | A deferred mission's `ClaimedPhysical`/`ReservationOwner` stayed at their zero/null defaults for the whole window between Provisioning-decision and Execution-application — `InfrastructureFulfillment.ReserveEconomyCost` was only ever called AFTER materialization. A second Economy mission provisioned later in the same batch pass could see the resource pool as still fully free and claim the same resources `StrategicSpendability.FitsSpendableResources` had already approved for the first | The deferred branch now reserves (`InfrastructureFulfillment.ReserveEconomyCost`) and sets `ClaimedPhysical`/`ReservationOwner` on the `ProvisionedMission` immediately, at the moment it commits to being deferred — not after Execution applies it. The matching call was removed from Execution (would have double-reserved). Stale/failure already routes through the existing `ReleaseEconomyReservation` — no new release lifecycle needed, it only needed `ReservationOwner` to actually be set this early. `StrategicSpendability.FitsSpendableResources`'s existing `excludeOwner` parameter (already passed as `prep.OwnerKey` on both the Provisioning-time and Execution-time calls) means re-checking after the reservation exists correctly excludes the mission's own hold — verified this was already correct, not a new fix. |
| P0 | The direct-army path (hero already a real, live field army) still called the old `FinishEconomyBuilder`, which applied `ApplyEconomyArmyLightening` synchronously INSIDE `ProvisionEconomy` — a live roster mutation outside Execution's observation window, before any `ExecutionResult` exists. This was a pre-existing, explicitly documented exception (`ProvisioningResult`'s own comment: "Raid assembly and Economy's same-hex builder lightening are the transactional exceptions") predating this whole review series, but the review correctly pointed out the fix applied to the synthetic garrison actor only, leaving this asymmetric carve-out in place | `FinishEconomyBuilder` is DELETED (had exactly one caller). The direct-army branch of `ProvisionEconomy` now calls the same pure `PlanEconomyCompletion` (with the REAL hero, `alreadyCommittedApCost: 0`) and returns a `ProvisionedMission` with the plan PINNED, never applied. `TaskExecutor.MaterializeEconomyGarrisonBuilder` is renamed `ApplyEconomyPreparation` and generalized: it now branches on whether extraction is needed (`EconomyExtractionGarrisonArmyId >= 0`) or the hero is already real, then applies the (either way) pinned `EconomyCompletionPlan` identically. A new `ProvisionedMission.EconomyPreparationPending` flag (true whenever there is a real composition change OR a donor loan still to suspend — donor-suspend bookkeeping is not itself an ArmyActions roster mutation, but still deliberately left for Execution, not Provisioning) drives whether this extra step runs at all; when a hero's roster is already exactly right and no donor is involved, Provisioning marks nothing pending and the mission proceeds straight to movement in the same admission pass, exactly as it always did — no AI player pays an extra admission pass for a step that would apply zero changes. |

**Not done — P0 "split extraction from composition-prep into separate task steps"** (the review's own suggested design: extraction is step N, composition prep is step N+1, movement is step N+2). Current state keeps extraction (when needed) and composition-prep as ONE step (round 5's fix — was already a bigger change than "just move it to Execution"), with movement still deferred to the next step. Not split further this round: each half is already independently atomic with honest partial-failure reporting (`ContainerCreated` vs `ActorMaterialized`), and splitting further adds a real, deliberate pacing cost (every deferred Economy mission would need one more admission pass before its hero can even start moving) that is a genuine gameplay-tempo trade-off, not a pure correctness fix — left as an explicit, separate decision if the user wants to pursue it.

`dotnet build` on both `Assembly-CSharp.csproj` and `Assembly-CSharp-Editor.csproj` — 0 errors/0 warnings, checked after each incremental edit. **This round changed the direct-army path — the most commonly executed Economy code path in the game — for the first time in this whole review series. Still NOT play-tested in Unity; this is now the single highest-priority thing to verify before trusting any of this in a real game.**

## Review round 8/9 (2026-09-14) — composition planning moved to Provisioning; ONE execution lifecycle

A further external review correctly rejected round 6/7's "structurally blocked" verdict on both
items round 7 left open, pointing at concrete existing primitives this session had missed:
`ArmyData.ComputeCapacity(IEnumerable<UnitData>, bool)` (a pure projection, not tied to a live
army), `ArmyCapacityRules`, and `ArmyData.MaxMovement`/`CurrentMovement` (`Members.Min(...)`,
already pure over any member list). Re-investigated with those in hand — both items turned out to
be achievable without new duplicated math or a new architectural seam.

**P0 — Execution no longer re-plans Economy composition.** `ProvisioningManager.FinishEconomyBuilder`
was split into a pure decision half, `PlanEconomyCompletion` (donor lookup, safe-route distance,
`PlanEconomyArmyLightening` call, AP/resource feasibility — returns a new `EconomyCompletionPlan`
struct) and the existing apply/reserve/donor-suspend tail (unchanged, still `FinishEconomyBuilder`,
still the sole caller for the direct-army path). For a deferred garrison-extraction candidate,
Provisioning now builds a READ-ONLY preview of the not-yet-real container
(`ArmyData.CreateVisualSnapshot()` — an existing primitive built for exactly this: `Id` stays `-1`,
never touches `ArmyRegistry`) with the hero's `UnitData` appended to `Members`, and the real
container's pre-existing members + `HasActivatedThisTurn` state copied in when one already exists
(Shell/Host tier). `PlanEconomyCompletion` is called against this preview — the EXACT same function
the direct-army path uses, so there is no second, divergence-prone implementation — and the
resulting `Unload`/`Reinforcement`/`Donor`/`RealAp`/`StageCost`/`OwnerKey` decision is pinned onto
`ProvisionedMission.EconomyExtractionPreparation`. `TaskExecutor.MaterializeEconomyGarrisonBuilder`
no longer calls `FinishEconomyBuilder` (or `PlanEconomyArmyLightening`, or the donor/route logic) at
all — it applies the pinned `GarrisonExtractionCandidate` (hero) via the existing
`ApplyGarrisonExtraction`, cheaply RE-VALIDATES only the two facts that can actually shift between
Provisioning and Execution within the same batch pass (AP still available, resources still
spendable — never composition/donor/route), applies the pinned `Unload`/`Reinforcement` via the
existing `ApplyEconomyArmyLightening`, and stamps the reservation/donor-suspend from the pinned
decision. `PlanEconomyArmyLightening` gained an `identityArmyId` parameter (defaulting to
`builder.Id` for both pre-existing callers, unchanged behaviour) so the loan-protection intent check
can be pointed at the REAL container id a Shell/Host preview stands in for, instead of the preview's
own always-`-1` id. As a side effect the deferred mission's `ClaimedAp` is now the AUTHORITATIVE
`EconomyMissionClaimedAp` figure (includes lightening/reinforcement AP), not the old coarse
pre-composition estimate — and the Provisioning-side candidate loop now `continue`s to the next
ranked builder if the full plan turns out infeasible, instead of deferring a doomed mission.

*Not fully collapsed into a single ArmyActions transaction*: `ApplyGarrisonExtraction` (hero) and
`ApplyEconomyArmyLightening` (composition, with its own round-6 rollback) remain two sequential
domain calls inside one Execution step, not merged into one preflight-then-commit primitive. Given
composition legality is checked live against the REAL post-hero-transfer container either way (no
projection needed at commit time — only at Provisioning-time planning), and each call is already
individually atomic with honest partial-failure reporting (`ContainerCreated` distinct from
`ActorMaterialized`), this was judged to close the review's actual underlying risk (stranded/
silently-lost mutations) without inventing a new ArmyActions-level transaction type or reversing the
established "a Create-tier shell that fails its hero transfer is kept, never rolled back" precedent
(round 2/3) — which a full merge would have had to either preserve awkwardly or reverse outright.
Revisit only if a concrete scenario shows the two-call sequencing itself (not what each call reports)
causing a problem.

**P1 — one execution lifecycle.** Round 7's three short-circuit helpers are now called from inside a
single new `TaskExecutor.ExecuteMissionCore` — the complete per-mission step (stale-plan check,
deferred-Economy materialization, mover resolve, stale-goal revalidation, kind dispatch, AP/version/
resource stamping, result recording, reservation release). `Execute` (batch) is now a thin loop that
constructs each mission's `ExecutionResult` and calls this core with `singleStepOnly: false`;
`ExecuteStep` calls it once with a singleton queue and `singleStepOnly: true`. Neither re-implements
any lifecycle logic of its own any more. `singleStepOnly` picks between Recon/Raid's own two
PRE-EXISTING execution strategies (`ReconGroundExecutor.Run`/`RunRaid` — continuous multi-step
lookahead — vs `RunStep`/`RunRaidStep` — one atomic step), per the review's own explicit framing
("this strategy difference is fine, it just shouldn't require two lifecycle owners") — it is not a
new fork, it is the parameter that was implicitly duplicated across two copies of the outer loop
before. The two small pre-existing observable differences between the old duplicated bodies (Execute
never sets `NeedsReplan` on a lost/stale mover or unsupported kind and logs a line ExecuteStep
doesn't; ExecuteStep does the reverse) are preserved explicitly via the same parameter, not silently
erased — neither was ever explained as a bug by either round.

`dotnet build Assembly-CSharp.csproj` — 0 errors/0 warnings after every incremental step of this
round (verified after each file edit, not just once at the end). **Still NOT play-tested in Unity —
this round touches the actual composition/AP/donor decision path, the highest-risk area yet; a
Unity playtest before trusting this in a real game matters more for this round than any prior one.**

## Review round 7 (2026-09-14) — Execute/ExecuteStep dedup; plan-immutability left structurally blocked

Follow-up on round 6's two deliberately-deferred items, per explicit instruction to proceed.

**Done — `Execute`/`ExecuteStep` shared plumbing.** Extracted `TryHandleStalePlan`,
`TryResolveMoverOrHandleGone`, `TryHandleStaleValidity` — the stale-plan short-circuit, the
mover-resolve-failed short-circuit, and the `MissionRevalidator` stale-goal short-circuit that both
loops previously re-implemented independently (~120 duplicated lines total). Each closes exactly one
of the four things `Classify`/`Execute`/`ExecuteStep` had drifted on: `Execute` never sets
`NeedsReplan` on a lost/stale mover and logs a debug line each helper's counterpart doesn't;
`ExecuteStep` does the reverse. Rather than silently unify these (risking an unexplained AI-behavior
change for whichever caller didn't have it), each helper takes explicit `setNeedsReplan`/
`logX`/reason-string parameters that reproduce the exact prior per-caller behavior — verified by
diffing each substituted block against what it replaced before compiling. `dotnet build
Assembly-CSharp.csproj` — 0 errors/0 warnings.

Scout dispatch itself (`ReconGroundExecutor.Run` vs `RunStep`) stays unmerged, per round 6's own
reasoning: `Execute` passes the full mission `queue`/`missionIndex` for multi-step lookahead, while
`ExecuteStep` uses a singleton queue + an explicit `StepControl` — two different execution contracts
for Scout, not two copies of the same loop. Merging that would change Recon behavior for
Reaction/legacy callers, outside what a dedup refactor should touch.

**Investigated, NOT done — full plan-immutability at Provisioning.** Traced exactly how far this
could go without a new failure surface: `PlanEconomyArmyLightening` needs a *live* `ArmyData` to
plan against — `builder.Hex`, `builder.Members`, `builder.MaxMovement`, plus
`ArmyActions.CanTransferMembers` capacity checks against the real destination object. For Shell/Host
tiers the destination container already exists live at Provisioning time (found by
`ReusableArmySelector.FindReusableAt`/`EconomyHostCandidates`), but the method's own very first gate
— `!builder.Members.Any(u => u.IsHero)` → bail with an empty plan — means calling it against that
container *before* the hero is added would just report "nothing to lighten", not the real plan for
once the hero *is* there. Making this genuinely pre-computable would mean decoupling
`PlanEconomyArmyLightening` from a live `ArmyData` (a `(hex, projected members, max movement)`
struct instead) and replicating `ArmyData.MaxMovement`'s live computation for a composition that
does not exist yet — meaningfully more code and a new place for army-capacity math to silently
diverge from the real one, with no test coverage and no gameplay-level compiler feedback available in
this environment to catch it. This is also in direct tension with round 4's own already-accepted
trade-off: the whole reason materialization moved INTO Execution was that the actor cannot be
"planned around" before it is real. Left as a genuine open architecture question, not attempted —
revisit only as an explicit, scoped decision if the user wants the virtual-army design pursued.

## Review round 6 (2026-09-14) — double version bump, lightening atomicity, snapshot, actor identity

An external review of round 5 found 4×P0/P1-ish issues plus a P2, all fixed this round except the
two explicitly deferred items below. Compiled clean (`dotnet build Assembly-CSharp.csproj`, 0/0).

| # | Issue | Fix |
|---|---|---|
| P0 | `FinishEconomyBuilder` → `ProvisioningResult.Ok` bumps `V2StateVersion` itself when `preparedMembers > 0`; `TaskExecutor.StampVersion` (the caller's caller) then bumps AGAIN because `ActorMaterialized` is true — one mutation, two version bumps | `ProvisioningResult.Ok`/`FinishEconomyBuilder` gained a `bumpVersion` parameter (default `true`, unchanged for the direct-army Provisioning-time path). `MaterializeEconomyGarrisonBuilder` passes `bumpVersion: false` — `StampVersion` is now the SOLE bump owner for the whole deferred-materialization Execution step. |
| P0 | `ApplyEconomyArmyLightening`: unload commits, then reinforcement fails — method returns `0`/failure but the already-applied unload is never rolled back, left silently real | Tracks `unloadApplied`; on a reinforcement failure after a successful unload, rolls the unload back via the same `TransferMembersAtomic` primitive (garrison→builder, reversed) before returning failure. Note: the current planner (`PlanEconomyArmyLightening`) never actually produces both `unload` and `reinforcement` non-empty at the same time — this fix removes the dependency on that as an unstated invariant rather than fixing a currently-reachable bug. |
| P1 | Deferred path called `FinishEconomyBuilder(snapshot: null, ...)` — `WorldAnalysis.KnownThreatsAffectingEconomyRoute` sees zero threats, which can silently empty a `ReinforceAtBase` candidate's escort plan and turn a real escort requirement into a bogus `AssemblyInfeasible` right after the hero was for-real extracted | `snapshot` (already available as a parameter on both `TaskExecutor.Execute`/`ExecuteStep`, populated by the orchestrator) is now threaded through `TryHandleDeferredEconomyMaterialization` → `MaterializeEconomyGarrisonBuilder` → `FinishEconomyBuilder` instead of hardcoding `null`. |
| P1 | `ActorMaterialized` conflated two different facts: "the world changed" vs. "a real Economy actor now exists". A Create-tier shell with a failed hero transfer set `ActorMaterialized = true` and `ActualActorArmyId = <hero-less shell id>`, which Continuity could track as this mission's mover | New `ExecutionResult.ContainerCreated` field, set instead of `ActorMaterialized` on that specific path; `ActualActorArmyId` is no longer set for a hero-less shell at all. `ActorMaterialized` now means exactly "a real hero-led mover was materialized". `StateChanged`/`StampVersion`/`MissionOutcomeLedger.MadeProgress`/the Economy `ProductiveStop` classification all now check `ActorMaterialized \|\| ContainerCreated` where "the world changed" is the relevant question, and `ActorMaterialized` alone where "a real actor exists" is. |
| P2 | `Outcome.succeeded` didn't include `ActorMaterialized` — a fully successful hero extraction reported `StateChanged=true, Succeeded=false`, a contradiction telemetry/`WasGenuineExecution` had to work around | `succeeded` now also ORs in `ActorMaterialized` (not `ContainerCreated` — an orphan shell alone is a state change, not this mission succeeding at anything). |

**Deliberately NOT done this round** (both explicitly flagged by the review as pre-existing, larger,
cross-cutting asks rather than new bugs from round 5):

- **Full plan-immutability at Provisioning** (deciding unload/reinforcement/escort composition
  before Execution, so Execution is pure validate+apply with zero re-planning). Structurally blocked
  by the same reason round 3 first noted it: `PlanEconomyArmyLightening` needs a live `ArmyData`
  (hex, members) to plan against, and for a deferred garrison-extraction candidate that `ArmyData`
  does not exist until `ApplyGarrisonExtraction` creates/populates it *inside* Execution. Moving this
  earlier would mean either simulating a virtual not-yet-real army through the planner (meaningfully
  more code, new failure surface) or reworking `PlanEconomyArmyLightening` to plan off a bare
  `UnitData` + hex instead of a live army — either is a real design decision, not a bugfix, and is
  left for an explicit follow-up if the user wants it pursued.
- **Collapsing `TaskExecutor.Execute` into a thin iterator over `ExecuteStep`** so Reaction gets the
  identical atomic-step contract. Checked and rejected as a same-round change: `Execute` passes the
  full `queue`/`missionIndex` list to `ReconGroundExecutor.Run` for Scout missions (multi-step
  lookahead), while `ExecuteStep` calls `ReconGroundExecutor.RunStep` with a singleton queue and an
  explicit `StepControl` — these are two different behavioral contracts for Scout, not just two
  copies of the same loop. Delegating `Execute` to `ExecuteStep` wholesale would change Recon
  execution semantics for Reaction/legacy callers, well outside this round's Economy-only scope. The
  concrete bug this created for Economy specifically (Execute not handling the synthetic actor id at
  all) was already closed in round 5 via the shared `TryHandleDeferredEconomyMaterialization` helper;
  what remains is the two loops' other shared logic (stale-plan check, revalidation, dispatch,
  stamping) still being independently implemented. Revisit before Aggression/Defence starts routing
  Economy missions through `ReactionRoundExecutor` in earnest.

## Review round 5 (2026-09-14) — plan pinning, atomic step split, envelope fix, batch-door parity

An external review of round 4 (the Execution-move) found six issues, all closed in this round,
compiled clean (`dotnet build Assembly-CSharp.csproj`, 0 errors/0 warnings — a real compiler was
available this session, unlike rounds 1-4).

| # | Issue | Fix |
|---|---|---|
| P0 | `TaskExecutor.MaterializeEconomyGarrisonBuilder` re-ran `ResolveGarrisonExtractionCandidate` with `commitments:null, session:null` instead of using the plan Provisioning actually chose and funded — could legally pick a different, or already-claimed, hero/container/tier | `ProvisionedMission` gained `EconomyExtractionPlan` (the exact `GarrisonExtractionCandidate` struct), pinned by `ProvisionEconomy` at the point it defers. Execution now reads `pm.EconomyExtractionPlan` directly — no second resolve. `ProvisioningSession.RegisterSuccess` also now adds the garrison id and (if Shell/Host) the container id to `ClaimedArmyIds`, not just the synthetic `MoverArmyId` — closes the same-pass double-claim window the review flagged. |
| P0 | One `ExecuteStep`/`Execute` call bundled materialization (CreateArmy/TransferMember/lightening) AND movement (`MoveArmyRoutine`) — several canonical mutations in one step | Materialization is now its own terminal step: `TryHandleDeferredEconomyMaterialization` returns immediately after materializing (`StopReason = StepCompleted`) without falling through to `RunEconomyStep`'s movement. The now-real mover is picked up as an ordinary direct-army mission on the *next* admission pass — the same "found again next pass" pattern round 4 already used for the failure case, now applied to the success case too. |
| P0 | `containerCreated` (CreateArmy ran, AP spent) was dropped whenever the following `TransferMember` failed (`materialized == null`) — a real, silent mutation with no `StateChanged`, no version bump | `ApplyGarrisonExtraction` gained an `out int createdContainerArmyId`. `MaterializeEconomyGarrisonBuilder` now sets `result.ActorMaterialized = true` / `ActualActorArmyId` on this path even though it still returns `false` (no mover this pass). `TaskExecutor.StampVersion` now also bumps `V2StateVersion` on `ActorMaterialized`, not just movement/stealth/infrastructure/combat — it never did, even for the successful-materialization case, before this round. |
| P0 | `FinishEconomyBuilder` was called from `MaterializeEconomyGarrisonBuilder` with `apEnvelope: root.ActionPoints` — the player's entire current AP pool, not the ECO-axis amount Provisioning actually funded this mission | Now passes `apEnvelope: pm.ClaimedAp - (AP the extraction itself already spent this call)`, floored at 0. `apPoolRemaining` stays live `root.ActionPoints` (correct as-is: by Execution time each mission already mutates AP for real, no session-tracked cross-mission claim to add back). |
| P1 | `MissionOutcomeLedger.MadeProgress` didn't consider `ActorMaterialized`; a materialized-but-no-mover-this-pass step (`StopReason = MoverLost`) fell into `Classify`'s Economy default case → `Failed`, even though the world genuinely changed | `MadeProgress` now ORs in `e.ActorMaterialized`. `Classify` special-cases Economy `ActorMaterialized && StopReason == MoverLost` → `ProductiveStop` before the switch. `ActualActorArmyId` (already generic Continuity plumbing) carries the real id into `o.MoverArmyId` automatically once set. |
| P1 | `TaskExecutor.Execute` (the batch adapter used by Reaction/legacy paths) had none of `ExecuteStep`'s synthetic-actor handling — resolved `pm.MoverArmyId` directly and reported `MoverLost` for every deferred Economy mission | Extracted `TryHandleDeferredEconomyMaterialization`, called from both `Execute`'s loop (before its `Resolve`) and `ExecuteStep` (same spot as round 4). One shared door, not two divergent copies. |

**Known residual, not fixed this round**: on the `containerCreated && materialized == null` path, `result.ActualActorArmyId` is set to the freshly-created empty shell's id, which `MissionOutcomeLedger` copies into the mission's tracked `MoverArmyId` via the existing generic `ActualActorArmyId.HasValue` plumbing — that shell has no hero yet. Judged low-risk: the mission's own `Outcome` is `MoverLost`/`ProductiveStop` (not `Completed`), so the next admission pass re-provisions from a live `WorldAnalysis` pass rather than trusting a stale `PreferredMoverArmyId` blindly; `IsMobileEconomyHero`/`IsCandidateEligible` both require a hero-led army, so a hero-less shell simply fails that check and falls through to ordinary re-selection. Revisit if a concrete misroute is ever observed in a trace.

**Not done this round (explicitly out of scope, per the review's own P1 framing)**: fully collapsing `TaskExecutor.Execute` into a thin adapter over `ExecuteStep` (the review's suggested "single lifecycle owner" refactor). The narrower fix (shared `TryHandleDeferredEconomyMaterialization` helper) closes the concrete correctness bug without touching `Execute`'s other call sites (Reaction, legacy `AiStrategyV2Pipeline`) or its different iteration/queue semantics — a larger, separate architectural decision if the user wants it pursued.

**Still NOT verified in Unity** — only `dotnet build` confirmed the code compiles. Play-test needed: start a match, let an AI player's garrison hold an idle hero with no field army, and watch `AiDebugLog.log` for `"materialized builder #N for ... from garrison #N"` immediately followed (next admission pass, not the same log line) by ordinary movement toward the FoundBase/BuildExtraction target, with `Concord Base`/`Ashen Base` still getting built.


Captured 2026-09-14 while debugging why AI players stopped founding bases (`Concord Base` /
`Ashen Base`, `CapabilityKind.EconomicExpansionBase`) and extraction facilities
(`CapabilityKind.EconomicInfrastructure`). Kept as a template for future "why doesn't the AI do X"
investigations that hinge on mover/army selection: draw this tree first, mark PURPOSEVERED /
NOT-COVERED nodes from the actual code before proposing a fix.

## Review round 4 (2026-09-14) — materialization moved into Execution

Rounds 2-3 kept discovering new Provisioning-side accounting bugs from the same root cause: garrison
extraction (`ArmyActions.CreateArmy`/`TransferMember`) mutated the world inside
`ProvisioningManager.ProvisionEconomy`, before all affordability checks were known to pass, and
outside the orchestrator's `beforeStep`/`afterStep` observation window entirely (that window opens
only around `TaskExecutor.ExecuteStep`, which runs strictly after Provisioning). Round 4 moved the
mutation to where it belongs, mirroring the pattern `ScoutExecutorKind.AirLaunch` already
established for "the actor does not exist yet":

- `ProvisionEconomy` no longer calls `ApplyGarrisonExtraction`. For a garrison-extraction candidate
  it now only resolves the same pure `GarrisonExtractionCandidate` (tier/hero/cost, no mutation) and
  checks a conservative pre-mutation AP estimate (`plan.ApCost + plan.Hero.ActivationApCost +
  max(BuildApCost, MinimumFollowupAp)` — no live `ArmyData` needed, `EconomyMissionClaimedAp` only
  ever sums `UnitData` stats) against the envelope. If it fits, it returns a `ProvisionedMission`
  with a **synthetic negative `MoverArmyId`**
  (`ProvisioningManager.SyntheticGarrisonExtractionActorId`) and `EconomyExtractionGarrisonArmyId`
  set — nothing has mutated, so `StateChanged` is honestly `false`.
- `TaskExecutor.ExecuteStep` detects `EconomyExtractionGarrisonArmyId >= 0` as the very first thing
  it does (before its own `Resolve(pm.MoverArmyId)` gate, which a synthetic id would otherwise fail)
  and calls the new `MaterializeEconomyGarrisonBuilder`: re-resolves the same
  `GarrisonExtractionCandidate`, applies it for real (`ApplyGarrisonExtraction` — unchanged since
  round 2, still the one owner of this mutation), then runs the shared tail extracted from the old
  `ProvisionEconomy` (`ProvisioningManager.FinishEconomyBuilder` — donor loan, lightening/
  reinforcement, the authoritative AP/resource recheck against the now-live hero, resource
  reservation) against the freshly materialized army. `pm.MoverArmyId` is mutated in place from
  synthetic to real, so everything below (the normal `Resolve`/`MissionRevalidator`/`RunEconomyStep`
  path) sees an ordinary, already-real mover from that point on.
- No rollback machinery survives this round — it is not needed. By construction the hero is only
  ever made real inside `ExecuteStep`'s own step, so a failure past that point (loan rejected, AP
  short after lightening, resources unspendable) is an ordinary "this attempt did not complete"
  outcome, exactly like a partially-moved army already was — not a mutation to undo. The hero stays
  a real field army and is picked up fresh next admission pass (now via the direct-army path, no
  extraction needed). This is what let `FailAfterHero`, `orphanedCreateApThisCall`, and
  `ProvisioningApSpent` all be deleted rather than carried forward.
- `ExecutionResult.ActorMaterialized` (new field, folded into `Outcome.StateChanged`) and
  `ExecutionResult.ActualActorArmyId` (now stamped for every Ground Economy step, not only the
  materialized case — it was silently `null` before) close the observation-window gap review round
  3 flagged: `WorldAnalysis.Observation.PublishStepObservationDelta`'s existing
  `EconomyDeliveryReady` branch already marked `ResourceSite | Actor` invalidation for "arrived,
  nothing else changed" turns; it just had no actor id to attach for Ground missions until now.

**Known, documented residual**: `ProvisioningSession.ClaimedArmyIds` (a same-pass, faster-than-
`MissionIntentRegistry` double-claim guard) is populated with the mission's `MoverArmyId` at the
moment Provisioning succeeds — for a deferred mission that is still the *synthetic* id; it is never
retroactively updated to the real one once `ExecuteStep` materializes it. Within the same
Provisioning pass, a *later* mission's eligibility check could therefore in principle still see the
freshly materialized real army as unclaimed by `ClaimedArmyIds` specifically (though
`MissionIntentRegistry`/`ActorCommitments`-based checks, which every candidate predicate also
applies, should catch it once `MissionContinuityLayer.ReconcileStep` records the new mission's
intent — the same mechanism the non-deferred path already relies on). Judged acceptable for now:
narrow window, redundant safety net degrading rather than disappearing, not a correctness
regression versus what full atomicity would have cost to guarantee. Revisit if a concrete double-
claim is ever observed in a trace.

## The question this answers

For an Economy demand (`FoundBase` / `BuildExtraction`) with a target hex, how does
`ProvisioningManager.ProvisionEconomy` (`Assets/Scripts/Ai/V2/Provisioning/ProvisioningManager.cs`)
pick and materialize the actor that will carry it out?

## The tree

```
                    Economy-демонд на целевой хекс
                    (FoundBase / BuildExtraction)
                                │
                                ▼
                 EconomyBuilderRoutes формирует кандидатов
                 (WorldAnalysis.Economy.cs:332-424)
                                │
                ┌───────────────┴────────────────┐
                │                                 │
        1.1 НЕТ готового героя            1.2 ЕСТЬ готовый герой
        (ни полевой армии с героем,       (полевая ИЛИ "запасной"
         ни запасного в гарнизоне)         в гарнизоне)
                │                                 │
                ▼                        ┌────────┴─────────┐
      rankedBuildersTotal=0              │                   │
      → NoMoverExists            1.2a герой уже        1.2b герой в
      [ПОКРЫТО: честная            ведёт полевую         гарнизоне —
       нехватка ресурса,           армию                 нужен контейнер
       демонд просто ждёт]              │                   │
                                         ▼                   ▼
                                  ResolveArmy()      ResolveGarrisonExtractionCandidate
                                  напрямую,          + ApplyGarrisonExtraction
                                  shell НЕ нужен      (ProvisioningManager.cs:267,314)
                                                             │
                                         │           ┌──────┼──────────────┐
                                         ▼            │      │              │
                                  [ПОКРЫТО]    1.3 ЕСТЬ  1.4/B ЕСТЬ    1.4/A НЕТ
                                  угроза/занятость/  пустая    занятая        ничего —
                                  путь — обычные     армия     армия на      создаём
                                  eligibility-чеки   (shell)   хексе, никем  новую
                                                      на хексе  не занятая   (CreateArmy,
                                                      гарнизона  (не shell)   2 AP из
                                                         │          │        ECO-envelope)
                                                         ▼          ▼            │
                                                  FindReusableAt EconomyHost      ▼
                                                     находит её  Candidates  CreateArmy
                                                         │       — берём      + Transfer
                                                         ▼       МЕНЬШУЮ      Member
                                                  TransferMember      │           │
                                                  герой→shell          ▼           ▼
                                                         │      TransferMember  [ПОКРЫТО,
                                                         ▼       герой→army    добавлено
                                                  [ПОКРЫТО, +   (незанятую)   2026-09-14,
                                                   фикс          │            вариант A]
                                                   2026-09-14:   ▼
                                                   откат, если  [ПОКРЫТО,
                                                   след.        добавлено
                                                   проверка в   2026-09-14,
                                                   этой же      вариант B —
                                                   попытке      лёгкость/
                                                   провалится   безопасный
                                                   — 1.3 не     маршрут уже
                                                   портится     даёт общий
                                                   в 1.4]       PlanEconomy-
                                                                ArmyLightening
                                                                ниже по коду,
                                                                без дублей]
```

## Coverage status (as of 2026-09-14, after review round 2)

| Node | Covered? | Where |
|---|---|---|
| 1.1 no hero at all | Yes — honest `NoMoverExists`, demand retries next turn | `ProvisioningManager.cs` mover-selection loop |
| 1.2a hero already leads a field army | Yes — direct `ResolveArmy`, no container needed | `IsCandidateEligible` else-branch |
| 1.3 free empty shell exists | Yes, **plus** rollback-on-later-failure | `ReusableArmySelector.FindReusableAt` + `FailAfterHero`, activation-charge AP now funded |
| 1.4/B free *populated*, hero-less, unclaimed field army at the same hex | Yes | `EconomyHostCandidates` — smallest-first, excludes hero-led armies, `ActorCommitments` AND `ProvisioningSession.ClaimedArmyIds` |
| 1.4/A no container at all — mint one | Yes | `ArmyActions.CreateArmy`, charged to `funded.Tentative.Ap` (same ECO axis ledger, never double-counted into `session.ApClaimed`), never rolled back if a later step fails (shell persists as a future 1.3 candidate) |

Known residual (documented, accepted, not fixed): if `ApplyEconomyArmyLightening` fully applies and
the AP recheck *right after it* still fails, `FailAfterHero` reverses only the hero's own transfer,
not the lightening/reinforcement batch that already applied. Judged acceptable because that batch's
own atomicity means the only way to reach that line is a fully-applied batch — there is nothing
partial to unwind — and reversing a fully-applied batch would risk a second, larger mutation on an
already-failing path for a case the report itself calls "almost unreachable."

Lightening (spec 1.1.3: unload down to fast units, only over a safe route — spec 1.1.3.1) needed
**no new code**: `PlanEconomyArmyLightening` / `ApplyEconomyArmyLightening` already run unconditionally
on whatever `hero` ends up being, later in `ProvisionEconomy` — reused as-is regardless of which tier
produced the mover.

## Review round 3 (2026-09-14) — ResourceAllocator AP ledger, orphaned-create tracking

Round 2's `ClaimedAp = realAp` fix (excluding already-spent extraction AP from the raw-pool check)
traded one bug for another: `ProvisionedMission.ClaimedAp` also feeds
`ResourceAllocator.RegisterProvisionSuccess`, and `ResourceAllocator.Pack()` prices its pool from
the FROZEN `WorldSnapshot` AP figure (`ResourceAllocator.cs` ~line 613) — it has no live view of
`root.ActionPoints`, so it never learned about AP a mid-turn `CreateArmy`/activation-charge already
spent. `ClaimedAp` was one field serving two different consumers that need two different numbers.

| # | Issue | Fix |
|---|---|---|
| P0 | `ResourceAllocator` under-reserves already-spent extraction AP against its frozen-snapshot pool, causing spurious re-pack/displacement of other missions this turn | Added `ProvisionedMission.ProvisioningApSpent` (the already-spent amount). `ClaimedAp` stays `realAp`-only for `ProvisioningSession`/raw-pool checks; all three `RegisterProvisionSuccess`/`CheckProvisionEnvelope` call sites (`AiStrategyV2Pipeline.cs` ×2, `ReactionRoundExecutor.cs`) now pass `ClaimedAp + ProvisioningApSpent` to the allocator specifically. |
| P1 | A rollback that successfully returns the hero (`rolledBack == true`) still hid AP spent by `TransferMember`'s activation charge into an already-acted container | `FailAfterHero`'s `stateChanged` now also checks `extractionExtraApSpent > 0f`, not just `extractionCreatedContainer \|\| !rolledBack`. |
| P1 | A candidate that ran `ArmyActions.CreateArmy` (AP spent) and then failed its own `TransferMember` was simply abandoned by the mover-selection loop — the spend never reached any exit path's `StateChanged`/AP accounting, including total failure (`NoMoverExists`) | `ApplyGarrisonExtraction` now reports `containerCreated` truthfully (whether `CreateArmy` actually ran, not inferred from the chosen tier). The loop accumulates every such orphaned spend (`orphanedCreateApThisCall`) across ALL attempted candidates, win or lose, folds it into `extractionExtraApSpent`, and a new `FailNoHero` wrapper routes every return in the "no hero at all" branch (including the final `NoMoverExists`) through it. |

Left open, tied to the same underlying question (**decision pending, not yet acted on**):

- **P0 — state-changing materialization (`CreateArmy`/`TransferMember`) still runs inside
  `ProvisioningManager`, before all affordability checks complete**, not as an atomic Execution
  step. The review's concrete failure scenario (hero extracted directly onto a target hex whose
  `RunEconomyStep` then sees `army.Hex.Equals(target.TargetHex)` and reports no movement) was
  checked against `TaskExecutor.RunEconomyStep` + `WorldAnalysis.Observation.PublishStepObservationDelta`:
  `ExecutionResult.EconomyDeliveryReady` already exists specifically for "reached target, nothing
  else changed" and explicitly marks `ResourceSite | Actor` invalidation on exactly that condition
  — pre-dating this patch, and not tier-specific (it fires the same way regardless of which of the
  three tiers produced the mover). So the SPECIFIC scenario described appears to already have a
  working compensating mechanism; the underlying architectural point (Provisioning mutates before
  `beforeStep`/`afterStep` diffing can see it, at all — Raid assembly and Economy lightening were
  already doing this before this patch) still stands.
- **P1 — the post-lightening AP recheck (now routed through `FailAfterHero`) still cannot undo an
  already-fully-applied lightening/reinforcement batch.** The review considers this unresolved until
  materialization moves to Execution.

Moving `ApplyGarrisonExtraction` (and the lightening/reinforcement apply step) into
`Execution/TaskExecutor` as an atomic sub-step is architecturally buildable now — the pure/impure
split from round 2 (`ResolveGarrisonExtractionCandidate` vs `ApplyGarrisonExtraction`) is exactly
the building block it would need — but is a materially larger change than anything fixed so far: it
crosses the Provisioning/Execution contract, and admission would need to price travel/build AP from
the not-yet-materialized mover's stats (the sparable unit's own `MoveMax`/`ActivationApCost`, the
same estimate `WorldAnalysis.Economy.cs` already uses at Analysis time) rather than a live
`ArmyData`. Deferred pending an explicit decision on scope/timing rather than a fourth unilateral
call in the same area.

## Review round 2 (2026-09-14) — bugs found in the first pass and how they were closed

An external review of the first patch found six real issues, all inside the same owner
(`ProvisioningManager.cs`). None required a new class or a new layer — the container search was
split into a pure `ResolveGarrisonExtractionCandidate` (decides the tier, never touches state) and
`ApplyGarrisonExtraction` (the one place that mutates), mirroring the `PlanEconomyArmyLightening` /
`ApplyEconomyArmyLightening` split already established lower in the same file.

| # | Issue | Fix |
|---|---|---|
| P0 | Rollback could return the WRONG hero: `EconomyHostCandidates` allowed a host that already had its own commander, and `ArmyData.AddMemberSorted` inserts a new hero AFTER existing ones — `hero.Members.FirstOrDefault(IsHero)` then found the old commander, not the extracted one | `EconomyHostCandidates` now excludes any army that already has a hero (`!a.Members.Any(u => u.IsHero)`) — such an army is itself a potential direct mover (1.2a), not a container. The exact `UnitData` extracted is also now tracked end-to-end (`extractionHeroUnit`) instead of re-derived by scanning members. |
| P0 | Provisioning could leave an irreversible `ArmyActions.CreateArmy` mutation (AP spent, army registered) with the result still reporting `StateChanged=false` | `FailAfterHero` and the success path now report `StateChanged`/transferred-count honestly: a Create-tier extraction always reports a real change (the shell persists even if the hero itself rolls back cleanly), and the extraction transfer itself now counts even when lightening moved nobody else. A full move of `CreateArmy` into `Execution/TaskExecutor` was considered and rejected as disproportionate — Economy (and Raid) are already the codebase's own documented exception to "pure binding" provisioning; this stays inside that existing exception instead of opening a new architectural seam. |
| P1 | A "free" host/shell could silently spend AP: `ArmyActions.TransferMember` charges `unit.ActivationApCost` when the destination already acted this turn (`ArmyData.RequiresActivationCharge`), and this was never checked or funded | `ResolveGarrisonExtractionCandidate` now reads `container.RequiresActivationCharge(hero)` (the same canonical accessor `TransferMember` itself uses — no formula duplicated) and checks the resulting cost against both the ECO envelope and the raw AP pool before accepting that candidate. |
| P1 | `EconomyHostCandidates` only checked `ActorCommitments` (durable intents), not `ProvisioningSession.ClaimedArmyIds` (armies already claimed earlier in the SAME batch pass) — could hijack a freshly-provisioned Recon/Raid mover | `EconomyHostCandidates` now takes `ProvisioningSession` and excludes `session.ClaimedArmyIds` too. |
| P1 | `ClaimedAp` included the already-spent extraction AP, which `ProvisioningSession.RegisterSuccess` adds into `session.ApClaimed` — double-subtracting AP that `root.ActionPoints` already reflects, starving later missions this same pass | `ClaimedAp` reverted to `realAp` only. The extraction AP is validated once, locally, against the envelope at the point it is spent, and never re-enters the cross-mission `session.ApClaimed` ledger. |
| P1 | The diagnostic TRACE recomputed its own copy of the container search (`cShellG`/`cHostG`/`cCreateG`) and had already drifted from the real gates (missing the activation-charge cost, the existing-hero exclusion, `ClaimedArmyIds`) | TRACE now calls `ResolveGarrisonExtractionCandidate` — the exact same pure resolver the real path uses — and prints its `Tier`/`ApCost`/`Reason` fields verbatim. No second implementation left to keep in sync. |
| extra | One `return ProvisioningResult.Fail(...)` after a successful `ApplyEconomyArmyLightening` still bypassed `FailAfterHero` | Routed through `FailAfterHero` too, with a comment noting the residual (documented, accepted) limitation: it reverses only the hero's own transfer, not a lightening/reinforcement batch that already fully applied — `ApplyEconomyArmyLightening`'s own atomicity guarantees that batch is either fully applied or not reached at all, so there is no partial roster to unwind at this point. |

## How to use this pattern next time

1. Find the single "owner" function that resolves a demand into an actor (here:
   `ProvisionEconomy` / `ResolveGarrisonExtractionCandidate` + `ApplyGarrisonExtraction`).
2. Enumerate every *mutually exclusive* precondition branch it actually checks in code — not what
   you assume it checks. Cross-reference against the log trace, not just the source, since a stale
   diagnostic can lie (see the `freeReusableShell` incident below).
3. Mark each leaf either covered (with the exact function/line) or a dead end. A dead end is not
   automatically a bug — 1.1 above is a dead end by honest design (no resource exists yet). Only
   flag a dead end as a bug candidate when the code's OWN comments concede it ("if no shell exists
   the candidate is simply not offered") — that is a designed limitation the owner may want lifted.
4. When adding a new branch, prefer reusing an existing generic downstream step (here: the lightening
   pass) over writing new logic — check whether the code after your new branch already applies
   uniformly to the resolved actor before adding anything bespoke.

## Known trap: stale diagnostic trace

The `[AI][V2][Economy][TRACE]` block in `ProvisionEconomy` used to re-derive eligibility for
logging, separately from the real gate — twice: on 2026-09-13/14 it mirrored only
`IsCandidateEligible` (hero sparability + path) and printed `ELIGIBLE=True` in exactly the turn a
real attempt was failing, because it never checked `ReusableArmySelector.FindReusableAt`; the fix
for that added a SECOND hand-written copy (`cShellG`/`cHostG`/`cCreateG`) that was itself already
missing the activation-charge AP cost and the existing-hero exclusion by the time review round 2
caught it. As of review round 2 the trace calls `ResolveGarrisonExtractionCandidate` — the exact
function the real extraction path calls — so there is exactly one implementation left, not a third
copy to eventually drift again. If you add a fourth materialization tier, add it inside that one
resolver; do not let the trace re-derive it.
