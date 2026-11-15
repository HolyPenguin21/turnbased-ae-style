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
        // Diagnostic: while ScopeTiming is collecting (one AI turn), the scope's wall time is also
        // summed by name for the turn's [AI][Timing] scope line.
        private readonly string _timedName;
        private readonly long _start;

        public ProfileScope(string name)
        {
            Begin(name);
            _timedName = ScopeTiming.Collecting ? name : null;
            _start = _timedName != null ? Stopwatch.GetTimestamp() : 0L;
        }

        public void Dispose()
        {
            End();
            if (_timedName != null)
                ScopeTiming.Add(_timedName, Stopwatch.GetTimestamp() - _start);
        }

        [Conditional("ENABLE_PROFILER")]
        private static void Begin(string name) => UnityEngine.Profiling.Profiler.BeginSample(name);

        [Conditional("ENABLE_PROFILER")]
        private static void End() => UnityEngine.Profiling.Profiler.EndSample();
    }

    // Diagnostic per-name totals of ProfileScope wall time (inclusive: a nested scope also counts
    // in its parent). Collected only between Begin and Take (one AI turn); off everywhere else.
    public static class ScopeTiming
    {
        public static bool Collecting { get; private set; }
        private static readonly System.Collections.Generic.Dictionary<string, (long Ticks, int Calls)> Totals =
            new System.Collections.Generic.Dictionary<string, (long, int)>(System.StringComparer.Ordinal);

        // Diagnostic per-name event counts of the same turn (e.g. why a cache was dropped).
        private static readonly System.Collections.Generic.Dictionary<string, long> Counts =
            new System.Collections.Generic.Dictionary<string, long>(System.StringComparer.Ordinal);

        public static void Begin()
        {
            Totals.Clear();
            Counts.Clear();
            Collecting = true;
        }

        // Adds `n` to the named counter while collecting; no-op otherwise.
        public static void Count(string name, long n = 1)
        {
            if (!Collecting || name == null) return;
            Counts.TryGetValue(name, out long c);
            Counts[name] = c + n;
        }

        // The `top` counters, largest first, as "name=n"; clears them. Call before Take.
        public static string TakeCounts(int top)
        {
            var rows = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, long>>(Counts);
            rows.Sort((a, b) => b.Value != a.Value ? b.Value.CompareTo(a.Value)
                : string.CompareOrdinal(a.Key, b.Key));
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < rows.Count && i < top; i++)
                sb.Append(i == 0 ? "" : " ").Append(rows[i].Key).Append('=').Append(rows[i].Value);
            Counts.Clear();
            return sb.ToString();
        }

        internal static void Add(string name, long ticks)
        {
            Totals.TryGetValue(name, out var t);
            Totals[name] = (t.Ticks + ticks, t.Calls + 1);
        }

        // Stops collecting; the `top` names by total time as "name=ms/calls", longest first.
        public static string Take(int top)
        {
            Collecting = false;
            var rows = new System.Collections.Generic.List<(string Name, long Ticks, int Calls)>();
            foreach (var kv in Totals) rows.Add((kv.Key, kv.Value.Ticks, kv.Value.Calls));
            rows.Sort((a, b) => b.Ticks.CompareTo(a.Ticks));
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < rows.Count && i < top; i++)
                sb.Append(i == 0 ? "" : " ").Append(rows[i].Name).Append('=')
                    .Append((rows[i].Ticks * 1000.0 / Stopwatch.Frequency).ToString("0",
                        System.Globalization.CultureInfo.InvariantCulture))
                    .Append("ms/").Append(rows[i].Calls);
            Totals.Clear();
            return sb.ToString();
        }
    }
}
