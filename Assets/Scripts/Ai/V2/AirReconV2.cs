using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  AIR RECON — RECON-ONLY FALLBACK
    // ===========================================================================================
    //  Ground Recon remains primary. This phase runs after the ordinary V2 mission batch and may
    //  consume only resources that are physically still available.
    //
    //  First V2 boundary:
    //    · same-turn sorties only: airfield -> information objective -> safe owned airfield;
    //    · the complete boomerang must fit CURRENT movement, not theoretical max movement;
    //    · every transition is one hex through AiTurnController.MoveArmyRoutine;
    //    · after every hex the complete remainder is re-planned against newly-known AA/capacity;
    //    · if forward safety is lost, the aircraft turns for the best currently reachable airfield;
    //    · observation refreshes AiReconIntelMemory but must never mark ground VisionSystem.Visited;
    //    · recently-flown targets are suppressed to create explicit diminishing returns.
    //
    //  Multi-turn helicopter sorties intentionally stay outside this first pass. They need a V2
    //  durable aviation assignment + landing reservation. Reusing V1 AiTaskRegistry for persistence
    //  would create a second state-ownership model inside Strategy V2.
    // ===========================================================================================
    internal static class AirReconV2
    {
        private const float ApOpportunityWeight = 6f;
        private const float EnergyOpportunityWeight = 3f;
        private const float DirectionWeight = 12f;
        private const float MinimumNetValue = 8f;
        private const int MaxSortiesPerTurn = 1;

        private sealed class Candidate
        {
            public ReconObjective Objective;
            public ArmyData ExistingArmy;
            public HexCoord LaunchHex;
            public List<UnitData> StoredAircraft;
            public AiAviationSupport.Sortie Sortie;
            public int ActivationAp;
            public int ActivationEnergy;
            public float InformationValue;
            public float OpportunityCost;
            public float NetValue;
        }

        public static IEnumerator RunFallback(WorldSnapshot snapshot, PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx, System.Action<bool> changed = null)
        {
            changed?.Invoke(false);
            if (!AiStrategyV2Scope.IsReconOnly || snapshot?.Self == null || player == null
                || root == null || ctx?.Map == null)
                yield break;

            int executed = 0;
            while (executed < MaxSortiesPerTurn)
            {
                Candidate plan = PickBest(snapshot, player, root, ctx);
                if (plan == null)
                {
                    if (executed == 0)
                        AiDebugLog.Write("[AI][V2][Recon][Air] no worthwhile safe same-turn sortie");
                    yield break;
                }

                AiDebugLog.Write($"[AI][V2][Recon][Air] select {plan.Objective.Kind} "
                    + $"focus=({plan.Objective.FocusHex.Q},{plan.Objective.FocusHex.R}) "
                    + $"launch=({plan.LaunchHex.Q},{plan.LaunchHex.R}) "
                    + $"landing=({plan.Sortie.LandingHex.Q},{plan.Sortie.LandingHex.R}) "
                    + $"info={plan.InformationValue:0.0} cost={plan.OpportunityCost:0.0} "
                    + $"net={plan.NetValue:0.0} ap={plan.ActivationAp} energy={plan.ActivationEnergy} "
                    + $"route={plan.Sortie.TotalCost}");

                bool sortieChanged = false;
                yield return Execute(player, ctx, plan, v => sortieChanged |= v);
                if (!sortieChanged)
                    yield break;

                executed++;
                changed?.Invoke(true);
            }
        }

        private static Candidate PickBest(WorldSnapshot snapshot, PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx)
        {
            IReadOnlyList<ReconObjective> objectives = ReconObjectiveEvaluator.Enumerate(snapshot);
            if (objectives == null || objectives.Count == 0)
                return null;

            ReconDirectionSnapshot directions = ReconDirectionModel.Build(snapshot);
            Candidate best = null;
            foreach (ReconObjective objective in objectives)
            {
                if (objective == null || VisionSystem.IsVisible(player, objective.FocusHex)
                    || ObservedThisTurn(player, objective.FocusHex, ctx.TurnNumber))
                    continue;

                // Air is fallback, not a replacement for a cheap ground hop. Surveil is observation
                // semantics and may compete even when a ground scout can physically approach it.
                if (objective.Kind != ReconObjectiveKind.Surveil)
                {
                    var ground = ScoutRouteCostEvaluator.Evaluate(snapshot, objective.ToTarget());
                    if (ground.HasRoute && ground.EtaTurns <= 1)
                        continue;
                }

                bool recentlyFlown = AiMapMemory.WasAirReconnedWithin(player, objective.FocusHex,
                    ctx.TurnNumber, AiConfig.airReconTargetCooldownTurns);
                if (recentlyFlown && objective.Kind != ReconObjectiveKind.Surveil)
                    continue;

                float direction = DirectionPressure(snapshot, directions, objective.FocusHex);
                float infoValue = objective.BaseValue + direction * DirectionWeight;
                Candidate candidate = BestVehicleFor(player, root, ctx, objective, infoValue);
                if (candidate == null || candidate.NetValue < MinimumNetValue)
                    continue;

                if (best == null || candidate.NetValue > best.NetValue
                    || (Mathf.Approximately(candidate.NetValue, best.NetValue)
                        && candidate.Sortie.TotalCost < best.Sortie.TotalCost))
                    best = candidate;
            }
            return best;
        }

        private static Candidate BestVehicleFor(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ReconObjective objective, float informationValue)
        {
            Candidate best = null;
            int freeEnergy = AiResourceReservation.Available(root, player, ResourceType.Energy);

            // Existing air formations are eligible only while sitting on an owned airfield and not
            // owned by a legacy V1 task. A theoretical sortie that does not fit CURRENT movement is
            // rejected even if the shared planner can prove it against max movement.
            foreach (ArmyData army in ArmyRegistry.AllForOwner(player))
            {
                if (!AviationRules.IsValidAirArmy(army) || army.CurrentMovement <= 0
                    || !AviationRules.IsOwnedAirfieldAt(army.Hex, player)
                    || AiTaskRegistry.TaskFor(player, army) != null)
                    continue;

                AiAviationSupport.Sortie? sortie =
                    AiAviationSupport.TryPlanSortie(army, objective.FocusHex, ctx.Map, player);
                if (!sortie.HasValue || sortie.Value.TotalCost > army.CurrentMovement)
                    continue;

                int ap = army.HasActivatedThisTurn ? 0 : army.ActivationApCost;
                int energy = army.HasActivatedThisTurn ? 0 : army.ActivationEnergyCost;
                if (!root.CanSpendActionPoints(ap) || freeEnergy < energy)
                    continue;

                Candidate c = MakeCandidate(objective, army, army.Hex, null, sortie.Value,
                    ap, energy, informationValue, root.ActionPoints, freeEnergy);
                if (best == null || c.NetValue > best.NetValue)
                    best = c;
            }

            // Stored aircraft use V1's authoritative launch-group rule. Pre-filter with the minimum
            // effective current movement of the selected group; launch is then re-proved once the
            // actual ArmyData exists, before any paid movement occurs.
            foreach (HexCoord hex in AiAviationSupport.OwnedAirfieldHexes(player))
            {
                ArmyData airfield = AviationRules.FindAirfieldAt(hex, player);
                if (airfield == null || airfield.Members.Count < AiConfig.aviationLaunchMinReadyAircraft)
                    continue;

                List<UnitData> aircraft = airfield.Members.Where(AviationRules.IsAviation).ToList();
                if (aircraft.Count < AiConfig.aviationLaunchMinReadyAircraft
                    || !AiAviationSupport.CanAffordLaunch(root, player, aircraft))
                    continue;

                AiAviationSupport.Sortie? sortie = AiAviationSupport.TryPlanSortieFromStorage(
                    hex, aircraft, objective.FocusHex, ctx.Map, player);
                if (!sortie.HasValue)
                    continue;

                int currentMove = aircraft.Min(AviationRules.EffectiveMoveCurrent);
                if (currentMove <= 0 || sortie.Value.TotalCost > currentMove)
                    continue;

                int ap = aircraft.Sum(u => u.ActivationApCost);
                int energy = aircraft.Sum(u => u.LaunchEnergyCost);
                Candidate c = MakeCandidate(objective, null, hex, aircraft, sortie.Value,
                    ap, energy, informationValue, root.ActionPoints, freeEnergy);
                if (best == null || c.NetValue > best.NetValue)
                    best = c;
            }
            return best;
        }

        private static Candidate MakeCandidate(ReconObjective objective, ArmyData existing,
            HexCoord launchHex, List<UnitData> stored, AiAviationSupport.Sortie sortie,
            int ap, int energy, float informationValue, int apAvailable, int energyAvailable)
        {
            float apScarcity = apAvailable > 0 ? ap / (float)apAvailable : (ap > 0 ? 1f : 0f);
            float energyScarcity = energyAvailable > 0
                ? energy / (float)energyAvailable : (energy > 0 ? 1f : 0f);
            float opportunity = ap * ApOpportunityWeight + energy * EnergyOpportunityWeight
                + sortie.TotalCost * AiConfig.airReconDistancePenalty
                + apScarcity * 8f + energyScarcity * 8f;

            return new Candidate
            {
                Objective = objective,
                ExistingArmy = existing,
                LaunchHex = launchHex,
                StoredAircraft = stored,
                Sortie = sortie,
                ActivationAp = ap,
                ActivationEnergy = energy,
                InformationValue = informationValue,
                OpportunityCost = opportunity,
                NetValue = informationValue - opportunity,
            };
        }

        private static IEnumerator Execute(PlayerSetupData player, AiTurnContext ctx,
            Candidate plan, System.Action<bool> changed)
        {
            bool visitedBefore = VisionSystem.IsVisited(player, plan.Objective.FocusHex);
            ArmyData airArmy = plan.ExistingArmy;
            bool launchedFromStorage = false;

            if (airArmy == null)
            {
                ArmyData airfield = AviationRules.FindAirfieldAt(plan.LaunchHex, player);
                if (airfield == null || plan.StoredAircraft == null || plan.StoredAircraft.Count == 0
                    || plan.StoredAircraft.Any(u => !airfield.Members.Contains(u)))
                {
                    AiDebugLog.Write("[AI][V2][Recon][Air] cancel before launch — stored group changed");
                    yield break;
                }

                bool launched = AviationActions.TryLaunch(airfield, plan.StoredAircraft.ToList(),
                    ctx.StartingDeckCatalog?.GetCatalog(player.Faction), ctx.HexSelection,
                    out airArmy, out string failReason);
                if (!launched || airArmy == null)
                {
                    AiDebugLog.Write($"[AI][V2][Recon][Air] launch rejected — {failReason}");
                    yield break;
                }
                launchedFromStorage = true;
                // TryLaunch is provisional here. Do not report lasting state-change until a paid
                // aviation step succeeds; a failed preflight is rolled back to the airfield below.
            }

            AiAviationSupport.Sortie? live =
                AiAviationSupport.TryPlanSortie(airArmy, plan.Objective.FocusHex, ctx.Map, player);
            if (!live.HasValue || live.Value.TotalCost > airArmy.CurrentMovement)
            {
                AiDebugLog.Write("[AI][V2][Recon][Air] cancel — safe same-turn outbound+return no longer fits current movement before first step");
                if (launchedFromStorage)
                    ReturnUnmovedLaunchToStorage(airArmy, player, ctx);
                yield break;
            }

            HexCoord landing = live.Value.LandingHex;
            bool outbound = true;
            bool observed = false;
            int guard = Mathf.Max(2, airArmy.CurrentMovement + 2);
            int steps = 0;

            AiDebugLog.Write($"[AI][V2][Recon][Air] launch/continue \"{airArmy.Name}\" "
                + $"target=({plan.Objective.FocusHex.Q},{plan.Objective.FocusHex.R}) "
                + $"landing=({landing.Q},{landing.R}) movement={airArmy.CurrentMovement}");

            while (guard-- > 0 && airArmy != null && airArmy.CurrentMovement > 0)
            {
                AiReconIntelMemory.ObserveCurrentVisibility(player, ctx.TurnNumber);
                if (!observed && ObservedThisTurn(player, plan.Objective.FocusHex, ctx.TurnNumber))
                {
                    observed = true;
                    outbound = false;
                    AiDebugLog.Write($"[AI][V2][Recon][Air] objective observed "
                        + $"({plan.Objective.FocusHex.Q},{plan.Objective.FocusHex.R}); turning home");
                }

                HexCoord? next = null;
                if (outbound)
                {
                    // Re-prove the COMPLETE remaining boomerang every hex. Newly discovered known AA,
                    // landing-capacity changes, or spent movement can invalidate forward progress.
                    live = AiAviationSupport.TryPlanSortie(airArmy,
                        plan.Objective.FocusHex, ctx.Map, player);
                    if (!live.HasValue || live.Value.TotalCost > airArmy.CurrentMovement)
                    {
                        outbound = false;
                        AiDebugLog.Write("[AI][V2][Recon][Air] forward plan invalidated (AA/landing/current movement); emergency return");
                        continue;
                    }
                    landing = live.Value.LandingHex;
                    if (live.Value.OutboundPath?.Hexes != null && live.Value.OutboundPath.Hexes.Count > 1)
                        next = live.Value.OutboundPath.Hexes[1];
                    else
                    {
                        outbound = false;
                        continue;
                    }
                }
                else
                {
                    HexCoord? replannedLanding = AiAviationSupport.TryReplan(airArmy, ctx.Map, player);
                    if (!replannedLanding.HasValue)
                    {
                        AiDebugLog.Write("[AI][V2][Recon][Air] return blocked — no owned airfield reachable with current movement; hold");
                        break;
                    }
                    landing = replannedLanding.Value;
                    if (airArmy.Hex.Equals(landing))
                    {
                        AviationActions.LandInSlotOrder(airArmy, ctx.HexSelection);
                        break;
                    }

                    HexPath ret = HexPathfinder.FindPath(ctx.Map, airArmy.Hex, landing, flatCost: true);
                    int returnSteps = ret?.Hexes != null ? ret.Hexes.Count - 1 : int.MaxValue;
                    if (ret?.Hexes == null || ret.Hexes.Count < 2 || returnSteps > airArmy.CurrentMovement)
                    {
                        AiDebugLog.Write("[AI][V2][Recon][Air] return replan is not affordable with current movement; hold");
                        break;
                    }
                    next = ret.Hexes[1];
                }

                if (!next.HasValue)
                    break;

                bool wasOutboundStep = outbound;
                HexCoord before = airArmy.Hex;
                var decision = AiDecision.Move(airArmy, next.Value,
                    wasOutboundStep ? "V2 AirRecon — one-hex information step" : "V2 AirRecon — one-hex safe return",
                    null, 0f, AiTaskCategory.Reconnaissance);
                var trace = new AiMoveExecutionTrace();
                yield return AiTurnController.MoveArmyRoutine(player, decision, ctx, trace);

                // MoveArmyRoutine mutates the registered ArmyData in place. Treat disappearance from
                // the owner's registry as authoritative loss instead of depending on an army-id API.
                bool stillRegistered = ArmyRegistry.AllForOwner(player)
                    .Any(a => ReferenceEquals(a, airArmy));
                if (!stillRegistered || !AviationRules.IsValidAirArmy(airArmy))
                {
                    changed?.Invoke(true);
                    AiDebugLog.Write("[AI][V2][Recon][Air] mover lost during authoritative aviation step");
                    yield break;
                }

                if (airArmy.Hex.Equals(before))
                {
                    AiDebugLog.Write("[AI][V2][Recon][Air] move rejected; stopping sortie executor");
                    break;
                }

                steps++;
                changed?.Invoke(true);
                AiReconIntelMemory.ObserveCurrentVisibility(player, ctx.TurnNumber);
                if (wasOutboundStep)
                    AiMapMemory.RecordAirReconTarget(player, plan.Objective.FocusHex, ctx.TurnNumber);
            }

            AiReconIntelMemory.ObserveCurrentVisibility(player, ctx.TurnNumber);
            observed |= ObservedThisTurn(player, plan.Objective.FocusHex, ctx.TurnNumber);

            if (airArmy != null && AviationRules.IsOwnedAirfieldAt(airArmy.Hex, player))
                AviationActions.LandInSlotOrder(airArmy, ctx.HexSelection);

            bool visitedAfter = VisionSystem.IsVisited(player, plan.Objective.FocusHex);
            if (!visitedBefore && visitedAfter)
            {
                AiDebugLog.Write($"[AI][V2][Recon][Air][ERROR] aircraft changed ground Visited "
                    + $"focus=({plan.Objective.FocusHex.Q},{plan.Objective.FocusHex.R}) 0->1");
            }

            string at = airArmy != null ? $"({airArmy.Hex.Q},{airArmy.Hex.R})" : "(lost)";
            AiDebugLog.Write($"[AI][V2][Recon][Air] finish \"{airArmy?.Name ?? "lost"}\" "
                + $"steps={steps} observed={(observed ? 1 : 0)} at={at} "
                + $"visitedGround={(visitedBefore ? 1 : 0)}->{(visitedAfter ? 1 : 0)}");
        }

        private static void ReturnUnmovedLaunchToStorage(ArmyData airArmy, PlayerSetupData player,
            AiTurnContext ctx)
        {
            if (airArmy == null || !AviationRules.IsOwnedAirfieldAt(airArmy.Hex, player))
                return;
            ArmyData airfield = AviationActions.EnsureAirfield(ctx.HexSelection, player, airArmy.Hex);
            if (airfield == null)
                return;
            foreach (UnitData aircraft in airArmy.Members.ToList())
            {
                airArmy.Members.Remove(aircraft);
                airfield.AddMemberSorted(aircraft);
            }
            HexCoord hex = airArmy.Hex;
            ctx.HexSelection?.DeleteArmyIfEmptied(airArmy);
            ctx.HexSelection?.RestackArmiesOn(hex, null);
            AiDebugLog.Write("[AI][V2][Recon][Air] provisional launch rolled back to storage");
        }

        private static bool ObservedThisTurn(PlayerSetupData player, HexCoord hex, int turn) =>
            AiReconIntelMemory.TryGetLastObservedTurn(player, hex, out int observedTurn)
            && observedTurn >= turn;

        private static float DirectionPressure(WorldSnapshot snapshot,
            ReconDirectionSnapshot directions, HexCoord focus)
        {
            if (snapshot?.Self == null || directions?.EnemyDirectionSectors == null)
                return 0f;
            ReconSector sector = ReconDirectionModel.Sector(snapshot.Self.Citadel, focus);
            float pressure = directions.EnemyDirectionSectors.TryGetValue(sector, out float p)
                ? Mathf.Clamp01(p) : 0f;
            if (directions.KnownEnemyCitadelDirection == sector)
                pressure = Mathf.Max(pressure, 0.75f);
            return pressure;
        }
    }
}
