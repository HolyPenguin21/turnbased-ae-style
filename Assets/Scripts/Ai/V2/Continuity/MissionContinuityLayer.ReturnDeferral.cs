using System.Collections.Generic;
using System.Linq;
using Game.Players;

namespace Game.Ai.V2
{
    // The result of deferring return legs before the first Phase B round.
    internal readonly struct ReturnDeferral
    {
        // The proposals that stay in the admission (the input list instance when nothing waits).
        internal readonly List<MissionProposal> Retained;
        // The return legs that wait for the tempo pass this turn.
        internal readonly IReadOnlyList<MissionProposal> Waiting;
        // The reason each waiting leg is reported with, keyed by its intent.
        internal readonly IReadOnlyDictionary<MissionIntentKey, string> Deferrals;

        internal ReturnDeferral(List<MissionProposal> retained, IReadOnlyList<MissionProposal> waiting,
            IReadOnlyDictionary<MissionIntentKey, string> deferrals)
        {
            Retained = retained;
            Waiting = waiting;
            Deferrals = deferrals;
        }
    }

    internal static partial class MissionContinuityLayer
    {
        // Lifecycle return legs (a Raid army walking home, a builder or collector going back, an
        // Attack force's recovery walk) carry no task value of their own and wait for the tempo
        // pass, unless the home is threatened. Continuity owns the rule end to end: which legs wait
        // (LifecycleReturnPolicy), that the wait is recorded so the same leg never waits two turns
        // in a row, and that a deliberate wait is not a stall (MarkProtectedThisTurn) - in this
        // order. The caller applies it only while the first Phase B round has not settled
        // (TurnLoopView.ReturnsMayWait) and reports a non-empty Waiting to the loop.
        internal static ReturnDeferral DeferReturnsBeforeTempo(WorldSnapshot snapshot, PlayerSetupData player,
            int turn, List<MissionProposal> proposals, IReadOnlyList<MissionIntent> activeIntents)
        {
            var deferrals = new Dictionary<MissionIntentKey, string>();
            if (LifecycleReturnPolicy.HomeThreatened(snapshot))
                return new ReturnDeferral(proposals, new List<MissionProposal>(), deferrals);

            List<MissionProposal> waiting = LifecycleReturnPolicy.SelectWaiting(
                proposals, activeIntents, player, turn);
            if (waiting.Count == 0)
                return new ReturnDeferral(proposals, waiting, deferrals);

            foreach (MissionProposal m in waiting)
            {
                MissionIntentKey waitKey = MissionIntentKey.For(m);
                deferrals[waitKey] = LifecycleReturnPolicy.DeferralReason;
                LifecycleReturnPolicy.RecordWait(player, waitKey, turn);
                // A deliberate wait is not a stall (ReconcileAfterTurn).
                MarkProtectedThisTurn(player, waitKey, turn);
            }
            AiDebugLog.WriteDeduped($"returns-wait#{player.ColorIndex}#{turn}",
                $"[AI][V2][Loop] lifecycle returns wait for the tempo pass (no home threat): "
                + string.Join(", ", waiting.Select(m => StableMissionKey.For(m).ToString())));
            return new ReturnDeferral(proposals.Except(waiting).ToList(), waiting, deferrals);
        }
    }
}
