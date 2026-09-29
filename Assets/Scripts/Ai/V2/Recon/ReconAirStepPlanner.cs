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
    // and a complete recovery route. Multi-turn sorties are admitted only through the existing fuel
    // simulation, so positive endurance can use multiple turns while zero endurance keeps
    // the same-turn landing invariant. The storage overload is used only to value a card before
    // deployment; Recon missions bind existing armies.
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
            AirReconScoringContext scoringCtx = null, HexCoord? missionFocusHex = null,
            List<string> diagnostics = null)
        {
            if (player == null || ctx?.Map == null || airArmy == null || snapshot?.Self == null
                || !AviationRules.IsValidAirArmy(airArmy) || airArmy.CurrentMovement <= 0)
            {
                diagnostics?.Add($"no_pick(mp={airArmy?.CurrentMovement ?? 0})");
                return null;
            }

            HexMap map = ctx.Map;
            // Form the strategic direction FIRST from landmarks (enemy concentration,
            // Citadel, own facility perimeters, corridors, frontier last). Supersedes the raw
            // ReconDirectionModel enemy-sector read; cheat feeds DIRECTION only.
            // `missionFocusHex` folds the bound Recon mission's target in as one more anchor.
            AirReconAnchorSet anchors = AirReconAnchorModel.Build(snapshot, player, turn, missionFocusHex);
            // Read live composition rather than a turn-start snapshot.
            int vision = (ctx.GameConfig != null ? ctx.GameConfig.armyVisionRadius : 0)
                + AbilityParams.GetBestRecceRadius(airArmy);
            float activationAp = airArmy.HasActivatedThisTurn ? 0f : airArmy.ActivationApCost;
            float activationEnergy = airArmy.HasActivatedThisTurn ? 0f : airArmy.ActivationEnergyCost;
            var choices = new List<StepChoice>();

            foreach (HexCoord h in HexGridMath.Neighbors(airArmy.Hex))
            {
                if (!map.TryGetTerrainAt(h, out _))
                    continue;
                Sortie? sortie =
                    AiAirSortiePlanner.TryPlanSortie(airArmy, h, map, player);
                MultiTurnSortie? multi = null;
                if (!sortie.HasValue)
                    multi = AiAirSortiePlanner.TryPlanMultiTurnSortie(airArmy, h, map, player);
                if (!sortie.HasValue && !multi.HasValue)
                {
                    diagnostics?.Add($"({h.Q},{h.R}):no_safe_sortie");
                    continue;
                }

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
                    anchors, snapshot, outbound, ret, sortieState, airArmy.Id, scoringCtx, diagnostics);
                if (c.HasValue)
                    choices.Add(c.Value);
            }

            bool outboundLeg = sortieState == null || sortieState.Phase == ReconAirPhase.Outbound;
            return ChooseBest(MissionBoundChoices(choices, airArmy.Hex, missionFocusHex, vision,
                outboundLeg, diagnostics));
        }

        // Card valuation only: score the first possible step for aircraft still in storage.
        // This does not create or provision a Recon mission actor.
        public static StepChoice? PickFromStorage(PlayerSetupData player, AiTurnContext ctx,
            HexCoord airfieldHex, IReadOnlyList<UnitData> aircraft, WorldSnapshot snapshot, ReconMode mode, int turn,
            AirReconScoringContext scoringCtx = null, HexCoord? missionFocusHex = null,
            List<string> diagnostics = null)
        {
            if (player == null || ctx?.Map == null || snapshot?.Self == null
                || aircraft == null || aircraft.Count == 0)
            {
                diagnostics?.Add("no_pick(storage)");
                return null;
            }

            int vision = (ctx.GameConfig != null ? ctx.GameConfig.armyVisionRadius : 0)
                + aircraft.Select(AbilityParams.GetBestRecceRadius).DefaultIfEmpty(0).Max();
            float activationAp = aircraft.Sum(u => u != null ? u.ActivationApCost : 0);
            float activationEnergy = aircraft.Sum(u => u != null ? u.LaunchEnergyCost : 0);
            AirReconAnchorSet anchors = AirReconAnchorModel.Build(snapshot, player, turn, missionFocusHex);
            var choices = new List<StepChoice>();

            foreach (HexCoord h in HexGridMath.Neighbors(airfieldHex))
            {
                if (!ctx.Map.TryGetTerrainAt(h, out _))
                    continue;

                Sortie? sortie = AiAirSortiePlanner.TryPlanSortieFromStorage(
                    airfieldHex, aircraft, h, ctx.Map, player);
                MultiTurnSortie? multi = null;
                if (!sortie.HasValue)
                    multi = AiAirSortiePlanner.TryPlanMultiTurnSortieFromStorage(
                        airfieldHex, aircraft, h, ctx.Map, player);
                if (!sortie.HasValue && !multi.HasValue)
                {
                    diagnostics?.Add($"({h.Q},{h.R}):no_safe_sortie");
                    continue;
                }

                HexCoord landing = sortie?.LandingHex ?? multi.Value.LandingHex;
                int routeCost = sortie?.TotalCost ?? multi.Value.TotalRouteCost;
                int requiredTurns = sortie.HasValue ? 1 : multi.Value.RequiredTurns;
                int unlandedEnds = sortie.HasValue ? 0 : multi.Value.RequiredUnlandedEnds;
                IReadOnlyList<HexCoord> outbound = sortie.HasValue
                    ? sortie.Value.OutboundPath?.Hexes : multi.Value.PathToAction?.Hexes;
                IReadOnlyList<HexCoord> ret = sortie.HasValue
                    ? sortie.Value.ReturnPath?.Hexes : multi.Value.PathFromActionToLanding?.Hexes;
                StepChoice? c = BuildChoice(player, ctx.Map, mode, turn, airfieldHex,
                    h, landing, vision, routeCost, requiredTurns, unlandedEnds, activationAp,
                    activationEnergy, anchors, snapshot, outbound, ret, null, -1, scoringCtx, diagnostics);
                if (c.HasValue)
                    choices.Add(c.Value);
            }

            // A launch is always the first Outbound step.
            return ChooseBest(MissionBoundChoices(choices, airfieldHex, missionFocusHex,
                vision, outboundLeg: true, diagnostics: diagnostics));
        }

        // THE "makes genuine progress toward THIS target" rule: the step lands strictly closer to
        // the bound mission target than where the wing stands, OR the target already falls within
        // the resulting vision footprint (the step itself completes the observation).
        // MinimumUsefulScore alone only proves SOME useful step exists somewhere, never that this
        // one serves the mission it is bound to. Pick applies it while choosing, and Assignment /
        // capacity (ReconAssignmentPlanner) re-check the chosen step with this same method.
        internal static bool MakesGenuineProgress(HexCoord from, HexCoord candidateHex,
            HexCoord missionTarget, int vision)
        {
            int before = HexGridMath.Distance(from, missionTarget);
            int after = HexGridMath.Distance(candidateHex, missionTarget);
            return after < before || after <= Math.Max(0, vision);
        }

        // A mission-bound OUTBOUND step is chosen among the steps that serve that mission. Picking
        // the best-scoring step overall and letting Assignment reject it when it was a lateral
        // information grab stranded the wing aloft (flagged "stuck") while a progressing step with
        // a near-equal score existed. Turning (the lateral sweep before home), Hold and Return, or
        // no bound mission, keep the whole set. An empty result means no step serves the mission:
        // the executor turns for home, capacity/Assignment see no candidate.
        private static List<StepChoice> MissionBoundChoices(List<StepChoice> choices, HexCoord from,
            HexCoord? missionFocusHex, int vision, bool outboundLeg, List<string> diagnostics)
        {
            if (!missionFocusHex.HasValue || !outboundLeg || choices == null)
                return choices;
            var kept = new List<StepChoice>(choices.Count);
            foreach (StepChoice c in choices)
            {
                if (MakesGenuineProgress(from, c.Hex, missionFocusHex.Value, vision))
                    kept.Add(c);
                else
                    diagnostics?.Add($"({c.Hex.Q},{c.Hex.R}):no_progress");
            }
            return kept;
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
            AirReconScoringContext scoringCtx = null, List<string> diagnostics = null)
        {
            ScoreInformation(player, map, h, vision, turn, out int neverObserved,
                out float staleInformation);

            // R2 review fix — one coverage read, one reference frame. Every assigned Recon actor
            // (air sortie OR ground scout with a live ReconPatrolState) is placed in its wedge FROM
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
            diagnostics?.Add($"({h.Q},{h.R}):" + (c.Rejected
                ? $"REJECT {c.RejectReason}"
                : $"score={c.TotalScore:0.00}"));
            if (c.Rejected)
                return null;

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
                staleInformation += ReconIntelSnapshotRegistry.Staleness(age);
            }

            if (observed > 0)
                staleInformation /= observed;
        }

        // spec §5 "already adequately covered by another assigned Recon actor" — count every
        // OTHER army that holds a live ReconPatrolState (air sortie or ground scout; idle Recce has
        // none) and whose LIVE position falls in `wedge` measured from `citadel`. One registry
        // (ReconPatrolStateRegistry, shared by ReconAirExecutor + ReconGroundExecutor), one origin,
        // live ArmyRegistry positions — no snapshot staleness, no mixed reference frames.
        private static int CountAssignedReconActorsInWedge(PlayerSetupData player, HexCoord citadel,
            ReconSector wedge, int excludeArmyId)
        {
            int n = 0;
            foreach (ArmyData a in ArmyRegistry.AllForOwner(player))
            {
                if (a == null || a.Id == excludeArmyId)
                    continue;
                if (!ReconPatrolStateRegistry.TryGet(player, a.Id, out _))
                    continue;
                if (ReconDirectionModel.Sector(citadel, a.Hex) == wedge)
                    n++;
            }
            return n;
        }
    }
}
