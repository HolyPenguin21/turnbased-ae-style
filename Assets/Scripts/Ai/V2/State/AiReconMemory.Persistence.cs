using System;
using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Players;
using Game.Map;

namespace Game.Ai.V2
{
    public static partial class AiReconMemory
    {
        internal readonly struct ObservationState
        {
            internal readonly int ArmyId;
            internal readonly HexCoord LastObservedHex;
            internal readonly int LastObservedTurn;
            internal readonly int MemberCount;
            internal readonly float AttackSum;
            internal readonly float DefenseSum;
            internal readonly bool HasAntiAir;
            internal readonly int RecceRadius;
            internal readonly int RecceSpotStrength;
            internal readonly bool IsGarrison;
            internal readonly bool IsAir;
            internal readonly int MaxMovement;
            internal readonly Game.Combat.WorthIt.SideCommander Commander;
            internal readonly string OwnerId;
            internal readonly IReadOnlyList<Game.Combat.WorthIt.DefenderProfile> Defenders;
            internal ObservationState(ReconObservation x, Func<PlayerSetupData, string> playerId)
            {
                ArmyId = x.ArmyId;
                LastObservedHex = x.LastObservedHex;
                LastObservedTurn = x.LastObservedTurn;
                MemberCount = x.MemberCount;
                AttackSum = x.AttackSum;
                DefenseSum = x.DefenseSum;
                HasAntiAir = x.HasAntiAir;
                RecceRadius = x.RecceRadius;
                RecceSpotStrength = x.RecceSpotStrength;
                IsGarrison = x.IsGarrison;
                IsAir = x.IsAir;
                MaxMovement = x.MaxMovement;
                Commander = AiPersistenceCollections.FreezeCommander(x.Commander);
                OwnerId = x.Owner == null ? null : AiPersistenceCollections.CapturePlayerId(x.Owner, playerId);
                Defenders = x.Defenders == null ? null : AiPersistenceCollections.FreezeList(x.Defenders);
            }
            internal ReconObservation Restore(Func<string, PlayerSetupData> player) => new ReconObservation
            {
                ArmyId = ArmyId,
                LastObservedHex = LastObservedHex,
                LastObservedTurn = LastObservedTurn,
                MemberCount = MemberCount,
                AttackSum = AttackSum,
                DefenseSum = DefenseSum,
                HasAntiAir = HasAntiAir,
                RecceRadius = RecceRadius,
                RecceSpotStrength = RecceSpotStrength,
                IsGarrison = IsGarrison,
                IsAir = IsAir,
                MaxMovement = MaxMovement,
                Commander = Commander,
                Owner = OwnerId != null ? AiPersistenceCollections.ResolvePlayer(OwnerId, player) : null,
                Defenders = Defenders == null ? null : AiPersistenceCollections.FreezeList(Defenders),
            };
        }
        internal sealed class PersistentState
        {
            internal readonly IReadOnlyDictionary<string, IReadOnlyDictionary<int, ObservationState>> Observations;
            internal readonly IReadOnlyDictionary<string, int> ObservedTurns;
            internal PersistentState(Func<PlayerSetupData, string> playerId)
            {
                Observations = AiPersistenceCollections.Capture(ByPlayer, playerId, x => AiPersistenceCollections.Freeze(x.Select(row =>
                    new KeyValuePair<int, ObservationState>(row.Key, new ObservationState(row.Value, playerId)))));
                ObservedTurns = AiPersistenceCollections.Capture(ObservedTurnByPlayer, playerId, x => x);
            }
        }
        internal static PersistentState CapturePersistentState(Func<PlayerSetupData, string> playerId) => new PersistentState(playerId);
        internal static void RestorePersistentState(PersistentState state, Func<string, PlayerSetupData> player)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            var observations = new Dictionary<PlayerSetupData, Dictionary<int, ReconObservation>>();
            var turns = new Dictionary<PlayerSetupData, int>();
            AiPersistenceCollections.Restore(observations, state.Observations, player,
                x => x.ToDictionary(row => row.Key, row => row.Value.Restore(player)));
            AiPersistenceCollections.Restore(turns, state.ObservedTurns, player, x => x);
            int highestArmyId = observations.Values.SelectMany(x => x.Values)
                .Select(x => x.ArmyId).DefaultIfEmpty(-1).Max();
            if (highestArmyId >= 0) ArmyData.ReserveIdentity(highestArmyId);
            // Do not call Clear(): sibling recon registries restore independently.
            ByPlayer.Clear(); ObservedTurnByPlayer.Clear();
            foreach (var row in observations) ByPlayer.Add(row.Key, row.Value);
            foreach (var row in turns) ObservedTurnByPlayer.Add(row.Key, row.Value);
        }
    }
}
