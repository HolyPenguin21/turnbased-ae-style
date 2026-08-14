namespace Game.Players
{
    // Plain runtime data — never shown in a Unity Inspector, so it doesn't need
    // [Serializable] (which would otherwise warn about the int? fields below anyway; Unity's
    // serializer doesn't support Nullable<T>).
    public class PlayerSetupData
    {
        public string Nickname;
        public int ColorIndex;
        public Faction Faction;
        public bool IsHuman;
        // True only for CitadelSetupController's single shared _neutralPlayer instance — never
        // set on a real player profile. Explicit flag rather than inferring it from ColorIndex/
        // Faction.None elsewhere (see BattleScreenUI.TryAssessSideRetreat's own "neutrals never
        // retreat" rule, per the user's own spec) — those already uniquely identify the neutral
        // player today, but only as a side effect of how it happens to be constructed, not
        // because either field means "neutral" anywhere else in this codebase.
        public bool IsNeutral;

        // Where this player's citadel ended up. Plain ints rather than HexCoord — this is a
        // player profile, not a map-geometry type. Null until the citadel-placement step has
        // actually run.
        public int? CitadelHexQ;
        public int? CitadelHexR;

        // Set once, permanently, by GameTurnController.EliminatePlayer — a starting citadel
        // capture doesn't set this immediately (see StartingCitadelLost's own comment on the
        // recapture buffer), only losing it AND still not owning it again by the start of this
        // player's own next turn does.
        public bool IsEliminated;
    }
}
