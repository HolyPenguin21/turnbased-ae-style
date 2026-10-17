using System.Collections;
using System.Diagnostics;

namespace Game.Ai.V2
{
    // Gives a frame back to the engine at a decision boundary once the AI work since the last
    // such yield exceeded the budget. A turn runs refreshes, demand regeneration and Phase A
    // back-to-back; without this, several of them land in one frame (2026-10-17 profile: 91 ms,
    // six knowledge refreshes and seven Phase A entries in a single frame). Nothing mutates the
    // world between AI frames, so a checkpoint never splits a decision from the state it read.
    // Wall clock (Stopwatch), not the engine clock: the turn loop also runs in world-less tests.
    internal sealed class FramePacer
    {
        internal const double BudgetSeconds = 0.008;

        private long _since = Stopwatch.GetTimestamp();

        internal bool Expired =>
            (Stopwatch.GetTimestamp() - _since) / (double)Stopwatch.Frequency >= BudgetSeconds;

        internal IEnumerator Checkpoint()
        {
            if (!Expired)
                yield break;
            yield return null;
            _since = Stopwatch.GetTimestamp();
        }
    }
}
