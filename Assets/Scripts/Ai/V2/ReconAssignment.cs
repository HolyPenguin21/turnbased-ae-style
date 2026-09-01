using System.Collections.Generic;
using Game.HexGrid;
using Game.Players;

namespace Game.Ai.V2
{
    // Durable identity for a Recon actor. A hex is only a tactical waypoint/anchor; reaching it
    // does not retire this record. This registry is deliberately actor-keyed so one scout can keep
    // the same role across turns/events while its next step is selected live.
    public enum ReconMode { Explore, Refresh }

    public sealed class ReconAssignment
    {
        public ReconMode Mode;
        public int PreferredMoverArmyId;
        public HexCoord StrategicAnchor;
        public ReconSector StrategicSector;
        public int StartedTurn;
        public int LastProgressTurn;
        public int LastModeSwitchTurn;
    }

    public static class ReconAssignmentRegistry
    {
        private static readonly Dictionary<PlayerSetupData, Dictionary<int, ReconAssignment>> ByPlayer =
            new Dictionary<PlayerSetupData, Dictionary<int, ReconAssignment>>();

        public static void ClearAll() => ByPlayer.Clear();

        public static ReconAssignment GetOrCreate(PlayerSetupData player, int armyId, HexCoord currentHex,
            HexCoord strategicAnchor, ReconMode requestedMode, int turn)
        {
            if (player == null)
                return New(armyId, currentHex, strategicAnchor, requestedMode, turn);
            if (!ByPlayer.TryGetValue(player, out Dictionary<int, ReconAssignment> byArmy))
                ByPlayer[player] = byArmy = new Dictionary<int, ReconAssignment>();
            if (!byArmy.TryGetValue(armyId, out ReconAssignment assignment))
            {
                assignment = New(armyId, currentHex, strategicAnchor, requestedMode, turn);
                byArmy[armyId] = assignment;
                AiDebugLog.Write($"[AI][V2][Recon][Assignment] actor=#{armyId} mode={requestedMode} "
                    + $"sector={assignment.StrategicSector} start={turn}");
                return assignment;
            }

            // The old focus may still provide a useful strategic prior, but it no longer defines
            // durable identity. Refresh it without recreating the assignment.
            assignment.StrategicAnchor = strategicAnchor;
            assignment.StrategicSector = ReconDirectionModel.Sector(currentHex, strategicAnchor);
            return assignment;
        }

        public static bool TryGet(PlayerSetupData player, int armyId, out ReconAssignment assignment)
        {
            assignment = null;
            return player != null
                && ByPlayer.TryGetValue(player, out Dictionary<int, ReconAssignment> byArmy)
                && byArmy.TryGetValue(armyId, out assignment);
        }

        public static void MarkProgress(PlayerSetupData player, int armyId, int turn)
        {
            if (TryGet(player, armyId, out ReconAssignment assignment))
                assignment.LastProgressTurn = turn;
        }

        public static void Retire(PlayerSetupData player, int armyId, string reason)
        {
            if (player == null || !ByPlayer.TryGetValue(player, out Dictionary<int, ReconAssignment> byArmy)
                || !byArmy.Remove(armyId))
                return;
            AiDebugLog.Write($"[AI][V2][Recon][Assignment] actor=#{armyId} retired reason={reason}");
        }

        private static ReconAssignment New(int armyId, HexCoord currentHex, HexCoord anchor,
            ReconMode mode, int turn) => new ReconAssignment
        {
            Mode = mode,
            PreferredMoverArmyId = armyId,
            StrategicAnchor = anchor,
            StrategicSector = ReconDirectionModel.Sector(currentHex, anchor),
            StartedTurn = turn,
            LastProgressTurn = turn,
            LastModeSwitchTurn = turn,
        };
    }
}
