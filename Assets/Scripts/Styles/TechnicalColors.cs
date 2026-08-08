using UnityEngine;

namespace Game.Styles
{
    // Reserved colours used for UI/highlight purposes across the project — never offered as
    // a player colour (see PlayerColorPalette, which deliberately excludes these two).
    public static class TechnicalColors
    {
        // Citadel-hex selection marker (CitadelSetupController).
        public static readonly Color CitadelSelection = new Color(0.55f, 0.45f, 0.05f);

        // General hex inspection highlight (HexSelectionController).
        public static readonly Color HexSelection = new Color(0.05f, 0.35f, 0.15f);

        // The currently-acting unit's cell in the Tactical Battle Module grid (UIRaggedGlowUI,
        // see BattleScreenUI/BattleGridCellUI) — a bright "technical" yellow per the user's own
        // spec, distinct from any player colour (see PlayerColorPalette's own exclusion).
        public static readonly Color BattleActingUnit = new Color(0.95f, 0.85f, 0.1f);

        // The round-start title's "X is retreating this round!" callout (BattleRoundStartPopupUI)
        // — a warning orange so it reads as an alert rather than blending into the plain "Round N"
        // text next to it (see the user's own "easy to miss" report on this line).
        public static readonly Color RetreatWarning = new Color(0.95f, 0.5f, 0.1f);
    }
}
