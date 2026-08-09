using UnityEngine;

namespace Game.Cards
{
    // Marks CardDefinition.grantedAbilities so its inspector drawer (Assets/Editor/
    // AbilityTagDrawer.cs) shows a per-entry dropdown sourced from UnitAbilityCatalog.
    // knownAbilities instead of a free-text field — same effect as UnitTypeTag being a real
    // enum, but the tag list stays data (the catalog asset) rather than compiled code, since
    // knownAbilities is meant to be tuned by editing that one asset.
    public class AbilityTagAttribute : PropertyAttribute
    {
    }
}
