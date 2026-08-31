using System.Collections.Generic;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  CAPABILITY POOL EXHAUSTION REGISTRY  (Strategy V2)
    // ===========================================================================================
    //  The GENERAL contract behind the earlier Scout-only special case
    //  (StrategicReactionPass.IsExhaustedScoutPoolFailure): once the WHOLE relevant capability pool
    //  is PROVEN unable to execute a demand/mission in the current allocation scope, the bounded
    //  pack -> provision -> re-pack loop must stop handing the same pool the same work.
    //
    //  INDIVIDUAL failure  (one actor/card/army could not) is NOT exhaustion — the re-pack must
    //  still get a chance to route the work to a different member of the pool.
    //  POOL-WIDE failure   (every eligible candidate in the snapshot, ignoring this cycle's own
    //  tentative claims, was proven unable) is exhaustion.
    //
    //  SCOPE is one allocation epoch: the main pipeline turn, or one bounded reaction round. A new
    //  turn (BeginTurn) or a new reaction round (BeginRound) is a fresh scope — capability may have
    //  been delivered by Strategic Manager Phase A in between, an actor may have freed up, or the
    //  world may have moved, so exhaustion never survives a scope boundary. There is no other
    //  reset and no cross-turn persistence: deterministic and bounded by construction.
    // ===========================================================================================
    internal enum CapabilityPoolKind
    {
        None,
        Scout,          // any solo Recce able to run a Scout mission
        StealthScout,   // the stealth-capable subset
        FieldCombat,    // ready ground field power able to execute a Raid this cycle
        Hero,           // a free deployed hero able to lead
    }

    internal static class CapabilityPoolExhaustionRegistry
    {
        private sealed class Scope
        {
            public int Turn = -1;
            public int Round;                       // 0 == main pipeline, >=1 == reaction round
            public readonly Dictionary<CapabilityPoolKind, string> Exhausted =
                new Dictionary<CapabilityPoolKind, string>();
        }

        private static readonly Dictionary<PlayerSetupData, Scope> ByPlayer =
            new Dictionary<PlayerSetupData, Scope>();

        private static Scope Get(PlayerSetupData player)
        {
            if (player == null) return new Scope();
            if (!ByPlayer.TryGetValue(player, out Scope s))
                ByPlayer[player] = s = new Scope();
            return s;
        }

        public static void BeginTurn(PlayerSetupData player, int turn)
        {
            Scope s = Get(player);
            s.Turn = turn;
            s.Round = 0;
            s.Exhausted.Clear();
        }

        // A bounded reaction round is a fresh scope — Phase A inside the round may have materialised
        // new capability, so anything marked exhausted in the main pass gets another chance.
        public static void BeginRound(PlayerSetupData player, int turn, int round)
        {
            Scope s = Get(player);
            s.Turn = turn;
            s.Round = round;
            s.Exhausted.Clear();
        }

        public static bool IsExhausted(PlayerSetupData player, CapabilityPoolKind pool)
        {
            if (player == null || pool == CapabilityPoolKind.None) return false;
            return Get(player).Exhausted.ContainsKey(pool);
        }

        public static void MarkExhausted(PlayerSetupData player, CapabilityPoolKind pool, string reason)
        {
            if (player == null || pool == CapabilityPoolKind.None) return;
            Scope s = Get(player);
            if (!s.Exhausted.ContainsKey(pool))
            {
                s.Exhausted[pool] = reason ?? "pool-wide failure";
                AiDebugLog.Write($"[AI][V2] capability pool exhausted — {pool} this "
                    + $"{(s.Round == 0 ? "turn" : "reaction round " + s.Round)}: {s.Exhausted[pool]}");
                V2Phase phase = s.Round == 0 ? V2Phase.Main : V2Phase.Reaction;
                V2TurnActivityTelemetry.Phase(player, s.Turn, phase).ExhaustionEvents++;
            }
        }

        // Which pool a mission draws on, for the IsExhausted gate in the pack loop.
        public static CapabilityPoolKind PoolFor(MissionProposal mission)
        {
            if (mission == null) return CapabilityPoolKind.None;
            if (mission.Kind == MissionKind.Scout && mission.Target is ScoutMissionTarget st)
                return st.Stealth == StealthRequirement.Required || st.DetectionRisk > 0f
                    ? CapabilityPoolKind.StealthScout : CapabilityPoolKind.Scout;
            if (mission.Kind == MissionKind.Raid)
                return CapabilityPoolKind.FieldCombat;
            return CapabilityPoolKind.None;
        }

        // The POOL-WIDE proof. Generalises IsExhaustedScoutPoolFailure: a contention/shortage-class
        // failure PLUS a snapshot in which the pool's own eligibility rule finds zero candidates
        // when this cycle's tentative claims are ignored. A genuinely-transient contention (a
        // second capable actor really exists) returns false so the normal re-pack fallback lives.
        public static bool ProvenPoolWideUnable(WorldSnapshot snap, PlayerSetupData player,
            MissionProposal mission, ProvisionFailure failure)
        {
            if (snap == null || mission == null)
                return false;
            bool contention = failure.Kind == ProvisionFailureKind.MoverContended
                || failure.Kind == ProvisionFailureKind.NoMoverExists;
            if (!contention)
                return false;

            switch (mission.Kind)
            {
                case MissionKind.Scout:
                    if (!(mission.Target is ScoutMissionTarget target))
                        return false;
                    // Ignore ProvisioningSession claims on purpose — changing the mission key
                    // cannot conjure another eligible ready scout this cycle.
                    return ScoutMoverSelector.Eligible(snap, target, null).Count == 0;

                case MissionKind.Raid:
                {
                    CapabilityInventory inv = CapabilityInventory.Build(snap, player, null);
                    // No unclaimed ready field power AND no free hero anywhere -> the raid pool
                    // itself is empty this cycle, not merely contended.
                    return inv.RaidAvailableFieldPower <= AiConfigV2.allocatorSliceEpsilon
                        && inv.AvailableHeroes <= 0;
                }
                default:
                    return false;
            }
        }

        public static void Clear() => ByPlayer.Clear();
    }
}
