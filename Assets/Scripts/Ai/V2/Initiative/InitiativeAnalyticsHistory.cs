using System.Collections.Generic;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2.Initiative
{
    // One real AI turn's worth of initiative-relevant AP telemetry, recorded at the end of that
    // turn. Its whole point is to tell genuine AP starvation ("I still had armies to move / cards
    // to play and ran out of AP") apart from a turn that simply had nothing useful left to do
    // ("ended at 0 AP because there was nothing worth spending it on"). Zero remaining AP is only
    // strong evidence of a need for more AP when HadPotentialApWorkAtEnd is true.
    public readonly struct InitiativeTurnRecord
    {
        public readonly int InitiativeBaseAp;                     // AP purely from the initiative rank
        public readonly int TotalStartAp;                         // AP the turn actually started with (rank + bonuses)
        public readonly int ApSpent;
        public readonly int EndAp;
        public readonly int ActionableArmyCountAtStart;
        public readonly int UnactivatedActionableArmyCountAtEnd;
        public readonly bool HadPotentialApWorkAtEnd;

        public InitiativeTurnRecord(int initiativeBaseAp, int totalStartAp, int apSpent, int endAp,
            int actionableArmyCountAtStart, int unactivatedActionableArmyCountAtEnd, bool hadPotentialApWorkAtEnd)
        {
            InitiativeBaseAp = initiativeBaseAp;
            TotalStartAp = totalStartAp;
            ApSpent = apSpent;
            EndAp = endAp;
            ActionableArmyCountAtStart = actionableArmyCountAtStart;
            UnactivatedActionableArmyCountAtEnd = unactivatedActionableArmyCountAtEnd;
            HadPotentialApWorkAtEnd = hadPotentialApWorkAtEnd;
        }
    }

    // Per-player bounded ring buffer of InitiativeTurnRecords. Belongs EXCLUSIVELY to initiative
    // analysis — nothing in it is ever read by WorldAnalysis / Radar / DemandLayer / MissionLayer
    // / ResourceAllocator / StrategicManager / TaskExecutor / HousekeepingManager.
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

        // Bounded [0..1] recent-history AP pressure. 0.5 == neutral / no history.
        //   ~1  : recent turns repeatedly ran out of AP with real work still queued.
        //   ~0  : recent turns repeatedly ended with substantial unused AP and nothing to do.
        // Recent turns are weighted more heavily (linear ramp).
        public static float HistoricalApPressure(PlayerSetupData player)
        {
            IReadOnlyList<InitiativeTurnRecord> list = For(player);
            if (list.Count == 0)
                return 0.5f;

            int starveAp = Mathf.Max(0, AiConfigV2.initiativeStarvationApThreshold);
            float wasteFrac = Mathf.Clamp01(AiConfigV2.initiativeWasteLeftoverFrac);

            float acc = 0f, wsum = 0f;
            for (int i = 0; i < list.Count; i++)
            {
                InitiativeTurnRecord r = list[i];
                float leftoverFrac = r.TotalStartAp > 0 ? (float)r.EndAp / r.TotalStartAp : 0f;

                float perTurn;
                if (r.HadPotentialApWorkAtEnd && r.EndAp <= starveAp)
                    perTurn = 1f; // wanted to act, no AP left
                else if (!r.HadPotentialApWorkAtEnd && leftoverFrac >= wasteFrac)
                    perTurn = 0f; // nothing to do AND AP to spare
                else
                    perTurn = Mathf.Clamp01(1f - leftoverFrac); // spent most of it, minor slack

                float w = i + 1f; // most recent sample weighted highest
                acc += perTurn * w;
                wsum += w;
            }
            return wsum > 0f ? Mathf.Clamp01(acc / wsum) : 0.5f;
        }
    }
}
