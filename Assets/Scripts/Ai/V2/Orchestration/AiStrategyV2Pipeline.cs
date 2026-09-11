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
    //    Final axes: Recon, Aggression, Defence, Economy, Development.  (DesireAxis enum below.)
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
    public enum DesireAxis { Recon, Aggression, Defence, Economy, Development }

    public static class DesireAxes
    {
        public static readonly DesireAxis[] All =
        {
            DesireAxis.Recon, DesireAxis.Aggression, DesireAxis.Defence,
            DesireAxis.Economy, DesireAxis.Development,
        };

        public static string Abbrev(DesireAxis a)
        {
            switch (a)
            {
                case DesireAxis.Recon: return "RCN";
                case DesireAxis.Aggression: return "AGG";
                case DesireAxis.Defence: return "DEF";
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
                        | StrategicInvalidationReason.EventState;
                case DesireAxis.Defence:
                    return StrategicInvalidationReason.Threat
                        | StrategicInvalidationReason.Actor;
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
                    return StrategicInvalidationReason.Resources
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

    // --- Radar model #1a. The radar's ONLY effect on decisions: it scales objective / mission
    //     VALUE. It does NOT slice AP (one shared pool) and is NOT part of within-lane ordering.
    //       scale(axis) = floor + (1 - floor) * min(1, weight * axisCount)
    //     weight == 1/axisCount (an even split) -> 1.0 ; weight -> 0 (a cold axis) -> floor ;
    //     weight >= 1/axisCount -> 1.0 (a hot axis is NOT super-boosted — cold-side attenuation is
    //     enough, and a hot axis already carries more/better objectives). floor is
    //     AiConfigV2.radarScaleFloor.
    public static class RadarValueScale
    {
        public static float For(Radar radar, DesireAxis axis)
        {
            float floor = UnityEngine.Mathf.Clamp01(AiConfigV2.radarScaleFloor);
            float w = radar?.Weight != null && radar.Weight.TryGetValue(axis, out float ww)
                ? UnityEngine.Mathf.Max(0f, ww) : 0f;
            float norm = UnityEngine.Mathf.Clamp01(w * DesireAxes.All.Length);
            return floor + (1f - floor) * norm;
        }

        // Contribution-weighted scale for a mission. Every real proposal today names exactly one
        // axis at 1.0, so this collapses to For(radar, thatAxis); a future multi-axis mission gets
        // the contribution-weighted blend.
        public static float For(Radar radar, MissionProposal m)
        {
            var contrib = m?.Axes?.Value;
            if (contrib == null || contrib.Count == 0)
                return For(radar, DesireAxis.Recon);
            float acc = 0f, wsum = 0f;
            foreach (DesireAxis a in DesireAxes.All)
                if (contrib.TryGetValue(a, out float c) && c > 0f)
                {
                    acc += c * For(radar, a);
                    wsum += c;
                }
            return wsum > 0f ? acc / wsum : For(radar, DesireAxis.Recon);
        }
    }

    // --- How much each axis a single mission serves. MANY-TO-MANY (risk 1): never collapse to one
    //     category. Values are 0..1 "relevance", not required to sum to anything.
    public sealed class AxisContribution
    {
        public readonly Dictionary<DesireAxis, float> Value = new Dictionary<DesireAxis, float>();
    }

    // Concrete mission kinds. Each maps to a V2 Task builder in TaskExecutor. Was a bare string
    // until build-order step 4 — typed now, before anything downstream depends on the spelling.
    public enum MissionKind { Scout, Raid, Economy }

    public enum EconomyTaskKind { BuildExtraction, FoundBase, ReturnBuilder }

    public struct EconomyMissionTarget
    {
        public EconomyTaskKind Kind;
        public HexCoord TargetHex;
        public ResourceType? ResourceType;
        public string ObjectiveId;
        public int? BuilderArmyId;
        public CardData BuildCard;
        public ResourceCost BuildResourceCost;
        public float BuildApCost;
        public float BuildValue;
        public float MinimumFollowupAp;
        public IReadOnlyList<EconomyBuilderRouteSnapshot> BuilderRoutes;
        public int ProjectedActivationApCost;
        public int ProjectedMaxMovement;
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
            // 3e. Development opportunities — EV-scored upgrade options from the shared
            //     snapshot.Development readiness. Consumed by DemandLayer (step 4) + Phase A (step 5).
            List<DevelopmentOpportunity> devOpportunities = AiStrategyV2Scope.AxisInScope(DesireAxis.Development)
                ? DevelopmentOpportunityEvaluator.Enumerate(snapshot, player, root, hand, aggressionObjectives)
                : new List<DevelopmentOpportunity>();

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
            List<MissionIntent> activeIntents = MissionContinuityLayer.ResolveActive(player, snapshot, reconObjectives);
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
                devOpportunities, radar, scopedDemandAxes);
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
                ctx, apLedger, demands, actorCommitments, activeIntents, reconObjectives);

            // S4. Operational self-state refresh — ONLY if StrategicManager changed gameplay state
            //     (a partial CreateArmy + failed deploy still counts). Rebuilds Self + Economy;
            //     keeps the frozen strategic observations (Known / TrueWorld / MapKnowledge / Threat
            //     / radar / breakdown / reconObjectives).
            if (phaseA.StateChanged)
            {
                snapshot = WorldAnalysis.RefreshOperationalState(snapshot, player, root, hand, ctx);
                // Direct Economy construction can atomically turn the builder's existing intent
                // into ReturnBuilder (or resume a safe scout). Re-read the same continuity owner
                // before mission construction so stale pre-build actor claims cannot execute.
                activeIntents = MissionContinuityLayer.ResolveActive(
                    player, snapshot, reconObjectives);
                activeIntents = AiStrategyV2Scope.ApplyIntentScope(player, activeIntents);
                actorCommitments = ActorCommitments.FromIntents(
                    activeIntents, snapshot, reconObjectives);
                // Phase A changed the settled facts behind the initial demand frame. Refresh that
                // frame once here; the first operational admission consumes it without another
                // full Generate call.
                devOpportunities = AiStrategyV2Scope.AxisInScope(DesireAxis.Development)
                    ? DevelopmentOpportunityEvaluator.Enumerate(
                        snapshot, player, root, hand, aggressionObjectives)
                    : new List<DevelopmentOpportunity>();
                demands = DemandLayer.Generate(snapshot, assessment.Breakdown,
                    reconObjectives, aggressionObjectives, activeIntents, actorCommitments,
                    player, ctx, root, devOpportunities, radar, scopedDemandAxes);
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

                string StrategicAdmissionFingerprint(DesireAxis axis)
                {
                    string resources = root == null ? "-" : string.Join(",",
                        ResourceBundle.All.Select(t => root.GetResource(t).ToString("0.###",
                            CultureInfo.InvariantCulture)));
                    string armies = string.Join(";", (snapshot?.Self?.Armies
                            ?? System.Array.Empty<ArmySnapshot>())
                        .Where(a => a != null).OrderBy(a => a.ArmyId)
                        .Select(a => $"{a.ArmyId}:{a.Hex.Q},{a.Hex.R}:{a.MemberCount}:"
                            + $"{a.CurrentMovement}:{a.ActivationApCost}:{(a.HasHero ? 1 : 0)}"));
                    string economyFacts = axis == DesireAxis.Economy
                        ? "|sites=" + string.Join(";", (snapshot?.Economy?.ExtractionOpportunities
                                ?? System.Array.Empty<EconomyExtractionOpportunity>())
                            .OrderBy(x => x.Hex.Q).ThenBy(x => x.Hex.R)
                            .ThenBy(x => (int)x.ResourceType)
                            .Select(x => $"{x.Hex.Q},{x.Hex.R}:{(int)x.ResourceType}:{x.MarginalIncomeGain}"))
                          + "|bases=" + string.Join(";", (snapshot?.Economy?.BaseOpportunities
                                ?? System.Array.Empty<EconomyBaseOpportunity>())
                            .OrderBy(x => x.Hex.Q).ThenBy(x => x.Hex.R)
                            .Select(x => $"{x.Hex.Q},{x.Hex.R}:{x.CapacityValue:0.###}"))
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
                    return $"v={V2StateVersion.Current}|axis={axis}|ap={root?.ActionPoints ?? 0}"
                        + $"|res={resources}|hand={hand?.MutationVersion ?? -1}|armies={armies}"
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
                            || intent.Economy.Kind == EconomyTaskKind.ReturnBuilder
                            || !intent.PreferredMoverArmyId.HasValue)
                            continue;
                        ArmySnapshot actor = snapshot?.Self?.Armies?.FirstOrDefault(a => a != null
                            && a.ArmyId == intent.PreferredMoverArmyId.Value);
                        if (actor != null && actor.Hex.Equals(intent.Economy.TargetHex))
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
                    operationalReasons = pending.Reasons
                        & DesireAxes.InvalidationMaskFor(DesireAxis.Recon);
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
                    aggressionObjectives = AiStrategyV2Scope.AxisInScope(DesireAxis.Aggression)
                        ? AggressionObjectiveEvaluator.Enumerate(
                            snapshot, assessment.Breakdown.OpportunityReport)
                        : new List<AggressionObjective>();
                    activeIntents = MissionContinuityLayer.ResolveActive(
                        player, snapshot, reconObjectives);
                    activeIntents = AiStrategyV2Scope.ApplyIntentScope(player, activeIntents);
                    actorCommitments = ActorCommitments.FromIntents(
                        activeIntents, snapshot, reconObjectives);
                    devOpportunities = AiStrategyV2Scope.AxisInScope(DesireAxis.Development)
                        ? DevelopmentOpportunityEvaluator.Enumerate(
                            snapshot, player, root, hand, aggressionObjectives)
                        : new List<DevelopmentOpportunity>();
                    List<AxisDemand> regenerated = DemandLayer.Generate(snapshot, assessment.Breakdown,
                        reconObjectives, aggressionObjectives, activeIntents,
                        actorCommitments, player, ctx, root, devOpportunities, radar,
                        dirtyAxes);
                    regenerated = AiStrategyV2Scope.ApplyDemandScope(regenerated);
                    List<AxisDemand> dirtyDemands = regenerated;
                    demands = demands.Where(d => d != null
                            && !dirtyAxes.Contains(d.RequestingAxis))
                        .Concat(regenerated).ToList();
                    if (dirtyAxes.Contains(DesireAxis.Economy)
                        && !dirtyDemands.Any(d => d.RequestingAxis == DesireAxis.Economy
                            && (d.Capability == CapabilityKind.EconomicInfrastructure
                                || d.Capability == CapabilityKind.EconomicExpansionBase)))
                        InfrastructureFulfillment.ClearDeferredEconomyResources(
                            player, ctx.TurnNumber);

                    WorldAnalysis.StepObservationStamp beforeCapabilities =
                        WorldAnalysis.CaptureStepObservation(root, hand, snapshot);
                    StrategicPhaseResult followup = StrategicManager.FulfillDemands(
                        snapshot, player, root, hand, ctx, apLedger, dirtyDemands,
                        actorCommitments, activeIntents, reconObjectives,
                        phaseB.Reservation ?? phaseA.Reservation);
                    phaseA.Accumulate(followup);
                    if (followup.StateChanged)
                    {
                        snapshot = WorldAnalysis.RefreshStrategicKnowledge(
                            snapshot, player, root, hand, ctx);
                        reconObjectives = ReconObjectiveEvaluator.Enumerate(snapshot);
                        activeIntents = MissionContinuityLayer.ResolveActive(
                            player, snapshot, reconObjectives);
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
                    AiDebugLog.Write("[AI][V2][Loop] begin — typed operational admission");

                    while (settledSteps < AiConfigV2.maxMidTurnStepsPerTurn
                        && noProgressCycles < AiConfigV2.maxMidTurnNoProgressCycles)
                    {
                    // Every admission reads a settled world. Strategic observations are refreshed
                    // here. The radar frame stays stable for this turn; typed Development facts
                    // re-enter the existing manager immediately after the settled task boundary.
                    if (!ownershipFreshAfterPhaseA)
                    {
                        snapshot = WorldAnalysis.RefreshStrategicKnowledge(snapshot, player, root, hand, ctx);
                        reconObjectives = ReconObjectiveEvaluator.Enumerate(snapshot);
                    }
                    aggressionObjectives = AiStrategyV2Scope.AxisInScope(DesireAxis.Aggression)
                        ? AggressionObjectiveEvaluator.Enumerate(
                            snapshot, assessment.Breakdown.OpportunityReport)
                        : new List<AggressionObjective>();
                    if (!ownershipFreshAfterPhaseA)
                    {
                        activeIntents = MissionContinuityLayer.ResolveActive(player, snapshot, reconObjectives);
                        activeIntents = AiStrategyV2Scope.ApplyIntentScope(player, activeIntents);
                        actorCommitments = ActorCommitments.FromIntents(
                            activeIntents, snapshot, reconObjectives);
                    }
                    ownershipFreshAfterPhaseA = false;
                    // Demand families persist across settled admissions. Only
                    // ReenterStrategicAxes replaces dirty families after a factual invalidation.

                    missions = BuildMissionSet(snapshot, assessment.Breakdown, activeIntents,
                        reconObjectives, aggressionObjectives, radar, demands, trace);
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

                    // Lifecycle safety is admitted before strategic progress, but its must-return
                    // predicate remains owned by ReconAirExecutor. Exactly one airborne action is
                    // settled, observed and then re-admitted like every other step.
                    List<ArmyData> recoveries =
                        ReconAirExecutor.FindMandatoryRecoveryActors(player, ctx);
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
                        noProgressCycles = recoveryProgress ? 0 : noProgressCycles + 1;
                        TakeTypedTriggers(out StrategicInvalidationReason recoveryOperationalReasons,
                            out StrategicInvalidationReason recoveryStrategicReasons,
                            out HashSet<DesireAxis> recoveryDirtyAxes);
                        bool recoveryStrategicChanged = ReenterStrategicAxes(
                            recoveryStrategicReasons, recoveryDirtyAxes);
                        StrategicInvalidation recoveryFollowupTriggers =
                            StrategicInterruptRegistry.Consume(player, ctx.TurnNumber,
                                DesireAxes.InvalidationMaskFor(DesireAxis.Recon));
                        recoveryOperationalReasons |= recoveryFollowupTriggers.Reasons;
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
                        AiDebugLog.Write("[AI][V2][Loop] stop — no funded typed mission");
                        break;
                    }

                    ProvisionedMission selected = null;
                    StableMissionKey selectedKey = default;
                    var attemptedKeys = new HashSet<StableMissionKey>();
                    int reallocPass = 0;
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
                                AiDebugLog.Write($"[AI][V2][Loop] assignment-batch "
                                    + $"[{failedFunding.Mission.AttemptId}] {failedKey} — FAIL "
                                    + $"{failure.Kind} [{failure.Disposition}] {failure.Detail}");
                            }

                            List<FundedEntry> openScouts = allocation.Funded.Where(fe =>
                                fe?.Mission?.Kind == MissionKind.Scout
                                && !cycleProvisioning.AlreadyProvisioned(
                                    StableMissionKey.For(fe.Mission))).ToList();
                            bool scoutPoolExhausted = openScouts.Count > 0
                                && openScouts.All(fe => scoutFailures.Any(f =>
                                    StableMissionKey.For(f.Funded.Mission).Equals(
                                        StableMissionKey.For(fe.Mission))));
                            if (scoutPoolExhausted)
                                foreach (CapabilityPoolKind pool in openScouts
                                             .Select(fe => CapabilityPoolExhaustionRegistry.PoolFor(fe.Mission))
                                             .Where(p => p != CapabilityPoolKind.None).Distinct())
                                    CapabilityPoolExhaustionRegistry.MarkExhausted(player, pool,
                                        $"assignment batch rejected all {openScouts.Count} funded Scout mission(s)");

                            // One batch means one re-pack. The allocator now sees every impossible
                            // Scout at once, so released AP can admit Economy/Development immediately.
                            if (cycleSession.HasNewFailures && !cycleSession.Converged
                                && reallocPass < AiConfigV2.maxReallocIterations)
                            {
                                reallocPass++;
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
                        AiDebugLog.Write($"[AI][V2][Loop] provision [{selectedFunding.Mission.AttemptId}] "
                            + $"{selectedKey} — FAIL {provisionResult.Failure.Kind} "
                            + $"[{provisionResult.Failure.Disposition}] {provisionResult.Failure.Detail}");

                        if (poolWide || !cycleSession.HasNewFailures || cycleSession.Converged
                            || ++reallocPass >= AiConfigV2.maxReallocIterations)
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

                    settledSteps++;
                    bool progressed = stepResults.Any(er =>
                        er != null && er.Outcome.StateChanged);
                    TakeTypedTriggers(out StrategicInvalidationReason operationalReasons,
                        out StrategicInvalidationReason strategicReasons,
                        out HashSet<DesireAxis> dirtyStrategicAxes);
                    bool strategicChanged = ReenterStrategicAxes(
                        strategicReasons, dirtyStrategicAxes);
                    progressed |= strategicChanged;
                    noProgressCycles = progressed ? 0 : noProgressCycles + 1;
                    StrategicInvalidation followupOperationalTriggers =
                        StrategicInterruptRegistry.Consume(player, ctx.TurnNumber,
                            DesireAxes.InvalidationMaskFor(DesireAxis.Recon));
                    operationalReasons |= followupOperationalTriggers.Reasons;
                    AiDebugLog.Write($"[AI][V2][Loop] step={settledSteps} task={selectedKey} "
                        + $"progress={(progressed ? 1 : 0)} stop={settled?.StopReason} "
                        + $"operationalTriggers={operationalReasons} strategicTriggers={strategicReasons} "
                        + $"noProgress={noProgressCycles}");
                    if (operationalReasons == StrategicInvalidationReason.None && !strategicChanged)
                    {
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
                    operationalDirty |= StrategicInterruptRegistry.Consume(
                        player, ctx.TurnNumber,
                        DesireAxes.InvalidationMaskFor(DesireAxis.Recon)).Any;

                    AiDebugLog.Write($"[AI][V2][Loop] management round={managementRound + 1} "
                        + $"strategicTriggers={strategicReasons} "
                        + $"operationalTriggers={operationalReasons} "
                        + $"operationalReadmit={(operationalDirty ? 1 : 0)}");

                    if (operationalDirty)
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
                    activeIntents, reconObjectives, aggressionObjectives, radar, demands, trace);
    
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
                            session.RegisterProvisionSuccess(fe, result.Provisioned.ClaimedAp, result.Provisioned.ClaimedPhysical);
                            ledger.RecordProvisionSuccess(fe.Mission, result.Provisioned);
                            provisioned.Add(result.Provisioned);
                            AiV2Trace.CheckProvisionEnvelope(fe.Mission.AttemptId,
                                result.Provisioned.ClaimedAp, fe.Tentative.Ap);
                            AiDebugLog.Write($"[AI][V2]   provision [{fe.Mission.AttemptId}] {key} — OK mover #{result.Provisioned.MoverArmyId} "
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
                            AiDebugLog.Write($"[AI][V2]   provision [{fe.Mission.AttemptId}] {key} — FAIL {result.Failure.Kind} "
                                + $"[{result.Failure.Disposition}] {result.Failure.Detail}");
                        }
                    }
    
                    if (anyFailure && allFailuresArePoolWide)
                    {
                        AiDebugLog.Write("[AI][V2] provision — every funded mission's capability pool is exhausted this turn; stop key-by-key reallocation");
                        break;
                    }
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
                    snapshot = WorldAnalysis.RefreshOperationalState(snapshot, player, root, hand, ctx);
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
                snapshot = WorldAnalysis.RefreshOperationalState(snapshot, player, root, hand, ctx);

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
            IReadOnlyList<AxisDemand> demands, V2TraceScope trace)
        {
            List<MissionProposal> missions = ReconMissionPlanner.Propose(snapshot, breakdown,
                activeIntents, reconObjectives);
            if (AiStrategyV2Scope.AxisInScope(DesireAxis.Aggression))
                missions.AddRange(AggressionMissionLayer.Propose(snapshot, breakdown,
                    activeIntents, aggressionObjectives));
            if (AiStrategyV2Scope.AxisInScope(DesireAxis.Economy))
                missions.AddRange(EconomyMissionPlanner.Propose(snapshot, breakdown,
                    activeIntents, demands));
            missions = AiStrategyV2Scope.ApplyMissionScope(missions);

            foreach (MissionProposal m in missions)
                if (m != null && string.IsNullOrEmpty(m.AttemptId))
                    m.AttemptId = trace?.NextMissionAttemptId() ?? "?";
            foreach (MissionProposal m in missions)
                if (m != null)
                    m.EffectiveValue = m.BaseValue * RadarValueScale.For(radar, m);

            AiV2Trace.CorrelateDemandsToMissions(demands, missions);
            foreach (MissionProposal m in missions)
            {
                MissionRequirements r = m.Requirements;
                AiDebugLog.WriteVerbose($"[AI][V2]   mission — [{m.AttemptId}] causeDemand={m.CauseDemandTrace} {m.Kind} baseValue "
                    + $"{m.BaseValue.ToString("0.0", CultureInfo.InvariantCulture)} "
                    + $"eff {m.EffectiveValue.ToString("0.0", CultureInfo.InvariantCulture)} "
                    + $"las {m.LocalAdmissionScore.ToString("0.00", CultureInfo.InvariantCulture)} "
                    + $"axes[{string.Join(",", m.Axes.Value.Select(kv => $"{DesireAxes.Abbrev(kv.Key)}={kv.Value.ToString("0.00", CultureInfo.InvariantCulture)}"))}] "
                    + $"| req ap {Fmt(r?.ApMinimum)}/{Fmt(r?.ApDesired)}/{Fmt(r?.ApMaximum)} "
                    + $"energy {Fmt(r?.EnergyMinimum)}/{Fmt(r?.EnergyDesired)}/{Fmt(r?.EnergyMaximum)} "
                    + (r != null && (r.HumanDesired > 0f || r.MaterialsDesired > 0f || r.TechDesired > 0f)
                        ? $"hmt {Fmt(r.HumanDesired)}/{Fmt(r.MaterialsDesired)}/{Fmt(r.TechDesired)} " : "")
                    + (r != null && r.RequiresArmy
                        ? $"army{(r.RequiresHero ? "+hero" : "")} cp {Fmt(r.CombatPowerMinimum)}/{Fmt(r.CombatPowerDesired)} " : "")
                    + $"eta {r?.EtaTurns} moverKnown {(r?.MoverKnown == true ? 1 : 0)}"
                    + $"{(m.PreferredMoverArmyId.HasValue ? " prefMv#" + m.PreferredMoverArmyId : "")} "
                    + $"| {m.Explain}");
            }
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

        private static string Fmt(float? v) =>
            v.HasValue ? v.Value.ToString("0.0", CultureInfo.InvariantCulture) : "-";
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
