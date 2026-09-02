using System;
using System.Collections.Generic;
using System.Linq;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // LIVE tactical planner for one ground Recon step. It never returns a multi-hex route: bounded
    // lookahead only ranks the FIRST adjacent step, and is thrown away after that movement resolves.
    // Tactical safety reads current VisionSystem / honest AiMapMemory / stealth state, never
    // TrueWorld. The frozen strategic assignment contributes only mode + coarse heading.
    internal static class ReconGroundStepPlanner
    {
        internal readonly struct StepChoice
        {
            public readonly HexCoord Hex;
            public readonly float Score;
            public readonly int FreshNeighbors;
            public readonly int MoveCost;
            public readonly int IntelAge;
            public readonly float TrailFactor;
            public readonly float DetectorRisk;
            public readonly string Reason;

            public StepChoice(HexCoord hex, float score, int freshNeighbors, int moveCost,
                int intelAge, float trailFactor, float detectorRisk, string reason)
            {
                Hex = hex;
                Score = score;
                FreshNeighbors = freshNeighbors;
                MoveCost = moveCost;
                IntelAge = intelAge;
                TrailFactor = trailFactor;
                DetectorRisk = detectorRisk;
                Reason = reason;
            }
        }

        public static StepChoice? Pick(PlayerSetupData player, HexMap map, ArmyData army,
            ReconAssignment assignment, int turn)
        {
            if (player == null || map == null || army == null || assignment == null
                || army.CurrentMovement <= 0)
                return null;

            int depth = Math.Max(2, Math.Min(4, army.CurrentMovement));
            var choices = new List<StepChoice>();
            foreach (HexCoord h in HexGridMath.Neighbors(army.Hex))
            {
                if (!TryScoreImmediate(player, map, army, assignment, turn, h, out StepChoice baseChoice))
                    continue;

                // Small bounded forecast: value unique useful continuation, but return only h.
                // The forecast intentionally ignores hypothetical discoveries; after the real
                // transition this whole score is discarded and Pick() is called on LIVE state.
                var seen = new HashSet<HexCoord> { army.Hex, h };
                float lookahead = Lookahead(player, map, army, assignment, turn, h,
                    depth - 1, army.CurrentMovement - baseChoice.MoveCost, seen);
                float score = baseChoice.Score + lookahead;
                choices.Add(new StepChoice(baseChoice.Hex, score, baseChoice.FreshNeighbors,
                    baseChoice.MoveCost, baseChoice.IntelAge, baseChoice.TrailFactor,
                    baseChoice.DetectorRisk, baseChoice.Reason + $" lookahead={lookahead:0.00}"));
            }

            if (choices.Count == 0)
                return null;

            StepChoice best = choices
                .OrderByDescending(c => c.Score)
                .ThenByDescending(c => c.FreshNeighbors)
                .ThenBy(c => c.MoveCost)
                .ThenBy(c => c.Hex.Q)
                .ThenBy(c => c.Hex.R)
                .First();

            AiDebugLog.Write($"[AI][V2][Recon][Ground][Step] actor=#{army.Id} mode={assignment.Mode} "
                + $"sector={assignment.StrategicSector} from=({army.Hex.Q},{army.Hex.R}) "
                + $"to=({best.Hex.Q},{best.Hex.R}) freshNeighbors={best.FreshNeighbors} "
                + $"intelAge={(best.IntelAge >= 0 ? best.IntelAge.ToString() : "never")} "
                + $"moveCost={best.MoveCost} trail={best.TrailFactor:0.00} "
                + $"detectorRisk={best.DetectorRisk:0.00} score={best.Score:0.00} {best.Reason}");
            return best;
        }

        private static bool TryScoreImmediate(PlayerSetupData player, HexMap map, ArmyData army,
            ReconAssignment assignment, int turn, HexCoord h, out StepChoice choice)
        {
            choice = default;
            if (!map.TryGetTerrainAt(h, out var terrain))
                return false;

            int moveCost = terrain != null ? Math.Max(1, terrain.moveCost) : 1;
            if (moveCost > army.CurrentMovement)
                return false;
            if (AiMapMemory.IsScoutDangerous(player, h)
                || ScoutExecutionSafety.VantageBlockedNow(player, h, turn))
                return false;

            // Owner-facing stealth state. We deliberately do not ask whether some enemy has
            // personally detected the scout here: that is observer-specific reaction state, not
            // a property of whether this actor is itself currently in stealth.
            bool hidden = army.Members.Count > 0 && army.Members.All(m => m.IsHidden);
            ArmyData visibleOccupant = VisionSystem.IsVisible(player, h)
                ? BattleInitiator.FindEnemyAt(h, player)
                : null;
            if (visibleOccupant != null && BattleInitiator.CanInitiateContact(army))
                return false;

            AiMapMemory.KnownEnemySighting? remembered = AiMapMemory.KnownEnemySightingAt(player, h);
            if (remembered.HasValue && !hidden)
                return false;

            float detectorRisk = DetectorRisk(player, h);
            if (hidden && detectorRisk >= 1f)
                return false;

            bool visited = VisionSystem.IsVisited(player, h);
            int fresh = FreshNeighborCount(player, map, h);
            int intelAge = AiReconIntelMemory.TryGetIntelAge(player, h, turn, out int age) ? age : -1;

            float information;
            if (assignment.Mode == ReconMode.Explore)
            {
                // Never-observed / unvisited ground dominates Explore; revisiting is legal only as
                // connective tissue and starts with much lower value.
                information = (visited ? 0f : 1f)
                    + Mathf.Clamp01(fresh / Math.Max(1f, AiConfigV2.scoutInfoGainNorm));
            }
            else
            {
                // Refresh only values actually-known information age. Never-observed is Explore,
                // not a fake 999-turn Refresh target.
                float stale = intelAge < 0 ? 0f
                    : Mathf.InverseLerp(AiConfigV2.scoutSurveilStaleTurnsLo,
                        AiConfigV2.scoutSurveilStaleTurnsHi, intelAge);
                information = stale + AiConfigV2.scoutStepRefreshFreshNeighborWeight
                    * Mathf.Clamp01(fresh / Math.Max(1f, AiConfigV2.scoutInfoGainNorm));
            }

            ReconSector stepSector = ReconDirectionModel.Sector(army.Hex, h);
            float heading = stepSector == assignment.StrategicSector ? 1f : 0f;
            float movementEfficiency = 1f / moveCost;
            float trailFactor = TrailFactor(player, army.Id, h, visited, assignment.Mode);
            float safetyFactor = Mathf.Clamp01(1f - AiConfigV2.scoutDetectionRiskSelectionPenalty * detectorRisk);

            // Multi-scout deconfliction is a preference, never a hard reservation. A second/third
            // scout may still enter the same corridor when terrain/safety leaves no better option,
            // but equal candidates in unclaimed sectors win. Nearby strategic anchors are weighted
            // more strongly than merely sharing a broad six-way sector.
            int sectorClaims = ReconAssignmentRegistry.OtherSectorClaims(player, army.Id, stepSector);
            int nearbyClaims = ReconAssignmentRegistry.OtherNearbyAnchorClaims(player, army.Id, h,
                Math.Max(1, AiConfigV2.scoutTargetMinSeparation));
            float coverageFactor = 1f / (1f + AiConfigV2.scoutStepCoverageSectorWeight * sectorClaims
                + AiConfigV2.scoutStepCoverageNearbyWeight * nearbyClaims);

            // Explore should not willingly terminate in a zero-frontier pocket when another step
            // with comparable value can keep the wave moving. Refresh is allowed to visit a stale
            // dead-end because the information itself may be the objective.
            float deadEndFactor = assignment.Mode == ReconMode.Explore && !visited && fresh == 0
                ? AiConfigV2.scoutStepDeadEndFactor
                : 1f;

            // §13 / §20 — a foreign undefended Facility/Base directly on this adjacent step is a
            // local opportunity: add a flat bonus so a scout ALREADY next to it bends on. This is
            // deliberately only in the immediate step, never in Lookahead, so it can never pull a
            // scout across the map toward a distant structure.
            float buildingBonus = ReconReactionPolicy.IsUndefendedForeignStructureAt(player, h)
                ? AiConfigV2.scoutStepUndefendedBuildingBonus
                : 0f;

            float score = (information + heading + movementEfficiency + buildingBonus)
                * trailFactor * safetyFactor * coverageFactor * deadEndFactor;
            string reason = $"info={information:0.00} heading={heading:0.00} mpEff={movementEfficiency:0.00} "
                + $"building={buildingBonus:0.00} "
                + $"coverage={coverageFactor:0.00}(sectorClaims={sectorClaims},near={nearbyClaims}) "
                + $"deadEnd={deadEndFactor:0.00}";
            choice = new StepChoice(h, score, fresh, moveCost, intelAge, trailFactor, detectorRisk, reason);
            return true;
        }

        private static float Lookahead(PlayerSetupData player, HexMap map, ArmyData army,
            ReconAssignment assignment, int turn, HexCoord from, int depth, int movementLeft,
            HashSet<HexCoord> seen)
        {
            if (depth <= 0 || movementLeft <= 0)
                return 0f;

            bool hidden = army.Members.Count > 0 && army.Members.All(m => m.IsHidden);
            float best = 0f;
            foreach (HexCoord h in HexGridMath.Neighbors(from))
            {
                if (seen.Contains(h) || !map.TryGetTerrainAt(h, out var terrain))
                    continue;
                int cost = terrain != null ? Math.Max(1, terrain.moveCost) : 1;
                if (cost > movementLeft
                    || AiMapMemory.IsScoutDangerous(player, h)
                    || ScoutExecutionSafety.VantageBlockedNow(player, h, turn))
                    continue;
                if (!hidden && AiMapMemory.KnownEnemySightingAt(player, h).HasValue)
                    continue;
                float detectorRisk = DetectorRisk(player, h);
                if (hidden && detectorRisk >= 1f)
                    continue;

                bool visited = VisionSystem.IsVisited(player, h);
                int fresh = FreshNeighborCount(player, map, h);
                float local;
                if (assignment.Mode == ReconMode.Explore)
                    local = (visited ? 0f : 1f)
                        + Mathf.Clamp01(fresh / Math.Max(1f, AiConfigV2.scoutInfoGainNorm));
                else
                    local = AiReconIntelMemory.TryGetIntelAge(player, h, turn, out int age)
                        ? Mathf.InverseLerp(AiConfigV2.scoutSurveilStaleTurnsLo,
                            AiConfigV2.scoutSurveilStaleTurnsHi, age)
                        : 0f;

                int nearbyClaims = ReconAssignmentRegistry.OtherNearbyAnchorClaims(player, army.Id, h,
                    Math.Max(1, AiConfigV2.scoutTargetMinSeparation));
                local *= 1f / (1f + AiConfigV2.scoutLookaheadNearbyClaimWeight * nearbyClaims);
                local *= Mathf.Clamp01(1f - AiConfigV2.scoutDetectionRiskSelectionPenalty * detectorRisk);

                seen.Add(h);
                float continuation = Lookahead(player, map, army, assignment, turn, h,
                    depth - 1, movementLeft - cost, seen);
                seen.Remove(h);

                // Each future layer is deliberately discounted by its depth through division;
                // bounded lookahead prevents a locally-attractive dead end without becoming a
                // cached route the executor could follow after new information appears.
                best = Math.Max(best, (local + continuation) / (depth + 1f));
            }
            return best;
        }

        private static float TrailFactor(PlayerSetupData player, int armyId, HexCoord h,
            bool visited, ReconMode mode)
        {
            float factor = 1f;
            if (ScoutTrailRegistry.IsImmediateReversal(player, armyId, h))
                factor *= AiConfigV2.scoutImmediateReversalFactor;
            int recent = ScoutTrailRegistry.RecentTrailHits(player, armyId, new[] { h });
            if (recent > 0)
                factor *= 1f / (1f + AiConfigV2.scoutRecentTrailPenaltyPerHex * recent);
            if (visited && mode == ReconMode.Explore)
                factor *= AiConfigV2.scoutExploredRouteFloor;
            return factor;
        }

        private static float DetectorRisk(PlayerSetupData player, HexCoord h)
        {
            int detectors = 0;
            foreach (AiMapMemory.KnownEnemySighting sighting in
                     AiMapMemory.KnownEnemySightingsNear(player, new[] { h }, AiConfigV2.frontierEnemyExposureRadius))
            {
                if (sighting.Owner == null || sighting.Owner.IsNeutral)
                    continue;
                if (sighting.CanDetectStealthAt(h))
                    detectors++;
            }
            return Mathf.Clamp01(detectors / Math.Max(1f, AiConfigV2.scoutDetectionRiskNorm));
        }

        private static int FreshNeighborCount(PlayerSetupData player, HexMap map, HexCoord center)
        {
            int count = 0;
            foreach (HexCoord n in HexGridMath.Neighbors(center))
                if (map.TryGetTerrainAt(n, out _)
                    && !VisionSystem.IsVisited(player, n)
                    && !AiMapMemory.IsScoutDangerous(player, n))
                    count++;
            return count;
        }
    }
}
