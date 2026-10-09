using System.Collections.Generic;
using System.Linq;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  When the cold / zero-Radar residual window may open (Pipeline.RunTurn's
    //  `zeroRadarResidualWindow`). A zero Radar is not a prohibition, but cold preparation only
    //  uses what ordinary work left: every funded entry that is still outstanding must be a
    //  zero-value, non-commitment one. Pure predicates over the allocator's Funded list; the
    //  caller owns the flag and its reset boundary.
    // ===========================================================================================
    internal static class ResidualWindowPolicy
    {
        // No task was provisioned: a rejected positive or durable mission must not be mistaken for
        // an exhausted portfolio; zero-only rejections leave a residual window.
        internal static bool AfterNoProvisionedTask(IEnumerable<FundedEntry> funded) =>
            funded.All(fe => fe != null && !fe.IsCommitment && fe.Mission != null
                && fe.Mission.EffectiveValue <= 0f);

        // A settled task produced no typed invalidation. The task that JUST executed is ignored:
        // only unfinished positive allocations prevent residual admission.
        internal static bool AfterSettledTask(IEnumerable<FundedEntry> funded,
            StableMissionKey executed) =>
            funded.All(fe => fe?.Mission != null
                && (StableMissionKey.For(fe.Mission).Equals(executed)
                    || (!fe.IsCommitment && fe.Mission.EffectiveValue <= 0f)));
    }
}
