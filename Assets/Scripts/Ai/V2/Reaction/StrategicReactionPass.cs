using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Cards;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // Bounded same-turn replanning. Round 0 consumes the ordinary strategic invalidation. A single
    // round 1 is permitted only when round 0 itself materializes new operational capability or a
    // terminal draw exposes an actionable hand. New contact discovery never recursively chains.
    public sealed class StrategicReactionResult
    {
        public bool Ran;
        public bool StateChanged;
        public int DiscoveredTargets;
        public int Demands;
        public int Missions;
        public int Provisioned;
        public int Executed;          // real attempts (superseded stale missions excluded)
        public int CardsPlayed;
        public int CardsDrawn;
        public int Rounds;
    }

    // AI-MGR-02 §7 (round 5) — a BOUNDED REACTION BUDGET backed by a REAL feasibility probe. The
    // bounded reaction round re-runs the whole Demand→Mission→Provision→Execute pipeline and picks
    // its own action, so the budget stays GENERIC (not bound to one exact actor/card). But it is
    // only created when the SAME gates the real pipeline uses prove at least one feasible reaction
    // exists, the reserved AP is >= that reaction's minimum feasible AP, and the persistent
    // H/E/M/T envelope needed to keep it feasible is reserved alongside the AP.
    internal readonly struct StrategicReactionOpportunity
    {
        public readonly bool IsActionable;
        public readonly string OwnerKey;         // reservation owner tag ("reaction-budget:<kind>")
        public readonly string Kind;             // "RespondToDiscovery" | "HandFollowup"
        public readonly float ReservedApBudget;  // BOUNDED replan budget (<= reactionReserveApCap), >= MinFeasibleAp
        public readonly ResourceCost Envelope;   // persistent H/E/M/T that must stay unspent (may be null)
        public readonly string Rationale;
        public readonly string FailReason;

        public StrategicReactionOpportunity(bool actionable, string ownerKey, string kind,
            float reservedApBudget, ResourceCost envelope, string rationale, string failReason)
        {
            IsActionable = actionable;
            OwnerKey = ownerKey;
            Kind = kind;
            ReservedApBudget = reservedApBudget;
            Envelope = envelope;
            Rationale = rationale;
            FailReason = failReason;
        }

        public static StrategicReactionOpportunity None(string failReason) =>
            new StrategicReactionOpportunity(false, null, null, 0f, null, null, failReason);
    }

    // Round 6/round 7 (P0.1/P0.2) — ONE feasible reaction the real pipeline would actually admit
    // at the current state. BuildReactionOpportunity collects EVERY witness from EVERY enabled
    // source (discovery-direct responder, discovery-materialization, hand follow-up) — never a
    // fixed `targetDriven ? A : B` branch — and reserves a bounded budget for the single cheapest
    // one. Execution is NOT bound to the witness (the reservation stays a generic replan budget) —
    // it only proves the budget protects something real.
    internal readonly struct ReactionWitness
    {
        public readonly string Kind;             // "RespondToDiscovery" | "MaterializeForDiscovery" | "HandFollowup"
        public readonly string ActionKey;        // stable deterministic tie-break key
        // round 10 (P0.1) — the ONE full AP the protected reaction actually needs, downstream/move
        // envelope INCLUDED (direct: activation + responder-move; materialization: prep + downstream;
        // hand: play AP + follow-up floor). Arbitration ranks and gates on this exact number and
        // NEVER reserves a budget below it (a clamped-below reservation does not protect the action).
        public readonly float RequiredAp;
        public readonly ResourceCost Envelope;   // persistent cost of that action (may be null)
        public readonly int WitnessActorId;      // responder army id (-1 for a hand / materialization play)
        public readonly int WitnessTargetId;     // discovered target army id (-1 for a pure hand play)
        public readonly string Detail;

        public ReactionWitness(string kind, string actionKey, float requiredAp,
            ResourceCost envelope, string detail, int witnessActorId = -1, int witnessTargetId = -1)
        {
            Kind = kind;
            ActionKey = actionKey;
            RequiredAp = requiredAp;
            Envelope = envelope;
            WitnessActorId = witnessActorId;
            WitnessTargetId = witnessTargetId;
            Detail = detail;
        }

        // Reservation owner tag — concrete per witness kind, NOT the shared Reason. P1: the
        // envelope-spendable check and the §6 re-probe exclude by THIS exact owner.
        public string OwnerKey => "reaction-budget:" + Kind;

        public float EnvelopeCost => Envelope == null ? 0f
            : Envelope.human + Envelope.energy + Envelope.materials + Envelope.tech;
    }

    // ARCH-02 §24/§25 — the reaction COORDINATOR. It owns only the lifecycle: gate -> probe
    // (ReactionOpportunityProbe) -> select witness (ReactionWitnessSelector) -> reserve (the
    // StrategicResourceReservationLedger, driven by StrategicPhaseB) -> revalidate -> execute a
    // bounded round (ReactionRoundExecutor) -> release. It does NOT score cards, build witnesses,
    // solve the materialization closure or run the round itself.
    internal static class StrategicReactionPass
    {
        // AI-MGR-02 §7 — CAN the pass run at all in this scope / with a resolvable hand.
        internal static bool CanStrategicReactionPassRun(PlayerSetupData player, AiTurnContext ctx)
        {
            if (player == null || ctx == null || ctx.Map == null)
                return false;
            // ExecuteIfPending consumes-and-suppresses the whole pass in ReconOnly scope.
            return !AiStrategyV2Scope.IsReconOnly;
        }

        // AI-MGR-02 §7 (round 5) — reserve a bounded reaction budget ONLY when a real feasibility
        // probe proves at least one genuinely feasible reaction exists, and only for an AP budget
        // >= its minimum feasible AP, plus its persistent-resource envelope. `snap` is the world
        // the probe runs against (the same one the caller will arbitrate / replan with).
        internal static StrategicReactionOpportunity BuildReactionOpportunity(PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx, WorldSnapshot snap)
        {
            if (!CanStrategicReactionPassRun(player, ctx))
                return StrategicReactionOpportunity.None("cannotRun(scope)");
            if (!StrategicInterruptRegistry.HasPending(player, ctx.TurnNumber))
                return StrategicReactionOpportunity.None("noPendingInvalidation");
            if (snap == null)
                return StrategicReactionOpportunity.None("noProbeSnapshot");

            AiHandData hand = AiHandRegistry.Peek(player);
            if (hand == null)
                StrategicInterruptRegistry.TryGetHand(player, ctx.TurnNumber, out hand);
            if (hand == null)
                return StrategicReactionOpportunity.None("noResolvableHand");

            bool targetDriven = StrategicInterruptRegistry.TargetIds(player, ctx.TurnNumber).Count > 0;
            bool followupDriven = StrategicInterruptRegistry.HasPendingFollowup(player, ctx.TurnNumber);
            if (!targetDriven && !followupDriven)
                return StrategicReactionOpportunity.None("noActionableContent");

            float apAvailable = root != null ? Mathf.Max(0f, root.ActionPoints) : 0f;
            int cap = AiConfigV2.reactionReserveApCap;
            float ceiling = Mathf.Min(apAvailable, (float)cap);

            // round 6 architectural-debt fix — the canonical normalized commitment source, not a
            // hand-rolled PreferredMoverArmyId scrape.
            ActorCommitments commitments = ActorCommitments.FromIntents(
                MissionIntentRegistry.GetOrCreate(player).All, snap, ReconObjectiveEvaluator.Enumerate(snap));

            // P0.1 — StrategicInterruptRegistry stores reasons as FLAGS, so a Discovery and a
            // HandOpportunity can both be pending at once. Probe every ENABLED source and let the
            // cheapest genuine witness win — no fixed Discovery>Hand (or Hand>Discovery) priority.
            // P0.2 — a discovered target whose aggression gate is not (yet) passed is still a real
            // reaction when the DemandLayer shortage it raises has a legal Phase-A materialization;
            // that is exactly what Phase A exists for (Raid accepted -> GatePassed=false because
            // power is short now -> DemandLayer asks for power -> Phase A plays a Unit -> Refresh
            // -> Raid executable). ProbeMaterializationForDiscovery composes the real primitives
            // (AggressionObjectiveEvaluator + RaidOperationalReadiness + the surplus materialization
            // enumerator), not a second planner.
            var witnesses = new List<ReactionWitness>();
            if (targetDriven)
            {
                // ONE canonical aggression evaluation, shared by both discovery probes (and the same
                // primitive DemandLayer.AggressionDemands uses). Stateless: CombatOpportunityAnalyzer
                // .Analyze does not mutate the radar; AggressionDemandEvaluator.Build only reads.
                var activeIntents = MissionIntentRegistry.GetOrCreate(player).All
                    .Where(i => i != null && i.Status == IntentStatus.Active).ToList();
                AggressionDemandEvaluation aggEval = AggressionDemandEvaluator.Build(snap,
                    AggressionObjectiveEvaluator.Enumerate(snap, CombatOpportunityAnalyzer.Analyze(snap)),
                    activeIntents, commitments, player);
                witnesses.AddRange(ReactionOpportunityProbe.ProbeTargetDriven(player, ctx, aggEval));
                witnesses.AddRange(ReactionOpportunityProbe.ProbeMaterializationForDiscovery(
                    player, root, ctx, snap, hand, commitments, aggEval));
            }
            if (followupDriven)
                witnesses.AddRange(ReactionOpportunityProbe.ProbeHandFollowup(player, root, ctx, snap, hand, commitments));

            return ReactionWitnessSelector.Select(witnesses, ceiling, player, root, ctx);
        }

        // §6 — before the bounded reaction round runs, a genuinely feasible reaction must STILL
        // exist (the same probe, not a weaker one). If not, its budget + envelope reservation is
        // released so the resources re-enter arbitration this turn (Housekeeping's tempo re-run).
        internal static bool ReactionStillActionable(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, WorldSnapshot snap)
            => BuildReactionOpportunity(player, root, ctx, snap).IsActionable;

        public static IEnumerator ExecuteIfPending(WorldSnapshot priorSnapshot, PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx, StrategicReactionResult result)
        {
            // ReconOnly isolates the current deep-rework from the legacy strategic reaction loop.
            // The live Recon executor will own ordinary step->refresh->reaction; until that lands,
            // do not let a contact discovery reopen Aggression/Defence/Economy/Development through
            // this second orchestration path. Consume the turn-scoped invalidation so it cannot
            // leak into the next turn.
            if (AiStrategyV2Scope.IsReconOnly)
            {
                if (player != null && ctx != null && StrategicInterruptRegistry.HasPending(player, ctx.TurnNumber))
                {
                    StrategicInterruptRegistry.Clear(player, ctx.TurnNumber);
                    AiDebugLog.Write("[AI][V2][Scope] strategic reaction pass suppressed reason=ReconOnly");
                }
                // AI-MGR-02 §4 — a scope-suppressed pass deliberately leaves any AP reservation in
                // place: HousekeepingManager releases it and re-runs end-of-turn tempo spending with
                // the freed AP the same turn (so it is not stranded).
                yield break;
            }

            yield return ReactionRoundExecutor.ExecuteRound(priorSnapshot, player, root, ctx,
                result ?? new StrategicReactionResult(), 0);

            // AI-MGR-02 §4 — the pass has had its bounded round(s); any AP Phase B reserved for it
            // is now free (its own inner Phase B call already spent whatever it wanted).
            if (player != null && ctx != null)
                StrategicResourceReservationLedger.ExpireStage(player, ctx.TurnNumber,
                    StrategicReservationExpiry.EndOfReaction);
        }
    }
}
