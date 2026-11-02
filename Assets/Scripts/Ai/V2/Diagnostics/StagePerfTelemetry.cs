using System.Diagnostics;
using System.Globalization;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  STAGE PERF TELEMETRY — diagnostics only, decides nothing
    // ===========================================================================================
    //  2026-11-02 playtest: strategic re-admissions stalled 15-80 s of wall time (one 846 s) with no
    //  log line between the executed step and Phase A. One [AI][V2][Perf] line per slow stage of
    //  the re-admission / operational refresh names where the time went without a profiler:
    //  wall (incl. engine frames given back), work (the AI's own time, FramePacer) and yields.
    // ===========================================================================================
    internal readonly struct StagePerfMark
    {
        internal readonly long Wall;
        internal readonly double Work;
        internal readonly int Yields;

        internal StagePerfMark(long wall, double work, int yields)
        {
            Wall = wall;
            Work = work;
            Yields = yields;
        }
    }

    internal static class StagePerfTelemetry
    {
        // A stage under this much wall time is not logged.
        internal const double LogAtSeconds = 1.0;

        internal static StagePerfMark Mark(FramePacer pacer) =>
            new StagePerfMark(Stopwatch.GetTimestamp(), pacer?.WorkSeconds ?? 0d, pacer?.Yields ?? 0);

        internal static void Log(FramePacer pacer, StagePerfMark start, string stage, string context = null)
        {
            double wall = (Stopwatch.GetTimestamp() - start.Wall) / (double)Stopwatch.Frequency;
            if (wall < LogAtSeconds)
                return;
            double work = pacer == null ? wall : pacer.WorkSeconds - start.Work;
            int yields = pacer == null ? 0 : pacer.Yields - start.Yields;
            AiDebugLog.Write($"[AI][V2][Perf] stage={stage} wall={F(wall)}s work={F(work)}s "
                + $"yields={yields}" + (string.IsNullOrEmpty(context) ? "" : " " + context));
        }

        private static string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    }
}
