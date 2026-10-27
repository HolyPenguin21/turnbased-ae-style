using System;
using System.Collections.Generic;
using Game.Players;
using Game.HexGrid;

namespace Game.Map
{
    public static partial class VisionSystem
    {
        public static VisionState CaptureState()
        {
            var state = new VisionState { CurrentViewerId = CurrentViewer == null ? null : StateReference.PlayerId(CurrentViewer) };
            var players = new HashSet<PlayerSetupData>(Visible.Keys); players.UnionWith(Visited.Keys); players.UnionWith(EverSeen.Keys);
            foreach (var player in players)
                state.Players.Add(new PlayerVisionState { PlayerId = StateReference.PlayerId(player),
                    Visible = Visible.TryGetValue(player, out var visible) ? new List<HexCoord>(visible) : new List<HexCoord>(),
                    Visited = Visited.TryGetValue(player, out var visited) ? new List<HexCoord>(visited) : new List<HexCoord>(),
                    EverSeen = EverSeen.TryGetValue(player, out var seen) ? new List<HexCoord>(seen) : new List<HexCoord>() });
            return state;
        }
        // Restore the saved perspective directly. RecomputeFor would learn newly placed content.
        public static void RestoreState(VisionState state, Func<string, PlayerSetupData> resolvePlayer)
        {
            if (state?.Players == null) throw new ArgumentException("Missing vision state.");
            var visible = new Dictionary<PlayerSetupData, HashSet<HexCoord>>();
            var visited = new Dictionary<PlayerSetupData, HashSet<HexCoord>>();
            var seen = new Dictionary<PlayerSetupData, HashSet<HexCoord>>();
            foreach (var entry in state.Players)
            {
                if (entry?.Visible == null || entry.Visited == null || entry.EverSeen == null) throw new ArgumentException("Invalid vision player.");
                var player = StateReference.Player(entry.PlayerId, resolvePlayer);
                if (visible.ContainsKey(player)) throw new ArgumentException("Duplicate vision player.");
                visible.Add(player, new HashSet<HexCoord>(entry.Visible)); visited.Add(player, new HashSet<HexCoord>(entry.Visited)); seen.Add(player, new HashSet<HexCoord>(entry.EverSeen));
            }
            var viewer = state.CurrentViewerId == null ? null : StateReference.Player(state.CurrentViewerId, resolvePlayer);
            Visible.Clear(); Visited.Clear(); EverSeen.Clear();
            foreach (var pair in visible) Visible.Add(pair.Key, pair.Value);
            foreach (var pair in visited) Visited.Add(pair.Key, pair.Value);
            foreach (var pair in seen) EverSeen.Add(pair.Key, pair.Value);
            CurrentViewer = viewer;
        }
    }
}
