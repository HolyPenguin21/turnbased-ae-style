using UnityEngine;

namespace Game.Core
{
    // Destroy for code that also runs outside Play Mode (EditMode tests, editor tools): Unity
    // refuses deferred Destroy there, so the object goes immediately.
    public static class UnityObjectLifetime
    {
        public static void Destroy(Object value)
        {
            if (value == null) return;
            if (Application.isPlaying) Object.Destroy(value);
            else Object.DestroyImmediate(value);
        }
    }
}
