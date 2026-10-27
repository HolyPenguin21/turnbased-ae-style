using System;
using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Players;
using Game.Map;

namespace Game.Ai.V2
{
    public static partial class AirSortieRegistry
    {
        internal readonly struct SortieState
        {
            internal readonly int ArmyId;
            internal readonly AirSortieKind Kind;
            internal readonly HexCoord TargetHex, LandingHex;
            internal readonly bool Outbound;
            internal readonly int? HeldTurn;
            internal readonly Game.Aviation.AirStrikePolicy StrikePolicy;
            internal SortieState(AirSortie x)
            {
                ArmyId = x.Army?.Id ?? throw new ArgumentException("Sortie without an army");
                Kind = x.Kind; TargetHex = x.TargetHex; LandingHex = x.LandingHex;
                Outbound = x.Outbound; HeldTurn = x.HeldTurn; StrikePolicy = x.StrikePolicy;
            }
            internal AirSortie Restore(Func<int, ArmyData> army) => new AirSortie
            {
                Army = AiPersistenceCollections.ResolveArmy(ArmyId, army),
                Kind = Kind, TargetHex = TargetHex, LandingHex = LandingHex,
                Outbound = Outbound, HeldTurn = HeldTurn, StrikePolicy = StrikePolicy,
            };
        }
        internal static IReadOnlyDictionary<string, IReadOnlyList<SortieState>> CapturePersistentState(Func<PlayerSetupData, string> playerId) =>
            AiPersistenceCollections.Capture(ByPlayer, playerId, x => AiPersistenceCollections.FreezeList(x.Select(s => new SortieState(s))));
        internal static void RestorePersistentState(IReadOnlyDictionary<string, IReadOnlyList<SortieState>> state,
            Func<string, PlayerSetupData> player, Func<int, ArmyData> army)
        {
            if (army == null) throw new ArgumentNullException(nameof(army));
            // Validate army ownership too; an ID must never bind another player's reservation.
            var restored = new Dictionary<PlayerSetupData, List<AirSortie>>();
            AiPersistenceCollections.Restore(restored, state, player, x => x.Select(s => s.Restore(army)).ToList());
            foreach (var row in restored)
                if (row.Value.Any(x => x.Army.Owner != row.Key)
                    || row.Value.Select(x => x.Army.Id).Distinct().Count() != row.Value.Count)
                    throw new ArgumentException("Invalid saved sortie ownership or duplicate army");
            ByPlayer.Clear();
            foreach (var row in restored) ByPlayer.Add(row.Key, row.Value);
        }
    }
}
