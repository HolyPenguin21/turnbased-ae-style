using System.Collections.Generic;
using System.Linq;
using Game.Players;
using UnityEngine;

using Game.Combat;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  AGGRESSION DEMAND EVALUATOR
    // ===========================================================================================
    //  ONE canonical decision for "does the Aggression axis have a runnable capability shortage
    //  this pass, and if so which objective / which demand(s)". The whole admission contract lives
    //  here — covered-by-committed-raid, allocator cooldown, top-value objective selection,
    //  RaidOperationalReadiness, the ReadyExecutable / NeedsAssembly-DEFER / NeedsHero / NeedsPower
    //  branch, and the exact AxisDemand shapes.
    //
    //  Consumed by BOTH:
    //    · DemandLayer.AggressionDemands  — the real Phase-A pipeline (yields Demands, replays
    //      Diagnostics, attaches trace ids downstream),
    //    · StrategicReactionPass          — the reaction feasibility probe (reads ChosenObjective /
    //      Readiness / Outcome; never mirrors the rules).
    //
    //  Build is a deterministic primitive: no yield, no trace ids, no logging as a side effect.
    //  It only READS AiAllocatorStateRegistry for cooldowns. Every diagnostic line is returned in
    //  `Diagnostics` for the caller to replay verbatim.
    //
    //  Build has NO exception to "no mutation": it is a pure snapshot read even
    //  for the weakened-primary reinforcement case. The Assault -> Reinforcement phase transition
    //  belongs to MissionContinuityLayer.AdvanceRaidPhase (it already independently re-verifies the
    //  primary's state every reconciliation pass); the RaidIntent.ReinforcementRequestedTurn dedup
    //  stamp — the "exactly one support intent per weakened primary" invariant — is written only
    //  once a materialization for this exact ConsumerIntentKey is actually accepted/funded
    //  (CapabilityDeliveryEvaluator.TryHandoffRaidSupport). Build only READS that stamp to decide
    //  whether a demand it is about to (re-)propose was already requested this turn.
    // ===========================================================================================

    public enum AggressionDemandOutcome
    {
        None,             // no self snapshot / no objectives / no runnable capability shortage
        Ready,            // the selected objective is already ReadyExecutable — no demand (unreached here; folded into None)
        AssemblyDeferred, // real shortage is STRUCTURAL — the pipeline DEFERs, buying power would not help
        Demand,           // Demands is non-empty (Hero and/or FieldCombatPower)
    }

    public sealed class AggressionDemandEvaluation
    {
        public RaidObjective ChosenObjective;
        public RaidOperationalReadiness Readiness;
        public IReadOnlyList<AxisDemand> Demands = System.Array.Empty<AxisDemand>();
        public AggressionDemandOutcome Outcome = AggressionDemandOutcome.None;
        public string Reason = "";
        public int BlockedByCooldown;
        // Every non-covered / non-cooldown discovered-or-not objective whose canonical
        // RaidOperationalReadiness is ReadyExecutable RIGHT NOW, with the ready GroundCombatAssemblyPlan
        // (its BaseArmyId is the canonical executable raid actor). The reaction direct-witness
        // probe reads this instead of a GatePassed filter + cheapest arbitrary pathable army.
        public IReadOnlyList<(RaidObjective Objective, GroundCombatAssemblyPlan Plan)> ReadyExecutable =
            System.Array.Empty<(RaidObjective, GroundCombatAssemblyPlan)>();
        // Fully-formatted "[AI][V2][Demand][Aggression] …" lines — the caller replays them through
        // AiDebugLog so Build itself performs no logging.
        public IReadOnlyList<string> Diagnostics = System.Array.Empty<string>();
    }

    public static partial class AggressionDemandEvaluator
    {

        public static AggressionDemandEvaluation Build(WorldSnapshot snap,
            IReadOnlyList<RaidObjective> objectives, IReadOnlyList<MissionIntent> activeIntents,
            ActorCommitments commitments, PlayerSetupData player) =>
            BuildRaid(snap, objectives, activeIntents, commitments, player);

    }
}

