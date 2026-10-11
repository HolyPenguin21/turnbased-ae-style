using System.Collections.Generic;
using UnityEngine;

namespace Game.UI
{
    // Shared by every UI component that instantiates a list of child components and needs to
    // tear them all down before repopulating (ArmyButtonRowUI's button row, ArmyViewerModalUI's
    // unit grid) — same "foreach Destroy, then Clear" shape, just for different component types.
    public static class UIListUtility
    {
        public static void DestroyAndClear<T>(List<T> items) where T : Component
        {
            foreach (T item in items)
                if (item != null)
                    Object.Destroy(item.gameObject);
            items.Clear();
        }
    }
}
