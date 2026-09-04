using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    public enum AirSortieKind
    {
        Strike,
        Recon,
    }

    // Minimal per-air-army execution record: one aircraft group is currently flying a sortie,
    // committed to landing at LandingHex, heading Outbound to TargetHex or back. Pure runtime
    // bookkeeping for aviation execution — landing-slot accounting reads it, the recon-air
    // executor advances it. It is NOT the old strategic task vocabulary (no category, no
    // 20-value kind, no lifecycle fields): a sortie is Strike or Recon and nothing more.
    public sealed class AirSortie
    {
        public ArmyData Army;
        public AirSortieKind Kind;
        public HexCoord TargetHex;   // current travel destination: the action hex while Outbound, the landing hex after
        public HexCoord LandingHex;  // owned airfield this sortie is committed to landing at
        public bool Outbound = true;
        public bool IsMultiTurn;
    }

    public static class AirSortieRegistry
    {
        private static readonly Dictionary<PlayerSetupData, List<AirSortie>> ByPlayer =
            new Dictionary<PlayerSetupData, List<AirSortie>>();

        public static void Clear() => ByPlayer.Clear();

        public static IReadOnlyList<AirSortie> For(PlayerSetupData player) =>
            player != null && ByPlayer.TryGetValue(player, out List<AirSortie> list)
                ? list
                : (IReadOnlyList<AirSortie>)System.Array.Empty<AirSortie>();

        public static AirSortie ForArmy(PlayerSetupData player, ArmyData army) =>
            army != null ? For(player).FirstOrDefault(s => s.Army == army) : null;

        public static void Add(PlayerSetupData player, AirSortie sortie)
        {
            if (player == null || sortie == null)
                return;
            if (!ByPlayer.TryGetValue(player, out List<AirSortie> list))
                ByPlayer[player] = list = new List<AirSortie>();
            list.Add(sortie);
        }

        public static void Remove(PlayerSetupData player, AirSortie sortie)
        {
            if (player != null && sortie != null && ByPlayer.TryGetValue(player, out List<AirSortie> list))
                list.Remove(sortie);
        }

        public static void Remove(PlayerSetupData player, ArmyData army)
        {
            if (player != null && army != null && ByPlayer.TryGetValue(player, out List<AirSortie> list))
                list.RemoveAll(s => s.Army == army);
        }
    }
}
