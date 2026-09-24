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

        // Continuous [0..1] generic Refresh pressure across actually-observed map information.
        // Never-observed hexes are absent by construction and therefore cannot masquerade as stale.
        public static float StalePressure(WorldSnapshot snapshot)
        {
            IReadOnlyDictionary<HexCoord, int> observed = LastObservedFor(snapshot);
            if (observed.Count == 0)
                return 0f;

            float sum = 0f;
            int count = 0;
            foreach (KeyValuePair<HexCoord, int> kv in observed)
            {
                int age = Mathf.Max(0, snapshot.TurnNumber - kv.Value);
                float stale = Mathf.InverseLerp(AiConfigV2.scoutSurveilStaleTurnsLo,
                    AiConfigV2.scoutSurveilStaleTurnsHi, age);
                sum += stale;
                count++;
            }
            return count > 0 ? Mathf.Clamp01(sum / count) : 0f;
        }

        // Snapshot identity is explicit. A cache read must never infer its player key from
        // mutable/optional contents such as current armies or citadel coordinates.
        private static PlayerSetupData ResolvePlayer(WorldSnapshot snapshot) => snapshot?.Observer;
    }
}
