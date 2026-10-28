using System;
using System.Collections.Generic;
using Game.HexGrid;
using Game.Players;
using Game.Units;

namespace Game.Map
{
    // Owner-specific facts only; this is not a world/entity serializer or a save format.
    [Serializable] public sealed class ArmyActivationState
    {
        public bool HasActivated;
        public List<int> CoveredUnitIds = new List<int>();
    }
    [Serializable] public sealed class UnitMovementState
    {
        public int PermanentMaximum;
        public int LegacyCurrent;
        public SerializableNullableInt Spent;
        public SerializableNullableInt TransportCarrierId;
        public SerializableNullableInt TransportArmyId;
    }
    [Serializable] public sealed class PlayerVisionState
    {
        public string PlayerId;
        public List<HexCoord> Visible = new List<HexCoord>();
        public List<HexCoord> Visited = new List<HexCoord>();
        public List<HexCoord> EverSeen = new List<HexCoord>();
    }
    [Serializable] public sealed class VisionState
    {
        public string CurrentViewerId;
        public List<PlayerVisionState> Players = new List<PlayerVisionState>();
    }
    internal static class StateReference
    {
        internal static UnitData Unit(int id, Func<int, UnitData> resolve)
        {
            var value = resolve?.Invoke(id);
            if (id <= 0 || value == null || value.RuntimeId != id)
                throw new ArgumentException("Unresolved unit identity: " + id);
            return value;
        }
        internal static ArmyData Army(int id, Func<int, ArmyData> resolve)
        {
            var value = resolve?.Invoke(id);
            if (id < 0 || value == null || value.Id != id)
                throw new ArgumentException("Unresolved army identity: " + id);
            return value;
        }
        internal static PlayerSetupData Player(string id, Func<string, PlayerSetupData> resolve)
        {
            var value = resolve?.Invoke(id);
            if (string.IsNullOrEmpty(id) || value == null || value.PlayerId != id)
                throw new ArgumentException("Unresolved player identity: " + id);
            return value;
        }
        internal static string PlayerId(PlayerSetupData player)
        {
            if (player == null || string.IsNullOrEmpty(player.PlayerId))
                throw new InvalidOperationException("Assign player identities before capturing state.");
            return player.PlayerId;
        }
    }
}
