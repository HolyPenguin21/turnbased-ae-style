namespace Game.Ai.V2
{
    // ===========================================================================================
    //  Which airborne obligation the operational loop settles first. A multi-turn rebase is
    //  resumed before a recovery unless the recovery actor has the lower army id; both lists come
    //  from their existing owners (AviationRebasePlanner.FindMandatoryContinuations,
    //  ReconAirExecutor.FindMandatoryRecoveryActors). This type only orders their heads.
    // ===========================================================================================
    internal static class MandatoryAviationOrder
    {
        internal static bool RebaseFirst(int? firstRebaseId, int? firstRecoveryId) =>
            firstRebaseId.HasValue
            && (!firstRecoveryId.HasValue || firstRebaseId.Value <= firstRecoveryId.Value);
    }
}
