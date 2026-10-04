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
                    && ArmyRegistry.AllForOwner(player).Contains(army)
                    && AviationRules.IsValidAirArmy(army) && army.Controller != null;
                if (!live || army.Hex.Equals(sortie.LandingHex))
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

        // A stored aircraft cannot fly: Recon and every combat-support lane bind only FORMED
        // wings (2026-10-02 playtest: Korrin's Wasp sat in its home airfield from T6 to T17 while
        // every AirSweep failed NoExecutableStep). This proposes forming ONE wing from storage,
        // for an application an existing task really has and no free formed wing already covers:
        //   · Recon — an AirSweep the canonical service projection proves from this airfield
        //     (BestAirfieldServiceTaskScore), capped by the one-actor AirSweep limit;
        //   · combat support — an Attack in Assault with no wing, an ActiveDefence threat with no
        //     air support, a weak Raid (Reinforcement) on a neutral roster of two or more — with a
        //     proven sortie route from this airfield to that target.
        // Either way the launch must fit the SPENDABLE bank now (the same view that protects card
        // play). Forming is free; the shell comes from AviationWingPreparation (reuse first).
        internal static AviationRebasePlan BuildFormationPlan(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx, IReadOnlyList<ReconObjective> objectives,
            IReadOnlyList<MissionIntent> activeIntents = null, ActorCommitments commitments = null)
        {
            if (player == null || root == null || ctx?.Map == null)
                return null;
            AviationRebasePlan plan = BuildReconFormation(snap, player, root, ctx, objectives)
                ?? BuildCombatFormation(snap, player, root, ctx, activeIntents, commitments);
            if (plan != null)
                plan.Commitments = commitments;
            return plan;
        }

        private static AviationRebasePlan BuildReconFormation(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx, IReadOnlyList<ReconObjective> objectives)
        {
            if (objectives == null || objectives.Count == 0)
                return null;
            int serviceable = objectives.Count(ReconAirCapacityPolicy.IsAirServiceable);
            if (serviceable == 0)
                return null;
            ReconAirObservationDetail detail = ReconAirCapacityPolicy.EvaluateDetailed(player, root);
            int formed = detail.AirborneWings.Count + detail.SpareCandidatesInOrder.Count;
            if (formed >= Mathf.Min(ReconAirCapacityPolicy.MaxAirReconActorsPerTurn, serviceable))
                return null;

            AviationRebasePlan best = null;
            foreach ((ArmyData source, IReadOnlyList<UnitData> group) in AffordableStoredGroups(player, root, ctx))
            {
                TaskScore service = NonCombatCardPlayer.BestAirfieldServiceTaskScore(
                    snap, player, ctx, group, source.Hex, objectives, out int coverage, out string witness);
                if (coverage <= 0 || service.Value <= AiConfigV2.allocatorSliceEpsilon)
                    continue;
                if (best != null && service.Value <= best.Utility + AiConfigV2.allocatorSliceEpsilon)
                    continue;
                best = FormPlan(source, group, service, witness);
            }
            return best;
        }

        // Targets of existing ground-combat tasks that want air support and have no wing yet.
        internal static List<HexCoord> CombatSupportTargets(WorldSnapshot snap,
            IReadOnlyList<MissionIntent> activeIntents)
        {
            var targets = new List<HexCoord>();
            int turn = snap?.TurnNumber ?? 0;
            foreach (MissionIntent i in activeIntents ?? System.Array.Empty<MissionIntent>())
            {
                if (i == null || i.Status != IntentStatus.Active)
                    continue;
                AttackIntent a = i.Attack;
                if (a != null && a.Phase == AttackMissionPhase.Assault && !a.AirSupportArmyId.HasValue
                    && a.AirSupportAttemptedTurn != turn)
                    targets.Add(a.Target.Hex);
                RaidIntent r = i.Raid;
                if (r != null && r.Target.Kind == RaidTargetKind.NeutralArmy
                    && r.Phase == RaidMissionPhase.Reinforcement && !r.AirSupportArmyId.HasValue
                    && GroundCombatAirSupport.KnownTargetCount(snap, r.LastKnownHex,
                        AirStrikePolicy.RaidSupport(r.Target.ArmyId),
                        AiV2Util.KnownOpposition(snap, r.Target)) > 1)
                    targets.Add(r.LastKnownHex);
            }
            var supported = new HashSet<int>((activeIntents ?? System.Array.Empty<MissionIntent>())
                .Where(i => i?.ActiveDefence?.Phase == ActiveDefencePhase.AirSupport)
                .Select(i => i.ActiveDefence.EnemyArmyId));
            foreach (ActiveDefenceObjective o in ActiveDefenceObjectiveEvaluator.Enumerate(snap))
                if (!supported.Contains(o.Target.EnemyArmyId)
                    && ActiveDefenceObjectiveEvaluator.Opposition(snap, o.Target.EnemyArmyId) != null)
                    targets.Add(o.Target.LastKnownHex);
            return targets.Distinct().ToList();
        }

        private static AviationRebasePlan BuildCombatFormation(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx, IReadOnlyList<MissionIntent> activeIntents,
            ActorCommitments commitments)
        {
            if (snap == null || activeIntents == null)
                return null;
            List<HexCoord> targets = CombatSupportTargets(snap, activeIntents);
            if (targets.Count == 0)
                return null;
            // Free formed wings already cover that many targets.
            int freeWings = ArmyRegistry.AllForOwner(player).Count(a => AviationRules.IsValidAirArmy(a)
                && AirSortieRegistry.ForArmy(player, a) == null && a.CurrentMovement > 0
                && (commitments == null || !commitments.IsArmyClaimed(a.Id)));
            if (freeWings >= targets.Count)
                return null;

            AviationRebasePlan best = null;
            int bestTurns = int.MaxValue;
            foreach ((ArmyData source, IReadOnlyList<UnitData> group) in AffordableStoredGroups(player, root, ctx))
                foreach (HexCoord target in targets)
                {
                    Sortie? same = AiAirSortiePlanner.TryPlanSortieFromStorage(source.Hex, group, target, ctx.Map, player);
                    MultiTurnSortie? multi = same.HasValue ? null
                        : AiAirSortiePlanner.TryPlanMultiTurnSortieFromStorage(source.Hex, group, target, ctx.Map, player);
                    if (!same.HasValue && !multi.HasValue)
                        continue;
                    int turns = same.HasValue ? 1 : multi.Value.RequiredTurns;
                    if (turns >= bestTurns)
                        continue;
                    bestTurns = turns;
                    best = FormPlan(source, group, default,
                        $"combat support ({target.Q},{target.R}) in {turns} turn(s)");
                }
            return best;
        }

        // One-aircraft groups whose launch fits the spendable bank now.
        private static IEnumerable<(ArmyData Source, IReadOnlyList<UnitData> Group)> AffordableStoredGroups(
            PlayerSetupData player, PlayerRoot root, AiTurnContext ctx)
        {
            float spendableAp = StrategicSpendability.SpendableAp(player, root, ctx);
            float spendableEnergy = StrategicSpendability.SpendableAmount(player, root, ctx, ResourceType.Energy);
            foreach (ArmyData source in ArmyRegistry.AllForOwner(player)
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

        private static IEnumerator ExecuteFormation(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, AviationRebasePlan plan, System.Action<bool> setChanged)
        {
            ArmyData source = AviationRules.FindAirfieldAt(plan.SourceHex, player);
            if (source == null || plan.Aircraft.Any(u => !source.Members.Contains(u)))
                yield break;
            // Re-proved against the live bank: the flight this wing exists for must still be payable.
            int ap = plan.Aircraft.Sum(u => Mathf.Max(0, u.ActivationApCost));
            int energy = plan.Aircraft.Sum(u => Mathf.Max(0, u.LaunchEnergyCost));
            if (!AiAirSortiePlanner.CanAffordLaunch(root, plan.Aircraft)
                || ap > StrategicSpendability.SpendableAp(player, root, ctx) + AiConfigV2.allocatorSliceEpsilon
                || energy > StrategicSpendability.SpendableAmount(player, root, ctx, ResourceType.Energy)
                    + AiConfigV2.allocatorSliceEpsilon)
            {
                AiDebugLog.Write("[AI][V2][Aviation][FormWing] cancelled — AP/Energy held by the resource bank");
                yield break;
            }
            if (!AviationWingPreparation.TryForm(player, ctx, source, plan.Aircraft, plan.Commitments,
                    out ArmyData wing, out string how) || wing == null)
            {
                AiDebugLog.Write($"[AI][V2][Aviation][FormWing] failed — {how}");
                yield break;
            }
            AiDebugLog.Write($"[AI][V2][Aviation][FormWing] {player.Nickname}: \"{wing.Name}\" #{wing.Id} formed from "
                + $"{plan.Aircraft.Count} stored aircraft at ({plan.SourceHex.Q},{plan.SourceHex.R}) ({how}) "
                + $"service={plan.Score.Value:0.00} witness={plan.DestinationWitness ?? "none"} "
                + $"(sortie launch ap {ap} energy {energy} is paid by the funded leg, not here)");
            setChanged?.Invoke(true);
            yield return AiTurnController.WaitStep(ctx);
        }

        internal static AviationRebasePlan BuildPlan(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx, IReadOnlyList<ReconObjective> objectives)
        {
            if (player == null || root == null || ctx?.Map == null || objectives == null
                || objectives.Count == 0)
                return null;

            AviationRebasePlan best = null;
            foreach (ArmyData source in ArmyRegistry.AllForOwner(player)
                .Where(AviationRules.IsAirfield)
                .OrderBy(a => a.Hex.Q).ThenBy(a => a.Hex.R))
            {
                foreach (UnitData aircraft in source.Members
                    .Where(AviationRules.IsAviation).OrderBy(u => u.RuntimeId))
                {
                    IReadOnlyList<UnitData> group = new[] { aircraft };
                    if (!AiAirSortiePlanner.CanAffordLaunch(root, group))
                        continue;
                    TaskScore sourceService = NonCombatCardPlayer.BestAirfieldServiceTaskScore(
                        snap, player, ctx, group, source.Hex, objectives,
                        out _, out string sourceWitness);

                    foreach (HexCoord destination in AiAirSortiePlanner.OwnedAirfieldHexes(player)
                        .Where(h => !h.Equals(source.Hex)))
                    {
                        TaskScore destinationService = NonCombatCardPlayer.BestAirfieldServiceTaskScore(
                            snap, player, ctx, group, destination, objectives,
                            out int coverage, out string destinationWitness);
                        float improvement = destinationService.Value - sourceService.Value;
                        if (coverage <= 0 || improvement <= AiConfigV2.allocatorSliceEpsilon)
                            continue;

                        AiAirSortiePlanner.RebaseRoute? route =
                            AiAirSortiePlanner.TryPlanRebaseFromStorage(
                                source.Hex, group, destination, ctx.Map, player);
                        if (!route.HasValue)
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
                            sourceService, destinationService, ap, energy);
                        if (score.Value <= AiConfigV2.allocatorSliceEpsilon)
                            continue;
                        var candidate = new AviationRebasePlan
                        {
                            SourceHex = source.Hex,
                            DestinationHex = destination,
                            Aircraft = group,
                            Route = route.Value,
                            Score = score,
                            ActivationAp = ap,
                            EnergyCost = energy,
                            SourceWitness = sourceWitness,
                            DestinationWitness = destinationWitness,
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

        internal static IEnumerator Execute(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, AviationRebasePlan plan, System.Action<bool> setChanged)
        {
            if (plan?.Aircraft == null || plan.Aircraft.Count == 0 || player == null
                || root == null || ctx?.Map == null)
                yield break;
            if (plan.FormOnly)
            {
                yield return ExecuteFormation(player, root, ctx, plan, setChanged);
                yield break;
            }
            ArmyData source = AviationRules.FindAirfieldAt(plan.SourceHex, player);
            if (source == null || plan.Aircraft.Any(u => !source.Members.Contains(u))
                || !AiAirSortiePlanner.CanAffordLaunch(root, plan.Aircraft))
                yield break;

            AiAirSortiePlanner.RebaseRoute? liveRoute =
                AiAirSortiePlanner.TryPlanRebaseFromStorage(plan.SourceHex, plan.Aircraft,
                    plan.DestinationHex, ctx.Map, player);
            if (!liveRoute.HasValue)
                yield break;

            int liveAp = plan.Aircraft.Sum(u => Mathf.Max(0, u.ActivationApCost));
            int liveEnergy = plan.Aircraft.Sum(u => Mathf.Max(0, u.LaunchEnergyCost));
            float spendableAp = StrategicSpendability.SpendableAp(player, root, ctx);
            float spendableEnergy = StrategicSpendability.SpendableAmount(
                player, root, ctx, ResourceType.Energy);
            if (liveAp > spendableAp + AiConfigV2.allocatorSliceEpsilon
                || liveEnergy > spendableEnergy + AiConfigV2.allocatorSliceEpsilon)
            {
                AiDebugLog.Write("[AI][V2][Aviation][Rebase] cancelled — AP/Energy held by the resource bank");
                yield break;
            }

            var before = new HashSet<int>(ArmyRegistry.AllForOwner(player).Select(a => a.Id));
            var decision = new AiDecision
            {
                Kind = AiActionKind.LaunchAirRecon,
                TargetHex = plan.SourceHex,
                AircraftToLaunch = plan.Aircraft,
                AirActionHex = plan.DestinationHex,
                AirLandingHex = plan.DestinationHex,
                Score = plan.Utility,
                Reason = $"V2 Aviation Rebase — current objective service improves "
                    + $"{plan.SourceWitness ?? "none"} -> {plan.DestinationWitness ?? "none"}",
            };
            var trace = new AiMoveExecutionTrace();
            yield return AiAirSortiePlanner.LaunchRoutine(
                player, decision, ctx, AirSortieKind.Rebase, trace);

            ArmyData wing = ArmyRegistry.AllForOwner(player)
                .Where(a => AviationRules.IsValidAirArmy(a) && !before.Contains(a.Id))
                .OrderBy(a => a.Id).FirstOrDefault();
            if (wing == null)
                yield break;

            bool changed = !wing.Hex.Equals(plan.SourceHex);
            int guard = Mathf.Max(1, wing.CurrentMovement + 1);
            while (!wing.Hex.Equals(plan.DestinationHex) && wing.CurrentMovement > 0 && guard-- > 0)
            {
                AirSortie task = AirSortieRegistry.ForArmy(player, wing);
                AiDecision move = task == null ? null : AiAirSortiePlanner.ContinueSortie(
                    player, root, ctx, task, "AviationRebase",
                    "relocates to the selected forward airfield", plan.Utility);
                if (move == null)
                    break;
                HexCoord prior = wing.Hex;
                yield return AiTurnController.MoveArmyRoutine(player, move, ctx, trace);
                wing = ArmyRegistry.AllForOwner(player).FirstOrDefault(a => a.Id == wing.Id);
                if (wing == null || wing.Hex.Equals(prior))
                    break;
                changed = true;
            }
            if (wing != null && wing.Hex.Equals(plan.DestinationHex))
                AirSortieRegistry.Remove(player, wing);
            setChanged?.Invoke(changed);
            AiDebugLog.Write($"[AI][V2][Aviation][Rebase] source=({plan.SourceHex.Q},{plan.SourceHex.R}) "
                + $"destination=({plan.DestinationHex.Q},{plan.DestinationHex.R}) "
                + $"score={plan.Score.Value:0.00} changed={(changed ? 1 : 0)} "
                + $"witness={plan.DestinationWitness ?? "none"}");
        }

        // `allowRecoveryStrike` — false when the wing comes straight from a task's strike series:
        // that series already struck under the task's own policy (e.g. RaidSupport's survivor
        // floor) and a Standard strike on the way out must not override it.
        internal static IEnumerator ExecuteContinuation(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ArmyData wing, System.Action<bool> setChanged,
            bool allowRecoveryStrike = true)
        {
            if (player == null || root == null || ctx?.Map == null || wing == null)
                yield break;
            AirSortie task = AirSortieRegistry.ForArmy(player, wing);
            if (task == null || task.Kind != AirSortieKind.Rebase)
                yield break;

            bool changed = false;
            var trace = new AiMoveExecutionTrace();

            // An airborne recovery/rebase wing does not become pacifist just because its original
            // mission released it. If it starts this continuation on an enemy-occupied hex and a
            // landing is still recoverable inside live endurance, take the free stationary strike
            // first; movement remains untouched and the return continues below.
            if (allowRecoveryStrike && !task.NoRecoveryStrike
                && !AviationRules.IsOwnedAirfieldAt(wing.Hex, player)
                && AviationActions.CanActivateForStationaryStrike(wing)
                && AiAirSortiePlanner.CanStrikeAndRecover(wing, ctx.Map, player))
            {
                AviationCombatPresenter presenter = ctx.HexSelection?.AviationCombatPresenter;
                if (presenter != null)
                {
                    var strike = new AviationCombatPresenter.AirStrikeResult();
                    yield return AviationActions.ResolveStationaryStrike(presenter, wing, strike);
                    if (strike.Attacked)
                    {
                        changed = true;
                        V2StateVersion.Bump();
                        AiDebugLog.Write($"[AI][V2][Aviation][RecoveryStrike] actor=#{wing.Id} "
                            + $"hex=({wing.Hex.Q},{wing.Hex.R}) attacked=1 "
                            + $"safeEnds={AviationRange.SafeUnlandedEndsRemaining(wing)}");
                    }
                }
            }

            int guard = Mathf.Max(1, wing.CurrentMovement + 1);
            while (wing != null && wing.CurrentMovement > 0 && guard-- > 0)
            {
                AiDecision move = AiAirSortiePlanner.ContinueSortie(
                    player, root, ctx, task, "AviationRebase",
                    "relocates to the selected forward airfield", AiConfig.airStrikeContinuationScore);
                if (move == null)
                    break;
                HexCoord prior = wing.Hex;
                yield return AiTurnController.MoveArmyRoutine(player, move, ctx, trace);
                wing = ArmyRegistry.AllForOwner(player).FirstOrDefault(a => a.Id == wing.Id);
                if (wing == null || wing.Hex.Equals(prior))
                    break;
                changed = true;
                if (wing.Hex.Equals(task.LandingHex))
                {
                    AirSortieRegistry.Remove(player, task);
                    break;
                }
            }

            // If the requested destination disappeared while the aircraft was already safely on
            // another owned airfield, there is no recovery obligation left to retain forever.
            if (!changed && wing != null && AviationRules.IsOwnedAirfieldAt(wing.Hex, player)
                && !AviationRules.IsOwnedAirfieldAt(task.LandingHex, player))
                AirSortieRegistry.Remove(player, task);
            setChanged?.Invoke(changed);
        }
    }
}
