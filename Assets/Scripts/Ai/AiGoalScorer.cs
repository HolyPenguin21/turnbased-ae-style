using System.Collections.Generic;
using System.Linq;
using Game.Core;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai
{
    // Stage 1 of an AI turn (see the AI architecture design doc): scores every AiGoalKind for
    // `actor` against the CURRENT map state. Pure read-only logic, same stateless-static style
    // as BattleAi — never mutates ArmyRegistry/BuildingRegistry, only reads them (and, for
    // ScoreExpandEconomy, AiMapMemory's own honest per-player memory rather than live state —
    // see that class's own comment). ExpandEconomy is currently the only AiGoalKind — the only
    // one with a real task chain behind it (see AiTurnController.TryStartEconomyCandidates/
    // AdvanceEconomyTask, which reuse ScoreExpandEconomyHex directly rather than going through
    // PickBest). Оборона/Атака goals (DefendBorder,
    // DestroyEnemyCitadel, HuntExposedHero) were removed — no AiTaskCategory exists for them yet
    // (see AiTaskCategory's own class comment); re-add once that category is designed rather than
    // scoring goals nothing can act on.
    public static class AiGoalScorer
    {
        // See AiConfig.goalScanRadius for what this means and why — moved there so it's tunable
        // without recompiling.
        private static int ScanRadius => AiConfig.Current.goalScanRadius;

        public static List<AiGoal> ScoreGoals(PlayerSetupData actor)
        {
            var goals = new List<AiGoal>();
            if (actor == null)
                return goals;

            AddIfPositive(goals, ScoreExpandEconomy(actor));
            return goals;
        }

        // Null if nothing scored above 0 — "genuinely nothing worth doing this turn" is a valid
        // outcome, not an error.
        public static AiGoal PickBest(PlayerSetupData actor)
        {
            AiGoal best = null;
            foreach (AiGoal goal in ScoreGoals(actor))
                if (best == null || goal.Score > best.Score)
                    best = goal;
            return best;
        }

        private static void AddIfPositive(List<AiGoal> goals, AiGoal goal)
        {
            if (goal != null && goal.Score > 0f)
                goals.Add(goal);
        }

        // Public — AiTurnController's own unified per-step arbiter re-derives a per-CANDIDATE
        // Economy score (see ScoreExpandEconomyHex) for hexes other than just the single best one
        // ScoreExpandEconomy itself returns, and needs the same own-hexes list to do it without
        // recomputing ArmyRegistry.AllForOwner per candidate.
        public static List<HexCoord> OwnHexes(PlayerSetupData actor) =>
            ArmyRegistry.AllForOwner(actor).Select(a => a.Hex).Distinct().ToList();

        private static int MinDistanceToAny(List<HexCoord> fromHexes, HexCoord target)
        {
            int min = int.MaxValue;
            foreach (HexCoord hex in fromHexes)
                min = Mathf.Min(min, HexGridMath.Distance(hex, target));
            return min;
        }

        // ---- Expand Economy ----

        // Free (no building yet) resource hexes within reach, weighted by proximity — needs at
        // least one of the actor's own heroes somewhere on the map, since only a hero can build
        // an extraction facility (see HexSelectionController.TryBuildExtractionFacility); with
        // no hero at all this goal could never actually be executed, so it doesn't score. Only
        // considers hexes AiMapMemory has actually observed at least once (per doc 2.2.1: "на
        // уже разведанных хексах") — BuildingRegistry.FindAt's own "already claimed" check stays
        // a live, unfiltered lookup on purpose: whether a hex has ANY building on it is world
        // state, not intel about a specific rival, unlike everything AiMapMemory itself guards.
        public static AiGoal ScoreExpandEconomy(PlayerSetupData actor)
        {
            bool hasHero = ArmyRegistry.AllForOwner(actor).Any(a => a.Members.Any(m => m.IsHero));
            if (!hasHero)
                return null;

            List<HexCoord> ownHexes = OwnHexes(actor);
            if (ownHexes.Count == 0)
                return null;

            AiGoal best = null;
            foreach (HexCoord hex in HexResourceBonusRegistry.AllBonusHexes())
            {
                if (!AiMapMemory.IsResourceHexKnown(actor, hex))
                    continue; // never actually scouted — no guessing (see the doc's own principle)
                if (BuildingRegistry.FindAt(hex) != null)
                    continue; // already claimed

                float? hexScore = ScoreExpandEconomyHex(actor, hex, ownHexes);
                if (!hexScore.HasValue)
                    continue;

                if (best == null || hexScore.Value > best.Score)
                {
                    int minDist = MinDistanceToAny(ownHexes, hex);
                    best = new AiGoal
                    {
                        Kind = AiGoalKind.ExpandEconomy,
                        Score = hexScore.Value,
                        TargetHex = hex,
                        Description = $"свободный ресурсный хекс в {minDist} хексах, есть герой для стройки",
                    };
                }
            }

            if (best != null)
                best.Score += IncomeBehindBonus(actor);
            return best;
        }

        // The proximity half of ScoreExpandEconomy's own per-hex formula, pulled out so
        // AiTurnController's unified per-step arbiter can score a SPECIFIC candidate hex (a
        // BuildFacility task's own fixed TargetHex, or one particular free known hex among
        // several TryStartEconomyCandidates is choosing between) rather than only ever getting back
        // ScoreExpandEconomy's single best-of-the-turn pick. Null (not 0) if `hex` is farther
        // than ScanRadius — same "doesn't even count" semantics ScoreExpandEconomy's own loop
        // `continue` already had, not a valid-but-low score. Deliberately excludes
        // IncomeBehindBonus — that's a flat per-actor offset, not per-hex, so callers add it once
        // themselves (see ScoreExpandEconomy's own tail) rather than paying for GameSession.Players
        // enumeration on every hex.
        public static float? ScoreExpandEconomyHex(PlayerSetupData actor, HexCoord hex, List<HexCoord> ownHexes)
        {
            int minDist = MinDistanceToAny(ownHexes, hex);
            return minDist <= ScanRadius ? (ScanRadius + 1 - minDist) * 10f : (float?)null;
        }

        // The doc's own one documented cheat slice for 2.2 Экономика: "сравнивает свой income с
        // остальными игроками (без учёта видимости) и старается не отставать" — compares the
        // actor's own current resource stockpile against the rest of the field (ignoring
        // visibility on purpose, unlike everything else in this file) and boosts ExpandEconomy's
        // urgency the further behind the actor is. A flat stockpile comparison, not a real
        // income-rate calculation — simplest thing that satisfies "старается не отставать"
        // without re-deriving GameTurnController.CollectResourceIncome's own per-turn math here.
        // Public — the unified arbiter (see ScoreExpandEconomyHex's own comment) adds this same
        // flat per-actor offset to every Economy candidate itself, once per Decide() call.
        public static float IncomeBehindBonus(PlayerSetupData actor)
        {
            PlayerRoot ownRoot = PlayerRootRegistry.FindFor(actor);
            if (ownRoot == null || GameSession.Players == null)
                return 0f;

            List<int> otherTotals = GameSession.Players
                .Where(p => p != actor && !p.IsEliminated)
                .Select(PlayerRootRegistry.FindFor)
                .Where(r => r != null)
                .Select(TotalResources)
                .ToList();
            if (otherTotals.Count == 0)
                return 0f;

            float avgOther = (float)otherTotals.Average();
            int ownTotal = TotalResources(ownRoot);
            if (avgOther <= ownTotal)
                return 0f;

            float deficitRatio = Mathf.Clamp01((avgOther - ownTotal) / Mathf.Max(1f, avgOther));
            return deficitRatio * 20f; // same order of magnitude as the per-hex proximity term above
        }

        private static int TotalResources(PlayerRoot root) =>
            root.GetResource(ResourceType.Human) + root.GetResource(ResourceType.Energy)
            + root.GetResource(ResourceType.Materials) + root.GetResource(ResourceType.Tech);
    }
}
