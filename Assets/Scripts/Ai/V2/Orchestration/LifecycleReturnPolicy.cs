using System.Collections.Generic;
using System.Linq;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  LIFECYCLE RETURNS WAIT FOR THE TEMPO PASS
    // ===========================================================================================
    //  2026-10-01 (user decision): a return leg — a Raid army walking home, a builder or collector
    //  going back after its job, an Attack force's recovery walk — carries no task value of its
    //  own (MissionIntent.IsLifecycleLeg), yet it was funded in the first mission pass and spent
    //  AP the turn's draws and card plays then lacked (playtest 2026-10-01 #3/#4: ~20 AP a game on
    //  Raid Return alone). While neither the Citadel nor a Base is threatened such a leg waits:
    //  the planner withholds it (a recorded deferral, so Continuity keeps the commitment), Phase B
    //  spends first, and the next admission pass after the first Phase B round funds the returns
    //  from what is left. An ActiveDefence return is a withdrawal from a threat and never waits.
    // ===========================================================================================
    internal static class LifecycleReturnPolicy
    {
        internal const string DeferralReason = "lifecycle_return_waits_for_tempo";

        internal static bool HomeThreatened(WorldSnapshot snap)
        {
            ThreatModel t = snap?.Threat;
            return t != null && (t.UnderSiege
                || t.CitadelThreatSeverity >= AiConfigV2.lifecycleReturnHomeThreatSeverity
                || t.BaseThreatSeverity >= AiConfigV2.lifecycleReturnHomeThreatSeverity);
        }

        internal static bool IsDeferrableReturn(MissionProposal mission, IReadOnlyList<MissionIntent> activeIntents)
        {
            if (mission == null || activeIntents == null)
                return false;
            MissionIntentKey key = MissionIntentKey.For(mission);
            MissionIntent intent = activeIntents.FirstOrDefault(i => i != null && i.IntentKey.Equals(key));
            return intent != null && intent.IsLifecycleLeg && intent.ActiveDefence == null;
        }
    }
}
