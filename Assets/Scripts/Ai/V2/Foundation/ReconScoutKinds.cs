namespace Game.Ai.V2
{
    // Small semantic helpers for the three explicit Scout target kinds. Keeping these predicates
    // central avoids scattered two-way assumptions as Recon evolves while the enum itself remains
    // the authoritative contract and stable persisted identity.
    public static class ReconScoutKinds
    {
        public const ScoutTargetKind Refresh = ScoutTargetKind.Refresh;

        public static bool IsRefresh(ScoutTargetKind kind) => kind == ScoutTargetKind.Refresh;
        public static bool IsCapture(ScoutTargetKind kind) => kind == ScoutTargetKind.CaptureStructure;
        public static bool IsHunt(ScoutTargetKind kind) => kind == ScoutTargetKind.Hunt;
        // A one-shot local ground action (take/destroy an adjacent structure, strike a reachable
        // enemy scouting force): no patrol lane, no lane quota or separation, no refocus, never
        // aviation. Its own validity rule is ReconReactionPolicy.CanStrikeLocalTarget.
        public static bool IsLocalStrike(ScoutTargetKind kind) => IsCapture(kind) || IsHunt(kind);
        public static bool IsExplore(ScoutTargetKind kind) => kind == ScoutTargetKind.Explore;
        // Aviation-only observation pass (support, never a ground visit). See ScoutTargetKind.
        public static bool IsAirSweep(ScoutTargetKind kind) => kind == ScoutTargetKind.AirSweep;

        // THE stealth-lane rule: a job whose objective requires stealth OR where a known enemy can
        // detect a scout is served only by a stealth-capable ground scout — never by a generic
        // scout's capacity, never by aviation (which cannot go hidden).
        public static bool NeedsStealth(StealthRequirement requirement, float detectionRisk) =>
            requirement == StealthRequirement.Required || detectionRisk > 0f;

        public static bool IsGround(ScoutTargetKind kind) =>
            kind == ScoutTargetKind.Explore || kind == ScoutTargetKind.Refresh || IsLocalStrike(kind);

        public static string Name(ScoutTargetKind kind)
        {
            switch (kind)
            {
                case ScoutTargetKind.Refresh: return "Refresh";
                case ScoutTargetKind.Explore: return "Explore";
                case ScoutTargetKind.AirSweep: return "AirSweep";
                case ScoutTargetKind.CaptureStructure: return "CaptureStructure";
                case ScoutTargetKind.Hunt: return "Hunt";
                default: return $"Unknown({(int)kind})";
            }
        }
    }
}
