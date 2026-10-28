using System;

namespace Game.HexGrid
{
    // Axial hex coordinate. Kept as its own type now (rather than a raw Vector2Int) since
    // neighbour/distance queries (movement, supply range) will need proper axial math later.
    // Fields are not readonly so Unity serialization (state snapshots) can store them; treat as immutable.
    [Serializable]
    public struct HexCoord : IEquatable<HexCoord>
    {
        // Exact detached save reconstruction; no planning, registration or normalization.
        internal HexCoord(bool __matchSave, int __Q, int __R) : this(__Q, __R) { }

        public int Q;
        public int R;

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
