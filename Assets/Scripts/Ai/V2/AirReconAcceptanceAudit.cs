using System.Collections.Generic;
using Game.HexGrid;
using Game.Players;

namespace Game.Ai.V2
{
    // Runtime evidence for the Air-Recon V2 pass. No scenario is credited merely because code for
    // it exists: PASS/FAIL appears only after the live situation occurred; otherwise the end-of-turn
    // summary reports NOT_OBSERVED so playtest logs remain honest.
    internal static class AirReconAcceptanceAudit
    {
        private const string SafeSameTurnReturn = "air-safe-same-turn-return";
        private const string EmergencyReplan = "air-aa-emergency-replan";
        private const string VisitedInvariant = "air-never-marks-ground-visited";
        private const string IntelRefresh = "air-refreshes-intel-age";
        private const string DiminishingReturns = "air-diminishing-returns-cooldown";
        private const string OpportunityCost = "air-ap-energy-opportunity-cost";

        private static readonly string[] Scenarios =
        {
            SafeSameTurnReturn,
            EmergencyReplan,
            VisitedInvariant,
            IntelRefresh,
            DiminishingReturns,
            OpportunityCost,
        };

        private enum Status { NotObserved, Pass, Fail }

        private sealed class TurnAudit
        {
            public int Turn;
            public bool SummaryWritten;
            public readonly Dictionary<string, Status> StatusByScenario =
                new Dictionary<string, Status>();
        }

        private static readonly Dictionary<PlayerSetupData, TurnAudit> ByPlayer =
            new Dictionary<PlayerSetupData, TurnAudit>();

        public static void ClearAll() => ByPlayer.Clear();

        public static void RecordSelection(PlayerSetupData player, int turn, HexCoord focus,
            float informationValue, float opportunityCost, float netValue, int routeCost)
        {
            bool pass = informationValue > 0f && opportunityCost >= 0f && netValue > 0f
                && routeCost >= 0;
            Record(player, turn, OpportunityCost, pass,
                $"focus=({focus.Q},{focus.R}) info={informationValue:0.0} "
                + $"cost={opportunityCost:0.0} net={netValue:0.0} route={routeCost}");
        }

        public static void RecordCooldownSkip(PlayerSetupData player, int turn, HexCoord focus,
            int cooldownTurns)
        {
            Record(player, turn, DiminishingReturns, true,
                $"focus=({focus.Q},{focus.R}) cooldown={cooldownTurns}");
        }

        public static void RecordVisitedStep(PlayerSetupData player, int turn, HexCoord hex,
            bool visitedBefore, bool visitedAfter)
        {
            bool pass = visitedBefore || !visitedAfter;
            Record(player, turn, VisitedInvariant, pass,
                $"hex=({hex.Q},{hex.R}) visited={(visitedBefore ? 1 : 0)}->{(visitedAfter ? 1 : 0)}");
        }

        public static void RecordEmergencyReplan(PlayerSetupData player, int turn, HexCoord from,
            bool landingFound, HexCoord? landing)
        {
            string target = landing.HasValue ? $"({landing.Value.Q},{landing.Value.R})" : "none";
            Record(player, turn, EmergencyReplan, landingFound,
                $"from=({from.Q},{from.R}) landing={target}");
        }

        public static void RecordFinish(PlayerSetupData player, int turn, HexCoord focus,
            int steps, bool observed, bool landed, bool intelFresh)
        {
            if (steps > 0)
                Record(player, turn, SafeSameTurnReturn, landed,
                    $"focus=({focus.Q},{focus.R}) steps={steps} landed={(landed ? 1 : 0)}");

            if (observed)
                Record(player, turn, IntelRefresh, intelFresh,
                    $"focus=({focus.Q},{focus.R}) observed=1 fresh={(intelFresh ? 1 : 0)}");
        }

        public static void Summarize(PlayerSetupData player, int turn)
        {
            TurnAudit state = StateFor(player, turn);
            if (state == null || state.SummaryWritten)
                return;
            state.SummaryWritten = true;
            foreach (string scenario in Scenarios)
            {
                Status status = state.StatusByScenario.TryGetValue(scenario, out Status s)
                    ? s : Status.NotObserved;
                AiDebugLog.Write($"[AI][V2][Recon][Air][Acceptance][Summary] turn={turn} "
                    + $"scenario={scenario} status={Name(status)}");
            }
        }

        private static TurnAudit StateFor(PlayerSetupData player, int turn)
        {
            if (player == null)
                return null;
            if (!ByPlayer.TryGetValue(player, out TurnAudit state) || state.Turn != turn)
            {
                state = new TurnAudit { Turn = turn };
                ByPlayer[player] = state;
            }
            return state;
        }

        private static void Record(PlayerSetupData player, int turn, string scenario,
            bool pass, string details)
        {
            TurnAudit state = StateFor(player, turn);
            if (state == null)
                return;

            Status incoming = pass ? Status.Pass : Status.Fail;
            state.StatusByScenario.TryGetValue(scenario, out Status previous);
            Status final = previous == Status.Fail || incoming == Status.Fail ? Status.Fail : Status.Pass;
            state.StatusByScenario[scenario] = final;
            if (previous == final && previous != Status.NotObserved)
                return;

            state.SummaryWritten = false;
            AiDebugLog.Write($"[AI][V2][Recon][Air][Acceptance] turn={turn} scenario={scenario} "
                + $"status={Name(final)} {details}");
        }

        private static string Name(Status status)
        {
            switch (status)
            {
                case Status.Pass: return "PASS";
                case Status.Fail: return "FAIL";
                default: return "NOT_OBSERVED";
            }
        }
    }
}
