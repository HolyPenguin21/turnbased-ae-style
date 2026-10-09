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

        public bool Equals(HexCoord other) => Q == other.Q && R == other.R;
        public override bool Equals(object obj) => obj is HexCoord other && Equals(other);
        public override int GetHashCode() => (Q, R).GetHashCode();
        public override string ToString() => $"({Q}, {R})";
    }
}
