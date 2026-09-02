using System.Collections.Generic;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // V2-only per-hex observation age. This deliberately sits BESIDE AiReconMemory: that class
    // remembers enemy contacts, while this one remembers when map information itself was last
    // refreshed. Observed != Visited — this class never writes VisionSystem's Visited/EverSeen
    // state and therefore aircraft can refresh intelligence without completing ground Explore.
    public static class AiReconIntelMemory
    {
        private static readonly Dictionary<PlayerSetupData, Dictionary<HexCoord, int>> LastObservedByPlayer =
            new Dictionary<PlayerSetupData, Dictionary<HexCoord, int>>();
        private static readonly Dictionary<PlayerSetupData, int> CurrentTurnByPlayer =
            new Dictionary<PlayerSetupData, int>();

        static AiReconIntelMemory()
        {
            // VisionSystem is authoritative for CURRENT visibility and already recomputes it after
            // every movement step. Listening here means a V2 scout/aircraft cannot move one hex and
            // keep stale ages until the next strategic scan. VisibleContentChanged covers the
            // complementary case where the visible set itself did not change but information on an
            // already-visible hex did.
            VisionSystem.VisibilityChanged += OnVisibilityChanged;
            VisionSystem.VisibleContentChanged += OnVisibleContentChanged;
        }

        public static void Clear()
        {
            LastObservedByPlayer.Clear();
            CurrentTurnByPlayer.Clear();
        }

        // Called by the normal V2 world-scan observation seam. Stamps every hex currently visible
        // to the AI at age 0 for this turn, then leaves the VisionSystem subscriptions above to
        // keep the memory fresh after each live movement/content update during the turn.
        public static void ObserveCurrentVisibility(PlayerSetupData player, int currentTurn)
        {
            if (player == null)
                return;

            CurrentTurnByPlayer[player] = currentTurn;
            Dictionary<HexCoord, int> store = StoreFor(player);
            foreach (HexCoord hex in VisionSystem.VisibleHexesFor(player))
                store[hex] = currentTurn;
        }

        public static bool TryGetLastObservedTurn(PlayerSetupData player, HexCoord hex, out int lastObservedTurn)
        {
            lastObservedTurn = 0;
            return player != null
                && LastObservedByPlayer.TryGetValue(player, out Dictionary<HexCoord, int> store)
                && store.TryGetValue(hex, out lastObservedTurn);
        }

        // Never-observed is represented by `false`, not an artificial huge age. This keeps
        // Exploration (unknown map) structurally distinct from Intelligence Refresh (known-but-
        // stale map) and prevents a never-seen hex from accidentally winning a refresh score.
        public static bool TryGetIntelAge(PlayerSetupData player, HexCoord hex, int currentTurn, out int age)
        {
            age = 0;
            if (!TryGetLastObservedTurn(player, hex, out int observedTurn))
                return false;
            age = System.Math.Max(0, currentTurn - observedTurn);
            return true;
        }

        // Immutable-by-convention copy for a frozen strategic snapshot. Callers cannot mutate the
        // live registry through the returned dictionary.
        public static IReadOnlyDictionary<HexCoord, int> Snapshot(PlayerSetupData player)
        {
            if (player == null || !LastObservedByPlayer.TryGetValue(player, out Dictionary<HexCoord, int> store))
                return new Dictionary<HexCoord, int>();
            return new Dictionary<HexCoord, int>(store);
        }

        private static Dictionary<HexCoord, int> StoreFor(PlayerSetupData player)
        {
            if (!LastObservedByPlayer.TryGetValue(player, out Dictionary<HexCoord, int> store))
                LastObservedByPlayer[player] = store = new Dictionary<HexCoord, int>();
            return store;
        }

        private static void OnVisibilityChanged(PlayerSetupData player)
        {
            if (player != null && CurrentTurnByPlayer.TryGetValue(player, out int turn))
                ObserveCurrentVisibility(player, turn);
        }

        private static void OnVisibleContentChanged(PlayerSetupData player, HexCoord hex)
        {
            if (player == null || !CurrentTurnByPlayer.TryGetValue(player, out int turn)
                || !VisionSystem.IsVisible(player, hex))
                return;
            StoreFor(player)[hex] = turn;
        }
    }
}
