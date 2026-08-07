using System.Collections.Generic;
using Game.HexGrid;
using Game.Terrain;
using UnityEngine;

namespace Game.Map
{
    // Pure data holder for a generated hex map: dimensions, per-hex terrain lookup, and
    // world<->hex conversions. Doesn't know how to build itself — HexMapGenerator populates
    // this once and then removes itself, so gameplay scripts only ever depend on the data,
    // never on the tool that produced it.
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class HexMap : MonoBehaviour
    {
        [SerializeField] private int width;
        [SerializeField] private int height;
        [SerializeField] private float outerRadius;

        private readonly Dictionary<HexCoord, TerrainTypeEntry> _hexData = new Dictionary<HexCoord, TerrainTypeEntry>();

        public int Width => width;
        public int Height => height;
        public float OuterRadius => outerRadius;

        public bool TryGetTerrainAt(HexCoord coord, out TerrainTypeEntry entry) => _hexData.TryGetValue(coord, out entry);

        public HexCoord WorldToHex(Vector3 worldPos) => HexGridMath.WorldToAxial(transform.InverseTransformPoint(worldPos), outerRadius);

        public Vector3 HexToWorld(HexCoord coord) => transform.TransformPoint(HexGridMath.AxialToWorld(coord.Q, coord.R, outerRadius));

        // Called once by HexMapGenerator right after building the mesh.
        public void SetData(int mapWidth, int mapHeight, float radius, Dictionary<HexCoord, TerrainTypeEntry> hexData)
        {
            width = mapWidth;
            height = mapHeight;
            outerRadius = radius;
            _hexData.Clear();
            foreach (KeyValuePair<HexCoord, TerrainTypeEntry> entry in hexData)
                _hexData[entry.Key] = entry.Value;
        }
    }
}
