using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Game.Ai.V2;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    // ===========================================================================================
    //  TaskScore calibration report (Unity: AI > TaskScore > Calibration Report)
    // ===========================================================================================
    //  "What if" view for the owner's cross-family calibration: typical raw facts of each task
    //  family are pushed through the REAL TaskScoreEvaluator converters, ActionPrice and the
    //  calibration table (AiConfigV2.TaskScore.cs), and the result is written to
    //  Tools/TaskScoreDashboard/calibration.json for the dashboard's Calibration tab. Change a
    //  constant, recompile, run the menu item, reload the dashboard.
    //  The raw facts below are representative inputs, not measurements; the per-family
    //  distribution of real games is the dashboard's "Log" view of the [AI][V2][TaskScore] lines.
    // ===========================================================================================
    public static class TaskScoreCalibrationReport
    {
        private sealed class Scenario
        {
            public string Name;
            public string Family;
            public string Facts;
            public TaskScore Score;
        }

        [MenuItem("AI/TaskScore/Calibration Report")]
        public static void Write()
        {
            string path = Path.GetFullPath(Path.Combine(Application.dataPath,
                "../Tools/TaskScoreDashboard/calibration.json"));
            File.WriteAllText(path, Build(), new UTF8Encoding(false));
            Debug.Log($"[TaskScore] calibration report written: {path}");
        }

        internal static string Build()
        {
            var sb = new StringBuilder();
            sb.Append("{\"generatedAt\":\"").Append(System.DateTime.UtcNow.ToString("o")).Append("\",");
            sb.Append("\"families\":[");
            AppendFamilies(sb);
            sb.Append("],\"scenarios\":[");
            List<Scenario> scenarios = Scenarios();
            for (int i = 0; i < scenarios.Count; i++)
            {
                if (i > 0) sb.Append(',');
                AppendScenario(sb, scenarios[i]);
            }
            sb.Append("]}");
            return sb.ToString();
        }

        // The largest Benefit each family can reach with every one of its facts saturated —
        // the ceiling the caps of the calibration table give it.
        private static void AppendFamilies(StringBuilder sb)
        {
            var ceilings = new (string Family, string Slots, float Max)[]
            {
                ("Economy · Extraction", "EconomicHexBenefit + Payback",
                    TaskScoreEvaluator.EconomicHexBenefit(100f, 1f) + TaskScoreEvaluator.Payback(0f)),
                ("Economy · Base", "EconomicHexBenefit + Payback + Airfield + GlobalCardEffect + EconomicExpansion",
                    TaskScoreEvaluator.EconomicHexBenefit(100f, 1f) + TaskScoreEvaluator.Payback(0f)
                    + TaskScoreEvaluator.Airfield(1f) + TaskScoreEvaluator.GlobalCardEffect(1f)
                    + TaskScoreEvaluator.EconomicExpansionValue(1f)),
                ("Recon · Explore", "InfoGain + StrategicRelevance (ruins)",
                    TaskScoreEvaluator.InfoGain(1f)
                    + TaskScoreEvaluator.StrategicRelevance(AiConfigV2.reconRuinsRelevance)),
                ("Recon · Refresh", "Staleness + StrategicRelevance + ThreatDirection",
                    TaskScoreEvaluator.PositiveStaleness(1f) + TaskScoreEvaluator.StrategicRelevance(1f)
                    + TaskScoreEvaluator.ThreatDirection(1f)),
                ("Military · Raid", "RaidReward + WinChance",
                    TaskScoreEvaluator.RaidReward() + TaskScoreEvaluator.WinChance(1f)),
                ("Military · Attack", "StrategicRelevance + ThreatDirection + AttackReadiness + WinChance",
                    TaskScoreEvaluator.StrategicRelevance(1f) + TaskScoreEvaluator.ThreatDirection(1f)
                    + TaskScoreEvaluator.AttackReadiness(1f) + TaskScoreEvaluator.WinChance(1f)),
                ("Military · ActiveDefence", "StrategicRelevance + ThreatDirection + PreventedDamage + WinChance",
                    TaskScoreEvaluator.StrategicRelevance(1f) + TaskScoreEvaluator.ThreatDirection(1f)
                    + TaskScoreEvaluator.PreventedDamage(1f) + TaskScoreEvaluator.WinChance(1f)),
                ("Development", "ForceAmplification", TaskScoreEvaluator.ForceAmplification(100f)),
                ("Positional (any family)", "FrontProgress + CorridorAlignment + TerrainDefense + proximity(+half span)",
                    TaskScoreEvaluator.FrontProgress(1f) + TaskScoreEvaluator.CorridorAlignment(1f)
                    + TaskScoreEvaluator.TerrainDefense(1f) + TaskScoreEvaluator.OwnTerritoryProximity(0f)),
            };
            for (int i = 0; i < ceilings.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"family\":").Append(Q(ceilings[i].Family))
                    .Append(",\"slots\":").Append(Q(ceilings[i].Slots))
                    .Append(",\"maxBenefit\":").Append(N(ceilings[i].Max)).Append('}');
            }
        }

        private static List<Scenario> Scenarios()
        {
            float P(float ap) => TaskScoreEvaluator.Price(ap);
            float March(float perTurnAp, float eta) => P(ActionPrice.RecurringAp(perTurnAp, eta));
            float Res(float units) => units * AiConfigV2.actionPriceResourceAp;   // neutral scarcity
            float Home(int hexes) => TaskScoreEvaluator.OwnTerritoryProximity(hexes);

            return new List<Scenario>
            {
                new Scenario { Name = "Extraction", Family = "Economy",
                    Facts = "income +1, shortage 0.5, payback 4 turns, 3 hexes home, card 2 AP + 4 res, walk 2 AP",
                    Score = new TaskScore(
                        economicHexBenefit: TaskScoreEvaluator.EconomicHexBenefit(1f, 0.5f),
                        payback: TaskScoreEvaluator.Payback(4f), ownTerritoryProximity: Home(3),
                        cardPrice: P(2f + Res(4f)), delivery: P(2f)) },
                new Scenario { Name = "Base", Family = "Economy",
                    Facts = "income +1.5 (two types, shortage 0.5), payback 5, airfield 0.5, front 0.4, corridor 0.25, new cluster 1/2, card 2 AP + 5 res, walk 2 AP",
                    Score = new TaskScore(
                        economicHexBenefit: TaskScoreEvaluator.EconomicHexBenefit(
                            new List<(float, float)> { (1f, 0.5f), (0.5f, 0.5f) }),
                        payback: TaskScoreEvaluator.Payback(5f), airfield: TaskScoreEvaluator.Airfield(0.5f),
                        frontProgress: TaskScoreEvaluator.FrontProgress(0.4f),
                        corridorAlignment: TaskScoreEvaluator.CorridorAlignment(0.25f),
                        ownTerritoryProximity: Home(3),
                        economicExpansionValue: TaskScoreEvaluator.EconomicExpansionValue(0.5f),
                        cardPrice: P(2f + Res(5f)), delivery: P(2f)) },
                new Scenario { Name = "Raid near", Family = "Military",
                    Facts = "win 0.8, 3 hexes home, activation 3 AP, one more turn",
                    Score = new TaskScore(raidReward: TaskScoreEvaluator.RaidReward(),
                        winChance: TaskScoreEvaluator.WinChance(0.8f), ownTerritoryProximity: Home(3),
                        cardPrice: P(3f), delivery: March(3f, 2f)) },
                new Scenario { Name = "Raid far", Family = "Military",
                    Facts = "win 0.8, 9 hexes home, activation 3 AP, two more turns",
                    Score = new TaskScore(raidReward: TaskScoreEvaluator.RaidReward(),
                        winChance: TaskScoreEvaluator.WinChance(0.8f), ownTerritoryProximity: Home(9),
                        cardPrice: P(3f), delivery: March(3f, 3f)) },
                new Scenario { Name = "Attack enemy Base", Family = "Military",
                    Facts = "asset 0.6, threat 0.3, readiness 0.5, win 0.7, front 0.5, corridor 0.5, 6 hexes, activation 5 AP, three turns",
                    Score = new TaskScore(strategicRelevance: TaskScoreEvaluator.StrategicRelevance(0.6f),
                        threatDirection: TaskScoreEvaluator.ThreatDirection(0.3f),
                        attackReadiness: TaskScoreEvaluator.AttackReadiness(0.5f),
                        winChance: TaskScoreEvaluator.WinChance(0.7f),
                        frontProgress: TaskScoreEvaluator.FrontProgress(0.5f),
                        corridorAlignment: TaskScoreEvaluator.CorridorAlignment(0.5f),
                        ownTerritoryProximity: Home(6), cardPrice: P(5f), delivery: March(5f, 3f),
                        citadelThreatRisk: 0f) },
                new Scenario { Name = "ActiveDefence intercept", Family = "Military",
                    Facts = "asset 0.6, enemy 1 turn away, damage 0.5, win 0.8, 1 hex home, activation 4 AP, one more turn",
                    Score = new TaskScore(strategicRelevance: TaskScoreEvaluator.StrategicRelevance(0.6f),
                        threatDirection: TaskScoreEvaluator.ThreatDirection(0.5f),
                        preventedDamage: TaskScoreEvaluator.PreventedDamage(0.5f),
                        winChance: TaskScoreEvaluator.WinChance(0.8f), ownTerritoryProximity: Home(1),
                        cardPrice: P(4f), delivery: March(4f, 2f)) },
                new Scenario { Name = "Recon Explore", Family = "Recon",
                    Facts = "info 0.8, 6 hexes home, activation 1 + stealth 1 AP, one more turn, detection 0.2",
                    Score = new TaskScore(infoGain: TaskScoreEvaluator.InfoGain(0.8f), ownTerritoryProximity: Home(6),
                        cardPrice: P(2f), delivery: March(1f, 2f),
                        detectionRisk: TaskScoreEvaluator.DetectionRisk(0.2f)) },
                new Scenario { Name = "Recon Refresh", Family = "Recon",
                    Facts = "staleness 0.5, relevance 0.6, threat direction 0.4, activation 1 AP, one more turn",
                    Score = new TaskScore(staleness: TaskScoreEvaluator.PositiveStaleness(0.5f),
                        strategicRelevance: TaskScoreEvaluator.StrategicRelevance(0.6f),
                        threatDirection: TaskScoreEvaluator.ThreatDirection(0.4f),
                        cardPrice: P(1f), delivery: March(1f, 2f)) },
                new Scenario { Name = "Mobile collection", Family = "Economy",
                    Facts = "income +1, shortage 0.5, 3 hexes home, activation 2 AP, one more turn",
                    Score = new TaskScore(economicHexBenefit: TaskScoreEvaluator.EconomicHexBenefit(1f, 0.5f),
                        ownTerritoryProximity: Mathf.Max(0f, Home(3)), cardPrice: P(2f), delivery: March(2f, 2f)) },
                new Scenario { Name = "Development operator walk", Family = "Development",
                    Facts = "need-weighted force 1.5 bodies, leaves a garrison (1 AP), activation 2 AP, one more turn",
                    Score = new TaskScore(forceAmplification: TaskScoreEvaluator.ForceAmplification(1.5f),
                        cardPrice: P(1f + 2f), delivery: March(2f, 2f)) },
            };
        }

        private static void AppendScenario(StringBuilder sb, Scenario s)
        {
            sb.Append("{\"name\":").Append(Q(s.Name)).Append(",\"family\":").Append(Q(s.Family))
                .Append(",\"facts\":").Append(Q(s.Facts))
                .Append(",\"value\":").Append(N(s.Score.Value));
            foreach (TaskSlotCategory c in (TaskSlotCategory[])System.Enum.GetValues(typeof(TaskSlotCategory)))
                sb.Append(",\"").Append(c.ToString().ToLowerInvariant()).Append("\":")
                    .Append(N(TaskScoreEvaluator.CategoryTotal(s.Score, c)));
            sb.Append(",\"terms\":{");
            bool first = true;
            foreach (TaskSlot slot in (TaskSlot[])System.Enum.GetValues(typeof(TaskSlot)))
            {
                if (Mathf.Abs(s.Score[slot]) < 0.005f) continue;
                if (!first) sb.Append(',');
                sb.Append(Q(slot.ToString())).Append(':').Append(N(s.Score[slot]));
                first = false;
            }
            sb.Append("}}");
        }

        private static string N(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        private static string Q(string s) =>
            "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
