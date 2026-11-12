using System;

namespace Game.Map
{
    // The one weather profile shared by a campaign region, a pending campaign operation, the
    // prepared match and a match save. Zero is deliberately not a value: a missing JSON field
    // reads as 0 and must be rejected, never mistaken for clear weather.
    public enum MatchWeatherProfile
    {
        Clear = 1,
        OneSandstorm = 2,
        TwoSandstorms = 3,
    }

    // Profile + the seed every storm slot's random stream derives from. Immutable value; the
    // live storms themselves belong to SandstormState. Files store the two fields explicitly.
    public readonly struct MatchWeatherConditions : IEquatable<MatchWeatherConditions>
    {
        public readonly MatchWeatherProfile Profile;
        public readonly long Seed;

        public MatchWeatherConditions(MatchWeatherProfile profile, long seed)
        {
            if (!IsValid(profile)) throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown weather profile.");
            Profile = profile;
            Seed = seed;
        }

        // A standalone match with no explicit choice keeps one sandstorm (the designed default);
        // an explicit Clear from a campaign region is never replaced by this default.
        public const MatchWeatherProfile StandaloneDefault = MatchWeatherProfile.OneSandstorm;

        public static bool IsValid(MatchWeatherProfile profile) =>
            profile == MatchWeatherProfile.Clear || profile == MatchWeatherProfile.OneSandstorm
            || profile == MatchWeatherProfile.TwoSandstorms;

        // The one player-facing weather line (campaign region panel, battle conditions, deck
        // choice). "up to" is the configured instance limit, not a promise that a storm is
        // present at every moment: each instance has its own two-round pause.
        public static string Describe(MatchWeatherProfile profile)
        {
            switch (profile)
            {
                case MatchWeatherProfile.Clear: return "Weather: Clear — no sandstorms";
                case MatchWeatherProfile.OneSandstorm: return "Weather: Sandstorms — up to 1";
                case MatchWeatherProfile.TwoSandstorms: return "Weather: Sandstorms — up to 2";
                default: throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown weather profile.");
            }
        }

        // Campaign operation seed -> weather seed, a separate salt so the weather stream never
        // shares draws with the operation's other uses of the same seed.
        public const long OperationWeatherSalt = 0x574541;

        public static long DeriveSeed(int operationSeed) =>
            unchecked((long)SandstormRandom.Mix((ulong)(uint)operationSeed ^ ((ulong)OperationWeatherSalt << 32)));

        public bool Equals(MatchWeatherConditions other) => Profile == other.Profile && Seed == other.Seed;
        public override bool Equals(object obj) => obj is MatchWeatherConditions other && Equals(other);
        public override int GetHashCode() => ((int)Profile * 397) ^ Seed.GetHashCode();
        public override string ToString() => $"{Profile}/{Seed}";
    }
}
