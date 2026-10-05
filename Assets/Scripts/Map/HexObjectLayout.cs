using System.Collections.Generic;
using Game.Core;
using Game.Players;
using UnityEngine;

namespace Game.Map
{
    // Offsets are in hex-radius units: X is world X, Y is world Z.
    // Callers supply only distinct owners visible to the current viewer, or a frozen
    // remembered snapshot. This resolver never reads live occupants or visibility itself.
    public static class HexObjectLayout
    {
        public readonly struct Result
        {
            public readonly Vector2 BuildingOffset;
            public readonly Vector2[] ArmyOffsets; // same order as the armyOwners list passed in

            public Result(Vector2 buildingOffset, Vector2[] armyOffsets)
            {
                BuildingOffset = buildingOffset;
                ArmyOffsets = armyOffsets;
            }
        }

        public static Result Resolve(GameConfig config, bool hasBuilding, IReadOnlyList<PlayerSetupData> armyOwners)
        {
            int armyCount = armyOwners?.Count ?? 0;
            var armyOffsets = new Vector2[armyCount];

            if (armyCount <= 1)
            {
                if (hasBuilding && armyCount == 1)
                    armyOffsets[0] = new Vector2(0.25f, -0.25f);
                return new Result(Vector2.zero, armyOffsets);
            }

            // Rank visible owners by their fixed player identity, never registry insertion
            // order. Return offsets in the caller's original order so all callers agree.
            var order = new List<int>();
            for (int i = 0; i < armyCount; i++) order.Add(i);
            order.Sort((a, b) => CompareOwners(armyOwners[a], armyOwners[b]));
            for (int rank = 0; rank < armyCount; rank++)
                armyOffsets[order[rank]] = Slot(hasBuilding, armyCount, rank);
            return new Result(Vector2.zero, armyOffsets);
        }

        private static int CompareOwners(PlayerSetupData a, PlayerSetupData b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a == null) return -1;
            if (b == null) return 1;
            int neutral = a.IsNeutral.CompareTo(b.IsNeutral);
            if (neutral != 0) return neutral;
            int colour = a.ColorIndex.CompareTo(b.ColorIndex);
            if (colour != 0) return colour;
            return string.CompareOrdinal(a.Nickname, b.Nickname);
        }

        private static Vector2 Slot(bool building, int count, int rank)
        {
            if (building)
            {
                if (count == 2) return new Vector2(rank == 0 ? -0.34f : 0.34f, -0.34f);
                if (count == 3)
                    return rank == 1 ? new Vector2(0f, -0.54f)
                        : new Vector2(rank == 0 ? -0.50f : 0.50f, -0.30f);
                // For 4+ owners distribute along a lower arc, safely inside the hex.
                float t = rank / (float)(count - 1);
                return new Vector2(Mathf.Lerp(-0.60f, 0.60f, t),
                    -0.28f - 0.28f * Mathf.Sin(t * Mathf.PI));
            }
            if (count == 2) return new Vector2(rank == 0 ? -0.28f : 0.28f, 0f);
            if (count == 3)
                return rank == 0 ? new Vector2(0f, 0.28f)
                    : new Vector2(rank == 1 ? -0.30f : 0.30f, -0.22f);
            if (count == 4)
                return new Vector2(rank % 2 == 0 ? -0.28f : 0.28f, rank < 2 ? 0.26f : -0.26f);
            float angle = 2f * Mathf.PI * rank / count;
            return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * 0.40f;
        }
    }
}
