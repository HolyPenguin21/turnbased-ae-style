using System;

namespace Game.HexGrid
{
    // Axial hex coordinate. Kept as its own type now (rather than a raw Vector2Int) since
    // neighbour/distance queries (movement, supply range) will need proper axial math later.
    public readonly struct HexCoord : IEquatable<HexCoord>
    {
        public readonly int Q;
        public readonly int R;

        public HexCoord(int q, int r)
        {
            Q = q;
            R = r;
        }

        // Odd-q vertical offset -> axial, matching HexTileMeshGenerator's flat-top
        // orientation: every other column is shifted so a rectangular col/row grid tiles
        // without gaps.
        public static HexCoord FromOffset(int col, int row)
        {
            int q = col;
            int r = row - (col - (col & 1)) / 2;
            return new HexCoord(q, r);
        }

        // Inverse of FromOffset — back to the (col, row) rectangular grid coordinates a
        // player/UI thinks in.
        public (int col, int row) ToOffset()
        {
            int col = Q;
            int row = R + (col - (col & 1)) / 2;
            return (col, row);
        }

        public bool Equals(HexCoord other) => Q == other.Q && R == other.R;
        public override bool Equals(object obj) => obj is HexCoord other && Equals(other);
        public override int GetHashCode() => (Q, R).GetHashCode();
        public override string ToString() => $"({Q}, {R})";
    }
}
