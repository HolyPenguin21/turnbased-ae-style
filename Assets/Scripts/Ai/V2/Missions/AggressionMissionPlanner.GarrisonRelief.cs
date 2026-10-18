using System.Collections.Generic;

namespace Game.Ai.V2
{
    internal static partial class AggressionMissionLayer
    {
        // GarrisonRelief (2026-10-18, project owner's decision) — a fresh relief leg for every
        // relief objective (GarrisonReliefObjectiveEvaluator): an ActiveDefence Return leg with the
        // GarrisonRelief purpose whose mover is the donor-hex shell. Provisioning moves the chosen
        // spare bodies into it, the ordinary Return transit walks it, and on arrival the executor
        // hands the bodies to the recipient's garrison. A leg already under way is re-proposed by
        // AppendActiveDefence's durable Return loop like every other Return.
        private static void AppendGarrisonRelief(WorldSnapshot snap, ISet<int> committed,
            List<MissionProposal> proposals)
        {
            foreach (GarrisonReliefObjective o in GarrisonReliefObjectiveEvaluator.Enumerate(snap, committed))
            {
                MissionRequirements requirements = GroundCombatLegs.PinnedLegRequirements(
                    o.Detachment, o.RecipientHex, out int eta);
                // This leg's first step is the free extraction (an empty, unactivated shell; the
                // executor's terminal step); the walk's activation is the next step's, funded by the
                // durable Return. The TaskScore still prices the whole walk.
                requirements.ApMinimum = requirements.ApDesired = requirements.ApMaximum = 0f;
                var target = new ActiveDefenceMissionTarget
                {
                    Phase = ActiveDefencePhase.Return,
                    ReturnPurpose = ActiveDefenceReturnPurpose.GarrisonRelief,
                    EnemyArmyId = -1,
                    LastKnownHex = o.RecipientHex,
                    LastObservedTurn = snap.TurnNumber,
                    Confidence = 1f,
                    ProtectedAssetHex = o.RecipientHex,
                    ProtectedAssetKind = o.RecipientKind,
                    PrimaryArmyId = o.Detachment.ArmyId,
                    ReturnHex = o.RecipientHex,
                    EstimatedEta = eta,
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
                    PreferredMoverArmyId = o.Detachment.ArmyId,
                    Requirements = requirements,
                    Explain = $"GarrisonRelief #{o.DonorGarrisonId}@{o.DonorHex.Q},{o.DonorHex.R} -> "
                        + $"{o.RecipientHex.Q},{o.RecipientHex.R} via shell #{o.Detachment.ArmyId} "
                        + $"({o.UnitRuntimeIds.Count} bodies, {o.Detachment.EffectiveArmyPower:0.#} of "
                        + $"{o.Shortfall:0.#} short) task {o.TaskScore.Value:0.00}",
                };
                proposal.Axes.Value[DesireAxis.Aggression] = 1f;
                proposals.Add(proposal);
            }
        }
    }
}
