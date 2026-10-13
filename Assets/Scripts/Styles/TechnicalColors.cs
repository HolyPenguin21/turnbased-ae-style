using UnityEngine;

namespace Game.Styles
{
    // Reserved colours used for UI/highlight purposes across the project — never offered as
    // a player colour (see PlayerColorPalette).
    public static class TechnicalColors
    {
        // General hex inspection highlight (HexSelectionController): neutral sun-bleached paint,
        // deliberately kept away from the old olive/yellow shader colour.
        public static readonly Color HexSelection = new Color(0.97f, 0.96f, 0.92f);
    }
}

