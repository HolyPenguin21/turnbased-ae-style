using Game.HexGrid;

namespace Game.Ai.V2
{
    // Factual domain payload, not another disposition or lifecycle owner.
    public sealed class AttackStepPayload : IMissionStepPayload
    {
        public bool HasAttackPayload;
        public AttackMissionTarget AttackTarget;
        public bool AttackOpportunisticStrike;
        public bool AttackIntermediateCaptured;
        public bool AttackCaptureHadBattle;
        public bool AirSupportStrikeSucceeded;
    }
}
