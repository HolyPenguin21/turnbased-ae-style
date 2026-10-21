using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using UnityEngine;

using Game.Combat;

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
        // Building-bound garrison (AiMapMemory.KnownEnemySighting.IsGarrison) — a site defender,
        // never a roaming threat contact.
        public bool IsGarrison;
        public bool IsAir;
        public int MaxMovement = 1;
        // The observed commander (AiMapMemory.KnownEnemySighting.Commander).
        public WorthIt.SideCommander Commander;
    }

    public static class AiReconMemory
    {
        private static readonly Dictionary<PlayerSetupData, Dictionary<int, ReconObservation>> ByPlayer =
            new Dictionary<PlayerSetupData, Dictionary<int, ReconObservation>>();
        // The turn of each player's latest Observe: the clock observation ages are read against.
        private static readonly Dictionary<PlayerSetupData, int> ObservedTurnByPlayer =
            new Dictionary<PlayerSetupData, int>();

        public static void Clear()
        {
            ByPlayer.Clear();
            ObservedTurnByPlayer.Clear();
            AiReconIntelMemory.Clear();
            ReconIntelSnapshotRegistry.Clear();
            ReconPatrolStateRegistry.ClearAll();
            ReconAirSortieRegistry.ClearAll();
            AirReconCoverageRegistry.ClearAll();
            ReconCapacityDeficitRegistry.ClearAll();
            ScoutTrailRegistry.ClearAll();
            ReconAcceptanceAudit.ClearAll();
        }

        // Called once per V2 scan with current honest sightings. This is also the canonical Recon
        // observation seam: first stamp CURRENT visibility into live IntelAge, then immediately
        // freeze a per-knowledge-revision strategic copy. Downstream strategy/mission planning reads only that
        // frozen copy; tactical execution may continue to update AiReconIntelMemory after moves.
        // `canHide(hex, isAir)` — whether an army could stand on that hex (map + terrain); null
        // treats every hex as enterable.
        public static void Observe(PlayerSetupData player, int turn, int knowledgeVersion,
            IEnumerable<AiMapMemory.KnownEnemySighting> currentSightings,
            System.Func<HexCoord, bool, bool> canHide = null)
        {
            if (player == null)
                return;
            ObservedTurnByPlayer[player] = turn;

            AiReconIntelMemory.ObserveCurrentVisibility(player, turn);
            ReconIntelSnapshotRegistry.Capture(player, turn, knowledgeVersion,
                AiReconIntelMemory.Snapshot(player));

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
                        IsGarrison = s.IsGarrison,
                        IsAir = s.IsAir,
                        MaxMovement = s.MaxMovement,
                        Commander = s.Commander,
                    };
                }
            }

            int cutoff = turn - AiConfigV2.reconObservationMemoryTurns;
            var drop = store
                .Where(kv => kv.Value.LastObservedTurn < cutoff
                    || AiMapMemory.IsWitnessedDestroyed(player, kv.Key)
                    || (!liveIds.Contains(kv.Key) && GoneOnReobservation(player, turn, kv.Value, canHide)))
                .Select(kv => kv.Key)
                .ToList();
            foreach (int id in drop)
                store.Remove(id);
        }

        // An army missing from its visible last-known hex has only LEFT it, unless it could not
        // have left unseen. 2026-10-17 playtest: Rust Bite (65) stepped from (-3,3) into fog at
        // (-3,2); (-3,3) was seen empty and the whole observation was dropped, so Cassia's Threat
        // read 0.13 next to her Citadel one turn before it fell. It is gone only when every hex
        // it could have reached since (MaxMovement per elapsed turn) is in sight now; a
        // building-bound garrison cannot leave, so an empty visible hex is its end.
        internal static bool GoneOnReobservation(PlayerSetupData player, int turn,
            ReconObservation o, System.Func<HexCoord, bool, bool> canHide)
        {
            if (!VisionSystem.IsVisible(player, o.LastObservedHex))
                return false;
            if (o.IsGarrison)
                return true;
            int elapsed = Mathf.Max(1, turn - o.LastObservedTurn);
            int reach = Mathf.Min(Mathf.Max(1, o.MaxMovement) * elapsed,
                AiConfigV2.reconObservationMaxHideRadius);
            foreach (HexCoord h in HexGridMath.HexesInRange(o.LastObservedHex, reach))
                if (!VisionSystem.IsVisible(player, h) && (canHide == null || canHide(h, o.IsAir)))
                    return false;
            return true;
        }

        // The turn the player's memory was last observed on (its clock for observation ages);
        // int.MinValue before any Observe.
        public static int ObservedTurn(PlayerSetupData player) =>
            player != null && ObservedTurnByPlayer.TryGetValue(player, out int t) ? t : int.MinValue;

        public static IReadOnlyList<ReconObservation> Historical(PlayerSetupData player, ISet<int> currentArmyIds)
        {
            if (player == null || !ByPlayer.TryGetValue(player, out Dictionary<int, ReconObservation> store))
                return System.Array.Empty<ReconObservation>();
            // A death seen after the last Observe (a battle earlier this turn) is honoured at once.
            return store.Values
                .Where(o => (currentArmyIds == null || !currentArmyIds.Contains(o.ArmyId))
                    && (o.Owner == null || !o.Owner.IsEliminated)
                    && !AiMapMemory.IsWitnessedDestroyed(player, o.ArmyId))
                .ToList();
        }

        public static float ConfidenceDecay(int age)
        {
            float w = Mathf.Max(1, AiConfigV2.reconObservationMemoryTurns);
            return Mathf.Clamp01(1f - age / w);
        }
    }
}
