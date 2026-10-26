using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  SCOUT OBJECTIVE EVALUATOR  (Strategy V2 build-order step 7 — the single completion/validity home)
    // ===========================================================================================
    //  One home for Explore / generic Refresh lifecycle rules, judged on the turn's WorldSnapshot
    //  plus the frozen Recon IntelAge sidecar captured with that snapshot (IsIntentStillValid).
    //  The live counterparts read at execution / provisioning time are
    //  Continuity/ObjectiveLiveStatus (ScoutSatisfied, ExploreSatisfied, RefreshSatisfied).
    // ===========================================================================================
    public static class ScoutObjectiveEvaluator
    {
        // THE "a met objective is only a waypoint" rule (D5): a ground Explore / Refresh role whose
        // focus is met keeps its durable role (ProductiveStop -> re-focused next pass) when its
        // actor can still scout AND the role is already durable or the actor really acted this
        // turn. A fresh mission met before it ever acted is not turned into a role.
        public static bool RoleContinuesAtWaypoint(ScoutTargetKind kind, bool durableIntent,
            bool actorStillScout, bool actedThisTurn) =>
            (kind == ScoutTargetKind.Explore || kind == ScoutTargetKind.Refresh)
            && actorStillScout && (durableIntent || actedThisTurn);

        // ---- SNAPSHOT (mission-layer re-materialisation) ------------------------------------

        // Is a durable intent still coherent against THIS frozen snapshot? This also folds in
        // already-satisfied state so completed intents are retired instead of re-materialised.
        public static bool IsIntentStillValid(WorldSnapshot snap, ScoutIntent intent)
        {
            if (snap == null || intent == null)
                return false;

            if (ReconScoutKinds.IsDisruption(intent.Kind))
                return ReconObjectiveEvaluator.AssaultNeedsDisruption(snap, intent.FocusHex);
            if (ReconScoutKinds.IsCapture(intent.Kind))
                return snap.Self?.ReconCaptureOpportunities?.Any(x => x.Hex.Equals(intent.FocusHex)) == true;
            if (ReconScoutKinds.IsHunt(intent.Kind))
                return snap.Self?.ReconHuntOpportunities?.Any(x => x.Hex.Equals(intent.FocusHex)) == true;

            // AirSweep stays a live operation while an anchor exists (the enemy is somewhere);
            // it is never "met" by observation — each sortie ends by its own refuel endurance.
            if (ReconScoutKinds.IsAirSweep(intent.Kind))
                return WorldAnalysis.TryAirSweepAnchor(snap, out _, out _);

            if (ReconScoutKinds.IsRefresh(intent.Kind))
            {
                // A Refresh intent is valid only while the frozen IntelAge still says the focus is
                // genuinely stale. If another observer refreshed it before this scan, age is 0 and
                // the intent is already complete. Never-observed is not Refresh and returns false.
                return ReconIntelSnapshotRegistry.TryGetIntelAge(snap, intent.FocusHex, out int age)
                    && ReconIntelSnapshotRegistry.IsStaleAge(age)
                    && (IsRefreshFocusRunnable(snap, intent.FocusHex)
                        || (AttackObjectiveEvaluator.ObservationNeeds(snap).Contains(intent.FocusHex)
                            && IsAttackObservationFocusRunnable(snap, intent.FocusHex)));
            }

            return ReconScoutKinds.IsExplore(intent.Kind)
                && IsExploreFocusRunnable(snap, intent.FocusHex);
        }

        // THE authoritative Explore validity predicate. An Explore focus is runnable iff it is a
        // real map hex that has not been physically visited and is not scout-hard-blocked.
        public static bool IsExploreFocusRunnable(WorldSnapshot snap, HexCoord focus)
        {
            MapKnowledgeSnapshot mk = snap?.MapKnowledge;
            if (mk?.AllHexes == null)
                return false;
            var onMap = mk.OnMapSet();
            if (!onMap.Contains(focus))
                return false;
            if (mk.VisitedHexSet != null && mk.VisitedHexSet.Contains(focus))
                return false;
            if (mk.IsBlockedForScout(focus, stealthCapable: false))
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
            var onMap = mk.OnMapSet();
            if (!onMap.Contains(focus))
                return false;
            return !mk.IsBlockedForScout(focus, stealthCapable: false);
        }

        // An Attack observation need (Recon audit B2) is looked at from a vantage, so only the hard
        // gates apply: a real map hex a ground scout can enter. The visible-arrival block
        // (the site's own defenders / takeover) is exactly what the vantage avoids.
        public static bool IsAttackObservationFocusRunnable(WorldSnapshot snap, HexCoord focus)
        {
            MapKnowledgeSnapshot mk = snap?.MapKnowledge;
            if (mk?.AllHexes == null)
                return false;
            var onMap = mk.OnMapSet();
            return onMap.Contains(focus) && !mk.IsBlockedForScout(focus, stealthCapable: true);
        }

        public static int ExploreStillOpen(WorldSnapshot snap, HexCoord focus)
        {
            if (!IsExploreFocusRunnable(snap, focus))
                return 0;

            MapKnowledgeSnapshot mk = snap.MapKnowledge;
            var onMap = mk.OnMapSet();
            int fresh = 0;
            foreach (HexCoord n in HexGridMath.Neighbors(focus))
            {
                if (!onMap.Contains(n))
                    continue;
                if (mk.VisitedHexSet != null && mk.VisitedHexSet.Contains(n))
                    continue;
                if (mk.IsBlockedForScout(n, stealthCapable: false))
                    continue;
                fresh++;
            }
            return fresh;
        }

        // Frozen counterpart of the live step's home coverage fact. Dangerous/arrival-blocked
        // cells are not outstanding local work for an ordinary ground scout.
        internal static float LocalExplorationGap(WorldSnapshot snap)
        {
            MapKnowledgeSnapshot mk = snap?.MapKnowledge;
            if (mk?.AllHexes == null || snap.Self == null) return 0f;
            var homes = (snap.Self.BaseHexes ?? new List<HexCoord>())
                .Concat(new[] { snap.Self.Citadel }).Distinct().ToList();
            int local = 0, open = 0;
            foreach (HexCoord h in mk.AllHexes)
            {
                if (mk.IsBlockedForScout(h, stealthCapable: false)
                    || homes.Min(b => HexGridMath.Distance(b, h))
                        > AiConfigV2.scoutStepHomeLocalRingRadius) continue;
                local++;
                if (mk.VisitedHexSet?.Contains(h) != true) open++;
            }
            return local > 0 ? open / (float)local : 0f;
        }
    }
}
