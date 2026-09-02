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
    //  Ground Recon remains the primary executor. This phase runs once, after the ordinary V2
    //  mission batch, and may consume only resources that are still physically available.
    //
    //  Deliberate first implementation boundary:
    //    · same-turn sorties only: current/launch airfield -> information objective -> safe airfield;
    //    · the whole boomerang is proven by AiAviationSupport before launch;
    //    · every physical transition is ONE hex through AiTurnController.MoveArmyRoutine;
    //    · after every hex the route is re-planned against newly-known AA / landing capacity;
    //    · if forward safety is no longer provable, the aircraft immediately turns for the best
    //      reachable airfield using AiAviationSupport.TryReplan;
    //    · observation updates AiReconIntelMemory, never VisionSystem.Visited. Aircraft therefore
    //      refresh intelligence without falsely completing a ground Explore objective;
    //    · a target recently flown toward is suppressed by AiMapMemory's AirRecon cooldown, giving
    //      explicit diminishing returns instead of repetitive stale-hex loops.
    //
    //  Multi-turn helicopter sorties intentionally stay out of this first V2 pass. They require a
    //  durable aviation intent/landing reservation in V2; reusing V1 AiTaskRegistry for that would
    //  violate V2's state-ownership boundary. Same-turn sorties give us the full authoritative
    //  movement/AA/observation loop without introducing a second persistence model.
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

            public bool FromStorage => ExistingArmy == null;
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
                yield return Execute(player, root, ctx, plan, v => sortieChanged |= v);
                if (!sortieChanged)
                    yield break;

                executed++;
                changed?.Invoke(true);
                // The strategic objective set remains the frozen turn set. Current observation and
                // the target cooldown below prevent a second pass from selecting already-completed
                // information, even if MaxSortiesPerTurn is raised later.
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

                // Air is a fallback, not a replacement for a cheap ground hop. Explore/Refresh that
                // a ground Recce can finish in one turn stays with the ground executor. Surveil has
                // observation-vantage semantics and is allowed to compete even when a scout exists.
                if (objective.Kind != ReconObjectiveKind.Surveil)
                {
                    ScoutRouteCostEvaluator.Assessment ground =
                        ScoutRouteCostEvaluator.Evaluate(snapshot, objective.ToTarget());
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

            // Already-formed air armies at owned airfields cost no launch mutation. Ignore anything
            // carrying a V1 task: V2 must never steal a formation whose ownership predates the V2
            // turn switch.
            foreach (ArmyData army in ArmyRegistry.AllForOwner(player))
            {
                if (!AviationRules.IsValidAirArmy(army) || army.CurrentMovement <= 0
                    || !AviationRules.IsOwnedAirfieldAt(army.Hex, player)
                    || AiTaskRegistry.TaskFor(player, army) != null)
                    continue;

                AiAviationSupport.Sortie? sortie =
                    AiAviationSupport.TryPlanSortie(army, objective.FocusHex, ctx.Map, player);
                if (!sortie.HasValue)
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

            // Stored aircraft use the exact same launch subset policy as V1: a ready airfield's
            // stored group launches together. TryPlanSortieFromStorage proves both route legs and
            // landing capacity before the free TryLaunch mutation is allowed to happen.
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
            // Absolute spend matters, but scarcity matters too: spending the last AP/Energy point is
            // more expensive than the same sortie from a deep reserve. The route term is the
            // physical opportunity cost of tying the wing up for more movement this turn.
            float apScarcity = apAvailable > 0 ? ap / (float)apAvailable : (ap > 0 ? 1f : 0f);
            float energyScarcity = energyAvailable > 0 ? energy / (float)energyAvailable : (energy > 0 ? 1f : 0f);
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

        private static IEnumerator Execute(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            Candidate plan, System.Action<bool> changed)
        {
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
                changed?.Invoke(true);
            }

            // Re-prove the boomerang after launch and immediately before the first paid move.
            // If anything changed since planning, a just-formed group is rolled back to storage
            // without having spent activation AP/Energy.
            AiAviationSupport.Sortie? live =
                AiAviationSupport.TryPlanSortie(airArmy, plan.Objective.FocusHex, ctx.Map, player);
            if (!live.HasValue)
            {
                AiDebugLog.Write("[AI][V2][Recon][Air] cancel — safe outbound+return no longer provable before first step");
                if (launchedFromStorage)
                    ReturnUnmovedLaunchToStorage(airArmy, player, ctx);
                yield break;
            }

            HexCoord landing = live.Value.LandingHex;
            bool outbound = true;
            bool observed = false;
            int guard = Mathf.Max(2, airArmy.CurrentMovement + 2);
            int steps = 0;

            AiMapMemory.RecordAirReconTarget(player, plan.Objective.FocusHex, ctx.TurnNumber);
            AiDebugLog.Write($"[AI][V2][Recon][Air] launch/continue \"{airArmy.Name}\" "
                + $"target=({plan.Objective.FocusHex.Q},{plan.Objective.FocusHex.R}) "
                + $"landing=({landing.Q},{landing.R})");

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
                    // Strict outbound rule: known AA anywhere on the newly-planned complete sortie
                    // invalidates forward progress. A newly revealed AA zone therefore takes effect
                    // before the next hex, not after the aircraft has followed a cached route.
                    live = AiAviationSupport.TryPlanSortie(airArmy,
                        plan.Objective.FocusHex, ctx.Map, player);
                    if (!live.HasValue)
                    {
                        outbound = false;
                        AiDebugLog.Write("[AI][V2][Recon][Air] forward plan invalidated (AA/landing/range); emergency return");
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
                    if (ret?.Hexes == null || ret.Hexes.Count < 2)
                        break;
                    next = ret.Hexes[1];
                }

                if (!next.HasValue)
                    break;

                HexCoord before = airArmy.Hex;
                var decision = AiDecision.Move(airArmy, next.Value,
                    outbound ? "V2 AirRecon — one-hex information step" : "V2 AirRecon — one-hex safe return",
                    null, 0f, AiTaskCategory.Reconnaissance);
                var trace = new AiMoveExecutionTrace();
                yield return AiTurnController.MoveArmyRoutine(player, decision, ctx, trace);

                airArmy = ArmyRegistry.AllForOwner(player)
                    .FirstOrDefault(a => a.Id == airArmy.Id && AviationRules.IsValidAirArmy(a));
                if (airArmy == null)
                {
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
                if (outbound)
                    AiMapMemory.RecordAirReconTarget(player, plan.Objective.FocusHex, ctx.TurnNumber);

                // Do not special-case an opportunistic air strike/challenge here. The authoritative
                // move resolver already handled it. We only refresh information and then re-plan the
                // next hex from the survivor's actual state.
            }

            AiReconIntelMemory.ObserveCurrentVisibility(player, ctx.TurnNumber);
            observed |= ObservedThisTurn(player, plan.Objective.FocusHex, ctx.TurnNumber);

            if (airArmy != null && AviationRules.IsOwnedAirfieldAt(airArmy.Hex, player))
                AviationActions.LandInSlotOrder(airArmy, ctx.HexSelection);

            AiDebugLog.Write($"[AI][V2][Recon][Air] finish \"{airArmy?.Name ?? "lost"}\" "
                + $"steps={steps} observed={(observed ? 1 : 0)} "
                + $"at=({airArmy?.Hex.Q},{airArmy?.Hex.R}) "
                + $"visitedGround={(VisionSystem.IsVisited(player, plan.Objective.FocusHex) ? 1 : 0)}");
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
