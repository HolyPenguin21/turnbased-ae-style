namespace Game.Ai.V2
{
    // Pure mappings retained from the former focus/test scope. Production always evaluates and
    // admits all four real axes.
    public static class AiStrategyV2Scope
    {
        internal static DesireAxis AxisOf(MissionKind kind)
        {
            switch (kind)
            {
                case MissionKind.Scout: return DesireAxis.Recon;
                case MissionKind.Raid:
                case MissionKind.ActiveDefence:
                case MissionKind.Attack: return DesireAxis.Aggression;
                case MissionKind.Economy: return DesireAxis.Economy;
                default: return DesireAxis.Development;
            }
        }

        internal static StrategicInvalidationReason OperationalInvalidationMask =>
            DesireAxes.InvalidationMaskFor(DesireAxis.Recon)
            | DesireAxes.InvalidationMaskFor(DesireAxis.Aggression)
            | DesireAxes.InvalidationMaskFor(DesireAxis.Development);
    }
}
