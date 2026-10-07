using Game.HexGrid;

namespace Game.Ai.V2
{
    // Factual domain payload, not another disposition or lifecycle owner.
    public sealed class RaidStepPayload : IMissionStepPayload
    {
        public bool HasRaidPayload;
        public RaidTargetRef RaidTarget;
        public HexCoord RaidLastKnownHex;
        public bool RaidTargetIsNeutral;
        public RaidMissionPhase RaidPhase;
        public int? RaidPrimaryArmyId;
        public int? RaidSupportArmyId;
        public int? RaidAirSupportArmyId;
        public HexCoord? RaidAirSupportLandingHex;
        public bool RaidAirSupportStrikeSucceeded;
        public RaidRefitAction RaidRefitAction;
        public bool RaidRefitSucceeded;
        public ResourceVector RaidResourcesSpent;
        // Read-only projection of RaidTarget for non-Raid/logging readers; never a second copy.
        public int RaidTargetArmyId => RaidTarget.Kind == RaidTargetKind.NeutralArmy ? RaidTarget.ArmyId : 0;
    }
}
