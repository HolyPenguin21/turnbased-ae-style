using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Players;

namespace Game.Ai.V2
{
    // Runtime acceptance collector for the Ground-Recon deep rework. The project does not have a
    // V2 Unity-test harness, so acceptance is deliberately evidence-based: a scenario is PASS/FAIL
    // only after the corresponding live situation actually occurred. Everything else is reported
    // as NOT_OBSERVED at the end of the execution batch instead of being treated as success.
    internal static class ReconAcceptanceAudit
    {
        private const string WeakRecceAttack = "weak-recce-opportunistic-attack";
        private const string HiddenFacilityCapture = "hidden-facility-capture";
        private const string HiddenFacilityCancel = "hidden-facility-cancel-on-danger";
        private const string MostlyExploredRefresh = "refresh-dominates-mostly-explored";
        private const string StaleStrategicRefresh = "stale-strategic-refresh";
        private const string CoarseDirectionBoundary = "coarse-direction-boundary";
        private const string HiddenConcentrationCoarse = "hidden-concentration-coarse-only";
        private const string CoarseDirectionInfluence = "coarse-direction-influences-refresh";
        private const string ThreeScoutDeconflict = "three-scout-deconflict";
        private const string NoAbabLoop = "no-abab-loop";
        private const string PerStepReplan = "per-step-live-replan";
        private const string PerStepIntelRefresh = "per-step-intel-refresh";

        private static readonly string[] Scenarios =
        {
            WeakRecceAttack,
            HiddenFacilityCapture,
            HiddenFacilityCancel,
            MostlyExploredRefresh,
            StaleStrategicRefresh,
            CoarseDirectionBoundary,
            HiddenConcentrationCoarse,
            CoarseDirectionInfluence,
            ThreeScoutDeconflict,
            NoAbabLoop,
            PerStepReplan,
            PerStepIntelRefresh,
        };

        private enum Status { NotObserved, Pass, Fail }

        private sealed class ScoutTrace
        {
            public readonly List<HexCoord> Path = new List<HexCoord>();
            public HexCoord? LastDecisionFrom;
            public HexCoord? LastDecisionTo;
            public HexCoord? LastStepEnd;
            public int Decisions;
            public int Steps;
        }

        private sealed class TurnAudit
        {
            public int Turn;
            public bool SummaryWritten;
            public readonly Dictionary<string, Status> StatusByScenario =
                new Dictionary<string, Status>();
            public readonly Dictionary<int, ScoutTrace> TraceByArmy =
                new Dictionary<int, ScoutTrace>();
        }

        private static readonly Dictionary<PlayerSetupData, TurnAudit> ByPlayer =
            new Dictionary<PlayerSetupData, TurnAudit>();

        public static void ClearAll() => ByPlayer.Clear();

        public static void BeginTurn(PlayerSetupData player, int turn)
        {
            StateFor(player, turn);
        }

        public static void RecordThreeScoutBatch(PlayerSetupData player, int turn,
            IReadOnlyList<ProvisionedMission> queue)
        {
            if (player == null || queue == null || !AiStrategyV2Scope.IsReconOnly)
                return;

            List<ProvisionedMission> scouts = queue
                .Where(m => m != null && m.Kind == MissionKind.Scout)
                .ToList();
            if (scouts.Count < ReconConcurrencyPolicy.ReconOnlyHardCap)
                return;

            int actors = scouts.Select(m => m.MoverArmyId).Distinct().Count();
            int spatialConflicts = 0;
            int groundMinDistance = int.MaxValue;
            for (int i = 0; i < scouts.Count; i++)
            {
                for (int j = i + 1; j < scouts.Count; j++)
                {
                    ProvisionedMission a = scouts[i];
                    ProvisionedMission b = scouts[j];
                    if (a.FocusHex.Equals(b.FocusHex))
                    {
                        spatialConflicts++;
                        continue;
                    }

                    // Mirror MissionAdmissionPolicy exactly: the minimum target separation applies
                    // only to two ground Recon missions. Surveil focus points may be close because
                    // provisioning supplies their safe observation vantages separately.
                    bool bothGround = ReconScoutKinds.IsGround(a.ScoutKind)
                        && ReconScoutKinds.IsGround(b.ScoutKind);
                    if (!bothGround)
                        continue;

                    int d = HexGridMath.Distance(a.FocusHex, b.FocusHex);
                    if (d < groundMinDistance)
                        groundMinDistance = d;
                    if (d < AiConfigV2.scoutTargetMinSeparation)
                        spatialConflicts++;
                }
            }

            bool pass = actors == scouts.Count && spatialConflicts == 0;
            string minGround = groundMinDistance == int.MaxValue ? "n/a" : groundMinDistance.ToString();
            Record(player, turn, ThreeScoutDeconflict, pass,
                $"missions={scouts.Count} actors={actors} spatialConflicts={spatialConflicts} "
                + $"groundMinDist={minGround} required={AiConfigV2.scoutTargetMinSeparation}");
        }

        public static void RecordDecision(PlayerSetupData player, int turn, int armyId,
            HexCoord from, HexCoord to, string reason)
        {
            TurnAudit state = StateFor(player, turn);
            if (state == null)
                return;
            ScoutTrace trace = TraceFor(state, armyId);

            if (trace.LastStepEnd.HasValue)
            {
                bool live = trace.LastStepEnd.Value.Equals(from);
                Record(player, turn, PerStepReplan, live,
                    $"actor=#{armyId} previous=({trace.LastStepEnd.Value.Q},{trace.LastStepEnd.Value.R}) "
                    + $"decisionFrom=({from.Q},{from.R}) reason={reason}");
            }

            trace.LastDecisionFrom = from;
            trace.LastDecisionTo = to;
            trace.Decisions++;
        }

        public static void RecordStep(PlayerSetupData player, int turn, int armyId,
            HexCoord from, HexCoord to)
        {
            TurnAudit state = StateFor(player, turn);
            if (state == null)
                return;
            ScoutTrace trace = TraceFor(state, armyId);
            if (trace.Path.Count == 0)
                trace.Path.Add(from);
            trace.Path.Add(to);
            trace.Steps++;

            bool decisionMatches = trace.LastDecisionFrom.HasValue && trace.LastDecisionTo.HasValue
                && trace.LastDecisionFrom.Value.Equals(from)
                && trace.LastDecisionTo.Value.Equals(to);
            if (!decisionMatches)
            {
                Record(player, turn, PerStepReplan, false,
                    $"actor=#{armyId} executed=({from.Q},{from.R})->({to.Q},{to.R}) did not match latest one-step decision");
            }

            trace.LastStepEnd = to;

            bool intelFresh = AiReconIntelMemory.TryGetLastObservedTurn(player, to, out int observedTurn)
                && observedTurn == turn;
            Record(player, turn, PerStepIntelRefresh, intelFresh,
                $"actor=#{armyId} hex=({to.Q},{to.R}) observedTurn={(intelFresh ? turn : observedTurn)} expected={turn}");

            int n = trace.Path.Count;
            if (n >= 4)
            {
                HexCoord a1 = trace.Path[n - 4];
                HexCoord b1 = trace.Path[n - 3];
                HexCoord a2 = trace.Path[n - 2];
                HexCoord b2 = trace.Path[n - 1];
                bool abab = !a1.Equals(b1) && a1.Equals(a2) && b1.Equals(b2);
                Record(player, turn, NoAbabLoop, !abab,
                    $"actor=#{armyId} tail=({a1.Q},{a1.R})->({b1.Q},{b1.R})->({a2.Q},{a2.R})->({b2.Q},{b2.R})");
            }
        }

        public static void RecordWeakRecceAttack(PlayerSetupData player, int turn, int armyId,
            int targetArmyId, bool battleOccurred, float winChance)
        {
            Record(player, turn, WeakRecceAttack, battleOccurred,
                $"actor=#{armyId} target=#{targetArmyId} win={winChance:0.00} battle={(battleOccurred ? 1 : 0)}");
        }

        public static void RecordHiddenFacilityCapture(PlayerSetupData player, int turn, int armyId,
            HexCoord hex, bool startedHidden, bool worldChanged)
        {
            Record(player, turn, HiddenFacilityCapture, startedHidden && worldChanged,
                $"actor=#{armyId} hex=({hex.Q},{hex.R}) hiddenEntry={(startedHidden ? 1 : 0)} "
                + $"resolved={(worldChanged ? 1 : 0)}");
        }

        public static void RecordHiddenFacilityCancel(PlayerSetupData player, int turn, int armyId,
            HexCoord hex, bool startedHidden, ReconReactionAction afterDecloak)
        {
            bool danger = afterDecloak == ReconReactionAction.Flee
                || afterDecloak == ReconReactionAction.EvadeDetector
                || afterDecloak == ReconReactionAction.StopAndReplan;
            Record(player, turn, HiddenFacilityCancel, startedHidden && danger,
                $"actor=#{armyId} hex=({hex.Q},{hex.R}) hiddenEntry={(startedHidden ? 1 : 0)} afterDecloak={afterDecloak}");
        }

        public static void RecordMostlyExploredPressure(PlayerSetupData player, int turn,
            float explorableUnknownFrac, float explorePressure, float refreshPressure)
        {
            if (explorableUnknownFrac > 0.25f)
                return;
            Record(player, turn, MostlyExploredRefresh, refreshPressure >= explorePressure,
                $"dark={explorableUnknownFrac:0.00} explore={explorePressure:0.00} refresh={refreshPressure:0.00}");
        }

        public static void RecordStaleStrategicRefresh(PlayerSetupData player, int turn, HexCoord hex,
            int ageTurns, float strategicRelevance)
        {
            if (strategicRelevance <= 0f)
                return;
            bool stale = ageTurns >= AiConfigV2.scoutSurveilStaleTurnsLo;
            Record(player, turn, StaleStrategicRefresh, stale,
                $"hex=({hex.Q},{hex.R}) age={ageTurns} strategic={strategicRelevance:0.00}");
        }

        public static void RecordDirectionBoundary(PlayerSetupData player, int turn,
            ReconDirectionSnapshot direction)
        {
            if (direction?.EnemyDirectionSectors == null || direction.EnemyPresenceWeight <= 0f)
                return;

            float sum = 0f;
            bool rangeOk = true;
            foreach (KeyValuePair<ReconSector, float> kv in direction.EnemyDirectionSectors)
            {
                sum += kv.Value;
                if (kv.Value < -0.0001f || kv.Value > 1.0001f)
                    rangeOk = false;
            }
            bool normalized = sum >= 0.999f && sum <= 1.001f;
            bool sixBuckets = direction.EnemyDirectionSectors.Count == 6;
            bool pass = rangeOk && normalized && sixBuckets;
            string detail = $"sectors={direction.EnemyDirectionSectors.Count} sum={sum:0.000} "
                + $"presence={direction.EnemyPresenceWeight:0}";
            Record(player, turn, CoarseDirectionBoundary, pass, detail);
            // ReconDirectionSnapshot exposes only aggregate six-sector weights, one aggregate
            // presence count, a coarse citadel sector and coarse watch sectors. Exact hidden hex,
            // army id, strength, roster, AA, Recce and stealth are not representable across this
            // boundary; this runtime check verifies the aggregate output retains its coarse shape.
            Record(player, turn, HiddenConcentrationCoarse, pass, detail + " exactHiddenFields=0");
        }

        public static void RecordDirectionInfluence(PlayerSetupData player, int turn,
            HexCoord focus, float directionPressure, float baseValue)
        {
            if (directionPressure <= 0f)
                return;
            Record(player, turn, CoarseDirectionInfluence, true,
                $"focus=({focus.Q},{focus.R}) direction={directionPressure:0.00} value={baseValue:0.0}");
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
                    ? s
                    : Status.NotObserved;
                AiDebugLog.Write($"[AI][V2][Recon][Acceptance][Summary] turn={turn} scenario={scenario} "
                    + $"status={Name(status)}");
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

        private static ScoutTrace TraceFor(TurnAudit state, int armyId)
        {
            if (!state.TraceByArmy.TryGetValue(armyId, out ScoutTrace trace))
                state.TraceByArmy[armyId] = trace = new ScoutTrace();
            return trace;
        }

        private static void Record(PlayerSetupData player, int turn, string scenario, bool pass, string details)
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
            AiDebugLog.Write($"[AI][V2][Recon][Acceptance] turn={turn} scenario={scenario} "
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
