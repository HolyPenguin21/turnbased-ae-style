namespace Game.Map
{
    // Fixed draw order (Renderer.sortingOrder) for everything that sits flat at Y=0 on the
    // strategic map — used to live as tunable GameConfig fields, moved here once the values
    // settled and stopped needing per-project tuning. See also HexHighlightStyle.sortingOrder
    // (hex highlights: 1-2) and MoveArrowStyle's own sortingOrder fields (11-15), which stack
    // directly above ArmyIcon, the highest value here.
    public static class MapSortingOrder
    {
        public const int Map = 0;
        // Must stay below BuildingCircle/ArmyCircle — the event marker is meant to sit visually
        // UNDER whichever army/building marker later shares its hex, per the project owner's own
        // call, not compete with them.
        public const int EventIcon = 1;
        public const int BuildingCircle = 3;
        public const int BuildingIcon = 4;
        public const int ArmyCircle = 9;
        public const int ArmyIcon = 10;
    }
}
