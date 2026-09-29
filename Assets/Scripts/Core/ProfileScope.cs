using System.Diagnostics;

namespace Game.Core
{
    // Named Unity Profiler sample for a synchronous block: `using var _ = new ProfileScope("AI/...");`
    // Shows up in the Profiler hierarchy without Deep Profile. Begin/End are
    // [Conditional("ENABLE_PROFILER")], so release players (and the out-of-Unity test runner)
    // compile the calls away. Never hold one across a `yield` — a sample must end in the frame it
    // began.
    public readonly struct ProfileScope : System.IDisposable
    {
        public ProfileScope(string name) => Begin(name);

        public void Dispose() => End();

        [Conditional("ENABLE_PROFILER")]
        private static void Begin(string name) => UnityEngine.Profiling.Profiler.BeginSample(name);

        [Conditional("ENABLE_PROFILER")]
        private static void End() => UnityEngine.Profiling.Profiler.EndSample();
    }
}
