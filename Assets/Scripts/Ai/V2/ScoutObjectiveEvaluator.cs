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
        // bother re-materialising it at all.
        public static bool IsIntentStillValid(WorldSnapshot snap, ScoutIntent intent)
        {
            if (snap == null || intent == null)
                return false;

            return intent.Kind == ScoutTargetKind.Surveil
                ? SurveilContact(snap, intent.TrackedArmyId) != null
                : ExploreStillOpen(snap, intent.FocusHex) > 0;
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
            MapKnowledgeSnapshot mk = snap?.MapKnowledge;
            if (mk?.AllHexes == null)
                return 0;

            var onMap = mk.AllHexes as HashSet<HexCoord> ?? new HashSet<HexCoord>(mk.AllHexes);
            if (!onMap.Contains(focus))
                return 0;
            if (mk.VisitedHexSet != null && mk.VisitedHexSet.Contains(focus))
                return 0;
            if (mk.ScoutHardBlockedHexes != null && mk.ScoutHardBlockedHexes.Contains(focus))
                return 0;

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
