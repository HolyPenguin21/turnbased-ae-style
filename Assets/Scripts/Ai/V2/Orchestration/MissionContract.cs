namespace Game.Ai.V2
{
    // The contract every mission kind's data answers for itself, so the shared pipeline never
    // switches on MissionKind to learn a target's identity.

    // One proposal's target (ScoutMissionTarget, RaidMissionTarget, ...): its kind, the durable
    // objective it serves and the per-cycle provisioning slot of this exact leg.
    public interface IMissionTarget
    {
        MissionKind Kind { get; }
        MissionIntentKey IntentKey();
        StableMissionKey StableKey();
    }

    // One durable MissionIntent's objective (ScoutIntent, RaidIntent, ...). `preferredMover` is the
    // intent's bound mover, for a kind whose identity falls back to it.
    public interface IMissionObjective
    {
        MissionKind Kind { get; }
        MissionIntentKey IntentKey(int? preferredMover);
    }
}
