using System.Collections.Generic;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  SCOUT OBJECTIVE EVALUATOR  (Strategy V2 build-order step 7 — the single completion/validity home)
    // ===========================================================================================
    //  One home for Explore / generic Refresh / contact Surveil lifecycle rules.
    //
    //    · LIVE overloads  — read the mutated world directly (VisionSystem / AiMapMemory). Called
    //                        at execution / provisioning time, where post-mutation live state IS
    //                        the truth and there is no fog on our own observations this turn.
    //    · SNAPSHOT overload (IsIntentStillValid) — reads ONLY the turn's WorldSnapshot plus the
    //                        frozen Recon IntelAge sidecar captured with that snapshot.
    // ===========================================================================================
    public static class ScoutObjectiveEvaluator
    {
        // ---- LIVE (execution / provisioning / post-execution) --------------------------------

        // Explore is done only by physical ground visitation.
        public static bool IsExploreSatisfiedLive(PlayerSetupData player, HexCoord focus) =>
            VisionSystem.IsVisited(player, focus);

        // Generic Refresh is an OBSERVATION objective. The focus was selected from stale frozen
        // IntelAge, so observing it again now is sufficient regardless of whether a ground unit
        // physically visits it. This preserves Observed != Visited and is future-compatible with
        // AirRecon refreshing information without claiming ground visitation.
        public static bool IsRefreshSatisfiedLive(PlayerSetupData player, HexCoord focus) =>
            VisionSystem.IsVisible(player, focus);

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

        // Is a durable intent still coherent against THIS frozen snapshot? This also folds in
        // already-satisfied state so completed intents are retired instead of re-materialised.
        public static bool IsIntentStillValid(WorldSnapshot snap, ScoutIntent intent)
        {
            if (snap == null || intent == null)
                return false;

            if (intent.Kind == ScoutTargetKind.Surveil)
            {
                EnemyContactSnapshot contact = SurveilContact(snap, intent.TrackedArmyId);
                return contact != null && contact.LastObservedTurn <= intent.BaselineObservedTurn;
            }

            if (ReconScoutKinds.IsRefresh(intent.Kind))
            {
                // A Refresh intent is valid only while the frozen IntelAge still says the focus is
                // genuinely stale. If another observer refreshed it before this scan, age is 0 and
                // the intent is already complete. Never-observed is not Refresh and returns false.
                return ReconIntelSnapshotRegistry.TryGetIntelAge(snap, intent.FocusHex, out int age)
                    && age >= AiConfigV2.scoutSurveilStaleTurnsLo
                    && IsRefreshFocusRunnable(snap, intent.FocusHex);
            }

            return IsExploreFocusRunnable(snap, intent.FocusHex);
        }

        // THE authoritative Explore validity predicate. An Explore focus is runnable iff it is a
        // real map hex that has not been physically visited and is not scout-hard-blocked.
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

        // Refresh explicitly allows previously visited ground; revisitation is the point. The only
        // structural gates are real on-map geometry and the shared Scout hard-block set.
        public static bool IsRefreshFocusRunnable(WorldSnapshot snap, HexCoord focus)
        {
            MapKnowledgeSnapshot mk = snap?.MapKnowledge;
            if (mk?.AllHexes == null)
                return false;
            var onMap = mk.AllHexes as HashSet<HexCoord> ?? new HashSet<HexCoord>(mk.AllHexes);
            if (!onMap.Contains(focus))
                return false;
            return mk.ScoutHardBlockedHexes == null || !mk.ScoutHardBlockedHexes.Contains(focus);
        }

        // The honest, positioned, last-known contact a Surveil intent tracks — or null if the AI no
        // longer has one. Read from the frozen snapshot lookup, never AiReconMemory directly.
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
