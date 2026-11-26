using System.Collections.Generic;
using System.Linq;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  V2 TURN ACTIVITY TELEMETRY  (Strategy V2)
    // ===========================================================================================
    //  One per-player, per-turn record of WHAT the AI actually did. The pipeline (Pipeline.RunTurn)
    //  writes it once at the end of the turn; the capability-pool registry and the infrastructure
    //  executor add their own events.
    //
    //  Counters are deliberately DERIVED from facts the pipeline already produced (demand lists,
    //  funded/provisioned lists, ExecutionResult stop reasons, StrategicPhaseResult.CardsPlayed)
    //  rather than incremented ad hoc at scattered call sites, so a miscount is a wiring bug in
    //  one place, not a drift across ten.
    // ===========================================================================================
    internal sealed class V2TurnActivity
    {
        public int DemandsRaised;
        public int MissionsConsidered;
        public int MissionsFunded;
        public int Provisioned;
        public readonly Dictionary<ProvisionFailureKind, int> ProvisionFailures =
            new Dictionary<ProvisionFailureKind, int>();
        public int ExecutionAttempts;
        public int ExecutionsSucceeded;
        public int ExecutionsStaleOrSkipped;   // revalidated-away / 0 AP & 0 steps
        public int CardsPlayed;
        public int CardsDrawn;
        public int CapabilityDeliveries;
        public int ExhaustionEvents;           // a capability pool proven pool-wide unable this scope
        public int PoolRecoveries;             // an exhausted pool revalidated as usable again

        // Production telemetry (spec §12).
        public int MaterializationAttempts;
        public int MaterializationsSucceeded;
        public int GeneratedCardAttempts;
        public int GeneratedCardsSucceeded;
        public int EquipmentAssignmentAttempts;
        public int EquipmentAssignmentsSucceeded;
        public int InfrastructureAttempts;
        public int InfrastructureBuilt;

        public void RecordProvisionFailure(ProvisionFailureKind kind)
        {
            if (kind == ProvisionFailureKind.None)
                return;
            ProvisionFailures.TryGetValue(kind, out int current);
            ProvisionFailures[kind] = current + 1;
        }

        private string ProvisionFailureLine() =>
            ProvisionFailures.Count == 0 ? "none" : string.Join(",",
                ProvisionFailures.OrderBy(kv => (int)kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));

        public string Line() =>
            $"demands {DemandsRaised}, missions {MissionsConsidered}, funded {MissionsFunded}, "
            + $"provisioned {Provisioned}, provFail {ProvisionFailures.Values.Sum()} [{ProvisionFailureLine()}], "
            + $"execAttempts {ExecutionAttempts}, execOk {ExecutionsSucceeded}, "
            + $"execStale {ExecutionsStaleOrSkipped}, "
            + $"cards {CardsPlayed}, draws {CardsDrawn}, capDeliveries {CapabilityDeliveries}, "
            + $"exhaustion {ExhaustionEvents}, poolRecov {PoolRecoveries}, "
            + $"mat {MaterializationsSucceeded}/{MaterializationAttempts}, "
            + $"genCard {GeneratedCardsSucceeded}/{GeneratedCardAttempts}, "
            + $"equip {EquipmentAssignmentsSucceeded}/{EquipmentAssignmentAttempts}, "
            + $"infra {InfrastructureBuilt}/{InfrastructureAttempts}";
    }

    internal static class V2TurnActivityTelemetry
    {
        private static readonly TurnScopedByPlayer<V2TurnActivity> ByPlayer =
            new TurnScopedByPlayer<V2TurnActivity>(() => new V2TurnActivity());

        // Called once when the turn session opens: a fresh record every turn.
        public static void Begin(PlayerSetupData player, int turn)
        {
            if (player == null) return;
            ByPlayer.Reset(player, turn);
        }

        public static V2TurnActivity For(PlayerSetupData player, int turn) =>
            player == null ? new V2TurnActivity() : ByPlayer.GetOrReset(player, turn);

        public static void LogSummary(PlayerSetupData player, int turn)
        {
            if (player == null) return;
            if (!(ByPlayer.Peek(player, turn) is V2TurnActivity a))
                return;
            AiDebugLog.Write($"[AI][V2][TURN-END] {player.Nickname}: {a.Line()}");
            ByPlayer.Remove(player, turn);
        }

        public static void Clear() => ByPlayer.Clear();
    }
}
