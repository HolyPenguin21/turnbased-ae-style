namespace Game.Ai.V2
{
    public enum MissionStepDisposition { Progress, Completed, Waiting, Replan, Invalidated, PermanentFailure }

    // Common step lifecycle. A completed sub-leg is never itself authority to retire a campaign.
    // MissionTurnOutcome is the existing typed domain payload/compatibility shape during migration.
    public abstract class MissionStepResult
    {
        public MissionIntentKey IntentKey;
        public MissionStepDisposition Disposition = MissionStepDisposition.Completed;
        public float ApSpent;
        public int? MoverArmyId;
    }
}
