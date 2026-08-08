using System.Collections.Generic;
using Game.Players;
using Game.Styles;
using UnityEngine;

namespace Game.Setup
{
    // Plain data/logic manager for the pre-game setup screen — deliberately has no UI
    // references, so the rules (random nickname, random unused colour, first player is
    // human) are easy to reason about independently of how the panel presents them.
    public class GameSetupModel
    {
        private static readonly string[] NamePool =
        {
            "Vex", "Kryll", "Draven", "Mordak", "Sable", "Rurik", "Thane", "Vashti",
            "Korrin", "Halden", "Ysolde", "Grimm", "Cassia", "Orlan", "Tessek", "Brynn"
        };

        // Default colour for the first three players, in join order — just what a new player
        // starts with, not a reservation; anyone can still repick via their row's own colour
        // dropdown (PlayerRowUI). Players beyond this bag fall back to a random unused colour,
        // same as before. Indices are into PlayerColorPalette.Colors (1=Blue, 4=Cyan, 0=Steel).
        // Second player defaults to Cyan per the project owner's own call.
        private static readonly int[] DefaultColorIndicesByOrder = { 1, 4, 0 };

        public List<PlayerSetupData> Players { get; } = new List<PlayerSetupData>();
        public int MinPlayers { get; }
        public int MaxPlayers { get; }

        public bool CanAddPlayer => Players.Count < MaxPlayers;
        public bool CanRemovePlayer => Players.Count > MinPlayers;

        public GameSetupModel(int minPlayers, int maxPlayers)
        {
            MinPlayers = minPlayers;
            MaxPlayers = maxPlayers;
        }

        public PlayerSetupData AddPlayer()
        {
            if (!CanAddPlayer)
                return null;

            var data = new PlayerSetupData
            {
                Nickname = PickRandomNickname(),
                ColorIndex = PickColorIndexForNewPlayer(),
                Faction = Faction.Random,
                IsHuman = Players.Count == 0 // first player added defaults to human, the rest AI
            };
            Players.Add(data);
            return data;
        }

        public void RemovePlayer(PlayerSetupData data)
        {
            if (!CanRemovePlayer)
                return;
            Players.Remove(data);
        }

        private string PickRandomNickname()
        {
            var available = new List<string>(NamePool);
            foreach (PlayerSetupData p in Players)
                available.Remove(p.Nickname);

            if (available.Count == 0)
                return NamePool[Random.Range(0, NamePool.Length)]; // pool exhausted, allow repeats

            return available[Random.Range(0, available.Count)];
        }

        // The new player's join-order position picks its default colour, as long as that
        // default isn't already taken (e.g. an earlier player repicked into it by hand) — in
        // which case, same as running past the end of DefaultColorIndicesByOrder, it just
        // falls back to a random unused colour.
        private int PickColorIndexForNewPlayer()
        {
            int order = Players.Count;
            if (order < DefaultColorIndicesByOrder.Length)
            {
                int candidate = DefaultColorIndicesByOrder[order];
                bool taken = false;
                foreach (PlayerSetupData p in Players)
                    if (p.ColorIndex == candidate) { taken = true; break; }
                if (!taken)
                    return candidate;
            }
            return PickRandomUnusedColorIndex();
        }

        private int PickRandomUnusedColorIndex()
        {
            var used = new HashSet<int>();
            foreach (PlayerSetupData p in Players)
                used.Add(p.ColorIndex);

            var available = new List<int>();
            for (int i = 0; i < PlayerColorPalette.Colors.Length; i++)
                if (!used.Contains(i) && i != PlayerColorPalette.NeutralColorIndex)
                    available.Add(i);

            if (available.Count == 0)
                // Pool exhausted, allow repeats — still never Neutral's reserved slot, which
                // NeutralColorIndex guarantees sits last in the array.
                return Random.Range(0, PlayerColorPalette.NeutralColorIndex);

            return available[Random.Range(0, available.Count)];
        }
    }
}
