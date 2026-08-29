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
    //
    //  WHY IT EXISTS
    //  --------------------------------------------------------------------------------------------
    //  V1 AiMapMemory drops a player-owned enemy sighting after AiConfig.enemySightingMemoryTurns
    //  (2) turns on purpose — short memory is what keeps V1's stale-avoidance / defence reactions
    //  honest. But the Recon SURVEIL mission is designed around a 2..8-turn staleness ramp: on
    //  V1's memory alone a contact is deleted at age 3, so staleness could never rise above 0 and
    //  the whole "revisit a last-known position that's going cold" behaviour was dead on arrival.
    //
    //  WHAT IT DOES
    //  --------------------------------------------------------------------------------------------
    //  Keyed by the army's stable ArmyData.Id (KnownEnemySighting.ArmyId), NOT owner+hex — the
    //  army moves. Each V2 world scan calls Observe with the CURRENT honest non-neutral sightings;
    //  every one overwrites its entry with the current turn. Entries older than
    //  AiConfigV2.reconObservationMemoryTurns are purged. Historical returns the entries whose
    //  army is NOT in the current sighting set — those are the genuinely stale last-known
    //  positions a Surveil mission can target, with a confidence that decays to 0 as the entry
    //  ages out.
    //
    //  HONESTY: it only ever remembers what V1's fog-respecting memory already held; it just
    //  RETAINS it a while after V1 lets go. It never sees anything V1 couldn't.
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

        public static void Clear() => ByPlayer.Clear();

        // Called once per V2 scan with the current honest (non-neutral) sightings (V1's memory,
        // which itself keeps a sighting up to enemySightingMemoryTurns after it was last SEEN).
        // Each entry is stamped with the sighting's real SeenTurn — NOT the current turn — so a
        // sighting merely lingering in V1's tactical memory does not keep rejuvenating the
        // observation age. Then: reconcile (drop an entry whose last-known hex we can now see is
        // empty — we looked, it is not there / it went stealth, so there is no honest concrete
        // position to send a Surveil to any more; a still-existing enemy feeds enemyBlindness
        // instead), and purge past the retention window.
        public static void Observe(PlayerSetupData player, int turn,
            IEnumerable<AiMapMemory.KnownEnemySighting> currentSightings)
        {
            if (player == null)
                return;
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

        // Entries whose army is NOT currently sighted — the stale last-known positions Surveil
        // can act on. `currentArmyIds` is the ArmyId set of this scan's live sightings.
        public static IReadOnlyList<ReconObservation> Historical(PlayerSetupData player, ISet<int> currentArmyIds)
        {
            if (player == null || !ByPlayer.TryGetValue(player, out Dictionary<int, ReconObservation> store))
                return System.Array.Empty<ReconObservation>();
            return store.Values
                .Where(o => currentArmyIds == null || !currentArmyIds.Contains(o.ArmyId))
                .ToList();
        }

        // Monotonic 1 -> 0 over the retention window. age 0 keeps full last-known confidence;
        // at reconObservationMemoryTurns it is 0.
        public static float ConfidenceDecay(int age)
        {
            float w = Mathf.Max(1, AiConfigV2.reconObservationMemoryTurns);
            return Mathf.Clamp01(1f - age / w);
        }
    }
}
