using Game.HexGrid;

namespace Game.Ai.V2
{
    // Factual domain payload, not another disposition or lifecycle owner.
    public sealed class DevelopmentStepPayload : IMissionStepPayload
    {
        public bool HasDevelopmentPayload;
        public DevelopmentMissionTarget DevelopmentTarget;
        public bool DeliveryReady;
    }
}
