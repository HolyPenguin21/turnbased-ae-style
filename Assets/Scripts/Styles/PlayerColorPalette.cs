using UnityEngine;

namespace Game.Styles
{
    // Fixed pool of distinct player colours. Index-based (not enum) so it's trivial to check
    // "is this index already taken" while assigning random unused colours to new players.
    // Green and yellow are deliberately excluded — see TechnicalColors, they're reserved for
    // UI/highlight use across the project and would be ambiguous if a player also had one. Red
    // is excluded for the same reason as of the move-arrow attack colour (see GameConfig.
    // moveArrowAttackColor) — replaced here with Steel rather than removed outright, so
    // existing PlayerSetupData.ColorIndex values still resolve to a valid slot.
    public static class PlayerColorPalette
    {
        public static readonly Color[] Colors =
        {
            new Color(0.55f, 0.60f, 0.65f), // Steel
            new Color(0.15f, 0.35f, 0.80f), // Blue
            new Color(0.55f, 0.25f, 0.70f), // Purple
            new Color(0.80f, 0.45f, 0.10f), // Orange
            new Color(0.10f, 0.55f, 0.60f), // Cyan
            new Color(0.75f, 0.20f, 0.50f), // Pink
            new Color(0.45f, 0.28f, 0.12f), // Brown
            new Color(0.80f, 0.80f, 0.82f), // White
            new Color(0.12f, 0.12f, 0.14f), // Black
        };

        public static readonly string[] Names =
        {
            "Steel", "Blue", "Purple", "Orange", "Cyan", "Pink", "Brown", "White", "Black"
        };
    }
}
