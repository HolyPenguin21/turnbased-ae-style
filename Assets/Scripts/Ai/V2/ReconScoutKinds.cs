namespace Game.Ai.V2
{
    // ScoutTargetKind predates the Recon deep-rework and is declared in the central pipeline
    // contract. Refresh is kept explicit here as a typed sentinel rather than masquerading as
    // Explore or Surveil; enum values are stable integral contracts in C#, so this remains a real
    // ScoutTargetKind value and flows through StableMissionKey.SubKind without a broad edit to the
    // monolithic pipeline contract file. A later contract-cleanup may add the named enum member
    // directly without changing the persisted numeric identity.
    public static class ReconScoutKinds
    {
        public static readonly ScoutTargetKind Refresh = (ScoutTargetKind)2;

        public static bool IsRefresh(ScoutTargetKind kind) => (int)kind == (int)Refresh;
        public static bool IsExplore(ScoutTargetKind kind) => kind == ScoutTargetKind.Explore;
        public static bool IsSurveil(ScoutTargetKind kind) => kind == ScoutTargetKind.Surveil;

        public static string Name(ScoutTargetKind kind)
        {
            if (IsRefresh(kind)) return "Refresh";
            if (IsSurveil(kind)) return "Surveil";
            return "Explore";
        }
    }
}
