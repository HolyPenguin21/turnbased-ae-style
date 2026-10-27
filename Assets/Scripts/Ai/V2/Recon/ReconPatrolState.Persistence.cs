using System;
using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Players;
using Game.Map;

namespace Game.Ai.V2
{
    public static partial class ReconPatrolStateRegistry
    {
        internal readonly struct PatrolState
        {
            internal readonly ReconMode Mode;
            internal readonly int PreferredMoverArmyId;
            internal readonly HexCoord StrategicAnchor;
            internal readonly ReconSector StrategicSector;
            internal readonly int StartedTurn;
            internal readonly int LastProgressTurn;
            internal readonly int LastModeSwitchTurn;
            internal readonly int LastStrategicReassignmentTurn;
            internal readonly (HexCoord FromHex, ReconEscapeCause Cause, float Risk, int? ThreatArmyId, int Turn)? Escape;
            internal PatrolState(ReconPatrolState x)
            {
                Mode = x.Mode;
                PreferredMoverArmyId = x.PreferredMoverArmyId;
                StrategicAnchor = x.StrategicAnchor;
                StrategicSector = x.StrategicSector;
                StartedTurn = x.StartedTurn;
                LastProgressTurn = x.LastProgressTurn;
                LastModeSwitchTurn = x.LastModeSwitchTurn;
                LastStrategicReassignmentTurn = x.LastStrategicReassignmentTurn;
                Escape = x.LastEscape == null ? ((HexCoord, ReconEscapeCause, float, int?, int)?)null :
                    (x.LastEscape.FromHex, x.LastEscape.Cause, x.LastEscape.Risk, x.LastEscape.ThreatArmyId, x.LastEscape.Turn);
            }
            internal ReconPatrolState Restore() => new ReconPatrolState
            {
                Mode = Mode,
                PreferredMoverArmyId = PreferredMoverArmyId,
                StrategicAnchor = StrategicAnchor,
                StrategicSector = StrategicSector,
                StartedTurn = StartedTurn,
                LastProgressTurn = LastProgressTurn,
                LastModeSwitchTurn = LastModeSwitchTurn,
                LastStrategicReassignmentTurn = LastStrategicReassignmentTurn,
                LastEscape = !Escape.HasValue ? null : new ReconEscape
                { FromHex = Escape.Value.FromHex, Cause = Escape.Value.Cause, Risk = Escape.Value.Risk,
                    ThreatArmyId = Escape.Value.ThreatArmyId, Turn = Escape.Value.Turn },
            };
        }
        internal static IReadOnlyDictionary<string, IReadOnlyDictionary<int, PatrolState>> CapturePersistentState(
            Func<PlayerSetupData, string> playerId) => AiPersistenceCollections.Capture(ByPlayer, playerId,
                x => AiPersistenceCollections.Freeze(x.Select(row => new KeyValuePair<int, PatrolState>(row.Key, new PatrolState(row.Value)))));
        internal static void RestorePersistentState(IReadOnlyDictionary<string, IReadOnlyDictionary<int, PatrolState>> state,
            Func<string, PlayerSetupData> player) => AiPersistenceCollections.Restore(ByPlayer, state, player,
                x => x.ToDictionary(row => row.Key, row => row.Value.Restore()));
    }
}
