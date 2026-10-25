using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Aviation;
using Game.Cards;
using Game.Combat;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Ai.V2
{
    // A generic aviation operation, deliberately not a new DesireAxis/MissionKind/manager.
    // Phase B admits it beside the other strategic spends, while current Recon objectives and
    // the canonical TaskScore service projection decide whether relocating one live aircraft is
    // materially better than leaving it at its present airfield.
    internal sealed class AviationRebasePlan
    {
        public HexCoord SourceHex;
        public HexCoord DestinationHex;
        public IReadOnlyList<UnitData> Aircraft;
        public AiAirSortiePlanner.RebaseRoute Route;
        public TaskScore Score;
        public float Utility => Score.Value;
        public int ActivationAp;
        public int EnergyCost;
        public string SourceWitness;
        public string DestinationWitness;
        // Project owner, 2026-10-02: Recon may form a wing out of stored aircraft. Forming is free
        // (AviationActions.TryLaunch: "forming a stack is not a take-off"); the sortie itself is
        // still admitted and paid by the ordinary Recon funding. Source == Destination.
        public bool FormOnly;
        // The ownership view the formation may reuse armies under (null: never repurpose).
        public ActorCommitments Commitments;
    }

    internal static class AviationRebasePlanner
    {
        // The relocation pays ONE sortie launch (AP + Energy at the neutral resource price — no
        // snapshot here). A multi-turn route costs nothing more: continuing a paid sortie is free
        // on every later turn (ArmyData.PendingActivation*), so there is no recurring delivery.
        internal static TaskScore ScoreImprovement(TaskScore sourceService,
            TaskScore destinationService, int activationAp, int energyCost)
        {
            float price = TaskScoreEvaluator.Price(ActionPrice.Ap(activationAp)
                + energyCost * AiConfigV2.actionPriceResourceAp);
            return TaskScoreEvaluator.NetChange(sourceService, destinationService, price);
        }

        // A launched multi-turn rebase is a physical landing obligation, not a fresh strategic
        // choice. It survives turn boundaries in AirSortieRegistry and is resumed before optional
        // spending; the exact route, capacity, ownership and fuel proof are still re-derived by
        // ContinueSortie on every step.
        // `turn` excludes a continuation that already could not take a step this turn (Recon audit
        // B1, AviationObligationStallRegistry); it is re-tried from the next turn.
        internal static List<ArmyData> FindMandatoryContinuations(PlayerSetupData player, int turn)
        {
            var result = new List<ArmyData>();
            foreach (AirSortie sortie in AirSortieRegistry.For(player).ToList())
            {
                if (sortie == null || sortie.Kind != AirSortieKind.Rebase)
                    continue;
                ArmyData army = sortie.Army;
                bool live = army != null && army.Owner == player
                    && OwnWorld.Of(player).Armies.Contains(army)
                    && AviationRules.IsValidAirArmy(army) && army.Controller != null;
                if (!live || (army.Hex.Equals(sortie.LandingHex)
                    && AviationRules.IsOwnedAirfieldAt(sortie.LandingHex, player)))
                {
                    AirSortieRegistry.Remove(player, sortie);
                    continue;
                }
                if (army.CurrentMovement > 0
                    && !AviationObligationStallRegistry.IsStalled(player, turn, army.Id))
                    result.Add(army);
            }
            return result.Distinct().OrderBy(a => a.Id).ToList();
        }

        // Optional preparation for an existing ground-combat operation. AirSweep storage
        // candidates are nominated by Recon Assignment and formed by its funded executor.
        internal static AviationRebasePlan BuildFormationPlan(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx, IReadOnlyList<ReconObjective> objectives,
            IReadOnlyList<MissionIntent> activeIntents = null, ActorCommitments commitments = null)
        {
            if (player == null || root == null || ctx?.Map == null)
                return null;
            if (commitments == null && activeIntents != null)
                commitments = ActorCommitments.FromIntents(activeIntents, snap, objectives);
            AviationRebasePlan plan = BuildCombatFormation(snap, player, root, ctx, activeIntents, commitments);
            if (plan != null)
                plan.Commitments = commitments;
            return plan;
        }

        // Targets of existing ground-combat tasks that want air support and have no wing yet.
        internal static List<CombatAirSupportRequest> CombatSupportTargets(WorldSnapshot snap,
            IReadOnlyList<MissionIntent> activeIntents)
        {
            var targets = new List<CombatAirSupportRequest>();
            int turn = snap?.TurnNumber ?? 0;
            foreach (MissionIntent i in activeIntents ?? System.Array.Empty<MissionIntent>())
            {
                if (i == null || i.Status != IntentStatus.Active)
                    continue;
                AttackIntent a = i.Attack;
                if (a != null && a.Phase == AttackMissionPhase.Assault && !a.AirSupportArmyId.HasValue
                    && a.AirSupportAttemptedTurn != turn)
                {
                    var objective = AttackObjectiveEvaluator.ForTrackedTarget(snap, a.Target);
                    if (objective != null) targets.Add(new CombatAirSupportRequest(i.IntentKey,
                        a.Target.Hex, AirStrikePolicy.Standard, objective.TaskScore));
                }
                RaidIntent r = i.Raid;
                if (r != null && r.Target.Kind == RaidTargetKind.NeutralArmy
                    && r.Phase == RaidMissionPhase.Reinforcement && !r.AirSupportArmyId.HasValue
                    && r.AirSupportAttemptedTurn != turn
                    && GroundCombatAirSupport.KnownTargetCount(snap, r.LastKnownHex,
                        AirStrikePolicy.RaidSupport(r.Target.ArmyId),
                        KnownOpposition.ForRaid(snap, r.Target)) > 1)
                    targets.Add(new CombatAirSupportRequest(i.IntentKey, r.LastKnownHex,
                        AirStrikePolicy.RaidSupport(r.Target.ArmyId),
                        RaidObjectiveEvaluator.BuildRaidScore(snap, r.Target, r.LastKnownHex)));
            }
            var supported = new HashSet<int>((activeIntents ?? System.Array.Empty<MissionIntent>())
                .Where(i => i?.Status == IntentStatus.Active
                    && i.ActiveDefence?.Phase == ActiveDefencePhase.AirSupport)
                .Select(i => i.ActiveDefence.EnemyArmyId));
            foreach (ActiveDefenceObjective o in GroundCombatAirSupport.AirSupportObjectives(snap, activeIntents))
                if (!supported.Contains(o.Target.EnemyArmyId)
                    && (o.Target.InterceptPurpose != ActiveDefenceInterceptPurpose.PreparationStrike
                        || !targets.Any(t => t.Target.Equals(o.Target.LastKnownHex)))
                    && KnownOpposition.OfEnemyArmy(snap, o.Target.EnemyArmyId) != null)
                {
                    ActiveDefenceMissionTarget target = o.Target;
                    target.Phase = ActiveDefencePhase.AirSupport;
                    targets.Add(GroundCombatAirSupport.AirSupportRequest(target, o.TaskScore));
                }
            return targets.GroupBy(t => t.Key).Select(g => g.First())
                .OrderByDescending(t => t.Score.Value).ThenBy(t => t.Key.ToString()).ToList();
        }

        private static AviationRebasePlan BuildCombatFormation(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx, IReadOnlyList<MissionIntent> activeIntents,
            ActorCommitments commitments)
        {
            if (snap == null || activeIntents == null)
                return null;
            List<CombatAirSupportRequest> targets = GroundCombatAirSupport.UncoveredRequests(
                snap, player, root, ctx, CombatSupportTargets(snap, activeIntents), commitments);
            AviationRebasePlan best = null;
            int bestEta = int.MaxValue, bestRoute = int.MaxValue;
            foreach ((ArmyData source, IReadOnlyList<UnitData> group) in AffordableStoredGroups(player, root, ctx))
                foreach (CombatAirSupportRequest target in targets)
                {
                    var service = GroundCombatAirSupport.ProjectService(snap, player, ctx.Map,
                        group, source.Hex, target);
                    if (!service.HasValue || service.Value.Score.Value <= AiConfigV2.allocatorSliceEpsilon)
                        continue;
                    if (best != null && (service.Value.Score.Value < best.Utility - AiConfigV2.allocatorSliceEpsilon
                        || (Mathf.Abs(service.Value.Score.Value - best.Utility) <= AiConfigV2.allocatorSliceEpsilon
                            && (service.Value.FirstStrikeEta > bestEta
                                || (service.Value.FirstStrikeEta == bestEta && service.Value.RouteCost >= bestRoute)))))
                        continue;
                    bestEta = service.Value.FirstStrikeEta;
                    bestRoute = service.Value.RouteCost;
                    best = FormPlan(source, group, service.Value.Score,
                        $"combat support {target.Key} in {service.Value.FirstStrikeEta} turn(s)");
                }
            return best;
        }

        // One-aircraft groups whose launch fits the spendable bank now.
        private static IEnumerable<(ArmyData Source, IReadOnlyList<UnitData> Group)> AffordableStoredGroups(
            PlayerSetupData player, PlayerRoot root, AiTurnContext ctx)
        {
            float spendableAp = StrategicSpendability.SpendableAp(player, root, ctx);
            float spendableEnergy = StrategicSpendability.SpendableAmount(player, root, ctx, ResourceType.Energy);
            foreach (ArmyData source in OwnWorld.Of(player).Armies
                .Where(AviationRules.IsAirfield)
                .OrderBy(a => a.Hex.Q).ThenBy(a => a.Hex.R).ToList())
                foreach (UnitData aircraft in source.Members
                    .Where(AviationRules.IsAviation).OrderBy(u => u.RuntimeId).ToList())
                {
                    IReadOnlyList<UnitData> group = new[] { aircraft };
                    if (!AiAirSortiePlanner.CanAffordLaunch(root, group))
                        continue;
                    int ap = group.Sum(u => Mathf.Max(0, u.ActivationApCost));
                    int energy = group.Sum(u => Mathf.Max(0, u.LaunchEnergyCost));
                    if (ap > spendableAp + AiConfigV2.allocatorSliceEpsilon
                        || energy > spendableEnergy + AiConfigV2.allocatorSliceEpsilon)
                        continue;
                    yield return (source, group);
                }
        }

        private static AviationRebasePlan FormPlan(ArmyData source, IReadOnlyList<UnitData> group,
            TaskScore service, string witness) => new AviationRebasePlan
            {
                SourceHex = source.Hex,
                DestinationHex = source.Hex,
                Aircraft = group,
                Score = service,
                ActivationAp = 0,
                EnergyCost = 0,
                SourceWitness = "stored",
                DestinationWitness = witness,
                FormOnly = true,
            };

        internal sealed class DeliveryStop
        {
            public HexCoord Hex;
            public HexCoord FirstHop;
            public AiAirSortiePlanner.RebaseRoute FirstRoute;
            public int Launches;
            public int Turns;
        }

        // A future turn after a real landing; never refresh the live unit while scoring.
        internal static IReadOnlyList<UnitData> ProjectAfterLanding(IReadOnlyList<UnitData> aircraft) =>
            aircraft.Select(u =>
            {
                UnitData copy = u.CloneForCombat();
                copy.MoveCurrent = copy.MoveMax;
                copy.TurnsWithoutRefuel = u.TurnsWithoutRefuel;
                copy.LaunchEnergyCost = u.LaunchEnergyCost;
                AviationRules.ResetAfterLanding(copy);
                return copy;
            }).ToList();

        // Reuse exact rebase proofs on the current owned base/carrier network. Only the first
        // hop is executed/reserved; ownership, capacity, carrier position and demand are read
        // again after landing. No speculative future carrier position or new mission is created.
        internal static IReadOnlyList<DeliveryStop> DeliveryStops(HexCoord source,
            IReadOnlyList<UnitData> aircraft, HexMap map, PlayerSetupData player)
        {
            var reached = new Dictionary<HexCoord, DeliveryStop>();
            if (aircraft == null || aircraft.Count == 0 || map == null || player == null)
                return new List<DeliveryStop>();
            bool multiHop = AiAirSortiePlanner.IsExpendableWing(aircraft);
            var fields = AiAirSortiePlanner.OwnedAirfieldHexes(player)
                .Distinct().OrderBy(h => h.Q).ThenBy(h => h.R).ToList();
            var refreshed = ProjectAfterLanding(aircraft);
            var pending = new List<DeliveryStop> { new DeliveryStop { Hex = source } };
            var settled = new HashSet<HexCoord>();
            while (pending.Count > 0)
            {
                DeliveryStop current = pending.OrderBy(x => x.Launches).ThenBy(x => x.Turns)
                    .ThenBy(x => x.Hex.Q).ThenBy(x => x.Hex.R).First();
                pending.Remove(current);
                if (!settled.Add(current.Hex)) continue;
                if (current.Launches > 0 && !multiHop) continue;
                foreach (HexCoord destination in fields)
                {
                    if (destination.Equals(source) || settled.Contains(destination)) continue;
                    var route = AiAirSortiePlanner.TryPlanRebaseFromStorage(current.Hex,
                        current.Launches == 0 ? aircraft : refreshed, destination, map, player);
                    if (!route.HasValue) continue;
                    int launches = current.Launches + 1;
                    int turns = current.Turns + Mathf.Max(1, route.Value.RequiredTurns);
                    if (reached.TryGetValue(destination, out var previous)
                        && (previous.Launches < launches
                            || (previous.Launches == launches && previous.Turns <= turns))) continue;
                    var next = new DeliveryStop
                    {
                        Hex = destination, Launches = launches, Turns = turns,
                        FirstHop = current.Launches == 0 ? destination : current.FirstHop,
                        FirstRoute = current.Launches == 0 ? route.Value : current.FirstRoute,
                    };
                    reached[destination] = next;
                    pending.Add(next);
                }
            }
            return reached.Values.OrderBy(x => x.Launches).ThenBy(x => x.Turns)
                .ThenBy(x => x.Hex.Q).ThenBy(x => x.Hex.R).ToList();
        }

        internal static AviationRebasePlan BuildPlan(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx, IReadOnlyList<ReconObjective> objectives)
        {
            if (player == null || root == null || ctx?.Map == null || objectives == null)
                return null;

            // A stored aircraft is worth relocating for the same service a new card of it would
            // be deployed for (NonCombatCardPlayer.AirfieldServiceTaskScore): Recon objectives and
            // the uncovered strike targets of existing ground-combat tasks. A carrier marching
            // with an Attack therefore draws aircraft once its hex serves the assault better.
            List<CombatAirSupportRequest> strikeTargets = NonCombatCardPlayer.KnownStrikeTargets(
                snap, player, root, ctx, objectives);
            if (objectives.Count == 0 && strikeTargets.Count == 0)
                return null;

            AviationRebasePlan best = null;
            foreach (ArmyData source in OwnWorld.Of(player).Armies
                .Where(AviationRules.IsAirfield)
                .OrderBy(a => a.Hex.Q).ThenBy(a => a.Hex.R))
            {
                foreach (UnitData aircraft in source.Members
                    .Where(AviationRules.IsAviation).OrderBy(u => u.RuntimeId))
                {
                    IReadOnlyList<UnitData> group = new[] { aircraft };
                    if (!AiAirSortiePlanner.CanAffordLaunch(root, group))
                        continue;
                    TaskScore sourceService = NonCombatCardPlayer.AirfieldServiceTaskScore(
                        snap, player, ctx, group, source.Hex, objectives, strikeTargets, !AiAirSortiePlanner.IsExpendableWing(group),
                        out _, out string sourceWitness, out _);

                    var afterLanding = AiAirSortiePlanner.IsExpendableWing(group)
                        ? ProjectAfterLanding(group) : group;
                    foreach (DeliveryStop delivery in DeliveryStops(source.Hex, group, ctx.Map, player))
                    {
                        HexCoord destination = delivery.Hex;
                        TaskScore destinationService = NonCombatCardPlayer.AirfieldServiceTaskScore(
                            snap, player, ctx, afterLanding, destination, objectives, strikeTargets, !AiAirSortiePlanner.IsExpendableWing(group),
                            out int coverage, out string destinationWitness, out _);
                        float improvement = destinationService.Value - sourceService.Value;
                        if (coverage <= 0 || improvement <= AiConfigV2.allocatorSliceEpsilon)
                            continue;

                        int ap = group.Sum(u => Mathf.Max(0, u.ActivationApCost));
                        int energy = group.Sum(u => Mathf.Max(0, u.LaunchEnergyCost));
                        if (ap > StrategicSpendability.SpendableAp(player, root, ctx)
                            + AiConfigV2.allocatorSliceEpsilon)
                            continue;
                        float spendableEnergy = StrategicSpendability.SpendableAmount(
                            player, root, ctx, ResourceType.Energy);
                        if (energy > spendableEnergy + AiConfigV2.allocatorSliceEpsilon)
                            continue;

                        TaskScore score = ScoreImprovement(
                            sourceService, destinationService, ap * delivery.Launches, energy * delivery.Launches);
                        if (score.Value <= AiConfigV2.allocatorSliceEpsilon)
                            continue;
                        var candidate = new AviationRebasePlan
                        {
                            SourceHex = source.Hex,
                            DestinationHex = delivery.FirstHop,
                            Aircraft = group,
                            Route = delivery.FirstRoute,
                            Score = score,
                            ActivationAp = ap,
                            EnergyCost = energy,
                            SourceWitness = sourceWitness,
                            DestinationWitness = $"{destinationWitness}; delivery via {delivery.Launches} launch(es) to ({destination.Q},{destination.R})",
                        };
                        if (best == null || candidate.Utility > best.Utility + AiConfigV2.allocatorSliceEpsilon
                            || (Mathf.Abs(candidate.Utility - best.Utility) <= AiConfigV2.allocatorSliceEpsilon
                                && aircraft.RuntimeId < best.Aircraft[0].RuntimeId))
                            best = candidate;
                    }
                }
            }
            return best;
        }
    }
}
