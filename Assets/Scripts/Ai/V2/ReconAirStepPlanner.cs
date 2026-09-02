using System;
using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.HexGrid;
using Game.Map;
using Game.Players;
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
    // the same-turn boomerang invariant.
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

        // These are tactical ranking weights, not strategic pressure weights. Keeping them local
        // prevents Air Recon from changing Radar economics while this feature is still isolated in
        // ReconOnly. AP/Energy are explicit opportunity costs: an already-activated aircraft pays
        // zero marginal activation cost, while a fresh sortie must earn enough information to
        // justify consuming both turn resources.
        private const float NeverObservedWeight = 1.00f;
        private const float StaleWeight = 0.80f;
        private const float DirectionWeight = 0.65f;
        private const float RouteCostPenalty = 0.10f;
        private const float ExtraTurnPenalty = 0.25f;
        private const float ActivationApPenalty = 0.35f;
        private const float ActivationEnergyPenalty = 0.20f;
        internal const float MinimumUsefulScore = 0.15f;

        public static StepChoice? Pick(PlayerSetupData player, HexMap map, ArmyData airArmy,
            WorldSnapshot snapshot, ReconMode mode, int turn)
        {
            if (player == null || map == null || airArmy == null || snapshot?.Self == null
                || !AviationRules.IsValidAirArmy(airArmy) || airArmy.CurrentMovement <= 0)
                return null;

            ReconDirectionSnapshot direction = ReconDirectionModel.Build(snapshot);
            int vision = EffectiveVision(snapshot, airArmy.Id);
            float activationAp = airArmy.HasActivatedThisTurn ? 0f : airArmy.ActivationApCost;
            float activationEnergy = airArmy.HasActivatedThisTurn ? 0f : airArmy.ActivationEnergyCost;

            var choices = new List<StepChoice>();
            foreach (HexCoord h in HexGridMath.Neighbors(airArmy.Hex))
            {
                if (!map.TryGetTerrainAt(h, out _))
                    continue;

                // Shared aviation layer is the authority for capacity, known-AA safety and return
                // feasibility. Prefer a same-turn boomerang; only then ask the existing multi-turn
                // fuel simulator. No direct TrueWorld/hidden-AA read occurs here.
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

                ScoreInformation(player, map, h, vision, turn, out int neverObserved,
                    out float staleInformation);

                ReconSector sector = ReconDirectionModel.Sector(airArmy.Hex, h);
                float sectorPressure = 0f;
                if (direction?.EnemyDirectionSectors != null)
                    direction.EnemyDirectionSectors.TryGetValue(sector, out sectorPressure);
                if (direction?.KnownEnemyCitadelDirection == sector)
                    sectorPressure = Mathf.Max(sectorPressure, 0.75f);

                // Explore wants genuinely never-observed cells. Refresh primarily wants old honest
                // observations. The secondary term is intentionally small so an air scout may pass
                // through the other mode's useful cells without mode-flapping every hex.
                float information = mode == ReconMode.Explore
                    ? NeverObservedWeight * neverObserved + 0.20f * StaleWeight * staleInformation
                    : StaleWeight * staleInformation + 0.20f * NeverObservedWeight * neverObserved;

                // Diminishing return is intrinsic: current/recently observed cells have age~0 and
                // therefore contribute no stale value; already-known cells also contribute no
                // never-observed value. Repeated flights naturally decay below the useful floor.
                float score = information
                    + DirectionWeight * Mathf.Clamp01(sectorPressure)
                    - RouteCostPenalty * routeCost
                    - ExtraTurnPenalty * Mathf.Max(0, requiredTurns - 1)
                    - ActivationApPenalty * activationAp
                    - ActivationEnergyPenalty * activationEnergy;

                string reason = $"info={information:0.00}(never={neverObserved},stale={staleInformation:0.00}) "
                    + $"dir={sectorPressure:0.00} route={routeCost}/t{requiredTurns} "
                    + $"activation=AP{activationAp:0.#}/E{activationEnergy:0.#}";
                choices.Add(new StepChoice(h, landing, score, neverObserved, staleInformation,
                    sectorPressure, routeCost, requiredTurns, activationAp, activationEnergy, reason));
            }

            if (choices.Count == 0)
                return null;

            StepChoice best = choices
                .OrderByDescending(c => c.Score)
                .ThenByDescending(c => c.NeverObserved)
                .ThenByDescending(c => c.StaleInformation)
                .ThenBy(c => c.RequiredTurns)
                .ThenBy(c => c.RouteCost)
                .ThenBy(c => c.Hex.Q)
                .ThenBy(c => c.Hex.R)
                .First();

            AiDebugLog.Write($"[AI][V2][Recon][Air][Step] actor=#{airArmy.Id} mode={mode} "
                + $"from=({airArmy.Hex.Q},{airArmy.Hex.R}) to=({best.Hex.Q},{best.Hex.R}) "
                + $"landing=({best.LandingHex.Q},{best.LandingHex.R}) score={best.Score:0.00} {best.Reason}");
            return best;
        }

        private static int EffectiveVision(WorldSnapshot snapshot, int armyId)
        {
            ArmySnapshot a = snapshot?.Self?.Armies?.FirstOrDefault(x => x != null && x.ArmyId == armyId);
            return Math.Max(0, a?.EffectiveVisionRadius ?? 0);
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

            // Normalize stale contribution by observed footprint so a large vision radius does not
            // win solely because it has more cells; never-observed count remains a real coverage
            // reward because revealing several new cells in one flight is genuinely more valuable.
            if (observed > 0)
                staleInformation /= observed;
        }
    }
}
