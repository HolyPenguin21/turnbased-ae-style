using UnityEngine;

namespace Game.Cards
{
    // Marks an inspector field as display-only (see ReadOnlyDrawer in Assets/Editor). Used by
    // CardDefinition.id, which FactionCardCatalog.OnValidate computes automatically — hand-editing
    // it would just get overwritten on the next inspector refresh anyway.
    public class ReadOnlyAttribute : PropertyAttribute
    {
    }
}
