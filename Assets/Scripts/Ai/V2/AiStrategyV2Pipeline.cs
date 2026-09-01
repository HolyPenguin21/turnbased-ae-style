using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.HexGrid;
using Game.Map;
using Game.Players;

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

    // --- How much each axis a single mission serves. MANY-TO-MANY (risk 1): never collapse to one
    //     category. Values are 0..1 "relevance", not required to sum to anything.
    public sealed class AxisContribution
    {
        public readonly Dictionary<DesireAxis, float> Value = new Dictionary<DesireAxis, float>();
    }

    // Concrete mission kinds. Each maps to a V2 Task builder in TaskExecutor. Was a bare string
    // until build-order step 4 — typed now, before anything downstream depends on the spelling.
    public enum MissionKind { Scout, Raid }

    // A Scout mission's focus. Explore -> a MapKnowledge.Frontier hex; Surveil -> a stale honest
    // contact's last-known hex (Contact non-null). No IMissionTarget hierarchy yet — MissionProposal
    // still boxes this into Target as object; the cast lives in one place (TaskExecutor / ScoutCostModel).
    public enum ScoutTargetKind { Explore, Surveil }

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
        public MissionKind Kind;
        public object Target;               // boxed ScoutMissionTarget for Scout; typed per-kind
        public float BaseValue;             // shared 0..100 scale across ALL mission kinds — INTRINSIC merit
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
        public static IEnumerator RunTurn(PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx)
        {
            AiDebugLog.Write($"[AI][V2] === {player?.Nickname} — Strategy V2 pipeline owns this turn "
                + $"(turn {ctx?.TurnNumber}) ===");

            if (player == null || root == null || ctx == null || ctx.Map == null)
            {
                AiDebugLog.Write("[AI][V2] missing player/root/ctx/map — nothing to do.");
                yield break;
            }

            // Turn-scoped activity record (main vs reaction vs total). Reset here so a stale
            // Reaction bucket from last turn can never leak into this turn's Total.
            V2TurnActivityTelemetry.Begin(player, ctx.TurnNumber);
            CapabilityPoolExhaustionRegistry.BeginTurn(player, ctx.TurnNumber);

            // Initiative AP telemetry — captured now (turn start) and written back at turn end.
            // Belongs EXCLUSIVELY to Game.Ai.V2.Initiative analysis; nothing else in this pipeline
            // reads it (see InitiativeAnalyticsHistory).
            int initiativeStartAp = root.ActionPoints;
            int initiativeBaseAp = root.LastApFromInitiative;
            int initiativeActionableAtStart =
                Game.Ai.V2.Initiative.PreTurnCapacityAnalysis.CountActionableFieldArmies(player, unactivatedOnly: false);

            // 2. One shared scan.
            WorldSnapshot snapshot = WorldAnalysis.Scan(player, root, hand, ctx);

            // 3. Strategy: independent raw desires -> normalize once -> radar. StrategyLayer writes
            //    its own detailed "[AI][V2]   desires — ..." trace; the line below is the summary.
            AiRadarState radarState = AiRadarStateRegistry.GetOrCreate(player);
            RadarAssessment assessment = StrategyLayer.Evaluate(snapshot, radarState);
            DesireVector desires = assessment.Desires;
            Radar radar = assessment.Radar;
            AiDebugLog.Write($"[AI][V2] {player.Nickname}: radar — {radar.DebugLine()} "
                + $"| threat {desires.MilitaryThreat.ToString("0.00", CultureInfo.InvariantCulture)} "
                + $"runway {desires.EconomicRunway.ToString("0.00", CultureInfo.InvariantCulture)}");

            // 3c. The ONE Recon-opportunity enumeration for the turn — shared by DemandLayer and
            //     MissionLayer. FROZEN here (before StrategicManager touches own forces): Strategic
            //     Manager changes which SCOUT can execute, never which objectives exist.
            List<ReconObjective> reconObjectives = ReconObjectiveEvaluator.Enumerate(snapshot);

            // 3d. The ONE Aggression-opportunity enumeration for the turn — shared by DemandLayer
            //     and AggressionMissionLayer (build-order step 9). FROZEN here alongside the Recon
            //     objectives, from the SAME shared CombatOpportunityReport the radar already
            //     computed. Strategic Manager changes which FORCE can raid, never which strategic
            //     targets exist — so this list is NOT recomputed after the operational refresh.
            List<AggressionObjective> aggressionObjectives =
                AggressionObjectiveEvaluator.Enumerate(snapshot, assessment.Breakdown.OpportunityReport);
            foreach (AggressionObjective ao in aggressionObjectives)
                AiDebugLog.Write($"[AI][V2]   aggObjective — {ao.ObjectiveId} @{ao.LastKnownHex.Q},{ao.LastKnownHex.R} "
                    + $"base {ao.BaseValue.ToString("0.0", CultureInfo.InvariantCulture)} "
                    + $"readyWin {ao.ReadyWinChance.ToString("0.00", CultureInfo.InvariantCulture)} "
                    + $"asmWin {ao.AssemblableWinChance.ToString("0.00", CultureInfo.InvariantCulture)} "
                    + $"def {ao.DefenderCount} gate {(ao.GatePassed ? 1 : 0)}"
                    + $"{(ao.NeedsCombatPower ? " needsPower" : "")}{(ao.NeedsHero ? " needsHero" : "")}");

            // 7a. Mission Continuity — resolve the durable in-flight intents FIRST, so the planner
            //     can re-materialise them from this snapshot (one place still owns proposal
            //     creation) and retarget hysteresis holds a multi-turn chain steady through Radar
            //     noise. Purges dead intents, suspends Soft funding under siege.
            List<MissionIntent> activeIntents = MissionContinuityLayer.ResolveActive(player, snapshot);
            // Normalized "which of my armies are already committed to an operation" view — so
            // DemandLayer / CapabilityInventory / ReusableArmySelector can tell an EXISTING scout
            // from an AVAILABLE one without knowing how continuity stores mover ownership.
            ActorCommitments actorCommitments = ActorCommitments.FromIntents(activeIntents, snapshot, reconObjectives);

            // S1. Demand Layer — capability SHORTAGES (no card selection). Axes say what is missing.
            List<AxisDemand> demands = DemandLayer.Generate(snapshot, assessment.Breakdown,
                reconObjectives, aggressionObjectives, activeIntents, actorCommitments, player);

            // S2. The ONE per-turn AP entitlement split: allocatable AP (real AP minus the
            //     HousekeepingManager reserve) sliced by the 5-axis radar. Strategic Manager Phase A
            //     debits the requesting axis here; the mission allocator then seeds its slices from
            //     this same ledger — NO second radar split.
            AxisBudgetLedger apLedger = AxisBudgetLedger.Create(snapshot.Self?.ActionPoints ?? 0, radar);
            AiDebugLog.Write($"[AI][V2] {player.Nickname}: budget ledger — {apLedger.DebugLine()}");

            // S3. Strategic Manager Phase A — demand-driven card play, before mission planning.
            //     Costs are charged to demand.RequestingAxis (no Management co-pay — Strategic
            //     Manager is a service, not an axis).
            StrategicPhaseResult phaseA = StrategicManager.FulfillDemands(snapshot, player, root, hand,
                ctx, apLedger, demands, actorCommitments);

            // S4. Operational self-state refresh — ONLY if StrategicManager changed gameplay state
            //     (a partial CreateArmy + failed deploy still counts). Rebuilds Self + Economy;
            //     keeps the frozen strategic observations (Known / TrueWorld / MapKnowledge / Threat
            //     / radar / breakdown / reconObjectives).
            if (phaseA.StateChanged)
                snapshot = WorldAnalysis.RefreshOperationalState(snapshot, player, root, hand, ctx);

            // 4. Planners -> mission proposals (+ requirements via the shared estimator). Reads the
            //    DesireBreakdown + the FROZEN Recon objectives, never re-derives the analysis behind
            //    them. Also materialises every active intent and applies the retarget margin.
            List<MissionProposal> missions = MissionLayer.Propose(snapshot, assessment.Breakdown,
                activeIntents, reconObjectives);
            // Step 9 — the Aggression lane. Same FROZEN objective set the Demand layer read; a
            // Raid candidate beam concatenated onto the Recon beam. The allocator's k-way merge
            // interleaves the two lanes by BaseValue.
            missions.AddRange(AggressionMissionLayer.Propose(snapshot, assessment.Breakdown,
                activeIntents, aggressionObjectives));
            foreach (MissionProposal m in missions)
            {
                MissionRequirements r = m.Requirements;
                AiDebugLog.Write($"[AI][V2]   mission — {m.Kind} baseValue "
                    + $"{m.BaseValue.ToString("0.0", CultureInfo.InvariantCulture)} "
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

            // 7b. Bind a funding policy to each Soft/Hard intent by matching it to its fresh
            //     proposal. The allocator sees these as pre-funded, sticky, drawn before fresh
            //     decisions — but never conjuring AP past the real pool.
            List<Commitment> commitments = MissionContinuityLayer.BindFunding(activeIntents, missions);

            var ledger = new MissionOutcomeLedger();
            ledger.RegisterProposals(missions);
            ledger.RegisterCommitments(commitments);

            // 5. Slices seeded from the SHARED AP ledger (net of Phase-A demand spend) -> many-to-
            //    many packing -> ordered tentative allocation. No second radar split.
            AllocationSession session = ResourceAllocator.BeginTurn(snapshot, radar, missions, commitments, player, apLedger);
            var provSession = new ProvisioningSession(snapshot);
            TentativeAllocation allocation = session.Pack();

            // 6. Provision the funded missions through the ONE atomic door, with the bounded
            //    pack -> provision -> re-pack loop (risk 2). Mover assignment across the funded set
            //    is a per-pass batch step (PreparePass) so a single Provision() call carries no
            //    hidden cross-mission responsibility. Re-pack is bounded by maxReallocIterations +
            //    the AllocationSession's own rejected/cooldown/repriced/fingerprint state.
            var provisioned = new List<ProvisionedMission>();
            int reallocPass = 0;
            while (true)
            {
                ProvisioningManager.PreparePass(player, root, ctx, provSession, allocation);
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
                    if (!CapabilityPoolExhaustionRegistry.RevalidateAndClearIfRecovered(player,
                            CapabilityPoolExhaustionRegistry.PoolFor(fe.Mission), snapshot))
                        continue;

                    ProvisioningResult result = ProvisioningManager.Provision(player, root, hand, ctx, provSession, fe);
                    if (result.Success)
                    {
                        provSession.RegisterSuccess(key, result.Provisioned);
                        session.RegisterProvisionSuccess(fe, result.Provisioned.ClaimedAp);
                        ledger.RecordProvisionSuccess(fe.Mission, result.Provisioned);
                        provisioned.Add(result.Provisioned);
                        AiDebugLog.Write($"[AI][V2]   provision {key} — OK mover #{result.Provisioned.MoverArmyId} "
                            + $"ap {result.Provisioned.ClaimedAp.ToString("0.#", CultureInfo.InvariantCulture)} "
                            + $"(envelope {fe.Tentative.Ap.ToString("0.#", CultureInfo.InvariantCulture)}) "
                            + $"stealthReserve {(result.Provisioned.StealthApReserved ? 1 : 0)}");
                    }
                    else
                    {
                        anyFailure = true;
                        bool poolWide = CapabilityPoolExhaustionRegistry.ProvenPoolWideUnable(
                            snapshot, player, fe.Mission, result.Failure);
                        if (poolWide)
                            CapabilityPoolExhaustionRegistry.MarkExhausted(player,
                                CapabilityPoolExhaustionRegistry.PoolFor(fe.Mission),
                                $"{result.Failure.Kind}: no eligible actor in snapshot");
                        allFailuresArePoolWide &= poolWide;
                        session.RegisterProvisionFailure(fe, result.Failure);
                        ledger.RecordProvisionFailure(fe.Mission, result.Failure);
                        AiDebugLog.Write($"[AI][V2]   provision {key} — FAIL {result.Failure.Kind} "
                            + $"[{result.Failure.Disposition}] {result.Failure.Detail}");
                    }
                }

                if (anyFailure && allFailuresArePoolWide)
                {
                    AiDebugLog.Write("[AI][V2] provision — every funded mission's capability pool is exhausted this turn; stop key-by-key reallocation");
                    break;
                }
                if (!session.HasNewFailures || session.Converged || ++reallocPass >= AiConfigV2.maxReallocIterations)
                    break;
                allocation = session.Pack();
            }

            // 6b. Tasks -> per-hex execution on the real map (reuses AiTurnController.MoveArmyRoutine).
            //     Hand the executor every Explore proposal's focus from this pass — funded, deferred
            //     or unrouted alike. The ledger rowed all of them (RegisterProposals above), so the
            //     bounded stale-Explore replacement must steer clear of the whole set, not just the
            //     foci that reached the execution queue, or a synthesised key could collide with a
            //     deferred proposal's row.
            var exploreProposalFoci = new HashSet<HexCoord>();
            foreach (MissionProposal m in missions)
                if (m?.Kind == MissionKind.Scout && m.Target is ScoutMissionTarget smt
                    && smt.Kind == ScoutTargetKind.Explore)
                    exploreProposalFoci.Add(smt.FocusHex);

            var executed = new List<ExecutionResult>();
            yield return TaskExecutor.Execute(player, root, ctx, provisioned, executed, snapshot, exploreProposalFoci);
            foreach (ExecutionResult er in executed)
            {
                // A synthesised replacement's proposal was never in the pre-execution
                // RegisterProposals set — register it here so continuity/reconciliation sees the
                // new Explore too (not only telemetry). Its fresh StableMissionKey keeps it
                // distinct from the superseded mission.
                if (er.IsReplacement && er.Source?.Mission != null)
                {
                    ledger.RegisterProposals(new[] { er.Source.Mission });
                    ledger.RecordProvisionSuccess(er.Source.Mission, er.Source);
                }
                ledger.RecordExecution(er);
            }
            ledger.RecordDeferrals(allocation.Deferred);
            // Post-execution LIVE pass — a mission run later this turn may have met an earlier
            // Surveil's objective. The ONLY live-world read on the continuity path, isolated in the
            // ledger via ScoutObjectiveEvaluator; ReconcileAfterTurn below stays pure.
            ledger.RefreshObjectiveStatesLive(player);

            // 7c. Update durable intent state for next turn — a PURE transition over the ledger's
            //     facts (no world reads). Creates intents for started-but-unfinished recon,
            //     advances/retires the rest, keeps a preferred mover.
            MissionContinuityLayer.ReconcileAfterTurn(player, snapshot.TurnNumber, ledger.Finalize());

            // S5. Strategic Manager Phase B — Surplus Preparation. The snapshot is still
            //     beginning-of-turn own-state at this point (missions have executed since), so
            //     refresh it FIRST; then rebuild actor ownership from the RECONCILED registry (not
            //     beginning-of-turn claims). Phase B then spends GENUINELY remaining real
            //     AP/resources on proactive card play + hand cycling. No radar slice; bounded by
            //     maxSurplusActionsPerTurn; every configured reserve respected.
            snapshot = WorldAnalysis.RefreshOperationalState(snapshot, player, root, hand, ctx);
            ActorCommitments postCommitments =
                ActorCommitments.FromIntents(MissionIntentRegistry.GetOrCreate(player).All, snapshot, reconObjectives);
            StrategicPhaseResult phaseB = StrategicManager.UseSurplus(snapshot, player, root, hand, ctx,
                postCommitments, phaseA.Reservation);
            if (phaseB.StateChanged)
                snapshot = WorldAnalysis.RefreshOperationalState(snapshot, player, root, hand, ctx);

            // 8. Off-budget housekeeping — NOT an axis, guaranteed minimum, cannot be out-competed.
            //    The LAST mutating AI layer: deterministic, same-hex army/garrison REORGANISATION
            //    (build-order step 8C). Reads the just-refreshed snapshot + the post-Phase-B actor
            //    ownership; a successful mutation triggers one final operational refresh so the
            //    saved end-turn state matches the real world.
            var housekeeping = new HousekeepingResult();
            yield return HousekeepingManager.RunHousekeeping(snapshot, player, root, ctx, postCommitments, housekeeping);
            if (housekeeping.StateChanged)
                snapshot = WorldAnalysis.RefreshOperationalState(snapshot, player, root, hand, ctx);

            // --- Main-phase activity bucket. DERIVED once, here, from this pipeline's own facts —
            //     never incremented inside a nested layer (spec §11). The Reaction bucket is owned
            //     by StrategicReactionPass the same way. Total = Main + Reaction, no double count.
            V2PhaseActivity main = V2TurnActivityTelemetry.Phase(player, ctx.TurnNumber, V2Phase.Main);
            main.DemandsRaised = demands.Count;
            main.MissionsConsidered = missions.Count;
            main.MissionsFunded = allocation.Funded.Count;
            main.Provisioned = provisioned.Count;
            main.ExecutionAttempts = executed.Count(MissionRevalidator.WasAttempt);
            main.ExecutionsSucceeded = executed.Count(MissionRevalidator.WasGenuineExecution);
            main.ExecutionsStaleOrSkipped = executed.Count(MissionRevalidator.WasStaleOrSkipped);
            main.ReplacementMissions = executed.Count(MissionRevalidator.WasReplacement);
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
                + $"funded {allocation.Funded.Count}, provisioned {provisioned.Count}, "
                + $"executed {executed.Count}, stratB {phaseB.CardsPlayed}) ===");
            V2TurnActivityTelemetry.LogSummary(player, ctx.TurnNumber);

            RecordInitiativeAnalytics(player, root, hand, initiativeStartAp, initiativeBaseAp, initiativeActionableAtStart);
            yield return null;
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
                    int ap = c != null ? AiCardCost.PlayAp(c) : 0;
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
