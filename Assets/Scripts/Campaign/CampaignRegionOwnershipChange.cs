using Game.Players;

namespace Game.Campaign
{
    // Presentation receipt for a newly committed ownership transition; never part of CampaignState/save data.
    public readonly struct CampaignRegionOwnershipChange
    {
        public readonly string CampaignId, OperationId;
        public readonly int SourceRegionId, TargetRegionId;
        public readonly Faction PreviousOwner, NewOwner;
        public CampaignRegionOwnershipChange(string campaignId, string operationId, int sourceId, int targetId,
            Faction previousOwner, Faction newOwner)
        {
            CampaignId = campaignId; OperationId = operationId; SourceRegionId = sourceId; TargetRegionId = targetId;
            PreviousOwner = previousOwner; NewOwner = newOwner;
        }
        // Only the still-pending, committed operation may consume this live receipt.
        public bool Matches(CampaignState state)
        {
            var op = state?.PendingOperation;
            int targetId = TargetRegionId;
            return state?.CampaignId == CampaignId && op != null && state.Phase == CampaignPhase.ShowingResult
                && op.OperationId == OperationId && op.OwnershipApplied && op.ResultRecorded
                && op.Outcome == CampaignOutcome.AttackerVictory && op.SourceRegionId == SourceRegionId
                && op.TargetRegionId == TargetRegionId && op.DefenderFaction == PreviousOwner
                && op.AttackerFaction == NewOwner && PreviousOwner != NewOwner
                && state.Regions.Find(r => r.RegionId == targetId)?.OwnerFaction == NewOwner;
        }
    }
}
