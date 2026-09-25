// Test-run only (net472 under Mono). Never part of the Unity project.
public static class Net472Compat { public static int SingleToInt32Bits(float v) { unsafe { return *(int*)&v; } } }
// Test-run only: a managed Mathf in the GLOBAL namespace shadows UnityEngine.Mathf (whose static
// constructor needs the native engine) for every `using UnityEngine;` file. Same semantics as Unity.
public static class Mathf
{
    public const float Epsilon = float.Epsilon;
    public const float Deg2Rad = (float)(System.Math.PI / 180.0);
    public const float Rad2Deg = (float)(180.0 / System.Math.PI);
    public const float PI = (float)System.Math.PI;
    public const float Infinity = float.PositiveInfinity;
    public const float NegativeInfinity = float.NegativeInfinity;
    public static float Max(float a, float b) => a > b ? a : b;
    public static float Max(params float[] v) { if (v.Length == 0) return 0; float m = v[0]; for (int i = 1; i < v.Length; i++) if (v[i] > m) m = v[i]; return m; }
    public static int Max(int a, int b) => a > b ? a : b;
    public static int Max(params int[] v) { if (v.Length == 0) return 0; int m = v[0]; for (int i = 1; i < v.Length; i++) if (v[i] > m) m = v[i]; return m; }
    public static float Min(float a, float b) => a < b ? a : b;
    public static float Min(params float[] v) { if (v.Length == 0) return 0; float m = v[0]; for (int i = 1; i < v.Length; i++) if (v[i] < m) m = v[i]; return m; }
    public static int Min(int a, int b) => a < b ? a : b;
    public static int Min(params int[] v) { if (v.Length == 0) return 0; int m = v[0]; for (int i = 1; i < v.Length; i++) if (v[i] < m) m = v[i]; return m; }
    public static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
    public static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
    public static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
    public static int RoundToInt(float f) => (int)System.Math.Round(f);
    public static float Round(float f) => (float)System.Math.Round(f);
    public static int CeilToInt(float f) => (int)System.Math.Ceiling(f);
    public static int FloorToInt(float f) => (int)System.Math.Floor(f);
    public static float Abs(float f) => System.Math.Abs(f);
    public static int Abs(int f) => System.Math.Abs(f);
    public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
    public static float InverseLerp(float a, float b, float v) => a != b ? Clamp01((v - a) / (b - a)) : 0f;
    public static bool Approximately(float a, float b) => Abs(b - a) < Max(1E-06f * Max(Abs(a), Abs(b)), Epsilon * 8f);
    public static float SmoothStep(float from, float to, float t) { t = Clamp01(t); t = -2f * t * t * t + 3f * t * t; return to * t + from * (1f - t); }
    public static float Sin(float f) => (float)System.Math.Sin(f);
    public static float Cos(float f) => (float)System.Math.Cos(f);
    public static float Sqrt(float f) => (float)System.Math.Sqrt(f);
    public static float Atan2(float y, float x) => (float)System.Math.Atan2(y, x);
    public static float Pow(float f, float p) => (float)System.Math.Pow(f, p);
    public static float Sign(float f) => f >= 0f ? 1f : -1f;
    public static float PerlinNoise(float x, float y) => 0.5f;
}
