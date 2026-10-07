using Game.HexGrid;

namespace Game.Ai.V2
{
    // Factual domain payload, not another disposition or lifecycle owner.
    public sealed class ReconStepPayload : IMissionStepPayload
    {
        public ScoutTargetKind ScoutKind;
        public bool ScoutRequiresStealth;
        public HexCoord FocusHex;
        public bool HasScoutPayload;
        public bool DurableRoleContinues;
    }
}
