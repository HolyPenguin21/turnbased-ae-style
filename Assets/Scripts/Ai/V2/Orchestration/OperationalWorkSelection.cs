namespace Game.Ai.V2
{
    public enum OperationalWorkKind { None, MandatoryAviation, Mission }

    // Which executor route a selected mission takes. Planning stays in AirReconPlanner /
    // TaskExecutor; this only names the existing seam so it is decided in one place.
    public enum MissionRoute { Task, AirRecon }

    // ===========================================================================================
    //  The one selection boundary of the operational loop, after the allocation pack. Pure: it
    //  only orders the work kinds that already existed. An obligation that is already paid
    //  (mandatory aviation) goes before discretionary mission progress; with nothing pending the
    //  funded missions compete in the allocator/provisioning as before (this type never ranks
    //  missions); with neither, the admission stops.
    // ===========================================================================================
    internal static class OperationalWorkSelection
    {
        internal static OperationalWorkKind Select(MandatoryAviationKind mandatory, int fundedMissions) =>
            mandatory != MandatoryAviationKind.None ? OperationalWorkKind.MandatoryAviation
            : fundedMissions > 0 ? OperationalWorkKind.Mission
            : OperationalWorkKind.None;

        // An existing-wing or stored-aircraft Scout is executed by the air Recon plan; every
        // other mission (and a ground Scout) by TaskExecutor. Explore never receives an aviation
        // actor here: it is not a Scout executor kind.
        internal static MissionRoute RouteFor(MissionKind kind, ScoutExecutorKind executor) =>
            kind == MissionKind.Scout && executor != ScoutExecutorKind.Ground
                ? MissionRoute.AirRecon : MissionRoute.Task;
    }

    // The reasons and verdict of one settled work step's typed-trigger fan-out. Transient value
    // produced by the pipeline's shared outcome step; no policy, no storage.
    internal readonly struct StepTriggerOutcome
    {
        internal readonly StrategicInvalidationReason Operational;
        internal readonly StrategicInvalidationReason Strategic;
        internal readonly bool StrategicChanged;

        internal StepTriggerOutcome(StrategicInvalidationReason operational,
            StrategicInvalidationReason strategic, bool strategicChanged)
        {
            Operational = operational;
            Strategic = strategic;
            StrategicChanged = strategicChanged;
        }

        // The step made progress when its action changed the world or the re-admission did.
        internal bool Progressed(bool actionChanged) => actionChanged || StrategicChanged;

        // Baseline: an ordinary mission step and a Phase B management round take the typed triggers and re-enters twice (the
        // follow-up pair routes a compound fact published by the first re-entry).
        internal const int StandardTriggerPairs = 2;

        // The no-progress counter: any progress resets it.
        internal static int NextNoProgress(int current, bool progressed) => progressed ? 0 : current + 1;
    }
}
