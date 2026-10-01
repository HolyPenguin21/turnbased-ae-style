using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Cards;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  AP BUDGET TELEMETRY — diagnostics only, decides nothing
    // ===========================================================================================
    //  Groundwork for the AP scarcity price (2026-10-01, user decision: log first, switch on
    //  after a playtest). The turn's measurement itself is ApTurnPressure (State); this class
    //  only logs it. One [AI][V2][ApBudget] line pair per turn:
    //    · start: what the existing AP model (ApActionEconomySnapshot ->
    //      EffectEvaluationContext.ResolveUsefulApDemand / ResolveMarginalApUtility) believes;
    //    · end:   what really happened — AP spent on task steps, draws and the rest; the AP demand
    //      left unmet (cards the hand still lacks, affordable hand cards not played, missions the
    //      allocator deferred for budget); the resulting pressure and the multiplier it would set;
    //      and which executed steps would have fallen to value <= 0 under that multiplier.
    //  Lifecycle legs (returns) carry no value, so they are listed under the multiplier too.
    // ===========================================================================================
    internal static class ApBudgetTelemetry
    {
        private sealed class Step
        {
            public string Label;
            public float Value;
            public float Ap;
            public bool Commitment;
        }

        private sealed class Turn
        {
            public int Number = -1;
            public int StartAp;
            public readonly List<Step> Steps = new List<Step>();
        }

        private static readonly Dictionary<PlayerSetupData, Turn> ByPlayer =
            new Dictionary<PlayerSetupData, Turn>();

        internal static void ClearAll() => ByPlayer.Clear();

        internal static void Begin(PlayerSetupData player, int turn, int startAp, WorldSnapshot snap)
        {
            if (player == null)
                return;
            ByPlayer[player] = new Turn { Number = turn, StartAp = startAp };
            ApActionEconomySnapshot ape = snap?.Self?.ApEconomy;
            if (ape == null)
                return;
            float structural = ape.EstimatedArmyApDemand + ape.EstimatedCardApDemand
                + ape.EstimatedDevelopmentApDemand + ape.EstimatedAirApDemand + ape.EstimatedDrawApDemand;
            float useful = EffectEvaluationContext.ResolveUsefulApDemand(snap, null);
            float marginal = EffectEvaluationContext.ResolveMarginalApUtility(snap, null);
            AiDebugLog.Write($"[AI][V2][ApBudget] {player.Nickname} T{turn} start — AP {startAp} | model demand: "
                + $"armies {F(ape.EstimatedArmyApDemand)} cards {F(ape.EstimatedCardApDemand)} "
                + $"dev {F(ape.EstimatedDevelopmentApDemand)} air {F(ape.EstimatedAirApDemand)} "
                + $"draws {F(ape.EstimatedDrawApDemand)} = structural {F(structural)} | witnessed (last turns) "
                + (ape.WitnessedApDemand.HasValue ? F(ape.WitnessedApDemand.Value) : "none") + $" -> useful {F(useful)} "
                + $"| ratio {F(useful / Mathf.Max(1f, ape.BaseActionPoints))} marginalApUtil {F(marginal)}");
        }

        internal static void RecordStep(PlayerSetupData player, int turn, MissionProposal mission,
            bool commitment, float apSpent)
        {
            if (player == null || mission == null || apSpent <= 0f
                || !ByPlayer.TryGetValue(player, out Turn t) || t.Number != turn)
                return;
            t.Steps.Add(new Step
            {
                Label = StableMissionKey.For(mission).ToString(),
                Value = mission.BaseValue,
                Ap = apSpent,
                Commitment = commitment,
            });
        }

        internal static void End(PlayerSetupData player, AiTurnContext ctx, int cardsDrawn, ApTurnMeasure m)
        {
            if (player == null || ctx == null
                || !ByPlayer.TryGetValue(player, out Turn t) || t.Number != ctx.TurnNumber)
                return;

            float steps = t.Steps.Sum(s => s.Ap);
            int draws = cardsDrawn * ctx.DrawApCost;
            float other = Mathf.Max(0f, m.Spent - steps - draws);
            float multiplier = Mathf.Clamp(m.Pressure, AiConfigV2.apScarcityMultiplierMin,
                AiConfigV2.apScarcityMultiplierMax);

            AiDebugLog.Write($"[AI][V2][ApBudget] {player.Nickname} T{ctx.TurnNumber} end — AP {m.StartAp}->{m.EndAp} "
                + $"| spent {m.Spent}: task steps {F(steps)}, draws {draws} ({cardsDrawn}), cards, reaction & other {F(other)} "
                + $"| unmet {F(m.Unmet)}: draws to {AiConfigV2.handReplenishTargetCards} cards {F(m.DrawsUnmet)} ({m.DrawsShort}), "
                + $"affordable hand cards {F(m.CardsUnmet)} ({m.CardsAffordable}), budget-deferred missions "
                + $"{F(m.MissionsUnmet)} ({m.MissionsDeferred}) | demand {F(m.Demand)} pressure {F(m.Pressure)} "
                + $"-> multiplier {F(multiplier)}");

            // Which executed steps the multiplier would have priced to <= 0. The extra price is
            // (multiplier - 1) per AP actually spent, on the TaskScore scale (1 point = 1 AP).
            float extra = Mathf.Max(0f, multiplier - 1f) * AiConfigV2.taskScorePerApEquivalent;
            var dropped = t.Steps
                .Where(s => s.Value - extra * s.Ap <= 0f)
                .GroupBy(s => s.Label)
                .Select(g => (Label: g.Key, Value: g.First().Value, Ap: g.Sum(s => s.Ap),
                    Commitment: g.Any(s => s.Commitment)))
                .OrderBy(x => x.Value)
                .ToList();
            if (dropped.Count > 0)
                AiDebugLog.Write($"[AI][V2][ApBudget] {player.Nickname} T{ctx.TurnNumber} at x{F(multiplier)} would drop "
                    + $"{F(dropped.Sum(x => x.Ap))} AP: "
                    + string.Join("; ", dropped.Select(x => $"{x.Label} v={F(x.Value)} ap={F(x.Ap)}"
                        + (x.Commitment ? " [commitment]" : ""))));
        }

        private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
