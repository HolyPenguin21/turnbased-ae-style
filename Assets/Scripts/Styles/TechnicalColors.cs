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
    }
}
