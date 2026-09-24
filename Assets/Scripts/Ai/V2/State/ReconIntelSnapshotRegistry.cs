using System.Collections.Generic;
using Game.HexGrid;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // Strategic view of AiReconIntelMemory: the copy every objective/desire/continuity reader uses,
    // so nothing strategic reads the live tactical memory directly.
    //
    // Captured at the WorldAnalysis observation seam — which runs at Scan AND at every
    // RefreshStrategicKnowledge whose KnowledgeVersion moved, i.e. potentially several times inside
    // one turn. This is deliberate and load-bearing, NOT an oversight: the readers below depend on
    // it. ReconObjectiveEvaluator.RefreshAt / BuildRefreshObjectives drop a hex whose intel age fell
    // under scoutSurveilStaleTurnsLo, and ScoutObjectiveEvaluator decides a durable Refresh intent
    // is satisfied from the same age — so a scout that just re-observed its target hex must be
    // visible here in the SAME turn, or the AI would keep re-proposing a job it already completed.
    //
    // The copy is therefore "per knowledge revision", not "per turn". What it does guarantee is
    // isolation: strategic readers never observe a half-updated tactical store, and every read is
    // gated on Turn == snapshot.TurnNumber so a previous turn's copy can never be served.
    internal static class ReconIntelSnapshotRegistry
    {
        private sealed class Entry
        {
            public int Turn;
            public IReadOnlyDictionary<HexCoord, int> LastObserved;
        }

        private static readonly Dictionary<PlayerSetupData, Entry> ByPlayer =
            new Dictionary<PlayerSetupData, Entry>();

        public static void Clear() => ByPlayer.Clear();

        public static void Capture(PlayerSetupData player, int turn,
            IReadOnlyDictionary<HexCoord, int> lastObserved)
        {
            if (player == null)
                return;
            ByPlayer[player] = new Entry
            {
                Turn = turn,
                LastObserved = lastObserved != null
                    ? new Dictionary<HexCoord, int>(lastObserved)
                    : new Dictionary<HexCoord, int>(),
            };
        }

        public static bool TryGetLastObservedTurn(WorldSnapshot snapshot, HexCoord hex, out int lastObservedTurn)
        {
            lastObservedTurn = 0;
            PlayerSetupData player = ResolvePlayer(snapshot);
            return player != null
                && ByPlayer.TryGetValue(player, out Entry e)
                && e.Turn == snapshot.TurnNumber
                && e.LastObserved != null
                && e.LastObserved.TryGetValue(hex, out lastObservedTurn);
        }

        public static bool TryGetIntelAge(WorldSnapshot snapshot, HexCoord hex, out int age)
        {
            age = 0;
            if (!TryGetLastObservedTurn(snapshot, hex, out int observed))
                return false;
            age = Mathf.Max(0, snapshot.TurnNumber - observed);
            return true;
        }

        public static IReadOnlyDictionary<HexCoord, int> LastObservedFor(WorldSnapshot snapshot)
        {
            PlayerSetupData player = ResolvePlayer(snapshot);
            if (player == null || !ByPlayer.TryGetValue(player, out Entry e)
                || e.Turn != snapshot.TurnNumber || e.LastObserved == null)
                return new Dictionary<HexCoord, int>();
            return e.LastObserved;
        }

        // Continuous [0..1] generic Refresh pressure across actually-observed map information,
        // weighted by what that information is worth. An unweighted mean let every empty hex ever
        // seen count the same as an enemy Base, so the pressure only grew with game time. Weight =
        // reconRefreshPressureFloorWeight + RefreshRelevance: plain terrain still counts a little,
        // buildings / resource sites / event guards dominate. Never-observed hexes are absent by
        // construction and therefore cannot masquerade as stale. Read by Desire (RefreshPressure),
        // ReconConcurrencyPolicy (refresh lane) and the ground executor's mode score alike.
        public static float StalePressure(WorldSnapshot snapshot)
        {
            IReadOnlyDictionary<HexCoord, int> observed = LastObservedFor(snapshot);
            if (observed.Count == 0)
                return 0f;

            float floor = Mathf.Max(0f, AiConfigV2.reconRefreshPressureFloorWeight);
            float sum = 0f;
            float weight = 0f;
            foreach (KeyValuePair<HexCoord, int> kv in observed)
            {
                int age = Mathf.Max(0, snapshot.TurnNumber - kv.Value);
                float stale = Mathf.InverseLerp(AiConfigV2.scoutSurveilStaleTurnsLo,
                    AiConfigV2.scoutSurveilStaleTurnsHi, age);
                float w = floor + RefreshRelevance(snapshot, kv.Key);
                sum += stale * w;
                weight += w;
            }
            return weight > 0f ? Mathf.Clamp01(sum / weight) : 0f;
        }

        // [0..1] strategic relevance of re-observing `hex`: a remembered building (citadel highest),
        // a known resource site or a known guarded Hex Event on it, or next to it. The one owner —
        // Refresh objectives (ReconObjectiveEvaluator.BuildRefresh) and StalePressure above both
        // read it, so the objective ranking and the lane/desire pressure cannot disagree.
        public static float RefreshRelevance(WorldSnapshot snap, HexCoord hex)
        {
            float relevance = 0f;
            if (snap?.Known == null)
                return relevance;
            if (snap.Known.Buildings != null)
                foreach (AiMapMemory.KnownBuilding b in snap.Known.Buildings)
                {
                    int d = HexGridMath.Distance(b.Hex, hex);
                    if (d == 0) relevance = Mathf.Max(relevance, b.IsStartingCitadel ? 1f : 0.85f);
                    else if (d == 1) relevance = Mathf.Max(relevance, 0.50f);
                }

            if (snap.Known.ResourceHexes != null)
                foreach (AiMapMemory.KnownResourceHex r in snap.Known.ResourceHexes)
                {
                    int d = HexGridMath.Distance(r.Hex, hex);
                    if (d == 0) relevance = Mathf.Max(relevance, 0.75f);
                    else if (d == 1) relevance = Mathf.Max(relevance, 0.40f);
                }

            if (snap.Known.EventGuardHexes != null)
                foreach (HexCoord e in snap.Known.EventGuardHexes)
                {
                    int d = HexGridMath.Distance(e, hex);
                    if (d == 0) relevance = Mathf.Max(relevance, 0.80f);
                    else if (d == 1) relevance = Mathf.Max(relevance, 0.45f);
                }
            return relevance;
        }

        // Snapshot identity is explicit. A cache read must never infer its player key from
        // mutable/optional contents such as current armies or citadel coordinates.
        private static PlayerSetupData ResolvePlayer(WorldSnapshot snapshot) => snapshot?.Observer;
    }
}
