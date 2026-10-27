namespace Game.Players
{
    // Plain runtime data — never shown in a Unity Inspector, so it doesn't need
    // [Serializable] (which would otherwise warn about the int? fields below anyway; Unity's
    // serializer doesn't support Nullable<T>).
    public class PlayerSetupData
    {
        // Stable match-local key, assigned by setup and preserved by a future save envelope.
        // Names, colors and turn-order positions are deliberately not identities.
        public string PlayerId;
        public Game.Cards.PlayerHandData Hand;
        public string Nickname;
        public int ColorIndex;
        public Faction Faction;
        public bool IsHuman;
        public string SelectedDeckId;
        // AI players only: the StartingDeckCatalog.aiDecks entry chosen for this match.
        public string AiDeckName;
        // Set only after the campaign's free leader is materialized. Consumed before any draws.
        public string AiGrantedLeaderKey;
        // AI players only: authoredKeys of the chosen AI deck's blueprints; null = unrestricted.
        public System.Collections.Generic.HashSet<string> AiBlueprints;
        public Game.Cards.MatchLoadout MatchLoadout;
        public Game.Cards.BlueprintQuota BlueprintQuota;
        public readonly Game.Cards.TacticHand Tactics = new Game.Cards.TacticHand();
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

