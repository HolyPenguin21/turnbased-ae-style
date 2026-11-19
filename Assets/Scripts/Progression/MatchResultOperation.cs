using System;
using System.Collections.Generic;
using Game.Campaign;
using Game.Core;
using Game.Players;
using UnityEngine;

namespace Game.Progression
{
    internal enum MatchResultOrigin { Game, Campaign, MainMenu }
    internal enum MatchResultStage { Idle, Reward, Retry, MenuNotice, CampaignResult, ReturnToCampaign, ReturnToMenu }

    internal sealed class MatchRewardView
    {
        internal MatchRewardView(PendingReward reward, RewardService rewards)
        {
            MatchId = reward.matchId;
            Outcome = reward.outcome;
            Claimed = reward.claimed;
            Keys = (Claimed ? reward.acquiredKeys : rewards.AvailableOffers(reward)).AsReadOnly();
            UnavailableCount = Claimed ? 0 : reward.offeredKeys.Count - Keys.Count;
        }

        internal string MatchId { get; }
        internal MatchOutcome Outcome { get; }
        internal bool Claimed { get; }
        internal IReadOnlyList<string> Keys { get; }
        internal int UnavailableCount { get; }
        internal int RequiredSelectionCount => RewardService.RequiredSelectionCount(Keys.Count);
    }

    internal sealed class MatchResultView
    {
        internal MatchResultView(MatchResultStage stage, MatchRewardView reward = null, string error = null)
        {
            Stage = stage;
            Reward = reward;
            Error = error;
        }

        internal MatchResultStage Stage { get; }
        internal MatchRewardView Reward { get; }
        internal string Error { get; }
    }

    // CampaignId/OperationId bind the tactical result to the campaign. MatchId binds its
    // profile offer, claim and dismissal to that same result. On retry consult both stores.
    internal sealed class MatchResultOperation
    {
        private readonly MatchResultOrigin origin;
        private readonly Func<CampaignController> getCampaign;
        private readonly Func<RewardService> getRewards;
        private readonly CampaignMatchContext campaignContext;
        private readonly CompletedMatchResult? finalResult;
        private readonly ParticipantResult? participant;

        internal MatchResultOperation(MatchResultOrigin origin, Func<CampaignController> getCampaign,
            Func<RewardService> getRewards, CampaignMatchContext campaignContext = null,
            CompletedMatchResult? finalResult = null, ParticipantResult? participant = null)
        {
            this.origin = origin;
            this.getCampaign = getCampaign;
            this.getRewards = getRewards;
            this.campaignContext = campaignContext;
            this.finalResult = finalResult;
            this.participant = participant;
            MatchId = campaignContext?.MatchId ?? participant?.MatchId;
        }

        internal string MatchId { get; }
        internal MatchResultView Current { get; private set; } = new MatchResultView(MatchResultStage.Idle);

        internal static MatchResultOperation Open(IProgressionContent content, MatchResultOrigin origin,
            CampaignMatchContext context = null, CompletedMatchResult? final = null, ParticipantResult? participant = null) =>
            new MatchResultOperation(origin,
                context == null ? () => CampaignMatchBridge.Controller : CampaignMatchBridge.RequireResultController,
                () => ProgressionContext.Initialize(content)
                    ? ProgressionContext.Rewards : throw new InvalidOperationException(ProgressionContext.Error),
                context, final, participant);

        internal MatchResultView Advance()
        {
            try
            {
                Current = origin switch
                {
                    MatchResultOrigin.Game => FinishMatch(),
                    MatchResultOrigin.Campaign => ResumeCampaign(),
                    _ => ResumeMenu(),
                };
            }
            catch (Exception ex)
            {
                Current = Failure(ex, "Could not save the match result. Try again.");
            }

            return Current;
        }

        private MatchResultView FinishMatch()
        {
            if (campaignContext != null)
            {
                var campaign = getCampaign();
                RecordCampaignResult(campaign, campaignContext,
                    finalResult ?? throw new InvalidOperationException("Match has not finished."));
                var reward = ReconcileCampaign(campaign, campaignContext.MatchId);
                return Present(reward, MatchResultStage.ReturnToCampaign);
            }

            if (!participant.HasValue || participant.Value.Outcome == MatchOutcome.Draw)
                return new MatchResultView(MatchResultStage.ReturnToMenu);

            var result = participant.Value;
            var rewards = getRewards();
            if (!rewards.Record(result, out string error)) throw new InvalidOperationException(error);
            return Present(FindReward(rewards, result.MatchId), MatchResultStage.ReturnToMenu);
        }

        private MatchResultView ResumeCampaign()
        {
            var campaign = getCampaign();
            var reward = ReconcileCampaign(campaign);
            return Present(reward, campaign.Phase == CampaignPhase.ShowingResult
                ? MatchResultStage.CampaignResult : MatchResultStage.Idle);
        }

        private MatchResultView ResumeMenu()
        {
            var rewards = getRewards();
            var campaign = getCampaign();
            var op = campaign?.View.PendingOperation;
            // Main Menu drains its existing queue. With a persisted dismissal it can also
            // finish the matching campaign write even if that popup disappeared before a retry.
            if (op?.ResultRecorded == true && op.Manual && !op.OwnershipApplied
                && (op.RewardAcknowledged || rewards.HasDismissed(op.MatchId)))
                ReconcileCampaign(campaign, op.MatchId);

            var pending = rewards.FirstPending();
            return Present(pending == null ? null : new MatchRewardView(pending, rewards), MatchResultStage.Idle);
        }

        private static MatchResultView Present(MatchRewardView reward, MatchResultStage next) =>
            reward == null ? new MatchResultView(next) : new MatchResultView(MatchResultStage.Reward, reward);

        private static MatchRewardView FindReward(RewardService rewards, string matchId)
        {
            var reward = rewards.FindPending(matchId);
            return reward == null ? null : new MatchRewardView(reward, rewards);
        }

        private MatchRewardView ReconcileCampaign(CampaignController campaign, string matchId = null)
        {
            var state = campaign.View;
            var op = state.PendingOperation;
            if (op?.ResultRecorded != true || (matchId != null && op.MatchId != matchId)) return null;
            if (!op.Manual || op.RewardAcknowledged)
            {
                campaign.ApplyResult();
                return null;
            }

            if (op.Outcome != CampaignOutcome.Draw)
            {
                var winner = op.Outcome == CampaignOutcome.AttackerVictory ? op.AttackerFaction : op.DefenderFaction;
                var outcome = winner == state.HumanFaction ? MatchOutcome.Victory : MatchOutcome.Defeat;
                var rewards = getRewards();
                var pending = rewards.FindPending(op.MatchId);
                if (pending != null)
                {
                    if (pending.faction != state.HumanFaction || pending.outcome != outcome)
                        throw new InvalidOperationException("This battle already has a reward for a different result.");
                    return new MatchRewardView(pending, rewards);
                }

                if (!rewards.HasDismissed(op.MatchId))
                {
                    var human = new PlayerSetupData { Faction = state.HumanFaction, IsHuman = true };
                    if (!rewards.Record(new ParticipantResult(op.MatchId, human, outcome), out string error))
                        throw new InvalidOperationException(error);
                    return FindReward(rewards, op.MatchId);
                }
            }

            // The only no-reward result is a draw. Non-draws advance after the durable
            // claim receipt remains in the profile and the pending reward has been removed.
            campaign.AcknowledgeReward(op.MatchId);
            return null;
        }

        internal MatchResultView Claim(string matchId, IEnumerable<string> keys)
        {
            var reward = Current.Reward;
            if (reward == null || reward.MatchId != matchId || reward.Claimed) return Current;
            try
            {
                var rewards = getRewards();
                if (!rewards.Claim(matchId, keys, out string error))
                    return Current = new MatchResultView(MatchResultStage.Reward, FindReward(rewards, matchId) ?? reward, error);
                return Advance();
            }
            catch (Exception ex)
            {
                return Current = Failure(ex, "Could not save the reward. Try again.");
            }
        }

        internal MatchResultView Dismiss(string matchId)
        {
            var reward = Current.Reward;
            if (reward == null || reward.MatchId != matchId || !reward.Claimed) return Current;
            try
            {
                if (!getRewards().Dismiss(matchId, out string error))
                    return Current = new MatchResultView(MatchResultStage.Reward, reward, error);
                return Advance();
            }
            catch (Exception ex)
            {
                return Current = Failure(ex, "Could not close the reward. Try again.");
            }
        }

        internal bool ContinueCampaign(string operationId, out string error)
        {
            error = null;
            try
            {
                getCampaign().Continue(operationId);
                return true;
            }
            catch (Exception ex)
            {
                error = Report(ex, "Could not continue the campaign. Try again.");
                return false;
            }
        }

        private static void RecordCampaignResult(CampaignController campaign, CampaignMatchContext context, CompletedMatchResult final)
        {
            var state = campaign.View;
            var op = state.PendingOperation;
            if (final.MatchId != context.MatchId || state.CampaignId != context.CampaignId || state.HumanFaction != context.HumanFaction)
                throw new InvalidOperationException("This result belongs to a different campaign battle.");

            if (op?.OperationId == context.OperationId)
            {
                if (op.MatchId != context.MatchId || op.AttackerFaction != context.AttackerFaction || op.DefenderFaction != context.DefenderFaction
                    || op.SourceRegionId != context.SourceRegionId || op.TargetRegionId != context.TargetRegionId || op.SelectedHumanDeckId != context.SelectedHumanDeckId)
                    throw new InvalidOperationException("This result does not match the current campaign battle.");
            }
            else
            {
                var receipt = state.FindBattle(context.OperationId);
                if (receipt == null || !receipt.Manual || receipt.MatchId != context.MatchId || receipt.Attacker != context.AttackerFaction
                    || receipt.Defender != context.DefenderFaction || receipt.TargetRegionId != context.TargetRegionId)
                    throw new InvalidOperationException("This result does not match the saved campaign battle.");
            }

            var winner = final.WinnerFaction;
            if (final.Draw ? winner != Faction.None
                : (winner != context.AttackerFaction && winner != context.DefenderFaction)
                    || final.LoserFaction != (winner == context.AttackerFaction ? context.DefenderFaction : context.AttackerFaction))
                throw new InvalidOperationException("Battle result is invalid: winner and loser do not match.");

            var outcome = final.Draw ? CampaignOutcome.Draw : winner == context.AttackerFaction ? CampaignOutcome.AttackerVictory : CampaignOutcome.DefenderVictory;
            campaign.RecordManualResult(context.CampaignId, context.OperationId, context.MatchId, outcome);
        }

        private MatchResultView Failure(Exception ex, string fallback) =>
            new MatchResultView(origin == MatchResultOrigin.MainMenu ? MatchResultStage.MenuNotice : MatchResultStage.Retry,
                error: Report(ex, fallback));

        private static string Report(Exception ex, string fallback)
        {
            Debug.LogWarning(ex);
            return ex is InvalidOperationException ? ex.Message : fallback;
        }
    }
}
