namespace Game.Ai.V2
{
    // ARCH-02 §58 — the ONE projected-army-capacity rule for the V2 path. Planner
    // (ProjectedPhysicalState, StrategicEffectRegistry.ResolveDestination) and executor preflight
    // (CardPlayExecutor.CanFitAfterDeploy) both go through here so the rule can never drift.
    //
    // It mirrors ArmyData.ComputeCapacity exactly:
    //   · an EXISTING hero governs capacity — its CommandRating is already baked into
    //     `nominalCapacity` (the recipient's live/frozen ArmyData.Capacity);
    //   · otherwise the FIRST hero being ADDED governs — capacity becomes its CommandRating,
    //     even if that rating is zero; it is a replacement, never a fallback or Math.Max;
    //   · otherwise the nominal value (garrison 4 / field 2, encoded in nominalCapacity).
    internal static class ArmyCapacityRules
    {
        internal static int ProjectedCapacity(int nominalCapacity, bool hasExistingHero,
            int addedHeroCount, int firstAddedHeroCommandRating)
        {
            if (hasExistingHero || addedHeroCount <= 0)
                return nominalCapacity;
            return firstAddedHeroCommandRating;
        }

        internal static bool RosterFits(int nominalCapacity, bool hasExistingHero,
            int projectedMemberCount, int addedHeroCount, int firstAddedHeroCommandRating)
            => projectedMemberCount <= ProjectedCapacity(
                nominalCapacity, hasExistingHero, addedHeroCount, firstAddedHeroCommandRating);
    }
}
