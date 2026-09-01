using System.Collections.Generic;
using Game.HexGrid;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  SCOUT TRAIL REGISTRY  (Strategy V2 — spec §5, reduce avoidable backtracking)
    // ===========================================================================================
    //  Bounded per-scout movement history so route ranking can prefer new information + forward
    //  progress over the shortest geometric route back through ground the scout has just crossed.
    //  Three tiers, matching the spec:
    //    · JustLeft         — the exact hex vacated on the previous step. Strongest retrace signal
    //                         (an immediate A->B->A reversal).
    //    · Recent trail     — a bounded ring of the last scoutTrailLength hexes actually stepped on.
    //    · Older visited     — not stored here; the caller reads that from the world snapshot's
    //                         VisitedHexSet and weights it most lightly.
    //  It is a preference, never a prohibition: nothing here blocks a hex. Cleared on session
    //  reset alongside the other V2 recon registries.
    // ===========================================================================================
    internal static class ScoutTrailRegistry
    {
        private sealed class Trail
        {
            public HexCoord? JustLeft;
            public readonly List<HexCoord> Recent = new List<HexCoord>();
        }

        private static readonly Dictionary<PlayerSetupData, Dictionary<int, Trail>> ByPlayer =
            new Dictionary<PlayerSetupData, Dictionary<int, Trail>>();

        public static void ClearAll() => ByPlayer.Clear();

        // Called once from AiTurnController.MoveArmyRoutine when a solo scout actually changed hex.
        public static void RecordStep(PlayerSetupData player, int armyId, HexCoord from, HexCoord to)
        {
            if (player == null || from.Equals(to))
                return;
            if (!ByPlayer.TryGetValue(player, out Dictionary<int, Trail> byArmy))
                ByPlayer[player] = byArmy = new Dictionary<int, Trail>();
            if (!byArmy.TryGetValue(armyId, out Trail t))
                byArmy[armyId] = t = new Trail();

            t.JustLeft = from;
            t.Recent.Add(to);
            int max = System.Math.Max(2, AiConfigV2.scoutTrailLength);
            if (t.Recent.Count > max)
                t.Recent.RemoveRange(0, t.Recent.Count - max);
        }

        // True iff the scout's next step would put it straight back on the hex it just left.
        public static bool IsImmediateReversal(PlayerSetupData player, int armyId, HexCoord firstStepHex)
        {
            return Get(player, armyId, out Trail t) && t.JustLeft.HasValue
                && t.JustLeft.Value.Equals(firstStepHex);
        }

        // How many hexes of a candidate route lie on the scout's recent trail.
        public static int RecentTrailHits(PlayerSetupData player, int armyId, IEnumerable<HexCoord> routeHexes)
        {
            if (routeHexes == null || !Get(player, armyId, out Trail t) || t.Recent.Count == 0)
                return 0;
            var recent = new HashSet<HexCoord>(t.Recent);
            int hits = 0;
            foreach (HexCoord h in routeHexes)
                if (recent.Contains(h))
                    hits++;
            return hits;
        }

        private static bool Get(PlayerSetupData player, int armyId, out Trail trail)
        {
            trail = null;
            return player != null
                && ByPlayer.TryGetValue(player, out Dictionary<int, Trail> byArmy)
                && byArmy.TryGetValue(armyId, out trail);
        }
    }
}
