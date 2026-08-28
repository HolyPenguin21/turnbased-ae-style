using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai
{
    // Strategic layer (2026-08-27, project owner's own redesign) — sits ABOVE the per-step
    // orchestrator (AiTurnController.Decide) and does not itself pick any action. Once per turn it
    // reads the game state and produces one "desire" axis in [0..1] per AiTaskCategory: how much
    // this player wants to be doing that kind of thing right now. AiTurnBudget then splits the
    // turn's AP by those axes, and Decide nudges every candidate's score by (axis - 0.5) plus a
    // penalty for a category that has already blown its budget (see AiStrategyLayer.Adjust).
    //
    // This is a NUDGE on top of the existing base-weight arbiter, never a hard gate — a genuinely
    // urgent candidate (a 120 Defence intercept) still wins over a nudged routine one. At
    // AiConfig.strategyAxisGain = 0 the whole layer is an exact no-op, so it can be dialed back to
    // pure logging at any time.
    //
    // Each axis is a small weighted blend of normalized 0..1 readings of the state (IAUS-style,
    // but only five formulas, not one per action), then low-pass filtered against the previous
    // turn's value (AiConfig.strategyAxisSmoothing) so the AI doesn't whipsaw. Hard events (under
    // siege) bypass the filter and snap the Defence axis. A posture LABEL (Expand/Consolidate/
    // Pressure/Defend/AllIn) is derived from the smoothed axes purely for the log and one discrete
    // override (AllIn zeroes Economy).
    public enum AiStrategyPosture { Expand, Consolidate, Pressure, Defend, AllIn }

    public readonly struct AiStrategyAssessment
    {
        public readonly float Aggression;
        public readonly float Defence;
        public readonly float Economy;
        public readonly float Reconnaissance;
        public readonly float Management;
        public readonly AiStrategyPosture Posture;

        public AiStrategyAssessment(float aggression, float defence, float economy, float reconnaissance,
            float management, AiStrategyPosture posture)
        {
            Aggression = aggression;
            Defence = defence;
            Economy = economy;
            Reconnaissance = reconnaissance;
            Management = management;
            Posture = posture;
        }

        public float AxisFor(AiTaskCategory category)
        {
            switch (category)
            {
                case AiTaskCategory.Aggression: return Aggression;
                case AiTaskCategory.Defence: return Defence;
                case AiTaskCategory.Economy: return Economy;
                case AiTaskCategory.Reconnaissance: return Reconnaissance;
                case AiTaskCategory.Management: return Management;
                default: return 0.5f;
            }
        }

        // Neutral fallback — every axis 0.5 (no tilt), used when the layer is disabled or a player
        // has no citadel yet.
        public static AiStrategyAssessment Neutral =>
            new AiStrategyAssessment(0.5f, 0.5f, 0.5f, 0.5f, 0.5f, AiStrategyPosture.Expand);
    }

    // Per-player persistent AI blackboard for the strategic layer — same static
    // Dictionary<PlayerSetupData, ...> registry shape AiTaskRegistry / AiHandRegistry already use.
    // Holds the smoothed axis values between turns (the only cross-turn state the layer needs).
    internal sealed class AiStrategyState
    {
        public float Aggression = 0.5f;
        public float Defence = 0.5f;
        public float Economy = 0.5f;
        public float Reconnaissance = 0.5f;
        public float Management = 0.35f;
        public AiStrategyPosture Posture = AiStrategyPosture.Expand;
        public int LastEvaluatedTurn = -1;
    }

    internal static class AiStrategyRegistry
    {
        private static readonly Dictionary<PlayerSetupData, AiStrategyState> ByPlayer =
            new Dictionary<PlayerSetupData, AiStrategyState>();

        public static AiStrategyState GetOrCreate(PlayerSetupData player)
        {
            if (!ByPlayer.TryGetValue(player, out AiStrategyState state))
                ByPlayer[player] = state = new AiStrategyState();
            return state;
        }

        public static void Clear() => ByPlayer.Clear();
    }

    public static class AiStrategyDirector
    {
        // Called once per turn from AiTurnController.RunTurn, before the per-step Decide loop.
        // Reads state, blends each axis, smooths against AiStrategyState, stores the result back,
        // logs one line, and returns the assessment for AiTurnBudget / AiStrategyLayer.Adjust.
        public static AiStrategyAssessment Evaluate(PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx)
        {
            if (!AiConfig.strategyLayerEnabled || player == null || root == null || ctx == null || ctx.Map == null)
                return AiStrategyAssessment.Neutral;
            if (!player.CitadelHexQ.HasValue || !player.CitadelHexR.HasValue)
                return AiStrategyAssessment.Neutral;

            AiStrategyState state = AiStrategyRegistry.GetOrCreate(player);

            // ---- shared readings ----
            int turn = ctx.TurnNumber;
            HexCoord citadel = new HexCoord(player.CitadelHexQ.Value, player.CitadelHexR.Value);

            List<HexCoord> baseHexes = ArmyRegistry.AllForOwner(player)
                .Where(a => a != null && a.IsGarrison && !a.IsPrison)
                .Select(a => a.Hex)
                .Distinct()
                .ToList();
            if (baseHexes.Count == 0)
                baseHexes.Add(citadel);

            float myArmyStrength = ArmyRegistry.AllForOwner(player)
                .Where(a => a != null && !a.IsPrison)
                .Sum(a => WorthIt.AttackSum(a) + WorthIt.DefenseSum(a));
            float myGarrisonStrength = ArmyRegistry.AllForOwner(player)
                .Where(a => a != null && a.IsGarrison && !a.IsPrison)
                .Sum(a => WorthIt.AttackSum(a) + WorthIt.DefenseSum(a));
            float myFieldStrength = ArmyRegistry.AllForOwner(player)
                .Where(a => a != null && !a.IsGarrison && !a.IsPrison)
                .Sum(a => WorthIt.AttackSum(a) + WorthIt.DefenseSum(a));

            var enemySightings = AiMapMemory.AllKnownEnemySightings(player).ToList();
            bool anyEnemyKnown = enemySightings.Count > 0;
            bool anyRaidTargetKnown = anyEnemyKnown || AiMapMemory.AllKnownNeutralSightings(player).Any();

            float enemyKnownStrength = enemySightings.Sum(s => s.DefenseSum + s.AttackSum);

            int nearestEnemyToBase = int.MaxValue;
            float enemyStrengthNearBases = 0f;
            foreach (AiMapMemory.KnownEnemySighting s in enemySightings)
            {
                int d = baseHexes.Min(b => HexGridMath.Distance(b, s.Hex));
                if (d < nearestEnemyToBase)
                    nearestEnemyToBase = d;
                if (d <= AiConfig.raidThreatRadius + 2)
                    enemyStrengthNearBases += s.DefenseSum + s.AttackSum;
            }
            if (nearestEnemyToBase == int.MaxValue)
                nearestEnemyToBase = 99;

            bool underSiege = AiDefencePlanner.IsUnderSiege(player, ctx);

            int total = 0, visited = 0;
            foreach (HexCoord c in ctx.Map.AllCoords)
            {
                total++;
                if (VisionSystem.IsVisited(player, c))
                    visited++;
            }
            float unknownFrac = total > 0 ? 1f - (float)visited / total : 0f;

            bool ecoMature = AiGoalScorer.HasMatureEconomy(player, AiConfig.economyMatureIncomePerType, ctx.Map);
            int stockpile = root.GetResource(ResourceType.Human) + root.GetResource(ResourceType.Energy)
                + root.GetResource(ResourceType.Materials) + root.GetResource(ResourceType.Tech);
            int handCount = hand?.Hand.Count ?? 0;

            // ---- Defence axis ----
            // Floored at strategyDefenceFloor even with nothing in sight (baseline vigilance — a
            // 0.00 axis takes far too many smoothed turns to climb back once a threat finally
            // appears; see the asymmetric rise/fall smoothing below). A committed field army with
            // the enemy known on the map raises that floor (`atWar` — you've provoked a response,
            // keep a guard up even before it reaches the gates). Snaps to ~0.95 under siege.
            float threatProximity = InvNorm(nearestEnemyToBase, 2f, 12f);
            float threatRatio = Norm(enemyStrengthNearBases / Mathf.Max(1f, myGarrisonStrength), 0.3f, 1.6f);
            float exposure = Norm(enemyStrengthNearBases, 0f, 24f);
            float atWarFloor = anyEnemyKnown && myFieldStrength > 4f
                ? AiConfig.strategyDefenceAtWarFloor
                : AiConfig.strategyDefenceFloor;
            float rawDefence = Mathf.Max(atWarFloor,
                0.4f * threatProximity + 0.4f * threatRatio + 0.2f * exposure);
            if (underSiege)
                rawDefence = Mathf.Max(rawDefence, 0.95f);

            // ---- Economy axis ----
            float earlyGame = InvNorm(turn, 3f, 20f);
            float notMature = ecoMature ? 0.15f : 0.8f;
            float lowStockpile = InvNorm(stockpile, 4f, 40f);
            float frontierSafe = 1f - rawDefence;
            float rawEconomy = 0.35f * notMature + 0.25f * earlyGame + 0.2f * lowStockpile + 0.2f * frontierSafe;

            // ---- Aggression axis ----
            // militaryEdge is NEUTRAL (0.5) with no enemy intel at all — "I haven't seen them"
            // must never read as "I'm crushing them" (that false-positive pinned AGG at ~0.9 all
            // game and tripped a turn-6 AllIn — project owner's own log audit 2026-08-27).
            // forceReadiness folds in absolute field strength so an army-less early game can't want
            // to attack regardless of what targets it has spotted.
            float militaryEdge = enemyKnownStrength < 1f
                ? 0.5f
                : Norm(myArmyStrength / enemyKnownStrength, 0.8f, 2.2f);
            float forceReadiness = Norm(myFieldStrength, 4f, 30f);
            float targetsKnown = anyRaidTargetKnown ? 1f : 0.2f;
            float ecoSecure = ecoMature ? 1f : 0.5f;
            float notThreatened = 1f - rawDefence;
            float rawAggression = 0.3f * militaryEdge + 0.2f * forceReadiness + 0.2f * targetsKnown
                + 0.15f * ecoSecure + 0.15f * notThreatened;

            // ---- Reconnaissance axis ----
            // Upper bound 0.75 (not 0.5) so the term actually TAPERS as the map opens instead of
            // sitting clamped at 1.0 for the whole game on a large map (2026-08-27 log audit — RCN
            // stayed pinned ~0.9 through turn 20 with 65% still dark).
            float unknownMap = Norm(unknownFrac, 0.05f, 0.75f);
            float staleEnemyInfo = anyEnemyKnown ? 0.3f : 0.7f;
            float decayWithTurn = InvNorm(turn, 4f, 30f);
            float rawRecon = Mathf.Max(0.1f, 0.5f * unknownMap + 0.2f * staleEnemyInfo + 0.3f * decayWithTurn);

            // ---- Management axis ---- (housekeeping; deliberately modest range)
            float handPressure = Norm(handCount, 4f, 9f);
            float rawManagement = Mathf.Clamp(0.15f + 0.4f * handPressure, 0.2f, 0.7f);

            // ---- smoothing ----
            float s0 = AiConfig.strategyAxisSmoothing;
            float aggression = Smooth(rawAggression, state.Aggression, s0);
            float economy = Smooth(rawEconomy, state.Economy, s0);
            float recon = Smooth(rawRecon, state.Reconnaissance, s0);
            float management = Smooth(rawManagement, state.Management, s0);
            // Defence reacts fast, relaxes slow — a rising threat barely gets smoothed (snap up),
            // a fading one decays gently so the AI doesn't drop its guard the instant an enemy
            // steps out of sight. Siege bypasses it entirely.
            float defence;
            if (underSiege)
                defence = rawDefence;
            else if (rawDefence > state.Defence)
                defence = Smooth(rawDefence, state.Defence, AiConfig.strategyDefenceRiseSmoothing);
            else
                defence = Smooth(rawDefence, state.Defence, AiConfig.strategyDefenceFallSmoothing);

            AiStrategyPosture posture = DerivePosture(aggression, defence, economy, turn, ecoMature);
            if (posture == AiStrategyPosture.AllIn)
                economy = Mathf.Min(economy, 0.1f);

            state.Aggression = aggression;
            state.Defence = defence;
            state.Economy = economy;
            state.Reconnaissance = recon;
            state.Management = management;
            state.Posture = posture;
            state.LastEvaluatedTurn = turn;

            AiDebugLog.Write($"[AI] {player.Nickname}: strategy — "
                + $"AGG {F(aggression)} (edge {F(militaryEdge)}, force {F(forceReadiness)}, tgt {F(targetsKnown)}) | "
                + $"DEF {F(defence)} (prox {F(threatProximity)}, ratio {F(threatRatio)}{(underSiege ? ", SIEGE" : "")}) | "
                + $"ECO {F(economy)} (mature {(ecoMature ? 1 : 0)}, early {F(earlyGame)}) | "
                + $"RCN {F(recon)} (unknown {F(unknownFrac)}) | "
                + $"MGT {F(management)} → {posture}");

            return new AiStrategyAssessment(aggression, defence, economy, recon, management, posture);
        }

        private static AiStrategyPosture DerivePosture(float aggression, float defence, float economy, int turn, bool ecoMature)
        {
            if (defence > 0.75f && defence >= aggression && defence >= economy)
                return AiStrategyPosture.Defend;
            if (aggression > 0.7f && aggression >= economy)
            {
                // AllIn (zeroes Economy) is a heavy commitment — only when the economy has already
                // matured, the game is past its opening, and there is genuinely no threat. Without
                // all three it's just Pressure.
                bool allIn = aggression > 0.85f && defence < 0.3f && ecoMature && turn >= AiConfig.strategyAllInMinTurn;
                return allIn ? AiStrategyPosture.AllIn : AiStrategyPosture.Pressure;
            }
            if (economy >= aggression && economy >= defence)
                return AiStrategyPosture.Expand;
            return AiStrategyPosture.Consolidate;
        }

        private static string F(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);
        private static float Norm(float v, float lo, float hi) => Mathf.Clamp01((v - lo) / Mathf.Max(0.0001f, hi - lo));
        private static float InvNorm(float v, float lo, float hi) => 1f - Norm(v, lo, hi);
        private static float Smooth(float raw, float prev, float prevWeight) => (1f - prevWeight) * raw + prevWeight * prev;
    }

    // Turn-scoped AP/resource budget, built once per turn from the strategy axes (same lifetime as
    // AiResourcePool — created in RunTurn, threaded through every Decide call this turn, accumulates
    // spend across steps). Splits the turn's AP across the five categories in proportion to their
    // axes, holding back AiConfig.strategyBudgetReserveFrac as an unallocated opportunity fund, with
    // per-category floors so a low-desire category (and Management especially) is never penalised
    // into total starvation. Decide reads OverBudgetRatio via AiStrategyLayer.Adjust and calls
    // RecordSpend after each executed decision.
    public sealed class AiTurnBudget
    {
        private static readonly AiTaskCategory[] Categories =
        {
            AiTaskCategory.Reconnaissance, AiTaskCategory.Economy, AiTaskCategory.Management,
            AiTaskCategory.Aggression, AiTaskCategory.Defence,
        };

        private readonly Dictionary<AiTaskCategory, float> _alloc = new Dictionary<AiTaskCategory, float>();
        private readonly Dictionary<AiTaskCategory, float> _spent = new Dictionary<AiTaskCategory, float>();
        private readonly int _totalAp;

        public AiTurnBudget(int totalAp, AiStrategyAssessment strategy)
        {
            _totalAp = Mathf.Max(0, totalAp);

            // Floors are carved out FIRST, then the remainder (after also holding back the reserve)
            // is split by axis on top — so sum(alloc) is exactly totalAp*(1-reserveFrac) and the
            // reserve is genuinely unallocated, rather than the floors silently overrunning it.
            float floorSum = 0f;
            foreach (AiTaskCategory c in Categories)
                floorSum += FloorFor(c);
            float afterReserve = _totalAp * (1f - AiConfig.strategyBudgetReserveFrac);
            // On a poor-AP turn afterReserve can be below the sum of the fixed floors (6 AP:
            // Recon/Eco/Agg/Def 1 + Management 2). Scale the floors down proportionally in that
            // case so sum(alloc) stays exactly totalAp*(1-reserveFrac) instead of the floors
            // overrunning it — the floor ratios (Management still 2× the rest) are preserved.
            // When afterReserve >= floorSum, floorScale is 1 and behaviour is unchanged.
            float floorScale = floorSum > 0f ? Mathf.Min(1f, afterReserve / floorSum) : 1f;
            float effectiveFloorSum = floorSum * floorScale;
            float distributable = Mathf.Max(0f, afterReserve - effectiveFloorSum);
            float axisSum = Categories.Sum(c => strategy.AxisFor(c));
            if (axisSum < 0.0001f)
                axisSum = 1f;

            foreach (AiTaskCategory c in Categories)
            {
                _alloc[c] = FloorFor(c) * floorScale + distributable * (strategy.AxisFor(c) / axisSum);
                _spent[c] = 0f;
            }
        }

        private static float FloorFor(AiTaskCategory c) => c == AiTaskCategory.Management
            ? AiConfig.strategyBudgetManagementMinAllocAp
            : AiConfig.strategyBudgetMinAllocAp;

        public void RecordSpend(AiTaskCategory category, float ap)
        {
            if (ap <= 0f)
                return;
            _spent.TryGetValue(category, out float cur);
            _spent[category] = cur + ap;
        }

        // 0..1 while the category is still within budget, >1 once it has overspent — the amount
        // over 1 is what AiStrategyLayer.Adjust turns into a score penalty.
        public float OverBudgetRatio(AiTaskCategory category)
        {
            _alloc.TryGetValue(category, out float a);
            _spent.TryGetValue(category, out float sp);
            return sp / Mathf.Max(0.5f, a);
        }

        public string DebugLine()
        {
            string parts = string.Join(" / ", Categories.Select(c =>
            {
                _alloc.TryGetValue(c, out float a);
                return $"{Abbrev(c)} {a.ToString("0.0", CultureInfo.InvariantCulture)}";
            }));
            float reserve = _totalAp * AiConfig.strategyBudgetReserveFrac;
            return $"budget: AP {_totalAp} → {parts} / reserve {reserve.ToString("0.0", CultureInfo.InvariantCulture)}";
        }

        private static string Abbrev(AiTaskCategory c)
        {
            switch (c)
            {
                case AiTaskCategory.Aggression: return "AGG";
                case AiTaskCategory.Defence: return "DEF";
                case AiTaskCategory.Economy: return "ECO";
                case AiTaskCategory.Reconnaissance: return "RCN";
                default: return "MGT";
            }
        }
    }

    // The single seam where the strategic layer touches the arbiter — Decide calls this on every
    // scored candidate before picking the max (gated by AiConfig.strategyLayerEnabled at the call
    // site, not here). Additive tilt only: axis pull plus an over-budget penalty, both bounded
    // well under the base-weight scale.
    public static class AiStrategyLayer
    {
        public static float Adjust(float rawScore, AiTaskCategory category, AiStrategyAssessment strategy, AiTurnBudget budget)
        {
            // Tactical/emergency candidates (rawScore >= strategyExemptScore) are fully exempt
            // from the strategic layer — neither the axis tilt nor the over-budget penalty may
            // touch them. They score at/above this line by design (Defence Active 120, Scout Flee
            // 125, Turtle 130) and the intended ladder 120 tactical → 125 retreat → 130 emergency
            // must survive intact regardless of the axis weights or AP spend this turn.
            if (rawScore >= AiConfig.strategyExemptScore)
                return rawScore;
            float axisOffset = (strategy.AxisFor(category) - 0.5f) * AiConfig.strategyAxisGain;
            float over = Mathf.Max(0f, (budget?.OverBudgetRatio(category) ?? 0f) - 1f);
            float budgetPenalty = Mathf.Min(over * AiConfig.strategyBudgetOverGain, AiConfig.strategyBudgetPenaltyCap);
            float adjusted = rawScore + axisOffset - budgetPenalty;
            // A routine candidate (rawScore < strategyExemptScore) must stay routine: the axis
            // tilt alone can add up to +strategyAxisGain/2, which would otherwise let a strong
            // Aggression/Defence axis push e.g. a deliberately-capped-119 AirStrike past the
            // protected tactical/retreat/emergency ladder (120/125/130). Clamp the strategic
            // nudge so it can re-rank the sub-120 space freely but never cross into it.
            return Mathf.Min(adjusted, AiConfig.strategyExemptScore - 1f);
        }
    }
}
