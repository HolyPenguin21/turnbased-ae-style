using UnityEngine;

namespace Game.UI
{
    // All positions are in the overlay Canvas's local space, after camera projection.
    public static class ArmyRadioLayout
    {
        public static Vector2 ArtSize(Sprite art, float height)
        {
            if (art == null || art.rect.height <= 0f) return Vector2.zero;
            return new Vector2(height * art.rect.width / art.rect.height, height);
        }

        public static Rect Inset(Rect rect, float margin) => Rect.MinMaxRect(
            rect.xMin + margin, rect.yMin + margin, rect.xMax - margin, rect.yMax - margin);

        // Choose the diagonal with most room; try all four directions before clamping.
        // Portrait stays at the end nearest the marker, while text always reads normally.
        public static Vector2 Place(Rect safe, Vector2 target, Vector2 size, float gap, out bool cardOnLeft)
        {
            float horizontal = target.x <= safe.center.x ? 1f : -1f;
            float vertical = target.y <= safe.center.y ? 1f : -1f;
            Vector2 best = Vector2.zero;
            float bestPenalty = float.PositiveInfinity;
            cardOnLeft = horizontal > 0f;
            for (int i = 0; i < 4; i++)
            {
                float x = i % 2 == 0 ? horizontal : -horizontal;
                float y = i < 2 ? vertical : -vertical;
                Vector2 proposed = target + new Vector2(x * (size.x * .5f + gap), y * (size.y * .5f + gap));
                Vector2 clamped = new Vector2(
                    ClampCentre(proposed.x, safe.xMin, safe.xMax, size.x),
                    ClampCentre(proposed.y, safe.yMin, safe.yMax, size.y));
                float penalty = (proposed - clamped).sqrMagnitude;
                if (penalty >= bestPenalty) continue;
                best = clamped; bestPenalty = penalty; cardOnLeft = x > 0f;
            }
            return best;
        }

        private static float ClampCentre(float value, float min, float max, float size) =>
            max - min < size ? (min + max) * .5f : Mathf.Clamp(value, min + size * .5f, max - size * .5f);

        public static bool Pointer(Rect panel, Vector2 target, float radius, float markerRadius,
            out Vector2 a, out Vector2 b, out Vector2 tip)
        {
            a = b = tip = Vector2.zero;
            if (panel.Contains(target) || panel.width < radius * 2 || panel.height < radius * 2) return false;
            Vector2 d = target - panel.center;
            bool side = Mathf.Abs(d.x) / panel.width > Mathf.Abs(d.y) / panel.height;
            float halfBase = 4f;
            if (side)
            {
                float x = d.x > 0f ? panel.xMax : panel.xMin;
                float y = Mathf.Clamp(panel.center.y + d.y * Mathf.Abs((x - panel.center.x) / d.x),
                    panel.yMin + radius + halfBase, panel.yMax - radius - halfBase);
                a = new Vector2(x, y - halfBase); b = new Vector2(x, y + halfBase);
            }
            else
            {
                float y = d.y > 0f ? panel.yMax : panel.yMin;
                float x = Mathf.Clamp(panel.center.x + d.x * Mathf.Abs((y - panel.center.y) / d.y),
                    panel.xMin + radius + halfBase, panel.xMax - radius - halfBase);
                a = new Vector2(x - halfBase, y); b = new Vector2(x + halfBase, y);
            }
            Vector2 start = (a + b) * .5f;
            Vector2 ray = target - start;
            if (ray.magnitude <= markerRadius) return false;
            tip = target - ray.normalized * markerRadius;
            return true;
        }
    }
}
