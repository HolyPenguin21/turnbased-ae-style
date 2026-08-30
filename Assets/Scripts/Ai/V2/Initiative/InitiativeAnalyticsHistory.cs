using System.Collections.Generic;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2.Initiative
{
    // One real AI turn's worth of initiative-relevant AP telemetry, recorded at the end of that
    // turn. The record deliberately stays descriptive; the pressure reader below decides which
    // observations are strong enough to count as AP starvation.
    public readonly struct InitiativeTurnRecord
    {
        public readonly int InitiativeBaseAp;
        public readonly int TotalStartAp;
        public readonly int ApSpent;
        public readonly int EndAp;
        public readonly int ActionableArmyCountAtStart;
        public readonly int UnactivatedActionableArmyCountAtEnd;
        // Legacy diagnostic field retained so the existing recorder signature does not have to
        // reshape the whole V2 pipeline. Its authoritative meaning is now simply "real occupied
        // field-army work remained"; the old card-affordability boolean passed by the recorder is
        // intentionally ignored because it was not a reliable AP-starvation signal.
        public readonly bool HadPotentialApWorkAtEnd;

        public InitiativeTurnRecord(int initiativeBaseAp, int totalStartAp, int apSpent, int endAp,
            int actionableArmyCountAtStart, int unactivatedActionableArmyCountAtEnd, bool legacyCardOrArmyWorkFlag)
        {
            InitiativeBaseAp = initiativeBaseAp;
            TotalStartAp = totalStartAp;
            ApSpent = apSpent;
            EndAp = endAp;
            ActionableArmyCountAtStart = actionableArmyCountAtStart;
            UnactivatedActionableArmyCountAtEnd = unactivatedActionableArmyCountAtEnd;
            HadPotentialApWorkAtEnd = unactivatedActionableArmyCountAtEnd > 0;
        }
    }

    // Per-player bounded ring buffer used ONLY by the pre-turn Initiative module. It never feeds
    // WorldAnalysis / Radar / DemandLayer / MissionLayer / ResourceAllocator / StrategicManager /
    // TaskExecutor / HousekeepingManager.
    public static class InitiativeAnalyticsHistory
    {
        private static readonly Dictionary<PlayerSetupData, List<InitiativeTurnRecord>> ByPlayer =
            new Dictionary<PlayerSetupData, List<InitiativeTurnRecord>>();

        public static void Clear() => ByPlayer.Clear();

        public static void Record(PlayerSetupData player, InitiativeTurnRecord record)
        {
            if (player == null)
                return;
            if (!ByPlayer.TryGetValue(player, out List<InitiativeTurnRecord> list))
                ByPlayer[player] = list = new List<InitiativeTurnRecord>();
            list.Add(record);
            int max = Mathf.Max(1, AiConfigV2.initiativeHistoryMaxSamples);
            if (list.Count > max)
                list.RemoveRange(0, list.Count - max);
        }

        public static IReadOnlyList<InitiativeTurnRecord> For(PlayerSetupData player) =>
            player != null && ByPlayer.TryGetValue(player, out List<InitiativeTurnRecord> list)
                ? (IReadOnlyList<InitiativeTurnRecord>)list
                : System.Array.Empty<InitiativeTurnRecord>();

        // Returns false when no history exists. That distinction matters: "no evidence yet" must
        // not inject an arbitrary 0.5 pressure into a player with little current AP workload.
        //
        // Historical starvation is intentionally conservative. The old recorder's card flag is
        // not strong enough evidence by itself: a card left in hand can be unplayed for placement,
        // resource or strategic reasons even when AP was available. An occupied field army that
        // still never activated is a much cleaner generic AP-capacity signal, so only that signal
        // can create historical pressure here.
        public static bool TryHistoricalApPressure(PlayerSetupData player, out float pressure)
        {
            IReadOnlyList<InitiativeTurnRecord> list = For(player);
            if (list.Count == 0)
            {
                pressure = 0f;
                return false;
            }

            int starveAp = Mathf.Max(0, AiConfigV2.initiativeStarvationApThreshold);
            float acc = 0f;
            float wsum = 0f;

            for (int i = 0; i < list.Count; i++)
            {
                InitiativeTurnRecord r = list[i];
                bool armyWorkRemained = r.UnactivatedActionableArmyCountAtEnd > 0;
                float leftoverFrac = r.TotalStartAp > 0
                    ? Mathf.Clamp01((float)r.EndAp / r.TotalStartAp)
                    : 0f;

                float perTurn = 0f;
                if (armyWorkRemained)
                {
                    if (r.EndAp <= starveAp)
                        perTurn = 1f; // strong evidence: useful activations remained at an AP floor
                    else
                        perTurn = 0.5f * Mathf.Clamp01(1f - leftoverFrac); // weaker, ambiguous evidence
                }
                // No real work remained => zero starvation evidence, even if EndAp happened to be
                // exactly zero. Spending the whole budget efficiently is not proof that more AP
                // would have produced another useful action.

                float w = i + 1f; // recent turns weighted more heavily
                acc += perTurn * w;
                wsum += w;
            }

            pressure = wsum > 0f ? Mathf.Clamp01(acc / wsum) : 0f;
            return true;
        }
    }
}
