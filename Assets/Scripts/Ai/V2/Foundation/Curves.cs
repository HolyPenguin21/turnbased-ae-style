using UnityEngine;

namespace Game.Ai.V2
{
    // Pure ramp helpers shared by Analysis, Evaluation and Strategy. Foundation owns them so a
    // lower layer never reaches up into Strategy for arithmetic.
    internal static class Curves
    {
        public static float Ramp(float v, float lo, float hi) =>
            Mathf.Clamp01((v - lo) / Mathf.Max(0.0001f, hi - lo));

        public static float InvRamp(float v, float lo, float hi) => 1f - Ramp(v, lo, hi);
    }
}
