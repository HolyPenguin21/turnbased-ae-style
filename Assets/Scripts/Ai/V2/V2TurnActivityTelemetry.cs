using System.Collections.Generic;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  V2 TURN ACTIVITY TELEMETRY  (Strategy V2)
    // ===========================================================================================
    //  One per-player, per-turn record of WHAT the AI actually did and IN WHICH PHASE. The main
    //  pipeline (Pipeline.RunTurn) writes the Main bucket; StrategicReactionPass writes the
    //  Reaction bucket (additively across its bounded rounds). Total is Main + Reaction with NO
    //  double counting — each source writes its own bucket exactly once per phase.
    //
    //  Counters are deliberately DERIVED from facts the pipeline already produced (demand lists,
    //  funded/provisioned lists, ExecutionResult stop reasons, StrategicPhaseResult.CardsPlayed)
    //  rather than incremented ad hoc at scattered call sites, so a miscount is a wiring bug in
    //  one place, not a drift across ten.
    // ===========================================================================================
    internal enum V2Phase { Main, Reaction }

    internal sealed class V2PhaseActivity
    {
        public int DemandsRaised;
        public int MissionsConsidered;
        public int MissionsFunded;
        public int Provisioned;
        public int ExecutionAttempts;
        public int ExecutionsSucceeded;
        public int ExecutionsStaleOrSkipped;   // revalidated-away or completed with 0 AP / 0 steps
        public int ReplacementMissions;        // stale provisioned mission handed to a live replacement
        public int CardsPlayed;
        public int CardsDrawn;
        public int CapabilityDeliveries;
        public int ExhaustionEvents;           // a capability pool proven pool-wide unable this scope
        public int InfrastructureBuilt;

        public void Add(V2PhaseActivity o)
        {
            if (o == null) return;
            DemandsRaised += o.DemandsRaised;
            MissionsConsidered += o.MissionsConsidered;
            MissionsFunded += o.MissionsFunded;
            Provisioned += o.Provisioned;
            ExecutionAttempts += o.ExecutionAttempts;
            ExecutionsSucceeded += o.ExecutionsSucceeded;
            ExecutionsStaleOrSkipped += o.ExecutionsStaleOrSkipped;
            ReplacementMissions += o.ReplacementMissions;
            CardsPlayed += o.CardsPlayed;
            CardsDrawn += o.CardsDrawn;
            CapabilityDeliveries += o.CapabilityDeliveries;
            ExhaustionEvents += o.ExhaustionEvents;
            InfrastructureBuilt += o.InfrastructureBuilt;
        }

        public string Line() =>
            $"demands {DemandsRaised}, missions {MissionsConsidered}, funded {MissionsFunded}, "
            + $"provisioned {Provisioned}, execAttempts {ExecutionAttempts}, execOk {ExecutionsSucceeded}, "
            + $"execStale {ExecutionsStaleOrSkipped}, replacements {ReplacementMissions}, "
            + $"cards {CardsPlayed}, draws {CardsDrawn}, capDeliveries {CapabilityDeliveries}, "
            + $"exhaustion {ExhaustionEvents}, infra {InfrastructureBuilt}";
    }

    internal sealed class V2TurnActivity
    {
        public int Turn;
        public readonly V2PhaseActivity Main = new V2PhaseActivity();
        public readonly V2PhaseActivity Reaction = new V2PhaseActivity();

        public V2PhaseActivity Total()
        {
            var t = new V2PhaseActivity();
            t.Add(Main);
            t.Add(Reaction);
            return t;
        }
    }

    internal static class V2TurnActivityTelemetry
    {
        private static readonly Dictionary<PlayerSetupData, V2TurnActivity> ByPlayer =
            new Dictionary<PlayerSetupData, V2TurnActivity>();

        // Called once at the very top of Pipeline.RunTurn. A fresh record every turn so a stale
        // Reaction bucket from last turn can never bleed into this turn's Total.
        public static void Begin(PlayerSetupData player, int turn)
        {
            if (player == null) return;
            ByPlayer[player] = new V2TurnActivity { Turn = turn };
        }

        public static V2PhaseActivity Phase(PlayerSetupData player, int turn, V2Phase phase)
        {
            if (player == null) return new V2PhaseActivity();
            if (!ByPlayer.TryGetValue(player, out V2TurnActivity a) || a.Turn != turn)
                ByPlayer[player] = a = new V2TurnActivity { Turn = turn };
            return phase == V2Phase.Reaction ? a.Reaction : a.Main;
        }

        public static void LogSummary(PlayerSetupData player, int turn)
        {
            if (player == null) return;
            if (!ByPlayer.TryGetValue(player, out V2TurnActivity a) || a.Turn != turn)
                return;
            V2PhaseActivity total = a.Total();
            AiDebugLog.Write($"[AI][V2] {player.Nickname}: activity main    — {a.Main.Line()}");
            AiDebugLog.Write($"[AI][V2] {player.Nickname}: activity reaction — {a.Reaction.Line()}");
            AiDebugLog.Write($"[AI][V2] {player.Nickname}: activity total    — {total.Line()}");
            ByPlayer.Remove(player);
        }

        public static void Clear() => ByPlayer.Clear();
    }
}
