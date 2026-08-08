using UnityEngine;

namespace Game.Styles
{
    // Fixed pool of distinct player colours. Index-based (not enum) so it's trivial to check
    // "is this index already taken" while assigning random unused colours to new players.
    // Green and yellow are deliberately excluded — see TechnicalColors, they're reserved for
    // UI/highlight use across the project and would be ambiguous if a player also had one. Red
    // is excluded for the same reason as of the move-arrow attack colour (see GameConfig.
    // moveArrowAttackColor). Purple/violet is excluded too as of the Neutral-faction colour
    // below — kept out of the whole family so nothing a real player picks reads as "the same
    // colour as Neutral." White/black/orange were dropped per the project owner's own call
    // (orange sat too close to TechnicalColors.RetreatWarning; white/black read as blending
    // into UI chrome rather than standing out against the map).
    public static class PlayerColorPalette
    {
        // Colors[NeutralColorIndex] is the Neutral faction's colour (see
        // CitadelSetupController's _neutralPlayer) — it has to live in this same array because
        // every marker/UI colour lookup in the codebase is a flat PlayerColorPalette.
        // Colors[somePlayer.ColorIndex] with no special-casing for Neutral. It's excluded from
        // both the setup-screen colour dropdown (PlayerRowUI) and random assignment
        // (GameSetupModel) so no real player can ever end up wearing it. Kept as the LAST
        // index deliberately — GameSetupModel's exhausted-pool fallback relies on that.
        public const int NeutralColorIndex = 11;

        public static readonly Color[] Colors =
        {
            new Color(0.55f, 0.60f, 0.65f), // 0  Steel
            new Color(0.15f, 0.35f, 0.80f), // 1  Blue
            new Color(0.05f, 0.50f, 0.40f), // 2  Teal
            new Color(0.20f, 0.75f, 0.72f), // 3  Turquoise
            new Color(0.10f, 0.55f, 0.60f), // 4  Cyan
            new Color(0.75f, 0.20f, 0.50f), // 5  Pink
            new Color(0.45f, 0.28f, 0.12f), // 6  Brown
            new Color(0.35f, 0.65f, 0.92f), // 7  Sky
            new Color(0.08f, 0.12f, 0.38f), // 8  Navy
            new Color(0.25f, 0.70f, 0.58f), // 9  Seafoam
            new Color(0.30f, 0.40f, 0.60f), // 10 Denim
            new Color(0.30f, 0.15f, 0.45f), // 11 Neutral (reserved, see NeutralColorIndex)
        };

        public static readonly string[] Names =
        {
            "Steel", "Blue", "Teal", "Turquoise", "Cyan", "Pink", "Brown", "Sky", "Navy",
            "Seafoam", "Denim", "Neutral"
        };
    }
}
