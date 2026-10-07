using Game.HexGrid;

namespace Game.Ai.V2
{
    // Factual domain payload, not another disposition or lifecycle owner.
    public sealed class GroundCombatStepPayload : IMissionStepPayload
    {
        public bool OperationStarted;
        public bool ReinforcementHandoffAttempted;
    }
}
