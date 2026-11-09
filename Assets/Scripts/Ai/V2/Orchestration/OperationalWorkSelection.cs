namespace Game.Ai.V2
{
    public enum OperationalWorkKind { None, MandatoryAviation, Mission }

    // Which executor route a selected mission takes. Planning stays in AirReconPlanner /
    // TaskExecutor; this only names the existing seam so it is decided in one place.
    public enum MissionRoute { Task, AirRecon }

    // What follows a settled step that raised no typed invalidation (AdmissionIteration).
    public enum SettledIdleVerdict { Stop, ParkAndStop, ParkAndContinue }

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

        // 2026-11-09 (match P2) — a settled step that changed nothing and stopped because its
        // mover cannot act on this turn's world (blocked step, rejected move, no movement left,
        // missing stealth or vantage) repeats the same stop when it is selected again this turn.
        // The loop parks it for the rest of the turn instead of ending the admission with it.
        // A world event (battle, hex event, discovery, lost mover, invalidated target) or a
        // completed step is not such a stop.
        internal static SettledIdleVerdict AfterUntriggeredStep(bool progressed, ExecutionStopReason? stop,
            bool onlyResidualLeft) =>
            progressed || !ParksIdleStep(stop) ? SettledIdleVerdict.Stop
            : onlyResidualLeft ? SettledIdleVerdict.ParkAndStop
            : SettledIdleVerdict.ParkAndContinue;

        internal static bool ParksIdleStep(ExecutionStopReason? stop)
        {
            switch (stop)
            {
                case ExecutionStopReason.NoSafeStep:
                case ExecutionStopReason.MoveRejected:
                case ExecutionStopReason.OutOfMovement:
                case ExecutionStopReason.RequiredStealthUnavailable:
                case ExecutionStopReason.ObservationUnavailable:
                    return true;
                default:
                    return false;
            }
        }
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

        // The no-progress counter: any progress resets it.
        internal static int NextNoProgress(int current, bool progressed) => progressed ? 0 : current + 1;
    }
}
