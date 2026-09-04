using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Cards;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // ARCH-02 §24 — the reaction opportunity probe: builds ReactionWitness feasibility evidence
    // from every enabled source (direct discovery responder, discovery materialization closure,
    // hand follow-up). It proves a reaction is genuinely admissible at the current state; it does
    // not rank or reserve (ReactionWitnessSelector / the coordinator do that). Bodies are verbatim
    // from the former StrategicReactionPass.
    internal static class ReactionOpportunityProbe
    {
        // round 9 (P0.1) — a DIRECT-responder reaction witness is built ONLY from a discovered target
        // whose canonical RaidOperationalReadiness is ReadyExecutable right now (no GatePassed
        // filter — GatePassed is a frozen strategic projection, not the live admission gate). The AP
        // envelope is the ready RaidAssemblyPlan's own actor (ReadyPlan.BaseArmyId), NOT the cheapest
        // arbitrary pathable army — the cheapest pathable army may not be the one that clears
        // RaidAssemblyPlanner, which under-reserved the budget.
        internal static List<ReactionWitness> ProbeTargetDriven(PlayerSetupData player, AiTurnContext ctx,
            AggressionDemandEvaluation eval, ReactionStateBasis basis)
        {
            var witnesses = new List<ReactionWitness>();
            HashSet<int> targetIds = StrategicInterruptRegistry.TargetIds(player, ctx.TurnNumber);

            foreach ((AggressionObjective obj, RaidAssemblyPlan plan) in eval.ReadyExecutable)
            {
                if (obj == null || plan == null || !targetIds.Contains(obj.TargetArmyId))
                    continue;
                ArmyData actor = ArmyRegistry.AllForOwner(player)
                    .FirstOrDefault(a => a != null && a.Id == plan.BaseArmyId);
                float activation = actor == null || actor.HasActivatedThisTurn ? 0f : actor.ActivationApCost;
                // P0.1 — RequiredAp is the FULL cost of the protected reaction: the ready actor's
                // activation PLUS the downstream move envelope. Arbitration reserves this exact
                // number or drops the witness — never a clamped-below "budget".
                float requiredAp = activation + Mathf.Max(0f, AiConfigV2.reactionResponderMoveApEstimate);
                witnesses.Add(new ReactionWitness("RespondToDiscovery",
                    $"discovery:direct:{plan.BaseArmyId}->{obj.TargetArmyId}", requiredAp, null,
                    $"targetDriven witness: canonical ready raid actor #{plan.BaseArmyId} -> discovered "
                    + $"target #{obj.TargetArmyId} (win {plan.ProjectedWinChance:0.00}, cover "
                    + $"{(plan.CoversAllDefenders ? 1 : 0)}); activation {activation:0.#} + move "
                    + $"{Mathf.Max(0f, AiConfigV2.reactionResponderMoveApEstimate):0.#} = {requiredAp:0.#} AP",
                    basis, plan.BaseArmyId, obj.TargetArmyId));
            }
            if (witnesses.Count == 0)
                AiDebugLog.Write("[AI][V2] reaction probe(targetDriven) — no discovered target is "
                    + "ReadyExecutable per the canonical RaidOperationalReadiness");
            return witnesses;
        }

        // A discovered target that has a canonical Hero / FieldCombatPower shortage (from the shared
        // AggressionDemandEvaluator — SAME primitive DemandLayer.AggressionDemands uses, no mirrored
        // admission rules) is a real reaction ONLY if ProjectMaterializationClosure proves a BOUNDED
        // combination of legal materialization actions FULLY closes that shortage (round 8/9 P0),
        // never counting one physical card twice (round 9 P0.2) and measuring contribution as the
        // projected RaidAvailableFieldPower delta, not raw Σ BasePower (round 9 P0.3).
        internal static List<ReactionWitness> ProbeMaterializationForDiscovery(PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx, WorldSnapshot snap, AiHandData hand,
            ActorCommitments commitments, AggressionDemandEvaluation eval, ReactionStateBasis basis)
        {
            var witnesses = new List<ReactionWitness>();
            HashSet<int> targetIds = StrategicInterruptRegistry.TargetIds(player, ctx.TurnNumber);

            if (eval.Outcome != AggressionDemandOutcome.Demand || eval.ChosenObjective == null
                || eval.Readiness == null)
            {
                AiDebugLog.Write($"[AI][V2] reaction probe(materializeForDiscovery) — canonical aggression demand "
                    + $"outcome={eval.Outcome} ({eval.Reason}); no materialization reaction");
                return witnesses;
            }
            if (!targetIds.Contains(eval.ChosenObjective.TargetArmyId))
            {
                AiDebugLog.Write($"[AI][V2] reaction probe(materializeForDiscovery) — canonical demand targets raid "
                    + $"#{eval.ChosenObjective.TargetArmyId}, not among this discovery "
                    + $"[{string.Join(",", targetIds.OrderBy(x => x))}]");
                return witnesses;
            }

            MaterializationClosure closure = ReactionMaterializationSolver.ProjectMaterializationClosure(
                player, root, ctx, snap, hand, commitments, eval.ChosenObjective, eval.Readiness);
            if (closure == null)
            {
                AiDebugLog.Write("[AI][V2] reaction probe(materializeForDiscovery) — no bounded legal "
                    + "materialization combination closes the canonical shortage within the reaction budget");
                return witnesses;
            }

            witnesses.Add(new ReactionWitness("MaterializeForDiscovery",
                $"discovery:materialize:{eval.ChosenObjective.TargetArmyId}:{closure.Key}",
                closure.TotalAp, closure.Envelope, closure.Detail,
                basis, -1, eval.ChosenObjective.TargetArmyId));
            return witnesses;
        }

        // §3/§P1 (round 6) — a hand follow-up is feasible only if SOME legal play (from the full
        // preflighted enumeration, not just the best-scored one) fits the reaction AP ceiling AND
        // its persistent envelope is spendable. FitsSpendableResources excludes the HandFollowup
        // reservation owner so a re-probe after the envelope is placed doesn't fail against itself.
        internal static List<ReactionWitness> ProbeHandFollowup(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, WorldSnapshot snap, AiHandData hand, ActorCommitments commitments,
            ReactionStateBasis basis)
        {
            var witnesses = new List<ReactionWitness>();
            var reservation = new MaterializationReservation
            {
                GenerationAttemptsUsed = StrategicTempoBudget.GenerationUsed(player, ctx.TurnNumber),
            };
            CapabilityInventory inv = CapabilityInventory.Build(snap, player, commitments);

            var options = new List<(float ap, ResourceCost env)>();
            foreach (NonCombatCardPlayer.NonCombatPlay p in NonCombatCardPlayer.EnumeratePlays(
                snap, player, root, hand, ctx, null, reservation))
                options.Add((p.Card != null ? p.Card.EffectivePlayApCost : 0f,
                    p.Generation != null ? p.Generation.GenerationResourceCost
                        : (p.Card != null ? p.Card.EffectivePlayResourceCost : null)));
            foreach (MaterializationPlan mp in MaterializationFeasibility.FilterSurplus(
                MaterializationChainEnumerator.EnumerateSurplusPlans(
                    snap, player, root, hand, ctx, inv, commitments, reservation),
                player, root, hand, ctx, reservation))
                options.Add((mp.ApCost, mp.ResCost));

            float ap = root != null ? Mathf.Max(0f, root.ActionPoints) : 0f;
            float ceiling = Mathf.Min(ap, (float)AiConfigV2.reactionReserveApCap);
            const string owner = "reaction-budget:HandFollowup";
            var feasible = options
                .Where(o => o.ap <= ceiling + 0.001f
                    && StrategicSpendability.FitsSpendableResources(player, root, ctx, o.env, owner))
                .OrderBy(o => o.ap)
                .ThenBy(o => ResCostSum(o.env))
                .ToList();
            if (feasible.Count == 0)
            {
                AiDebugLog.Write("[AI][V2] reaction probe(handFollowup) — no legal hand play fits the reaction "
                    + $"AP ceiling ({ceiling:0.#}) + spendable-resource check");
                return witnesses;
            }
            var best = feasible[0];
            string env = best.env == null ? "-"
                : $"H{best.env.human} E{best.env.energy} M{best.env.materials} T{best.env.tech}";
            // P0.1 — RequiredAp is the FULL cost: the play AP, floored by the reaction follow-up
            // estimate (the replan's own downstream allowance).
            float requiredAp = Mathf.Max(best.ap, Mathf.Max(0f, AiConfigV2.reactionFollowupApEstimate));
            witnesses.Add(new ReactionWitness("HandFollowup",
                $"handFollowup:{best.ap:0.#}:{env}", requiredAp, best.env,
                $"handFollowup witness: play {best.ap:0.#} AP (RequiredAp {requiredAp:0.#}) + envelope "
                + $"[{env}] ({feasible.Count} feasible plays)", basis));
            return witnesses;
        }

        private static float ResCostSum(ResourceCost c) => c == null ? 0f
            : c.human + c.energy + c.materials + c.tech;
    }
}
