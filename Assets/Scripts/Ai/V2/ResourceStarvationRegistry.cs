using System.Collections.Generic;
using Game.Economy;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  RESOURCE STARVATION REGISTRY  (Strategy V2 — spec §17, P2)
    // ===========================================================================================
    //  Bounded, decaying feedback: when important AGG/RCN strategic chains keep failing
    //  specifically because a required resource is unavailable, that resource's economic
    //  pressure rises a little, so the Economy layer temporarily values a KNOWN extraction site
    //  for it more.
    //
    //  Invariants (spec §17):
    //    · smooth / bounded          — EWMA clamped to [0, 1]
    //    · decays when the shortage stops — *starvationDecayPerTurn each turn, once per turn
    //    · no enemy / TrueWorld info  — only reads our own chain/resource diagnostics
    //    · never a global inflation   — one verified resource, one bounded Economy value bump
    // ===========================================================================================
    internal static class ResourceStarvationRegistry
    {
        private sealed class State
        {
            public readonly Dictionary<ResourceType, float> Pressure = new Dictionary<ResourceType, float>();
            // RecordBlock is intentionally gated. StrategicManager still calls it from its old
            // broad "stock == 0" loop, but only MaterializationDiagnostics may arm a resource
            // after proving that a matching chain actually required more of it than was available.
            public readonly HashSet<ResourceType> VerifiedPending = new HashSet<ResourceType>();
            public int LastDecayTurn = int.MinValue;
        }

        private static readonly Dictionary<PlayerSetupData, State> ByPlayer =
            new Dictionary<PlayerSetupData, State>();

        public static void Clear() => ByPlayer.Clear();

        // Called only by the no-chain diagnostic after it has inspected a matching card/chain and
        // found a concrete resource deficit. The subsequent RecordBlock consumes this evidence.
        public static void VerifyBlock(PlayerSetupData player, ResourceType type)
        {
            if (player == null)
                return;
            Get(player).VerifiedPending.Add(type);
        }

        public static void RecordBlock(PlayerSetupData player, ResourceType type)
        {
            if (player == null)
                return;
            State s = Get(player);
            // Ignore unverified callers. This makes the old broad StrategicManager zero-stock loop
            // harmless while preserving its call site until the larger orchestrator is next split.
            if (!s.VerifiedPending.Remove(type))
                return;
            s.Pressure.TryGetValue(type, out float cur);
            s.Pressure[type] = Mathf.Clamp01(cur + AiConfigV2.starvationHitGain);
        }

        public static void DecayOncePerTurn(PlayerSetupData player, int turn)
        {
            if (player == null)
                return;
            State s = Get(player);
            if (s.LastDecayTurn == turn)
                return;
            s.LastDecayTurn = turn;
            // Evidence is local to one diagnostic failure; never carry an armed resource across a
            // turn boundary where an unrelated zero-stock observation could consume it.
            s.VerifiedPending.Clear();
            var keys = new List<ResourceType>(s.Pressure.Keys);
            foreach (ResourceType k in keys)
            {
                float v = s.Pressure[k] * AiConfigV2.starvationDecayPerTurn;
                if (v < 0.02f)
                    s.Pressure.Remove(k);
                else
                    s.Pressure[k] = v;
            }
        }

        public static float Pressure(PlayerSetupData player, ResourceType type)
        {
            if (player == null || !ByPlayer.TryGetValue(player, out State s))
                return 0f;
            return s.Pressure.TryGetValue(type, out float v) ? v : 0f;
        }

        private static State Get(PlayerSetupData player)
        {
            if (!ByPlayer.TryGetValue(player, out State s))
                ByPlayer[player] = s = new State();
            return s;
        }
    }
}
