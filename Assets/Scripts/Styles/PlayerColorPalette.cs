using UnityEngine;

namespace Game.Styles
{
    // Fixed pool of six map-readable player colours. Index-based (not enum) so it's trivial to
    // check "is this index already taken" while assigning random unused colours to new players.
    // The selectable palette deliberately omits the old dark/gunmetal and violet options.
    // Violet remains only in the reserved Neutral slot below, so no real player can pick it.
    public static class PlayerColorPalette
    {
        // Colors[NeutralColorIndex] is the Neutral faction's colour (see
        // CitadelSetupController's _neutralPlayer) — it has to live in this same array because
        // every marker/UI colour lookup in the codebase is a flat PlayerColorPalette.
        // Colors[somePlayer.ColorIndex] with no special-casing for Neutral. It's excluded from
        // both the setup-screen colour dropdown (PlayerRowUI) and random assignment
        // (GameSetupModel) so no real player can ever end up wearing it. Kept as the LAST
        // index deliberately — GameSetupModel's exhausted-pool fallback relies on that.
        public const int NeutralColorIndex = 6;

        public static readonly Color[] Colors =
        {
            new Color(0.2470588f, 0.4039216f, 0.7764706f), // 0 Cobalt       #3F67C6
            new Color(0.1843137f, 0.5568627f, 0.5137255f), // 1 Teal         #2F8E83
            new Color(0.3529412f, 0.5490196f, 0.3333333f), // 2 Sage Green   #5A8C55
            new Color(0.7686275f, 0.5803922f, 0.1960784f), // 3 Ochre        #C49432
            new Color(0.7725490f, 0.4235294f, 0.2274510f), // 4 Burnt Orange #C56C3A
            new Color(0.7058824f, 0.3176471f, 0.3333333f), // 5 Brick Red    #B45155
            new Color(0.30f, 0.15f, 0.45f),               // 6 Neutral (reserved)
        };

        public static readonly string[] Names =
        {
            "Cobalt", "Teal", "Sage Green", "Ochre", "Burnt Orange", "Brick Red", "Neutral"
        };
    }
}
