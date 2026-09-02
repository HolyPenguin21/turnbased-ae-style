namespace Game.Ai.V2
{
    // Small semantic helpers for the three explicit Scout target kinds. Keeping these predicates
    // central avoids scattered two-way assumptions as Recon evolves while the enum itself remains
    // the authoritative contract and stable persisted identity.
    public static class ReconScoutKinds
    {
        public const ScoutTargetKind Refresh = ScoutTargetKind.Refresh;

        public static bool IsRefresh(ScoutTargetKind kind) => kind == ScoutTargetKind.Refresh;
        public static bool IsExplore(ScoutTargetKind kind) => kind == ScoutTargetKind.Explore;
        public static bool IsSurveil(ScoutTargetKind kind) => kind == ScoutTargetKind.Surveil;

        public static bool IsGround(ScoutTargetKind kind) =>
            kind == ScoutTargetKind.Explore || kind == ScoutTargetKind.Refresh;

        public static string Name(ScoutTargetKind kind)
        {
            switch (kind)
            {
                case ScoutTargetKind.Refresh: return "Refresh";
                case ScoutTargetKind.Surveil: return "Surveil";
                case ScoutTargetKind.Explore: return "Explore";
                default: return $"Unknown({(int)kind})";
            }
        }
    }
}
