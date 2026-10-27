using System;

namespace Game.Campaign
{
    public static partial class CampaignMatchBridge
    {
        public static bool CanRestoreContext(CampaignMatchContext context, out string error)
        {
            error = null;
            if (context == null) return true;
            try { return ValidateRestoreContext(Store.Load(out _), context, out error); }
            catch (Exception ex) { error = "Could not validate the saved campaign battle: " + ex.Message; return false; }
        }

        internal static bool ValidateRestoreContext(CampaignState state, CampaignMatchContext context, out string error)
        {
            error = null;
            if (context == null) return true;
            var op = state?.PendingOperation;
            if (state == null || string.IsNullOrWhiteSpace(context.CampaignId)
                || string.IsNullOrWhiteSpace(context.OperationId) || string.IsNullOrWhiteSpace(context.MatchId)
                || state.CampaignId != context.CampaignId || state.HumanFaction != context.HumanFaction
                || op == null || !op.Manual || op.TestAutoResolve || op.ResultRecorded || op.OwnershipApplied
                || op.OperationId != context.OperationId || op.MatchId != context.MatchId
                || op.AttackerFaction != context.AttackerFaction || op.DefenderFaction != context.DefenderFaction
                || op.SourceRegionId != context.SourceRegionId || op.TargetRegionId != context.TargetRegionId
                || op.SelectedHumanDeckId != context.SelectedHumanDeckId)
            {
                error = "This save no longer matches the campaign's pending battle.";
                return false;
            }
            return true;
        }
    }
}
