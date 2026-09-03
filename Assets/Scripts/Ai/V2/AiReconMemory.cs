using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  AI RECON MEMORY  (Strategy V2 build-order step 4 follow-up)
    // ===========================================================================================
    //  A LONGER observation history than V1's tactical enemy memory, and V2-only.
    // ===========================================================================================
    public sealed class ReconObservation
    {
        public int ArmyId;
        public PlayerSetupData Owner;
        public HexCoord LastObservedHex;
        public int LastObservedTurn;
        public int MemberCount;
        public float AttackSum;
        public float DefenseSum;
        public IReadOnlyList<WorthIt.DefenderProfile> Defenders;
        public bool HasAntiAir;
        public int RecceRadius;
        public int RecceSpotStrength;
    }

    public static class AiReconMemory
    {
        private static readonly Dictionary<PlayerSetupData, Dictionary<int, ReconObservation>> ByPlayer =
            new Dictionary<PlayerSetupData, Dictionary<int, ReconObservation>>();

        public static void Clear()
        {
            ByPlayer.Clear();
            AiReconIntelMemory.Clear();
            ReconIntelSnapshotRegistry.Clear();
            ReconAssignmentRegistry.ClearAll();
            ReconAirSortieRegistry.ClearAll();
            AirReconCoverageRegistry.ClearAll();
            ReconCapacityDeficitRegistry.ClearAll();
            ReconAirReservationRegistry.Clear();
            ScoutTrailRegistry.ClearAll();
            ReconAcceptanceAudit.ClearAll();
        }

        // Called once per V2 scan with current honest sightings. This is also the canonical Recon
        // observation seam: first stamp CURRENT visibility into live IntelAge, then immediately
        // freeze a per-turn strategic copy. Downstream strategy/mission planning reads only that
        // frozen copy; tactical execution may continue to update AiReconIntelMemory after moves.
        public static void Observe(PlayerSetupData player, int turn,
            IEnumerable<AiMapMemory.KnownEnemySighting> currentSightings)
        {
            if (player == null)
                return;

            AiReconIntelMemory.ObserveCurrentVisibility(player, turn);
            ReconIntelSnapshotRegistry.Capture(player, turn, AiReconIntelMemory.Snapshot(player));

            if (!ByPlayer.TryGetValue(player, out Dictionary<int, ReconObservation> store))
                ByPlayer[player] = store = new Dictionary<int, ReconObservation>();

            var liveIds = new HashSet<int>();
            if (currentSightings != null)
            {
                foreach (AiMapMemory.KnownEnemySighting s in currentSightings)
                {
                    liveIds.Add(s.ArmyId);
                    store[s.ArmyId] = new ReconObservation
                    {
                        ArmyId = s.ArmyId,
                        Owner = s.Owner,
                        LastObservedHex = s.Hex,
                        LastObservedTurn = s.SeenTurn,
                        MemberCount = s.MemberCount,
                        AttackSum = s.AttackSum,
                        DefenseSum = s.DefenseSum,
                        Defenders = s.Defenders,
                        HasAntiAir = s.HasAntiAir,
                        RecceRadius = s.RecceRadius,
                        RecceSpotStrength = s.RecceSpotStrength,
                    };
                }
            }

            int cutoff = turn - AiConfigV2.reconObservationMemoryTurns;
            var drop = store
                .Where(kv => kv.Value.LastObservedTurn < cutoff
                    || (!liveIds.Contains(kv.Key) && VisionSystem.IsVisible(player, kv.Value.LastObservedHex)))
                .Select(kv => kv.Key)
                .ToList();
            foreach (int id in drop)
                store.Remove(id);
        }

        public static IReadOnlyList<ReconObservation> Historical(PlayerSetupData player, ISet<int> currentArmyIds)
        {
            if (player == null || !ByPlayer.TryGetValue(player, out Dictionary<int, ReconObservation> store))
                return System.Array.Empty<ReconObservation>();
            return store.Values
                .Where(o => currentArmyIds == null || !currentArmyIds.Contains(o.ArmyId))
                .ToList();
        }

        public static float ConfidenceDecay(int age)
        {
            float w = Mathf.Max(1, AiConfigV2.reconObservationMemoryTurns);
            return Mathf.Clamp01(1f - age / w);
        }
    }
}
