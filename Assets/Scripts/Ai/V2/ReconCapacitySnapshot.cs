using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  AI-RECON-02 — UNIFIED RECON CAPACITY MODEL
    // ===========================================================================================
    //  Observation capacity and ground-traversal capacity are DIFFERENT resources and are counted
    //  separately:
    //
    //    · An OBSERVATION lane (Refresh / Surveil — keep fresh eyes on a hex or a contact) can be
    //      served by a ground scout, a ready aircraft on its airfield, an already-airborne recon
    //      wing, OR an air sortie that is actor-reserved + funded but not yet launched. A funded
    //      sortie already reduces the observation deficit (spec §5) — DemandLayer must not invent
    //      an extra recon need in the gap between "sortie funded" and "wing airborne".
    //
    //    · A GROUND-TRAVERSAL lane (Explore — a frontier hex that must be physically stood on to
    //      count as visited) can ONLY be served by a ground actor. Aviation reveals a hex; it
    //      never visits it, so it is NEVER counted here.
    //
    //  Everything is tracked by actor id / lane actor id (HashSet<int>), never by a raw count, so
    //  a single actor can never be counted into two lanes or two capacity pools.
    //
    //  The two deficits are what DemandLayer acts on: a new Scout is materialised for Recon only
    //  when a real requirement exists AND a usable capacity deficit — already net of ready/airborne/
    //  funded aviation and idle ground scouts — persists (spec §7), NOT merely because the Recon
    //  desire axis is high.
    // ===========================================================================================
    internal sealed class ReconCapacitySnapshot
    {
        // ---- Observation-capable actors, partitioned into disjoint sets (no id in two sets) -----
        public readonly HashSet<int> GroundObservationActors = new HashSet<int>();   // ground scout on an obs lane OR idle-usable ground scout
        public readonly HashSet<int> ReadyAirObservationActors = new HashSet<int>(); // aircraft ready on an airfield, activation Energy still spendable
        public readonly HashSet<int> AirborneObservationActors = new HashSet<int>(); // recon wing already in flight (durable ReconAssignment)
        public readonly HashSet<int> PlannedAirObservationActors = new HashSet<int>();// actor-reserved + funded AirRecon sortie, not yet launched

        // ---- Ground-traversal-capable actors (aviation is structurally excluded) ----------------
        public readonly HashSet<int> GroundTraversalActors = new HashSet<int>();

        // ---- Lane accounting (counts derived from the actor sets above) -------------------------
        public int ActiveObservationLanes;
        public int ReservedObservationLanes;         // funded-but-unlaunched air sorties
        public int ActiveGroundTraversalLanes;
        public int ReservedGroundTraversalLanes;     // no reservation primitive today; kept for the model

        public int DesiredObservationConcurrency;
        public int DesiredGroundTraversalConcurrency;

        public int ObservationDeficit;
        public int GroundTraversalDeficit;

        public string Explain =>
            $"desiredObs={DesiredObservationConcurrency} desiredGround={DesiredGroundTraversalConcurrency} "
            + $"obs[active={ActiveObservationLanes} airborne={AirborneObservationActors.Count} "
            + $"plannedAir={PlannedAirObservationActors.Count} readyAir={ReadyAirObservationActors.Count} "
            + $"groundObs={GroundObservationActors.Count}] "
            + $"ground[active={ActiveGroundTraversalLanes} actors={GroundTraversalActors.Count}] "
            + $"=> obsDeficit={ObservationDeficit} groundTraversalDeficit={GroundTraversalDeficit}";

        // observationRunnable — runnable Refresh/Surveil objectives; groundVisitRunnable — runnable
        // Explore objectives. Both drive the desired-concurrency estimate through the SAME
        // ReconConcurrencyPolicy the single-pool path used, just per requirement class.
        public static ReconCapacitySnapshot Build(WorldSnapshot snap,
            IReadOnlyList<ReconObjective> observationRunnable,
            IReadOnlyList<ReconObjective> groundVisitRunnable,
            IReadOnlyList<MissionIntent> activeIntents,
            ActorCommitments commitments,
            PlayerSetupData player)
        {
            var cap = new ReconCapacitySnapshot
            {
                DesiredObservationConcurrency = ReconConcurrencyPolicy.DesiredTotal(snap, observationRunnable),
                DesiredGroundTraversalConcurrency = ReconConcurrencyPolicy.DesiredTotal(snap, groundVisitRunnable),
            };

            HashSet<int> claimed = commitments?.ClaimedArmyIdSet ?? new HashSet<int>();

            // --- Active durable lanes, split by requirement. Only a claimed mover is a real lane;
            //     a corrupted/duplicated intent is surfaced elsewhere, not double-counted here.
            var observationLaneActors = new HashSet<int>();
            var groundLaneActors = new HashSet<int>();
            if (activeIntents != null && commitments != null)
                foreach (MissionIntent i in activeIntents)
                {
                    if (i?.Scout == null || i.PreferredMoverArmyId == null
                        || !commitments.IsArmyClaimed(i.PreferredMoverArmyId.Value))
                        continue;
                    int id = i.PreferredMoverArmyId.Value;
                    if (i.Scout.Kind == ScoutTargetKind.Explore)
                        groundLaneActors.Add(id);
                    else
                        observationLaneActors.Add(id);   // Refresh / Surveil == observation freshness
                }

            // --- Aviation. First classify airborne + funded (they also commit first-activation
            //     Energy), then decide which parked aircraft are actually ready.
            IReadOnlyList<ArmySnapshot> armies = snap?.Self?.Armies ?? System.Array.Empty<ArmySnapshot>();
            var airRecoTasked = new HashSet<int>();
            if (player != null)
                foreach (AiTask t in AiTaskRegistry.TasksFor(player))
                    if (t != null && t.Kind == AiTaskKind.AirRecon && t.Army != null)
                        airRecoTasked.Add(t.Army.Id);

            var airArmies = armies.Where(a => a != null && a.IsAir && !a.IsPrison && a.MemberCount > 0).ToList();
            foreach (ArmySnapshot a in airArmies)
            {
                bool airborne = ReconAssignmentRegistry.TryGet(player, a.ArmyId, out _);
                if (airborne)
                {
                    cap.AirborneObservationActors.Add(a.ArmyId);
                    continue;
                }
                if (airRecoTasked.Contains(a.ArmyId))
                    cap.PlannedAirObservationActors.Add(a.ArmyId);
            }

            // Energy the airborne / funded sorties still owe on their own first activation this turn
            // — a parked aircraft is only "ready" if its activation Energy is spendable on top.
            float committedAirEnergy = airArmies
                .Where(a => !a.HasActivatedThisTurn
                    && (cap.AirborneObservationActors.Contains(a.ArmyId)
                        || cap.PlannedAirObservationActors.Contains(a.ArmyId)))
                .Sum(a => Mathf.Max(0, a.ActivationEnergyCost));
            float spendableAirEnergy = Mathf.Max(0f, (snap?.Self?.Stockpile.Energy ?? 0f) - committedAirEnergy);

            foreach (ArmySnapshot a in airArmies)
            {
                if (cap.AirborneObservationActors.Contains(a.ArmyId)
                    || cap.PlannedAirObservationActors.Contains(a.ArmyId))
                    continue;
                if (claimed.Contains(a.ArmyId))
                    continue;                                   // committed to other work
                if (!a.HasActivatedThisTurn && a.CurrentMovement <= 0)
                    continue;                                   // spent this turn — cannot fly
                if (!a.HasActivatedThisTurn && a.ActivationEnergyCost > spendableAirEnergy)
                    continue;                                   // spec §6 — activation Energy not really spendable
                cap.ReadyAirObservationActors.Add(a.ArmyId);
            }

            // --- Idle-usable ground scouts (solo Recce, not spent, not on a lane, not committed).
            //     Matches ScoutMoverSelector.Eligible's turn-transient filter (CurrentMovement > 0).
            var idleGroundScouts = new HashSet<int>();
            foreach (ArmySnapshot a in armies)
            {
                if (a == null || !a.IsSoloRecce || a.IsPrison || a.IsAir || a.MemberCount <= 0)
                    continue;
                if (a.CurrentMovement <= 0)
                    continue;
                if (claimed.Contains(a.ArmyId)
                    || observationLaneActors.Contains(a.ArmyId) || groundLaneActors.Contains(a.ArmyId))
                    continue;
                idleGroundScouts.Add(a.ArmyId);
            }

            cap.GroundObservationActors.UnionWith(observationLaneActors);
            cap.GroundObservationActors.UnionWith(idleGroundScouts);
            cap.GroundTraversalActors.UnionWith(groundLaneActors);
            cap.GroundTraversalActors.UnionWith(idleGroundScouts);

            cap.ActiveObservationLanes = observationLaneActors.Count;
            cap.ActiveGroundTraversalLanes = groundLaneActors.Count;
            cap.ReservedObservationLanes = cap.PlannedAirObservationActors.Count;

            // --- Ground-traversal has first claim on the shared idle-scout pool: it is the only
            //     requirement class aviation cannot help with.
            int groundTravSupply = cap.ActiveGroundTraversalLanes + idleGroundScouts.Count;
            cap.GroundTraversalDeficit =
                Mathf.Max(0, cap.DesiredGroundTraversalConcurrency - groundTravSupply);

            int idleConsumedByTraversal = Mathf.Min(idleGroundScouts.Count,
                Mathf.Max(0, cap.DesiredGroundTraversalConcurrency - cap.ActiveGroundTraversalLanes));
            int idleGroundForObs = idleGroundScouts.Count - idleConsumedByTraversal;

            int obsSupply = cap.ActiveObservationLanes
                + cap.AirborneObservationActors.Count
                + cap.ReservedObservationLanes
                + cap.ReadyAirObservationActors.Count
                + idleGroundForObs;
            cap.ObservationDeficit = Mathf.Max(0, cap.DesiredObservationConcurrency - obsSupply);

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
