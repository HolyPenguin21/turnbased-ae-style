using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Aviation;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    public enum AirSortieKind
    {
        Strike,
        Recon,
        // Execution bookkeeping for a generic owned-airfield relocation. Strategic admission
        // still comes from current Recon objectives, not a new desire axis or mission category.
        Rebase,
    }

    // Minimal per-air-army execution record: one aircraft group is currently flying a sortie,
    // committed to landing at LandingHex, heading Outbound to TargetHex or back. Pure runtime
    // bookkeeping for aviation execution — landing-slot accounting reads it and the owning
    // executor advances it. It is NOT the old strategic task vocabulary (no category, no
    // 20-value kind, no lifecycle fields): Strike/Recon/Rebase are physical flight modes only.
    public sealed class AirSortie
    {
        public ArmyData Army;
        public AirSortieKind Kind;
        public HexCoord TargetHex;   // current travel destination: the action hex while Outbound, the landing hex after
        public HexCoord LandingHex;  // owned airfield this sortie is committed to landing at
        public bool Outbound = true; // false from launch for Rebase: destination is its landing
        // Strike only: the turn the wing chose to end over its target (proved safe by
        // AiAirSortiePlanner.CanEndTurnHereAndRecover) to strike again on its next turn. The
        // support leg is not proposed again for that turn; null otherwise.
        public int? HeldTurn;
        // A former support series flying home: it struck under its task's own policy, so the
        // generic recovery leg never adds a Standard strike on the way out.
        public bool NoRecoveryStrike;
    }

    public static class AirSortieRegistry
    {
        private static readonly Dictionary<PlayerSetupData, List<AirSortie>> ByPlayer =
            new Dictionary<PlayerSetupData, List<AirSortie>>();

        static AirSortieRegistry()
        {
            AviationRules.ReservedLandingSlots = ReservedSlots;
        }

        private static int ReservedSlots(HexCoord hex, PlayerSetupData owner, ArmyData excluding)
            => For(owner).Where(s => s?.Army != null && s.Army != excluding
                && s.Army.Owner == owner && AviationRules.IsValidAirArmy(s.Army)
                && s.LandingHex.Equals(hex) && !s.Army.Hex.Equals(hex)
                && ArmyRegistry.AllForOwner(owner).Contains(s.Army))
                .GroupBy(s => s.Army.Id).Sum(g => g.First().Army.Members.Count);

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
