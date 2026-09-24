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
    // Stage model types for the V2 pipeline — design record: AiStrategyV2Pipeline.cs file header.

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
        // physical pool — never radar-sliced (ApBudgetLedger stays AP-only, spec §18).
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
}
