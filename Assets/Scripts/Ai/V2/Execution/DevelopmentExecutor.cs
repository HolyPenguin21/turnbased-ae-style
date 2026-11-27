using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Combat;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Ai.V2
{
    // The Development lane's step: the pinned garrison extraction of its operator hero, the walk to
    // the facility and the delivery once the operator is prepared there. Called by TaskExecutor.
    internal static class DevelopmentExecutor
    {
        // The one mutation site of a Development step whose hero still stands in a garrison: the
        // garrison extraction Provisioning pinned (pm.EconomyExtractionPlan), re-proved live first.
        // Returns false when the step has no pending extraction. On success the step ends here (one
        // mutation per step) and pm.MoverArmyId becomes the real extracted army.
        internal static bool TryApplyPendingExtraction(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisionedMission pm, ExecutionResult result, int apBefore)
        {
            if (pm.Kind != MissionKind.Development || pm.EconomyExtractionGarrisonArmyId < 0)
                return false;
            if (!DevelopmentOpportunityEvaluator.IsPreparationSite(player,
                    pm.DevelopmentTarget.FacilityHex, pm.DevelopmentTarget.Mode, ctx: ctx))
            {
                result.StopForReplan(ExecutionStopReason.TargetInvalidated);
                result.FinalHex = pm.ExecutionHex;
                return true;
            }
            if (BattleInitiator.FindEnemyAt(pm.DevelopmentTarget.FacilityHex, player) != null)
            {
                result.StopForReplan(ExecutionStopReason.NoSafeStep);
                result.FinalHex = pm.ExecutionHex;
                return true;
            }
            ArmyData garrison = AiV2Util.ResolveArmy(player, pm.EconomyExtractionGarrisonArmyId);
            ReusableArmySelector.GarrisonExtractionCandidate pinned = pm.EconomyExtractionPlan;
            if (garrison == null || pinned.Tier == ReusableArmySelector.GarrisonExtractionTier.None
                || !ReferenceEquals(pinned.Hero, pm.DevelopmentTarget.Hero)
                || !garrison.Members.Contains(pinned.Hero)
                || !(garrison.IsGarrison
                    ? AiArmyRoles.CanSpareGarrisonMember(player, garrison, pinned.Hero)
                    : AiArmyRoles.IsDetachedFieldDelivery(garrison, pinned.Hero))
                || pinned.ApCost > pm.ClaimedAp + AiConfigV2.allocatorSliceEpsilon
                || root == null || !root.CanSpendActionPoints(Mathf.CeilToInt(pinned.ApCost)))
            {
                result.StopForReplan(ExecutionStopReason.TargetInvalidated);
                result.FinalHex = pm.ExecutionHex;
                return true;
            }
            ArmyData extracted = EconomyExecutor.ApplyGarrisonExtraction(
                player, garrison, pinned, ctx, out string extractionReason);
            if (extracted == null)
                AiDebugLog.Write($"[AI][V2][Development] extraction rejected for {pm.Key}: {extractionReason}");
            result.StartHex = pm.ExecutionHex;
            result.FinalHex = extracted?.Hex ?? pm.ExecutionHex;
            result.ActualActorArmyId = extracted?.Id;
            result.ActorMaterialized = extracted != null;
            result.StopReason = extracted == null
                ? ExecutionStopReason.TargetInvalidated : ExecutionStopReason.StepCompleted;
            result.NeedsReplan = extracted == null;
            if (extracted != null)
            {
                pm.MoverArmyId = extracted.Id;
                pm.EconomyExtractionGarrisonArmyId = -1;
            }
            return true; // extraction is exactly one canonical mutation step
        }

        internal static IEnumerator RunStep(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisionedMission pm, ExecutionResult result, int apBefore)
        {
            ArmyData army = AiV2Util.ResolveArmy(player, pm.MoverArmyId);
            DevelopmentMissionTarget target = pm.DevelopmentTarget;
            if (army == null || !army.Members.Contains(target.Hero))
            {
                result.StopForReplan(ExecutionStopReason.MoverLost);
                yield break;
            }
            result.ActualActorArmyId = army.Id;
            if (!army.Hex.Equals(target.FacilityHex))
                yield return EconomyExecutor.RunTransportStep(player, root, ctx, pm, result, apBefore,
                    target.FacilityHex, $"development - {target.Mode} hero={target.HeroKey}");

            // A battle or event can interrupt an otherwise valid transport step; it must be
            // settled by its domain owner before this delivery can be marked ready.
            if (result.StopReason != ExecutionStopReason.MoverLost
                && result.StopReason != ExecutionStopReason.BattleStarted
                && result.StopReason != ExecutionStopReason.HexEventStarted
                && DevelopmentOpportunityEvaluator.OperatorPreparedAt(player, target.Hero,
                    target.FacilityHex, target.Mode)
                && BattleInitiator.FindEnemyAt(target.FacilityHex, player) == null)
            {
                result.ReachedGoal = true;
                result.DevelopmentDeliveryReady = true;
                result.StopReason = ExecutionStopReason.ReachedGoal;
                result.NeedsReplan = false;
                result.FinalHex = target.FacilityHex;
                AiDebugLog.Write($"[AI][V2][Development] arrived hero={target.HeroKey} "
                    + $"@({target.FacilityHex.Q},{target.FacilityHex.R}); preparation readmit");
            }
        }
    }
}
