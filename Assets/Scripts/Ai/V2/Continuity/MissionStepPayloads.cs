namespace Game.Ai.V2
{
    // Typed domain fact access over the one payload store of a MissionStepResult. Reads never
    // create a payload; ForWrite creates the single instance. The generic result knows no domain.
    internal static class MissionStepPayloads
    {
        internal static ReconStepPayload ReconFacts(this MissionStepResult r) => r.GetPayload<ReconStepPayload>() ?? new ReconStepPayload();
        internal static ReconStepPayload ReconFactsForWrite(this MissionStepResult r) => r.PayloadForWrite<ReconStepPayload>();
        internal static RaidStepPayload RaidFacts(this MissionStepResult r) => r.GetPayload<RaidStepPayload>() ?? new RaidStepPayload();
        internal static RaidStepPayload RaidFactsForWrite(this MissionStepResult r) => r.PayloadForWrite<RaidStepPayload>();
        internal static GroundCombatStepPayload GroundFacts(this MissionStepResult r) => r.GetPayload<GroundCombatStepPayload>() ?? new GroundCombatStepPayload();
        internal static GroundCombatStepPayload GroundFactsForWrite(this MissionStepResult r) => r.PayloadForWrite<GroundCombatStepPayload>();
        internal static AttackStepPayload AttackFacts(this MissionStepResult r) => r.GetPayload<AttackStepPayload>() ?? new AttackStepPayload();
        internal static AttackStepPayload AttackFactsForWrite(this MissionStepResult r) => r.PayloadForWrite<AttackStepPayload>();
        internal static ActiveDefenceStepPayload DefenceFacts(this MissionStepResult r) => r.GetPayload<ActiveDefenceStepPayload>() ?? new ActiveDefenceStepPayload();
        internal static ActiveDefenceStepPayload DefenceFactsForWrite(this MissionStepResult r) => r.PayloadForWrite<ActiveDefenceStepPayload>();
        internal static EconomyStepPayload EconomyFacts(this MissionStepResult r) => r.GetPayload<EconomyStepPayload>() ?? new EconomyStepPayload();
        internal static EconomyStepPayload EconomyFactsForWrite(this MissionStepResult r) => r.PayloadForWrite<EconomyStepPayload>();
        internal static DevelopmentStepPayload DevelopmentFacts(this MissionStepResult r) => r.GetPayload<DevelopmentStepPayload>() ?? new DevelopmentStepPayload();
        internal static DevelopmentStepPayload DevelopmentFactsForWrite(this MissionStepResult r) => r.PayloadForWrite<DevelopmentStepPayload>();
        // Fluent attachment for a result under construction (tests, domain constructors).
        internal static MissionStepResult WithPayload<T>(this MissionStepResult r, T payload)
            where T : class, IMissionStepPayload { r.SetPayload(payload); return r; }
    }
}
