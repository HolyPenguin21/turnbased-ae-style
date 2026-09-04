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
            WorldSnapshot snapshot, ReconMode mode, int turn, ReconAirSortieState sortieState = null,
            AirReconScoringContext scoringCtx = null)
        {
            if (player == null || ctx?.Map == null || airArmy == null || snapshot?.Self == null
                || !AviationRules.IsValidAirArmy(airArmy) || airArmy.CurrentMovement <= 0)
                return null;

            HexMap map = ctx.Map;
            // AI-AIR-01 — form the strategic direction FIRST from landmarks (enemy concentration,
            // Citadel, own facility perimeters, corridors, frontier last). Supersedes the raw
            // ReconDirectionModel enemy-sector read; cheat feeds DIRECTION only.
            AirReconAnchorSet anchors = AirReconAnchorModel.Build(snapshot, player, turn);
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
                AviationSupport.Sortie? sortie =
                    AviationSupport.TryPlanSortiePreferForwardLanding(airArmy, h, map, player);
                AviationSupport.MultiTurnSortie? multi = null;
                if (!sortie.HasValue)
                    multi = AviationSupport.TryPlanMultiTurnSortie(airArmy, h, map, player);
                if (!sortie.HasValue && !multi.HasValue)
                    continue;

                HexCoord landing = sortie?.LandingHex ?? multi.Value.LandingHex;
                int routeCost = sortie?.TotalCost ?? multi.Value.TotalRouteCost;
                int requiredTurns = sortie.HasValue ? 1 : multi.Value.RequiredTurns;
                int unlandedEnds = sortie.HasValue ? 0 : multi.Value.RequiredUnlandedEnds;
                IReadOnlyList<HexCoord> outbound = sortie.HasValue
                    ? sortie.Value.OutboundPath?.Hexes : multi.Value.PathToAction?.Hexes;
                IReadOnlyList<HexCoord> ret = sortie.HasValue
                    ? sortie.Value.ReturnPath?.Hexes : multi.Value.PathFromActionToLanding?.Hexes;
                StepChoice? c = BuildChoice(player, map, mode, turn, airArmy.Hex, h, landing,
                    vision, routeCost, requiredTurns, unlandedEnds, activationAp, activationEnergy,
                    anchors, snapshot, outbound, ret, sortieState, airArmy.Id, scoringCtx);
                if (c.HasValue)
                    choices.Add(c.Value);
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
            AirLaunchCandidate candidate, WorldSnapshot snapshot, ReconMode mode, int turn,
            AirReconScoringContext scoringCtx = null)
        {
            if (player == null || ctx?.Map == null || snapshot?.Self == null
                || candidate.ExistingArmy != null || candidate.Aircraft == null || candidate.Aircraft.Count == 0)
                return null;

            int vision = (ctx.GameConfig != null ? ctx.GameConfig.armyVisionRadius : 0)
                + candidate.Aircraft.Select(AbilityParams.GetBestRecceRadius).DefaultIfEmpty(0).Max();
            float activationAp = candidate.Aircraft.Sum(u => u != null ? u.ActivationApCost : 0);
            float activationEnergy = candidate.Aircraft.Sum(u => u != null ? u.LaunchEnergyCost : 0);
            AirReconAnchorSet anchors = AirReconAnchorModel.Build(snapshot, player, turn);
            var choices = new List<StepChoice>();

            foreach (HexCoord h in HexGridMath.Neighbors(candidate.AirfieldHex))
            {
                if (!ctx.Map.TryGetTerrainAt(h, out _))
                    continue;

                AviationSupport.Sortie? sortie = AviationSupport.TryPlanSortieFromStorage(
                    candidate.AirfieldHex, candidate.Aircraft, h, ctx.Map, player);
                AviationSupport.MultiTurnSortie? multi = null;
                if (!sortie.HasValue)
                    multi = AviationSupport.TryPlanMultiTurnSortieFromStorage(
                        candidate.AirfieldHex, candidate.Aircraft, h, ctx.Map, player);
                if (!sortie.HasValue && !multi.HasValue)
                    continue;

                HexCoord landing = sortie?.LandingHex ?? multi.Value.LandingHex;
                int routeCost = sortie?.TotalCost ?? multi.Value.TotalRouteCost;
                int requiredTurns = sortie.HasValue ? 1 : multi.Value.RequiredTurns;
                int unlandedEnds = sortie.HasValue ? 0 : multi.Value.RequiredUnlandedEnds;
                IReadOnlyList<HexCoord> outbound = sortie.HasValue
                    ? sortie.Value.OutboundPath?.Hexes : multi.Value.PathToAction?.Hexes;
                IReadOnlyList<HexCoord> ret = sortie.HasValue
                    ? sortie.Value.ReturnPath?.Hexes : multi.Value.PathFromActionToLanding?.Hexes;
                StepChoice? c = BuildChoice(player, ctx.Map, mode, turn, candidate.AirfieldHex,
                    h, landing, vision, routeCost, requiredTurns, unlandedEnds, activationAp,
                    activationEnergy, anchors, snapshot, outbound, ret, null, -1, scoringCtx);
                if (c.HasValue)
                    choices.Add(c.Value);
            }

            StepChoice? best = ChooseBest(choices);
            if (best.HasValue)
                AiDebugLog.Write($"[AI][V2][Recon][Air][StorageStep] airfield=({candidate.AirfieldHex.Q},{candidate.AirfieldHex.R}) "
                    + $"aircraft={candidate.Aircraft.Count} mode={mode} to=({best.Value.Hex.Q},{best.Value.Hex.R}) "
                    + $"landing=({best.Value.LandingHex.Q},{best.Value.LandingHex.R}) "
                    + $"score={best.Value.Score:0.00} {best.Value.Reason}");
            return best;
        }

        // AI-AIR-01 — one candidate first step, scored for its PROVEN WHOLE ROUTE via
        // AirReconRouteScorer (destination footprint + route-observation sum + strategic-anchor
        // alignment − travel/activation/recovery/redundancy). Returns null when the scorer rejects
        // the candidate outright (spec §5 hard rules: no strategic value / repeats a recent air
        // observation).
        private static StepChoice? BuildChoice(PlayerSetupData player, HexMap map, ReconMode mode,
            int turn, HexCoord from, HexCoord h, HexCoord landing, int vision,
            int routeCost, int requiredTurns, int requiredUnlandedEnds, float activationAp,
            float activationEnergy, AirReconAnchorSet anchors, WorldSnapshot snapshot,
            IReadOnlyList<HexCoord> outboundHexes, IReadOnlyList<HexCoord> returnHexes,
            ReconAirSortieState sortieState = null, int moverArmyId = -1,
            AirReconScoringContext scoringCtx = null)
        {
            ScoreInformation(player, map, h, vision, turn, out int neverObserved,
                out float staleInformation);

            // R2 review fix — one coverage read, one reference frame. Every assigned Recon actor
            // (air sortie OR ground scout with a live ReconAssignment) is placed in its wedge FROM
            // OUR CITADEL using its LIVE ArmyRegistry position; the candidate's wedge is measured
            // the same way. Idle Recce (no assignment) is not counted. Storage launches get a real
            // count too (moverArmyId -1 simply excludes nobody). R3 review fix — add air slots the
            // reservation prepass reserved earlier this pass but has not launched yet (invisible to
            // the live scan) so a second reserved sortie is not scored as if the first didn't exist.
            HexCoord citadel = snapshot?.Self != null ? snapshot.Self.Citadel : from;
            ReconSector stepSector = ReconDirectionModel.Sector(citadel, h);
            int sectorClaims = CountAssignedReconActorsInWedge(player, citadel, stepSector, moverArmyId)
                + (scoringCtx?.ProvisionalClaimsIn(stepSector) ?? 0);
            // R3 review fix — identity exclusion is EXPLICIT. A caller that passes a scoring
            // context (the reservation prepass) has ALREADY resolved which sortie's own footprint
            // to ignore and sets ExcludeSortieId deliberately (real id for an airborne wing, -1 for
            // a fresh ready/storage launch); trust it verbatim. Only the executor, which owns the
            // live sortieState and passes no context, derives it from that state.
            int excludeSortieId = scoringCtx != null
                ? scoringCtx.ExcludeSortieId
                : sortieState?.SortieId ?? -1;

            var inputs = new AirReconRouteInputs(player, map, mode, turn, from, h, h, landing,
                outboundHexes, returnHexes, vision, routeCost, requiredTurns, requiredUnlandedEnds,
                activationAp, activationEnergy, neverObserved, staleInformation, anchors, snapshot,
                sortieState, sectorClaims, excludeSortieId);
            AirReconRouteCandidate c = AirReconRouteScorer.Score(inputs);
            if (c.Rejected)
            {
                AiDebugLog.Write($"[AI][V2][Recon][Air][Route] actor=#{moverArmyId} "
                    + $"to=({h.Q},{h.R}) DROP — {c.Breakdown}");
                return null;
            }

            float sectorPressure = anchors != null ? anchors.PressureFor(stepSector) : 0f;
            return new StepChoice(h, landing, c.TotalScore, neverObserved, staleInformation,
                sectorPressure, routeCost, requiredTurns, activationAp, activationEnergy, c.Breakdown);
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

        // spec §5 "already adequately covered by another assigned Recon actor" — count every
        // OTHER army that holds a live ReconAssignment (air sortie or ground scout; idle Recce has
        // none) and whose LIVE position falls in `wedge` measured from `citadel`. One registry
        // (ReconAssignmentRegistry, shared by ReconAirExecutor + ReconGroundExecutor), one origin,
        // live ArmyRegistry positions — no snapshot staleness, no mixed reference frames.
        private static int CountAssignedReconActorsInWedge(PlayerSetupData player, HexCoord citadel,
            ReconSector wedge, int excludeArmyId)
        {
            int n = 0;
            foreach (ArmyData a in ArmyRegistry.AllForOwner(player))
            {
                if (a == null || a.Id == excludeArmyId)
                    continue;
                if (!ReconAssignmentRegistry.TryGet(player, a.Id, out _))
                    continue;
                if (ReconDirectionModel.Sector(citadel, a.Hex) == wedge)
                    n++;
            }
            return n;
        }
    }
}
