using System.Collections.Generic;
using System.Linq;
using Game.Cards;
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

    public static partial class AiReconMemory
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
            if (HidesInSight(o.Defenders))
                return false;
            int elapsed = Mathf.Max(1, turn - o.LastObservedTurn);
            int reach = Mathf.Min(UnseenReach(o.MaxMovement, elapsed),
                AiConfigV2.reconObservationMaxHideRadius);
            foreach (HexCoord h in HexGridMath.HexesInRange(o.LastObservedHex, reach))
                if (CouldStandUnseen(player, h, o.IsAir, canHide))
                    return false;
            return true;
        }

        // ---- THE model of where an unobserved army can be (2026-11-13, match Sable vs Korrin) ----
        // An army last seen `unseen` turns ago has walked at most MaxMovement per turn (a step costs
        // at least 1, so the hex distance bounds its route), and it stands on a hex we do not see
        // now: a hex in sight would show it. Read by GoneOnReobservation (is any such hex left),
        // WorldAnalysis.ContactApproachCost (threat ETA) and AiArmyRoles.GarrisonThreatFloor (which
        // garrison it can reach). Before, the two "how near can it be" readers subtracted the full
        // advance and floored at 0: a contact 4 hexes away seen 2 turns ago read ETA 0 to a Base
        // whose whole neighbourhood was in sight, and pinned that Base's field army for 12 turns.
        // An army whose every known member carries Stealth may stand unseen on a hex in sight:
        // for it the observer's sight bounds nothing.
        internal static bool HidesInSight(IEnumerable<WorthIt.DefenderProfile> members)
        {
            bool any = false;
            foreach (WorthIt.DefenderProfile m in members ?? Enumerable.Empty<WorthIt.DefenderProfile>())
            {
                any = true;
                if (m.Abilities == null || !m.Abilities.Any(a => AbilityParams.TryGetStealthLevel(a, out int level) && level > 0))
                    return false;
            }
            return any;
        }

        internal static int UnseenReach(int maxMovement, int unseen) =>
            Mathf.Max(1, maxMovement) * Mathf.Max(0, unseen);

        internal static bool CouldStandUnseen(PlayerSetupData observer, HexCoord hex, bool isAir,
            System.Func<HexCoord, bool, bool> canHide) =>
            !VisionSystem.IsVisible(observer, hex) && (canHide == null || canHide(hex, isAir));

        // The least hex distance from `to` to where the army can stand now: the advance bound, and
        // with an `observer` the nearest hex it could hide on unseen. Null observer keeps the bare
        // advance bound. When no hidden position is left (every reachable hex is in sight) the
        // advance bound stays: the memory drops such an army on its own (GoneOnReobservation).
        internal static int PossibleDistance(PlayerSetupData observer, HexCoord from, int maxMovement,
            int unseen, bool isAir, HexCoord to, System.Func<HexCoord, bool, bool> canHide = null,
            bool hidesInSight = false, int? limit = null)
        {
            int distance = HexGridMath.Distance(from, to);
            if (unseen <= 0)
                return distance;
            int reach = UnseenReach(maxMovement, unseen);
            int advanced = Mathf.Max(0, distance - reach);
            if (observer == null || hidesInSight)
                return advanced;
            // One pass over the disc around `to` that holds `from`: no hex nearer than `advanced` is
            // reachable, the cheap distance tests run before the vision lookup. `limit`: a caller
            // that only asks "within `limit`?" scans no further and reads anything beyond as
            // limit + 1.
            int scan = limit.HasValue ? Mathf.Min(distance, Mathf.Max(0, limit.Value)) : distance;
            if (advanced > scan)
                return advanced;
            int best = int.MaxValue;
            foreach (HexCoord h in HexGridMath.HexesInRange(to, scan))
            {
                int near = HexGridMath.Distance(to, h);
                if (near >= best || near < advanced || HexGridMath.Distance(from, h) > reach
                    || !CouldStandUnseen(observer, h, isAir, canHide))
                    continue;
                best = near;
                if (best == advanced)
                    break;
            }
            if (best != int.MaxValue)
                return best;
            return scan < distance ? scan + 1 : advanced;
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
