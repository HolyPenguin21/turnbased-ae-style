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
    internal readonly struct ResourceBlockEvidence
    {
        public readonly float Required;
        public readonly float AvailableAtBlock;
        public readonly float IncomePerTurn;
        public readonly float DemandValue;
        public readonly int Turn;

        public ResourceBlockEvidence(float required, float availableAtBlock, float incomePerTurn,
            float demandValue, int turn)
        {
            Required = Mathf.Max(0f, required);
            AvailableAtBlock = Mathf.Max(0f, availableAtBlock);
            IncomePerTurn = Mathf.Max(0f, incomePerTurn);
            DemandValue = Mathf.Max(0f, demandValue);
            Turn = turn;
        }
    }

    internal static class ResourceStarvationRegistry
    {
        private sealed class State
        {
            public readonly Dictionary<ResourceType, float> Pressure = new Dictionary<ResourceType, float>();
            public readonly Dictionary<ResourceType, ResourceBlockEvidence> CurrentBlocks =
                new Dictionary<ResourceType, ResourceBlockEvidence>();
            public readonly HashSet<ResourceType> VerifiedPending = new HashSet<ResourceType>();
            // Production diagnostics enable strict evidence mode. Standalone pure tests that call
            // RecordBlock directly keep the original simple semantics unless they explicitly enter
            // this mode, so the existing S26 harness remains useful and backward compatible.
            public bool RequireVerifiedEvidence;
            public int LastDecayTurn = int.MinValue;
        }

        private static readonly Dictionary<PlayerSetupData, State> ByPlayer =
            new Dictionary<PlayerSetupData, State>();

        public static void Clear() => ByPlayer.Clear();

        public static void BeginVerifiedPass(PlayerSetupData player)
        {
            if (player == null)
                return;
            State s = Get(player);
            s.RequireVerifiedEvidence = true;
            s.VerifiedPending.Clear();
        }

        // Called only by the no-chain diagnostic after it has inspected a matching card/chain and
        // found a concrete resource deficit. The subsequent RecordBlock consumes this evidence.
        public static void VerifyBlock(PlayerSetupData player, ResourceType type)
        {
            if (player == null)
                return;
            State s = Get(player);
            s.RequireVerifiedEvidence = true;
            s.VerifiedPending.Add(type);
        }

        public static void RecordBlock(PlayerSetupData player, ResourceType type)
        {
            if (player == null)
                return;
            State s = Get(player);
            // During real Strategy-V2 diagnostics, ignore the old broad StrategicManager
            // "stock == 0" calls unless the diagnostic has armed this exact resource. Outside
            // verified mode (pure harnesses / isolated callers), retain the original API behaviour.
            if (s.RequireVerifiedEvidence && !s.VerifiedPending.Remove(type))
                return;
            AddPressure(s, type);
        }

        // Exact current-turn evidence used by StrategicCardEvaluator to price the marginal value of
        // NOT consuming stock that an unresolved AGG/RCN capability is trying to accumulate.
        // Diagnostics remains the only producer; this registry records facts and does not score cards.
        public static void RecordVerifiedBlock(PlayerSetupData player, ResourceType type, float required,
            float available, float incomePerTurn, float demandValue, int turn)
        {
            if (player == null || required <= available)
                return;
            State s = Get(player);
            s.RequireVerifiedEvidence = true;
            s.VerifiedPending.Remove(type);
            AddPressure(s, type);

            var next = new ResourceBlockEvidence(required, available, incomePerTurn, demandValue, turn);
            if (s.CurrentBlocks.TryGetValue(type, out ResourceBlockEvidence current)
                && current.Turn == turn
                && (current.DemandValue > next.DemandValue
                    || (Mathf.Approximately(current.DemandValue, next.DemandValue)
                        && current.Required <= next.Required)))
                return;
            s.CurrentBlocks[type] = next;
        }

        public static bool TryGetCurrentBlock(PlayerSetupData player, ResourceType type, int turn,
            out ResourceBlockEvidence evidence)
        {
            evidence = default;
            return player != null
                && ByPlayer.TryGetValue(player, out State s)
                && s.CurrentBlocks.TryGetValue(type, out evidence)
                && evidence.Turn == turn;
        }

        private static void AddPressure(State s, ResourceType type)
        {
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
            s.CurrentBlocks.Clear();
            // Strict mode is re-enabled by the first real diagnostic of the new turn. Clearing it
            // here keeps isolated direct RecordBlock tests/callers backward compatible.
            s.RequireVerifiedEvidence = false;
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
