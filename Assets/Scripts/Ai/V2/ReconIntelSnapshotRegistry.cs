using System.Collections.Generic;
using Game.HexGrid;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // Frozen strategic view of AiReconIntelMemory. Captured at the WorldAnalysis observation seam
    // once per player/turn, before any downstream manager can mutate the world. Mission/evaluator
    // code reads this copy only; live AiReconIntelMemory remains reserved for tactical execution.
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

        // WorldSnapshot deliberately has no player identity field. Resolve the frozen owner from
        // honest self-state without inventing one: normally any own army gives the owner directly;
        // the citadel fallback keeps an eliminated/temporarily army-less player unambiguous.
        private static PlayerSetupData ResolvePlayer(WorldSnapshot snapshot)
        {
            if (snapshot?.Self == null)
                return null;

            if (snapshot.Self.Armies != null)
                foreach (ArmySnapshot army in snapshot.Self.Armies)
                    if (army?.Owner != null)
                        return army.Owner;

            HexCoord citadel = snapshot.Self.Citadel;
            foreach (KeyValuePair<PlayerSetupData, Entry> kv in ByPlayer)
            {
                PlayerSetupData p = kv.Key;
                if (p == null || kv.Value == null || kv.Value.Turn != snapshot.TurnNumber
                    || !p.CitadelHexQ.HasValue || !p.CitadelHexR.HasValue)
                    continue;
                if (p.CitadelHexQ.Value == citadel.Q && p.CitadelHexR.Value == citadel.R)
                    return p;
            }
            return null;
        }
    }
}
