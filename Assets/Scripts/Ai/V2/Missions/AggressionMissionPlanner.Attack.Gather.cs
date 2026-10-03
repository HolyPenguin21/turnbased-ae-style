using System.Collections.Generic;
using System.Linq;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Ai.V2
{
    internal static partial class AggressionMissionLayer
    {
        // Audit F7 — the first leg of a fresh cross-hex gather. The whole operation is priced here
        // (win of the assembled force, gather + assault ETA, total AP spread over that ETA) so it
        // competes honestly with every other lane; its first executed step creates the Hard
        // intent (§70) carrying the frozen plan, after which the remaining legs are proposed as
        // durable lifecycle work by AppendAttackGather. The lead leg is the critical path: the
        // support with the longest walk that can act this turn.
        private static bool TryAppendFreshAttackGather(WorldSnapshot snap, AttackObjective objective,
            IReadOnlyList<WorthIt.DefendingArmy> opposition, float hexBonus, ISet<int> excluded,
            List<MissionProposal> proposals)
        {
            Dictionary<int, float> donorValues = GroundCombatDonorPolicy.BorrowableDonorValues(
                snap.Observer == null ? null : MissionIntentRegistry.GetOrCreate(snap.Observer).All);
            GroundCombatGatherPlan gather = GroundCombatAssemblyPlanner.PlanGather(snap, opposition,
                hexBonus, objective.Hex, excluded, GroundCombatAdmissionPolicy.AttackCoverageGate,
                donorValues: donorValues,
                minimumArmyPower: AttackForceReadiness.RequiredPower(snap.Self.AttackPeak));
            if (!gather.Feasible || gather.SupportArmyIds.Count == 0)
            {
                AiDebugLog.WriteDeduped(objective.Target.DiagnosticLabel + "#gather",
                    $"[AI][V2][Attack][Gather] decision=REJECT target={objective.Target.DiagnosticLabel} "
                    + $"reason={gather.Reason ?? "host_already_clears"}");
                return false;
            }
            ArmySnapshot host = snap.Self.Armies?.FirstOrDefault(x => x != null
                && x.ArmyId == gather.HostArmyId);
            // The lead leg must be a FREE army: a bought donor is still held by its operation until
            // the Attack intent this lead step creates makes Continuity retire that operation.
            ArmySnapshot lead = gather.SupportArmyIds
                .Where(id => !donorValues.ContainsKey(id))
                .Select(id => snap.Self.Armies?.FirstOrDefault(x => x != null && x.ArmyId == id))
                .FirstOrDefault(s => s != null && (s.Hex.Equals(gather.HostHex) || s.CurrentMovement > 0));
            if (host == null || lead == null)
            {
                AiDebugLog.WriteDeduped(objective.Target.DiagnosticLabel + "#gather",
                    $"[AI][V2][Attack][Gather] decision=HOLD target={objective.Target.DiagnosticLabel} "
                    + $"host={gather.HostArmyId} reason=no_free_planned_support_can_act_this_turn");
                return false;
            }

            // Priced off the plan's own AP split, never off the host's activation state: the
            // supports' legs are what this turn pays, the rest of the GATHER stage is spread over
            // its turns. The assault march is not charged to the gather step (project owner,
            // 2026-10-02): the operation moves toward the goal turn by turn.
            int eta = Mathf.Max(1, gather.GatherTurns);
            TaskScore score = TaskScoreEvaluator.WithResponse(objective.TaskScore,
                gather.ProjectedWinChance, gather.CurrentTurnAp,
                AiV2Util.CeilDiv(gather.GatherFutureAp, eta), eta,
                moverOpportunityCost: gather.DisplacedValue);
            MissionProposal proposal = BuildAttackGatherLeg(objective.Target, host, lead,
                gather.SupportArmyIds, hexBonus, objective.DefenderCount, gather.ProjectedWinChance,
                gather.CoversAllDefenders, 0, score, null);
            proposal.Explain = $"Attack {objective.Target.DiagnosticLabel} Gather (fresh) task "
                + $"{F(score.Value)} host #{host.ArmyId} supports [{string.Join(",", gather.SupportArmyIds)}] "
                + $"win {F(gather.ProjectedWinChance)} gatherTurns {gather.GatherTurns} "
                + $"assaultEta {gather.AssaultEta} ap {gather.TotalAp}";
            proposals.Add(proposal);
            AiDebugLog.WriteDeduped(objective.Target.DiagnosticLabel + "#gather",
                $"[AI][V2][Attack][Gather] decision=PROPOSE target={objective.Target.DiagnosticLabel} "
                + $"host={host.ArmyId} lead={lead.ArmyId} supports=[{string.Join(",", gather.SupportArmyIds)}] "
                + $"win={F(gather.ProjectedWinChance)} gatherTurns={gather.GatherTurns} "
                + $"assaultEta={gather.AssaultEta} ap={gather.TotalAp} score={F(score.Value)}");
            return true;
        }

        // Audit F7 — the durable Gather legs: every planned support still expected at the host
        // walks to it (or hands over once there) in parallel. Lifecycle work of a Hard operation,
        // so, like the Reinforcement convoy, the intrinsic score stays neutral.
        private static void AppendAttackGather(WorldSnapshot snap, MissionIntent intent,
            AttackIntent a, ISet<int> committed, List<MissionProposal> proposals, AiTurnContext ctx)
        {
            if (!a.PrimaryArmyId.HasValue)
                return;
            ArmySnapshot host = snap.Self.Armies?.FirstOrDefault(x => x != null
                && x.ArmyId == a.PrimaryArmyId.Value);
            if (host == null)
                return;
            float hexBonus = AttackObjectiveEvaluator.KnownSiteDefenceBonus(snap, ctx?.Map, a.Target.Hex);
            int defenderCount = AttackObjectiveEvaluator.KnownSiteDefenders(snap, a.Target.Hex).Count;
            HexCoord? staging = a.Preparation
                ? AttackPreparationPolicy.PreparationStagingBase(snap, a.Target.Hex, host) : null;
            if (a.Preparation && staging.HasValue && !host.Hex.Equals(staging.Value)
                && host.MemberCount > 0)
            {
                // The durable host walks on to the staging Base (lifecycle leg, neutral score). A
                // step is only offered when the host can really pay the NEXT hex now: movement left
                // that is below that hex's cost (rough terrain costs 2) would only fail the
                // provisioning with "no safe first step" and mark the whole operation Blocked.
                ArmyData liveHost = ctx?.Map == null || snap.Observer == null ? null
                    : AiV2Util.ResolveArmy(snap.Observer, host.ArmyId);
                bool canStepNow = liveHost != null
                    ? SafeStepPathing.FindNextSafeStep(ctx.Map, liveHost, staging.Value,
                        profile: SafeRouteProfile.Combat).HasValue
                    : host.CurrentMovement > 0;
                if (canStepNow)
                {
                    AttackObjective tracked = AttackObjectiveEvaluator.ForTrackedTarget(snap, a.Target)
                        ?? new AttackObjective { Target = a.Target };
                    AppendPreparationStep(snap, tracked, host.ArmyId, staging.Value,
                        AttackPreparationStep.MoveHost,
                        host.HasActivatedThisTurn ? 0f : host.ActivationApCost, null, hexBonus,
                        proposals, $"[AI][V2][Attack][Mobilization] {intent.IntentKey}", intent);
                }
            }
            else if (a.Preparation)
            {
                AppendPreparationAssembly(snap, intent, a, host, hexBonus, committed, proposals);
                if (a.GatherSupportArmyIds.Count == 0)
                    AppendPreparationRecruit(snap, intent, a, host, hexBonus, committed, proposals);
                if (!a.CommanderArmyId.HasValue)
                    AppendPreparationCommanderFetch(snap, intent, a, host, hexBonus, proposals);
            }
            // 2026-10-01 (variant B) — the fetched commander walks to the host and hands itself
            // over there (a Gather leg whose larger Command is the progress: CommanderLeg).
            if (a.Preparation && a.CommanderArmyId.HasValue)
            {
                ArmySnapshot commander = snap.Self.Armies?.FirstOrDefault(x => x != null
                    && x.ArmyId == a.CommanderArmyId.Value);
                if (commander != null && (commander.Hex.Equals(host.Hex) || commander.CurrentMovement > 0))
                {
                    MissionProposal leg = BuildAttackGatherLeg(a.Target, host, commander,
                        a.GatherSupportArmyIds, hexBonus, defenderCount, a.ProjectedWinChance,
                        a.CoversAllDefenders, a.LastOpportunisticStrikeTurn, default(TaskScore), intent);
                    MarkPreparation(leg, AttackPreparationStep.None);
                    AttackMissionTarget lt = (AttackMissionTarget)leg.Target;
                    lt.CommanderLeg = true;
                    leg.Target = lt;
                    proposals.Add(leg);
                }
            }
            foreach (int supportId in a.GatherSupportArmyIds.ToList())
            {
                ArmySnapshot support = snap.Self.Armies?.FirstOrDefault(x => x != null
                    && x.ArmyId == supportId);
                // Nothing to do this turn: an idle proposal would only fail NoExecutableStep.
                if (support == null || (!support.Hex.Equals(host.Hex) && support.CurrentMovement <= 0))
                    continue;
                MissionProposal leg = BuildAttackGatherLeg(a.Target, host, support, a.GatherSupportArmyIds,
                    hexBonus, defenderCount, a.ProjectedWinChance, a.CoversAllDefenders,
                    a.LastOpportunisticStrikeTurn, default(TaskScore), intent);
                if (a.Preparation)
                    MarkPreparation(leg, AttackPreparationStep.None);
                proposals.Add(leg);
            }
            AiDebugLog.WriteDeduped(intent.IntentKey.ToString(),
                $"[AI][V2][Attack][Gather] decision=CONTINUE {intent.IntentKey} host={host.ArmyId} "
                + $"supports=[{string.Join(",", a.GatherSupportArmyIds)}]");
        }

        private static MissionProposal BuildAttackGatherLeg(AttackTargetRef targetRef,
            ArmySnapshot host, ArmySnapshot support, IEnumerable<int> gatherSupportIds,
            float hexBonus, int defenderCount, float win, bool cover, int opportunisticTurn,
            TaskScore score, MissionIntent intent)
        {
            MissionRequirements requirements = GroundCombatLegs.PinnedLegRequirements(
                support, host.Hex, out int eta);
            var target = new AttackMissionTarget
            {
                Phase = AttackMissionPhase.Gather,
                Target = targetRef,
                PrimaryArmyId = host.ArmyId,
                SupportArmyId = support.ArmyId,
                GatherSupportArmyIds = gatherSupportIds.ToArray(),
                DestinationHex = host.Hex,
                DefenderHexDefenseBonus = hexBonus,
                DefenderCount = defenderCount,
                ProjectedWinChance = win,
                CoversAllDefenders = cover,
                EstimatedEta = eta,
                OpportunisticStrikeTurn = opportunisticTurn,
            };
            var proposal = new MissionProposal
            {
                Kind = MissionKind.Attack,
                Target = target,
                BaseValue = score.Value,
                Score = score,
                LocalAdmissionScore = score.Value,
                PreferredMoverArmyId = support.ArmyId,
                FromDurableIntent = intent != null,
                DurableFundingTier = intent?.Funding ?? CommitmentTier.None,
                Requirements = requirements,
                Explain = $"Attack {targetRef.DiagnosticLabel} Gather support #{support.ArmyId} -> host "
                    + $"#{host.ArmyId} at ({host.Hex.Q},{host.Hex.R})",
            };
            proposal.Axes.Value[DesireAxis.Aggression] = 1f;
            return proposal;
        }
    }
}
