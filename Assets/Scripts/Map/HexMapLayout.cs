using System;
using Game.Terrain;

namespace Game.Map
{
    // Save boundary only: primitive values and stable authored content keys, never scene,
    // texture or terrain references. HexMap owns a copy; callers receive their own copy.
    [Serializable]
    public sealed class HexMapLayout
    {
        public int Version = 1;
        public int FieldRadius;
        public float OuterRadius;
        public Biome Biome;
        public float Blend;
        public float Alpha;
        public MapLayoutColor GroundColor;
        public MapLayoutColor BorderTint;
        public MapLayoutCell[] Cells = Array.Empty<MapLayoutCell>();
        public MapLayoutCell[] BorderCells = Array.Empty<MapLayoutCell>();
        public MapLayoutComplex[] Complexes = Array.Empty<MapLayoutComplex>();

        public HexMapLayout Copy() => new HexMapLayout
        {
            Version = Version, FieldRadius = FieldRadius, OuterRadius = OuterRadius,
            Biome = Biome, Blend = Blend, Alpha = Alpha, GroundColor = GroundColor, BorderTint = BorderTint,
            Cells = CopyCells(Cells), BorderCells = CopyCells(BorderCells),
            Complexes = Complexes == null ? null : Array.ConvertAll(Complexes, c => c?.Copy()),
        };

        internal static MapLayoutCell[] CopyCells(MapLayoutCell[] cells) => cells == null
            ? null : Array.ConvertAll(cells, c => c?.Copy());
    }

    [Serializable]
    public struct MapLayoutColor
    {
        public float R, G, B, A;
        public MapLayoutColor(float r, float g, float b, float a) { R = r; G = g; B = b; A = a; }
    }

    [Serializable]
    public sealed class MapLayoutCell
    {
        public int Q, R;
        public string TerrainKey;
        // Complex parts use the template/part mapping below instead of a baseline texture.
        public string TextureKey;
        public MapLayoutCell Copy() => (MapLayoutCell)MemberwiseClone();
    }

    [Serializable]
    public sealed class MapLayoutComplex
    {
        public string TemplateKey;
        public int RotationSteps;
        public int Phase;
        public float FramesPerSecond;
        // Array order is authored part order, preserved even when the footprint is rotated.
        public MapLayoutCell[] Parts = Array.Empty<MapLayoutCell>();
        public MapLayoutComplex Copy() => new MapLayoutComplex
        {
            TemplateKey = TemplateKey, RotationSteps = RotationSteps, Phase = Phase,
            FramesPerSecond = FramesPerSecond, Parts = HexMapLayout.CopyCells(Parts),
        };
    }
}
