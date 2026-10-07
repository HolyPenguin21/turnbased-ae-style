using Game.HexGrid;

namespace Game.Ai.V2
{
    // Factual domain payload, not another disposition or lifecycle owner.
    public sealed class ActiveDefenceStepPayload : IMissionStepPayload
    {
        public bool HasActiveDefencePayload;
        public ActiveDefenceMissionTarget ActiveDefenceTarget;
    }
}
