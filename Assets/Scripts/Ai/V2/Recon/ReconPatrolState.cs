using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Players;

namespace Game.Ai.V2
{
    // Durable TACTICAL execution state for a Recon actor (Explore/Refresh mode, strategic anchor/
    // sector, progress, mode + reassignment hysteresis) — Level 6 (Ground/Air Execution) territory,
    // NOT Level 5 mission-actor Assignment (ReconAssignmentPlanner: Mission Y -> Actor X). Renamed
    // from ReconAssignment/ReconAssignmentRegistry specifically to avoid colliding with that
    // different "assignment" concept (spec §22). A hex is only a tactical waypoint/anchor; reaching
    // it does not retire this record. This registry is deliberately actor-keyed so one scout can
    // keep the same role across turns/events while its next step is selected live.
    public enum ReconMode { Explore, Refresh }

    public sealed class ReconPatrolState
    {
        public ReconMode Mode;
        public int PreferredMoverArmyId;
        public HexCoord StrategicAnchor;
        public ReconSector StrategicSector;
        public int StartedTurn;
        public int LastProgressTurn;
        public int LastModeSwitchTurn;
        public int LastStrategicReassignmentTurn;
    }

    public static class ReconPatrolStateRegistry
    {
        private static readonly Dictionary<PlayerSetupData, Dictionary<int, ReconPatrolState>> ByPlayer =
            new Dictionary<PlayerSetupData, Dictionary<int, ReconPatrolState>>();

        // One strategic turn of hysteresis is enough to stop two proposals in the same pass from
        // ping-ponging an actor between Explore and Refresh, while still allowing the next turn's
        // changed information picture to retask it.
        private const int ModeHoldTurns = AiConfigV2.reconAssignmentModeHoldTurns;

        // Strategic heading is durable independently of a proposal's focus hex. A different mission
        // in the SAME turn may not rewrite anchor/sector merely because it was materialised later.
        // Reassignment becomes legal after one strategic turn, immediately after a mode change,
        // when the old anchor has been reached, or after a real no-progress stall.
        private const int StrategicReassignmentHoldTurns = AiConfigV2.reconAssignmentReassignHoldTurns;
        private const int StrategicStallTurns = AiConfigV2.reconAssignmentStallTurns;

        public static void ClearAll() => ByPlayer.Clear();

        public static ReconPatrolState GetOrCreate(PlayerSetupData player, int armyId, HexCoord currentHex,
            HexCoord strategicAnchor, ReconMode requestedMode, int turn,
            float exploreScore = 0f, float refreshScore = 0f)
        {
            if (player == null)
                return New(armyId, currentHex, strategicAnchor, requestedMode, turn);
            if (!ByPlayer.TryGetValue(player, out Dictionary<int, ReconPatrolState> byArmy))
                ByPlayer[player] = byArmy = new Dictionary<int, ReconPatrolState>();
            if (!byArmy.TryGetValue(armyId, out ReconPatrolState assignment))
            {
                assignment = New(armyId, currentHex, strategicAnchor, requestedMode, turn);
                byArmy[armyId] = assignment;
                AiDebugLog.Write($"[AI][V2][Recon][Patrol] actor=#{armyId} mode={requestedMode} "
                    + $"anchor=({strategicAnchor.Q},{strategicAnchor.R}) sector={assignment.StrategicSector} start={turn}");
                return assignment;
            }

            // Mission objectives are strategic priors, never durable destination identities.
            // Mode switching and strategic heading reassignment are separate hysteresis decisions.
            // Spec §25 — a mode switch needs BOTH the time hold to have elapsed AND the requested
            // mode's strategic score to beat the current mode's by reconModeSwitchMargin, so a
            // small score wobble cannot ping-pong Explore<->Refresh. When no scores are supplied
            // (both 0, e.g. the single-mode air caller) the margin test fails closed and mode is
            // effectively pinned to whatever it was created with.
            bool modeChanged = false;
            if (assignment.Mode != requestedMode
                && turn - assignment.LastModeSwitchTurn >= ModeHoldTurns)
            {
                float currentScore = assignment.Mode == ReconMode.Explore ? exploreScore : refreshScore;
                float requestedScore = requestedMode == ReconMode.Explore ? exploreScore : refreshScore;
                if (requestedScore > currentScore + AiConfigV2.reconModeSwitchMargin)
                {
                    ReconMode old = assignment.Mode;
                    assignment.Mode = requestedMode;
                    assignment.LastModeSwitchTurn = turn;
                    modeChanged = true;
                    AiDebugLog.Write($"[AI][V2][Recon][Patrol] actor=#{armyId} mode {old}→{requestedMode} "
                        + $"turn={turn} score {currentScore:0.00}->{requestedScore:0.00}");
                }
                else
                {
                    AiDebugLog.Write($"[AI][V2][Recon][Patrol] actor=#{armyId} mode-switch suppressed: "
                        + $"{requestedMode} score {requestedScore:0.00} <= {assignment.Mode} {currentScore:0.00} + margin");
                }
            }

            if (!assignment.StrategicAnchor.Equals(strategicAnchor))
            {
                ReconSector requestedSector = ReconDirectionModel.Sector(currentHex, strategicAnchor);
                bool oldAnchorReached = currentHex.Equals(assignment.StrategicAnchor);
                bool stalled = turn - assignment.LastProgressTurn >= StrategicStallTurns;
                bool holdExpired = turn - assignment.LastStrategicReassignmentTurn >= StrategicReassignmentHoldTurns;
                bool allow = modeChanged || oldAnchorReached || stalled || holdExpired;

                if (allow)
                {
                    HexCoord oldAnchor = assignment.StrategicAnchor;
                    ReconSector oldSector = assignment.StrategicSector;
                    assignment.StrategicAnchor = strategicAnchor;
                    assignment.StrategicSector = requestedSector;
                    assignment.LastStrategicReassignmentTurn = turn;

                    string reason = modeChanged ? "mode-change"
                        : oldAnchorReached ? "anchor-reached"
                        : stalled ? "stalled"
                        : "hold-expired";
                    AiDebugLog.Write($"[AI][V2][Recon][Patrol] actor=#{armyId} reassign "
                        + $"anchor=({oldAnchor.Q},{oldAnchor.R})→({strategicAnchor.Q},{strategicAnchor.R}) "
                        + $"sector={oldSector}→{requestedSector} reason={reason} turn={turn}");
                }
                else
                {
                    AiDebugLog.Write($"[AI][V2][Recon][Patrol] actor=#{armyId} keep "
                        + $"anchor=({assignment.StrategicAnchor.Q},{assignment.StrategicAnchor.R}) "
                        + $"sector={assignment.StrategicSector}; suppress incoming=({strategicAnchor.Q},{strategicAnchor.R}) "
                        + $"requestedSector={requestedSector} reason=strategic-hold turn={turn}");
                }
            }

            return assignment;
        }

        public static bool TryGet(PlayerSetupData player, int armyId, out ReconPatrolState assignment)
        {
            assignment = null;
            return player != null
                && ByPlayer.TryGetValue(player, out Dictionary<int, ReconPatrolState> byArmy)
                && byArmy.TryGetValue(armyId, out assignment);
        }

        // Snapshot of active actor claims for live multi-scout deconfliction. Values are durable
        // assignments only — no target hex reservation is invented here.
        public static IReadOnlyList<ReconPatrolState> ActiveFor(PlayerSetupData player)
        {
            if (player == null || !ByPlayer.TryGetValue(player, out Dictionary<int, ReconPatrolState> byArmy))
                return System.Array.Empty<ReconPatrolState>();
            return byArmy.Values.ToList();
        }

        public static int OtherSectorClaims(PlayerSetupData player, int armyId, ReconSector sector)
        {
            int count = 0;
            foreach (ReconPatrolState a in ActiveFor(player))
                if (a.PreferredMoverArmyId != armyId && a.StrategicSector == sector)
                    count++;
            return count;
        }

        public static int OtherNearbyAnchorClaims(PlayerSetupData player, int armyId, HexCoord hex, int radius)
        {
            int count = 0;
            foreach (ReconPatrolState a in ActiveFor(player))
                if (a.PreferredMoverArmyId != armyId
                    && HexGridMath.Distance(a.StrategicAnchor, hex) <= radius)
                    count++;
            return count;
        }

        public static void MarkProgress(PlayerSetupData player, int armyId, int turn)
        {
            if (TryGet(player, armyId, out ReconPatrolState assignment))
                assignment.LastProgressTurn = turn;
        }

        public static void Retire(PlayerSetupData player, int armyId, string reason)
        {
            if (player == null || !ByPlayer.TryGetValue(player, out Dictionary<int, ReconPatrolState> byArmy)
                || !byArmy.Remove(armyId))
                return;
            AiDebugLog.Write($"[AI][V2][Recon][Patrol] actor=#{armyId} retired reason={reason}");
        }

        private static ReconPatrolState New(int armyId, HexCoord currentHex, HexCoord anchor,
            ReconMode mode, int turn) => new ReconPatrolState
        {
            Mode = mode,
            PreferredMoverArmyId = armyId,
            StrategicAnchor = anchor,
            StrategicSector = ReconDirectionModel.Sector(currentHex, anchor),
            StartedTurn = turn,
            LastProgressTurn = turn,
            LastModeSwitchTurn = turn,
            LastStrategicReassignmentTurn = turn,
        };
    }
}
