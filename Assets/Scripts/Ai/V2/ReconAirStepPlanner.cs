using System;
using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Ai.V2
{
    // Live V2 Air Recon planner. Like ReconGroundStepPlanner it returns ONE adjacent step only;
    // every forecast is discarded after the authoritative move resolves. Strategic direction is
    // the sanitized six-sector ReconDirectionSnapshot, never exact hidden army data.
    //
    // Safety is deliberately delegated to AiAviationSupport: a voluntary step survives only when
    // the shared aviation layer can prove a complete step -> owned-airfield sortie with capacity
    // and no KNOWN-AA exposure. Multi-turn sorties are admitted only through the existing fuel
    // simulation, so helicopters may use their real TurnsWithoutRefuel margin while planes keep
    // the same-turn boomerang invariant. The storage overload uses the exact matching
    // TryPlan*FromStorage primitives before AviationActions has created an ArmyData.
    internal static class ReconAirStepPlanner
    {
        internal readonly struct StepChoice
        {
            public readonly HexCoord Hex;
            public readonly HexCoord LandingHex;
            public readonly float Score;
            public readonly int NeverObserved;
            public readonly float StaleInformation;
            public readonly float DirectionPressure;
            public readonly int RouteCost;
            public readonly int RequiredTurns;
            public readonly float ActivationAp;
            public readonly float ActivationEnergy;
            public readonly string Reason;

            public StepChoice(HexCoord hex, HexCoord landingHex, float score, int neverObserved,
                float staleInformation, float directionPressure, int routeCost, int requiredTurns,
                float activationAp, float activationEnergy, string reason)
            {
                Hex = hex;
                LandingHex = landingHex;
                Score = score;
                NeverObserved = neverObserved;
                StaleInformation = staleInformation;
                DirectionPressure = directionPressure;
                RouteCost = routeCost;
                RequiredTurns = requiredTurns;
                ActivationAp = activationAp;
                ActivationEnergy = activationEnergy;
                Reason = reason;
            }
        }

        // All step-scoring tunables now live in AiConfigV2 (spec §24). Kept as an alias so callers
        // reading "the threshold that means turn for home / do not launch" have one obvious name.
        internal const float MinimumUsefulScore = AiConfigV2.airReconMinimumUsefulScore;

        public static StepChoice? Pick(PlayerSetupData player, AiTurnContext ctx, ArmyData airArmy,
            WorldSnapshot snapshot, ReconMode mode, int turn, ReconAirSortieState sortieState = null)
        {
            if (player == null || ctx?.Map == null || airArmy == null || snapshot?.Self == null
                || !AviationRules.IsValidAirArmy(airArmy) || airArmy.CurrentMovement <= 0)
                return null;

            HexMap map = ctx.Map;
            ReconDirectionSnapshot direction = ReconDirectionModel.Build(snapshot);
            // Live, not frozen. A wing launched from storage did not exist in the turn-start
            // SelfSnapshot, and a composition-changing aviation rule must be reflected immediately.
            int vision = (ctx.GameConfig != null ? ctx.GameConfig.armyVisionRadius : 0)
                + AbilityParams.GetBestRecceRadius(airArmy);
            float activationAp = airArmy.HasActivatedThisTurn ? 0f : airArmy.ActivationApCost;
            float activationEnergy = airArmy.HasActivatedThisTurn ? 0f : airArmy.ActivationEnergyCost;
            var choices = new List<StepChoice>();

            foreach (HexCoord h in HexGridMath.Neighbors(airArmy.Hex))
            {
                if (!map.TryGetTerrainAt(h, out _))
                    continue;
                AiAviationSupport.Sortie? sortie =
                    AiAviationSupport.TryPlanSortiePreferForwardLanding(airArmy, h, map, player);
                AiAviationSupport.MultiTurnSortie? multi = null;
                if (!sortie.HasValue)
                    multi = AiAviationSupport.TryPlanMultiTurnSortie(airArmy, h, map, player);
                if (!sortie.HasValue && !multi.HasValue)
                    continue;

                HexCoord landing = sortie?.LandingHex ?? multi.Value.LandingHex;
                int routeCost = sortie?.TotalCost ?? multi.Value.TotalRouteCost;
                int requiredTurns = sortie.HasValue ? 1 : multi.Value.RequiredTurns;
                choices.Add(BuildChoice(player, map, mode, turn, airArmy.Hex, h, landing,
                    vision, routeCost, requiredTurns, activationAp, activationEnergy, direction, sortieState));
            }

            StepChoice? best = ChooseBest(choices);
            if (best.HasValue)
                AiDebugLog.Write($"[AI][V2][Recon][Air][Step] actor=#{airArmy.Id} mode={mode} "
                    + $"phase={(sortieState != null ? sortieState.Phase.ToString() : "Outbound")} "
                    + $"from=({airArmy.Hex.Q},{airArmy.Hex.R}) to=({best.Value.Hex.Q},{best.Value.Hex.R}) "
                    + $"landing=({best.Value.LandingHex.Q},{best.Value.LandingHex.R}) "
                    + $"score={best.Value.Score:0.00} {best.Value.Reason}");
            return best;
        }

        // Storage candidate has no ArmyData yet. Score exactly the first adjacent airborne hex,
        // but prove the whole sortie using the storage-aware aviation planners. This keeps launch
        // candidate generation and execution on the same aircraft subset and same AP/Energy basis.
        public static StepChoice? PickFromStorage(PlayerSetupData player, AiTurnContext ctx,
            AirStrikeTask.LaunchCandidate candidate, WorldSnapshot snapshot, ReconMode mode, int turn)
        {
            if (player == null || ctx?.Map == null || snapshot?.Self == null
                || candidate.ExistingArmy != null || candidate.Aircraft == null || candidate.Aircraft.Count == 0)
                return null;

            int vision = (ctx.GameConfig != null ? ctx.GameConfig.armyVisionRadius : 0)
                + candidate.Aircraft.Select(AbilityParams.GetBestRecceRadius).DefaultIfEmpty(0).Max();
            float activationAp = candidate.Aircraft.Sum(u => u != null ? u.ActivationApCost : 0);
            float activationEnergy = candidate.Aircraft.Sum(u => u != null ? u.LaunchEnergyCost : 0);
            ReconDirectionSnapshot direction = ReconDirectionModel.Build(snapshot);
            var choices = new List<StepChoice>();

            foreach (HexCoord h in HexGridMath.Neighbors(candidate.AirfieldHex))
            {
                if (!ctx.Map.TryGetTerrainAt(h, out _))
                    continue;

                AiAviationSupport.Sortie? sortie = AiAviationSupport.TryPlanSortieFromStorage(
                    candidate.AirfieldHex, candidate.Aircraft, h, ctx.Map, player);
                AiAviationSupport.MultiTurnSortie? multi = null;
                if (!sortie.HasValue)
                    multi = AiAviationSupport.TryPlanMultiTurnSortieFromStorage(
                        candidate.AirfieldHex, candidate.Aircraft, h, ctx.Map, player);
                if (!sortie.HasValue && !multi.HasValue)
                    continue;

                HexCoord landing = sortie?.LandingHex ?? multi.Value.LandingHex;
                int routeCost = sortie?.TotalCost ?? multi.Value.TotalRouteCost;
                int requiredTurns = sortie.HasValue ? 1 : multi.Value.RequiredTurns;
                choices.Add(BuildChoice(player, ctx.Map, mode, turn, candidate.AirfieldHex,
                    h, landing, vision, routeCost, requiredTurns, activationAp, activationEnergy, direction));
            }

            StepChoice? best = ChooseBest(choices);
            if (best.HasValue)
                AiDebugLog.Write($"[AI][V2][Recon][Air][StorageStep] airfield=({candidate.AirfieldHex.Q},{candidate.AirfieldHex.R}) "
                    + $"aircraft={candidate.Aircraft.Count} mode={mode} to=({best.Value.Hex.Q},{best.Value.Hex.R}) "
                    + $"landing=({best.Value.LandingHex.Q},{best.Value.LandingHex.R}) "
                    + $"score={best.Value.Score:0.00} {best.Value.Reason}");
            return best;
        }

        private static StepChoice BuildChoice(PlayerSetupData player, HexMap map, ReconMode mode,
            int turn, HexCoord from, HexCoord h, HexCoord landing, int vision,
            int routeCost, int requiredTurns, float activationAp, float activationEnergy,
            ReconDirectionSnapshot direction, ReconAirSortieState sortieState = null)
        {
            ScoreInformation(player, map, h, vision, turn, out int neverObserved,
                out float staleInformation);

            ReconSector sector = ReconDirectionModel.Sector(from, h);
            float sectorPressure = 0f;
            if (direction?.EnemyDirectionSectors != null)
                direction.EnemyDirectionSectors.TryGetValue(sector, out sectorPressure);
            if (direction?.KnownEnemyCitadelDirection == sector)
                sectorPressure = Mathf.Max(sectorPressure, 0.75f);

            float information = mode == ReconMode.Explore
                ? AiConfigV2.airReconNeverObservedWeight * neverObserved
                    + 0.20f * AiConfigV2.airReconStaleWeight * staleInformation
                : AiConfigV2.airReconStaleWeight * staleInformation
                    + 0.20f * AiConfigV2.airReconNeverObservedWeight * neverObserved;

            // Current/recently observed cells have age~0 and no stale value; already-known cells
            // have no never-observed value. Thus repeated flights naturally lose marginal value.
            float score = information
                + AiConfigV2.airReconDirectionWeight * Mathf.Clamp01(sectorPressure)
                - AiConfigV2.airReconRouteCostPenalty * routeCost
                - AiConfigV2.airReconExtraTurnPenalty * Mathf.Max(0, requiredTurns - 1)
                - AiConfigV2.airReconActivationApPenalty * activationAp
                - AiConfigV2.airReconActivationEnergyPenalty * activationEnergy;

            // Boomerang shaping (spec §33 / §48) — Outbound and the Turning pivot only, always
            // subordinate to the shared aviation safety filter that already vetted every candidate
            // here. Discourage hugging the way out; nudge toward an informative sideways sweep
            // instead of a pure radial out-and-back. The pivot step leans on this hardest — that is
            // what makes Turning a real lateral bend rather than an instant U-turn.
            int trailOverlap = 0;
            bool lateral = false;
            if (sortieState != null
                && (sortieState.Phase == ReconAirPhase.Outbound || sortieState.Phase == ReconAirPhase.Turning))
            {
                float lateralWeight = sortieState.Phase == ReconAirPhase.Turning ? 2f : 1f;
                trailOverlap = sortieState.TrailAdjacency(h);
                score -= AiConfigV2.airReconOutboundTrailOverlapPenalty * trailOverlap;
                lateral = information > 0.01f
                    && HexGridMath.Distance(sortieState.LaunchHex, h) <= HexGridMath.Distance(sortieState.LaunchHex, from);
                if (lateral)
                    score += lateralWeight * AiConfigV2.airReconLateralNoveltyBonus;
            }

            string reason = $"info={information:0.00}(never={neverObserved},stale={staleInformation:0.00}) "
                + $"dir={sectorPressure:0.00} route={routeCost}/t{requiredTurns} "
                + $"activation=AP{activationAp:0.#}/E{activationEnergy:0.#} "
                + $"trailOverlap={trailOverlap} lateral={(lateral ? 1 : 0)}";
            return new StepChoice(h, landing, score, neverObserved, staleInformation,
                sectorPressure, routeCost, requiredTurns, activationAp, activationEnergy, reason);
        }

        private static StepChoice? ChooseBest(List<StepChoice> choices)
        {
            if (choices == null || choices.Count == 0)
                return null;
            return choices
                .OrderByDescending(c => c.Score)
                .ThenByDescending(c => c.NeverObserved)
                .ThenByDescending(c => c.StaleInformation)
                .ThenBy(c => c.RequiredTurns)
                .ThenBy(c => c.RouteCost)
                .ThenBy(c => c.Hex.Q)
                .ThenBy(c => c.Hex.R)
                .First();
        }

        private static void ScoreInformation(PlayerSetupData player, HexMap map, HexCoord center,
            int vision, int turn, out int neverObserved, out float staleInformation)
        {
            neverObserved = 0;
            staleInformation = 0f;
            int observed = 0;

            foreach (HexCoord h in HexGridMath.HexesInRange(center, Math.Max(0, vision)))
            {
                if (!map.TryGetTerrainAt(h, out _))
                    continue;
                if (!AiReconIntelMemory.TryGetIntelAge(player, h, turn, out int age))
                {
                    neverObserved++;
                    continue;
                }

                observed++;
                staleInformation += Mathf.InverseLerp(AiConfigV2.scoutSurveilStaleTurnsLo,
                    AiConfigV2.scoutSurveilStaleTurnsHi, age);
            }

            if (observed > 0)
                staleInformation /= observed;
        }
    }
}
