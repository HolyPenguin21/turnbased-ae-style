# AI Strategy V2 — Architecture (ARCH-02)

This document is **normative**. The original V2 pipeline (phase ordering, decision
authority, score/resource/lifecycle semantics) is unchanged; ARCH-02 only
consolidates *ownership* — which class/file/folder owns which responsibility — so
that new objectives, missions, cards, equipment, generated-card mechanics and
skills/effects have an obvious place to live.

## Folder taxonomy (semantic ownership, not assembly boundaries)

Everything stays in namespace `Game.Ai.V2` (flat). Folders express ownership only.

| Folder | Owns |
|---|---|
| `Orchestration/` | Turn *ordering* only. No scoring, capacity, placement or eligibility logic. |
| `Foundation/` | Cross-cutting config, enums, shared low-level primitives. |
| `State/` | Turn-scoped registries, ledgers, reservations, commitments, budgets. Mutated only through each type's explicit lifecycle API. |
| `Analysis/` | Read-only world scan and derived facts. One coherent `WorldSnapshot`. |
| `Strategy/Desire/` | Axis desire intensities → radar. |
| `Strategy/Objectives/` | Concrete known opportunities per axis. |
| `Strategy/Demand/` | Missing capability detection. **Never selects cards.** |
| `Strategy/` (root) | Phase-A / Phase-B coordination, maintenance & pressure policy. |
| `Evaluation/Cards/` | The single strategic card scoring entry point (`StrategicCardEvaluator`). |
| `Evaluation/Effects/` | `StrategicEffectRegistry` — canonical skills/effects semantic bridge. |
| `Evaluation/Power/` | `AiPower` — canonical own-force power. |
| `Materialization/` | Chain enumeration, placement options, plan, cost, consumption, delivery, execution. |
| `Missions/` | Objective → `MissionProposal` planners + admission. `Missions/Raid/` for raid assembly. |
| `Allocation/` | The one `ResourceAllocator`. |
| `Provisioning/` | Funded mission → actor binding → assembly → provisioned mission. Never plays strategic cards. |
| `Reaction/` | Interrupt lifecycle coordinator + probe/witness/reservation/solver/executor. |
| `Execution/` | Plan → canonical gameplay calls → structured result. Never re-plans or re-scores. |
| `Continuity/` | Mission intent lifecycle: `ResolveActive` (before planning) and `Reconcile` (after execution). |
| `Housekeeping/` | Turn-final, task-neutral, zero-AP local force packaging and invariant repair. It may sort/fold same-hex rosters against world-snapshot combat benchmarks, but never selects objectives, creates missions, moves armies, plays cards or spends resources. |
| `Recon/` | Recon-mission machinery (route/step planning, scout pricing, air/ground policy). |
| `Diagnostics/` | Logging, telemetry, audit. No influence on ordering, score, eligibility or state. |

## Mission axes (`MissionKind`)

Four axes compete for the same turn's AP/resources, all through the same
`MissionProposal` shape and the same allocator. **There is still no Defence axis.**
Aggression produces three mission shapes — `Raid`, `ActiveDefence` and `Attack` —
from the same `Missions/AggressionMissionPlanner`,
scored by the same `TaskScore`, admitted by the same `GroundCombatAdmissionPolicy`
thresholds and funded from the same Aggression slice. ActiveDefence is a durable
threat-response mission (`Intercept`, or one army's `Return` to regroup/retreat) owned by Aggression, **not** a
fifth axis and no longer the "reactive-only via `Reaction/`" stub older revisions of
this document described: `Reaction/` remains the end-of-turn interrupt safety net
only. Intercept skips a contact weaker than `activeDefenceMinEnemyPower` (10, by roster power, not skills), and past `activeDefenceLeashHexes` (4) from the nearest own Base/Citadel each hex lowers its `OwnTerritoryProximity` (`TaskScoreEvaluator.ActiveDefenceProximity`; a desire penalty, no gate). A Regroup / withdrawal only answers a threat within `activeDefenceWithdrawMaxEnemyEta` turns of its asset; a farther one is deferred. Its win-chance gate selection (fresh start 0.80 vs. pinned continuation 0.55) is the
same `GroundCombatAdmissionPolicy` pair Raid uses, applied identically in Missions,
`State/GroundCombatAdmissionRegistry` and `Provisioning`.

| Axis | Produced by | Task shape |
|---|---|---|
| `Economy` | `Missions/EconomyMissionPlanner` | `EconomyTaskKind`: BuildExtraction, FoundBase, MobileCollection, ReturnCollector, ReturnBuilder. Several build obligations may be active at once: Continuity keeps each one on its own facts (`Continuity/MissionContinuityLayer.ResolveActive`), real ownership conflicts (same actor / same objective / same physical card) are resolved where ownership is granted (`BeginEconomyDelivery`), and each obligation holds its own owner-scoped rows in `StrategicResourceReservationLedger`. Keeping an obligation and funding it are separate decisions — the allocator still owns the budget. |
| `Raid` | `Missions/AggressionMissionPlanner` | One durable multi-turn mission moving through `RaidMissionPhase`: Assault → Reinforcement/SupportReturn/Return → AirSupport / RecoveryReturn → Refit. Not five competing tasks — five phases of one committed raid. |
| `ActiveDefence` (Aggression) | `Missions/AggressionMissionPlanner` (`AppendActiveDefence`) | `ActiveDefencePhase`: Intercept or Return. An honest hostile threat gets ONE response decision, `ActiveDefenceObjectiveEvaluator.AssessResponse`, which the planner and Demand both read: (1) a concrete army or same-hex assembly (`GroundCombatAssemblyPlanner.Plan`) clears the combat gate → Intercept; (2) a capable army exists but cannot act this pass (spent MP, owned by another operation, the pinned incumbent waiting) → DEFER, no withdrawal, no production; (3) the usable field armies (`GroundCombatActorEligibility`, not claimed by another operation) together reach `GroundCombatFeasibility.RequiredPower` (summed by `GroundCombatFeasibility.AggregatePower`) → each one walks to the Citadel (`AiTurnController.GarrisonHexFor`) on its own Return leg, and the existing same-hex owners (`GroundCombatAssemblyPlanner`, Housekeeping) form the force there before a fresh evaluation; (4) not enough usable power, or every usable army already in the Citadel and still short (regroup exhausted) → field armies retreat to their own bases (`AiReturnBasePolicy.SelectReturnBase`) and Demand publishes `FieldCombatPower`. An Intercept is keyed by the enemy army; a Return by its own mover + destination (`MissionIntentKey.ForActiveDefence`), so several withdrawals never share a key. A Return is claimed (`ActorCommitments`) until it arrives and is finished even if the threat disappears. Intercept success, a vanished/handed-off threat, or an actor that lost or no longer clears its continuation floor simply retires the intent — the next global replan decides afresh. ActiveDefence owns no cross-hex reinforcement, support convoy, offensive preemption or post-victory lifecycle. |
| `Attack` (Aggression) | `Missions/AggressionMissionPlanner.Attack` | Targets known hostile Bases and starting Citadels, plus every live opponent's sanctioned starting-Citadel coordinates (`WorldAnalysis.SanctionedEnemyCitadels`) as a **location-only** objective until that hex is first observed (defenders unknown, never a sighting/threat; an observed destroyed/captured site never resurrects). A fresh Assault needs the projected legal ground army power strictly above 0.80 × the current map + hand + remaining-deck peak (`SelfSnapshot.TotalMilitaryPotential`); no win-probability floor. `CanDamageAll` is a physical coverage condition only while `AiConfigV2.attackRequiresDefenderCoverage` is on (2026-10-04 TEST BEHAVIOR: off — Attack ignores coverage before and during the assault; `GroundCombatAdmissionPolicy.RequiresCoverage`); a fresh march never starts on a location-only site, but a preparation host that strictly clears the > 80% peak bar marches on it anyway (2026-10-01: nothing left to wait for) and every pass of the march re-checks the then-known defenders. Gather transfers legal supports toward the threshold. **One live operation:** a player holds at most one live Attack operation (Gather — preparation included —, Assault or Reinforcement; `AggressionMissionLayer.LiveAttackOperation`): while it lives no fresh objective is offered, and of several fresh candidates only the best reaches the allocator. **Mobilization (T01):** when the gate is open (`AttackForceReadiness.MobilizationOpen(SelfSnapshot)`: the additive deployed ground share `DeployedPower / AvailablePower` is ≥ 75%, **or** `SelfSnapshot.FieldStrikePotential` — the strongest one stack the field bodies can form, armies of other operations included, explicit scouts (lone scouts and Scout-mission armies), aviation and the garrison defence floor excluded — clears the same > 80% bar as the march) and no Attack operation is live, one *preparation* opens inside the ordinary Gather phase (`AttackIntent.Preparation`, `AttackPreparationStep`): the fist assembles on its staging Base — of every held own Base the one nearest to the target (`AttackPreparationPolicy.PreparationStagingBase`; the starting Citadel only breaks ties), so cards and generated outputs land in it directly. Host = a free field army (one standing elsewhere first walks there: `AttackPreparationStep.MoveHost`, the walk-home leg's validation and Transit; supports are planned only after it arrived), else an empty reusable shell on the staging Base, else a container created there (`CreateArmyWithMember` with a legal first member, else one empty shell while hand/deck can fill it; 2 AP, funded by the allocator). The host may be weak, hero-only or empty; a heroless host takes one same-hex hero for its capacity (`GroundCombatAssemblyPlanner.PlanPreparationAssembly`: a capacity rise is progress; a lone-hero field army may hand its hero over, `GroundCombatDonorPolicy.LeavesDonorLegal`), a garrison hero only as a fallback priced by `ActionPrice.GarrisonHeroFallback`; `ActorCommitments` keeps it claimed (`PreparationHostStillValid`), same-hex legal bodies join it (`GroundCombatAssemblyPlanner.PlanPreparationAssembly`), supports gather partially (`PlanGather allowPartial`), and only after that a pinned FieldCombatPower demand names the exact host (`AttackFistIsPreparationHost`). For that demand only a native Recce unit is a combat body too (`MaterializationChainMatching.AbilitiesSatisfyCapability(recceMayFight)`); every other FieldCombatPower demand keeps scouts for Recon. A preparation with nothing in flight WAITs only on a concrete witness: a legal same-hex body, or a card that would strengthen that exact host and fills a missing position of its frozen target roster (`AttackIntent.TargetRoster`, `StrikeRoster`) or is an equivalent at least as strong as the weakest missing body; a full host counts a slot Housekeeping will free for an exactly missing card; a held card Phase A structurally failed to chain into this host (this turn or the last, `State/PreparationDeliveryMemory`) is no witness (`AggressionDemandEvaluator.PreparationHostCardSource`: held Unit card, staffed Research/Production output — a closed window, or stock short by no more than `devChainFundingHorizonTurns` of income, is timing — or an undrawn card; judged by `MaterializationDeliveryPolicy.StrengthensArmy` and `ArmyData.CanFitAdditionalCard`); a positive `Reserve` alone is no delivery, otherwise the ordinary stall lifecycle runs. Continuity re-plans a live gather with FREE armies only (a completed Raid's unclaimed fallback included); an army another operation holds is bought only by the planner's fresh `AttackPreparationStep.RecruitDonors` proposal, priced with the lenders' `DisplacementValue` as `MoverOpportunityCost` and funded by the allocator like a fresh gather — on execution its supports enter the intent (`AdvanceIntent`) and the lender retires on the next pass. It becomes an Assault only by the same strict >80% + coverage rule on an observed target. While the gate is open the objective's `AttackReadiness` slot is full (every preparation step keeps Attack's priority), and a fresh ActiveDefence Intercept that takes the army a preparation would host in pays that preparation's value as `MoverOpportunityCost` (`AggressionMissionLayer.PendingPreparationHost`). The unbound demand names a free fist at an own Base with no hand-only prerequisite: the chain (hand card, generated output or none) is Materialization's choice; Phase A/B count delivery only when that exact army gains power, while an undrawn card remains Phase B's Draw work. A threshold Gather is created only with an actionable support leg. Continuity checks the current peak until the primary actually begins its march; later rewards do not reverse a marching operation. **Committed Assault (2026-10-04):** once an Attack has committed to Assault (`AttackIntent.AssaultStarted`, set by the first executed Assault step), worsening defender intelligence does not revoke the operation. Existing field reinforcement may be intercepted and handed off if it improves the primary without requiring strategic retreat: Continuity (`ResolveCommittedAssault`) binds the best support from `GroundCombatAssemblyPlanner.ReinforcementSupportCandidates` that `GroundCombatRendezvous.SelectForward` can meet on a hex of the primary's own route to the target (the primary never backtracks and waits at most `attackReinforcementMaxWaitTurns`); the primary walks to that hex (`AttackMissionTarget.PrimaryRendezvousLeg`), the support walks there too, the handoff runs when both stand on it, and the support's container walks home as a GatherReturn while the primary resumes the Assault at once. A lost, no longer useful or unreachable support is replaced by another existing one or dropped; if no such reinforcement exists, the primary continues the Assault. A committed Assault never asks Production for a new support army (`AppendAttackDemands` skips it) and never enters RecoveryReturn for weak odds; RecoveryReturn stays for a primary reduced to a non-combat remnant. Before commitment (a Gather that fell apart, an Assault whose first march step has not run) the earlier rule holds: Reinforcement, the IndependentFieldArmy demand, then RecoveryReturn. Facilities in Base slots and standalone resource sites are not Attack targets. |
| `Scout` (Recon) | `Missions/ReconMissionPlanner` | `ScoutTargetKind`: Explore, Refresh, AirSweep, CaptureStructure. CaptureStructure binds an existing solo ground Recce to a currently visible empty foreign structure on its own or an adjacent reachable hex. It does not create a patrol lane or request a new scout; actor ownership and funding still apply. Hidden arrival is resolved by the canonical ExitStealth contact check. Aviation serves only AirSweep through an already formed `AirExisting` wing. Every proposed step proves a route to an owned airfield within live `TurnsWithoutRefuel` endurance; a zero-endurance aircraft lands the same turn. Strategic AA does not filter routes or strikes. Explore / Refresh remain ground jobs. |
| `Development` | `Missions/DevelopmentMissionPlanner` | Place an existing hero as operator on a Research or Production facility (`ResearchProductionMode`). Laboratory/Factory are a late resource sink that strengthens units already on the map: **when** they may spend is `State/DevelopmentInvestmentGate` (each resource the concrete chain consumes has turn-start headroom ≥ threshold for N consecutive turns; unconsumed resources never matter, a cost-free spend is always open), **what** is worth doing is `DevelopmentOpportunityEvaluator.Enumerate` (READY upgrades + PREPARE facility/operator). Every opportunity carries a world-task `TaskScore`: intrinsic `ForceAmplification` plus the execution of the world task itself (the existing operator hero's walk and the task it abandons); facility, operator and output cards are priced only by the chains that play them. A PREPARE passes two gates, one per currency — the card-currency `Ev` (is the card chain worth its cards; also read by the capacity-upgrade look-ahead) and a positive `TaskScore`, on which it then competes in the allocator. A PREPARE is funded in stages (`AddPreparation`): only this pass's stage (facility, else operator; `DevelopmentOpportunity.StageResourceCost`) is paid from spendable stock and judged by the gate, the rest of the chain must fit spendable + `devChainFundingHorizonTurns` of income, and at the facility stage the operator may still be in the remaining deck. When every unlocked slot of the site is taken, the next Base tier (`StrategicMaintenancePolicy.CapacityUnlockTierAt`) is part of the facility stage: priced into its stage cost and EV and bought with the facility in one action (`InfrastructureFulfillment.PlaceFacilityAfterOptionalUpgrade`, shared with the global-source path). Production amplifies an Attack/Defence need and never creates one without a military witness; `ForceNeed.Surplus` (idle stock, `ForceNeedModel.SurplusNeed`) is part of that need so banked resources arm the next Attack: a minted Unit/Hero/Aviation is worth its force only in the `ForceNeedModel.JustifiedForceNeed` share (desire, PREPARE score and the Phase-B surplus card score alike); Equipment keeps its own known-threat matchup gate. Facility/operator requests run in Phase A's residual infrastructure pass, after card arbitration. |

## Unified task scoring (one struct, one fold)

`Evaluation/TaskScore.cs` is the **only** way a mission candidate's merit may be
computed. It is a plain struct of named bonus/penalty slots folded by
`TaskScoreEvaluator`/`.Value` into one comparable number across Economy, Recon,
Development, Raid, ActiveDefence and Attack alike. A new scoring fact must be added as a named slot on
`TaskScore` and constructed *before* the fold — never applied to `.Value`/
`BaseValue` after the fact from planner- or allocator-local code. See the
canonical-seams table below.

* **One slot table.** `TaskSlot` lists every slot in fold order; `TaskScoreEvaluator.Sign`
  (benefit + / price −) and `GroupOf` (Intrinsic / Execution) are the only place a slot's
  folding and ownership are stated. `Fold`, `NetChange` and every composition iterate the
  table (`TaskScore.FromSlots`, the indexer); nobody copies slots by hand. A new slot is
  added to the enum, the field, the indexer and `FromSlots` — the slot-table test fails
  otherwise.
* **Four categories, one calibration table.** `Value = Benefit − Cost − Risk − Opportunity`
  (`TaskSlotCategory`, `TaskScoreEvaluator.CategoryOf`); the Benefit slots are each family's
  own facts. Every lever — Benefit caps per family, the price table, risk caps and the
  lifecycle margins on the TaskScore scale — lives in `Foundation/AiConfigV2.TaskScore.cs`.
  Every mission proposal carries its `TaskScore` (`MissionProposal.Score`; null only for a
  value restored from a durable intent) and is logged once per turn in one format
  (`AiFrameLog.TaskScores` → `[AI][V2][TaskScore] … | benefit (…) | cost (…) | risk | opportunity`),
  which `Tools/TaskScoreDashboard` aggregates per family next to a what-if report computed by
  the real converters (Unity: AI → TaskScore → Calibration Report).
* **One price table.** `Evaluation/ActionPrice` prices everything an action spends in
  AP-equivalents: 1 AP = 1 AP-equivalent for every use (card play, Challenge, activation now
  or on a later turn of a march); 1 H/E/M/T unit = `actionPriceResourceAp` x scarcity
  (pending hand/deck demand against stock + income). TaskScore and the card score are two
  scales of that table (`taskScorePerApEquivalent`, `cardScorePerApEquivalent`); the only
  converters are `ActionPrice.ToTaskScore` / `ToCardScore` (`TaskScoreEvaluator.Price` for
  the CardPrice and Delivery slots). No caller multiplies an AP or resource weight itself.
* **Intrinsic vs execution.** Intrinsic slots are facts of the target (built once by the
  objective/site owner); execution slots — `WinChance`, `CardPrice`, `Delivery`,
  `MoverOpportunityCost` — are facts of the actor/chain serving it.
  `TaskScoreEvaluator.WithExecution` keeps one side and replaces the other; `WithResponse`
  is the ground-combat instance. A capability demand whose actor is materialized by a card
  chain (CollectorCapability, GlobalResourceCarrier/Facility, Development CardUpgrade) carries
  intrinsic slots only: the chain prices its own execution.
* **One fact, one slot.** `RaidReward` (fixed Raid reward), `EventReward` (a Hex Event guard Raid's own reward, by the guard tier the observer remembers — `AiMapMemory.GuardStrength.RewardTier`), `AttackReadiness` (Attack's
  stronghold readiness) and `PreventedDamage` (ActiveDefence) are separate military slots;
  `Staleness` is Recon's value of refreshing old intel and `IntelAgePenalty` the price of
  acting on it (Attack, ActiveDefence). `ForceAmplification` is the need-weighted force a
  Development output adds.
* **Raw facts in, score units out.** Slots are filled only through `TaskScoreEvaluator`
  converters from raw facts. `MoverOpportunityCost`'s raw fact is the TaskScore value the
  actor's current task loses (`MissionIntent.DisplacementValue`: `LastIntrinsicValue` of an
  Active intent, zero on a lifecycle leg — return/recovery — and zero for the asking task's
  own intent). Economy builder loans, Attack gather donors and Raid recovery donors all read
  it; it is never an AP figure or a count of actors.

Aggression contains exactly three peer task families: Raid (neutral armies and guarded
events), ActiveDefence (hostile field armies threatening owned assets), and Attack
(hostile Base/Citadel capture for strategic war and game
completion). Raid, ActiveDefence and Attack use the shared canonical TaskScore and
receive the same Aggression Radar scale. There is no task-family pressure layer
inside Aggression. Raw Aggression is broad force readiness (surplus above the fixed home
guard, economic security, edge over the known enemy) while a war witness exists
(`ForceNeedModel.HasAggressionWitness`: known combat activity, a sanctioned enemy
starting Citadel, or an open Attack mobilization gate); home threat moves it in neither
direction (2026-10-04: the threat reserve no longer shrinks its surplus term), and it
does not read an objective's TaskScore. Recon's RefreshPressure carries an Attack
observation term (`AttackObjectiveEvaluator.ObservationNeeds` unseen or older than
`attackIntelMaxAgeTurns`). Development's `ForceNeed.Offensive` counts known defended
hostile Bases/Citadels our strongest stack cannot reach (`RequiredSitePower`) beside
the field fights.

Radar answers which **axis** matters now. `TaskScore` answers how good a concrete
world task is. `EffectiveValue` is the intrinsic `BaseValue` multiplied by that
task's axis weight. Radar never chooses Attack versus Raid: all three military
families share the Aggression axis and therefore compete by their canonical task
values in the global allocator. Lifecycle hysteresis may break an otherwise local
tie, but it is not intrinsic world-task value and may not manufacture a new score.

Attack readiness (`AttackForceReadiness.Readiness`: assembly = Fist / P_field,
deployment = P_field / (P_deck + equipment reserve), each ramped, multiplied) is an
Attack-only strategic fact. `AttackObjectiveEvaluator` converts it into its own
`AttackReadiness` slot before the fold (Base/Citadel only). It does not enter
the common Aggression desire, so it cannot raise Raid or ActiveDefence value. Attack requires strictly more than 80% of the dynamic ground peak before the primary starts its march. The attack win chance remains a ranking term in `TaskScore`, while defender coverage is a separate physical condition. Coefficients are calibration points pending gameplay logs.

The force measures all live on `SelfSnapshot`, on one scale — the AiPower strength of
one composed ground stack — and are built in one pass by
`WorldAnalysis.BuildForceMeasures`: `FieldPotential` (P_field, map only),
`BestStackPotential` (map + hand), `TotalMilitaryPotential` (P_deck, + deck), `FistPower`
(strongest existing army), `StartPotential` (P_start, `ForceBaselineRegistry`) and
`Reserve` (units / hero / equipment / aviation that hand + deck can still add). The pools
are nested and every ceiling uses the same commander-in-slot rule
(`AiPower.NestedPotentials`: map → + hand/deck bodies under the map's own commanders →
+ hand/deck heroes), each never below the pool it contains, so P_field + Reserve.Units +
Reserve.Hero = P_deck by construction and a hero card adds only through the slots its
Command opens. `PlayerForceAnalysis` reads the same P_deck. Aviation is support and
never joins a ground stack.

One completed Raid target is one completed strategic objective. Continuity never
selects or mutates the intent to a second neutral target. It exposes the surviving
army to fresh mission construction/allocation; a new Raid, Attack or ActiveDefence
must win the shared competition. A zero-value Return/Recovery fallback remains
available if no fresh operation is admitted.

## Dependency direction (must hold)

```
Orchestration
   ↓
Strategy / Materialization / Missions / Allocation / Provisioning / Reaction / Continuity
   ↓
Analysis / Evaluation / Foundation / State
   ↓
Game domain
```

Forbidden edges: `Evaluator → StrategicManager`, `Domain → MissionPlanner`,
`Executor → ObjectiveEvaluator`, `Demand → MaterializationExecutor`, and any
cycle across the tiers above. `Execution/` receives concrete plans and calls
canonical game actions; it never selects objectives or invents alternative actions.

## Mid-turn loop boundary (rollout contract)

The loop is a bounded repetition of the existing horizontal pipeline, not a new
vertical manager. Ownership remains:

| Level | Mid-turn responsibility |
|---|---|
| `Orchestration/` | Chooses the next admitted task family, enforces turn/cycle/step bounds and stops. No scoring. |
| `Analysis/` | Refreshes `WorldSnapshot`, compares the previous observation with the new one and reports factual deltas. Domain event/reward code never depends on V2. |
| `Strategy/` | Maps typed factual invalidations to dirty task families and re-runs only the existing affected policy. |
| `State/` | Keeps turn-scoped reservations, commitments, state version and persistent no-op parking across cycles. |
| Existing executors | Execute one admitted atomic task step through canonical gameplay calls and return one structured result. They never re-plan. |
| `Reaction/` | End-of-turn safety net, bounded external-interrupt fallback and final reconciliation; not the ordinary post-step loop owner. |

A **task step** contains at most one canonical state-changing gameplay operation,
or one explicit no-op/blocked result. Its executor must wait until that operation
and any battle/event consequence have settled before returning. Only then may
Analysis refresh observations and produce typed invalidations. This preserves
method atomicity: the loop surrounds existing operations; it does not yield from
inside their mutation boundary.

| Lifetime | Starts | Ends | May survive |
|---|---|---|---|
| Turn | V2 turn entry | final reconciliation / end turn | reservations, commitments, mission intent, parking |
| Cycle | refreshed observation + dirty-family admission | one bounded task selection/replan pass | turn-owned state only |
| Step | admitted task result is selected | execution settles and result is observed | no executor-local planning state |

Phase A and Phase B are not deleted. Their existing policies remain the owners of
capability fulfilment and surplus/tempo arbitration. They are re-entered only
through bounded adapters and retain turn-scoped parking/reservation state; the
terminal Phase-B/reaction path remains the final safety net.

**Rollout is complete, not partial.** The bounded typed loop is the single production
execution path. There is no runtime strategy/focus mode and no axis-scope filtering.
Every turn builds the real Desire evaluators, normalizes one Radar and runs all four
axes — Recon, Economy, Aggression and Development. `AiStrategyV2Scope` remains only
as a pure `MissionKind → DesireAxis`/operational-invalidation mapping helper.

Ground Recon concurrency is value-gated `0..maxConcurrentReconExecutions`.
`DemandUrgencyPolicy.NormalizedWorldValue` is the canonical adapter from TaskScore
to “material value”; frontier-region count or stale pressure may justify a second
lane only when a second runnable observation objective passes that value gate.
There is no mandatory first lane, third production lane or turn-number decay. Across the turn's bounded replans the distinct ground scouts bound to Recon are capped by `ReconConcurrencyPolicy.GroundActorsPerTurn` (3; 2 while the Attack mobilization gate is open), counted from scouts that really walked (`ReconTurnState.ReconGroundActorsUsedThisTurn`); an over-budget mission is MoverContended, never a capability shortage.
Consequently Recon naturally falls to zero on a fully known/current map and can
reactivate when important contact becomes stale or blind again.

## Canonical seams (one owner each)

| Concern | Canonical owner |
|---|---|
| Cross-axis mission/task scoring | `Evaluation/TaskScore.cs` (`TaskScoreEvaluator`) — the only mission-candidate scorer; see "Unified task scoring" above |
| Own-force power | `Evaluation/Power/AiPower` — no `ReactionPower` / `RaidPower` |
| Tactical roster odds + per-defender penetration | `Game.Combat.WorthIt` — skill-aware; each side's commander (initiative bonus, Fate rerolls through `FateDuelAi`'s policy); a multi-army hex is `EstimateSequential` (strongest defender first, wounds carry, Fate refills). The aggregate-sum estimator is gone |
| Who leads an army (capacity, battle initiative, Fate) | `ArmyData.Commander` — the army's first hero; battle, UI and AI read only this |
| Which hero SHOULD lead a formation | `HeroRoleEvaluator.ProjectCommand` + `CompareCandidates` — the formation's fight under that hero (WorthIt, with its capacity/initiative/Fate) against the opposition, then capacity, then role/leadership. Same-hex assembly (`GroundCombatDonorPolicy.PickAttachableHero`), Housekeeping's commander reorder, bench pick and `CommanderMismatch` all use it; so does `CombatOpportunityAnalyzer` for the assemblable roster, over `SelfSnapshot.CommandHeroes` (map and hand heroes, a hero card through `HeroRoleEvaluator.Profile(CardDefinition)`), and Phase A/B's hero-card value (`StrategicCardEvaluator.HeroCommandMarginalValue`: would it lead the destination, and the win gain if so). With no concrete target the fight is `HeroRoleEvaluator.CommandContext` — the strongest enemy field army (`IsCommandBenchmark`) |
| Own force measures (P_field, best stack, P_deck, Fist, P_start, reinforcement reserve) | `WorldAnalysis.BuildForceMeasures` → `SelfSnapshot`, on `AiPower`'s one-stack scale; the nested ceilings and `Reserve.Units`/`Reserve.Hero` come from `AiPower.NestedPotentials` only. P_start is player memory in `State/ForceBaselineRegistry`, written once by the pipeline after the first scan |
| Field strike force (mobilization start B, Panel_Data "Field strike force") | `WorldAnalysis.FieldStrikePotential` → `SelfSnapshot.FieldStrikePotential` and `PlayerForceAnalysis.FieldStrikePotential`, against P_deck through `AttackObjectiveEvaluator.FieldStrikeForceReady` |
| Additive deployed / available force (Attack mobilization trigger, Panel_Data first line) | `Analysis/PlayerForceAnalysis.Calculate` (Σ `AiPower.UnitPower` of the GROUND force — garrisons included, aviation and prisoners excluded (user decision 2026-09-30); hand + remaining deck added) → `SelfSnapshot.DeployedPower` / `AvailablePower`; the gate is `AttackForceReadiness.MobilizationOpen` (≥ 4/5, never a march) |
| Sanctioned enemy starting-Citadel coordinates | `WorldAnalysis.SanctionedEnemyCitadels` / `IsSanctionedEnemyCitadel` — the air sweep anchor, `ObservationNeeds` and Attack's location-only objectives; `AttackObjectiveEvaluator.IsLocationOnly` answers "never observed yet" |
| Attack preparation host (validity, claim, same-hex step) | `MissionActorPolicy.PreparationHostStillValid` / `ActorCommitments.IsPreparationHost`; `GroundCombatAssemblyPlanner.PlanPreparationAssembly`; the step is executed only by `AttackExecutor.TryRunPreparationStep` |
| A body's quick combat value (Attack+Defense+HP+0.25·Initiative) | `WorthIt.CombatValue` |
| The opposition of a ground fight (each defending army + its observed commander) | `AiV2Util.KnownOpposition` (Raid/ActiveDefence target), `AttackObjectiveEvaluator.KnownSiteOpposition` (Attack site); flat defender lists are `WorthIt.UnitsOf` of these. `GroundCombatFeasibility.Clears` takes the attacker's commander + the opposition |
| Strategic card value | `Evaluation/Cards/StrategicCardEvaluator` — the only strategic scorer |
| Skills / effects semantics | `Evaluation/Effects/StrategicEffectRegistry` |
| Materialization delivery ("can this satisfy demand X") | `Materialization/MaterializationDeliveryPolicy` (plan- and army-level) |
| Chain enumeration (raw shapes only — no preflight, no feasibility, no score) | `Materialization/MaterializationChainEnumerator` |
| Per-chain feasibility (Preflight + Phase-A entitlement/AP/resource gate + Phase-B reserves/strategic-claim gate) | `Materialization/MaterializationFeasibility` (`FilterForDemand` / `FilterSurplus`) |
| Air-recon per-step tactical decisions (phase machine / mode / `Pick` / return-step + landing hysteresis / activation gates / opportunistic-strike arbitration) | `Recon/AirReconStepDirector` |
| Air-recon information-weighting (Explore vs Refresh) | `AirReconModePolicy` (internal class inside `Recon/AirReconStepDirector.cs`, not its own file) |
| Plan construction + `StrategicActionCost` + `StableKey` | `Materialization/MaterializationPlanFactory` |
| Capability / trait / equipment-host matching | `Materialization/MaterializationChainMatching` |
| Joint physical projection (recipient / hero / hand slots) | `Materialization/ProjectedPhysicalState` |
| Projected army capacity rule (planner == executor) | `Materialization/ArmyCapacityRules` |
| Air-recon actor/target selection (round 4 — same owner as ground) | `Recon/ReconAssignmentPlanner` (`AppendAirCandidates`) |
| Air-recon execution-input assembly (mode / launch-subset re-derivation / first-step gate / energy) | `Recon/AirReconPlanner` |
| What a live Attack needs observed, and how Recon serves it | `AttackObjectiveEvaluator.ObservationNeeds` publishes the operation's target site; `ReconObjectiveEvaluator.AttackNeedRefresh` turns it into an ordinary Refresh (stale past `attackIntelMaxAgeTurns`, full relevance), gated on the HARD scout block only (`ScoutObjectiveEvaluator.IsAttackObservationFocusRunnable`) — a known hostile site is always a visible-arrival block, so it is observed from a vantage (`ObservationVantageSelector.UsesVantage`). No new Recon kind |
| Air support of a ground fight (wing options, strike estimate split back per defending army, second strike, landing base, leg requirements, wing provisioning, the flight step) | `Missions/GroundCombat/GroundCombatAirSupport` + `Execution/GroundCombatLegStep.AirStrikeSortie`. Raid (its AirSupport recovery phase) and Attack (a side leg) keep only their target identity, strike policy, win read and lifecycle. The held wing is `GroundCombatLegs.HeldAirSupportArmyId`; an airborne strike sortie no operation holds becomes a landing obligation (`ReleaseOrphanStrikes` → `AviationRebasePlanner.FindMandatoryContinuations`) |
| Turns an army needs to cover a distance | `AiV2Util.TurnsToCover` |
| Typed strategic invalidations | `State/StrategicInterruptRegistry` — factual reason mask plus per-reason payload; no second event bus |
| Mid-turn strategic re-admission (demand regeneration + Phase-A follow-up) | `Pipeline.TakeTypedTriggers` / `ReenterStrategicAxes` for Economy, Development and Aggression, each on `DesireAxes.InvalidationMaskFor(axis)` and gated by its admission fingerprint (`DevelopmentAdmissionFingerprint`, `AggressionAdmissionFingerprint`, the Economy key) so an unchanged input set never re-runs the lane. Aggression's mask adds Hand (field cards decide deliverability); its baseline is taken after `ResolveActive`/`ActorCommitments`, before `Generate`, and each re-admission logs the family old→new by consumer identity (`DemandIdentityDigest`). Recon stays operational-only |
| Execution state-version counter | `State/V2StateVersion` |
| Materialization action cost | `Materialization/MaterializationPlan` accounting fields (`ApCost` / `ResCost` / `HandSlotsNeededAtPeak` / `Generation`) — the canonical `StrategicActionCost` |
| Physical card / equipment / generation consumption | `Materialization/MaterializationConsumptionState` |
| Jointly-feasible materialization portfolio | `Strategy/PhaseA/MaterializationPortfolioSolver` |
| Delivered capability + Housekeeping lease | `Strategy/PhaseA/CapabilityDeliveryEvaluator` |
| What Housekeeping may do to a claimed army (T05) | `State/ActorCommitments` — each claim carries an `ArmyMutationContract` (`MayReceive`, `MayReorderCommander`, `KeepsMovement`; two claims intersect, a bare `Claim` locks the container). Raid/Attack/ActiveDefence primaries and their return legs, and an Attack preparation host, take free same-hex members (route-bound ones only if the army's movement is unchanged) and may promote a hero already in the roster; a hero enters a claimed receiver only as its best commander (HeroRoleEvaluator) without losing room for bodies (`MissionReceiverTakesHero`). A claimed army never donates, folds, swaps or deposits — with one exception (ATK-F03): the preparation host's contract `MayReleaseExcessHeroes` lets a hero that is neither its commander nor its best leader, and no operator, leave zero-AP to a free local container (`PreparationSlotWaste` in the planner's tuple, `HousekeepingExecutor.CommonPreflight` live), and — under the same contract — a body its frozen target roster does not contain when a source of a missing position needs the slot (2026-10-01, `ReorgViability.PreparationRosterWaste`); it is intersected away by any other claim or a same-turn lease. Supports, air wings, Economy/Development actors and scouts stay fully locked. `ArmyReorgAnalyzer.MutationContractFor` (claim ∩ strategic lease; a garrison never has one, a leased scout/collector is locked) is read by both the planner (`ReorgContainer.IsMissionReceiver`, movement floors in `CanAccept`) and `HousekeepingExecutor`'s live preflight |
| Card placement legality | `Materialization/PlacementRules` |
| Drawing cards (2026-10-01) | Two owners, one executor (`Execution/CardDrawExecutor`), one per-turn draw cap (`StrategicTempoBudget`, `maxTerminalDrawsPerTurn`): `Strategy/HandReplenishPolicy` refills a hand below `handReplenishTargetCards` BEFORE the turn's scan, keeping `handReplenishMinApLeft` + the pending aviation obligations' activation (`AviationObligations.ActivationAp`) on top of the bank's claims, and raises no hand interrupt (the scan sees the new hand); every other draw is Phase B's Draw candidate |
| How much AP the AI could usefully spend (2026-10-01) | `EffectEvaluationContext.ResolveUsefulApDemand`: the witnessed AP demand of the last turns (`State/ApTurnPressure`: AP spent + draws to the hand target + affordable AP-costing hand cards + budget-deferred missions, mean of `apWitnessedDemandHistoryTurns`, read into `ApActionEconomySnapshot.WitnessedApDemand` at the scan) replaces the structural guess and floors the evaluation-time card workload; the structural fallback (first turn only) counts draws. It prices every recurring +AP source (Base, hero, facility) through `ResolveMarginalApUtility`. `[AI][V2][ApBudget]` logs the same measurement |
| When a return leg spends AP (2026-10-01) | `Orchestration/LifecycleReturnPolicy`: while no home threat (`ThreatModel` siege, or Citadel/Base severity >= `lifecycleReturnHomeThreatSeverity`), a lifecycle return (Raid Return/RecoveryReturn, Economy ReturnBuilder/ReturnCollector, Attack RecoveryReturn — `MissionIntent.IsLifecycleLeg`, ActiveDefence excluded) is withheld from admission as a recorded planner deferral until the first Phase B round has spent, then admitted from what is left |
| Attack preparation vs card play and Raids (2026-10-01) | While mobilization is open (`OperationContinuationWindow.SetMobilizationOpen`, stamped at the scan) and no Attack operation exists, `StrategicSpendability.OperationContinuationHold` holds `attackPreparationFirstStepApHold` AP for the first preparation step; a live preparation's host still walking to an own Base is a protected leg. The hold binds card play until the mission loop settles; a demand with `ConsumerMissionKind == Attack` may draw on it (`InfrastructureFulfillment.SpendAuthorityFor`), the allocator (ledger-only) always may. `Missions/AttackPreparationPriority` ranks every fresh Raid just below a fresh preparation step; started Raids keep their rank |
| Strategic spendability ("does this cost fit spendable resources") | `State/StrategicSpendability` — every admission and its later gate read the SAME pool: Phase A chain admission (AP and H/E/M/T) and `MaterializationExecutor` re-check; `ResourceAllocator.PhysicalAvailableFor` (one owner-aware physical pool for every mission kind, holds already drawn by their own Economy mission credited once) and each lane's Provisioning gate; Raid recovery's repair projection reads `EconomyStanding.SpendableStockpile` like the Phase-B repair it predicts |
| Attack bar and the pool it is measured on (2026-10-01) | `Strategy/Objectives/AttackForcePool` → `SelfSnapshot.AttackPeak` (the > 80% bar of every Attack stage, the mobilization field-strike gate, Phase B's draw bonus), `StrikeRoster`, `StrikePool`: the peak greedy over the force an Attack can really assemble — every field army, other operations' included (Raid, ActiveDefence, Economy, Development come back; 2026-10-01 user decision), garrison bodies above the defence floor, spareable garrison heroes, held cards (with attached equipment) and the deck; OUT: explicit scouts (lone scouts and Scout-mission armies — the same set FieldStrikePotential leaves out, so the mobilization gate compares like with like), the garrison's mandatory defence, garrison heroes and facility operators. `TotalMilitaryPotential` stays the whole-deck ceiling (reserve, readiness, Panel_Data). A residual demand Phase A proved structurally undeliverable (`AxisDemand.StructurallyUndeliverable`) keeps no claim on hand cards (`UnresolvedClaimFor`), so Phase B may play them |
| Strike-force target roster (which cards the peak army is made of) | `AiPower.NestedPotentialsOf` (the same greedy as `TotalMilitaryPotential`, over identified candidates; a held card counts with its attached equipment, `AiPower.ToPowerUnit(CardData)`) → `SelfSnapshot.StrikeRoster` / `StrikePoolKeyCounts` / `StrikePool`; a preparation freezes its OWN roster under the host's commander (variant B, `StrikeRoster.ComposeUnder`; `MissionContinuityLayer.RefreshTargetRoster`: re-frozen when the host's commander changed, the peak grew by `attackTargetRosterRefreezeGrowth` or a position left the pool). A stronger spareable commander in an own garrison elsewhere (never a garrison hero or operator) is fetched when the host's roster cannot clear the bar: `AttackPreparationStep.FetchCommander` (CreateArmyWithMember, 2 AP) → `AttackIntent.CommanderArmyId` → a Gather leg with `CommanderLeg` whose handoff counts the larger Command as progress (`PlanAttackHandoff(capacityIsProgress)`); held by the operation (`HeldGroundSupportArmyIds`) and its activation held from card play (`StrategicSpendability.OperationLegMovers`). A Recce body the peak itself picked is a combat body for every FieldCombatPower demand (`StrikeRoster.IsPeakBody`). Positions are a multiset by card key (`StrikeRoster.CardKey` / `UnitKey`). Housekeeping gathers it on the host's hex: `ReorgViability.PreparationRosterWaste` (pending sources — a garrison body only when the garrison may spare it, a held card only when Phase A has not refused it — + non-roster bodies blocking their slots, one term of the preparation-slot tier) drives the release of a non-roster body (`ArmyReorgAnalyzer.IsPreparationNonRosterBody`, the executor's live twin) and the intake of a same-hex roster body; Housekeeping may still take free armies apart — the roster is by card, not by army, so any host re-gathers it |
| Garrison heroes and facility operators | `AiArmyRoles.IsGarrisonHero` — a hero whose card carries the `Support` type tag (ApBonus, Researcher, Assembler heroes; the catalog test enforces it). Housekeeping moves it into the local garrison and never out of it (`ReorgUnit.IsGarrisonHero`, counted with operators in `OperatorExposure`); an active task takes it only when no other hero qualifies, paying `ActionPrice.GarrisonHeroFallback` (Economy picks it last, Development asks for exactly those roles). A hero operating an own Research/Production facility (`AiArmyRoles.IsFacilityOperator`) is refused by `CanSpareGarrisonMember` itself — one rule for every lane |
| Research/Production investment window (every Challenge, facility build, operator delivery) | `State/DevelopmentInvestmentGate` — written once per turn by `DemandLayer.Generate`, read by `GenerationSource.Enumerate` and `DevelopmentOpportunityEvaluator.Enumerate` |
| Development opportunity admission (READY upgrade / PREPARE facility+operator, one world-task TaskScore) | `Strategy/Objectives/DevelopmentOpportunityEvaluator.Enumerate` — Demand and the capacity-upgrade look-ahead consume its list, never a predicate of their own |
| Force facts of the axis chain: known combat activity, military witness, defensive reserve for threats, relative edge, justified force need | `Analysis/ForceNeedModel` — Aggression desire, Development desire, `BaselineForceReadiness`, the Production output score and Development opportunities all read it; nobody re-derives them |
| "Can we take this known fight now" | `CombatOpportunity.IsViable` — Raid objective admission and `ForceNeedModel` |
| Scout self-defence | `ReconReactionPolicy.FindWeakScoutOpportunity`: an adjacent enemy scouting force (a lone scout or an army whose every visible member is Recce) is attacked from `scoutReactionAttackWinChance` (0.60), with the damage-complete and post-combat safety gates |
| Recon ordinary step vs a recent safety escape (T09) | `ReconPatrolState.LastEscape` (written by `ReconGroundExecutor` only after an EvadeDetector / Flee step really moved) is read by `ReconGroundStepPlanner.ReentryBlocked` in both the immediate score and the forecast; the executor moves only to `Pick`'s hex, so the live step obeys the same rule. A hidden scout re-enters detector risk ≥ the escaped risk only when the hex's fresh information is at least that risk; a visible scout does not step back within flee radius of the still-known threat. The memory ends on `scoutEscapeMemoryTurns`, when the cause leaves honest memory, or when the scout's stealth state removes it; emergency reactions never read it. Live detector risk has one owner, `ScoutRiskModel.DetectorRiskLive` |
| A completed Raid target's army (T07) | `MissionContinuityLayer.AdvanceRaidPhase` marks `CompletedTargetAwaitingFreshDecision` and leaves a zero-value, unclaimed Return fallback; the army is free for the next global competition (fresh Raid, ActiveDefence, Attack gather) like any free army. A live Attack gather re-planned in `ResolveActive` may recruit it; the fallback is then retired in the same pass (never two owners, never walking home instead) |
| Foreign facility cards (T08) | Never known to any task. `AiMapMemory` records a building's slot abilities and free slots only when the observer owns it; `WorldAnalysis.ToBuildingSnapshot(b, observer)` does the same for `TrueWorld.AllBuildings`. Identity (`IsBase`, `IsStartingCitadel`, owner, `Defense`, the building card's own abilities) and the observed aggregate `CollectedAmounts` (how much of the hex this structure collects — `ObservedOpponentIncomeFloor`, remaining site yield) stay for every owner. Attack targets are foreign Bases/Citadels only (`AttackObjectiveEvaluator.IsHostileStrategicStructure`); a standalone site is Economy/Recon (sabotage) |
| Garrison defence floor (which garrison body may leave) | `AiArmyRoles.CanSpareGarrisonMembers` → `SpareableBodies` + `GarrisonDefenceFloor`: the garrison keeps `garrisonDefenceShareCitadel` (10%) / `garrisonDefenceShareBase` (5%) of the ground force (`PlayerForceAnalysis` additive scale), at least one body; how many may go is what the strongest remainder can hold, which go maximizes (power released − shortfall below the floor), ties keep the stronger defence. Every V2 donor, Housekeeping (`GarrisonMayRelease`, `GarrisonPowerFloor`) and the held-base garrison demand read it; the old 2-body headcount and the Housekeeping reserve 20 are gone (V1 `IsBaseGarrisonSecure` keeps its headcount) |
| Army combat power (the one scalar every lane reads) | `Evaluation/Power/AiPower` — a hero is the army's container, not a body: `ToPowerUnit` gives it 0 power (own, card, and remembered enemy profiles), and `CompositionQuality` reads bodies only. A hero shapes an army only through its `CommandRating` slots, so `TotalMilitaryPotential` (the Attack threshold) is the best commander's slots filled with bodies. A hero's indirect value (initiative, battle Fate, command choice) is `HeroRoleEvaluator`'s; a wounded hero's repair is priced on `AiPower.StatLinePower` |
| "Can the known pool EVER cover this fight" (T06) | `CombatOpportunityAnalyzer.ProvenUncoverableWithinKnownPool` — coverage (`WorthIt.CanDamageAll`, a hard part of `GroundCombatFeasibility.Clears`) is monotone in the attacker set, so the whole optimistic pool failing it is a proof: map units, hand/deck Unit cards, every Research/Production output of the faction (`DevelopmentReadiness.CatalogOutputs`), each under every known equipment grant, plus the RaiseTheRots summon. Claims, MP, resources, capacity and investment windows are ignored (timing, never impossibility); no catalog → nothing is proven. `RaidOperationalReadiness.ProvenUnreachableWithinKnownPool` clears NeedsPower/NeedsHero/NeedsAssembly; `AggressionDemandEvaluator` skips such a target and asks no support for such a bound Raid. Recomputed per snapshot — no blacklist |
| AP a continuing Hard ground-combat operation needs for its next step (Raid Assault/Reinforcement, Attack Assault/Gather/Reinforcement, ActiveDefence Intercept) | `StrategicSpendability.SpendableAp` subtracts the unpaid activation of the leg's movers that can still act (live, affordable prefix, like air recovery), so Phase A card play cannot strand an operation the allocator funds first; `OperationContinuationWindow.Settle` ends it once the mission loop has run, before Phase B. The allocator never reads it — it funds these operations |
| What taking an actor off its current task loses | `MissionIntent.DisplacementValue` / `DisplacementValueOf` → `TaskScoreEvaluator.MoverOpportunityCost` |
| Actor occupancy truth | `State/ActorCommitments` |
| Explicit resource reservations (owner-aware) | `StrategicResourceReservationLedger` (`State/StrategicResourceReservation.cs`) |
| Phase-A AP pool (one scalar pool + follow-up reserve; the axis is a log label only) | `State/ApBudgetLedger` (AP-only) |
| Turn tempo budget | `State/StrategicTempoBudget` |
| Persistent-resource hold policy | `Strategy/PhaseB/HoldEvaluator` |
| Raid actor eligibility | `IsStructuralRaidActor` field on the army snapshot in `WorldSnapshot`, computed by `Analysis/WorldAnalysis.Self.cs` (no separate `RaidActorEligibility` type any more — no "Ready" alias) |
| Ground-combat assembly: same-hex package, eligible-actor enumeration, cross-hex Attack gather | `Missions/GroundCombat/GroundCombatAssemblyPlanner` (`Plan`, `EligibleActorIds`, `PlanGather`, `SupportImprovesPrimary`) |
| The Attack handoff (who moves support → primary and back, commander) | `GroundCombatReinforcement.PlanAttackHandoff` — one plan for `SupportImprovesPrimary` on live armies, the leg check, the rendezvous AP and the executed transfer. Command handover and bodies-only are alternatives: the one that raises power (or the missing coverage) wins, a non-strengthening hero never displaces a useful body. A leg still walking is judged without today's activation charge (`ArmyActions.CanExchangeMembers(requireChargeNow: false)`); the rendezvous pays it. `PlanGather` projects under the host's current commander with the same handover rule. A refusal names both alternatives with power before/after |
| Ground-combat leg occupancy (which legs pin actors, join the batch solve, own their mover; held supports) | `Missions/GroundCombat/GroundCombatLegs` — read by ProvisioningManager, ProvisioningSession, ActorCommitments, AdvanceIntent and the admission fingerprints |
| Non-capturing transit step of every lifecycle leg (Raid/Attack/ActiveDefence returns, Raid/Attack convoys and gathers) | `Execution/GroundCombatLegStep.Transit`; each lane executor maps the step onto its own result semantics |
| Ground-combat operation core (primary / support army) | `IGroundCombatOperation` on Raid/Attack/ActiveDefence intents; `MissionIntent.PreferredMoverArmyId` projects onto it |
| Ground-combat win-chance gates (start vs continue) | `GroundCombatAdmissionPolicy` (internal class inside `Missions/GroundCombat/GroundCombatAssemblyPlanner.cs`, not `Missions/Raid/`) |
| Ground-combat (ActiveDefence) win-chance gates | `GroundCombatAdmissionPolicy` — one owner of `FreshStartWinChanceGate` / `ContinuationWinChanceFloor` / `AttackCoverageGate` (zero for Attack: no probability floor; coverage only while `RequiresCoverage` says so — the Attack test switch, Raid/ActiveDefence always) and of the assigned-assault selection `AssaultGate`. Missions, `GroundCombatAdmissionRegistry` and `ProvisioningManager` only *select* between them, all on the same predicate (the proposal continues a durable intent whose pinned actor is the actor being bound). A re-check must never apply a stricter gate than the admission it is re-checking: a `GroundCombatAssemblyPlan` carries the `WinChanceGate` it was admitted at, and `GroundCombatAssaultTransactionRunner.Run` re-checks at exactly that gate. |
| Which win-chance gate a stage re-plans at | `GroundCombatAdmissionPolicy.PinnedOrFreshGate` (the durable operation's own pinned actor vs anything new), `RaidPrimaryGate` (a started Raid stays in Assault down to the continuation floor; Continuity's phase machine and Demand read the same gate), `AssaultGate` (Provisioning). No stage writes its own ternary |
| Ground-combat response score (win chance, activation AP now, recurring AP, mover opportunity cost) | `TaskScoreEvaluator.WithResponse` / `WithActorResponse` (via `WithExecution`) — Raid, Attack and ActiveDefence keep every intrinsic slot and add the same execution slots. An Attack gather's bought donors enter as `MoverOpportunityCost` (`GroundCombatGatherPlan.DisplacedValue`), never as AP — the fresh gather and a live preparation's `RecruitDonors` proposal alike; Continuity never buys |
| ActiveDefence response decision (Intercept / Defer / Regroup / Shortage) | `ActiveDefenceObjectiveEvaluator.AssessResponse` — read by `AggressionMissionPlanner.AppendActiveDefence` and `AggressionDemandEvaluator.BuildActiveDefenceDemands`; aggregate usable power is `GroundCombatFeasibility.AggregatePower` |
| "Does a bound primary need a NEW support army" (Raid and Attack) | `AggressionDemandEvaluator.BoundPrimaryShortage`; a legal source exists (`CanDeliverIndependentFieldArmy`) when a free army, an empty shell, a hand Unit/Hero or an open ground Research/Production output (`GroundGenerationOffers`) could field it — the chain itself is Materialization's choice |
| Transactional assault assembly (Raid, Attack, ActiveDefence intercept) | `Provisioning/GroundCombatAssaultTransaction.cs` — `GroundCombatAssaultTransactionRunner.Run` |
| Per-lane continuity | `MissionContinuityLayer.ResolveActive` dispatches to `ResolveRaidIntent` (`Continuity/MissionContinuityLayer.Raid.cs`), `ResolveAttackIntent` (`.Attack.cs`) and `ResolveActiveDefenceIntent` (`.ActiveDefence.cs`) |
| Walk-home destination of every lifecycle leg | `MissionContinuityLayer.KeepOrReselectHome` (keep a still-owned, reachable base; otherwise `SelectReturnBase`) |
| Which Scout jobs execute from a vantage (never on the focus) | `ObservationVantageSelector.UsesVantage` — a Refresh whose focus a visible scout may not stand on (the Attack observation need). Assignment, `ScoutCostModel`, Provisioning and the ground executor's anchor read it |
| Recon intel staleness (ramp and "stale" threshold) | `ReconIntelSnapshotRegistry.Staleness` / `IsStaleAge` (`reconIntelStaleTurnsLo..Hi`) |
| A Scout job needs the stealth lane (`Required` or positive `DetectionRisk`) | `ReconScoutKinds.NeedsStealth` (`ScoutMissionTarget.NeedsStealth` / `ReconObjective.NeedsStealth`) |
| A scout can serve stealth (this turn / at all) | `ScoutMoverSelector.StealthReadyThisTurn` (Assignment eligibility, garrison extraction) / `CanServeStealth` (continuity claim, vantage choice). `StructuralCandidates` keeps its own diagnostic rule on purpose |
| Scout exposure and stealth-detector risk | `Recon/ScoutRiskModel` (`IsExposed` / `CountDetectors` / `DetectorRisk`, snapshot or live sightings) — frontier annotation, objective scan, vantage ranking, optional-stealth leg risk |
| A met Scout objective is only a waypoint (the durable ground role continues) | `ScoutObjectiveEvaluator.RoleContinuesAtWaypoint` — ground executor, `TaskExecutor` stale-goal path, air executor |
| An air sortie must turn for home (endurance deadline / used-up outbound leg) | `ReconAirSortieState.OutboundCapReached` + must-recover, applied identically by `AirReconStepDirector.PlanStep` and the read-only `ReconAirReservationPrepass.ProjectScoringSortie` (mandatory recovery, capacity) |
| Scout objective met live | `ScoutObjectiveEvaluator.IsSatisfiedLive` — post-execution ledger, ground and air executors, `MissionRevalidator` |
| A durable Scout intent's current objective | `ReconObjectiveEvaluator.ForIntent` — mission re-materialisation and `ActorCommitments`' off-list stealth requirement |
| Writing a Scout outcome into its durable intent (create / advance / absorb) | `MissionContinuityLayer.ApplyScoutPayload` |
| Which durable actor a Recon mission may re-bind | only its own durable intent's actor (`ReconAssignmentPlanner.IsOwnDurableActor`), the rule ground combat applies in `ProvisioningSession.ExcludedForGroundCombat`; a planning witness never relaxes another intent's claim |
| The two air-recon actor states (ready standalone wing / airborne Recon wing) | `ReconAirCapacityPolicy.IsReadyStandaloneWing` / `IsAirborneReconWing` — capacity (`EvaluateDetailed`), `ProvisionAir` (ready / continuing adds a live Recon sortie) and `ReconAirExecutor.FindMandatoryRecoveryActors` |
| A mandatory air obligation (flight recovery / rebase continuation) that cannot progress this turn | `State/AviationObligationStallRegistry` — skipped by `ReconAirExecutor.FindMandatoryRecoveryActors`, `AviationRebasePlanner.FindMandatoryContinuations` and so `StrategicSpendability` until the next turn; the typed loop continues with missions |
| A Scout's `TargetInvalidated` (execution or provisioning) | `Blocked`, never `Failed` (`MissionOutcomeLedger`): Continuity re-validates the objective (`IsIntentStillValid` → re-focus / retire) |
| Support-local failure of a leg (never ends the operation) | `GroundCombatLegs.IsSupportLeg` / `RaidLegOf` / `AttackLegOf` — the ledger's execution and provisioning classifiers and `ReconcileOutcome` (`ReleaseInvalidSupport`) |
| Strategic knowledge of an enemy army | `Analysis/AiMapMemory` sightings. Objectives, Missions, Provisioning and Execution read enemy existence/position only from there. A global `ArmyRegistry` sweep may confirm the outcome of a canonical operation the AI itself just performed (e.g. did the target survive the battle our army fought) — it may never stand in for knowledge of a hidden army, and "absent from the world" is never objective completion. |
| Reaction feasibility evidence | `ReactionWitness` (struct in `Reaction/StrategicReactionPass.cs`) + `Reaction/ReactionOpportunityProbe` |
| Reaction witness arbitration (§28) | `Reaction/ReactionWitnessSelector` |
| Economy objective / key encoding (intent key, attempt key, reservation owner) | `MissionIntentKey.EconomyObjectiveId` + `MissionIntentKey.ForEconomy` / `StableMissionKey.ForEconomy`; a build's reservation owner is `InfrastructureFulfillment.EconomyBuildOwner` (= `EconomyMissionPlanner.OwnerKey` of the mission key) |
| Which strategic reservations one demand-closing action may draw on | `StrategicSpendability.SpendAuthority` (Owner + EconomyCompletesNow), decided once by `InfrastructureFulfillment.SpendAuthorityFor` and read as `AxisDemand.SpendAuthority` by every stage: Phase-A admission (`MaterializationFeasibility`), pricing (`MaterializationCandidateBuilder`), portfolio (`MaterializationPortfolioSolver`), execution (`MaterializationExecutor`) and `InfrastructureFulfillment.TryFulfill` |
| Putting a PlayerGlobal recurring-resource source (ApBonus / Produce*) from hand into play | `DemandLayer.GlobalResourceSourceDemands` — one Economy Phase-A demand per carrier card, pinned by `AxisDemand.EconomySourceCard`: `GlobalResourceFacility` → `InfrastructureFulfillment` (Economy completes now, so other builds' `EconomyDeferredBuild` holds do not block it; when every slot is locked it buys the Base upgrade chosen by `StrategicMaintenancePolicy.TryFindCapacityUnlock`, the same rule as the Phase-B capacity candidate), `GlobalResourceCarrier` (Unit/Hero) → the materialization chain, any placement, no follow-up AP; it competes for the card in the same injective Phase-A portfolio as every other demand, and is closed once the card is in play through any demand (a carrier hero may lead an army or build). Its card value is `StrategicCardEvaluator.ResourceGainRoleFit` in every lane (Phase A, Phase-B surplus, non-combat Facility); Base carriers stay with FoundBase |
| Which build an Economy demand is about (FoundBase vs BuildExtraction) | `DemandLayer.EconomyBuildKind` — Demand, Missions, Phase A, Continuity and the owner keys |
| A build obligation that still leases its site (Active, or transiently Suspended) | `MissionContinuityLayer.IsLiveEconomyBuild` / `HoldsEconomyBuildSite` — lease grant, Demand's committed-site reads, Phase A's protected builds, the active-intent resource hold, the planner's incumbent |
| The Base commitment the switch hysteresis protects / retargets | `DemandLayer.IsRetargetableBaseCommitment` (both Base selectors, `CanReplaceCommittedBase`) |
| Retiring an Economy intent (loan returned, owner's reservations released, removed) | `MissionContinuityLayer.RetireEconomyIntent` — every ResolveActive / ReconcileOutcome / AdvanceIntent / reap / takeover exit; `ResumeEconomyLender` is the only `EconomyLoan -> Active` transition |
| A transient suspension (PoolExhausted / CapabilityUnavailable) is re-tested each pass | `MissionContinuityLayer.ResumeTransientSuspension` (every ResolveActive branch) |
| A no-progress Economy outcome | `ReconcileOutcome`: retire only on a proven route failure (`IsEconomyRouteFailure`: provisioning `NoExecutableStep`, executed `NoSafeStep`/`MoveRejected`); NoMover/MoverContended suspend; anything else ages via StallTurns. Execution-side Economy `TargetInvalidated` is `Blocked` (`MissionOutcomeLedger`). A build ready on its site (`EconomyDeliveryReady`) and a collector holding its site (`EconomyHolding`) are progress |
| A collector already on its site | no planner step (`EconomyMissionPlanner`); `ResolveActive` records the hold as progress |
| Economy intent age | `ShouldReap(intent, turn)`: stall bound or `commitmentMaxTurns` WITHOUT progress; `KeepReturnBuilder` applies the same bound to the "unconditional" return walk |
| Bounded delivery-failure streaks (Base per card+site, Extraction per resource+site) | `EconomyLifecycleState.DeliveryFailureStreaks` |
| Is a mobile collector worth its site (admit / keep) | `EconomyResourceStanding.UsefulMarginalIncomeGain` / `UsefulRetainedIncomeGain` (same test without its own income) |
| The physical pool an Economy mission is funded from | `AllocationSession.PhysicalAvailableFor` — raw stock minus every hold except EconomyDeferredBuild and its own owner; a funded completion owner's physical claim already reduces the remaining stock, so its completion hold is credited once, not subtracted again |
| AP reserved by an Economy completion or reaction | `AllocationSession.Pack` (`ApAvailableFor`) reads the one `StrategicResourceReservationLedger` for every mission axis; funding an Economy owner's own completion credits only the AP that owner has claimed, leaving unclaimed AP protected |
| When the bounded reaction spends its own reservation | `ReactionRoundExecutor.ExecuteRound` releases `StrategicReactionPass` rows after the feasibility recheck and before Phase A and mission allocation; Economy owners' rows survive |
| Threat contacts | `WorldAnalysis.BuildThreat` — honest contacts only (live sightings + AiReconMemory history), each with a position and an ETA. There is no hidden-army (cheat) contact; `ThreatModel.CitadelThreatSeverity` / `BaseThreatSeverity` are the highest Severity against the starting Citadel / any other own Base |
| Economy threat witness (builder, escort, collector, post-build recovery) | `WorldAnalysis.KnownThreatsAffectingEconomyRoute` — known mobile armies of other players within `economyRouteThreatRadius` (1) of the route or `economySiteThreatRadius` (2) of the site. Neutrals never count (the route avoids them; `KnownHostileAtHex` covers one standing on the site). A listed threat is answered by an escort (`EconomyRosterSafe`), never by a score penalty |
| Home threat in task scoring | `TaskScore.CitadelThreatRisk` / `BaseThreatRisk` (`TaskScoreEvaluator.CitadelThreatRisk(snap)` / `BaseThreatRisk(snap)`), kept apart from the task-hex `HexThreatRisk`. Charged to Economy build tasks (extraction, Base, builder hero) — not to collectors — and to the offensive ground-combat intrinsics (Raid, Attack, incl. a fogged Raid incumbent); never to ActiveDefence. Home threat never moves the Aggression Radar (no threat floor, no siege damp): the axis also carries ActiveDefence, so offensive restraint lives only in this slot. `taskScoreBaseThreatRiskMax = 0`: Base threat is off for every task |
| FoundBase protection | `DemandLayer.AssessEconomyArmy` requires a transferable ground body for FoundBase; extraction/return pricing is independent of this requirement. The final current builder is reassessed in `InfrastructureFulfillment.BuildEconomyBaseCandidate` before spend. `BuildingPlayExecutor.PlanBaseGarrison` may prove a held, affordable, deployable defender card for synchronous completion instead. `InfrastructureActions.TryFoundBase` commits only after that continuation succeeds; canonical transfer/card play is the executor of the defender. |
| Base proximity | `TaskScore.BaseCrowdingCost`, intrinsic Cost: 8 at distance 1, 4 at distance 2, 0 at distance >=3. Own-home spacing is not a structural veto. Physical occupancy and the enemy-Citadel perimeter retain their existing gates. |
| Fresh Attack selection | Candidate knowledge may accumulate behind a closed mobilization gate; `AggressionMissionPlanner.AppendAttack`, `AppendUnboundAttackDemand` and unbound `ObservationNeeds` require the existing gate before selecting a fresh objective or demanding target-specific reinforcement. Bound operations retain their phase/target continuity. |
| Economy builder candidate gates | `DemandLayer.CandidateRejection` (structural, `EconomyBuilderCandidates`: shape / assignment / claim); `ProvisionEconomy.CandidateRejection` + `EvaluateGarrisonCandidate` (live); the FoundBase traces print these answers |

## Verified boundary invariants (02F–02H audit)

* **One `ResourceAllocator`** — no per-mission/per-axis allocator.
* **One `StrategicCardEvaluator`** — no `Hero`/`Reaction`/`PhaseB`/`Aviation` card scorer.
* **Executors do not plan or rescore** — `Execution/TaskExecutor`, `ReconGroundExecutor` and
  `ReactionRoundExecutor` call canonical gameplay actions and return a structured result; the
  only evaluator calls are `Is*SatisfiedLive` completion checks (a legit §37 concern), never
  objective selection or replacement-mission synthesis (the stale-Explore replacement builder
  was removed — a stale-goal Scout is recorded and re-targeted by Continuity next pass).
* **Air recon Assignment/Execution split.** `ReconAssignmentPlanner.AppendAirCandidates` binds
  funded AirSweep missions only to already formed `AirExisting` wings. The shared candidate
  solver claims each actor once; `ReconAirReservationPrepass.EvaluateAirStructuralFeasibility`
  proves a useful, recoverable live route. `ProvisioningManager.ProvisionAir` reserves its
  activation cost and any guaranteed next-turn continuation. Recon never forms aircraft from
  storage. Explore and other ground objectives cannot claim an aviation actor.
  The shared allocator deliberately does not apply Recon's ground `HardCap`, because executor kind
  is unknown there. `ReconAssignmentPlanner` applies that cap only to ground-bound candidates and
  preserves the independent `MaxAirReconActorsPerTurn` ceiling for aviation, including across
  provisioning re-packs. Thus air observation may run in addition to the allowed ground lanes.
  `AirReconPlanner.Plan` validates only the provisioned existing actors. Mandatory recovery
  runs independently when a live airborne wing is already committed to returning. Every
  tactical decision stays in `Recon/AirReconStepDirector.PlanStep`, which replans the
  outbound or return step and optionally strikes a recoverable target. The executor issues
  canonical gameplay actions and records the resulting state changes.
* **Provisioning plays no strategic cards** — `Provisioning/*` binds actors and locks; it never
  calls `MaterializationExecutor` / `StrategicPhaseA/B`.
* **The strategic layer is skill-agnostic** — Strategy / Materialization / Missions / Reaction
  branch only on the AI's own `CapabilityKind` taxonomy, never on a gameplay ability name
  (`Splash` / `Regen` / `Summon` / `AbilityKind.*`). Ability specifics stay behind
  `StrategicEffectRegistry.Roles(...)`, so a new effect needs a registry entry, not a manager
  `if`.
* **Execution results are a structured family** — `MaterializationResult` / `CardPlayResult` /
  `BuildingPlayResult` / `InfraFulfillResult` / `ExecutionResult` / `AirReconExecutionResult`
  implement `IV2ActionResult` and project to the common `V2ActionOutcome` (`Succeeded` /
  `StateChanged` / `ApSpent` / `ResourcesSpent` / `Played` / `Generated` / `Attached` / `Moved` /
  `Created` / `NeedsReplan` / `StateVersionAfter` / `FailReason`). Each keeps its domain payload; a
  caller that only needs the lifecycle facts reads `.Outcome`. `MaterializationExecutor` measures
  the real H/E/M/T delta for `ResourcesSpent`; `BuildingPlayExecutor.BuildExtractionFacility`
  reports the facility definition's `resourceCost` and `InfraFulfillResult.Outcome.Played` is the
  real hand-card consumption (true for the DEV Facility card, false for the hero-built extraction
  site). (`ProvisioningResult` stays on its own `Success`/`Failure` shape — provisioning is §34,
  not §35 execution.)
* **One execution state-version counter** — `State/V2StateVersion`. It is bumped by every V2
  execution-tier operation that mutates authoritative world state: `CardPlayExecutor` /
  `BuildingPlayExecutor` / `MaterializationExecutor` / `TaskExecutor`, the air-recon executor
  (per confirmed move / launch / strike), `StrategicPhaseB` tempo (Draw / capacity-upgrade /
  Pressure, which their own executors do not version) and the turn-start hand refill
  (`Strategy/HandReplenishPolicy`, per draw). Phase B's parked-candidate lifecycle keys
  on `V2StateVersion.Current` directly — there is no second local counter.

### Founding completion audit

Final FoundBase admission reuses `DemandLayer.SelectEconomyBuilder` with live intent/actor commitments, including for an on-site hero. `PlanBaseGarrison` accepts only zero-cost legal transfers, an existing ground defender, or a jointly admitted held-card deployment. Card deployments defer hand consumption to the enclosing founding commit. `InfrastructureActions.TryFoundBase` owns compensation of the wallet, local rosters and join activation coverage; `ArmyData.MarkUnitActivationPaid` restores that coverage through its existing boundary. A compensated refusal refreshes Analysis without reporting an AP debit or a completed project. Phase A counts the additional consumed defender card. Local Recon capture uses transient execution state and cannot write the Explore/Refresh patrol registry.


## Lifecycle ownership migration (706b1bbd baseline)

This is a session/lease ownership migration checkpoint; full operational result ingestion and transaction migration is not complete.

* `AiTurnSession` owns one player/turn scope. Pipeline creates it before card refill and closes it explicitly after the final summary; `using` is an exception/disposal fallback. Starting the next turn also closes a scope abandoned by native coroutine cancellation. Persistent stores are never cleared by turn disposal.
* Session delegates reservation storage to `StrategicResourceReservationLedger` and pending-fact storage to `StrategicInterruptRegistry`. It begins telemetry/exhaustion/reservations, and ends reservations, invalidations, exhaustion, temporary capability protection, tempo, aviation stall and the continuation window. Pass-scoped assignment/admission/budget objects stay with their existing owners. Diagnostic summaries are emitted before cleanup.
* `MissionIntentState` stores the durable intent dictionary and exposes the specialised `EconomyLifecycleState` and `DevelopmentLifecycleState` owners. Economy counters/suppression and generated Development cards have exactly one store each. Legacy methods delegate and own no copies. Recon data is in `ReconTurnState`, one immutable turn scope shared with compatibility lookups; detached test states remain isolated. There are no separate trim/used turn clocks.
* `MissionLeaseBook` owns the session actor claim table, keyed by physical actor with its MissionIntentKey owners. `ActorCommitments` is a storage-free normalized claimed-actor/mutation-contract view. `MissionActorPolicy` alone owns the existing role validity and contract construction. Compatibility snapshot builders call that policy. Detached pass views remain derived, and cannot retire session operations. Housekeeping consumes the same normalized view and gains no strategic policy dependency.
* `MissionStepResult.Disposition` is the stored common result status. `MissionTurnOutcome.Outcome` and `StructuralFailure` are compatibility projections. `MissionStepResult` stores typed payloads once; `MissionTurnOutcome` is a compatibility facade over Recon/Raid/Attack/Defence/Economy/Development and shared ground-combat facts. Reading a missing payload creates no state; nullable actor/loan defaults remain null. `MissionStepResult<TPayload>` supports a new fact schema without editing common result fields. The normalization boundary dispatches unchanged execution classification into existing domain Continuity partials; it retains provisioning/default interruption handling. Ledger only correlates/delegates and has no domain field schema or Economy predicate. EconomyLifecycleState owns the unchanged live Economy objective answer. Continuity remains authority for durable transitions; its operational entry points still require the legacy facade.
* `WorldDeltaLifecycle` owns the process-monotonic revision and typed factual publication. `V2StateVersion` is a freshness compatibility adapter without storage. Committed mutation endpoints call `CommitMutation`; observations call `Publish`, which does not bump again. Dirty masks, reason-scoped evidence and cache consumption remain unchanged. Synchronous FoundBase stages nested card-play stamps and advances once on canonical commit; rollback drops them. Transaction scopes must never cross a coroutine yield. Raid/Attack terminal handoff commit/publication use one Apply with their original mask and return the existing StateVersionAfter receipt. TaskExecutor stamps only results without a child receipt; it does not advance again for an already committed step. Continuous ground/capture and strike→return sequences still require boundary consolidation before global exactly-once acceptance. `AiTurnSession.Apply` supports atomic combined revision/fact publication, but old producer boundaries have not all been consolidated into combined deltas.
* `MissionIntentKey` remains durable operation identity. `AttemptId` is correlation; `StableMissionKey` can identify a distinct leg. `ReservationOwner` is an immutable typed wrapper over MissionIntentKey (or a pass token); it introduces no independent OperationId. Economy token encoding and `SpendAuthority` are unchanged. StrategicResourceReservation.Owner is a compatibility projection over Identity.

* Session.RefreshActors feeds the same role-valid claims/contracts into its lease table at the existing pipeline refresh boundaries. Pass-local consumed-actor protection retains its existing reset boundaries. MissionIntentState removal retires through the lease book; in-place continuity rekeys transfer claims. Domain continuity decides when a durable operation ends. Resource requests, stage expiry/replacement/downgrade and release enter MissionLeaseBook and delegate to the original ledger storage. End session closes actor lease handles and runs the existing reservation expiry/leak boundary.

Remaining work: domain policies publishing operation-level terminal dispositions, retirement of the internal legacy domain view after direct typed-policy migration, combined mutation/observation transactions, all old production lifecycle callers removed, full Unity EditMode and gameplay E2E acceptance. In particular, Completed at the legacy boundary can complete only a Raid/Attack leg or Recon waypoint; it must not automatically release the durable operation's actor ownership.

Domain transition composition: the common ReconcileOutcome coordinator switches only on normalized disposition and common objective facts. Ordered callbacks in existing domain partials own Raid target/campaign completion, Attack intermediate/side legs, Recon waypoint continuation and Economy recovery/failure streaks. Creation callbacks preserve the existing payload priority; retirement callbacks prepare domain state before the existing intent removal releases its operation lease. No new state is stored in composition. AdvanceIntent owns shared accounting/suspension/aging and invokes ordered domain mover/fact callbacks; role interpretations and Economy suppression are in their domain partials. Composition preserves existing durable pinning and capability-failure aging exceptions. ResolveActive still contains domain coordination; its remaining details and the full operation-level disposition contract have not been declared fully migrated.

Operational result ingress: Pipeline reads `FinalizeSteps()` and calls `AiTurnSession.Settle(MissionStepResult)`. Public step/end reconciliation accepts common results. `MissionTurnOutcome.View` is a temporary noncopying adapter for existing internal domain transitions; both objects share one common fact record and one typed payload dictionary. Changes through either API cannot diverge. The generic constructor derives MissionKind from the existing operation key; no additional operation identity is created. Session rejects settlement after its turn has ended.

Allowed direction at this stage: generic session/result/delta infrastructure delegates storage to old adapters; domain policies invoke authoritative game eligibility; normalized ownership/Housekeeping do not implement mission strategy. Bank formulas, physical spending and allocator competition are unchanged.
