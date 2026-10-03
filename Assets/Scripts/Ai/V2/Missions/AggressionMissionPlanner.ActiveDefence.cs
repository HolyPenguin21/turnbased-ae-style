using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.HexGrid;
using Game.Combat;
using Game.Aviation;

namespace Game.Ai.V2
{
    internal static partial class AggressionMissionLayer
    {
        // ActiveDefence: every honest threat gets ONE response decision
        // (ActiveDefenceObjectiveEvaluator.AssessResponse, the same answer Demand reads) and this
        // lane only turns it into proposals — an Intercept, or one independent Return leg per
        // withdrawing army (to the Citadel to regroup, or home on a real shortage). No support
        // convoy, no cross-hex gather, no borrowed offensive actor.
        private static void AppendActiveDefence(WorldSnapshot snap,
            IReadOnlyList<MissionIntent> activeIntents, ISet<int> committed,
            List<MissionProposal> proposals,
            IDictionary<MissionIntentKey, string> deferredThisPass)
        {
            // A Return already under way is finished by its own army, whatever became of the
            // threat that started it. Lifecycle work of a Hard obligation: neutral intrinsic score.
            if (activeIntents != null)
                foreach (MissionIntent intent in activeIntents.Where(i => i?.ActiveDefence != null
                    && i.Status == IntentStatus.Active
                    && i.ActiveDefence.Phase == ActiveDefencePhase.Return
                    && i.ActiveDefence.PrimaryArmyId.HasValue
                    && i.ActiveDefence.ReturnHex.HasValue))
                {
                    ActiveDefenceIntent d = intent.ActiveDefence;
                    ArmySnapshot actor = snap.Self?.Armies?.FirstOrDefault(a => a != null
                        && a.ArmyId == d.PrimaryArmyId.Value);
                    if (actor == null) continue;
                    MissionRequirements requirements = GroundCombatLegs.PinnedLegRequirements(
                        actor, d.ReturnHex.Value, out int eta);
                    var target = new ActiveDefenceMissionTarget
                    {
                        Phase = ActiveDefencePhase.Return, EnemyArmyId = d.EnemyArmyId,
                        LastKnownHex = d.LastKnownHex, LastObservedTurn = d.LastObservedTurn,
                        Confidence = d.Confidence, ProtectedAssetHex = d.ProtectedAssetHex,
                        ProtectedAssetKind = d.ProtectedAssetKind,
                        ProtectedAssetValue = d.ProtectedAssetValue,
                        ThreatSeverity = d.ThreatSeverity, PrimaryArmyId = d.PrimaryArmyId,
                        ReturnHex = d.ReturnHex, EstimatedEta = eta,
                    };
                    var proposal = new MissionProposal
                    {
                        Kind = MissionKind.ActiveDefence, Target = target,
                        BaseValue = 0f, LocalAdmissionScore = 0f, Score = default(TaskScore),
                        PreferredMoverArmyId = actor.ArmyId,
                        FromDurableIntent = true, DurableFundingTier = intent.Funding,
                        Requirements = requirements,
                        Explain = $"ActiveDefence Return actor #{actor.ArmyId} -> {d.ReturnHex.Value.Q},{d.ReturnHex.Value.R}",
                    };
                    proposal.Axes.Value[DesireAxis.Aggression] = 1f;
                    proposals.Add(proposal);
                }

            HashSet<int> withdrawing = ActiveDefenceObjectiveEvaluator.WithdrawingArmyIds(activeIntents);
            // The army an opening Attack preparation would host in: an Intercept that takes it pays
            // that preparation's value (MoverOpportunityCost). Resolved once, only when needed.
            (int armyId, float value)? pendingHost = null;
            // One fresh withdrawal per army per pass, however many threats ask for it: a Return is
            // identified by its own mover and destination, never by the threat.
            var withdrawalProposed = new HashSet<int>();
            foreach (ActiveDefenceObjective objective in ActiveDefenceObjectiveEvaluator.Enumerate(snap))
            {
                MissionIntent incumbent = ActiveDefenceObjectiveEvaluator.IncumbentIntercept(
                    activeIntents, objective.Target.EnemyArmyId);
                ActiveDefenceResponse response = ActiveDefenceObjectiveEvaluator.AssessResponse(
                    snap, objective, committed, withdrawing, incumbent?.ActiveDefence?.PrimaryArmyId);
                if (response == null)
                    continue;
                switch (response.Kind)
                {
                    case ActiveDefenceResponseKind.Intercept:
                        pendingHost ??= PendingPreparationHost(snap, activeIntents, committed);
                        AppendActiveDefenceIntercept(snap, objective, response, incumbent, proposals,
                            pendingHost.Value);
                        break;
                    case ActiveDefenceResponseKind.Defer:
                        if (incumbent != null && deferredThisPass != null)
                            deferredThisPass[incumbent.IntentKey] = response.Reason ?? "active_defence_deferred";
                        AiDebugLog.WriteDeduped(objective.Target.EnemyArmyId.ToString(),
                            $"[AI][V2][ActiveDefence][Admission] decision=DEFER enemy={objective.Target.EnemyArmyId} "
                            + $"reason={response.Reason}");
                        break;
                    case ActiveDefenceResponseKind.Regroup:
                    case ActiveDefenceResponseKind.Shortage:
                        foreach (ArmySnapshot mover in response.Movers)
                        {
                            if (mover.CurrentMovement <= 0
                                || ActiveDefenceObjectiveEvaluator.IsPinnedStrongholdDefender(snap, mover)
                                || !withdrawalProposed.Add(mover.ArmyId))
                                continue;
                            HexCoord? destination = response.Kind == ActiveDefenceResponseKind.Regroup
                                ? response.RegroupHex
                                : AiReturnBasePolicy.SelectReturnBase(snap, snap.Observer,
                                    mover.ArmyId);
                            if (!destination.HasValue || mover.Hex.Equals(destination.Value))
                                continue;
                            proposals.Add(BuildActiveDefenceWithdrawal(objective, mover,
                                destination.Value, response));
                        }
                        AiDebugLog.WriteDeduped(objective.Target.EnemyArmyId + "#withdraw",
                            $"[AI][V2][ActiveDefence][Admission] decision="
                            + (response.Kind == ActiveDefenceResponseKind.Regroup ? "REGROUP" : "RETREAT")
                            + $" enemy={objective.Target.EnemyArmyId} reason={response.Reason} "
                            + $"power={response.AvailablePower:0.#}/{response.RequiredPower:0.#} "
                            + $"hold={(response.HoldWinChance < 0f ? "n/a" : response.HoldWinChance.ToString("0.00"))} "
                            + $"movers=[{string.Join(",", response.Movers.Select(m => m.ArmyId))}]");
                        break;
                }
            }
        }

        private static void AppendActiveDefenceIntercept(WorldSnapshot snap,
            ActiveDefenceObjective objective, ActiveDefenceResponse response,
            MissionIntent incumbent, List<MissionProposal> proposals,
            (int armyId, float value) pendingPreparationHost)
        {
            GroundCombatAssemblyPlan plan = response.Plan;
            ArmySnapshot actor = snap.Self.Armies.FirstOrDefault(a => a != null
                && a.ArmyId == plan.BaseArmyId);
            EnemyContactSnapshot contact = snap.Threat?.Contacts?.FirstOrDefault(c =>
                c?.Army != null && c.Army.ArmyId == objective.Target.EnemyArmyId
                && c.Position.HasValue);
            if (actor == null || contact == null) return;
            // Price and time the force this plan will ACTUALLY field, exactly as the Raid lane
            // does. ActiveDefence shares GroundCombatAssemblyPlanner
            // with Raid, so its plan may recruit same-hex bodies too; costing the untouched
            // host systematically underprices the intercept (the AP the allocator then funds)
            // and over-states its speed (a slower recruit drags the whole formation down).
            // Null means the projection does not resolve live — fall back to the snapshot
            // figure rather than invent a second cost model.
            int projectedMove = GroundCombatAssemblyPlanner.ProjectedMaxMovement(snap, plan)
                ?? actor.MaxMovement;
            int distance = AiV2Util.TravelCost(snap, actor, objective.Target.LastKnownHex, maxMovement: projectedMove);
            if (distance == int.MaxValue) return;
            int eta = AiV2Util.CeilDiv(distance,
                UnityEngine.Mathf.Max(AiConfigV2.etaFallbackMoveBudget, projectedMove));
            // An incumbent keeps its actor; a fresh intercept that takes the pending preparation's
            // host (or one of its bodies' donors) pays that preparation's value.
            float moverCost = incumbent == null && pendingPreparationHost.armyId >= 0
                && (plan.BaseArmyId == pendingPreparationHost.armyId
                    || plan.MergeArmyIds.Contains(pendingPreparationHost.armyId))
                ? pendingPreparationHost.value : 0f;
            TaskScore actorScore = ActiveDefenceObjectiveEvaluator.WithResponse(objective,
                actor, plan.ProjectedWinChance, eta, moverOpportunityCost: moverCost,
                projectedActivationAp: GroundCombatAssemblyPlanner.ProjectedActivationApCost(snap, plan));
            ActiveDefenceMissionTarget target = objective.Target;
            target.PrimaryArmyId = actor.ArmyId;
            target.ProjectedWinChance = plan.ProjectedWinChance;
            target.CoversAllDefenders = plan.CoversAllDefenders;
            target.EstimatedEta = eta;
            float ap = actor.HasActivatedThisTurn ? 0f
                : GroundCombatAssemblyPlanner.ProjectedActivationApCost(snap, plan)
                    ?? actor.ActivationApCost;
            var proposal = new MissionProposal
            {
                Kind = MissionKind.ActiveDefence,
                Target = target,
                BaseValue = actorScore.Value,
                Score = actorScore,
                LocalAdmissionScore = actorScore.Value,
                PreferredMoverArmyId = actor.ArmyId,
                FromDurableIntent = incumbent != null,
                DurableFundingTier = incumbent?.Funding ?? CommitmentTier.None,
                Requirements = new MissionRequirements
                {
                    MoverKnown = true, RequiresArmy = true,
                    ApMinimum = ap, ApDesired = ap, ApMaximum = ap,
                    EtaTurns = eta, EstimatedDistance = distance,
                    CombatPowerMinimum = contact.Army.EffectiveArmyPower,
                    CombatPowerDesired = contact.Army.EffectiveArmyPower,
                },
                Explain = $"ActiveDefence enemy #{target.EnemyArmyId} -> asset "
                    + $"{target.ProtectedAssetKind}@{target.ProtectedAssetHex.Q},{target.ProtectedAssetHex.R} "
                    + $"task {actorScore.Value:0.00} win {plan.ProjectedWinChance:0.00} eta {eta}",
            };
            proposal.Axes.Value[DesireAxis.Aggression] = 1f;
            GroundCombatAdmissionRegistry.RecordActiveDefence(proposal, snap, response.Opposition,
                response.ExcludedArmyIds);
            if (GroundCombatAdmissionRegistry.TryGet(proposal, out HashSet<int> eligible)
                && eligible.Count > 0)
            {
                proposals.Add(proposal);
                AiDebugLog.WriteDeduped(target.EnemyArmyId.ToString(),
                    $"[AI][V2][ActiveDefence][Admission] decision=PROPOSE enemy={target.EnemyArmyId} actor={actor.ArmyId} score={actorScore.Value:0.00}");
            }
        }

        // One army's own withdrawal leg: to the Citadel (regroup) or to its home base (shortage).
        // Scored off the threat it answers — the canonical TaskScore with the walk's own price and
        // no fight — so it competes honestly with every other lane for this army's activation.
        private static MissionProposal BuildActiveDefenceWithdrawal(ActiveDefenceObjective objective,
            ArmySnapshot mover, HexCoord destination, ActiveDefenceResponse response)
        {
            MissionRequirements requirements = GroundCombatLegs.PinnedLegRequirements(
                mover, destination, out int eta);
            TaskScore score = ActiveDefenceObjectiveEvaluator.WithResponse(objective, mover,
                0f, eta);
            ActiveDefenceMissionTarget target = objective.Target;
            target.Phase = ActiveDefencePhase.Return;
            target.PrimaryArmyId = mover.ArmyId;
            target.ReturnHex = destination;
            target.EstimatedEta = eta;
            var proposal = new MissionProposal
            {
                Kind = MissionKind.ActiveDefence,
                Target = target,
                BaseValue = score.Value,
                Score = score,
                LocalAdmissionScore = score.Value,
                PreferredMoverArmyId = mover.ArmyId,
                Requirements = requirements,
                Explain = $"ActiveDefence enemy #{target.EnemyArmyId} "
                    + (response.Kind == ActiveDefenceResponseKind.Regroup ? "regroup" : "retreat")
                    + $" #{mover.ArmyId} -> {destination.Q},{destination.R} ({response.Reason}; "
                    + $"power {response.AvailablePower:0.#}/{response.RequiredPower:0.#}) task {score.Value:0.00}",
            };
            proposal.Axes.Value[DesireAxis.Aggression] = 1f;
            return proposal;
        }
    }
}
