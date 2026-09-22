using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;

using Game.Cards;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  AI STRATEGY V2 — PARALLEL PIPELINE  (design record, 2026-08-29)
    // ===========================================================================================
    //
    //  WHY THIS EXISTS
    //  --------------------------------------------------------------------------------------------
    //  V1 (AiStrategyDirector + AiOperationPlanner + AiTurnController.Decide + the Level-1
    //  planners) picks actions well enough, but its *state* keeps corrupting itself: half-reserved
    //  armies, locked raid slots, orphaned field armies, reservation leaks, estimate-vs-execution
    //  desync, oscillation between half-built plans. The project's AiDebug.log history is ~20
    //  rounds of fixes plus follow-ups all against that same class of bug — evidence it is
    //  ARCHITECTURAL, not incidental. V2's job is to make that class of bug impossible *by
    //  construction*, not patchable.
    //
    //  WHAT "SOLID" MEANS HERE — and what it does NOT mean
    //  --------------------------------------------------------------------------------------------
    //  V2 is plumbing that cannot corrupt its own state and cannot thrash. It does NOT, on its
    //  own, make the AI play better. Decision quality still lives entirely in the response curves
    //  inside the evaluators and in mission Base Value scoring — those are ported from V1 and
    //  tuned exactly as before. "Plays smarter" is separate work in the same evaluators.
    //
    //  THE SWITCH  (hard rule)
    //  --------------------------------------------------------------------------------------------
    //  V1 stays the shipping default. V2 is enabled only by AiConfig.aiStrategyV2Enabled. The two
    //  NEVER both run in one AI turn — AiTurnController.RunTurn forks at the top: flag set => this
    //  pipeline owns the whole turn and RunTurn returns immediately after it; flag clear => V1
    //  runs untouched and this file is dead code. V1 is deliberately NOT deleted: its planners,
    //  estimators and guards are ported into V2 one method at a time, adapted, never rewritten
    //  from memory.
    //
    //  THE RADAR  (settled — do not re-litigate)
    //  --------------------------------------------------------------------------------------------
    //  Normalised: sum of all axes == 1. It is an *allocation vector* — each axis is "what share
    //  of the shared resource pool goes here". Independent [0..1] axes were rejected: every action
    //  draws on the same pool, so unbacked independent desires are a false model.
    //    Final axes: Recon, Aggression, Economy, Development.  (DesireAxis enum below.) Active
    //    Defence is not a separate axis — it is folded into Aggression.
    //    Management is NOT an axis — there is no DesireAxis.Management and no ManagementEvaluator.
    //    Card play + capability preparation is a SERVICE, split across two managers:
    //      · StrategicManager (StrategicManager.cs) — the single owner of V2 Unit/Hero/Recce card
    //        play. Axes expose AxisDemand[] ("what capability is missing"); StrategicManager decides
    //        how (which card, where, reuse vs. create an army, whether it is worth it). Phase A
    //        (FulfillDemands, before mission planning) is charged to demand.RequestingAxis via the
    //        shared AxisBudgetLedger — the axis that needs the capability pays. Phase B (UseSurplus,
    //        after mission execution) spends only genuinely-remaining real AP/resources, no slice.
    //      · HousekeepingManager (below) — the OFF-BUDGET post-mission army/garrison reorganisation
    //        + cleanup pass, guaranteed minimum (housekeepingApReserve), same way garrison reorg
    //        sits outside V1's arbiter. Never has to "win" priority against Aggression to happen.
    //  Two ABSOLUTE scalars are kept OUTSIDE the simplex (DesireVector.MilitaryThreat /
    //  .EconomicRunway): the normalised vector alone can't tell "calm, 40% to defence because
    //  nothing else competed" from "existential threat, 40% is nowhere near enough". These two
    //  scalars measure world state, are not a share of anything, and act as modifiers (widen
    //  aggression thresholds, permit risky trades, force turtle). Nothing beyond these two.
    //
    //  RAW DESIRES -> NORMALIZER -> RADAR
    //  --------------------------------------------------------------------------------------------
    //  Evaluators produce an independent raw intensity per axis in [0..1] (interpretable,
    //  per-axis-tunable). Normalisation to sum==1 happens ONCE, here, at the boundary into
    //  allocation. Evaluators use RESPONSE CURVES (one curve per input factor -> contribution,
    //  summed), the V1 AiStrategyDirector style. NOT fuzzy logic — fuzzy gives the same result but
    //  is harder to tune (membership-function shapes, rule-conflict resolution).
    //
    //  THE FOUR MIDDLE-BAND RISKS  (must be designed in, never patched on later)
    //  --------------------------------------------------------------------------------------------
    //  1. Mission<->axis is MANY-TO-MANY. A raid on a neutral guarding a factory serves
    //     Aggression + Economy + Development at once. Every MissionProposal carries an
    //     AxisContribution vector, never a single category. The allocator cuts per-axis budget
    //     SLICES from the radar first, then packs missions into slices, a multi-axis mission
    //     drawing proportionally from several.
    //  2. RE-ALLOCATE ON FAIL is a loop with a HARD BOUND. Provisioning FAIL -> release tentative
    //     budget -> mark mission rejected-this-turn -> re-allocate remainder. Bounded by a max
    //     iteration count + a per-mission rejected set + a cooldown (reuse the
    //     raidPlanRejectCooldownTurns pattern). Without the bound this is the V1 stall-watchdog
    //     bug class all over again.
    //  3. ONE ESTIMATOR, TWO STAGES. MissionRequirements ("raid needs CombatPower >= X") and
    //     Provisioning feasibility validation MUST call the same estimator module (WorthIt /
    //     battle-estimate). Two different estimates => "allocator approves, provisioning can't
    //     deliver" thrash (V1 hit this exact bug: raid diagnostics desynced from
    //     raidMinimumWinChance).
    //  4. COMMITMENT IS FIRST-CLASS. The pipeline recomputes everything each cycle; without a
    //     commitment layer a half-assembled raid is dropped on a 0.05 radar wobble. In-flight
    //     missions reach the allocator as "already funded, cancellation cost = reserved value +
    //     sunk turns", their reservations are sticky, retarget hysteresis applies. Start simple:
    //     commitments honoured to completion; add allocator-driven pre-emption later.
    //
    //  PROVISIONING MANAGER
    //  --------------------------------------------------------------------------------------------
    //  ONE entry point, ONE exit point, ATOMIC. Consumes the tentative allocation in priority
    //  order, one mission at a time, so mission N sees the resources mission N-1 already claimed.
    //  Per mission: Army/Card/Equipment logic -> Assembly Plan -> feasibility validation (same
    //  estimator as risk 3) -> SUCCESS: reserve/claim/spend all-or-nothing, emit ProvisioningResult
    //  / FAIL: change nothing, return FAIL. No partial-commit state can exist between the doors.
    //  This is the single biggest reason V2 is worth building.
    //
    //  BUILD ORDER  (recon end-to-end first, aggression second)
    //  --------------------------------------------------------------------------------------------
    //   1. Contracts + walking skeleton (THIS FILE) — every stage a stub, full loop runs, zero
    //      tasks, no throw, no game-state mutation. V1/V2 switch + fork.
    //   2. WorldAnalysis — one shared scan (threat map, opportunity map, map knowledge, army /
    //      garrison state, resource pool). Port V1 scans. Everything downstream reads only this.
    //      DONE 2026-08-29 — WorldSnapshot.cs (types) + WorldAnalysis.cs (Scan) + AiPower.cs
    //      (strength model, replaces WorthIt.AttackSum+DefenseSum) + AiConfigV2.cs. Layers:
    //      Self / Known (honest) / TrueWorld (cheat) / MapKnowledge / EconomyStanding / ThreatModel.
    //      Cheat/honest boundary is a type invariant on EnemyContactSnapshot (a Cheat contact
    //      can't carry a Position). V1 CheatEstimateRaiderThreat SCOPE ported into the ThreatModel
    //      cheat-contact loop; DynamicPatrolUrgencyScore dropped (-> continuous Severity + MissionLayer).
    //      Frontier is still a stub until step 4.
    //   3. Recon + Aggression evaluators -> raw desires -> Normalizer -> Radar. Response curves.
    //      N-axis normalizer from the start even though only 2 axes are live.
    //   4. Recon planner -> one Scout MissionProposal -> MissionRequirements. Establish the shared
    //      Base Value scale (0..100) and the shared estimator module now.
    //   5. ResourceAllocator — radar -> slices -> many-to-many packing -> ordered TentativeAllocation.
    //      Bake in the iteration bound + rejected set + cooldown (risk 2).
    //   6. ProvisioningManager (Scout needs no Army Logic — just atomic AP claim) + TaskExecutor.
    //      >>> FIRST TEST STATE: AI actually scouts, end to end, in game. <<<
    //   7. Mission Continuity — multi-turn recon survives radar noise.
    //      DONE 2026-08-29 — MissionIntent.cs (MissionIntentKey / ScoutIntent / MissionIntent /
    //      registry, CommitmentTier + IntentStatus, MissionOutcomeLedger, MissionTurnOutcome
    //      registry, CommitmentTier + IntentStatus, MissionOutcomeLedger, MissionContinuityLayer:
    //      ResolveActive / BindFunding / ReconcileAfterTurn) + ScoutObjectiveEvaluator.cs (the one
    //      completion/validity home). INTENT (durable objective, drives retarget hysteresis in
    //      MissionLayer) is split from COMMITMENT (a funding policy — Soft for a far Surveil that
    //      has started moving; funded first, sticky, but Σ commitments <= real AP pool). Explore
    //      keeps an intent with NO funding. Pre-emption is deferred: commitments honoured to
    //      completion, ContinuationValue / SwitchingCost recorded but not yet weighed. Verified by
    //      Tools/commitment-sim (22/22).
    //   8. Manager — off-budget housekeeping (reservation cleanup, garrison reorg, last-defender
    //      guard). Its safety-net half may land as early as step 6.
    //   9. Aggression as the second mission type — Raid planner, CombatPower/Army/Hero
    //      requirements via the shared estimator, Army Logic in provisioning (ready army ->
    //      garrison detach -> assemble, with V1 preflight guards).
    //      >>> TARGET TEST STATE: scout + raid concurrently, allocator splits the pool by radar,
    //      20-turn run with no reservation leaks and no oscillation. <<<
    //
    //  GLOSSARY  (V2 term -> V1 type — they are similar-but-different; do not conflate on port)
    //  --------------------------------------------------------------------------------------------
    //    V2 "Planner"     : NOT AiScoutPlanner / AiAggressionPlanner / AiDevelopmentPlanner —
    //                       those are V1 Level-1 category planners. V2 planners only emit
    //                       MissionProposals; they never score cross-category or touch registries.
    //    V2 "Task"        : NOT AiTaskKind / AiTaskRegistry — a V2 Task is the concrete executable
    //                       step list produced AFTER provisioning succeeds.
    //    V2 "Radar/axis"  : conceptually V1's AiStrategyAssessment, but normalised (sum==1) and
    //                       without a Management axis.
    //    Reused as-is     : AiResourcePool, AiResourceReservation, WorthIt, AiMapMemory,
    //                       VisionSystem, ArmyActions, HexSelectionController — V2 mutates game
    //                       state only through the same player-agnostic paths V1 (and the human)
    //                       already use.
    // ===========================================================================================

    // Normalised radar axes. Order is the canonical iteration order for every Dictionary<DesireAxis,*>
    // and every log line below. Management is intentionally absent — see the file header.
    public enum DesireAxis { Recon, Aggression, Economy, Development }

    public static class DesireAxes
    {
        public static readonly DesireAxis[] All =
        {
            DesireAxis.Recon, DesireAxis.Aggression,
            DesireAxis.Economy, DesireAxis.Development,
        };

        public static string Abbrev(DesireAxis a)
        {
            switch (a)
            {
                case DesireAxis.Recon: return "RCN";
                case DesireAxis.Aggression: return "AGG";
                case DesireAxis.Economy: return "ECO";
                default: return "DEV";
            }
        }

        // Strategy-level mapping from factual state invalidations to the task families whose
        // prior conclusions are now dirty. State owns the flags; Orchestration only asks this
        // policy which local family to re-admit.
        internal static StrategicInvalidationReason InvalidationMaskFor(DesireAxis axis)
        {
            switch (axis)
            {
                case DesireAxis.Recon:
                    return StrategicInvalidationReason.ReconKnowledge
                        | StrategicInvalidationReason.Actor;
                case DesireAxis.Aggression:
                    return StrategicInvalidationReason.Contact
                        | StrategicInvalidationReason.Actor
                        | StrategicInvalidationReason.EventState
                        | StrategicInvalidationReason.Threat;
                case DesireAxis.Economy:
                    // Economy feasibility depends on where a Hero-led builder is NOW, not only
                    // on newly discovered resources. A Recon step can deliver that builder onto
                    // an already-known extraction hex; without Actor here the settled-step loop
                    // re-admits Recon alone and immediately walks the Hero away before the
                    // existing Phase-A infrastructure owner gets another chance to build.
                    return StrategicInvalidationReason.Actor
                        | StrategicInvalidationReason.Resources
                        | StrategicInvalidationReason.Infrastructure
                        | StrategicInvalidationReason.Hand
                        | StrategicInvalidationReason.Capability
                        | StrategicInvalidationReason.ResourceSite;
                case DesireAxis.Development:
                    return StrategicInvalidationReason.Actor
                        | StrategicInvalidationReason.Resources
                        | StrategicInvalidationReason.Infrastructure
                        | StrategicInvalidationReason.Hand
                        | StrategicInvalidationReason.Capability
                        | StrategicInvalidationReason.ResourceSite;
                default:
                    return StrategicInvalidationReason.None;
            }
        }
    }

    // --- Stage 2 output: the single shared world scan (WorldSnapshot). Every later stage reads
    //     ONLY this, never raw game state. Types live in WorldSnapshot.cs; the scan that fills it
    //     is WorldAnalysis.Scan in WorldAnalysis.cs (build-order step 2, done 2026-08-29).

    // --- Stage 3a output: INDEPENDENT raw desire intensities in [0..1], one per axis, plus the
    //     two out-of-simplex absolute scalars. Not yet normalised.
    public sealed class DesireVector
    {
        public readonly Dictionary<DesireAxis, float> Raw = new Dictionary<DesireAxis, float>();
        // Absolute, NOT a share of anything. Modifiers only (see file header).
        public float MilitaryThreat;   // 0 = no known threat ... 1 = existential
        public float EconomicRunway;   // 0 = broke/stalled ... 1 = deep surplus

        public static DesireVector Neutral()
        {
            var v = new DesireVector();
            foreach (DesireAxis a in DesireAxes.All)
                v.Raw[a] = 0.5f;
            return v;
        }
    }

    // --- Stage 3b output: the normalised allocation vector (sum of Weight == 1).
    public sealed class Radar
    {
        public readonly Dictionary<DesireAxis, float> Weight = new Dictionary<DesireAxis, float>();

        public static Radar Even()
        {
            var r = new Radar();
            foreach (DesireAxis a in DesireAxes.All)
                r.Weight[a] = 1f / DesireAxes.All.Length;
            return r;
        }

        // The ONLY normalisation point in the pipeline. Raw independent intensities in, an
        // allocation vector summing to 1 out.
        public static Radar Normalize(DesireVector desires)
        {
            var r = new Radar();
            float sum = 0f;
            foreach (DesireAxis a in DesireAxes.All)
                sum += UnityEngine.Mathf.Max(0f, desires.Raw.TryGetValue(a, out float w) ? w : 0f);
            if (sum < 0.0001f)
                return Even();
            foreach (DesireAxis a in DesireAxes.All)
                r.Weight[a] = UnityEngine.Mathf.Max(0f, desires.Raw[a]) / sum;
            return r;
        }

        public string DebugLine()
        {
            return string.Join(" ", DesireAxes.All.Select(a =>
                $"{DesireAxes.Abbrev(a)} {Weight[a].ToString("0.00", CultureInfo.InvariantCulture)}"));
        }
    }

    // --- The strategic coefficient that scales mission VALUE by Radar (RadarValueScale) now
    //     lives in Strategy/Desire (DesireEvaluators.cs) — it computes a Radar-derived number, the
    //     same responsibility as the rest of that file, not an Orchestration one.

    // --- How much each axis a single mission serves. MANY-TO-MANY (risk 1): never collapse to one
    //     category. Values are 0..1 "relevance", not required to sum to anything.
    public sealed class AxisContribution
    {
        public readonly Dictionary<DesireAxis, float> Value = new Dictionary<DesireAxis, float>();
    }

    // Concrete mission kinds. Each maps to a V2 Task builder in TaskExecutor. Was a bare string
    // until build-order step 4 — typed now, before anything downstream depends on the spelling.
    public enum MissionKind { Scout, Raid, ActiveDefence, Economy, Development }

    public enum EconomyTaskKind
    {
        BuildExtraction,
        FoundBase,
        MobileCollection,
        ReturnCollector,
        ReturnBuilder,
    }

    public struct EconomyMissionTarget
    {
        public EconomyTaskKind Kind;
        public HexCoord TargetHex;
        public ResourceType? ResourceType;
        public string ObjectiveId;
        public int? BuilderArmyId;
        public int? CollectorArmyId;
        public int? CollectorSourceArmyId;
        public int ExpectedMarginalYield;
        public HexCoord? SafeReturnHex;
        public CardData BuildCard;
        public ResourceCost BuildResourceCost;
        public float BuildApCost;
        public float BuildValue;
        public float MinimumFollowupAp;
        public IReadOnlyList<EconomyBuilderRouteSnapshot> BuilderRoutes;
        public int ProjectedActivationApCost;
        public int ProjectedMaxMovement;
    }

    // The *existing* hero, not a card-in-hand or an anonymous "operator" demand. This target
    // is persisted as a DevelopmentIntent and revalidated at every execution boundary.
    public struct DevelopmentMissionTarget
    {
        public HexCoord FacilityHex;
        public ResearchProductionMode Mode;
        public Game.Units.UnitData Hero;
        public string HeroKey;
        public int? SourceArmyId;
        public float IntrinsicValue;
    }

    // A Scout mission's focus. Explore -> a MapKnowledge.Frontier hex; Refresh -> a previously
    // observed hex whose frozen IntelAge is stale; Surveil -> a stale honest contact's last-known
    // hex (Contact non-null). The numeric identities remain Explore=0, Surveil=1, Refresh=2.
    public enum ScoutTargetKind { Explore, Surveil, Refresh }

    // How hidden the mover must be by the time it reaches the risky leg. None -> any scout.
    // Required -> the mover must be hidden OR able to enter stealth first (a visible scout is not a
    // valid executor at all — parity with V1's hard exclusion). Preferred is reserved for a future
    // softer tier; step 4 never emits it.
    public enum StealthRequirement { None, Preferred, Required }

    public struct ScoutMissionTarget
    {
        public HexCoord FocusHex;
        public ScoutTargetKind Kind;
        public EnemyContactSnapshot Contact;   // non-null ONLY for Surveil

        public StealthRequirement Stealth;
        public float DetectionRisk;            // [0..1] — 0 unless the enemy can actually detect stealth here
    }

    // --- Stage 4 output: a concrete thing the AI could do, with the resources it would need.
    public sealed class MissionProposal
    {
        // --- Correlation (AiV2Trace debuggability pass) ---------------------------------------
        // AttemptId — this proposal's id for THIS planning pass ("{scope}-M01"). A fresh id every
        // pass (a re-materialised intent is a NEW attempt of a durable objective). Distinct from
        // StableMissionKey (stable proposal/execution identity) and MissionIntentKey (durable
        // multi-turn intent). Assigned by the orchestrator right after the pass's mission list is
        // built; it then rides through FundedEntry → ProvisionedMission → ExecutionResult →
        // MissionOutcomeLedger → MissionTurnOutcome → MissionContinuity.
        public string AttemptId;
        // Every DemandTraceId whose capability shortage was blocking this exact operation (an
        // exact target-hex + capability match — never inferred from the shared axis, spec §1.6).
        // A SET, not a forced 1:1: a raid gated by both a Hero and a FieldCombatPower shortage
        // links both. Empty => "none".
        public readonly System.Collections.Generic.List<string> CauseDemandTraceIds =
            new System.Collections.Generic.List<string>();
        public string CauseDemandTrace =>
            CauseDemandTraceIds.Count == 0 ? "none" : "[" + string.Join(",", CauseDemandTraceIds) + "]";
        public MissionKind Kind;
        public object Target;               // boxed ScoutMissionTarget for Scout; typed per-kind
        public float BaseValue;             // shared 0..100 scale across ALL mission kinds — INTRINSIC merit
        // Radar model #1a — BaseValue scaled by the radar weight of the axis/axes this mission
        // serves (RadarValueScale). Set ONCE by the orchestrator right after the proposal list is
        // built. This is the figure the ResourceAllocator ranks on CROSS-LANE; BaseValue stays the
        // radar-blind intrinsic merit and still orders WITHIN a lane via LocalAdmissionScore.
        public float EffectiveValue;
        public readonly AxisContribution Axes = new AxisContribution();
        public MissionRequirements Requirements;

        // Set ONLY when this proposal was re-materialised from a durable MissionIntent (step 7) —
        // the mover that carried the intent last turn. Provisioning's assignment solver prefers it
        // (a tie-break, not a reservation). null for a fresh proposal.
        public int? PreferredMoverArmyId;

        // Step 7.1 — this proposal is an active MissionIntent re-materialised this turn, not a
        // fresh candidate. DurableFundingTier is that intent's funding policy (None for Explore /
        // short Surveil; Soft/Hard reach the allocator as pre-bound Commitments, never through the
        // fresh loop). Together they let MissionAdmissionPolicy.AdmissionRank apply the retarget
        // hysteresis at the allocator's K-cut, not just inside the beam.
        public bool FromDurableIntent;
        public CommitmentTier DurableFundingTier;

        // Planner-LOCAL preference for ordering alternatives WITHIN one execution lane / mission
        // type — LocalAdmissionScore = BaseValue * the relevant Recon sub-desire * a risk factor.
        // The allocator uses it (via MissionAdmissionPolicy.AdmissionRank) only to pick between
        // same-lane Recon alternatives; cross-lane ordering stays on BaseValue + radar slices, so
        // the Recon sub-desire is never counted twice.
        public float LocalAdmissionScore;
        public string Explain;
    }

    // Resources to fund THIS allocation cycle (NOT a multi-turn projection — a mission that needs
    // another turn comes back through the allocator next turn as a commitment and re-pays then).
    // Computed with the SAME estimator provisioning will use (risk 3) — ScoutCostModel here.
    // Build-order step 4 fills the AP + Energy envelope for Scout; step 9 adds the rest.
    public sealed class MissionRequirements
    {
        public bool MoverKnown;              // false -> sized off a notional cheap mover; Provisioning (step 6) resolves the real one

        public float ApMinimum, ApDesired, ApMaximum;
        public float EnergyMinimum, EnergyDesired, EnergyMaximum;

        // Step 9 — the deferred physical-resource contract, closed. These are EXECUTION costs that
        // remain AFTER Strategic Manager Phase A (Phase A's preparation spend is already gone from
        // the real pool — the allocator must not count it again). For a Scout, and for many Raids,
        // H/M/T are all 0; a resource dimension existing in the shared contract does not oblige a
        // mission to spend from it. Checked GLOBALLY by ResourceAllocator against one post-Phase-A
        // physical pool — never radar-sliced (AxisBudgetLedger stays AP-only, spec §18).
        public float HumanMinimum, HumanDesired, HumanMaximum;
        public float MaterialsMinimum, MaterialsDesired, MaterialsMaximum;
        public float TechMinimum, TechDesired, TechMaximum;

        // Step 9 — structural requirements. Describe WHAT the mission needs; they never name a
        // concrete actor (that is ProvisioningManager's job). A Scout leaves these at their zero
        // defaults.
        public bool RequiresArmy;
        public bool RequiresHero;
        public float CombatPowerMinimum;
        public float CombatPowerDesired;
        public TraitPreference RequiredCombatTraits;

        public int EtaTurns;                 // ceil(distance / move budget) — informational, not a resource
        public float EstimatedDistance;
    }

    // --- Stage 7: an in-flight mission the allocator must fund BEFORE fresh decisions and may not
    //     drop over a small Radar move. It is a FUNDING POLICY on a durable MissionIntent, not the
    //     intent itself (Intent != Commitment). ContinuationValue / SwitchingCost are forward-
    //     looking — the value of FINISHING and the cost of ABANDONING — and are recorded for a
    //     future pre-emption pass. Sunk AP / turns invested are telemetry on MissionIntent and are
    //     NEVER added here. Types + the MissionContinuityLayer that produces these live in
    //     MissionIntent.cs (build-order step 7).
    public sealed class Commitment
    {
        public MissionIntentKey IntentKey;
        public MissionProposal Mission;
        public CommitmentTier Tier;

        public float ContinuationValue;     // intrinsic merit of completing (== current proposal BaseValue in step 7)
        public float SwitchingCost;         // real loss from abandoning + restarting elsewhere (0 until pre-emption, step 9+)
        public float ProtectedValue => ContinuationValue + SwitchingCost;
    }

    // --- Stage 5 types (BudgetSlice / FundingStage / FundedEntry / DeferReason / DeferredEntry /
    //     TentativeAllocation / StableMissionKey / ResourceVector / ProvisionFailureKind /
    //     AiAllocatorState / AllocationSession) live in ResourceAllocator.cs — the whole stage
    //     grew out of a stub into its own file (build-order step 5).

    // --- Stage 6 output (ProvisioningResult / ProvisionedMission / ProvisionFailure /
    //     ProvisioningSession) lives in ProvisioningManager.cs — the stage grew into its own file
    //     (build-order step 6a). ProvisionFailureKind / ProvisionDisposition live in
    //     ResourceAllocator.cs beside the AllocationSession that consumes them. ExecutionResult /
    //     ExecutionStopReason live in TaskExecutor.cs.

    // ===========================================================================================
    //  THE PIPELINE — walking skeleton. Every stage below is a stub that returns empty/neutral and
    //  mutates NOTHING. Toggling AiConfig.aiStrategyV2Enabled on right now yields an AI that logs
    //  one full pipeline pass and then passes its turn. That is the intended build-order step 1
    //  end state: full loop runs, zero tasks, no throw.
    // ===========================================================================================
    public static class Pipeline
    {
        internal static bool StrategicAdmissionNeeded(
            IReadOnlyDictionary<DesireAxis, string> lastHandled,
            DesireAxis axis, string fingerprint) => lastHandled == null
            || !lastHandled.TryGetValue(axis, out string previous)
            || previous != fingerprint;

        internal static bool RefreshDevelopmentOpportunities(ISet<DesireAxis> dirtyAxes) =>
            dirtyAxes != null && dirtyAxes.Contains(DesireAxis.Development);

        // AI-02 — AP no longer enters this fingerprint as a raw number. Development consults the
        // AP pool through exactly two affordability predicates, both of the form
        // `root.CanSpendActionPoints(x)` == `ActionPoints >= x`:
        //   * per offering: ResearchProductionSystem.AttemptApCost(card) + card.activationApCost
        //     (DevelopmentOpportunityEvaluator.Enumerate's Challenge+attach gate), and
        //   * per Unit card in hand: CardData.EffectivePlayApCost
        //     (BestAffordableHandUnitPower, which feeds AlternativeValue and therefore EV ordering).
        // Emitting which of those thresholds the current AP clears is therefore EXACTLY as
        // discriminating as the raw number, and an AP delta that crosses none of them provably
        // cannot change any Development decision. Resources are deliberately NOT narrowed: they
        // shift BestAffordableHandUnitPower's displaced-alternative term continuously, so any
        // resource change can reorder EV — correctness over savings, as required.
        internal static string DevelopmentAdmissionFingerprint(WorldSnapshot snapshot,
            IReadOnlyList<MissionIntent> activeIntents, int actionPoints,
            string resources, int handVersion, AiHandData hand = null) =>
            $"axis={DesireAxis.Development}|apfit={DevelopmentApAffordability(snapshot, hand, actionPoints)}"
            + $"|res={resources}"
            + $"|hand={handVersion}|{DevelopmentAdmissionFacts(snapshot, activeIntents)}";

        // The complete, ordered set of AP thresholds Development can cross (see above).
        internal static string DevelopmentApAffordability(WorldSnapshot snapshot, AiHandData hand,
            int actionPoints)
        {
            var thresholds = new List<int>();
            foreach (DevelopmentOffering off in snapshot?.Development?.Offerings
                         ?? (IReadOnlyList<DevelopmentOffering>)System.Array.Empty<DevelopmentOffering>())
                if (off.Card != null)
                    thresholds.Add(ResearchProductionSystem.AttemptApCost(off.Card)
                        + UnityEngine.Mathf.Max(0, off.Card.activationApCost));
            foreach (CardData c in hand?.Hand ?? (IReadOnlyList<CardData>)System.Array.Empty<CardData>())
                if (c?.Definition != null && c.Definition.cardType == CardType.Unit)
                    thresholds.Add(c.EffectivePlayApCost);
            if (thresholds.Count == 0)
                return $"raw:{actionPoints}";   // nothing enumerable — never guess, keep the raw fact
            return string.Join("", thresholds.Distinct().OrderBy(x => x)
                .Select(x => actionPoints >= x ? "1" : "0"));
        }

        // Development's actor dependency is narrower than the operational Actor invalidation.
        // Every army movement must still wake Recon/Aggression, but only a Researcher/Assembler
        // move can change operator delivery. Composition/capability remains represented for every
        // army because equipment recipient selection genuinely depends on it.
        internal static string DevelopmentAdmissionFacts(WorldSnapshot snapshot,
            IReadOnlyList<MissionIntent> activeIntents)
        {
            DevelopmentReadiness rd = snapshot?.Development;
            string facilities = string.Join(";", (rd?.Facilities
                    ?? System.Array.Empty<DevelopmentFacility>())
                .OrderBy(f => f.Hex.Q).ThenBy(f => f.Hex.R).ThenBy(f => (int)f.Mode)
                .Select(f => $"{f.Hex.Q},{f.Hex.R}:{(int)f.Mode}:{(f.HasHero ? 1 : 0)}:"
                    + $"{(f.Contested ? 1 : 0)}:{f.HeroFate}:{f.HeroCommandRating}"));
            string offerings = string.Join(";", (rd?.Offerings
                    ?? System.Array.Empty<DevelopmentOffering>())
                .OrderBy(o => o.FacilityHex.Q).ThenBy(o => o.FacilityHex.R)
                .ThenBy(o => (int)o.Mode)
                .ThenBy(o => o.Card?.authoredKey ?? o.Card?.displayName,
                    System.StringComparer.Ordinal)
                .Select(o => $"{o.FacilityHex.Q},{o.FacilityHex.R}:{(int)o.Mode}:"
                    + $"{o.Card?.authoredKey ?? o.Card?.displayName}:{o.SuccessChance:0.###}:"
                    + $"{(o.ProducesEquipment ? 1 : 0)}:{o.StakeCost.Human:0.###},"
                    + $"{o.StakeCost.Energy:0.###},{o.StakeCost.Materials:0.###},"
                    + $"{o.StakeCost.Tech:0.###}"));
            // AI-02 — only an army that can actually HOST a need-supporting equipment recipient
            // (AI-03's finalized dependency set) contributes composition/combat detail; every other
            // army contributes only its identity, so an unrelated army's stat change no longer
            // forces a full Development re-enumeration. The pre-existing operator-position
            // optimization is preserved verbatim for Research/Production operator armies.
            //
            // Composition and position are tracked SEPARATELY because they answer different proofs:
            // Economy's delivery proof (AI-03) compares an army's route and shared movement
            // bottleneck before/after a grant, so an Economy mover's position/movement/activation
            // has to invalidate Development. Raid's combat proof (WorthIt via
            // ImprovesRaidCombatOutcome) reads army.Members composition but never position. Recon's
            // proof (ImprovesReconCapability) reads only the OFFERED equipment/recipient card's own
            // static abilities/MoveMax/ActivationApCost — never anything about the scout army
            // itself — so a Scout-only mover needs neither block: a plain scout patrolling its
            // waypoint must not force a full re-enumeration on every step.
            var positionRelevantArmyIds = DevelopmentEconomyRelevantArmyIds(snapshot, activeIntents);
            var raidRelevantArmyIds = DevelopmentRaidRelevantArmyIds(activeIntents);
            string armies = string.Join(";", (snapshot?.Self?.Armies
                    ?? System.Array.Empty<ArmySnapshot>())
                .Where(a => a != null).OrderBy(a => a.ArmyId)
                .Select(a =>
                {
                    bool developmentOperator = a.HasResearchOperator || a.HasProductionOperator;
                    string operatorState = developmentOperator
                        ? $":operator={a.Hex.Q},{a.Hex.R}:{a.CurrentMovement}:"
                          + $"{(a.HasActivatedThisTurn ? 1 : 0)}:{(a.IsGarrison ? 1 : 0)}"
                        : string.Empty;
                    // Operator armies keep exactly the pre-existing full block (composition +
                    // position) regardless of the narrowed sets below — that optimization is
                    // untouched by this pass.
                    bool wantsPosition = developmentOperator || positionRelevantArmyIds.Contains(a.ArmyId);
                    bool wantsComposition = wantsPosition || raidRelevantArmyIds.Contains(a.ArmyId);
                    if (!wantsComposition && !wantsPosition)
                        return a.ArmyId.ToString(CultureInfo.InvariantCulture);
                    // FIX-04 — the ATTACKING side of the same proof needs the same precision as
                    // the defending side: ImprovesRaidCombatOutcome builds `before`/`after` from
                    // this army's individual non-hero members (WorthIt.FromLiveUnit each) and
                    // replaces exactly one of them with the equipped projection. Aggregates alone
                    // (MemberCount/AttackSum/DefenseSum/power/quality) cannot distinguish two
                    // rosters whose per-unit coverage, abilities or initiative order differ, so a
                    // relevant recipient-side change could go unnoticed. `a.Members` is already the
                    // non-hero profile list this proof iterates — the same canonical serialization
                    // as the defender side, no second representation.
                    // StrategicCoverage now contributes its lossless bitmask instead of
                    // GetHashCode(): a long-lived key must never rest on a hash.
                    string composition = wantsComposition
                        ? $":{a.MemberCount}:{(a.HasHero ? 1 : 0)}:"
                          + $"{a.AttackSum:0.###}:{a.DefenseSum:0.###}:"
                          + $"{a.EffectiveArmyPower:0.###}:{a.CompositionQuality:0.###}:"
                          + $"{a.Capacity}:{a.OccupiedBattleSlots}:{a.StrategicCoverage.Bits}:"
                          + $"{(a.HasResearchOperator ? 1 : 0)}:{(a.HasProductionOperator ? 1 : 0)}:"
                          + $"roster={DefenderFingerprint(a.Members)}"
                        : string.Empty;
                    // Position/movement/activation are part of the recipient facts now: AI-03's
                    // delivery proof compares this army's route and shared movement bottleneck
                    // before/after the grant, so they can change the admission answer.
                    string position = wantsPosition
                        ? $":at={a.Hex.Q},{a.Hex.R}:{a.CurrentMovement}:{a.MaxMovement}:"
                          + $"{a.ActivationApCost}:{(a.HasActivatedThisTurn ? 1 : 0)}:"
                          + $"{a.CollectionCapacity.Human:0.###},{a.CollectionCapacity.Energy:0.###},"
                          + $"{a.CollectionCapacity.Materials:0.###},{a.CollectionCapacity.Tech:0.###}"
                        : string.Empty;
                    return $"{a.ArmyId}{composition}{position}{operatorState}";
                }));
            string bases = string.Join(";", (snapshot?.Self?.BaseHexes
                    ?? System.Array.Empty<Game.HexGrid.HexCoord>())
                .OrderBy(h => h.Q).ThenBy(h => h.R).Select(h => $"{h.Q},{h.R}"));
            // AI-02 — the old field was `{IntentKey}:{Kind}:{Status}:{PreferredMoverArmyId}` for
            // EVERY intent, so any unrelated mission retargeting (a new IntentKey for the same
            // work), retiring or being created rewrote it and forced a full re-enumeration — the
            // single biggest source of the 571/88 repeated NO-recipient passes. Development reads
            // intents through exactly two channels, and each now contributes only its own facts:
            //
            //  (a) ACTOR OCCUPANCY. DevelopmentOpportunityEvaluator.EnumeratePreparation builds
            //      ActorCommitments.FromIntents over ALL intents, so every kind still has to be
            //      represented — but only through the inputs that produce a claim (kind, status
            //      and the claimed actor ids), never through intent identity. Two different intent
            //      keys that occupy the same actors are, to Development, the same world.
            string claims = string.Join(";", (activeIntents ?? new List<MissionIntent>())
                .Where(i => i != null)
                .SelectMany(i =>
                {
                    var rows = new List<string>();
                    string k = $"{i.Kind}:{i.Status}";
                    if (i.PreferredMoverArmyId.HasValue)
                        rows.Add($"{k}:{i.PreferredMoverArmyId.Value}");
                    if (i.Raid?.SupportArmyId != null)
                        rows.Add($"{k}:sup{i.Raid.SupportArmyId.Value}:{(int)i.Raid.Phase}");
                    if (i.Raid?.AirSupportArmyId != null)
                        rows.Add($"{k}:air{i.Raid.AirSupportArmyId.Value}:{(int)i.Raid.Phase}");
                    if (i.Economy?.BuilderArmyId != null)
                        rows.Add($"{k}:bld{i.Economy.BuilderArmyId.Value}");
                    if (i.Economy?.CollectorArmyId != null)
                        rows.Add($"{k}:col{i.Economy.CollectorArmyId.Value}");
                    if (i.Development?.Hero != null)
                        rows.Add($"{k}:dev{i.Development.HeroKey}");
                    if (rows.Count == 0)
                        rows.Add(k);
                    return rows;
                })
                .Distinct().OrderBy(x => x, System.StringComparer.Ordinal));
            //  (b) SUPPORTED NEED. Only an Economy obligation, a Scout mover or an
            //      Assault/Reinforcement Raid primary can witness a need
            //      (DemandLayer.HasSupportedDevelopmentAxisDemand), and each only through the
            //      fields the proof actually reads.
            string owners = string.Join(";", (activeIntents ?? new List<MissionIntent>())
                .Where(DevelopmentRelevantIntent)
                .Select(i =>
                {
                    string extra = string.Empty;
                    if (i.Kind == MissionKind.Economy && i.Economy != null)
                        // The specific economic obligation AI-03 proves against.
                        extra = $":{(int)i.Economy.Kind}:{i.Economy.TargetHex.Q},{i.Economy.TargetHex.R}"
                            + $":{(i.Economy.ResourceType.HasValue ? ((int)i.Economy.ResourceType.Value).ToString(CultureInfo.InvariantCulture) : "-")}"
                            + $":{i.Economy.BuilderArmyId}:{i.Economy.CollectorArmyId}"
                            + $":{(i.Economy.SafeReturnHex.HasValue ? $"{i.Economy.SafeReturnHex.Value.Q},{i.Economy.SafeReturnHex.Value.R}" : "-")}";
                    else if (i.Kind == MissionKind.Raid && i.Raid != null)
                        // The exact target/combat-viability feed into ImprovesRaidCombatOutcome.
                        // A Scout intent contributes nothing beyond its mover: ImprovesReconCapability
                        // reads only the recipient's own abilities, never the scout's target.
                        extra = $":{(int)i.Raid.Phase}:{i.Raid.PrimaryArmyId}"
                            + $":{i.Raid.LastKnownHex.Q},{i.Raid.LastKnownHex.R}"
                            + $":{DefenderFingerprint(AiV2Util.KnownDefenders(snapshot, i.Raid.Target))}";
                    return $"{i.Kind}:{i.PreferredMoverArmyId}{extra}";
                })
                .Distinct().OrderBy(x => x, System.StringComparer.Ordinal));
            // AI-03's economy admission reads the site's own income physics (extraction proof) and
            // the remembered sightings on the mission's route (protection proof), so those facts
            // must invalidate Development too — they did not before, which was a staleness bug in
            // the other direction. Own-force and remembered-sighting facts only; nothing hidden.
            string econ = "|sites=" + string.Join(";", (snapshot?.Economy?.CollectorSites
                    ?? System.Array.Empty<EconomyExtractionOpportunity>())
                .OrderBy(x => x.Hex.Q).ThenBy(x => x.Hex.R).ThenBy(x => (int)x.ResourceType)
                .Select(x => $"{x.Hex.Q},{x.Hex.R}:{(int)x.ResourceType}:{x.EffectiveYield}:"
                    + $"{x.CurrentBuildingCollection}:{x.MarginalIncomeGain}"))
                + "|threats=" + string.Join(";", (snapshot?.Known?.EnemySightings
                        ?? System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                    .Concat(snapshot?.Known?.NeutralSightings
                        ?? System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                    .OrderBy(x => x.Hex.Q).ThenBy(x => x.Hex.R).ThenBy(x => x.ArmyId)
                    .Select(x => $"{x.Hex.Q},{x.Hex.R}:{DefenderFingerprint(x.Defenders)}"))
                // FIX-05 — both combat proofs now take their hexBonus from remembered building
                // defence (AiMapMemory.KnownHexDefenseBonus), so a re-observed Base appearing,
                // being upgraded, changing owner or being razed genuinely changes the cached
                // answer and must invalidate it. Knowledge only: these are this player's own
                // observations, never a live BuildingRegistry sweep.
                + "|knownbases=" + string.Join(";", (snapshot?.Known?.Buildings
                        ?? System.Array.Empty<AiMapMemory.KnownBuilding>())
                    .Where(b => b.IsBase)
                    .OrderBy(b => b.Hex.Q).ThenBy(b => b.Hex.R)
                    .Select(b => $"{b.Hex.Q},{b.Hex.R}:{b.Defense:0.###}"));
            return $"fac={facilities}|off={offerings}|bases={bases}|armies={armies}|claims={claims}|owners={owners}{econ}"
                + $"|ready={(rd?.AnyFacilityWithHero == true ? 1 : 0)}:"
                + $"{(rd?.AnyOperatorlessFacility == true ? 1 : 0)}:"
                + $"{(rd?.ResearcherCardInHand == true ? 1 : 0)}:"
                + $"{(rd?.AssemblerCardInHand == true ? 1 : 0)}:"
                + $"{(rd?.DevPathViable == true ? 1 : 0)}:{rd?.UpgradeTargetCount ?? 0}:"
                + $"{(rd?.BestSuccessChance ?? 0f):0.###}:"
                + $"{(rd?.SurplusFraction ?? 0f):0.###}:"
                + $"{(rd?.ProductionSupport ?? 0f):0.###}";
        }

        // AI-02/AI-03 — the ONLY intents a Development decision can depend on:
        // DemandLayer.HasSupportedDevelopmentAxisDemand witnesses a need through an Economy
        // obligation, a Scout mover, or an Assault/Reinforcement Raid primary. Anything else
        // (a Return leg of someone else's raid, an ActiveDefence, a Development intent of its own)
        // cannot change whether an equipment offering is admitted.
        internal static bool DevelopmentRelevantIntent(MissionIntent i)
        {
            if (i == null || i.Status != IntentStatus.Active)
                return false;
            switch (i.Kind)
            {
                case MissionKind.Economy:
                    return i.Economy != null;
                case MissionKind.Scout:
                    return true;
                case MissionKind.Raid:
                    return i.Raid != null
                        && (i.Raid.Phase == RaidMissionPhase.Assault
                            || i.Raid.Phase == RaidMissionPhase.Reinforcement);
                default:
                    return false;
            }
        }

        // Armies whose POSITION/movement/activation can change a Development decision: only
        // Economy's delivery proof (AI-03) compares an army's route and shared movement bottleneck
        // before/after a grant. Includes every army the Economy analysis already advertises as a
        // possible builder/collector — those become the EconomyPreferredBuilderArmyId witness a
        // fresh Economy demand carries, and protection/delivery proofs may run against them too.
        // Operator armies are handled separately by the caller (pre-existing optimization).
        internal static HashSet<int> DevelopmentEconomyRelevantArmyIds(WorldSnapshot snapshot,
            IReadOnlyList<MissionIntent> activeIntents)
        {
            var ids = new HashSet<int>();
            foreach (MissionIntent i in activeIntents ?? new List<MissionIntent>())
            {
                if (i == null || i.Status != IntentStatus.Active || i.Kind != MissionKind.Economy
                    || i.Economy == null)
                    continue;
                if (i.PreferredMoverArmyId.HasValue) ids.Add(i.PreferredMoverArmyId.Value);
                if (i.Economy.BuilderArmyId != null) ids.Add(i.Economy.BuilderArmyId.Value);
                if (i.Economy.CollectorArmyId != null) ids.Add(i.Economy.CollectorArmyId.Value);
            }
            EconomyStanding eco = snapshot?.Economy;
            if (eco != null)
            {
                void AddRoutes(IReadOnlyList<EconomyBuilderRouteSnapshot> routes)
                {
                    foreach (EconomyBuilderRouteSnapshot r in routes
                                 ?? System.Array.Empty<EconomyBuilderRouteSnapshot>())
                        ids.Add(r.ArmyId);
                }
                foreach (EconomyExtractionOpportunity x in eco.ExtractionOpportunities
                             ?? System.Array.Empty<EconomyExtractionOpportunity>())
                    AddRoutes(x.BuilderRoutes);
                foreach (EconomyExtractionOpportunity x in eco.CollectorSites
                             ?? System.Array.Empty<EconomyExtractionOpportunity>())
                    AddRoutes(x.BuilderRoutes);
                foreach (EconomyBaseOpportunity x in eco.BaseOpportunities
                             ?? System.Array.Empty<EconomyBaseOpportunity>())
                    AddRoutes(x.BuilderRoutes);
                foreach (MobileCollectionOpportunity x in eco.MobileCollectionOpportunities
                             ?? System.Array.Empty<MobileCollectionOpportunity>())
                    ids.Add(x.CollectorArmyId);
            }
            return ids;
        }

        // Armies whose COMPOSITION (beyond Economy's — see DevelopmentEconomyRelevantArmyIds
        // above, unioned in by the caller) can change a Development decision: only Raid's combat
        // proof (ImprovesRaidCombatOutcome via WorthIt) reads army.Members. Recon's proof
        // (ImprovesReconCapability) reads only the offered equipment/recipient card's own static
        // abilities/MoveMax/ActivationApCost — never anything about the scout army itself — so a
        // Scout-only mover contributes nothing here, and a plain scout stepping its waypoint no
        // longer forces a full Development re-enumeration.
        internal static HashSet<int> DevelopmentRaidRelevantArmyIds(
            IReadOnlyList<MissionIntent> activeIntents)
        {
            var ids = new HashSet<int>();
            foreach (MissionIntent i in activeIntents ?? new List<MissionIntent>())
            {
                if (i == null || i.Status != IntentStatus.Active || i.Kind != MissionKind.Raid
                    || i.Raid == null
                    || (i.Raid.Phase != RaidMissionPhase.Assault
                        && i.Raid.Phase != RaidMissionPhase.Reinforcement))
                    continue;
                if (i.Raid.PrimaryArmyId != null) ids.Add(i.Raid.PrimaryArmyId.Value);
            }
            return ids;
        }

        // FIX-04 — EXACT, order-stable digest of a combat roster. It used to be Count plus four
        // SUMS (Attack/Defense/HP/Initiative), which is strictly weaker than what the cached
        // decision actually depends on: DemandLayer.Development.ImprovesRaidCombatOutcome runs
        // WorthIt.CanDamageAll and WorthIt.Estimate, and those read each profile INDIVIDUALLY —
        // per-defender Defense, CeramicArmor, ability list, unit type tags, current AND max HP, and
        // Initiative (which sets the turn order, not a sum). Two genuinely different rosters can
        // therefore share every aggregate while giving different WorthIt answers (the canonical
        // example: one defender carrying CeramicArmor instead of none — identical sums, different
        // coverage verdict), so the fingerprint failed to invalidate a decision that had changed.
        //
        // Every field WorthIt reads is emitted verbatim; nothing is hashed (a long-lived key must
        // not be built on GetHashCode) and no new cache is introduced — this is the SAME key the
        // pipeline already kept, simply made complete. Per-profile rows are sorted ordinally so a
        // pure REORDERING of the same roster keeps the same key: WorthIt orders combat by
        // Initiative, never by list position, so order carries no information here.
        private static string DefenderFingerprint(IReadOnlyList<Game.Combat.WorthIt.DefenderProfile> defenders)
        {
            if (defenders == null || defenders.Count == 0)
                return "0";
            var rows = new List<string>(defenders.Count);
            foreach (Game.Combat.WorthIt.DefenderProfile d in defenders)
            {
                string tags = d.TypeTags == null ? string.Empty
                    : string.Join(",", d.TypeTags.Select(t => ((int)t).ToString(CultureInfo.InvariantCulture))
                        .OrderBy(t => t, System.StringComparer.Ordinal));
                string abilities = d.Abilities == null ? string.Empty
                    : string.Join(",", d.Abilities.Where(x => x != null)
                        .OrderBy(x => x, System.StringComparer.Ordinal));
                rows.Add($"{d.Attack:0.###}/{d.Defense:0.###}/{d.HitPoints:0.###}/"
                    + $"{d.MaxHitPoints:0.###}/{d.Initiative}/{(d.HasCeramicArmor ? 1 : 0)}/"
                    + $"[{tags}]/[{abilities}]");
            }
            rows.Sort(System.StringComparer.Ordinal);
            return $"{defenders.Count}:" + string.Join("|", rows);
        }

        public static IEnumerator RunTurn(PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx)
        {
            AiDebugLog.Write($"[AI][V2] === {player?.Nickname} — Strategy V2 pipeline owns this turn "
                + $"(turn {ctx?.TurnNumber}) ===");

            if (player == null || root == null || ctx == null || ctx.Map == null)
            {
                AiDebugLog.Write("[AI][V2] missing player/root/ctx/map — nothing to do.");
                yield break;
            }

            // Correlation scope for this whole main pass (T{turn}-P{colorIndex}-M) + the physical
            // resource totals it opens with, for the end-of-Main [STATE] control line (spec §2.7).
            V2TraceScope trace = AiV2Trace.BeginMain(player, ctx.TurnNumber);
            V2ResourceStamp stateStart = AiV2Trace.Stamp(root);

            // Turn-scoped activity record (main vs reaction vs total). Reset here so a stale
            // Reaction bucket from last turn can never leak into this turn's Total.
            V2TurnActivityTelemetry.Begin(player, ctx.TurnNumber);
            CapabilityPoolExhaustionRegistry.BeginTurn(player, ctx.TurnNumber);
            // AI-MGR-02 §4 — fresh explicit strategic resource reservations for this turn.
            StrategicResourceReservationLedger.BeginTurn(player, ctx.TurnNumber);

            // Initiative AP telemetry — captured now (turn start) and written back at turn end.
            // Belongs EXCLUSIVELY to Game.Ai.V2.Initiative analysis; nothing else in this pipeline
            // reads it (see InitiativeAnalyticsHistory).
            int initiativeStartAp = root.ActionPoints;
            int initiativeBaseAp = root.LastApFromInitiative;
            int initiativeActionableAtStart =
                Game.Ai.V2.Initiative.PreTurnCapacityAnalysis.CountActionableFieldArmies(player, unactivatedOnly: false);

            // 2. One shared scan.
            WorldSnapshot snapshot = WorldAnalysis.Scan(player, root, hand, ctx);
            AiFrameLog.GameState(snapshot, hand);
            AiFrameLog.WorldAnalysis(snapshot);

            // 3. Strategy: independent raw desires -> normalize once -> radar. StrategyLayer writes
            //    its own detailed "[AI][V2]   desires — ..." trace; the line below is the summary.
            AiRadarState radarState = AiRadarStateRegistry.GetOrCreate(player);
            RadarAssessment assessment = StrategyLayer.Evaluate(snapshot, radarState);
            assessment = AiStrategyV2Scope.ApplyRadarScope(assessment);
            DesireVector desires = assessment.Desires;
            Radar radar = assessment.Radar;
            AiDebugLog.Write($"[AI][V2] {player.Nickname}: radar — {radar.DebugLine()} "
                + $"| threat {desires.MilitaryThreat.ToString("0.00", CultureInfo.InvariantCulture)} "
                + $"runway {desires.EconomicRunway.ToString("0.00", CultureInfo.InvariantCulture)}");
            AiFrameLog.Strategy(assessment);

            // 3c. The ONE Recon-opportunity enumeration for the turn — shared by DemandLayer and
            //     ReconMissionPlanner. FROZEN here (before StrategicManager touches own forces): Strategic
            //     Manager changes which SCOUT can execute, never which objectives exist.
            List<ReconObjective> reconObjectives = ReconObjectiveEvaluator.Enumerate(snapshot);

            // 3d. The ONE Aggression-opportunity enumeration for the turn — shared by DemandLayer
            //     and AggressionMissionLayer (build-order step 9). A focus scope that drops the
            //     Aggression axis (ReconOnly, ReconDevelopment) deliberately keeps the layer present
            //     but does not enumerate or execute it.
            List<AggressionObjective> aggressionObjectives = AiStrategyV2Scope.AxisInScope(DesireAxis.Aggression)
                ? AggressionObjectiveEvaluator.Enumerate(snapshot, assessment.Breakdown.OpportunityReport)
                : new List<AggressionObjective>();
            // 3e. 2026-09-21 Block D — Development opportunities are NO LONGER enumerated here.
            //     Enumerate/BestEquipmentOpportunity keeps only ONE recipient per offering, so
            //     picking that recipient before any demand or durable intent exists silently threw
            //     away every other legal recipient: DemandLayer.Development then re-checked the
            //     surviving recipient against the real need, found none, and dropped the whole
            //     opportunity — although another recipient DID close a real need. Selection now
            //     happens in DemandLayer.Development, the one place that holds the current
            //     supported-need context, using the supportsNeed predicate Enumerate already takes.
            //     (This also removes four hand-rolled copies of that predicate from this file.)

            foreach (AggressionObjective ao in aggressionObjectives)
                AiDebugLog.Write($"[AI][V2]   aggObjective — {ao.ObjectiveId} @{ao.LastKnownHex.Q},{ao.LastKnownHex.R} "
                    + $"base {ao.BaseValue.ToString("0.0", CultureInfo.InvariantCulture)} "
                    + $"readyWin {ao.ReadyWinChance.ToString("0.00", CultureInfo.InvariantCulture)} "
                    + $"asmWin {ao.AssemblableWinChance.ToString("0.00", CultureInfo.InvariantCulture)} "
                    + $"def {ao.DefenderCount} gate {(ao.GatePassed ? 1 : 0)}"
                    + $"{(ao.NeedsCombatPower ? " needsPower" : "")}{(ao.NeedsHero ? " needsHero" : "")}");
            AiFrameLog.Objectives(reconObjectives, aggressionObjectives);

            // 7a. Mission Continuity — resolve the durable in-flight intents FIRST, then apply the
            //     centralized execution scope. In ReconOnly this cleanly retires stale Raid intents
            //     before ActorCommitments or the allocator can protect them.
            List<MissionIntent> activeIntents = MissionContinuityLayer.ResolveActive(
                player, snapshot, reconObjectives, aggressionObjectives, ctx);
            activeIntents = AiStrategyV2Scope.ApplyIntentScope(player, activeIntents);
            // Normalized "which of my armies are already committed to an operation" view — so
            // DemandLayer / CapabilityInventory / ReusableArmySelector can tell an EXISTING scout
            // from an AVAILABLE one without knowing how continuity stores mover ownership.
            ActorCommitments actorCommitments = ActorCommitments.FromIntents(activeIntents, snapshot, reconObjectives);
            AiFrameLog.MissionContinuity(activeIntents, actorCommitments);

            // RECON-AIR-02 (round 5) — the old separate Recon Air Reservation Prepass stage is
            //     gone: DemandLayer now measures air capacity itself via
            //     ReconAssignmentPlanner.MeasureAirCapacity (the same canonical capacity owner
            //     ground already uses), recomputed fresh every call — no cross-call registry.

            // S1. Demand Layer — capability SHORTAGES (no card selection). Pass the centralized
            //     scope into the owner itself so suppressed axes do not even emit demand telemetry.
            var scopedDemandAxes = new HashSet<DesireAxis>(AiStrategyV2Scope.AxesInScope);
            List<AxisDemand> demands = DemandLayer.Generate(snapshot, assessment.Breakdown,
                reconObjectives, aggressionObjectives, activeIntents, actorCommitments, player, ctx, root,
                null, scopedDemandAxes);
            demands = AiStrategyV2Scope.ApplyDemandScope(demands);

            // S2. The ONE per-turn AP pool: allocatable AP (real AP minus the
            //     HousekeepingManager reserve). Radar scales objective value only; Strategic Manager
            //     Phase A and the mission allocator spend the same scalar ledger. Round 3 — no recon-air AP carve-out any
            //     more: Recon Air no longer gets a pre-funding reservation Phase A can't touch.
            AxisBudgetLedger apLedger = AxisBudgetLedger.Create(
                UnityEngine.Mathf.Max(0f, snapshot.Self?.ActionPoints ?? 0));
            AiDebugLog.Write($"[AI][V2] {player.Nickname}: budget ledger — {apLedger.DebugLine()}");

            // S3. Strategic Manager Phase A — demand-driven card play, before mission planning.
            //     In ReconOnly the filtered demand set can materialize only capability requested by Recon.
            int handAtStart = hand?.Hand?.Count ?? 0;
            StrategicPhaseResult phaseA = StrategicManager.FulfillDemands(snapshot, player, root, hand,
                ctx, apLedger, demands, actorCommitments, activeIntents, reconObjectives,
                radar: radar, deferFreshZeroRadar: true);

            // S4. Analysis owns refresh granularity. The existing AiMapMemory revision decides
            //     whether honest knowledge/map facts changed; action kind is not used as a proxy.
            //     Radar remains fixed while operational Aggression facts refresh below.
            if (phaseA.StateChanged)
            {
                snapshot = WorldAnalysis.RefreshStrategicKnowledge(
                    snapshot, player, root, hand, ctx);
                reconObjectives = ReconObjectiveEvaluator.Enumerate(snapshot);
                if (AiStrategyV2Scope.AxisInScope(DesireAxis.Aggression))
                    StrategyLayer.RefreshAggressionLanePressures(snapshot, assessment.Breakdown);
                aggressionObjectives = AiStrategyV2Scope.AxisInScope(DesireAxis.Aggression)
                    ? AggressionObjectiveEvaluator.Enumerate(
                        snapshot, assessment.Breakdown.OpportunityReport)
                    : new List<AggressionObjective>();
                // Direct Economy construction can atomically turn the builder's existing intent
                // into ReturnBuilder (or resume a safe scout). Re-read the same continuity owner
                // before mission construction so stale pre-build actor claims cannot execute.
                activeIntents = MissionContinuityLayer.ResolveActive(
                    player, snapshot, reconObjectives, aggressionObjectives);
                activeIntents = AiStrategyV2Scope.ApplyIntentScope(player, activeIntents);
                actorCommitments = ActorCommitments.FromIntents(
                    activeIntents, snapshot, reconObjectives);
                // Phase A changed the settled facts behind the initial demand frame. Refresh that
                // frame once here; the first operational admission consumes it without another
                // full Generate call.
                // Block D — this call regenerates every axis in scope, so DemandLayer.Development
                // builds its own opportunities against this pass's complete need context.
                demands = DemandLayer.Generate(snapshot, assessment.Breakdown,
                    reconObjectives, aggressionObjectives, activeIntents, actorCommitments,
                    player, ctx, root, null, scopedDemandAxes);
                demands = AiStrategyV2Scope.ApplyDemandScope(demands);
            }

            List<MissionProposal> missions;
            TentativeAllocation allocation = new TentativeAllocation();
            var fundedKeysThisTurn = new HashSet<StableMissionKey>();
            var provisioned = new List<ProvisionedMission>();
            var provisioningFailures = new Dictionary<ProvisionFailureKind, int>();
            var allExecuted = new List<ExecutionResult>();
            var phaseB = new StrategicPhaseResult();
            ActorCommitments postCommitments = null;
            bool phaseBHandled = false;

            // The typed mid-turn architecture is canonical for every runtime scope. The
            // initial Phase A settles before operational admission; a later factual Development
            // invalidation may re-enter that same manager through the shared ledger. Each Recon
            // admission still settles exactly one task command. Full therefore uses the same
            // bounded settle -> observe -> typed re-admission path as focused diagnostics.
            if (AiStrategyV2Scope.UsesTypedLoop)
            {
                missions = new List<MissionProposal>();
                int settledSteps = 0;
                int noProgressCycles = 0;
                var lastStrategicAdmissionFingerprint = new Dictionary<DesireAxis, string>();
                bool ownershipFreshAfterPhaseA = phaseA.StateChanged;
                bool zeroRadarResidualWindow = false;

                string StrategicAdmissionFingerprint(DesireAxis axis)
                {
                    string resources = root == null ? "-" : string.Join(",",
                        ResourceBundle.All.Select(t => root.GetResource(t).ToString("0.###",
                            CultureInfo.InvariantCulture)));
                    if (axis == DesireAxis.Development)
                        return DevelopmentAdmissionFingerprint(snapshot, activeIntents,
                            root?.ActionPoints ?? 0, resources, hand?.MutationVersion ?? -1, hand);
                    string armies = string.Join(";", (snapshot?.Self?.Armies
                            ?? System.Array.Empty<ArmySnapshot>())
                        .Where(a => a != null).OrderBy(a => a.ArmyId)
                        .Select(a => $"{a.ArmyId}:{a.Hex.Q},{a.Hex.R}:{a.MemberCount}:"
                            + $"{a.CurrentMovement}:{a.ActivationApCost}:{(a.HasHero ? 1 : 0)}"));
                    // FIX-06 — the fingerprint's site facts are now produced by the SAME
                    // WorldAnalysis.EconomyOpportunityRows the typed invalidation is derived
                    // from. Previously it carried only ExtractionOpportunities' MarginalIncomeGain
                    // — no CollectorSites, no MobileCollectionOpportunities, no actor-availability
                    // — so a genuine "this known site became usable" event could be published and
                    // then immediately suppressed here on an unchanged key. One producer, so the
                    // trigger and the admission gate can no longer describe different worlds.
                    string economyFacts = axis == DesireAxis.Economy
                        ? "|sites=" + string.Join(";", WorldAnalysis.EconomyOpportunityRows(snapshot)
                            .OrderBy(kv => kv.Key, System.StringComparer.Ordinal)
                            .Select(kv => $"{kv.Key}={kv.Value}"))
                          + "|bases=" + string.Join(";", (snapshot?.Economy?.BaseOpportunities
                                ?? System.Array.Empty<EconomyBaseOpportunity>())
                            .OrderBy(x => x.Hex.Q).ThenBy(x => x.Hex.R)
                            .Select(x => $"{x.Hex.Q},{x.Hex.R}:{x.HexYield.Sum:0.###}"))
                          + "|threats=" + string.Join(";", (snapshot?.Known?.EnemySightings
                                ?? System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                            .Concat(snapshot?.Known?.NeutralSightings
                                ?? System.Array.Empty<AiMapMemory.KnownEnemySighting>())
                            .OrderBy(x => x.Hex.Q).ThenBy(x => x.Hex.R)
                            .Select(x => $"{x.Hex.Q},{x.Hex.R}:{x.SeenTurn}:{x.Defenders?.Count ?? 0}"))
                          + "|owners=" + string.Join(";", (activeIntents
                                ?? new List<MissionIntent>())
                            .Where(i => i?.Kind == MissionKind.Economy)
                            .OrderBy(i => i.IntentKey)
                            .Select(i => $"{i.IntentKey}:{i.Status}:{i.PreferredMoverArmyId}"))
                        : string.Empty;
                    return $"axis={axis}|ap={root?.ActionPoints ?? 0}"
                        + $"|res={resources}|hand={hand?.MutationVersion ?? -1}"
                        + $"|v={V2StateVersion.Current}|armies={armies}"
                        + economyFacts;
                }

                foreach (DesireAxis axis in scopedDemandAxes.Where(a =>
                             a == DesireAxis.Economy || a == DesireAxis.Development))
                    lastStrategicAdmissionFingerprint[axis] =
                        StrategicAdmissionFingerprint(axis);

                // One factual flag may invalidate more than one family (for example, discovering
                // a deficient ResourceSite changes both Recon knowledge and Development
                // opportunity). Snapshot the aggregate once, derive every in-scope family, and
                // only then consume the shared reasons so family order cannot erase a sibling's
                // trigger.
                bool EconomyBuilderReadyForCompletion()
                {
                    foreach (MissionIntent intent in activeIntents ?? new List<MissionIntent>())
                    {
                        if (intent?.Kind != MissionKind.Economy
                            || intent.Status != IntentStatus.Active
                            || intent.Economy == null
                            || !intent.PreferredMoverArmyId.HasValue)
                            continue;
                        ArmySnapshot actor = snapshot?.Self?.Armies?.FirstOrDefault(a => a != null
                            && a.ArmyId == intent.PreferredMoverArmyId.Value);
                        if (actor == null || !actor.Hex.Equals(intent.Economy.TargetHex))
                            continue;
                        // A ReturnBuilder that just reached home is about to be retired by the next
                        // MissionContinuityLayer.ResolveActive pass, freeing its builder for a new
                        // Economy demand this same turn — that is exactly as actionable as a builder
                        // arriving at a fresh build hex.
                        return true;
                    }
                    return false;
                }

                void TakeTypedTriggers(out StrategicInvalidationReason operationalReasons,
                    out StrategicInvalidationReason strategicReasons,
                    out HashSet<DesireAxis> dirtyStrategicAxes)
                {
                    StrategicInvalidation pending =
                        StrategicInterruptRegistry.Peek(player, ctx.TurnNumber);
                    // AGG-RAID §12/P1#2 — the OPERATIONAL mask is built from EVERY currently-enabled
                    // mission axis, not only Recon. Without Aggression here, destroying a neutral
                    // published a Contact invalidation that nothing consumed, so the bounded loop
                    // never got a same-turn chance to refresh the objective list, complete the old
                    // target, select the next one, or start a Return mission. Active Defence is
                    // folded into Aggression (no separate axis/mission), so this mask needs no
                    // extra case for it.
                    StrategicInvalidationReason operationalMask =
                        AiStrategyV2Scope.OperationalInvalidationMask;
                    operationalReasons = pending.Reasons & operationalMask;
                    strategicReasons = StrategicInvalidationReason.None;
                    dirtyStrategicAxes = new HashSet<DesireAxis>();
                    foreach (DesireAxis axis in new[]
                             {
                                 DesireAxis.Economy, DesireAxis.Development,
                             })
                    {
                        if (!AiStrategyV2Scope.AxisInScope(axis))
                            continue;
                        StrategicInvalidationReason axisReasons = pending.Reasons
                            & DesireAxes.InvalidationMaskFor(axis);
                        // Actor movement alone must not re-run every Economy infrastructure
                        // candidate. It becomes actionable only when continuity's committed
                        // builder actually reached its build hex; factual resource/site changes
                        // still re-admit Economy normally.
                        if (axis == DesireAxis.Economy
                            && axisReasons == StrategicInvalidationReason.Actor
                            && !EconomyBuilderReadyForCompletion())
                            continue;
                        if (axisReasons == StrategicInvalidationReason.None)
                            continue;
                        dirtyStrategicAxes.Add(axis);
                        strategicReasons |= axisReasons;
                    }
                    StrategicInterruptRegistry.Consume(player, ctx.TurnNumber,
                        operationalReasons | strategicReasons);
                }

                // Typed strategic re-admission uses the existing Phase-A owner, shared AP ledger and
                // carried reservation. This is deliberately local orchestration, not a second
                // manager or a new vertical layer.
                bool ReenterStrategicAxes(StrategicInvalidationReason reasons,
                    HashSet<DesireAxis> dirtyAxes)
                {
                    if (reasons == StrategicInvalidationReason.None
                        || dirtyAxes == null || dirtyAxes.Count == 0)
                        return false;

                    dirtyAxes.RemoveWhere(axis =>
                    {
                        string fingerprint = StrategicAdmissionFingerprint(axis);
                        bool unchanged = !StrategicAdmissionNeeded(
                            lastStrategicAdmissionFingerprint, axis, fingerprint);
                        if (unchanged)
                            AiDebugLog.Write($"[AI][V2][Loop] strategic re-admission skipped "
                                + $"axis={axis} reason=settled_state_unchanged fingerprint={fingerprint}");
                        return unchanged;
                    });
                    if (dirtyAxes.Count == 0)
                        return false;
                    Dictionary<DesireAxis, string> admittedFingerprints = dirtyAxes
                        .ToDictionary(axis => axis, StrategicAdmissionFingerprint);

                    reconObjectives = ReconObjectiveEvaluator.Enumerate(snapshot);
                    if (AiStrategyV2Scope.AxisInScope(DesireAxis.Aggression))
                        StrategyLayer.RefreshAggressionLanePressures(snapshot, assessment.Breakdown);
                    aggressionObjectives = AiStrategyV2Scope.AxisInScope(DesireAxis.Aggression)
                        ? AggressionObjectiveEvaluator.Enumerate(
                            snapshot, assessment.Breakdown.OpportunityReport)
                        : new List<AggressionObjective>();
                    activeIntents = MissionContinuityLayer.ResolveActive(
                        player, snapshot, reconObjectives, aggressionObjectives);
                    activeIntents = AiStrategyV2Scope.ApplyIntentScope(player, activeIntents);
                    actorCommitments = ActorCommitments.FromIntents(
                        activeIntents, snapshot, reconObjectives);
                    // Block D — a PARTIAL re-evaluation does not regenerate the other axes, but
                    // their demands from the carrying pass are still valid need evidence. Hand
                    // them to DemandLayer as carried context so Development's recipient selection
                    // sees the same facts a full pass would, instead of an empty demand frame.
                    List<AxisDemand> carriedForDevelopment = demands.Where(d => d != null
                        && !dirtyAxes.Contains(d.RequestingAxis)).ToList();
                    List<AxisDemand> regenerated = DemandLayer.Generate(snapshot, assessment.Breakdown,
                        reconObjectives, aggressionObjectives, activeIntents,
                        actorCommitments, player, ctx, root, null,
                        dirtyAxes, carriedForDevelopment);
                    regenerated = AiStrategyV2Scope.ApplyDemandScope(regenerated);
                    List<AxisDemand> dirtyDemands = regenerated;
                    demands = demands.Where(d => d != null
                            && !dirtyAxes.Contains(d.RequestingAxis))
                        .Concat(regenerated).ToList();
                    // Economy deferred-hold reconciliation now lives entirely inside
                    // StrategicPhaseA (economyAxisAuthoritative) — a single canonical writer
                    // instead of this call duplicating the same existence check right before it.
                    // dirtyAxes.Contains(Economy) is the exact "was Economy actually re-evaluated
                    // this round" signal Phase A needs to tell "Economy resolved" apart from
                    // "Economy wasn't part of this dirty-axis subset".
                    WorldAnalysis.StepObservationStamp beforeCapabilities =
                        WorldAnalysis.CaptureStepObservation(root, hand, snapshot);
                    StrategicPhaseResult followup = StrategicManager.FulfillDemands(
                        snapshot, player, root, hand, ctx, apLedger, dirtyDemands,
                        actorCommitments, activeIntents, reconObjectives,
                        phaseB.Reservation ?? phaseA.Reservation,
                        economyAxisAuthoritative: dirtyAxes.Contains(DesireAxis.Economy), radar: radar,
                        deferFreshZeroRadar: true);
                    phaseA.Accumulate(followup);
                    if (followup.StateChanged)
                    {
                        snapshot = WorldAnalysis.RefreshStrategicKnowledge(
                            snapshot, player, root, hand, ctx);
                        reconObjectives = ReconObjectiveEvaluator.Enumerate(snapshot);
                        if (AiStrategyV2Scope.AxisInScope(DesireAxis.Aggression))
                            StrategyLayer.RefreshAggressionLanePressures(snapshot, assessment.Breakdown);
                        aggressionObjectives = AiStrategyV2Scope.AxisInScope(DesireAxis.Aggression)
                            ? AggressionObjectiveEvaluator.Enumerate(
                                snapshot, assessment.Breakdown.OpportunityReport)
                            : new List<AggressionObjective>();
                        activeIntents = MissionContinuityLayer.ResolveActive(
                            player, snapshot, reconObjectives, aggressionObjectives);
                        activeIntents = AiStrategyV2Scope.ApplyIntentScope(player, activeIntents);
                        actorCommitments = ActorCommitments.FromIntents(
                            activeIntents, snapshot, reconObjectives);
                        ownershipFreshAfterPhaseA = true;
                    }
                    WorldAnalysis.StepObservationStamp afterCapabilities =
                        WorldAnalysis.CaptureStepObservation(root, hand, snapshot);
                    WorldAnalysis.PublishStepObservationDelta(player, ctx.TurnNumber,
                        beforeCapabilities, afterCapabilities, null);
                    foreach (DesireAxis axis in dirtyAxes)
                        lastStrategicAdmissionFingerprint[axis] =
                            admittedFingerprints[axis];
                    AiDebugLog.Write($"[AI][V2][Loop] strategic re-admission "
                        + $"axes={string.Join(",", dirtyAxes)} triggers={reasons} "
                        + $"changed={(followup.StateChanged ? 1 : 0)}");
                    return followup.StateChanged;
                }

                IEnumerator RunTypedAdmissions()
                {
                    zeroRadarResidualWindow = false;
                    AiDebugLog.Write("[AI][V2][Loop] begin — typed operational admission");

                    // Scout jobs rejected with ProvisionDisposition.RetryNextTurn ("out of the
                    // running THIS turn" — ResourceAllocator.cs:172, covers MoverContended AND
                    // NoExecutableStep alike) carry that verdict, but nothing enforced it across
                    // settled steps: BuildMissionSet re-proposed the same losing job every
                    // micro-step, re-running the full batch solve only to reach the identical
                    // rejection again (same busy/unreachable movers, nothing changed). Originally
                    // this set only recorded MoverContended, so a NoExecutableStep rejection (a
                    // scout physically can't reach its target this turn) kept re-entering the
                    // batch solve every settled step for no reason — same churn, different kind.
                    // Recorded live as each RetryNextTurn failure is seen below (NOT by reading
                    // ProvisioningSession.AssignmentRejections after the step settles — a later
                    // intra-step realloc pass drops an already-rejected mission out of Funded
                    // entirely, and ProvisioningSession.SetAssignment clears+refills that dict on
                    // every pass, so by settle time it only ever held the last pass's leftovers,
                    // almost always empty). Consumed only at the NEXT settled step's BuildMissionSet
                    // filter below, so this step's own remaining realloc passes still see the full
                    // candidate set — the existing intra-step "chance within the batch" is untouched.
                    var retryNextTurnThisPass = new HashSet<StableMissionKey>();

                    // Perf: an AI turn can run dozens of settled steps back-to-back with no other
                    // yield in between (each step's own yields resolve synchronously — see the
                    // profiler frame that motivated this), so the whole turn used to land in one
                    // single-frame hitch (observed ~885ms / 15 FPS). Give a real frame back to the
                    // engine whenever the wall-clock budget since the last frame is exceeded, so the
                    // same total work is spread across several frames instead of freezing one.
                    // Total AI-turn wall-clock time goes UP by roughly one frame per yield — that
                    // tradeoff (smoother frame pacing over shorter total wait) is the project
                    // owner's explicit call, 2026-09-20.
                    const float yieldBudgetSeconds = 0.008f;
                    float lastYieldTime = UnityEngine.Time.realtimeSinceStartup;

                    while (settledSteps < AiConfigV2.maxMidTurnStepsPerTurn
                        && noProgressCycles < AiConfigV2.maxMidTurnNoProgressCycles)
                    {
                    if (UnityEngine.Time.realtimeSinceStartup - lastYieldTime >= yieldBudgetSeconds)
                    {
                        yield return null;
                        lastYieldTime = UnityEngine.Time.realtimeSinceStartup;
                    }
                    // Every admission reads a settled world. Strategic observations are refreshed
                    // here. The radar frame stays stable for this turn; typed Development facts
                    // re-enter the existing manager immediately after the settled task boundary.
                    if (!ownershipFreshAfterPhaseA)
                    {
                        snapshot = WorldAnalysis.RefreshStrategicKnowledge(snapshot, player, root, hand, ctx);
                        reconObjectives = ReconObjectiveEvaluator.Enumerate(snapshot);
                        // AGG-RAID §3/§12 — rebuild the operational Aggression facts from THIS
                        // settled snapshot before re-enumerating objectives, so a neutral destroyed
                        // during the previous step is gone from the report in the same turn.
                        if (AiStrategyV2Scope.AxisInScope(DesireAxis.Aggression))
                            StrategyLayer.RefreshAggressionLanePressures(
                                snapshot, assessment.Breakdown);
                        aggressionObjectives = AiStrategyV2Scope.AxisInScope(DesireAxis.Aggression)
                            ? AggressionObjectiveEvaluator.Enumerate(
                                snapshot, assessment.Breakdown.OpportunityReport)
                            : new List<AggressionObjective>();
                        activeIntents = MissionContinuityLayer.ResolveActive(
                            player, snapshot, reconObjectives, aggressionObjectives);
                        activeIntents = AiStrategyV2Scope.ApplyIntentScope(player, activeIntents);
                        actorCommitments = ActorCommitments.FromIntents(
                            activeIntents, snapshot, reconObjectives);
                    }
                    ownershipFreshAfterPhaseA = false;
                    // Demand families persist across settled admissions. Only
                    // ReenterStrategicAxes replaces dirty families after a factual invalidation.

                    missions = BuildMissionSet(snapshot, assessment.Breakdown, activeIntents,
                        reconObjectives, aggressionObjectives, radar, demands, trace, ctx,
                        aggressionPressureAlreadyRefreshed: true);
                    if (retryNextTurnThisPass.Count > 0)
                        missions = missions.Where(m => m == null
                            || !retryNextTurnThisPass.Contains(StableMissionKey.For(m))).ToList();
                    List<Commitment> cycleCommitments =
                        MissionContinuityLayer.BindFunding(activeIntents, missions);
                    var cycleLedger = new MissionOutcomeLedger();
                    cycleLedger.RegisterProposals(missions);
                    cycleLedger.RegisterCommitments(cycleCommitments);

                    AllocationSession cycleSession = ResourceAllocator.BeginTurn(snapshot, radar,
                        missions, cycleCommitments, player, apLedger);
                    var cycleProvisioning = new ProvisioningSession(snapshot);
                    allocation = cycleSession.Pack();
                    foreach (FundedEntry fe in allocation.Funded)
                        if (fe?.Mission != null)
                            fundedKeysThisTurn.Add(StableMissionKey.For(fe.Mission));

                    // A multi-turn rebase is already airborne and committed to landing. Resume
                    // one such obligation before discretionary mission progress; its activation
                    // resources were protected by StrategicSpendability during the earlier
                    // strategic phases. Route safety and destination validity are live-rechecked
                    // inside ExecuteContinuation rather than trusting last turn's projection.
                    List<ArmyData> rebaseContinuations =
                        AviationRebasePlanner.FindMandatoryContinuations(player);
                    List<ArmyData> recoveries =
                        ReconAirExecutor.FindMandatoryRecoveryActors(player, ctx);
                    bool rebaseFirst = rebaseContinuations.Count > 0
                        && (recoveries.Count == 0
                            || rebaseContinuations[0].Id <= recoveries[0].Id);
                    if (rebaseFirst)
                    {
                        ArmyData rebaseWing = rebaseContinuations[0];
                        WorldAnalysis.StepObservationStamp beforeRebase =
                            WorldAnalysis.CaptureStepObservation(root, hand, snapshot);
                        bool rebaseMoved = false;
                        yield return AviationRebasePlanner.ExecuteContinuation(
                            player, root, ctx, rebaseWing, v => rebaseMoved = v);
                        if (rebaseMoved)
                            V2StateVersion.Bump();
                        snapshot = WorldAnalysis.RefreshStrategicKnowledge(
                            snapshot, player, root, hand, ctx);
                        WorldAnalysis.StepObservationStamp afterRebase =
                            WorldAnalysis.CaptureStepObservation(root, hand, snapshot);
                        WorldAnalysis.PublishStepObservationDelta(player, ctx.TurnNumber,
                            beforeRebase, afterRebase, null);
                        settledSteps++;
                        TakeTypedTriggers(out StrategicInvalidationReason rebaseOperationalReasons,
                            out StrategicInvalidationReason rebaseStrategicReasons,
                            out HashSet<DesireAxis> rebaseDirtyAxes);
                        bool rebaseStrategicChanged = ReenterStrategicAxes(
                            rebaseStrategicReasons, rebaseDirtyAxes);
                        bool rebaseProgress = rebaseMoved || rebaseStrategicChanged;
                        noProgressCycles = rebaseProgress ? 0 : noProgressCycles + 1;
                        AiDebugLog.Write($"[AI][V2][Loop] step={settledSteps} aviation-rebase "
                            + $"actor=#{rebaseWing.Id} progress={(rebaseProgress ? 1 : 0)} "
                            + $"operationalTriggers={rebaseOperationalReasons} "
                            + $"strategicTriggers={rebaseStrategicReasons}");
                        if (!rebaseProgress)
                        {
                            AiDebugLog.Write("[AI][V2][Loop] stop — aviation rebase could not take a safe step");
                            break;
                        }
                        continue;
                    }

                    // Lifecycle safety is admitted before strategic progress, but its must-return
                    // predicate remains owned by ReconAirExecutor. Exactly one airborne action is
                    // settled, observed and then re-admitted like every other step.
                    if (recoveries.Count > 0)
                    {
                        ArmyData recovery = recoveries[0];
                        HexCoord? recoveryFocus =
                            ReconPatrolStateRegistry.TryGet(player, recovery.Id, out ReconPatrolState recoveryState)
                                ? recoveryState.StrategicAnchor : (HexCoord?)null;
                        WorldAnalysis.StepObservationStamp beforeRecovery =
                            WorldAnalysis.CaptureStepObservation(root, hand, snapshot);
                        var recoveryResult = new AirReconExecutionResult();
                        var recoveryControl = new ReconAirExecutor.ActorStepControl();
                        int recoveryApBefore = root.ActionPoints;
                        yield return ReconAirExecutor.RunActorStep(player, root, ctx, snapshot,
                            recovery, recoveryResult, recoveryApBefore, recoveryFocus,
                            perMissionResult: null, control: recoveryControl);
                        snapshot = WorldAnalysis.RefreshStrategicKnowledge(
                            snapshot, player, root, hand, ctx);
                        WorldAnalysis.StepObservationStamp afterRecovery =
                            WorldAnalysis.CaptureStepObservation(root, hand, snapshot);
                        WorldAnalysis.PublishStepObservationDelta(player, ctx.TurnNumber,
                            beforeRecovery, afterRecovery, null);
                        settledSteps++;
                        bool recoveryProgress = recoveryResult.Mutated;
                        TakeTypedTriggers(out StrategicInvalidationReason recoveryOperationalReasons,
                            out StrategicInvalidationReason recoveryStrategicReasons,
                            out HashSet<DesireAxis> recoveryDirtyAxes);
                        bool recoveryStrategicChanged = ReenterStrategicAxes(
                            recoveryStrategicReasons, recoveryDirtyAxes);
                        // Reentry may publish another compound fact (for example, materializing a
                        // Raid reinforcement changes Actor + Capability). Route that fact through
                        // the same typed fan-out before consuming it so Economy/Development cannot
                        // lose their share to an operational-axis follow-up.
                        TakeTypedTriggers(
                            out StrategicInvalidationReason recoveryFollowupOperational,
                            out StrategicInvalidationReason recoveryFollowupStrategic,
                            out HashSet<DesireAxis> recoveryFollowupAxes);
                        recoveryOperationalReasons |= recoveryFollowupOperational;
                        recoveryStrategicReasons |= recoveryFollowupStrategic;
                        recoveryStrategicChanged |= ReenterStrategicAxes(
                            recoveryFollowupStrategic, recoveryFollowupAxes);
                        recoveryProgress |= recoveryStrategicChanged;
                        noProgressCycles = recoveryProgress ? 0 : noProgressCycles + 1;
                        AiDebugLog.Write($"[AI][V2][Loop] step={settledSteps} recovery actor=#{recovery.Id} "
                            + $"progress={(recoveryProgress ? 1 : 0)} "
                            + $"operationalTriggers={recoveryOperationalReasons} "
                            + $"strategicTriggers={recoveryStrategicReasons}");
                        if (recoveryOperationalReasons == StrategicInvalidationReason.None
                            && !recoveryStrategicChanged)
                        {
                            AiDebugLog.Write("[AI][V2][Loop] stop — recovery produced no typed invalidation");
                            break;
                        }
                        continue;
                    }

                    if (allocation.Funded.Count == 0)
                    {
                        zeroRadarResidualWindow = true;
                        AiDebugLog.Write("[AI][V2][Loop] stop — no funded typed mission");
                        break;
                    }

                    ProvisionedMission selected = null;
                    StableMissionKey selectedKey = default;
                    var attemptedKeys = new HashSet<StableMissionKey>();
                    // Two independent bounded budgets, not one shared counter: a Scout batch that
                    // keeps failing (assignmentReallocPass) must not be able to consume every
                    // realloc this cycle had, starving the single-mission repack
                    // (repriceReallocPass) that a mandatory Economy EnvelopeTooSmall/
                    // RepriceThisTurn depends on to ever see a corrected envelope this turn.
                    int assignmentReallocPass = 0;
                    int repriceReallocPass = 0;
                    bool provisioningSettled = false;
                    while (!provisioningSettled)
                    {
                        ProvisioningManager.PreparePass(player, root, ctx,
                            cycleProvisioning, allocation, actorCommitments);
                        IReadOnlyList<(FundedEntry Funded, ProvisionFailure Failure)> scoutFailures =
                            ProvisioningManager.ScoutAssignmentFailures(cycleProvisioning, allocation);
                        if (scoutFailures.Count > 0)
                        {
                            foreach ((FundedEntry failedFunding, ProvisionFailure failure) in scoutFailures)
                            {
                                StableMissionKey failedKey = StableMissionKey.For(failedFunding.Mission);
                                attemptedKeys.Add(failedKey);
                                provisioningFailures.TryGetValue(failure.Kind, out int scoutFailureCount);
                                provisioningFailures[failure.Kind] = scoutFailureCount + 1;
                                CapabilityPoolExhaustionRegistry.DeferNoExecutableStep(
                                    player, failedFunding.Mission, failure);
                                cycleSession.RegisterProvisionFailure(failedFunding, failure);
                                cycleLedger.RecordProvisionFailure(failedFunding.Mission, failure);
                                // Record any RetryNextTurn failure here (during the pass, before the
                                // next realloc's repack can drop this mission out of Funded entirely
                                // and erase it from cycleProvisioning.AssignmentRejections) — reading
                                // the rejection dict only after the whole step settles was catching
                                // just the last realloc pass's leftovers, near-always empty by then.
                                if (failure.Disposition == ProvisionDisposition.RetryNextTurn)
                                    retryNextTurnThisPass.Add(failedKey);
                                AiDebugLog.Write($"[AI][V2][Loop] assignment-batch "
                                    + $"[{AiV2Trace.FormatCorrelation(failedFunding.Mission)}] {failedKey} — FAIL "
                                    + $"{failure.Kind} [{failure.Disposition}] {failure.Detail}");
                            }

                            List<FundedEntry> openScouts = allocation.Funded.Where(fe =>
                                fe?.Mission?.Kind == MissionKind.Scout
                                && !cycleProvisioning.AlreadyProvisioned(
                                    StableMissionKey.For(fe.Mission))).ToList();
                            Dictionary<StableMissionKey, ProvisionFailure> scoutFailureByKey =
                                scoutFailures.ToDictionary(
                                    f => StableMissionKey.For(f.Funded.Mission), f => f.Failure);
                            // Exhaustion is a claim about the whole physical pool, not about this
                            // batch's session contention: only mark a pool exhausted when every
                            // still-open mission drawing on it failed AND each failure is proven
                            // pool-wide (ProvenPoolWideUnable), and no mission in that same pool
                            // already succeeded this batch (a scout that got a mover is live proof
                            // the pool is not exhausted).
                            foreach (CapabilityPoolKind pool in openScouts
                                         .Select(fe => CapabilityPoolExhaustionRegistry.PoolFor(fe.Mission))
                                         .Where(p => p != CapabilityPoolKind.None).Distinct())
                            {
                                List<FundedEntry> poolOpenScouts = openScouts.Where(fe =>
                                    CapabilityPoolExhaustionRegistry.PoolFor(fe.Mission) == pool).ToList();
                                bool poolHasSuccessThisBatch = cycleProvisioning.Successful.Values.Any(m =>
                                    m?.Mission != null
                                    && CapabilityPoolExhaustionRegistry.PoolFor(m.Mission) == pool);
                                if (poolHasSuccessThisBatch)
                                    continue;
                                bool poolWideExhausted = poolOpenScouts.Count > 0 && poolOpenScouts.All(fe =>
                                    scoutFailureByKey.TryGetValue(StableMissionKey.For(fe.Mission),
                                        out ProvisionFailure fail)
                                    && CapabilityPoolExhaustionRegistry.ProvenPoolWideUnable(
                                        snapshot, player, fe.Mission, fail));
                                if (poolWideExhausted)
                                    CapabilityPoolExhaustionRegistry.MarkExhausted(player, pool,
                                        $"assignment batch rejected all {poolOpenScouts.Count} funded "
                                        + $"Scout mission(s) in pool {pool}, proven pool-wide unable");
                            }

                            // One batch means one re-pack. The allocator now sees every impossible
                            // Scout at once, so released AP can admit Economy/Development immediately.
                            if (cycleSession.HasNewFailures && !cycleSession.Converged
                                && assignmentReallocPass < AiConfigV2.maxReallocIterations)
                            {
                                assignmentReallocPass++;
                                allocation = cycleSession.Pack();
                                foreach (FundedEntry fe in allocation.Funded)
                                    if (fe?.Mission != null)
                                        fundedKeysThisTurn.Add(StableMissionKey.For(fe.Mission));
                                continue;
                            }
                        }
                        FundedEntry selectedFunding = allocation.Funded.FirstOrDefault(fe =>
                            fe?.Mission != null
                            && CapabilityPoolExhaustionRegistry.CanAttempt(
                                player, fe.Mission, snapshot));
                        if (selectedFunding == null)
                            break;

                        selectedKey = StableMissionKey.For(selectedFunding.Mission);
                        attemptedKeys.Add(selectedKey);
                        ProvisioningResult provisionResult = ProvisioningManager.Provision(
                            player, root, hand, ctx, cycleProvisioning, selectedFunding);
                        if (provisionResult.Success)
                        {
                            selected = provisionResult.Provisioned;
                            cycleProvisioning.RegisterSuccess(selectedKey, selected);
                            cycleSession.RegisterProvisionSuccess(selectedFunding,
                                selected.ClaimedAp, selected.ClaimedPhysical);
                            cycleLedger.RecordProvisionSuccess(selectedFunding.Mission, selected);
                            provisioned.Add(selected);
                            AiV2Trace.CheckProvisionEnvelope(selectedFunding.Mission.AttemptId,
                                selected.ClaimedAp, selectedFunding.Tentative.Ap);
                            provisioningSettled = true;
                            break;
                        }

                        provisioningFailures.TryGetValue(provisionResult.Failure.Kind,
                            out int failureCount);
                        provisioningFailures[provisionResult.Failure.Kind] = failureCount + 1;
                        CapabilityPoolExhaustionRegistry.DeferNoExecutableStep(
                            player, selectedFunding.Mission, provisionResult.Failure);
                        bool poolWide = CapabilityPoolExhaustionRegistry.ProvenPoolWideUnable(
                            snapshot, player, selectedFunding.Mission, provisionResult.Failure);
                        if (poolWide)
                            CapabilityPoolExhaustionRegistry.MarkExhausted(player,
                                CapabilityPoolExhaustionRegistry.PoolFor(selectedFunding.Mission),
                                $"{provisionResult.Failure.Kind}: no eligible actor in snapshot");
                        cycleSession.RegisterProvisionFailure(selectedFunding, provisionResult.Failure);
                        cycleLedger.RecordProvisionFailure(selectedFunding.Mission,
                            provisionResult.Failure);
                        if (provisionResult.Failure.Disposition == ProvisionDisposition.RetryNextTurn)
                            retryNextTurnThisPass.Add(selectedKey);
                        AiDebugLog.Write($"[AI][V2][Loop] provision [{AiV2Trace.FormatCorrelation(selectedFunding.Mission)}] "
                            + $"{selectedKey} — FAIL {provisionResult.Failure.Kind} "
                            + $"[{provisionResult.Failure.Disposition}] {provisionResult.Failure.Detail}");

                        // A non-repricing failure rejects this key; repack can consider other missions.
                        // Count only a retry of the SAME key with a repriced envelope.
                        if (!cycleSession.HasNewFailures || cycleSession.Converged
                            || (provisionResult.Failure.Disposition == ProvisionDisposition.RepriceThisTurn
                                && ++repriceReallocPass >= AiConfigV2.maxReallocIterations))
                        {
                            provisioningSettled = true;
                            break;
                        }
                        allocation = cycleSession.Pack();
                        foreach (FundedEntry fe in allocation.Funded)
                            if (fe?.Mission != null)
                                fundedKeysThisTurn.Add(StableMissionKey.For(fe.Mission));
                    }

                    if (selected == null)
                    {
                        cycleLedger.RecordDeferrals(allocation.Deferred);
                        foreach (MissionTurnOutcome outcome in cycleLedger.Finalize()
                                     .Where(o => o != null && attemptedKeys.Contains(o.AttemptKey)))
                            MissionContinuityLayer.ReconcileStep(
                                player, snapshot.TurnNumber, outcome);
                        noProgressCycles++;
                        // A rejected positive or durable mission must not be mistaken for
                        // an exhausted portfolio; zero-only rejections leave a residual window.
                        zeroRadarResidualWindow = allocation.Funded.All(fe => fe != null
                            && !fe.IsCommitment && fe.Mission != null
                            && fe.Mission.EffectiveValue <= 0f);
                        AiDebugLog.Write($"[AI][V2][Loop] admission stopped — no provisioned task; "
                            + $"noProgress={noProgressCycles}");
                        // No task command ran and no observation can differ. Repeating the same
                        // admission under a fresh session only reproduces the same rejection; stop
                        // this family without consuming the real bounded task-step budget.
                        break;
                    }

                    WorldAnalysis.StepObservationStamp beforeStep =
                        WorldAnalysis.CaptureStepObservation(root, hand, snapshot);
                    var stepResults = new List<ExecutionResult>();
                    if (selected.Kind == MissionKind.Scout
                        && selected.ExecutorKind != ScoutExecutorKind.Ground)
                    {
                        AirReconPlan plan = AirReconPlanner.Plan(player, root, ctx,
                            snapshot, new[] { selected });
                        var airStepResult = new AirReconExecutionResult();
                        yield return ReconAirExecutor.ExecutePlanStep(plan, player, root, ctx,
                            snapshot, airStepResult, stepResults);
                    }
                    else
                    {
                        yield return TaskExecutor.ExecuteStep(player, root, ctx,
                            selected, stepResults, snapshot, enforceFreshPlan: true);
                    }

                    snapshot = WorldAnalysis.RefreshStrategicKnowledge(
                        snapshot, player, root, hand, ctx);
                    ExecutionResult settled = stepResults.FirstOrDefault();
                    WorldAnalysis.StepObservationStamp afterStep =
                        WorldAnalysis.CaptureStepObservation(root, hand, snapshot);
                    WorldAnalysis.PublishStepObservationDelta(player, ctx.TurnNumber,
                        beforeStep, afterStep, settled);

                    foreach (ExecutionResult er in stepResults)
                    {
                        cycleLedger.RecordExecution(er);
                        allExecuted.Add(er);
                    }
                    cycleLedger.RecordDeferrals(allocation.Deferred);
                    cycleLedger.RefreshObjectiveStatesLive(player);
                    foreach (MissionTurnOutcome outcome in cycleLedger.Finalize()
                                 .Where(o => o != null && attemptedKeys.Contains(o.AttemptKey)))
                        MissionContinuityLayer.ReconcileStep(
                            player, snapshot.TurnNumber, outcome);
                    // A single atomic move may consume the last MP after Provisioning had
                    // legitimately reserved this owner's completion AP. Settle its stage now.
                    InfrastructureFulfillment.ReconcileEconomyCompletionReservations(
                        player, root, hand, ctx);

                    settledSteps++;
                    bool progressed = stepResults.Any(er =>
                        er != null && er.Outcome.StateChanged);
                    TakeTypedTriggers(out StrategicInvalidationReason operationalReasons,
                        out StrategicInvalidationReason strategicReasons,
                        out HashSet<DesireAxis> dirtyStrategicAxes);
                    bool strategicChanged = ReenterStrategicAxes(
                        strategicReasons, dirtyStrategicAxes);
                    // A follow-up Phase A action may publish a reason shared by operational and
                    // strategic families. Take one typed snapshot and acknowledge every affected
                    // recipient before the registry clears that reason.
                    TakeTypedTriggers(
                        out StrategicInvalidationReason followupOperationalReasons,
                        out StrategicInvalidationReason followupStrategicReasons,
                        out HashSet<DesireAxis> followupDirtyAxes);
                    operationalReasons |= followupOperationalReasons;
                    strategicReasons |= followupStrategicReasons;
                    strategicChanged |= ReenterStrategicAxes(
                        followupStrategicReasons, followupDirtyAxes);
                    progressed |= strategicChanged;
                    noProgressCycles = progressed ? 0 : noProgressCycles + 1;
                    AiDebugLog.Write($"[AI][V2][Loop] step={settledSteps} task={selectedKey} "
                        + $"progress={(progressed ? 1 : 0)} stop={settled?.StopReason} "
                        + $"operationalTriggers={operationalReasons} strategicTriggers={strategicReasons} "
                        + $"noProgress={noProgressCycles}");
                    if (operationalReasons == StrategicInvalidationReason.None && !strategicChanged)
                    {
                        // Ignore the task that JUST executed: only unfinished positive
                        // allocations should prevent residual admission.
                        zeroRadarResidualWindow = allocation.Funded.All(fe => fe?.Mission != null
                            && (StableMissionKey.For(fe.Mission).Equals(selectedKey)
                                || (!fe.IsCommitment && fe.Mission.EffectiveValue <= 0f)));
                        AiDebugLog.Write("[AI][V2][Loop] stop — settled task produced no typed invalidation");
                        break;
                    }
                    }

                if (settledSteps >= AiConfigV2.maxMidTurnStepsPerTurn)
                    AiDebugLog.Write($"[AI][V2][Loop] bounded stop — max steps "
                        + $"{AiConfigV2.maxMidTurnStepsPerTurn}");
                if (noProgressCycles >= AiConfigV2.maxMidTurnNoProgressCycles)
                    AiDebugLog.Write($"[AI][V2][Loop] bounded stop — no progress cycles "
                        + $"{noProgressCycles}");

                }

                yield return RunTypedAdmissions();
                // Also reconcile on bounded/no-progress exits where no additional typed
                // admission occurs: Phase B must see AP that no actor can spend on a build.
                InfrastructureFulfillment.ReconcileEconomyCompletionReservations(
                    player, root, hand, ctx);

                // Management/Development is another bounded task family, not the owner of the
                // operational loop. Phase B settles until it either exhausts its candidates or
                // publishes a capability-changing residual. Typed Analysis deltas then re-admit
                // only the affected Development and/or Recon family, after which the same shared
                // per-turn tempo budget may resume.
                for (int managementRound = 0;
                     managementRound <= AiConfigV2.maxEndOfTurnTempoReruns;
                     managementRound++)
                {
                    snapshot = WorldAnalysis.RefreshStrategicKnowledge(
                        snapshot, player, root, hand, ctx);
                    reconObjectives = ReconObjectiveEvaluator.Enumerate(snapshot);
                    postCommitments = ActorCommitments.FromIntents(
                        MissionIntentRegistry.GetOrCreate(player).All, snapshot, reconObjectives);

                    WorldAnalysis.StepObservationStamp beforeManagement =
                        WorldAnalysis.CaptureStepObservation(root, hand, snapshot);
                    // A prior Phase B action may have spent AP or removed a build card.
                    // Revalidate each owner's stronger completion claim before the next pass.
                    InfrastructureFulfillment.ReconcileEconomyCompletionReservations(
                        player, root, hand, ctx);
                    var phaseBRound = new StrategicPhaseResult();
                    yield return StrategicManager.UseSurplus(snapshot, player, root, hand, ctx,
                        postCommitments, phaseB.Reservation ?? phaseA.Reservation,
                        phaseBRound, reconObjectives);
                    snapshot = WorldAnalysis.RefreshStrategicKnowledge(
                        snapshot, player, root, hand, ctx);
                    WorldAnalysis.StepObservationStamp afterManagement =
                        WorldAnalysis.CaptureStepObservation(root, hand, snapshot);
                    WorldAnalysis.PublishStepObservationDelta(player, ctx.TurnNumber,
                        beforeManagement, afterManagement, null);
                    phaseB.Accumulate(phaseBRound);

                    TakeTypedTriggers(out StrategicInvalidationReason operationalReasons,
                        out StrategicInvalidationReason strategicReasons,
                        out HashSet<DesireAxis> dirtyStrategicAxes);
                    bool operationalDirty = operationalReasons != StrategicInvalidationReason.None;
                    bool strategicDirty = strategicReasons != StrategicInvalidationReason.None;
                    bool strategicChanged = ReenterStrategicAxes(
                        strategicReasons, dirtyStrategicAxes);
                    // Phase B reentry can itself publish a compound invalidation. Preserve its
                    // full typed fan-out before acknowledging it.
                    TakeTypedTriggers(
                        out StrategicInvalidationReason managementFollowupOperational,
                        out StrategicInvalidationReason managementFollowupStrategic,
                        out HashSet<DesireAxis> managementFollowupAxes);
                    operationalReasons |= managementFollowupOperational;
                    strategicReasons |= managementFollowupStrategic;
                    operationalDirty |= managementFollowupOperational
                        != StrategicInvalidationReason.None;
                    strategicDirty |= managementFollowupStrategic
                        != StrategicInvalidationReason.None;
                    strategicChanged |= ReenterStrategicAxes(
                        managementFollowupStrategic, managementFollowupAxes);
                    if (operationalDirty || strategicChanged)
                        noProgressCycles = 0;

                    AiDebugLog.Write($"[AI][V2][Loop] management round={managementRound + 1} "
                        + $"strategicTriggers={strategicReasons} "
                        + $"operationalTriggers={operationalReasons} "
                        + $"operationalReadmit={(operationalDirty ? 1 : 0)}");

                    if (operationalDirty)
                    {
                        noProgressCycles = 0;
                        yield return RunTypedAdmissions();
                    }

                    // Phase B can change the hand or world without publishing a typed
                    // operational trigger. Reuse the canonical bounded admission loop
                    // on the settled state before admitting any zero-Radar residual.
                    if (phaseBRound.StateChanged && !operationalDirty)
                    {
                        noProgressCycles = 0;
                        yield return RunTypedAdmissions();
                    }
                    if (!phaseBRound.StateChanged && !strategicChanged)
                        break;
                    if (!operationalDirty && !strategicDirty)
                        break;
                }
                phaseBHandled = true;

                // A zero Radar is not a prohibition. Only AFTER the existing operational
                // and tempo passes exhaust their actionable budgets may new cold-axis
                // preparation use what is physically left. No new budget/scorer/executor:
                // call the same Phase A owner with freshly regenerated cold demands.
                var coldAxes = new HashSet<DesireAxis>(scopedDemandAxes.Where(a =>
                    RadarValueScale.For(radar, a) <= 0f));
                if (zeroRadarResidualWindow && coldAxes.Count > 0
                    && settledSteps < AiConfigV2.maxMidTurnStepsPerTurn
                    && noProgressCycles < AiConfigV2.maxMidTurnNoProgressCycles)
                {
                    snapshot = WorldAnalysis.RefreshStrategicKnowledge(
                        snapshot, player, root, hand, ctx);
                    reconObjectives = ReconObjectiveEvaluator.Enumerate(snapshot);
                    if (AiStrategyV2Scope.AxisInScope(DesireAxis.Aggression))
                        StrategyLayer.RefreshAggressionLanePressures(snapshot, assessment.Breakdown);
                    aggressionObjectives = AiStrategyV2Scope.AxisInScope(DesireAxis.Aggression)
                        ? AggressionObjectiveEvaluator.Enumerate(
                            snapshot, assessment.Breakdown.OpportunityReport)
                        : new List<AggressionObjective>();
                    activeIntents = MissionContinuityLayer.ResolveActive(
                        player, snapshot, reconObjectives, aggressionObjectives);
                    activeIntents = AiStrategyV2Scope.ApplyIntentScope(player, activeIntents);
                    actorCommitments = ActorCommitments.FromIntents(
                        activeIntents, snapshot, reconObjectives);
                    List<AxisDemand> coldDemands = AiStrategyV2Scope.ApplyDemandScope(
                        DemandLayer.Generate(snapshot, assessment.Breakdown,
                            reconObjectives, aggressionObjectives, activeIntents,
                            actorCommitments, player, ctx, root, null, scopedDemandAxes))
                        .Where(d => d != null && coldAxes.Contains(d.RequestingAxis)).ToList();
                    if (coldDemands.Count > 0)
                    {
                        // Phase A owns one carried Reservation object. Its per-call residual
                        // rewrite must not erase still-unfulfilled positive-axis telemetry.
                        List<AxisDemand> warmResidual = (phaseB.Reservation ?? phaseA.Reservation)
                            .UnresolvedDemands.Where(d => d != null
                                && RadarValueScale.For(radar, d.RequestingAxis) > 0f).ToList();
                        WorldAnalysis.StepObservationStamp beforeCold =
                            WorldAnalysis.CaptureStepObservation(root, hand, snapshot);
                        StrategicPhaseResult coldPass = StrategicManager.FulfillDemands(
                            snapshot, player, root, hand, ctx, apLedger, coldDemands,
                            actorCommitments, activeIntents, reconObjectives,
                            phaseB.Reservation ?? phaseA.Reservation,
                            economyAxisAuthoritative: coldAxes.Contains(DesireAxis.Economy),
                            radar: radar);
                        phaseA.Accumulate(coldPass);
                        phaseA.Reservation.UnresolvedDemands.AddRange(warmResidual);
                        AiDebugLog.Write($"[AI][V2][Loop] cold Radar residual — demands={coldDemands.Count} "
                            + $"spent={coldPass.CardsPlayed} changed={(coldPass.StateChanged ? 1 : 0)}");
                        if (coldPass.StateChanged)
                        {
                            snapshot = WorldAnalysis.RefreshStrategicKnowledge(
                                snapshot, player, root, hand, ctx);
                            WorldAnalysis.StepObservationStamp afterCold =
                                WorldAnalysis.CaptureStepObservation(root, hand, snapshot);
                            WorldAnalysis.PublishStepObservationDelta(player, ctx.TurnNumber,
                                beforeCold, afterCold, null);
                            reconObjectives = ReconObjectiveEvaluator.Enumerate(snapshot);
                            if (AiStrategyV2Scope.AxisInScope(DesireAxis.Aggression))
                                StrategyLayer.RefreshAggressionLanePressures(snapshot, assessment.Breakdown);
                            aggressionObjectives = AiStrategyV2Scope.AxisInScope(DesireAxis.Aggression)
                                ? AggressionObjectiveEvaluator.Enumerate(
                                    snapshot, assessment.Breakdown.OpportunityReport)
                                : new List<AggressionObjective>();
                            activeIntents = MissionContinuityLayer.ResolveActive(
                                player, snapshot, reconObjectives, aggressionObjectives);
                            activeIntents = AiStrategyV2Scope.ApplyIntentScope(player, activeIntents);
                            actorCommitments = ActorCommitments.FromIntents(
                                activeIntents, snapshot, reconObjectives);
                            demands = AiStrategyV2Scope.ApplyDemandScope(DemandLayer.Generate(
                                snapshot, assessment.Breakdown, reconObjectives,
                                aggressionObjectives, activeIntents, actorCommitments,
                                player, ctx, root, null, scopedDemandAxes));
                            ownershipFreshAfterPhaseA = true;
                            yield return RunTypedAdmissions();
                        }
                    }
                }

                // Cold Phase A and the following typed admissions may have created or
                // re-bound actors AFTER management captured postCommitments. Housekeeping
                // must see the latest canonical ownership, never the pre-cold snapshot.
                postCommitments = ActorCommitments.FromIntents(
                    MissionIntentRegistry.GetOrCreate(player).All, snapshot, reconObjectives);

                // Final reconciliation remains the only owner of end-of-turn aging/reaping. Intents
                // already reconciled locally carry LastReconciledTurn==turn and are not aged twice.
                MissionContinuityLayer.ReconcileAfterTurn(player,
                    snapshot.TurnNumber, new List<MissionTurnOutcome>());
                ReconAcceptanceAudit.Summarize(player, ctx.TurnNumber);
            }
            else
            {
                // 4. Planners -> mission proposals. Mission construction/stamping/logging has one
                //    owner shared by the legacy batch and the optional mid-turn re-admission loop.
                missions = BuildMissionSet(snapshot, assessment.Breakdown,
                    activeIntents, reconObjectives, aggressionObjectives, radar, demands, trace, ctx);
    
                // 7b. Bind a funding policy to each Soft/Hard intent by matching it to its fresh
                //     proposal. In ReconOnly activeIntents was already stripped of non-Recon durability.
                List<Commitment> commitments = MissionContinuityLayer.BindFunding(activeIntents, missions);
    
                var ledger = new MissionOutcomeLedger();
                ledger.RegisterProposals(missions);
                ledger.RegisterCommitments(commitments);
    
                // 5. Slices seeded from the SHARED AP ledger (net of Phase-A demand spend) -> many-to-
                //    many packing -> ordered tentative allocation. No second radar split.
                AllocationSession session = ResourceAllocator.BeginTurn(snapshot, radar, missions, commitments, player,
                    apLedger);
                var provSession = new ProvisioningSession(snapshot);
                allocation = session.Pack();
    
                // Spec §8 — distinguish "funded on the FINAL allocation pack" from "distinct missions
                // funded at any point this turn". After a pack -> provision -> re-pack loop these differ
                // legitimately (a mission funded on pass 1, provisioned, then dropped from the last
                // pack), and reporting only the last pack next to cumulative provisioned/executed
                // counters reads as an inconsistency during debugging.
                fundedKeysThisTurn = new HashSet<StableMissionKey>();
                void AccrueFundedKeys(TentativeAllocation a)
                {
                    if (a?.Funded == null) return;
                    foreach (FundedEntry fe in a.Funded)
                        if (fe?.Mission != null)
                            fundedKeysThisTurn.Add(StableMissionKey.For(fe.Mission));
                }
                AccrueFundedKeys(allocation);
    
                // 6. Provision the funded missions through the ONE atomic door, with the bounded
                //    pack -> provision -> re-pack loop (risk 2). Mover assignment across the funded set
                //    is a per-pass batch step (PreparePass) so a single Provision() call carries no
                //    hidden cross-mission responsibility. Re-pack is bounded by maxReallocIterations +
                //    the AllocationSession's own rejected/cooldown/repriced/fingerprint state.
                provisioned = new List<ProvisionedMission>();
                provisioningFailures = new Dictionary<ProvisionFailureKind, int>();
                int reallocPass = 0;
                while (true)
                {
                    ProvisioningManager.PreparePass(player, root, ctx, provSession, allocation,
                        actorCommitments);
                    bool anyFailure = false;
                    bool allFailuresArePoolWide = true;
                    foreach (FundedEntry fe in allocation.Funded)
                    {
                        if (fe?.Mission == null)
                            continue;
                        StableMissionKey key = StableMissionKey.For(fe.Mission);
                        if (provSession.AlreadyProvisioned(key))
                            continue; // locked by an earlier pass this turn
                        // A capability pool proven pool-wide unable is not asked again UNLESS a cheap
                        // revalidation now finds an eligible actor (spec §7).
                        if (!CapabilityPoolExhaustionRegistry.CanAttempt(player, fe.Mission, snapshot))
                            continue;
    
                        ProvisioningResult result = ProvisioningManager.Provision(player, root, hand, ctx, provSession, fe);
                        if (result.Success)
                        {
                            provSession.RegisterSuccess(key, result.Provisioned);
                            session.RegisterProvisionSuccess(fe,
                                result.Provisioned.ClaimedAp, result.Provisioned.ClaimedPhysical);
                            ledger.RecordProvisionSuccess(fe.Mission, result.Provisioned);
                            provisioned.Add(result.Provisioned);
                            AiV2Trace.CheckProvisionEnvelope(fe.Mission.AttemptId,
                                result.Provisioned.ClaimedAp, fe.Tentative.Ap);
                            AiDebugLog.Write($"[AI][V2]   provision [{AiV2Trace.FormatCorrelation(fe.Mission)}] {key} — OK mover #{result.Provisioned.MoverArmyId} "
                                + $"ap {result.Provisioned.ClaimedAp.ToString("0.#", CultureInfo.InvariantCulture)} "
                                + $"(envelope {fe.Tentative.Ap.ToString("0.#", CultureInfo.InvariantCulture)}) "
                                + $"stealthReserve {(result.Provisioned.StealthApReserved ? 1 : 0)}");
                        }
                        else
                        {
                            anyFailure = true;
                            provisioningFailures.TryGetValue(result.Failure.Kind, out int failureCount);
                            provisioningFailures[result.Failure.Kind] = failureCount + 1;
                            CapabilityPoolExhaustionRegistry.DeferNoExecutableStep(
                                player, fe.Mission, result.Failure);
                            bool poolWide = CapabilityPoolExhaustionRegistry.ProvenPoolWideUnable(
                                snapshot, player, fe.Mission, result.Failure);
                            if (poolWide)
                                CapabilityPoolExhaustionRegistry.MarkExhausted(player,
                                    CapabilityPoolExhaustionRegistry.PoolFor(fe.Mission),
                                    $"{result.Failure.Kind}: no eligible actor in snapshot");
                            allFailuresArePoolWide &= poolWide;
                            session.RegisterProvisionFailure(fe, result.Failure);
                            ledger.RecordProvisionFailure(fe.Mission, result.Failure);
                            AiDebugLog.Write($"[AI][V2]   provision [{AiV2Trace.FormatCorrelation(fe.Mission)}] {key} — FAIL {result.Failure.Kind} "
                                + $"[{result.Failure.Disposition}] {result.Failure.Detail}");
                        }
                    }
    
                    if (anyFailure && allFailuresArePoolWide)
                        AiDebugLog.Write("[AI][V2] provision — failed capability pools exhausted; re-pack to admit other runnable lanes");
                    if (!session.HasNewFailures || session.Converged)
                        break;
                    if (++reallocPass >= AiConfigV2.maxReallocIterations)
                        break;
                    allocation = session.Pack();
                    AccrueFundedKeys(allocation);
                }
    
                // 6b. Tasks -> per-hex execution on the real map (reuses AiTurnController.MoveArmyRoutine).
                // Round 4 — a Scout ProvisionedMission bound to an air actor by ReconAssignmentPlanner/
                // ProvisioningManager.ProvisionAir must NOT go through TaskExecutor/ReconGroundExecutor
                // (which expects a live ground solo-Recce mover); it is execution-input for the terminal
                // air-recon stage instead. Ground + Raid missions are unaffected.
                var groundProvisioned = provisioned
                    .Where(pm => pm.Kind != MissionKind.Scout || pm.ExecutorKind == ScoutExecutorKind.Ground)
                    .ToList();
                var airProvisioned = provisioned
                    .Where(pm => pm.Kind == MissionKind.Scout && pm.ExecutorKind != ScoutExecutorKind.Ground)
                    .ToList();
    
                var executed = new List<ExecutionResult>();
                yield return TaskExecutor.Execute(player, root, ctx, groundProvisioned, executed, snapshot);
    
                if (executed.Any(e => e != null && e.Outcome.StateChanged))
                    snapshot = WorldAnalysis.RefreshStrategicKnowledge(snapshot, player, root, hand, ctx);
    
                // ARCH-02 §35 — terminal air-recon is its OWN stage: PLAN the pass against the real,
                // current world state, then EXECUTE the plan. TaskExecutor no longer touches air recon.
                // Round 3 — no protection to release any more (AiConfigV2/ReconAirReservation.cs).
                // Round 4 — AirReconPlanner no longer SELECTS; it turns this pass's air-bound
                // ProvisionedMissions (WHO/WHICH-TARGET already decided by Assignment) into the
                // executor's input shape.
                AirReconPlan airReconPlan = AirReconPlanner.Plan(player, root, ctx, snapshot, airProvisioned);
                var airReconResult = new AirReconExecutionResult();
                // RECON-AIR-06 — `airPerMissionResults` collects one ExecutionResult PER air-executed
                // ProvisionedMission this pass (the SAME shape Ground's `executed` list carries), so each
                // flows into MissionOutcomeLedger.RecordExecution / MissionContinuity exactly like
                // Ground's do — a provisioned air mission no longer silently falls through to
                // Finalize()'s Blocked default for lack of any recorded Execution.
                var airPerMissionResults = new List<ExecutionResult>();
                yield return ReconAirExecutor.Execute(airReconPlan, player, root, ctx, snapshot, airReconResult, airPerMissionResults);
                AiDebugLog.Write($"[AI][V2][Recon][Air] exec — outcome moved={airReconResult.AnyMoved} "
                    + $"launched={airReconResult.AnyLaunched} struck={airReconResult.AnyStruck} steps={airReconResult.Steps} "
                    + $"ap={airReconResult.ApSpent:0.#} stateVer={airReconResult.StateVersionAfter} "
                    + $"perMission={airPerMissionResults.Count}");
                allExecuted = executed.Concat(airPerMissionResults).ToList();
                foreach (ExecutionResult er in allExecuted)
                    ledger.RecordExecution(er);
                ledger.RecordDeferrals(allocation.Deferred);
                // Post-execution LIVE pass — a mission run later this turn may have met an earlier
                // Surveil's objective. The ONLY live-world read on the continuity path, isolated in the
                // ledger via ScoutObjectiveEvaluator; ReconcileAfterTurn below stays pure.
                ledger.RefreshObjectiveStatesLive(player);
    
                // 7c. Update durable intent state for next turn — a PURE transition over the ledger's
                //     facts (no world reads). Creates intents for started-but-unfinished recon,
                //     advances/retires the rest, keeps a preferred mover.
                MissionContinuityLayer.ReconcileAfterTurn(player, snapshot.TurnNumber, ledger.Finalize());
    
    
            }

            // S5. Strategic Manager Phase B — Surplus Preparation. Spec §5/§13 — this runs in EVERY
            //     mode, ReconOnly included: it is hand/card lifecycle management, not an operational
            //     mission family. A combat Unit/Hero/Aviation it places may sit on the map while
            //     Aggression/Defence missions stay disabled; that is expected in the isolated test.
            //     Card type is never on its own a reason a legal card is left unplayed.
            // Execution can reveal contacts and alter map knowledge (especially aviation). Phase B
            // must consume a coherent strategic snapshot, not operational resources paired with
            // the pre-execution Known/MapKnowledge layers.
            if (!phaseBHandled)
            {
                snapshot = WorldAnalysis.RefreshStrategicKnowledge(snapshot, player, root, hand, ctx);
                reconObjectives = ReconObjectiveEvaluator.Enumerate(snapshot);
                postCommitments = ActorCommitments.FromIntents(
                    MissionIntentRegistry.GetOrCreate(player).All, snapshot, reconObjectives);
                // AI-MGR-02 — Phase B is now the single bounded end-of-turn tempo arbiter (coroutine).
                yield return StrategicManager.UseSurplus(snapshot, player, root, hand, ctx,
                    postCommitments, phaseA.Reservation, phaseB, reconObjectives);
                if (phaseB.StateChanged)
                    snapshot = WorldAnalysis.RefreshStrategicKnowledge(
                        snapshot, player, root, hand, ctx);
            }

            // Spec §9 — one per-turn StrategicManager summary so it is always answerable why each
            // hand card was or was not played this turn. Per-card blocking reasons are on the
            // strat.A/strat.B diag lines above (fails=[card: needs H/E/M/T=…] / defer … / hold …).
            // "remaining" carries NO "blocked=ReconOnly / wrongAxis" reason: Phase B runs in every
            // mode and card type alone never suppresses a legal card.
            int mPlayed = phaseA.CardsPlayed + phaseB.CardsPlayed;
            int mGen = phaseA.GeneratedCardsSucceeded + phaseB.GeneratedCardsSucceeded;
            int mEquip = phaseA.EquipmentAssignmentsSucceeded + phaseB.EquipmentAssignmentsSucceeded;
            int mInfra = phaseA.InfrastructureBuilt + phaseB.InfrastructureBuilt;
            int mDrawn = phaseA.CardsDrawn + phaseB.CardsDrawn;
            int handEnd = hand?.Hand?.Count ?? 0;
            AiDebugLog.Write($"[AI][V2][StrategicManager][Summary] handStart={handAtStart} "
                + $"played={mPlayed} (phaseA {phaseA.CardsPlayed}, phaseB {phaseB.CardsPlayed}) "
                + $"generated={mGen} equipAttached={mEquip} infraBuilt={mInfra} drawn={mDrawn} "
                + $"handEnd={handEnd} remaining={System.Math.Max(0, handEnd)} "
                + $"matAttempts={phaseA.MaterializationAttempts + phaseB.MaterializationAttempts} "
                + $"capDeliveries={phaseA.CapabilityDeliveries + phaseB.CapabilityDeliveries} "
                + "blockedReasons=see strat.A/strat.B diag lines (never ReconOnly/wrongAxis)");

            // End-of-Main physical resource control totals (spec §2.7). Housekeeping is zero-AP by
            // invariant and the bounded reaction pass logs its own [STATE]; captured here so the
            // Main line means the main phase.
            AiV2Trace.LogState(trace.Id, stateStart, AiV2Trace.Stamp(root));

            // 8. Off-budget housekeeping — NOT an axis, guaranteed minimum, cannot be out-competed.
            //    ReconOnly keeps this safety/cleanup layer; it does not buy cards or create new
            //    capability and remains the authoritative same-hex reorganisation path.
            var housekeeping = new HousekeepingResult();
            yield return HousekeepingManager.RunHousekeeping(
                snapshot, player, root, ctx, postCommitments, housekeeping, phaseB.Reservation);
            if (housekeeping.StateChanged)
                snapshot = WorldAnalysis.RefreshStrategicKnowledge(
                    snapshot, player, root, hand, ctx);

            // --- Main-phase activity bucket. DERIVED once, here, from this pipeline's own facts —
            //     never incremented inside a nested layer (spec §11). The Reaction bucket is owned
            //     by StrategicReactionPass the same way. Total = Main + Reaction, no double count.
            V2PhaseActivity main = V2TurnActivityTelemetry.Phase(player, ctx.TurnNumber, V2Phase.Main);
            main.DemandsRaised = demands.Count;
            main.MissionsConsidered = missions.Count;
            // §8 — the activity bucket's peers (Provisioned, ExecutionAttempts, …) are all
            // full-turn cumulative, so MissionsFunded is the distinct-missions-funded-this-turn
            // count, not just the last pack's.
            main.MissionsFunded = fundedKeysThisTurn.Count;
            main.Provisioned = provisioned.Count;
            foreach (KeyValuePair<ProvisionFailureKind, int> failure in provisioningFailures)
                for (int i = 0; i < failure.Value; i++)
                    main.RecordProvisionFailure(failure.Key);
            main.ExecutionAttempts = allExecuted.Count(MissionRevalidator.WasAttempt);
            main.ExecutionsSucceeded = allExecuted.Count(MissionRevalidator.WasGenuineExecution);
            main.ExecutionsStaleOrSkipped = allExecuted.Count(MissionRevalidator.WasStaleOrSkipped);
            main.CardsPlayed = phaseA.CardsPlayed + phaseB.CardsPlayed;
            main.CardsDrawn = phaseA.CardsDrawn + phaseB.CardsDrawn;
            main.InfrastructureAttempts = phaseA.InfrastructureAttempts + phaseB.InfrastructureAttempts;
            main.InfrastructureBuilt = phaseA.InfrastructureBuilt + phaseB.InfrastructureBuilt;
            main.MaterializationAttempts = phaseA.MaterializationAttempts + phaseB.MaterializationAttempts;
            main.MaterializationsSucceeded = phaseA.MaterializationsSucceeded + phaseB.MaterializationsSucceeded;
            main.GeneratedCardAttempts = phaseA.GeneratedCardAttempts + phaseB.GeneratedCardAttempts;
            main.GeneratedCardsSucceeded = phaseA.GeneratedCardsSucceeded + phaseB.GeneratedCardsSucceeded;
            main.EquipmentAssignmentAttempts = phaseA.EquipmentAssignmentAttempts + phaseB.EquipmentAssignmentAttempts;
            main.EquipmentAssignmentsSucceeded = phaseA.EquipmentAssignmentsSucceeded + phaseB.EquipmentAssignmentsSucceeded;
            main.CapabilityDeliveries = phaseA.CapabilityDeliveries + phaseB.CapabilityDeliveries;

            AiDebugLog.Write($"[AI][V2] === {player.Nickname} — V2 turn ends "
                + $"(demands {demands.Count}, stratA {phaseA.CardsPlayed}, missions {missions.Count}, "
                + $"lastPackFunded {allocation.Funded.Count}, turnFundedUnique {fundedKeysThisTurn.Count}, "
                + $"provisioned {provisioned.Count}, executed {allExecuted.Count}, stratB {phaseB.CardsPlayed}) ===");
            V2TurnActivityTelemetry.LogSummary(player, ctx.TurnNumber);

            // AI-MGR-02 §8 — no strategic resource reservation may survive turn end. Anything still
            // standing is an owner that failed to release; log it and force-clear.
            StrategicResourceReservationLedger.ExpireStage(player, ctx.TurnNumber,
                StrategicReservationExpiry.EndOfTurn);
            StrategicResourceReservationLedger.AssertClearAtTurnEnd(player, ctx.TurnNumber);

            RecordInitiativeAnalytics(player, root, hand, initiativeStartAp, initiativeBaseAp, initiativeActionableAtStart);
            yield return null;
        }

        private static List<MissionProposal> BuildMissionSet(WorldSnapshot snapshot,
            DesireBreakdown breakdown, IReadOnlyList<MissionIntent> activeIntents,
            IReadOnlyList<ReconObjective> reconObjectives,
            IReadOnlyList<AggressionObjective> aggressionObjectives, Radar radar,
            IReadOnlyList<AxisDemand> demands, V2TraceScope trace,
            AiTurnContext ctx, bool aggressionPressureAlreadyRefreshed = false)
        {
            // Orchestration owns mid-turn sequencing: refresh only the Recon lane pressures from
            // the current snapshot right before Missions consumes them, so a frontier completion
            // earlier this same settled pass is reflected without Missions itself triggering
            // Strategy/Desire recomputation.
            StrategyLayer.RefreshReconLanePressures(snapshot, breakdown);
            // AGG-RAID §3 — the same discipline for the Aggression lane: refresh only the
            // operational opportunity facts from the current snapshot, never the radar.
            if (AiStrategyV2Scope.AxisInScope(DesireAxis.Aggression)
                && !aggressionPressureAlreadyRefreshed)
                StrategyLayer.RefreshAggressionLanePressures(snapshot, breakdown);
            List<MissionProposal> missions = ReconMissionPlanner.Propose(snapshot, breakdown,
                activeIntents, reconObjectives);
            if (AiStrategyV2Scope.AxisInScope(DesireAxis.Aggression))
                missions.AddRange(AggressionMissionLayer.Propose(snapshot, breakdown,
                    activeIntents, aggressionObjectives, ctx));
            if (AiStrategyV2Scope.AxisInScope(DesireAxis.Economy))
                missions.AddRange(EconomyMissionPlanner.Propose(snapshot, breakdown,
                    activeIntents, demands));
            if (AiStrategyV2Scope.AxisInScope(DesireAxis.Development))
                missions.AddRange(DevelopmentMissionPlanner.Propose(snapshot, activeIntents, demands));
            missions = AiStrategyV2Scope.ApplyMissionScope(missions);

            foreach (MissionProposal m in missions)
                if (m != null && string.IsNullOrEmpty(m.AttemptId))
                    m.AttemptId = trace?.NextMissionAttemptId() ?? "?";
            foreach (MissionProposal m in missions)
                if (m != null)
                    m.EffectiveValue = m.BaseValue * RadarValueScale.For(radar, m);

            AiV2Trace.CorrelateDemandsToMissions(demands, missions);
            return missions;
        }

        // End-of-turn initiative AP telemetry write-back (see the turn-start capture above). A
        // turn that ended at 0 AP only counts as "needed more AP" if real AP work still remained
        // — an unactivated field army, or an affordable AP-costing card still in hand.
        private static void RecordInitiativeAnalytics(PlayerSetupData player, PlayerRoot root, AiHandData hand,
            int startAp, int baseAp, int actionableAtStart)
        {
            int endAp = root.ActionPoints;
            int apSpent = UnityEngine.Mathf.Max(0, startAp - endAp);
            int unactivatedActionable =
                Game.Ai.V2.Initiative.PreTurnCapacityAnalysis.CountActionableFieldArmies(player, unactivatedOnly: true);

            bool affordableCardWaiting = false;
            if (hand != null && endAp > 0)
                foreach (Game.Cards.CardData c in hand.Hand)
                {
                    int ap = c != null ? CardCostRules.PlayAp(c) : 0;
                    if (ap > 0 && ap <= endAp) { affordableCardWaiting = true; break; }
                }

            bool hadPotentialWork = unactivatedActionable > 0 || affordableCardWaiting;

            Game.Ai.V2.Initiative.InitiativeAnalyticsHistory.Record(player,
                new Game.Ai.V2.Initiative.InitiativeTurnRecord(
                    baseAp, startAp, apSpent, endAp,
                    actionableAtStart, unactivatedActionable, hadPotentialWork));
        }

    }

    // ---- Stage stubs. Each grows real logic in its build-order step, then splits into its own
    //      file. Signatures are deliberate seams; fill the bodies, don't reshape the flow.

    // WorldAnalysis (build-order step 2) now lives in its own file, WorldAnalysis.cs.

    // StrategyLayer (build-order step 3) now lives in its own file, DesireEvaluators.cs, together
    // with ReconEvaluator / AggressionEvaluator, the AiRadarState cross-turn registry, and the
    // RadarAssessment / DesireBreakdown contract it returns.

    // MissionLayer (build-order step 4, + step 7.1 candidate beam) now lives in its own file,
    // ReconMissionPlanner.cs, with ScoutCostModel (the shared AP/Energy/ETA estimator — risk 3).
    // It reads the DesireBreakdown and emits a CANDIDATE BEAM of up to
    // AiConfigV2.scoutCandidateBeamWidth Scout proposals (execution capacity K and mission
    // conflicts are the allocator's job — MissionAdmissionPolicy); Raid is added in step 9.

    // MissionContinuityLayer (build-order step 7) lives in MissionIntent.cs, with MissionIntent /
    // MissionIntentKey / ScoutIntent / MissionIntentRegistry (durable intent state), CommitmentTier
    // / IntentStatus (funding policy + suspension), and MissionOutcomeLedger / MissionTurnOutcome
    // (the ordered per-turn record ReconcileAfterTurn transitions on). ScoutObjectiveEvaluator (the
    // shared completion / validity home) lives in ScoutObjectiveEvaluator.cs.

    // ResourceAllocator (build-order step 5) lives in ResourceAllocator.cs. ProvisioningManager /
    // ProvisioningSession / ProvisionedMission / ProvisionFailure / ProvisioningResult (build-order
    // step 6a) live in ProvisioningManager.cs, with the shared ScoutMoverSelector. TaskExecutor /
    // ExecutionResult / ExecutionStopReason (step 6a) live in TaskExecutor.cs. AiScoutStealthPolicy
    // (the shared V1+V2 stealth-warrant primitive) lives in Assets/Scripts/Ai/AiScoutStealthPolicy.cs.

    // HousekeepingManager (renamed from Manager) — build-order step 8C. A SEPARATE, post-mission
    // system from StrategicManager: it owns deterministic same-hex army/garrison REORGANIZATION,
    // not card play. NOT a radar axis — off-budget. It now lives in its own file,
    // HousekeepingManager.cs, with LocalForceGroup / ArmyReorgProfile (ArmyReorgProfile.cs),
    // ArmyReorgAnalyzer.cs, ArmyReorganizationPlanner.cs, ReorganizationPlan.cs and
    // HousekeepingExecutor.cs. This orchestration file only calls it (stage 8 above).
}
