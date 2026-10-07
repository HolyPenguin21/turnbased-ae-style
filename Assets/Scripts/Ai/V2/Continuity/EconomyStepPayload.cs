using Game.HexGrid;

namespace Game.Ai.V2
{
    // Factual domain payload, not another disposition or lifecycle owner.
    public sealed class EconomyStepPayload : IMissionStepPayload
    {
        public bool HasEconomyPayload;
        public EconomyMissionTarget EconomyTarget;
        public bool EconomyBuildCompleted;
        public MissionIntentKey? EconomyLoanSource;
        public bool DeliveryReady;
        public bool Holding;
    }
}
