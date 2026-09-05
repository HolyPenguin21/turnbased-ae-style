using System.Collections.Generic;
using System.Linq;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  AI-RECON-02 — UNIFIED RECON CAPACITY MODEL
    // ===========================================================================================
    //  Observation capacity and ground-traversal capacity are DIFFERENT resources:
    //
    //    · An OBSERVATION lane (Refresh / Surveil — keep fresh eyes on a hex or a contact) can be
    //      served by a ground scout, an airborne recon wing, or a launchable air sortie.
    //    · A GROUND-TRAVERSAL lane (Explore — a frontier hex that must be physically stood on to
    //      count as visited) can ONLY be served by a ground actor. Aviation reveals a hex; it
    //      never visits it, so it is NEVER counted here.
    //
    //  BUDGET DISCIPLINE (review round 3): all air observation capacity comes from ONE place,
    //  ReconAirCapacityPolicy (WorldSnapshot.SelfSnapshot.AirborneReconWings /
    //  SpareAirObservationSorties). That policy runs a single greedy AP/Energy pass bounded by the
    //  per-turn air-recon actor slot cap, so the same AP/Energy is never counted for two aircraft
    //  and the executor's own MaxAirReconActorsPerTurn ceiling is honoured here too. This class no
    //  longer re-derives air readiness (which had diverged from the executor).
    //
    //  STEALTH: deficits here are sized for GENERIC (non-stealth) lanes only. Neither aviation nor
    //  an ordinary scout can serve a stealth-required objective, so stealth is DemandLayer's
    //  dedicated path. An ACTIVE stealth lane (ScoutIntent.RequiresStealth) is excluded from the
    //  generic active-lane sets so it can't mask a real generic deficit.
    // ===========================================================================================
    internal sealed class ReconCapacitySnapshot
    {
        // Active durable GENERIC (non-stealth) lanes, by their claimed mover id (disjoint sets).
        public readonly HashSet<int> GenericGroundLaneActors = new HashSet<int>();
        public readonly HashSet<int> GenericObservationLaneActors = new HashSet<int>();
        // Idle-usable ground solo Recce (can serve either generic class, counted once).
        public readonly HashSet<int> IdleGroundScouts = new HashSet<int>();

        // Air observation capacity — counts, not ids (a hangar sortie has no army id yet).
        public int AirborneReconLanes;          // wings already flying a durable ReconPatrolState
        public int SpareAirObservationSorties;  // ADDITIONAL sorties launchable this turn (slot + AP/Energy bounded)

        // Sized for GENERIC lanes only.
        public int DesiredObservationConcurrency;
        public int DesiredGroundTraversalConcurrency;
        public int CombinedDesiredConcurrency;

        public int ObservationDeficit;
        public int GroundTraversalDeficit;

        // Raw usable-supply counts behind the two deficits above (pre-bootstrap). Persistence-gate
        // Rule 1 (zero-capacity bootstrap) needs the EXACT class-scoped supply, not the cross-class
        // union in ExistingGroundUsableCapacity below — an Observation lane can be served by air
        // with zero ground actors present, so "0 ground usable" must not gate Observation bootstrap.
        public int GroundTraversalSupply;
        public int ObservationSupply;

        // Distinct GENERIC GROUND actors already in hand — deduped ids (a scout counted once even
        // though it could serve either class): active generic Explore/Refresh/Surveil lanes plus
        // idle-usable solo Recce. This is what DemandLayer's global-concurrency clamp subtracts.
        // Air capacity is DELIBERATELY NOT folded in: aviation can close an Observation lane but
        // NEVER a GroundTraversal lane, so letting it shrink the combined ceiling would let a
        // helicopter phantom-cover a required physical visit (review round 4, P0).
        public int ExistingGroundUsableCapacity;

        public string Explain =>
            $"desiredObs={DesiredObservationConcurrency} desiredGround={DesiredGroundTraversalConcurrency} "
            + $"combinedCeiling={CombinedDesiredConcurrency} existingGroundUsable={ExistingGroundUsableCapacity} "
            + $"obs[genLanes={GenericObservationLaneActors.Count} airborne={AirborneReconLanes} "
            + $"spareAir={SpareAirObservationSorties}] "
            + $"ground[genLanes={GenericGroundLaneActors.Count} idleScouts={IdleGroundScouts.Count}] "
            + $"=> obsDeficit={ObservationDeficit} groundTraversalDeficit={GroundTraversalDeficit}";

        private static bool IsStealth(ReconObjective o) =>
            o != null && (o.Stealth == StealthRequirement.Required || o.DetectionRisk > 0f);

        // observationRunnable — runnable Refresh/Surveil objectives; groundVisitRunnable — runnable
        // Explore objectives.
        public static ReconCapacitySnapshot Build(WorldSnapshot snap,
            IReadOnlyList<ReconObjective> observationRunnable,
            IReadOnlyList<ReconObjective> groundVisitRunnable,
            IReadOnlyList<MissionIntent> activeIntents,
            ActorCommitments commitments,
            PlayerSetupData player,
            ReconAirReservationState airReservation = null)
        {
            var obsGeneric = (observationRunnable ?? System.Array.Empty<ReconObjective>())
                .Where(o => !IsStealth(o)).ToList();
            var groundGeneric = (groundVisitRunnable ?? System.Array.Empty<ReconObjective>())
                .Where(o => !IsStealth(o)).ToList();
            var allGeneric = obsGeneric.Concat(groundGeneric)
                .OrderByDescending(o => o.BaseValue).ThenBy(o => o.IntentKey).ToList();

            var cap = new ReconCapacitySnapshot
            {
                DesiredObservationConcurrency = ReconConcurrencyPolicy.DesiredForClass(
                    snap, obsGeneric, ReconConcurrencyPolicy.ReconCoverageClass.Observation),
                DesiredGroundTraversalConcurrency = ReconConcurrencyPolicy.DesiredForClass(
                    snap, groundGeneric, ReconConcurrencyPolicy.ReconCoverageClass.GroundTraversal),
                CombinedDesiredConcurrency = Mathf.Min(allGeneric.Count, ReconConcurrencyPolicy.DesiredForClass(
                    snap, allGeneric, ReconConcurrencyPolicy.ReconCoverageClass.Combined)),
                // AI-RECON-01 — air observation capacity is only what the Recon Air Reservation
                // Prepass has actually PINNED + resource-protected this turn, never a fresh unpinned
                // ReconAirCapacityPolicy re-evaluation the pipeline never committed to (that was the
                // phantom-capacity path: model says the helicopter covers a lane, nothing reserved
                // its AP/Energy, Phase A spends it, the sortie can't launch).
                AirborneReconLanes = Mathf.Max(0, airReservation?.ReservedAirborneWings
                    ?? snap?.Self?.AirborneReconWings ?? 0),
                SpareAirObservationSorties = Mathf.Max(0, airReservation?.ReservedLaunchSorties
                    ?? snap?.Self?.SpareAirObservationSorties ?? 0),
            };

            HashSet<int> claimed = commitments?.ClaimedArmyIdSet ?? new HashSet<int>();

            // --- Active durable GENERIC lanes, split by requirement. A claimed mover only; a
            //     RequiresStealth lane is NOT generic capacity and is skipped here.
            if (activeIntents != null && commitments != null)
                foreach (MissionIntent i in activeIntents)
                {
                    if (i?.Scout == null || i.PreferredMoverArmyId == null
                        || !commitments.IsArmyClaimed(i.PreferredMoverArmyId.Value)
                        || i.Scout.RequiresStealth)
                        continue;
                    int id = i.PreferredMoverArmyId.Value;
                    if (i.Scout.Kind == ScoutTargetKind.Explore)
                        cap.GenericGroundLaneActors.Add(id);
                    else
                        cap.GenericObservationLaneActors.Add(id);   // Refresh / Surveil == observation freshness
                }

            // --- Idle-usable ground scouts (solo Recce, MP left, not committed, not on a lane).
            //     A scout on a stealth lane is `claimed`, so it is excluded here automatically.
            IReadOnlyList<ArmySnapshot> armies = snap?.Self?.Armies ?? System.Array.Empty<ArmySnapshot>();
            foreach (ArmySnapshot a in armies)
            {
                if (a == null || !a.IsSoloRecce || a.IsPrison || a.IsAir || a.MemberCount <= 0)
                    continue;
                if (a.CurrentMovement <= 0)
                    continue;
                if (claimed.Contains(a.ArmyId)
                    || cap.GenericGroundLaneActors.Contains(a.ArmyId)
                    || cap.GenericObservationLaneActors.Contains(a.ArmyId))
                    continue;
                cap.IdleGroundScouts.Add(a.ArmyId);
            }

            // --- Ground-traversal has first claim on the shared idle-scout pool: it is the only
            //     requirement class aviation cannot help with.
            int groundTravSupply = cap.GenericGroundLaneActors.Count + cap.IdleGroundScouts.Count;
            cap.GroundTraversalSupply = groundTravSupply;
            cap.GroundTraversalDeficit =
                Mathf.Max(0, cap.DesiredGroundTraversalConcurrency - groundTravSupply);

            int idleConsumedByTraversal = Mathf.Min(cap.IdleGroundScouts.Count,
                Mathf.Max(0, cap.DesiredGroundTraversalConcurrency - cap.GenericGroundLaneActors.Count));
            int idleGroundForObs = cap.IdleGroundScouts.Count - idleConsumedByTraversal;

            int obsSupply = cap.GenericObservationLaneActors.Count
                + cap.AirborneReconLanes
                + cap.SpareAirObservationSorties
                + idleGroundForObs;
            cap.ObservationSupply = obsSupply;
            cap.ObservationDeficit = Mathf.Max(0, cap.DesiredObservationConcurrency - obsSupply);

            var distinctGround = new HashSet<int>(cap.GenericGroundLaneActors);
            distinctGround.UnionWith(cap.GenericObservationLaneActors);
            distinctGround.UnionWith(cap.IdleGroundScouts);
            cap.ExistingGroundUsableCapacity = distinctGround.Count;

            return cap;
        }
    }

    internal enum ReconDeficitKind { Observation, GroundTraversal }

    // Persistence gate for spec §7 ("a PERSISTENT usable capacity deficit"). A capacity deficit
    // that shows for a single demand evaluation and is gone the next — a normal artefact of a
    // scout finishing one leg and the next mission not yet admitted — must not trigger a fresh
    // Scout materialisation. RegisterAndCheck returns true only once the same deficit class has
    // held for reconCapacityDeficitPersistTurns CONSECUTIVE turns beyond the turn it first
    // appeared (persist = 0 => act immediately).
    internal static class ReconCapacityDeficitRegistry
    {
        private sealed class Entry
        {
            public int FirstSeenTurn = -1;
            public int LastSeenTurn = -1;
        }

        private static readonly Dictionary<PlayerSetupData, Dictionary<ReconDeficitKind, Entry>> ByPlayer =
            new Dictionary<PlayerSetupData, Dictionary<ReconDeficitKind, Entry>>();

        public static void ClearAll() => ByPlayer.Clear();

        public static bool RegisterAndCheck(PlayerSetupData player, int turn, ReconDeficitKind kind,
            int deficit, out int streakTurns)
        {
            streakTurns = 0;
            if (player == null)
                return deficit > 0;   // no per-player store — act on the live reading

            if (!ByPlayer.TryGetValue(player, out Dictionary<ReconDeficitKind, Entry> byKind))
                ByPlayer[player] = byKind = new Dictionary<ReconDeficitKind, Entry>();
            if (!byKind.TryGetValue(kind, out Entry e))
                byKind[kind] = e = new Entry();

            if (deficit <= 0)
            {
                e.FirstSeenTurn = -1;
                e.LastSeenTurn = turn;
                return false;
            }

            if (e.FirstSeenTurn < 0 || turn - e.LastSeenTurn > 1)
                e.FirstSeenTurn = turn;       // first sighting, or the streak was broken by a gap
            e.LastSeenTurn = turn;
            streakTurns = Mathf.Max(0, turn - e.FirstSeenTurn);
            return streakTurns >= Mathf.Max(0, AiConfigV2.reconCapacityDeficitPersistTurns);
        }
    }
}
