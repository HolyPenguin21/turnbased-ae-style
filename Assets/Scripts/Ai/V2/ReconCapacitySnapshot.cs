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
        public readonly HashSet<int> ReadyAirObservationActors = new HashSet<int>(); // launched wing not on recon, still able to fly a step this turn (MP + AP + Energy)
        public readonly HashSet<int> AirborneObservationActors = new HashSet<int>(); // recon wing already in flight (durable ReconAssignment)
        public readonly HashSet<int> PlannedAirObservationActors = new HashSet<int>();// funded-but-unlaunched sortie — NOT modelled yet (see note); always empty

        // ---- Ground-traversal-capable actors (aviation is structurally excluded) ----------------
        public readonly HashSet<int> GroundTraversalActors = new HashSet<int>();

        // Aircraft parked in owned airfield storage that could launch a recon sortie this turn
        // (WorldSnapshot.SelfSnapshot.LaunchableStoredAircraft — greedy AP+Energy vs cheapest
        // launch cost). These have no army id, so they are a count, not an id set. THIS is the
        // "a ready helicopter can already cover an observation lane" capacity the task is about.
        public int ReadyStoredAirObservationCapacity;

        // ---- Lane accounting (counts derived from the actor sets above) -------------------------
        public int ActiveObservationLanes;
        public int ReservedObservationLanes;         // funded-but-unlaunched air sorties (always 0 today)
        public int ActiveGroundTraversalLanes;
        public int ReservedGroundTraversalLanes;     // no reservation primitive today; kept for the model

        // All three are sized for GENERIC (non-stealth) lanes only — stealth objectives are the
        // dedicated stealth path's job in DemandLayer, never covered by aviation or a generic scout.
        public int DesiredObservationConcurrency;
        public int DesiredGroundTraversalConcurrency;
        public int CombinedDesiredConcurrency;       // HardCap-bounded ceiling the two deficits together may chase

        public int ObservationDeficit;
        public int GroundTraversalDeficit;

        public string Explain =>
            $"desiredObs={DesiredObservationConcurrency} desiredGround={DesiredGroundTraversalConcurrency} "
            + $"combinedCeiling={CombinedDesiredConcurrency} "
            + $"obs[active={ActiveObservationLanes} airborne={AirborneObservationActors.Count} "
            + $"readyWing={ReadyAirObservationActors.Count} readyHangar={ReadyStoredAirObservationCapacity} "
            + $"groundObs={GroundObservationActors.Count}] "
            + $"ground[active={ActiveGroundTraversalLanes} actors={GroundTraversalActors.Count}] "
            + $"=> obsDeficit={ObservationDeficit} groundTraversalDeficit={GroundTraversalDeficit}";

        private static bool IsStealth(ReconObjective o) =>
            o != null && (o.Stealth == StealthRequirement.Required || o.DetectionRisk > 0f);

        // observationRunnable — runnable Refresh/Surveil objectives; groundVisitRunnable — runnable
        // Explore objectives. NOTE on §5 (a funded-but-unlaunched air sortie counting as capacity):
        // AiAviationSupport.LaunchRoutine creates the AiTask and the actor claim only AFTER a
        // successful TryLaunch, so there is no state today that represents "sortie reserved +
        // funded, not yet airborne". Modelling that needs a pre-launch air reservation and is
        // deferred to the actor-reservation work (AI-RECON-01); PlannedAirObservationActors stays
        // empty until then rather than being faked from the post-launch AirRecon task.
        public static ReconCapacitySnapshot Build(WorldSnapshot snap,
            IReadOnlyList<ReconObjective> observationRunnable,
            IReadOnlyList<ReconObjective> groundVisitRunnable,
            IReadOnlyList<MissionIntent> activeIntents,
            ActorCommitments commitments,
            PlayerSetupData player)
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
            };

            HashSet<int> claimed = commitments?.ClaimedArmyIdSet ?? new HashSet<int>();

            // --- Active durable lanes, split by requirement. Only a claimed mover is a real lane;
            //     a corrupted/duplicated intent is surfaced elsewhere, not double-counted here.
            //     (An active lane's stealth-ness is not tracked on the intent; a rare active stealth
            //     lane counts here as a generic lane, which can only UNDER-state a generic deficit —
            //     the missing scout is still recovered by the persistence gate + next-turn recompute.)
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

            IReadOnlyList<ArmySnapshot> armies = snap?.Self?.Armies ?? System.Array.Empty<ArmySnapshot>();
            var airRecoTasked = new HashSet<int>();
            if (player != null)
                foreach (AiTask t in AiTaskRegistry.TasksFor(player))
                    if (t != null && t.Kind == AiTaskKind.AirRecon && t.Army != null)
                        airRecoTasked.Add(t.Army.Id);

            // --- Aviation already represented as standalone armies == LAUNCHED wings. A wing with a
            //     durable ReconAssignment is an active observation lane; one with no assignment is
            //     only spare observation capacity if it can genuinely fly a step this turn: it must
            //     still have MP, and (unless already activated) its first-activation AP and Energy
            //     must be really spendable — the same gate the real launch/first-move path applies.
            var airArmies = armies.Where(a => a != null && a.IsAir && !a.IsPrison && a.MemberCount > 0).ToList();
            foreach (ArmySnapshot a in airArmies)
                if (ReconAssignmentRegistry.TryGet(player, a.ArmyId, out _))
                    cap.AirborneObservationActors.Add(a.ArmyId);

            float committedAirEnergy = airArmies
                .Where(a => !a.HasActivatedThisTurn && cap.AirborneObservationActors.Contains(a.ArmyId))
                .Sum(a => Mathf.Max(0, a.ActivationEnergyCost));
            float spendableAirEnergy = Mathf.Max(0f, (snap?.Self?.Stockpile.Energy ?? 0f) - committedAirEnergy);
            int spendableAp = snap?.Self?.ActionPoints ?? 0;

            foreach (ArmySnapshot a in airArmies)
            {
                if (cap.AirborneObservationActors.Contains(a.ArmyId))
                    continue;
                if (claimed.Contains(a.ArmyId) || airRecoTasked.Contains(a.ArmyId))
                    continue;                                   // committed to other air work
                if (a.CurrentMovement <= 0)
                    continue;                                   // 0 MP — no sortie step possible this turn
                if (!a.HasActivatedThisTurn
                    && (a.ActivationEnergyCost > spendableAirEnergy || a.ActivationApCost > spendableAp))
                    continue;                                   // spec §6 — activation cost not really spendable
                cap.ReadyAirObservationActors.Add(a.ArmyId);
            }

            cap.ReadyStoredAirObservationCapacity = Mathf.Max(0, snap?.Self?.LaunchableStoredAircraft ?? 0);

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
                + cap.ReadyStoredAirObservationCapacity
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
