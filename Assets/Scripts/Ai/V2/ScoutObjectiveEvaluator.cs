using System.Collections.Generic;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  SCOUT OBJECTIVE EVALUATOR  (Strategy V2 build-order step 7 — the single completion/validity home)
    // ===========================================================================================
    //  "Is this scout objective done / still worth pursuing" was being answered in three places
    //  independently (ProvisioningManager, TaskExecutor.IsSurveilSatisfied, and — as of step 7 —
    //  the continuity layer). That is exactly the kind of drift ScoutCostModel was extracted to
    //  prevent. One home now:
    //
    //    · LIVE overloads  — read the mutated world directly (VisionSystem / AiMapMemory). Called
    //                        at execution / provisioning time, where post-mutation live state IS
    //                        the truth and there is no fog on our own observations this turn.
    //    · SNAPSHOT overload (IsIntentStillValid) — reads ONLY the turn's WorldSnapshot. Called by
    //                        MissionLayer when it re-materialises a durable intent, so proposal
    //                        creation never queries a mutable game system.
    // ===========================================================================================
    public static class ScoutObjectiveEvaluator
    {
        // ---- LIVE (execution / provisioning / post-execution) --------------------------------

        // Explore is done the moment its focus hex is VISITED (only standing on a hex marks it so).
        public static bool IsExploreSatisfiedLive(PlayerSetupData player, HexCoord focus) =>
            VisionSystem.IsVisited(player, focus);

        // Surveil is an INFORMATION objective: the focus hex visible again, OR the tracked army
        // honestly re-sighted ANYWHERE with a SeenTurn past the baseline. Honest memory only —
        // never TrueWorld.
        public static bool IsSurveilSatisfiedLive(PlayerSetupData player, HexCoord focus,
            int? trackedArmyId, int baselineObservedTurn)
        {
            if (VisionSystem.IsVisible(player, focus))
                return true;
            if (!trackedArmyId.HasValue)
                return false;
            foreach (AiMapMemory.KnownEnemySighting s in AiMapMemory.AllKnownEnemySightings(player))
                if (s.ArmyId == trackedArmyId.Value && s.SeenTurn > baselineObservedTurn)
                    return true;
            return false;
        }

        // ---- SNAPSHOT (mission-layer re-materialisation) ------------------------------------

        // Is a durable intent still a coherent thing to pursue against THIS snapshot? Not
        // "satisfied" (that retires it as a win); "still valid" gates whether MissionLayer should
        // bother re-materialising it at all. NOTE this DOES fold in "already satisfied": a Surveil
        // whose tracked army has been honestly re-observed SINCE the intent's baseline (by this
        // scout, another scout, or any other action) is done — the snapshot knows via
        // EnemyContactSnapshot.LastObservedTurn, so an intent must not keep chasing a fix it
        // already has. ResolveActive purges what this rejects; the retirement is logged there.
        public static bool IsIntentStillValid(WorldSnapshot snap, ScoutIntent intent)
        {
            if (snap == null || intent == null)
                return false;

            if (intent.Kind == ScoutTargetKind.Surveil)
            {
                EnemyContactSnapshot contact = SurveilContact(snap, intent.TrackedArmyId);
                return contact != null && contact.LastObservedTurn <= intent.BaselineObservedTurn;
            }
            // §6 — ONE Explore validity contract, shared with fresh Recon objective enumeration
            // (ReconObjectiveEvaluator). FreshNeighbors == 0 is a productivity/value signal, NOT
            // an invalidation: an unvisited, unblocked frontier focus stays a coherent objective
            // even when every immediate neighbour is currently visited — reaching it still marks
            // its own tile and can expand the frontier.
            return IsExploreFocusRunnable(snap, intent.FocusHex);
        }

        // THE authoritative Explore validity predicate. An Explore focus is a runnable objective
        // iff it is a real map hex that has not been visited and is not scout-hard-blocked.
        public static bool IsExploreFocusRunnable(WorldSnapshot snap, HexCoord focus)
        {
            MapKnowledgeSnapshot mk = snap?.MapKnowledge;
            if (mk?.AllHexes == null)
                return false;
            var onMap = mk.AllHexes as HashSet<HexCoord> ?? new HashSet<HexCoord>(mk.AllHexes);
            if (!onMap.Contains(focus))
                return false;
            if (mk.VisitedHexSet != null && mk.VisitedHexSet.Contains(focus))
                return false;
            if (mk.ScoutHardBlockedHexes != null && mk.ScoutHardBlockedHexes.Contains(focus))
                return false;
            return true;
        }

        // The honest, positioned, last-known contact a Surveil intent tracks — or null if the AI no
        // longer has one (re-observed, aged out of AiReconMemory, or only a cheat-region signal
        // remains). Read from the snapshot's by-army lookup, never AiReconMemory directly.
        public static EnemyContactSnapshot SurveilContact(WorldSnapshot snap, int? trackedArmyId)
        {
            if (!trackedArmyId.HasValue || snap?.Threat?.ReconContactByArmyId == null)
                return null;
            if (!snap.Threat.ReconContactByArmyId.TryGetValue(trackedArmyId.Value, out EnemyContactSnapshot c))
                return null;
            return c != null && c.Source == ContactSource.Honest
                   && c.Knowledge == ContactKnowledge.LastKnown && c.Position.HasValue
                ? c : null;
        }

        // How many still-openable neighbours an Explore focus hex has against this snapshot: 0 ==
        // the hex is visited, hard-blocked, or fully boxed in by visited/blocked ground — nothing
        // left to discover there, so the intent is stale.
        public static int ExploreStillOpen(WorldSnapshot snap, HexCoord focus)
        {
            if (!IsExploreFocusRunnable(snap, focus))
                return 0;

            MapKnowledgeSnapshot mk = snap.MapKnowledge;
            var onMap = mk.AllHexes as HashSet<HexCoord> ?? new HashSet<HexCoord>(mk.AllHexes);
            int fresh = 0;
            foreach (HexCoord n in HexGridMath.Neighbors(focus))
            {
                if (!onMap.Contains(n))
                    continue;
                if (mk.VisitedHexSet != null && mk.VisitedHexSet.Contains(n))
                    continue;
                if (mk.ScoutHardBlockedHexes != null && mk.ScoutHardBlockedHexes.Contains(n))
                    continue;
                fresh++;
            }
            return fresh;
        }
    }
}
