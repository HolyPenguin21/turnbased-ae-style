using System.Collections.Generic;
using Game.Players;

namespace Game.Map
{
    // Every player's PlayerRoot, keyed by their PlayerSetupData — mirrors ArmyRegistry/
    // BuildingRegistry. Needed wherever turn/economy logic (GameTurnController,
    // TurnOrderResolver, AI) has a PlayerSetupData and needs that player's live state
    // (resources, bonus initiative dice) without a scene reference to their PlayerRoot.
    public static class PlayerRootRegistry
    {
        private static readonly Dictionary<PlayerSetupData, PlayerRoot> ByPlayer = new Dictionary<PlayerSetupData, PlayerRoot>();

        public static void Clear()
        {
            ByPlayer.Clear();
        }

        public static void Register(PlayerSetupData player, PlayerRoot root)
        {
            if (player == null || root == null)
                return;
            ByPlayer[player] = root;
        }

        public static PlayerRoot FindFor(PlayerSetupData player)
        {
            if (player == null)
                return null;
            return ByPlayer.TryGetValue(player, out PlayerRoot root) ? root : null;
        }
    }
}
