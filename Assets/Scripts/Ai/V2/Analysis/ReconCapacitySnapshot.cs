using System.Collections.Generic;
using System.Linq;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  UNIFIED RECON CAPACITY MODEL
    // ===========================================================================================
    //  Observation capacity and ground-traversal capacity are DIFFERENT resources:
    //
    //    · An OBSERVATION lane (Refresh / Surveil — keep fresh eyes on a hex or a contact) can be
    //      served by a ground scout, an airborne recon wing, or a launchable air sortie.
    //    · A GROUND-TRAVERSAL lane (Explore — a frontier hex that must be physically stood on to
    //      count as visited) can ONLY be served by a ground actor. Aviation reveals a hex; it
    //      never visits it, so it is NEVER counted here.
    //
    //  BUDGET DISCIPLINE: all air observation capacity comes from ONE place,
    //  ReconAirCapacityPolicy (WorldSnapshot.SelfSnapshot.AirborneReconWings /
    //  SpareAirObservationSorties). That policy runs a single greedy AP/Energy pass bounded by the
    //  per-turn air-recon actor slot cap, so the same AP/Energy is never counted for two aircraft
    //  and the executor's own MaxAirReconActorsPerTurn ceiling is honoured here too. This class
    //  never re-derives air readiness itself.
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

        // Current-turn usable-supply counts behind the two deficits above. These may drop to
        // zero simply because an otherwise valid scout has spent its movement this turn.
        public int GroundTraversalSupply;
        public int ObservationSupply;

        // Durable/structural capacity for Rule 1 (zero-capacity bootstrap). Unlike the executable
        // supply above, an existing generic scout still counts here when it has spent its MP or was
        // trimmed for the remainder of THIS turn: it will be usable again after the normal turn
        // refresh, so that transient condition must not immediately manufacture another Scout.
        // Active generic lanes are counted regardless of current MP. Non-Recon claimed actors stay
        // excluded; a genuinely unavailable actor must not mask a persistent capacity deficit.
        public int StructuralGroundTraversalSupply;
        public int StructuralObservationSupply;

        // Distinct GENERIC GROUND actors already in hand — deduped ids (a scout counted once even
        // though it could serve either class): active generic Explore/Refresh/Surveil lanes plus
        // idle-usable solo Recce. This is what DemandLayer's global-concurrency clamp subtracts.
        // Air capacity is DELIBERATELY NOT folded in: aviation can close an Observation lane but
        // NEVER a GroundTraversal lane, so letting it shrink the combined ceiling would let a
        // helicopter phantom-cover a required physical visit.
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
            int airborneWitnessed = 0,
            int spareLaunchWitnessed = 0)
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
                // Air observation capacity is the WITNESSED count
                // ReconAssignmentPlanner.MeasureAirCapacity just computed (the same "does a usable
                // actor structurally exist" question MeasureCapacity answers for ground), never a
                // fresh unpinned ReconAirCapacityPolicy re-evaluation the pipeline did not commit to:
                // that would let the model count a helicopter nothing reserved AP/Energy for, Phase A
                // spend it, and the sortie fail to launch.
                AirborneReconLanes = Mathf.Max(0, airborneWitnessed),
                SpareAirObservationSorties = Mathf.Max(0, spareLaunchWitnessed),
            };

            HashSet<int> claimed = commitments?.ClaimedArmyIdSet ?? new HashSet<int>();
            var ownById = (snap?.Self?.Armies ?? System.Array.Empty<ArmySnapshot>())
                .Where(a => a != null).ToDictionary(a => a.ArmyId, a => a);
            // Continuity's turn-wide contraction makes the released actor unavailable to Recon
            // until the next turn. Do not advertise it as idle supply after removing its intent.
            IReadOnlyCollection<int> trimmedThisTurn = player != null && snap != null
                ? MissionIntentRegistry.GetOrCreate(player).ReconActorsTrimmedThisTurn(snap.TurnNumber)
                : System.Array.Empty<int>();

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
                    // Air observation is measured exactly once by MeasureAirCapacity below. It is
                    // not a generic ground lane and must not inflate ExistingGroundUsableCapacity.
                    if (!ownById.TryGetValue(id, out ArmySnapshot actor) || actor.IsAir)
                        continue;
                    if (i.Scout.Kind == ScoutTargetKind.Explore)
                        cap.GenericGroundLaneActors.Add(id);
                    else
                        cap.GenericObservationLaneActors.Add(id);   // Refresh / Surveil == observation freshness
                }

            // --- Idle ground scouts. Keep TWO horizons:
            //     * structuralIdleGroundScouts — the physical generic capacity that survives a
            //       turn refresh (MP may be 0 / this actor may have been trimmed for this turn);
            //     * IdleGroundScouts — executable THIS turn, used by the real deficits/matching.
            // A scout claimed by another durable mission (including a stealth lane) belongs to
            // neither pool. Active generic Recon lanes are counted by their lane sets above.
            IReadOnlyList<ArmySnapshot> armies = snap?.Self?.Armies ?? System.Array.Empty<ArmySnapshot>();
            var structuralIdleGroundScouts = new HashSet<int>();
            foreach (ArmySnapshot a in armies)
            {
                if (a == null || !a.IsSoloRecce || a.IsPrison || a.IsAir || a.MemberCount <= 0)
                    continue;
                if (claimed.Contains(a.ArmyId)
                    || cap.GenericGroundLaneActors.Contains(a.ArmyId)
                    || cap.GenericObservationLaneActors.Contains(a.ArmyId))
                    continue;

                structuralIdleGroundScouts.Add(a.ArmyId);
                if (a.CurrentMovement <= 0 || trimmedThisTurn.Contains(a.ArmyId))
                    continue;
                cap.IdleGroundScouts.Add(a.ArmyId);
            }

            // --- Ground-traversal has first claim on the shared idle-scout pool: it is the only
            //     requirement class aviation cannot help with.
            int groundTravSupply = cap.GenericGroundLaneActors.Count + cap.IdleGroundScouts.Count;
            cap.GroundTraversalSupply = groundTravSupply;
            cap.GroundTraversalDeficit =
                Mathf.Max(0, cap.DesiredGroundTraversalConcurrency - groundTravSupply);

            cap.StructuralGroundTraversalSupply =
                cap.GenericGroundLaneActors.Count + structuralIdleGroundScouts.Count;

            int idleConsumedByTraversal = Mathf.Min(cap.IdleGroundScouts.Count,
                Mathf.Max(0, cap.DesiredGroundTraversalConcurrency - cap.GenericGroundLaneActors.Count));
            int idleGroundForObs = cap.IdleGroundScouts.Count - idleConsumedByTraversal;

            int obsSupply = cap.GenericObservationLaneActors.Count
                + cap.AirborneReconLanes
                + cap.SpareAirObservationSorties
                + idleGroundForObs;
            cap.ObservationSupply = obsSupply;
            cap.ObservationDeficit = Mathf.Max(0, cap.DesiredObservationConcurrency - obsSupply);

            // The bootstrap horizon mirrors the same ground-first sharing rule, but uses physical
            // idle scouts rather than only movers that can execute right now. Air stays on the
            // canonical air-capacity counts: this change fixes the proven ground-scout time-horizon
            // bug without introducing a second air-readiness authority.
            int structuralIdleConsumedByTraversal = Mathf.Min(structuralIdleGroundScouts.Count,
                Mathf.Max(0, cap.DesiredGroundTraversalConcurrency - cap.GenericGroundLaneActors.Count));
            int structuralIdleGroundForObs =
                structuralIdleGroundScouts.Count - structuralIdleConsumedByTraversal;
            cap.StructuralObservationSupply = cap.GenericObservationLaneActors.Count
                + cap.AirborneReconLanes
                + cap.SpareAirObservationSorties
                + structuralIdleGroundForObs;

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
