using System.Collections.Generic;
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

        public static Rect Bounds(Vector2 centre, Vector2 size) => new Rect(centre - size * .5f, size);

        // Keep the previous camera-relative offset when it still fits. Otherwise try nearby
        // rows on both sides. Never clamp into another message or cover the speaker marker.
        public static bool TryPlace(Rect safe, Vector2 target, Vector2 size, float gap,
            IList<Rect> occupied, Vector2? previousOffset, out Vector2 position, out bool cardOnLeft, float artWidth = 0f, float panelHeight = 0f, float markerRadius = 0f, float cornerRadius = 9f)
        {
            position = Vector2.zero; cardOnLeft = true;
            if (size.x > safe.width || size.y > safe.height) return false;
            if (previousOffset.HasValue)
            {
                Vector2 old = target + previousOffset.Value;
                if (Fits(old, size, target, gap, safe, occupied, artWidth, panelHeight, markerRadius, cornerRadius))
                { position = old; cardOnLeft = old.x > target.x; return true; }
            }
            float horizontal = target.x <= safe.center.x ? 1f : -1f;
            float vertical = target.y <= safe.center.y ? 1f : -1f;
            float score = float.PositiveInfinity;
            for (int row = 0; row < 4; row++)
                for (int side = 0; side < 8; side++)
                {
                    float x = side % 2 == 0 ? horizontal : -horizontal;
                    float y = side < 2 ? vertical : -vertical;
                    Vector2 distance;
                    if (side < 4)
                        distance = new Vector2(x * (size.x * .5f + gap), y * (size.y * .5f + gap + row * (size.y + 12f)));
                    else if (side < 6)
                        distance = new Vector2(x * (size.x * .5f + gap + row * (size.x + 12f)), 0f);
                    else
                        distance = new Vector2(0f, (side == 6 ? vertical : -vertical) * (size.y * .5f + gap + row * (size.y + 12f)));
                    Vector2 proposed = target + distance;
                    Vector2 candidate = new Vector2(ClampCentre(proposed.x, safe.xMin, safe.xMax, size.x),
                        ClampCentre(proposed.y, safe.yMin, safe.yMax, size.y));
                    if (!Fits(candidate, size, target, gap, safe, occupied, artWidth, panelHeight, markerRadius, cornerRadius)) continue;
                    float penalty = (candidate - target).sqrMagnitude + (candidate - proposed).sqrMagnitude;
                    if (penalty >= score) continue;
                    score = penalty; position = candidate; cardOnLeft = candidate.x > target.x;
                }
            return !float.IsPositiveInfinity(score);
        }

        private static bool Fits(Vector2 centre, Vector2 size, Vector2 target, float gap, Rect safe, IList<Rect> occupied, float artWidth, float panelHeight, float markerRadius, float cornerRadius)
        {
            Rect rect = Bounds(centre, size);
            if (rect.xMin < safe.xMin || rect.xMax > safe.xMax || rect.yMin < safe.yMin || rect.yMax > safe.yMax) return false;
            Rect padded = Inset(rect, -8f);
            if (padded.Overlaps(Bounds(target, Vector2.one * Mathf.Max(4f, gap - 10f) * 2f))) return false;
            for (int i = 0; i < occupied.Count; i++)
                if (padded.Overlaps(occupied[i])) return false;
            Rect panel = PanelBounds(centre, size, artWidth, panelHeight > 0f ? panelHeight : size.y, centre.x > target.x);
            if (Pointer(panel, target, cornerRadius, markerRadius, out var a, out var b, out var tip)
                && !PointerPathClear((a + b) * .5f, tip, occupied)) return false;
            return true;
        }

        public static Rect PanelBounds(Vector2 centre, Vector2 size, float artWidth, float height, bool cardOnLeft) =>
            Bounds(centre + new Vector2((cardOnLeft ? 1f : -1f) * artWidth * .5f, 0f), new Vector2(size.x - artWidth, height));

        // Small conservative rectangles cover the thin pointer, including its base.
        // This lets later cards avoid earlier connectors without reserving a huge bounding box.
        public static void AddPointerObstacles(Vector2 start, Vector2 tip, IList<Rect> occupied)
        {
            int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(start, tip) / 10f));
            for (int i = 0; i < steps; i++)
            {
                Vector2 a = Vector2.Lerp(start, tip, (float)i / steps);
                Vector2 b = Vector2.Lerp(start, tip, (float)(i + 1) / steps);
                occupied.Add(Rect.MinMaxRect(Mathf.Min(a.x, b.x) - 5f, Mathf.Min(a.y, b.y) - 5f,
                    Mathf.Max(a.x, b.x) + 5f, Mathf.Max(a.y, b.y) + 5f));
            }
        }
        public static bool PointerPathClear(Vector2 start, Vector2 tip, IList<Rect> occupied)
        {
            int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(start, tip) / 6f));
            for (int i = 0; i <= steps; i++)
            {
                Vector2 point = Vector2.Lerp(start, tip, (float)i / steps);
                for (int j = 0; j < occupied.Count; j++)
                    if (Inset(occupied[j], -5f).Contains(point)) return false;
            }
            return true;
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
