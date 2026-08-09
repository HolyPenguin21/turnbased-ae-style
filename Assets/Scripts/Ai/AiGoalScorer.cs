using System.Collections.Generic;
using System.Linq;
using Game.Combat;
using Game.Core;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai
{
    // Stage 1 of an AI turn (see the AI architecture design doc): scores every AiGoalKind for
    // `actor` against the CURRENT map state and picks the single best one. Pure read-only logic,
    // same stateless-static style as BattleAi — never mutates ArmyRegistry/BuildingRegistry,
    // only reads them. Turning the winning goal into an actual task chain executed across
    // armies/heroes is a later phase (needs the player-agnostic action API first) — today
    // GameTurnController only logs what was picked, nothing on the map actually happens yet.
    public static class AiGoalScorer
    {
        // How far (in hex steps) from the actor's own territory a threat/opportunity still
        // counts. Deliberately coarse for now — a real reachability check (actual move points,
        // terrain) belongs to task execution, not this first-pass scoring gate. Tune once
        // Combat Worth-It scoring exists to weigh in instead of a flat radius.
        private const int ScanRadius = 4;

        public static List<AiGoal> ScoreGoals(PlayerSetupData actor)
        {
            var goals = new List<AiGoal>();
            if (actor == null)
                return goals;

            AddIfPositive(goals, ScoreDefendBorder(actor));
            AddIfPositive(goals, ScoreExpandEconomy(actor));
            AddIfPositive(goals, ScoreDestroyEnemyCitadel(actor));
            AddIfPositive(goals, ScoreHuntExposedHero(actor));
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

        private static List<HexCoord> OwnHexes(PlayerSetupData actor) =>
            ArmyRegistry.AllForOwner(actor).Select(a => a.Hex).Distinct().ToList();

        private static int MinDistanceToAny(List<HexCoord> fromHexes, HexCoord target)
        {
            int min = int.MaxValue;
            foreach (HexCoord hex in fromHexes)
                min = Mathf.Min(min, HexGridMath.Distance(hex, target));
            return min;
        }

        // ---- Defend Border ----

        // Highest-scoring enemy/neutral army within ScanRadius of any of the actor's own
        // occupied hexes — rewards both proximity (closer = more urgent) and threat size (more
        // members = more dangerous).
        private static AiGoal ScoreDefendBorder(PlayerSetupData actor)
        {
            List<HexCoord> ownHexes = OwnHexes(actor);
            if (ownHexes.Count == 0)
                return null;

            AiGoal best = null;
            foreach (HexCoord threatHex in ArmyRegistry.AllOccupiedHexes())
            {
                foreach (ArmyData threat in ArmyRegistry.AllAt(threatHex))
                {
                    if (threat.Owner == actor || !BattleInitiator.IsEngageable(threat))
                        continue;

                    int minDist = MinDistanceToAny(ownHexes, threatHex);
                    if (minDist > ScanRadius)
                        continue;

                    float proximity = ScanRadius + 1 - minDist;
                    float score = proximity * 15f + threat.Members.Count * 8f;

                    if (best == null || score > best.Score)
                    {
                        best = new AiGoal
                        {
                            Kind = AiGoalKind.DefendBorder,
                            Score = score,
                            TargetHex = threatHex,
                            Description = $"{threat.Name} ({threat.Members.Count} юнита) в {minDist} хексах от своей территории",
                        };
                    }
                }
            }
            return best;
        }

        // ---- Expand Economy ----

        // Free (no building yet) resource hexes within reach, weighted by proximity — needs at
        // least one of the actor's own heroes somewhere on the map, since only a hero can build
        // an extraction facility (see HexSelectionController.TryBuildExtractionFacility); with
        // no hero at all this goal could never actually be executed, so it doesn't score.
        private static AiGoal ScoreExpandEconomy(PlayerSetupData actor)
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
                if (BuildingRegistry.FindAt(hex) != null)
                    continue; // already claimed

                int minDist = MinDistanceToAny(ownHexes, hex);
                if (minDist > ScanRadius)
                    continue;

                float score = (ScanRadius + 1 - minDist) * 10f;
                if (best == null || score > best.Score)
                {
                    best = new AiGoal
                    {
                        Kind = AiGoalKind.ExpandEconomy,
                        Score = score,
                        TargetHex = hex,
                        Description = $"свободный ресурсный хекс в {minDist} хексах, есть герой для стройки",
                    };
                }
            }
            return best;
        }

        // ---- Destroy Enemy Citadel ----

        // Weakest enemy citadel relative to the actor's own total military strength — only
        // scores once the actor is clearly ahead (ratio > 1), since this is the long-game
        // win-condition goal, not a discretionary skirmish; a losing/even matchup should never
        // outscore Defend/Expand. Replace the flat ratio-threshold with real Combat Worth-It
        // scoring once that exists.
        private static AiGoal ScoreDestroyEnemyCitadel(PlayerSetupData actor)
        {
            float ownStrength = ArmyRegistry.AllForOwner(actor).SelectMany(a => a.Members).Where(m => !m.IsHero).Sum(m => m.Attack);
            if (ownStrength <= 0f || GameSession.Players == null)
                return null;

            AiGoal best = null;
            foreach (PlayerSetupData other in GameSession.Players)
            {
                if (other == actor || other.IsEliminated || !other.CitadelHexQ.HasValue || !other.CitadelHexR.HasValue)
                    continue;

                var citadelHex = new HexCoord(other.CitadelHexQ.Value, other.CitadelHexR.Value);
                BuildingData citadel = BuildingRegistry.FindAt(citadelHex);
                float garrisonStrength = ArmyRegistry.AllAt(citadelHex)
                    .Where(a => a.Owner == other)
                    .SelectMany(a => a.Members)
                    .Where(m => !m.IsHero)
                    .Sum(m => m.Defense);
                float defenseStat = citadel != null ? citadel.Defense : 0f;

                float ratio = ownStrength / Mathf.Max(1f, garrisonStrength + defenseStat);
                if (ratio <= 1f)
                    continue;

                float score = (ratio - 1f) * 20f;
                if (best == null || score > best.Score)
                {
                    best = new AiGoal
                    {
                        Kind = AiGoalKind.DestroyEnemyCitadel,
                        Score = score,
                        TargetHex = citadelHex,
                        Description = $"цитадель {other.Nickname} слабее нашей армии (x{ratio:0.0})",
                    };
                }
            }
            return best;
        }

        // ---- Hunt Exposed Hero ----

        // Any enemy army that's hero-only (no rank-and-file units, so it can't fight back in
        // Ground Combat — only a Capture Kill Challenge applies, see
        // BattleInitiator.IsCombatCapable) within reach — an opportunistic, usually-cheap win.
        private static AiGoal ScoreHuntExposedHero(PlayerSetupData actor)
        {
            List<HexCoord> ownHexes = OwnHexes(actor);
            if (ownHexes.Count == 0)
                return null;

            AiGoal best = null;
            foreach (HexCoord hex in ArmyRegistry.AllOccupiedHexes())
            {
                foreach (ArmyData candidate in ArmyRegistry.AllAt(hex))
                {
                    if (candidate.Owner == null || candidate.Owner == actor || candidate.Owner.IsEliminated)
                        continue;
                    if (BattleInitiator.IsCombatCapable(candidate) || !BattleInitiator.IsEngageable(candidate))
                        continue; // either can still fight back (not "exposed"), or nothing there at all

                    int minDist = MinDistanceToAny(ownHexes, hex);
                    if (minDist > ScanRadius)
                        continue;

                    float heroValue = candidate.Members.Sum(m => m.CommandRating + m.Fate);
                    float score = (ScanRadius + 1 - minDist) * 12f + heroValue * 5f;
                    if (best == null || score > best.Score)
                    {
                        best = new AiGoal
                        {
                            Kind = AiGoalKind.HuntExposedHero,
                            Score = score,
                            TargetHex = hex,
                            Description = $"герой {candidate.Owner.Nickname} без охраны в {minDist} хексах",
                        };
                    }
                }
            }
            return best;
        }
    }
}
