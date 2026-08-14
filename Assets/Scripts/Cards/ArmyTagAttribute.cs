using UnityEngine;

namespace Game.Cards
{
    // Marks EventDefinition.guardArmyName so its inspector drawer (Assets/Editor/
    // ArmyTagDrawer.cs) shows a per-entry dropdown sourced from NeutralArmyCatalog.armies
    // instead of a free-text field — same idea as [AbilityTag] for CardDefinition.
    // grantedAbilities: the choice list stays data (the catalog asset) rather than compiled
    // code.
    public class ArmyTagAttribute : PropertyAttribute
    {
    }
}
