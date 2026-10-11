using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // Authored on the radio portrait. Image still owns its sprite, aspect and tint;
    // this modifies only vertex alpha, with no material/texture/object creation.
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Image))]
    public sealed class ArmyRadioArtFeather : BaseMeshEffect
    {
        [Min(0f)] [SerializeField] private float edgeWidth = 6f;
        [Min(0f)] [SerializeField] private float tornDepth = 5f;
        [Min(1f)] [SerializeField] private float grainScale = 9f;
        [Range(1f, 4f)] [SerializeField] private float gridStep = 2f;
        private readonly List<UIVertex> _source = new List<UIVertex>(6);

        public override void ModifyMesh(VertexHelper mesh)
        {
            if (!IsActive() || mesh.currentVertCount == 0) return;
            _source.Clear(); mesh.GetUIVertexStream(_source);
            // The authored portrait is a Simple Image (two triangles). Preserve other
            // Image modes if an artist changes the prefab instead of corrupting their UVs.
            if (_source.Count != 6) return;
            float minX = float.PositiveInfinity, minY = float.PositiveInfinity;
            float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;
            foreach (var vertex in _source)
            {
                minX = Mathf.Min(minX, vertex.position.x); maxX = Mathf.Max(maxX, vertex.position.x);
                minY = Mathf.Min(minY, vertex.position.y); maxY = Mathf.Max(maxY, vertex.position.y);
            }
            Vector2 size = new Vector2(maxX - minX, maxY - minY);
            if (size.x <= 0f || size.y <= 0f) return;
            UIVertex bl = Corner(minX, minY), br = Corner(maxX, minY);
            UIVertex tl = Corner(minX, maxY), tr = Corner(maxX, maxY);
            int columns = Mathf.Clamp(Mathf.CeilToInt(size.x / Mathf.Max(1f, gridStep)), 2, 64);
            int rows = Mathf.Clamp(Mathf.CeilToInt(size.y / Mathf.Max(1f, gridStep)), 2, 96);
            mesh.Clear();
            for (int row = 0; row <= rows; row++)
                for (int column = 0; column <= columns; column++)
                {
                    float x = (float)column / columns, y = (float)row / rows;
                    UIVertex v = bl;
                    v.position = Vector3.Lerp(Vector3.Lerp(bl.position, br.position, x), Vector3.Lerp(tl.position, tr.position, x), y);
                    v.uv0 = Vector4.Lerp(Vector4.Lerp(bl.uv0, br.uv0, x), Vector4.Lerp(tl.uv0, tr.uv0, x), y);
                    v.color = Color32.Lerp(Color32.Lerp(bl.color, br.color, x), Color32.Lerp(tl.color, tr.color, x), y);
                    v.color.a = (byte)Mathf.RoundToInt(v.color.a * AlphaAt(new Vector2(x * size.x, y * size.y), size, edgeWidth, tornDepth, grainScale));
                    mesh.AddVert(v);
                }
            for (int row = 0; row < rows; row++)
                for (int column = 0; column < columns; column++)
                {
                    int a = row * (columns + 1) + column;
                    int b = a + columns + 1;
                    // Match Image's clockwise UI winding and preserve its canvas/mask data.
                    mesh.AddTriangle(a, b, b + 1); mesh.AddTriangle(b + 1, a + 1, a);
                }
        }

        private UIVertex Corner(float x, float y)
        {
            foreach (var vertex in _source)
                if (Mathf.Abs(vertex.position.x - x) < .001f && Mathf.Abs(vertex.position.y - y) < .001f) return vertex;
            return _source[0];
        }

        public static float AlphaAt(Vector2 point, Vector2 size, float width, float depth, float grain)
        {
            float shortest = Mathf.Min(size.x, size.y);
            if (shortest <= 0f) return 0f;
            width = Mathf.Clamp(width, 0f, shortest * .35f);
            depth = Mathf.Clamp(depth, 0f, shortest * .12f);
            if (width <= 0f && depth <= 0f) return 1f;
            grain = Mathf.Max(1f, grain);
            return Edge(point.x, point.y / grain, 1.3f, width, depth)
                * Edge(size.x - point.x, point.y / grain, 4.9f, width, depth)
                * Edge(point.y, point.x / grain, 8.1f, width, depth)
                * Edge(size.y - point.y, point.x / grain, 12.7f, width, depth);
        }

        private static float Edge(float distance, float along, float phase, float width, float depth)
        {
            float noise = Mathf.Sin(along * 1.7f + phase) * .5f
                + Mathf.Sin(along * 3.9f + phase * 1.6f) * .3f
                + Mathf.Sin(along * 9.1f + phase * .7f) * .2f;
            float inset = depth * (.5f + noise * .5f);
            float alpha = Mathf.Clamp01((distance - inset) / Mathf.Max(.001f, width));
            return alpha * alpha * (3f - 2f * alpha);
        }
    }
}
