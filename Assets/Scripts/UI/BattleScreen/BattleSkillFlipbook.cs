using System;

namespace Game.UI
{
    // Matches the approved v2 atlases. Elapsed time is unscaled; no gameplay RNG or allocations.
    public static class BattleSkillFlipbook
    {
        public const float Arrival = .48f;
        public const string FirePath = "UI/Effects/Flipbooks/Scorcher_Flipbook";
        public const string SplashPath = "UI/Effects/Flipbooks/Splash_Flipbook";
        private static readonly int[] FireMs = { 40,40,40,40,60,60,60,60,80,60,80,80,100,100,80,80,100,100,120 };
        private static readonly int[] SplashMs = { 40,40,40,60,60,60,80,100,80,80,80,80,80,80,120 };
        public readonly struct FrameSample
        {
            public readonly int Frame, Next;
            public readonly float Blend;
            public FrameSample(int frame, int next, float blend) { Frame = frame; Next = next; Blend = blend; }
        }
        public static int FrameCount(bool fire) => fire ? FireMs.Length : SplashMs.Length;
        public static float Duration(bool fire) => fire ? 1.38f : 1.08f;
        public static int Rows(bool fire) => fire ? 5 : 4;
        public static FrameSample Sample(float elapsed, bool fire)
        {
            if (float.IsNaN(elapsed) || elapsed < 0f || elapsed >= Duration(fire))
                return new FrameSample(-1, -1, 0f);
            float ms = elapsed * 1000f;
            int[] durations = fire ? FireMs : SplashMs;
            for (int frame = 0; frame < durations.Length; frame++)
            {
                int duration = durations[frame];
                if (ms >= duration) { ms -= duration; continue; }
                float blendMs = Math.Min(40f, duration * .5f);
                float p = Math.Max(0f, Math.Min(1f, (ms - duration + blendMs) / blendMs));
                return new FrameSample(frame, frame + 1 < durations.Length ? frame + 1 : -1,
                    p * p * (3f - 2f * p));
            }
            return new FrameSample(-1, -1, 0f);
        }
    }
}
