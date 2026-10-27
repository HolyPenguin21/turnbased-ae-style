using System;
using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Players;
using Game.Map;

namespace Game.Ai.V2
{
    internal static partial class ReconAirSortieRegistry
    {
        internal readonly struct SortieState
        {
            internal readonly int SortieId;
            internal readonly ReconAirPhase Phase;
            internal readonly HexCoord LaunchHex;
            internal readonly HexCoord ChosenLandingHex;
            internal readonly bool HasChosenLanding;
            internal readonly ReconSector ClaimedSector;
            internal readonly bool HasClaim;
            internal readonly float BestOutboundStepScore;
            internal readonly int LastProcessedTurn;
            internal readonly string LastDecisionReason;
            internal readonly IReadOnlyList<HexCoord> Trail;
            internal SortieState(ReconAirSortieState x)
            { SortieId = x.SortieId; Phase = x.Phase; LaunchHex = x.LaunchHex; ChosenLandingHex = x.ChosenLandingHex; HasChosenLanding = x.HasChosenLanding; ClaimedSector = x.ClaimedSector; HasClaim = x.HasClaim; BestOutboundStepScore = x.BestOutboundStepScore; LastProcessedTurn = x.LastProcessedTurn; LastDecisionReason = x.LastDecisionReason; Trail = AiPersistenceCollections.FreezeList(x.Trail); }
            internal ReconAirSortieState Restore()
            {
                var restored = new ReconAirSortieState { SortieId = SortieId, Phase = Phase, LaunchHex = LaunchHex, ChosenLandingHex = ChosenLandingHex, HasChosenLanding = HasChosenLanding, ClaimedSector = ClaimedSector, HasClaim = HasClaim, BestOutboundStepScore = BestOutboundStepScore, LastProcessedTurn = LastProcessedTurn, LastDecisionReason = LastDecisionReason };
                restored.Trail.AddRange(Trail); return restored;
            }
        }
        internal sealed class PersistentState
        {
            internal readonly int NextSortieId;
            internal readonly IReadOnlyDictionary<string, IReadOnlyDictionary<int, SortieState>> Players;
            internal PersistentState(Func<PlayerSetupData, string> playerId)
            {
                NextSortieId = _nextSortieId;
                Players = AiPersistenceCollections.Capture(ByPlayer, playerId, x => AiPersistenceCollections.Freeze(x.Select(row =>
                    new KeyValuePair<int, SortieState>(row.Key, new SortieState(row.Value)))));
            }
        }
        internal static PersistentState CapturePersistentState(Func<PlayerSetupData, string> playerId) => new PersistentState(playerId);
        internal static void RestorePersistentState(PersistentState state, Func<string, PlayerSetupData> player)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            int high = state.Players.Values.SelectMany(x => x.Values).Select(x => x.SortieId).DefaultIfEmpty(0).Max();
            if (state.NextSortieId <= high) throw new ArgumentException("Invalid saved recon sortie sequence");
            AiPersistenceCollections.Restore(ByPlayer, state.Players, player,
                x => x.ToDictionary(row => row.Key, row => row.Value.Restore()));
            _nextSortieId = state.NextSortieId;
        }
    }
    internal static partial class AirReconCoverageRegistry
    {
        internal static IReadOnlyDictionary<string, IReadOnlyDictionary<HexCoord, IReadOnlyDictionary<int, int>>> CapturePersistentState(
            Func<PlayerSetupData, string> playerId) => AiPersistenceCollections.Capture(ByPlayer, playerId,
                x => AiPersistenceCollections.Freeze(x.Select(row => new KeyValuePair<HexCoord, IReadOnlyDictionary<int, int>>(
                    row.Key, AiPersistenceCollections.Freeze(row.Value)))));
        internal static void RestorePersistentState(
            IReadOnlyDictionary<string, IReadOnlyDictionary<HexCoord, IReadOnlyDictionary<int, int>>> state,
            Func<string, PlayerSetupData> player) => AiPersistenceCollections.Restore(ByPlayer, state, player,
                x => x.ToDictionary(row => row.Key, row => row.Value.ToDictionary(s => s.Key, s => s.Value)));
    }
}
