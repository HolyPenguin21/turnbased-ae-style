using System.Collections.Generic;

namespace Game.HexGrid
{
    // A route from one hex to another, as found by HexPathfinder — Hexes includes the start
    // hex itself as the first element (already occupied, costs nothing), TotalCost is the sum
    // of every hex actually entered after that.
    public class HexPath
    {
        public readonly List<HexCoord> Hexes;
        public readonly int TotalCost;

        public HexPath(List<HexCoord> hexes, int totalCost)
        {
            Hexes = hexes;
            TotalCost = totalCost;
        }
    }
}
