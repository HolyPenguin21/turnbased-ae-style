using System.Collections.Generic;
using System.Linq;

namespace Game.Ai.V2
{
    internal static partial class AggressionMissionLayer
    {
        // FIST MUSTER (2026-11-19, project owner's decision) — before the mobilization gate opens and
        // while no Attack operation is live, every muster step FistMusterObjectiveEvaluator finds
        // becomes an ActiveDefence Return leg with the Muster purpose to the staging Base:
        //  * Walk — the army itself walks (its activation is the leg's AP);
        //  * Detach — the GarrisonRelief extraction into an empty shell on the donor's hex (0 AP),
        //    the walk is the next step's;
        //  * Intake — the staging garrison's bodies (or commanding hero) move into the fist on the
        //    same hex (0 AP, the executor's terminal step).
        // On arrival the executor joins the mover to the fist (ActiveDefenceExecutor.JoinFist).
        // A muster leg lives for its admission turn only (ImmediateTurn): the next turn the army is
        // free again and every lane competes for it on its own value.
        private static void AppendFistMuster(WorldSnapshot snap, List<AttackObjective> objectives,
            IReadOnlyList<MissionIntent> activeIntents, ISet<int> committed, List<MissionProposal> proposals)
        {
            if (snap?.Self == null || objectives == null || objectives.Count == 0
                || LiveAttackOperation(activeIntents) != null
                || AttackForceReadiness.MobilizationOpen(snap.Self))
                return;
            // A defensive regroup or withdrawal owns where the armies stand, whether it is a live
            // intent or proposed by this pass (AppendActiveDefence runs first): a muster to the
            // staging Base would walk the same armies the other way, or the same army the same way
            // under a second key.
            MissionIntent defensive = activeIntents?.FirstOrDefault(i => i != null && i.Status == IntentStatus.Active
                && i.ActiveDefence?.Phase == ActiveDefencePhase.Return
                && IsDefensiveReturn(i.ActiveDefence.ReturnPurpose));
            MissionProposal proposed = defensive != null ? null : proposals.FirstOrDefault(p =>
                p?.Target is ActiveDefenceMissionTarget t && t.Phase == ActiveDefencePhase.Return
                && IsDefensiveReturn(t.ReturnPurpose));
            if (defensive != null || proposed != null)
            {
                AiDebugLog.WriteDeduped($"muster-hold#{snap.Observer?.ColorIndex}",
                    "[AI][V2][Muster] decision=HOLD reason=defensive_return_"
                    + (defensive != null ? $"live:{defensive.IntentKey}" : $"proposed:{StableMissionKey.For(proposed)}"));
                return;
            }
            AttackObjective objective = PreparationObjectives(objectives, activeIntents).FirstOrDefault();
            foreach (FistMusterObjective o in FistMusterObjectiveEvaluator.Enumerate(snap, objective, committed))
            {
                MissionRequirements requirements = GroundCombatLegs.PinnedLegRequirements(
                    snap, o.Mover, o.StagingHex, out int eta);
                // Detach / Intake: the first step is the free extraction (an unactivated shell or a
                // same-hex transfer); a Detach walk is the next step's activation.
                if (o.DonorGarrisonId.HasValue)
                    requirements.ApMinimum = requirements.ApDesired = requirements.ApMaximum = 0f;
                var target = new ActiveDefenceMissionTarget
                {
                    Phase = ActiveDefencePhase.Return,
                    ReturnPurpose = ActiveDefenceReturnPurpose.Muster,
                    EnemyArmyId = -1,
                    LastKnownHex = o.StagingHex,
                    LastObservedTurn = snap.TurnNumber,
                    Confidence = 1f,
                    ProtectedAssetHex = o.StagingHex,
                    ProtectedAssetKind = o.StagingHex.Equals(snap.Self.Citadel) ? AssetKind.Citadel : AssetKind.Base,
                    PrimaryArmyId = o.Mover.ArmyId,
                    ReturnHex = o.StagingHex,
                    EstimatedEta = eta,
                    ImmediateTurn = snap.TurnNumber,
                    ReliefDonorGarrisonId = o.DonorGarrisonId,
                    ReliefUnitRuntimeIds = o.UnitRuntimeIds,
                };
                var proposal = new MissionProposal
                {
                    Kind = MissionKind.ActiveDefence,
                    Target = target,
                    BaseValue = o.TaskScore.Value,
                    Score = o.TaskScore,
                    LocalAdmissionScore = o.TaskScore.Value,
                    PreferredMoverArmyId = o.Mover.ArmyId,
                    Requirements = requirements,
                    Explain = $"Muster {o.Kind} #{o.Mover.ArmyId}"
                        + (o.DonorGarrisonId.HasValue ? $" from garrison #{o.DonorGarrisonId} ({o.UnitRuntimeIds?.Count ?? 0} units)" : "")
                        + $" -> fist @{o.StagingHex.Q},{o.StagingHex.R} power {o.Power:0.#} share {o.Share:0.##}"
                        + $" eta {o.EtaTurns} task {o.TaskScore.Value:0.00}",
                };
                proposal.Axes.Value[DesireAxis.Aggression] = 1f;
                proposals.Add(proposal);
            }
        }

        private static bool IsDefensiveReturn(ActiveDefenceReturnPurpose purpose) =>
            purpose == ActiveDefenceReturnPurpose.RegroupForAsset
            || purpose == ActiveDefenceReturnPurpose.SafeWithdrawal;
    }
}
