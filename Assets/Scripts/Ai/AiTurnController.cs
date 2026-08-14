using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Cameras;
using Game.Cards;
using Game.Core;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.UI;
using Game.Units;
using UnityEngine;

namespace Game.Ai
{
    // Everything AiTurnController needs from the scene to both perform and visualize an AI
    // player's turn — bundled so GameTurnController.BeginPlayerTurn doesn't have to pass a long
    // parameter list every call. StartingDeckCatalog/StartingHandSize/DrawApCost are read once
    // from the human's own CardHandUI (see its own comment) purely as the shared deck-catalog/
    // rule source — each AI player still resolves its own deck from it via its own
    // PlayerSetupData.Faction (see AiHandRegistry.GetOrCreate), never sharing the human's actual
    // _cards/hand.
    public class AiTurnContext
    {
        public RtsCameraController Camera;
        public HexMap Map;
        public HexSelectionController HexSelection;
        public ArmyViewerModalUI ArmyViewerModal;
        // Kept alongside StartingDeckCatalog/etc (all read from this same source, see From)
        // purely so RunTurn can push live hand updates to CardHandUI.RefreshAiHandDebugIfShowing
        // — a no-op whenever debugFollowAiVision isn't showing this player's hand, so this never
        // needs to know that flag itself.
        public CardHandUI HumanCardHandUI;
        public StartingDeckCatalog StartingDeckCatalog;
        public int StartingHandSize;
        public int DrawApCost;
        public float MinStepDelay = 0.5f;
        public float MaxStepDelay = 1f;
        // For BuildFacilityRoutine's own extractionFacilityCards lookup — GameTurnController
        // already holds a GameConfig reference, just not previously threaded through to here.
        public GameConfig GameConfig;

        public static AiTurnContext From(RtsCameraController camera, HexMap map, HexSelectionController hexSelection,
            ArmyViewerModalUI armyViewerModal, CardHandUI humanCardHand, float minStepDelay, float maxStepDelay,
            GameConfig gameConfig)
        {
            return new AiTurnContext
            {
                Camera = camera,
                Map = map,
                HexSelection = hexSelection,
                ArmyViewerModal = armyViewerModal,
                HumanCardHandUI = humanCardHand,
                StartingDeckCatalog = humanCardHand != null ? humanCardHand.StartingDeckCatalog : null,
                StartingHandSize = humanCardHand != null ? humanCardHand.StartingHandSize : 0,
                DrawApCost = humanCardHand != null ? humanCardHand.DrawApCost : 2,
                MinStepDelay = minStepDelay,
                MaxStepDelay = maxStepDelay,
                GameConfig = gameConfig,
            };
        }
    }

    // Level 1 of the AI architecture doc (see AI_ARCHITECTURE.html section 01) executed for
    // real, across all three concurrent categories that currently exist (Разведка, Экономика,
    // Менеджмент — see AiTaskCategory; Оборона/Атака are still AiGoalScorer-only, see its own
    // class comment) — replaces GameTurnController's old "log a goal, then pass" placeholder for
    // a non-human turn. Each call to RunTurn is a small AP budget loop: every iteration asks
    // Decide (pure, read-only) for the single best next thing to do given the player's current
    // hand/armies/AP/AiTaskRegistry state, performs it (with camera pans + the same popups a
    // human would see — see PerformDecision), and loops again until nothing useful is left or
    // the AP is spent. Borrowing/creating armies, drawing cards, and issuing moves all go
    // through the exact same player-agnostic methods a human's own clicks use (Game.Map.
    // ArmyActions, HexSelectionController.IssueMoveOrder) — nothing here mutates game state
    // through a separate path.
    public static class AiTurnController
    {
        // Every tunable number this class used to hold as private consts (turn-loop safety cap,
        // arbiter base weights, per-task radii/caps/bonuses) now lives on AiConfig, read via
        // AiConfig.Current at each use site — see that class for what each field means and why
        // (comments preserved there). One shared asset, tunable without recompiling, same reason
        // Game.Core.GameConfig already exists for gameplay constants generally.

        private enum AiActionKind
        {
            MoveArmy,
            PlayCard,
            ReserveArmy,
            DrawCard,
            BuildFacility,
            SplitGarrisonArmy,
            ConsolidateUnits,
            DetachCollector,
            Wait,
            Pass,
        }

        private class AiDecision
        {
            public AiActionKind Kind;
            public ArmyData ExistingArmy;
            public HexCoord TargetHex;
            public CardData Card;
            public string Reason;
            // SplitGarrisonArmy only — the garrison members SplitGarrisonArmyRoutine moves into
            // the freshly created army (see AiManagementPlanner.FindGarrisonOverflow).
            public IReadOnlyList<UnitData> UnitsToMove;
            // ConsolidateUnits only — see AiManagementPlanner.FindConsolidationMove.
            public AiManagementPlanner.ConsolidationMove ConsolidationMove;
            // DetachCollector only — see AiEconomyPlanner.FindCollectorDetachPlan. CollectorUnit
            // stays in ExistingArmy (Source) either way; MergeTarget null means DetachCollector
            // Routine creates a fresh army at Source's own hex instead (only ever proposed when
            // that hex IS the player's own garrison hex — see TryStartCollectorDetachCandidates).
            public UnitData CollectorUnit;
            public ArmyData MergeTarget;
            // Set whenever this decision advances/starts a persistent AiTask (every MoveArmy
            // decision under Разведка/Экономика, and every BuildFacility decision) — null for
            // PlayCard/ReserveArmy/DrawCard/Pass, which never persist one (see AiTaskKind's own
            // comment on DrawCard/ReserveArmy).
            public AiTask Task;

            // The unified arbiter's own comparison key (see Decide's own class comment) — every
            // candidate gathered this step gets one, on the same shared scale, and Decide picks
            // the single highest. Left at its default 0f for AiDecision.None (Pass never competes
            // against anything — it's only ever produced when the candidate list is empty).
            public float Score;

            // Economy-start candidates only (see TryStartEconomyCandidates) — the OTHER task the
            // hero this candidate wants would have to give up. Removed only if THIS candidate
            // actually wins Decide's own arbitration (see Commit) — generating the candidate must
            // never itself preempt anything, since most candidates built in a given step lose.
            public AiTask PreemptedTask;

            public static AiDecision Move(ArmyData army, AiScoutPlanner.ScoutTarget target, AiTask task, float score) => new AiDecision
            {
                Kind = AiActionKind.MoveArmy, ExistingArmy = army, TargetHex = target.Hex, Reason = target.Reason, Task = task,
                Score = score,
            };

            public static AiDecision BuildFacility(AiTask task, float score) => new AiDecision
            {
                Kind = AiActionKind.BuildFacility, ExistingArmy = task.Army, TargetHex = task.TargetHex, Task = task, Score = score,
                Reason = $"задача «Экономика»: строит объект добычи {task.ResourceType} на ({task.TargetHex.Q},{task.TargetHex.R})",
            };

            // Экономика · Задача 1's own visible stand-down — see EconomyWaitScore's own comment.
            // No-op on purpose: WaitRoutine touches neither the army nor the task, so the SAME
            // task just gets re-evaluated fresh next turn (AiResourceReservation keeps topping up
            // meanwhile — see AdvanceEconomyTask).
            public static AiDecision Wait(AiTask task, string reason) => new AiDecision
            {
                Kind = AiActionKind.Wait, ExistingArmy = task.Army, TargetHex = task.TargetHex, Task = task,
                Score = AiConfig.Current.economyWaitScore, Reason = reason,
            };

            // Экономика · Задача 2's own prerequisite step — see AiEconomyPlanner.
            // CollectorDetachPlan's own comment for the two shapes `plan` can take. No Task here:
            // same one-shot-reorg shape as SplitGarrison/ConsolidateUnits, not a persistent
            // AiTaskRegistry entry — the collector becomes tracked only once TryStartResourcesScrap
            // Candidates picks it up as an already-solo army next step.
            public static AiDecision DetachCollector(AiEconomyPlanner.CollectorDetachPlan plan, ResourceType type, float score) => new AiDecision
            {
                Kind = AiActionKind.DetachCollector, ExistingArmy = plan.Source, CollectorUnit = plan.Unit,
                MergeTarget = plan.MergeTarget, TargetHex = plan.Source.Hex, Score = score,
                Reason = plan.MergeTarget != null
                    ? $"задача «Экономика»: выделяет {plan.Unit.Name} из {plan.Source.Name} для добычи {type} — "
                        + $"остальной состав переходит в {plan.MergeTarget.Name}"
                    : $"задача «Экономика»: выделяет {plan.Unit.Name} из {plan.Source.Name} для добычи {type} — новая армия в гарнизоне",
            };

            public static AiDecision PlayCard(ArmyData existing, CardData card, AiManagementPlanner.CardRole role, float score) => new AiDecision
            {
                Kind = AiActionKind.PlayCard,
                ExistingArmy = existing,
                Card = card,
                Score = score,
                Reason = existing != null
                    ? $"пополняет {existing.Name} картой {card.Definition.displayName}{RoleLabel(role)}"
                    : $"новая армия под карту {card.Definition.displayName}{RoleLabel(role)}",
            };

            private static string RoleLabel(AiManagementPlanner.CardRole role)
            {
                switch (role)
                {
                    case AiManagementPlanner.CardRole.Recce: return " (Recce, соло)";
                    case AiManagementPlanner.CardRole.Hero: return " (герой)";
                    default: return "";
                }
            }

            // Менеджмент · капасити гарнизона — see AiManagementPlanner.FindGarrisonOverflow.
            public static AiDecision SplitGarrison(ArmyData garrison, IReadOnlyList<UnitData> unitsToMove, float score) => new AiDecision
            {
                Kind = AiActionKind.SplitGarrisonArmy,
                ExistingArmy = garrison,
                TargetHex = garrison.Hex,
                UnitsToMove = unitsToMove,
                Score = score,
                Reason = $"задача «Менеджмент»: гарнизон полон — выделяет новую армию ({unitsToMove.Count} юнит(ов))",
            };

            // Менеджмент · передача юнитов между армиями в базе — see
            // AiManagementPlanner.FindConsolidationMove.
            public static AiDecision Consolidate(AiManagementPlanner.ConsolidationMove move, float score) => new AiDecision
            {
                Kind = AiActionKind.ConsolidateUnits,
                ExistingArmy = move.Source,
                TargetHex = move.Source.Hex,
                ConsolidationMove = move,
                Score = score,
                Reason = move.Target.IsGarrison
                    ? $"задача «Менеджмент»: {move.Unit.Name} — одиночка, передаётся в гарнизон"
                    : $"задача «Менеджмент»: {move.Unit.Name} — одиночка, объединяется с {move.Target.Name}",
            };

            public static AiDecision Reserve(int currentSpare, float score) => new AiDecision
            {
                Kind = AiActionKind.ReserveArmy,
                Score = score,
                Reason = $"задача «Менеджмент»: резервная армия про запас ({currentSpare + 1}/{AiConfig.Current.maxSpareArmies})",
            };

            public static AiDecision Draw(float score) => new AiDecision
            {
                Kind = AiActionKind.DrawCard,
                Score = score,
                Reason = "задача «Менеджмент»: рука разыграна или недоступна по AP/ресурсам — остаточный добор",
            };

            public static AiDecision None(string reason) => new AiDecision { Kind = AiActionKind.Pass, Reason = reason };
        }

        public static IEnumerator RunTurn(PlayerSetupData player, AiTurnContext ctx, Action onDone)
        {
            if (player == null || ctx == null)
            {
                onDone?.Invoke();
                yield break;
            }

            PlayerRoot root = PlayerRootRegistry.FindFor(player);
            if (root == null)
            {
                onDone?.Invoke();
                yield break;
            }

            AiHandData hand = AiHandRegistry.GetOrCreate(player, ctx.StartingDeckCatalog, ctx.StartingHandSize);
            AiDebugLog.Write($"[AI] === {player.Nickname}'s turn begins — AP={root.ActionPoints}, "
                + $"armies={ArmyRegistry.AllForOwner(player).Count(a => !a.IsGarrison && !a.IsPrison)}, "
                + $"human={root.GetResource(ResourceType.Human)}, energy={root.GetResource(ResourceType.Energy)}, "
                + $"materials={root.GetResource(ResourceType.Materials)}, tech={root.GetResource(ResourceType.Tech)} ===");
            LogHand(player, hand);
            LogActiveTasks(player);

            // A pick that turns out unaffordable (terrain move cost eats the army's remaining
            // points before even the first step) just fails to move, same army and same target
            // every time Decide is asked again, since nothing about that army's state changed.
            // Tracked here so a stuck army is only ever tried once per turn — every continuation/
            // start-new tier below checks this before proposing a move, so a genuinely stuck
            // army is skipped in favour of the next candidate instead of burning the whole
            // MaxStepsPerTurn budget re-proposing the exact same failing move.
            var stuckScouts = new HashSet<ArmyData>();
            var pool = new AiResourcePool(player, root, hand);

            for (int step = 0; step < AiConfig.Current.maxStepsPerTurn; step++)
            {
                AiDecision decision = Decide(player, root, hand, ctx, stuckScouts, pool);
                AiDebugLog.Write($"[AI] {player.Nickname}: step {step + 1}/{AiConfig.Current.maxStepsPerTurn} — decided {decision.Kind} "
                    + $"(score {decision.Score:0.0}) — {decision.Reason}.");
                if (decision.Kind == AiActionKind.Pass)
                    break;
                // Wait only ever wins Decide's arbitration when nothing else scored — same
                // "nothing left this turn" situation Pass handles, just with an actual reason
                // attached (see EconomyWaitScore's own comment) instead of the old silent drop.
                // Re-deciding again this same turn would just re-propose the identical Wait for
                // the identical unchanged state, MaxStepsPerTurn times over, for nothing.
                if (decision.Kind == AiActionKind.Wait)
                {
                    yield return PerformDecision(player, decision, ctx);
                    break;
                }

                HexCoord? beforeHex = decision.Kind == AiActionKind.MoveArmy ? decision.ExistingArmy.Hex : (HexCoord?)null;
                yield return PerformDecision(player, decision, ctx);
                if (beforeHex.HasValue && decision.ExistingArmy.Hex.Equals(beforeHex.Value))
                    stuckScouts.Add(decision.ExistingArmy);

                // A draw/deploy just above may have consumed a card from `hand` — GetOrCreate is
                // idempotent (returns the same instance once created), so this just re-syncs the
                // local reference rather than creating anything new.
                hand = AiHandRegistry.GetOrCreate(player, ctx.StartingDeckCatalog, ctx.StartingHandSize);
                ctx.HumanCardHandUI?.RefreshAiHandDebugIfShowing(hand);
            }

            AiDebugLog.Write($"[AI] === {player.Nickname}'s turn ends — AP left={root.ActionPoints} ===");
            onDone?.Invoke();
        }

        // Pure and read-only — gathers every candidate action reachable from current state,
        // scores each on the shared scale AiConfig's own base-weight fields define (see
        // AiConfig.economyBaseWeight's own comment), and picks the single highest. This is the unified
        // arbiter: unlike the old fixed tier order, a category with an unusually strong candidate
        // CAN outrank a category that would normally go first, because the numbers actually
        // compete rather than one category short-circuiting the rest.
        //
        // Candidate sources, in the order they're gathered (order doesn't imply priority any
        // more — only Score does; this is just a stable read order):
        // 1) Continue in-flight AiTask work — one candidate per active task, across all three
        // kinds (BuildFacility/VisitHex/ScoutResourceHex). A task whose army is in `stuckScouts`
        // (already failed to move this turn) contributes no candidate, never retried this step.
        // 2) TryReturnHomeCandidates — AiArmyRoles.IsSoloHeroAwaitingEscort's own fallback once
        // an army has no active task and nothing nearby to visit.
        // 3) Start new AiTask work — Экономика (see TryStartEconomyCandidates's own comment on
        // why it's allowed to preempt a Разведка task's army), Разведка Задача 1, Задача 2 — all
        // three still respect their own caps (MaxConcurrentVisitHex/MaxConcurrentScoutResourceHex)
        // before generating any candidate at all.
        // 4) Менеджмент's own base upkeep — garrison-overflow split, lone-army consolidation (see
        // AiManagementPlanner.FindGarrisonOverflow/FindConsolidationMove), then "выложить карту из
        // руки" candidates, one per affordable Unit/Hero/Recce card in hand (see AiArmyRoles's own
        // class comment for the three shapes cards map to; Base/Facility cards are skipped
        // entirely — see TryPlayCardCandidates).
        // 5) Менеджмент's own leftover-AP fallbacks — a spare army, a fresh card draw.
        //
        // Only the winning candidate is committed (see Commit) — every other candidate built this
        // step (unregistered AiTask objects, PreemptedTask references) is simply discarded.
        private static AiDecision Decide(PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx,
            HashSet<ArmyData> stuckScouts, AiResourcePool pool)
        {
            // Claim every army already committed to a persistent task up front — keeps
            // AvailableArmies() (and thus every "start a NEW task" tier below) from re-offering
            // a busy army. Deliberately does NOT touch stuckScouts — that set is strictly
            // "already tried and failed to move THIS STEP" (see RunTurn's own comment), checked
            // separately by each continuation/start-new tier below.
            foreach (AiTask inFlight in AiTaskRegistry.TasksFor(player))
                if (inFlight.Army != null)
                    pool.ClaimArmy(inFlight.Army);

            var candidates = new List<AiDecision>();

            foreach (AiTask task in AiTaskRegistry.TasksFor(player).Where(t => t.Kind == AiTaskKind.BuildFacility).ToList())
            {
                if (stuckScouts.Contains(task.Army))
                    continue;
                AiDecision decision = AdvanceEconomyTask(player, root, ctx, task);
                if (decision != null)
                    candidates.Add(decision);
            }
            foreach (AiTask task in AiTaskRegistry.TasksFor(player).Where(t => t.Kind == AiTaskKind.VisitHex).ToList())
            {
                if (stuckScouts.Contains(task.Army))
                    continue;
                AiDecision decision = TryContinueVisitTask(player, root, ctx, task);
                if (decision != null)
                    candidates.Add(decision);
            }
            foreach (AiTask task in AiTaskRegistry.TasksFor(player).Where(t => t.Kind == AiTaskKind.ScoutResourceHex).ToList())
            {
                if (stuckScouts.Contains(task.Army))
                    continue;
                AiDecision decision = TryContinueScoutResourceTask(player, root, ctx, task);
                if (decision != null)
                    candidates.Add(decision);
            }
            foreach (AiTask task in AiTaskRegistry.TasksFor(player).Where(t => t.Kind == AiTaskKind.ResourcesScrap).ToList())
            {
                if (stuckScouts.Contains(task.Army))
                    continue;
                AiDecision decision = AdvanceResourcesScrapTask(player, root, task);
                if (decision != null)
                    candidates.Add(decision);
            }

            candidates.AddRange(TryReturnHomeCandidates(player, root, ctx, stuckScouts));
            candidates.AddRange(TryStartEconomyCandidates(player, root, ctx, stuckScouts));
            candidates.AddRange(TryStartResourcesScrapCandidates(player, pool));
            candidates.AddRange(TryStartCollectorDetachCandidates(player, root, pool));
            candidates.AddRange(TryStartVisitCandidates(player, root, ctx, pool, stuckScouts));
            candidates.AddRange(TryStartScoutResourceCandidates(player, root, ctx, pool, stuckScouts));

            ArmyData garrison = GarrisonArmyFor(player);
            AiDecision splitCandidate = TryGarrisonSplitCandidate(root, garrison);
            if (splitCandidate != null)
                candidates.Add(splitCandidate);
            AiDecision consolidateCandidate = TryConsolidationCandidate(player, garrison);
            if (consolidateCandidate != null)
                candidates.Add(consolidateCandidate);

            candidates.AddRange(TryPlayCardCandidates(player, root, hand));

            int spareArmies = ArmyRegistry.AllForOwner(player).Count(a => !a.IsGarrison && !a.IsPrison && a.Members.Count == 0);
            bool reservePreferred = AiManagementPlanner.IsPreferred(player, AiManagementPlanner.FallbackKind.ReserveArmy);
            if (spareArmies < AiConfig.Current.maxSpareArmies && root.CanSpendActionPoints(ArmyActions.CreateArmyApCost))
                candidates.Add(AiDecision.Reserve(spareArmies, reservePreferred
                    ? AiConfig.Current.managementFallbackHighScore : AiConfig.Current.managementFallbackLowScore));

            if (hand != null && hand.HasCardsLeftToDraw && root.CanSpendActionPoints(ctx.DrawApCost))
                candidates.Add(AiDecision.Draw(reservePreferred
                    ? AiConfig.Current.managementFallbackLowScore : AiConfig.Current.managementFallbackHighScore));

            AiDebugLog.Write($"[AI] {player.Nickname}: {candidates.Count} кандидат(ов) — "
                + string.Join(" | ", candidates.Select(c => $"{c.Kind}({c.Score:0.0}) {c.Reason}")));

            AiDecision best = null;
            foreach (AiDecision candidate in candidates)
                if (best == null || candidate.Score > best.Score)
                    best = candidate;

            if (best == null)
            {
                bool anyCardInHand = hand != null && hand.Hand.Count > 0;
                return AiDecision.None(anyCardInHand
                    ? "не хватает AP ни на что из доступного"
                    : "нечего делать — армии заняты, руке/AP нечего предложить");
            }

            Commit(player, best, pool);
            return best;
        }

        // Deferred mutation for whichever candidate Decide's own arbiter just picked — every
        // OTHER candidate built this step (still-unregistered AiTask objects, PreemptedTask
        // references) is simply discarded here, never touching AiTaskRegistry/AiResourcePool at
        // all. Keeps MaxConcurrentVisitHex/MaxConcurrentScoutResourceHex honest — generating N
        // scored candidates this step must never register more than the ONE that actually wins.
        private static void Commit(PlayerSetupData player, AiDecision decision, AiResourcePool pool)
        {
            if (decision.PreemptedTask != null)
            {
                AiDebugLog.Write($"[AI] {player.Nickname}: {decision.PreemptedTask.Army.Name} снят с задачи "
                    + $"«{decision.PreemptedTask.Kind}» ради постройки на ({decision.TargetHex.Q},{decision.TargetHex.R}) "
                    + "— старая задача помечена невыполненной.");
                AiTaskRegistry.Remove(player, decision.PreemptedTask);
            }

            if (decision.Task != null && !AiTaskRegistry.TasksFor(player).Contains(decision.Task))
            {
                AiTaskRegistry.Add(player, decision.Task);
                pool.ClaimArmy(decision.ExistingArmy);
                if (decision.Task.Kind == AiTaskKind.BuildFacility)
                    AiDebugLog.Write($"[AI] {player.Nickname}: начинает задачу «Экономика» — {decision.ExistingArmy.Name} идёт "
                        + $"строить {decision.Task.ResourceType} на ({decision.Task.TargetHex.Q},{decision.Task.TargetHex.R}).");
            }
        }

        // ---- Общая реакция на угрозу (Разведка · Задача 1/2, Экономика · Задача 1) ----

        // A KNOWN enemy army (AiMapMemory — honest memory, not live vision, same read
        // AiScoutPlanner's own homeRadius check already uses) within scoutFleeRadius of `army`'s
        // OWN current hex → retreat toward the garrison for ONE turn instead of whatever the
        // caller's own task would otherwise propose (Разведка's next scout target, or Экономика's
        // travel/build step). Called first, before that other logic, at every start AND
        // continuation site across all three tasks that use it.
        //
        // Neutral armies never trigger this — the project owner's own call: neutrals aren't worth
        // running from, only worth not fighting (see FindVisitTargetHex's own mayAttack/
        // IsEnemyWeaker check, which already declines an attack the mover would lose, regardless
        // of whether the hex holder is neutral or an enemy player).
        //
        // `task` is null for a not-yet-started task (see the two Разведка TryStart... callers —
        // Экономика always has one, a fresh task is run through AdvanceEconomyTask immediately,
        // see TryStartEconomyCandidates). Null always means free to flee. For an in-flight task,
        // fleeing twice in a row is deliberately NOT allowed (see AiTask.FledLastTurn's own
        // comment): a turn spent fleeing consumes the flag and skips this whole check next call,
        // so a persistent threat produces "flee, resume, flee, resume, ..." rather than a forced
        // march all the way home (Разведка) or the task being abandoned outright (Экономика's old
        // behaviour). FledLastTurn is always reset by this method — to true when it proposes a
        // flee, to false when it doesn't — so the caller never needs its own separate bookkeeping.
        private static AiScoutPlanner.ScoutTarget? TryFleeTarget(PlayerSetupData player, ArmyData army, AiTask task)
        {
            if (army == null)
                return null;

            if (task != null && task.FledLastTurn)
            {
                task.FledLastTurn = false;
                return null;
            }

            float worstKnownDefense = -1f;
            bool anyThreat = false;
            foreach (AiMapMemory.KnownEnemySighting sighting in
                     AiMapMemory.KnownEnemySightingsNear(player, new[] { army.Hex }, AiConfig.Current.scoutFleeRadius))
            {
                if (sighting.Owner != null && sighting.Owner.IsNeutral)
                    continue; // neutrals never trigger flight — see this method's own comment

                anyThreat = true;
                if (sighting.DefenseSum > worstKnownDefense)
                    worstKnownDefense = sighting.DefenseSum;
            }
            if (!anyThreat)
            {
                if (task != null)
                    task.FledLastTurn = false;
                return null;
            }

            if (AiArmyRoles.IsMakeshiftScoutCapable(army))
            {
                float ownAttack = army.Members.Where(m => !m.IsHero).Sum(m => m.Attack);
                if (ownAttack > worstKnownDefense)
                {
                    if (task != null)
                        task.FledLastTurn = false;
                    return null; // strong enough — no need to run
                }
            }

            if (task != null)
                task.FledLastTurn = true;
            HexCoord garrisonHex = GarrisonHexFor(player);
            return new AiScoutPlanner.ScoutTarget(garrisonHex, AiConfig.Current.scoutFleeBonus,
                "рядом известная вражеская армия — уходит в гарнизон на один ход");
        }

        // ---- Разведка · Задача 1 (Посещение хекса) ----

        private static AiDecision TryContinueVisitTask(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx, AiTask task)
        {
            if (task.Army?.Controller == null || !ArmyRegistry.AllForOwner(player).Contains(task.Army))
            {
                AiTaskRegistry.Remove(player, task);
                return null;
            }
            if (task.Army.CurrentMovement <= 0)
                return null;
            if (!task.Army.HasActivatedThisTurn && !root.CanSpendActionPoints(task.Army.ActivationApCost))
                return null;

            // Re-evaluated fresh every call rather than trusting the stored TargetHex — same
            // "непрерывная переоценка" principle the doc's own turn-execution section already
            // documents (an equally-good or better hex may have opened up since the task
            // started). Задача 1 has no single completion hex: it keeps proposing a next
            // unvisited target for as long as one exists nearby (see AiTaskKind's own comment),
            // so "nothing left" is what actually frees the army, not "arrived once".
            //
            // mayAttack/requireVisible/homeRadius are re-derived from the army's own LIVE
            // composition every call, exactly like TryStartVisitCandidates does when the task
            // first starts — a solo hero (AiArmyRoles.IsSoloHeroAwaitingEscort) must keep its
            // homeRadius leash on every single continuation turn, not just turn one, or the whole
            // point of that leash (never drag a fragile lone hero far from home) evaporates the
            // moment the task starts continuing instead of starting.
            AiScoutPlanner.ScoutTarget? target = TryFleeTarget(player, task.Army, task);
            if (!target.HasValue)
            {
                bool mayAttack = AiArmyRoles.IsMakeshiftScoutCapable(task.Army);
                bool requireVisible = AiArmyRoles.IsSoloHeroAwaitingEscort(task.Army);
                int? homeRadius = requireVisible ? AiConfig.Current.soloHeroHomeRadius : (int?)null;
                target = AiScoutPlanner.FindVisitTargetHex(player, task.Army, ctx.Map, mayAttack, requireVisible, homeRadius);
            }
            if (!target.HasValue)
            {
                AiTaskRegistry.Remove(player, task);
                return null;
            }
            task.TargetHex = target.Value.Hex;
            return AiDecision.Move(task.Army, target.Value, task, AiConfig.Current.reconBaseWeight + target.Value.Score);
        }

        private static List<AiDecision> TryStartVisitCandidates(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            AiResourcePool pool, HashSet<ArmyData> stuckScouts)
        {
            var results = new List<AiDecision>();
            if (AiTaskRegistry.CountActive(player, AiTaskKind.VisitHex) >= AiConfig.Current.maxConcurrentVisitHex)
                return results;

            // Every eligible composition contributes its own candidates now — a real Recce scout,
            // a hero-led army already sturdy enough to fight (AiArmyRoles.IsMakeshiftScoutCapable
            // — also the ONLY composition allowed to attack, see FindVisitTargetHex's own
            // mayAttack param), and a lone hero with no escorts yet — restricted to already-
            // visible ground within a couple hexes of the citadel (see AiScoutPlanner's own
            // requireVisible/homeRadius) since it has no Recce vision boost and nothing to fall
            // back on in a real fight. Which composition actually gets picked this step is now
            // Decide's own Score comparison, not read order (see ReconBaseWeight's own comment).
            results.AddRange(TryStartVisitCandidatesFor(player, root, ctx, pool, stuckScouts, AiArmyRoles.IsScoutCapable, mayAttack: false));
            results.AddRange(TryStartVisitCandidatesFor(player, root, ctx, pool, stuckScouts, AiArmyRoles.IsMakeshiftScoutCapable, mayAttack: true));
            results.AddRange(TryStartVisitCandidatesFor(player, root, ctx, pool, stuckScouts, AiArmyRoles.IsSoloHeroAwaitingEscort,
                mayAttack: false, requireVisible: true, homeRadius: AiConfig.Current.soloHeroHomeRadius));
            return results;
        }

        private static List<AiDecision> TryStartVisitCandidatesFor(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            AiResourcePool pool, HashSet<ArmyData> stuckScouts, Func<ArmyData, bool> isEligible, bool mayAttack,
            bool requireVisible = false, int? homeRadius = null)
        {
            var results = new List<AiDecision>();
            foreach (ArmyData army in pool.AvailableArmies())
            {
                if (!isEligible(army) || army.CurrentMovement <= 0 || army.Controller == null || stuckScouts.Contains(army))
                    continue;
                if (!army.HasActivatedThisTurn && !root.CanSpendActionPoints(army.ActivationApCost))
                    continue;

                // No task exists yet to carry FledLastTurn — always free to flee on first
                // encounter, then stamp the flag onto the freshly created task below so the very
                // next continuation call (see TryContinueVisitTask) already honours the one-turn
                // cap.
                AiScoutPlanner.ScoutTarget? fleeTarget = TryFleeTarget(player, army, null);
                AiScoutPlanner.ScoutTarget? target = fleeTarget
                    ?? AiScoutPlanner.FindVisitTargetHex(player, army, ctx.Map, mayAttack, requireVisible, homeRadius);
                if (!target.HasValue)
                    continue;

                var task = new AiTask
                {
                    Kind = AiTaskKind.VisitHex, Army = army, TargetHex = target.Value.Hex, Reason = target.Value.Reason,
                    FledLastTurn = fleeTarget.HasValue,
                };
                results.Add(AiDecision.Move(army, target.Value, task, AiConfig.Current.reconBaseWeight + target.Value.Score));
            }
            return results;
        }

        // AiArmyRoles.IsSoloHeroAwaitingEscort's own other half: once it has no active Задача 1
        // task and nothing left nearby to visit, walk it back to the garrison instead of leaving
        // it wherever it happened to stop — that's where TryPlayCard's own Unit-role routing
        // will actually find it the next time an escort card is affordable. A no-op once it's
        // already there.
        private static List<AiDecision> TryReturnHomeCandidates(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            HashSet<ArmyData> stuckScouts)
        {
            var results = new List<AiDecision>();
            HexCoord garrisonHex = GarrisonHexFor(player);
            foreach (ArmyData army in ArmyRegistry.AllForOwner(player))
            {
                if (!AiArmyRoles.IsSoloHeroAwaitingEscort(army) || army.CurrentMovement <= 0 || army.Controller == null
                    || stuckScouts.Contains(army) || army.Hex.Equals(garrisonHex) || AiTaskRegistry.TaskFor(player, army) != null)
                    continue;
                if (!army.HasActivatedThisTurn && !root.CanSpendActionPoints(army.ActivationApCost))
                    continue;
                var target = new AiScoutPlanner.ScoutTarget(garrisonHex, 0f,
                    "рядом нечего посетить — возвращается в цитадель дожидаться подкрепления");
                results.Add(AiDecision.Move(army, target, null, AiConfig.Current.managementReturnHomeScore));
            }
            return results;
        }

        // ---- Разведка · Задача 2 (Поиск хекса с ресурсом) ----

        private static AiDecision TryContinueScoutResourceTask(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx, AiTask task)
        {
            if (task.Army?.Controller == null || !ArmyRegistry.AllForOwner(player).Contains(task.Army))
            {
                AiTaskRegistry.Remove(player, task);
                return null;
            }

            // Goal already satisfied — a free known hex of the wanted type turned up (maybe even
            // via a DIFFERENT army's own vision) — free this army immediately rather than making
            // it walk further once the actual objective (see AiTaskKind's own comment) is met.
            if (task.ResourceType.HasValue
                && AiMapMemory.KnownResourceHexesOfType(player, task.ResourceType.Value).Any(hex => BuildingRegistry.FindAt(hex) == null))
            {
                AiDebugLog.Write($"[AI] {player.Nickname}: {task.Army.Name} — известный свободный хекс с "
                    + $"{task.ResourceType} найден, задача «Разведка» (поиск ресурса) выполнена.");
                AiTaskRegistry.Remove(player, task);
                return null;
            }

            if (task.Army.CurrentMovement <= 0)
                return null;
            if (!task.Army.HasActivatedThisTurn && !root.CanSpendActionPoints(task.Army.ActivationApCost))
                return null;

            AiScoutPlanner.ScoutTarget? target = TryFleeTarget(player, task.Army, task)
                ?? AiScoutPlanner.FindResourceScoutTargetHex(player, task.Army, ctx.Map);
            if (!target.HasValue)
            {
                AiTaskRegistry.Remove(player, task);
                return null;
            }
            task.TargetHex = target.Value.Hex;
            return AiDecision.Move(task.Army, target.Value, task, AiConfig.Current.reconBaseWeight + target.Value.Score);
        }

        private static List<AiDecision> TryStartScoutResourceCandidates(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            AiResourcePool pool, HashSet<ArmyData> stuckScouts)
        {
            var results = new List<AiDecision>();
            if (AiTaskRegistry.CountActive(player, AiTaskKind.ScoutResourceHex) >= AiConfig.Current.maxConcurrentScoutResourceHex)
                return results;

            ResourceType? wantedType = WantedResourceType(player);
            if (wantedType == null)
                return results; // a free known hex of every type already exists — see WantedResourceType's own comment

            foreach (ArmyData army in pool.AvailableArmies())
            {
                if (!AiArmyRoles.IsScoutCapable(army) || army.CurrentMovement <= 0 || army.Controller == null
                    || stuckScouts.Contains(army))
                    continue;
                if (!army.HasActivatedThisTurn && !root.CanSpendActionPoints(army.ActivationApCost))
                    continue;

                AiScoutPlanner.ScoutTarget? fleeTarget = TryFleeTarget(player, army, null);
                AiScoutPlanner.ScoutTarget? target = fleeTarget
                    ?? AiScoutPlanner.FindResourceScoutTargetHex(player, army, ctx.Map);
                if (!target.HasValue)
                    continue;

                var task = new AiTask
                {
                    Kind = AiTaskKind.ScoutResourceHex, Army = army, TargetHex = target.Value.Hex, ResourceType = wantedType,
                    FledLastTurn = fleeTarget.HasValue,
                };
                results.Add(AiDecision.Move(army, target.Value, task, AiConfig.Current.reconBaseWeight + target.Value.Score));
            }
            return results;
        }

        // Which ResourceType Разведка · Задача 2 should be hunting for right now — the type the
        // player's own stockpile is currently lowest on, the same cheap "старается не отставать"
        // heuristic AiGoalScorer.IncomeBehindBonus already uses rather than a real deck-cost
        // analysis (doc 2.2's own "приоритет типа ресурса — по стоимости карт в колоде" is a
        // documented later refinement, not this pass). Skips any type that already has a free
        // known hex somewhere — no point hunting for what's already found and unclaimed — AND any
        // type an already-active ScoutResourceHex task is hunting for (per the project owner's own
        // call: two armies hunting the SAME type is wasted coverage, a second army should chase a
        // DIFFERENT type instead so the two searches cover each other rather than overlap).
        private static ResourceType? WantedResourceType(PlayerSetupData player)
        {
            PlayerRoot root = PlayerRootRegistry.FindFor(player);
            if (root == null)
                return null;

            var alreadyHunting = new HashSet<ResourceType>(AiTaskRegistry.TasksFor(player)
                .Where(t => t.Kind == AiTaskKind.ScoutResourceHex && t.ResourceType.HasValue)
                .Select(t => t.ResourceType.Value));

            ResourceType? best = null;
            int bestAmount = int.MaxValue;
            foreach (ResourceType type in (ResourceType[])Enum.GetValues(typeof(ResourceType)))
            {
                if (alreadyHunting.Contains(type))
                    continue;
                if (AiMapMemory.KnownResourceHexesOfType(player, type).Any(hex => BuildingRegistry.FindAt(hex) == null))
                    continue;
                int amount = root.GetResource(type);
                if (amount < bestAmount)
                {
                    bestAmount = amount;
                    best = type;
                }
            }
            return best;
        }

        // ---- Экономика · Задача 1 (Постройка добывающей facility) ----

        // "если найден хекс с ресурсами, то туда нужно отправить ближайшего героя" — the project
        // owner's own spec: tries every free known resource hex not already targeted by an
        // existing BuildFacility task, and for each, the nearest hero overall (see
        // AiEconomyPlanner.FindNearestHero) — INCLUDING a hero another task already claimed. A
        // candidate for a hero already mid-Разведка carries PreemptedTask so Decide's own Commit
        // step can drop that old task IF (and only if) this specific candidate ends up winning
        // the step's arbitration — "его текущее задание можно отложить в угоду экономии
        // времени... а потом вернуться к отложенному заданию (которое будет помечено как
        // невыполненное)". A hero already on a DIFFERENT BuildFacility task is never offered a
        // candidate here at all — that would only shuffle the same work around, not add any.
        private static List<AiDecision> TryStartEconomyCandidates(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            HashSet<ArmyData> stuckScouts)
        {
            var results = new List<AiDecision>();
            var alreadyTargeted = new HashSet<HexCoord>(AiTaskRegistry.TasksFor(player)
                .Where(t => t.Kind == AiTaskKind.BuildFacility)
                .Select(t => t.TargetHex));

            foreach (HexCoord hex in HexResourceBonusRegistry.AllBonusHexes())
            {
                if (!AiMapMemory.IsResourceHexKnown(player, hex) || BuildingRegistry.FindAt(hex) != null || alreadyTargeted.Contains(hex))
                    continue;

                // Resolved BEFORE touching any existing task below — a hex whose bonus turns out
                // to carry no real amount (DominantResourceType's own "shouldn't happen, but
                // don't assume" case) must never cost a Разведка task its own preemption for
                // nothing.
                ResourceType? resourceType = AiEconomyPlanner.DominantResourceType(hex);
                if (resourceType == null)
                    continue;

                ArmyData hero = AiEconomyPlanner.FindNearestHero(player, hex);
                if (hero == null || stuckScouts.Contains(hero))
                    continue;

                AiTask existingTask = AiTaskRegistry.TaskFor(player, hero);
                if (existingTask != null && existingTask.Kind == AiTaskKind.BuildFacility)
                    continue; // already building elsewhere — preempting it would only shuffle work, not add any

                var task = new AiTask { Kind = AiTaskKind.BuildFacility, Army = hero, TargetHex = hex, ResourceType = resourceType };
                AiDecision decision = AdvanceEconomyTask(player, root, ctx, task);
                if (decision == null)
                    continue;
                decision.PreemptedTask = existingTask;
                results.Add(decision);
            }
            return results;
        }

        // Drives `task` one stage forward: still travelling → a move decision toward the fixed
        // target hex; arrived → a BuildFacility decision once fully reserved (see
        // AiResourceReservation), or an explicit low-score Wait otherwise — still AP-short or
        // still saving up (no separate timeout for "arrived but still saving up" — see the
        // task's own BuildAttempts, which only counts actual failed build calls; Wait never
        // touches it). Checked first in Decide's own continuation order — a task
        // already standing at its target should finish building immediately, not wait behind
        // Разведка. Also doubles as TryStartEconomyCandidates' own first-turn gate — a brand new
        // task is run through here immediately, so the safety/reservation checks below apply
        // identically whether this is turn one or turn ten of the task.
        private static AiDecision AdvanceEconomyTask(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx, AiTask task)
        {
            if (task.Army?.Controller == null || !ArmyRegistry.AllForOwner(player).Contains(task.Army))
            {
                // Bound army is gone (lost in combat, etc.) — self-heal rather than leave a dead
                // reference parked in the registry forever; a fresh task can start again later.
                AiResourceReservation.Release(task);
                AiTaskRegistry.Remove(player, task);
                return null;
            }

            // A known NEUTRAL army guarding the target hex isn't a threat to react to (see
            // TryFleeTarget's own neutral exemption below) — it's a reason this specific hex was
            // never a good build spot. Re-checked every call — a neutral wandering within range
            // AFTER the task started cancels it outright, same shape TryStartEconomyCandidates'
            // own filtering effectively gets for free (it calls this same method on a brand-new
            // task, see this method's own class comment), so a better, unguarded hex gets tried
            // instead next step.
            if (AiMapMemory.HasKnownNeutralWithin(player, task.TargetHex, AiConfig.Current.neutralBuildAvoidRadius))
            {
                if (AiTaskRegistry.TasksFor(player).Contains(task))
                    AiDebugLog.Write($"[AI] {player.Nickname}: {task.Army.Name} — известная нейтральная армия в "
                        + $"{AiConfig.Current.neutralBuildAvoidRadius} хексах от ({task.TargetHex.Q},{task.TargetHex.R}), "
                        + "задача «Экономика» отменена.");
                AiResourceReservation.Release(task);
                AiTaskRegistry.Remove(player, task);
                return null;
            }

            // Same one-turn retreat rule Разведка uses (see TryFleeTarget) — the project owner's
            // own call to stop cancelling the whole task the moment a known threat wanders near
            // (that used to abandon the hero's entire trip and release the reservation outright).
            // Reroutes THIS task toward the garrison for a single turn instead, reservation left
            // untouched, then resumes travel/build next turn regardless of whether the threat is
            // still there — checked every call, starting OR continuing, same as Разведка.
            AiScoutPlanner.ScoutTarget? fleeTarget = TryFleeTarget(player, task.Army, task);
            if (fleeTarget.HasValue)
                return AiDecision.Move(task.Army, fleeTarget.Value, task, AiConfig.Current.economyBaseWeight + fleeTarget.Value.Score);

            CardDefinition definition = ctx.GameConfig?.extractionFacilityCards != null && task.ResourceType.HasValue
                && (int)task.ResourceType.Value < ctx.GameConfig.extractionFacilityCards.Length
                ? ctx.GameConfig.extractionFacilityCards[(int)task.ResourceType.Value]
                : null;
            // Tops up toward this turn's own share even while still travelling — "по мере
            // поступления ресурсов" per the owner's own spec, not just once the hero arrives.
            if (definition != null)
                AiResourceReservation.TopUp(root, player, task, definition.resourceCost);

            if (!task.Army.Hex.Equals(task.TargetHex))
            {
                if (task.Army.CurrentMovement <= 0)
                    return null;
                if (!task.Army.HasActivatedThisTurn && !root.CanSpendActionPoints(task.Army.ActivationApCost))
                    return null;

                // A BuildFacility hero has no way to react to an enemy it stumbles onto — a
                // hero-only army can't fight OR hunt yet (see Game.Combat.BattleInitiator's own
                // note), so contact with anything sitting on an unvisited hex would just be
                // silently ignored instead of stopping the hero. Routed one hex at a time,
                // through only-already-visited ground (never straight through the fog, even
                // toward a target the AI otherwise already knows about via AiMapMemory) so this
                // task never walks the hero blind — see HexPathfinder.FindPath's own blockHex.
                // The target hex itself is exempted: that's the one hex this trip is allowed to
                // step onto without having visited it first.
                HexCoord? nextStep = FindNextVisitedStep(ctx.Map, task.Army, task.TargetHex);
                if (nextStep == null)
                    return null; // no currently-known safe route yet — wait for more of the map to be scouted
                var target = new AiScoutPlanner.ScoutTarget(nextStep.Value, 0f,
                    $"задача «Экономика»: везёт героя строить {task.ResourceType}");
                float score = AiConfig.Current.economyBaseWeight + EconomyHexScore(player, task.TargetHex) + AiGoalScorer.IncomeBehindBonus(player);
                return AiDecision.Move(task.Army, target, task, score);
            }

            if (definition == null)
                return null;

            if (!root.CanSpendActionPoints(definition.apCost))
                return AiDecision.Wait(task, $"задача «Экономика»: {task.Army.Name} на месте, но не хватает AP "
                    + $"чтобы построить {task.ResourceType} на ({task.TargetHex.Q},{task.TargetHex.R}) — ждёт");

            // Belt-and-suspenders on top of IsFullyReserved's own virtual ledger: a direct read of
            // PlayerRoot's real stockpile, the exact same check TryBuildExtractionFacility itself
            // makes (see ResourceCost.CanAfford) — so the AI never even attempts a build the game
            // would actually reject. Per the project owner's own report, the ledger check alone
            // still let a build attempt through short on real resources (task.BuildAttempts
            // ticking up for no reason visible in the reservation log); simplest fix is refusing
            // to build unless BOTH agree, and just waiting on the hex instead.
            if (!AiResourceReservation.IsFullyReserved(task, definition.resourceCost) || !definition.resourceCost.CanAfford(root))
                return AiDecision.Wait(task, $"задача «Экономика»: {task.Army.Name} на месте, копит ресурсы "
                    + $"на {task.ResourceType} на ({task.TargetHex.Q},{task.TargetHex.R}) — ждёт");

            return AiDecision.BuildFacility(task, AiConfig.Current.economyBaseWeight + AiConfig.Current.buildFacilityReadyBonus);
        }

        // One step of a BuildFacility hero's own route toward `targetHex` — hard-restricted to
        // hexes `army`'s owner has already VISITED (see VisionSystem.IsVisited; broader than just
        // currently visible), `targetHex` itself always exempted since that's the one hex this
        // trip is inherently allowed to arrive at unvisited. Null if no such route exists yet
        // (fully boxed in by fog on every side) — AdvanceEconomyTask's own caller treats that as
        // "nothing to do this step", same as any other unaffordable/blocked move candidate,
        // rather than falling back to the unsafe direct route.
        private static HexCoord? FindNextVisitedStep(HexMap map, ArmyData army, HexCoord targetHex)
        {
            HexPath path = HexPathfinder.FindPath(map, army.Hex, targetHex,
                blockHex: hex => !hex.Equals(targetHex) && !VisionSystem.IsVisited(army.Owner, hex));
            if (path == null || path.Hexes.Count < 2)
                return null;
            return path.Hexes[1];
        }

        // AiGoalScorer.ScoreExpandEconomyHex's own proximity term for one SPECIFIC hex, plus the
        // per-actor IncomeBehindBonus offset — the unified arbiter's own Economy score for a
        // candidate tied to a fixed hex (a BuildFacility task's own TargetHex, or one particular
        // free known hex TryStartEconomyCandidates is scoring among several). Recomputes
        // AiGoalScorer.OwnHexes(player) each call rather than threading it through every caller —
        // cheap relative to the rest of a Decide() step (same brute-force style AiGoalScorer
        // itself already uses), not worth the extra parameter plumbing.
        private static float EconomyHexScore(PlayerSetupData player, HexCoord hex)
        {
            List<HexCoord> ownHexes = AiGoalScorer.OwnHexes(player);
            return AiGoalScorer.ScoreExpandEconomyHex(player, hex, ownHexes) ?? 0f;
        }

        // ---- Экономика · Задача 2 (Добыча без постройки facility) ----

        // Whether `hex` already has a real facility covering `type` — either because a BuildFacility
        // task finished there, or the citadel itself sat there all along. Shared by both "should I
        // even start scrapping this hex" (below) and AdvanceResourcesScrapTask's own "am I now
        // redundant" check — a real facility always wins, per the project owner's own "добыча
        // продолжается пока там не будет построена добывающая facility".
        private static bool HasExtractionFacility(HexCoord hex, ResourceType type)
        {
            BuildingData building = BuildingRegistry.FindAt(hex);
            return building != null && building.HasFacilityWithAbility(BuildingAbilities.CollectAbilityFor(type));
        }

        // "если найден такой хекс и такой юнит есть в составе одной из армий" — the project
        // owner's own spec: an already-solo collector (see AiEconomyPlanner.FindNearestSoloCollector)
        // walks straight to the nearest still-unclaimed free known resource hex matching its own
        // ability, no BuildingRegistry occupancy check at all (unlike Задача 1 — sharing a hex with
        // a BuildFacility hero, or even that hex's own future facility, is fine; see
        // AdvanceResourcesScrapTask's own comment on why the two tasks coexist on purpose).
        // ResourceScrapBaseWeight alone (no readiness bonus like BuildFacilityReadyBonus) since
        // there's no separate "arrived, now commit" step — arriving IS the whole task.
        private static List<AiDecision> TryStartResourcesScrapCandidates(PlayerSetupData player, AiResourcePool pool)
        {
            var results = new List<AiDecision>();
            var alreadyTargeted = new HashSet<HexCoord>(AiTaskRegistry.TasksFor(player)
                .Where(t => t.Kind == AiTaskKind.ResourcesScrap)
                .Select(t => t.TargetHex));

            foreach (HexCoord hex in HexResourceBonusRegistry.AllBonusHexes())
            {
                if (!AiMapMemory.IsResourceHexKnown(player, hex) || alreadyTargeted.Contains(hex))
                    continue;
                ResourceType? resourceType = AiEconomyPlanner.DominantResourceType(hex);
                if (resourceType == null || HasExtractionFacility(hex, resourceType.Value))
                    continue;

                ArmyData collector = AiEconomyPlanner.FindNearestSoloCollector(player, hex, resourceType.Value, pool);
                if (collector == null || collector.CurrentMovement <= 0)
                    continue;

                var task = new AiTask { Kind = AiTaskKind.ResourcesScrap, Army = collector, TargetHex = hex, ResourceType = resourceType };
                var target = new AiScoutPlanner.ScoutTarget(hex, 0f,
                    $"задача «Экономика»: {collector.Name} идёт добывать {resourceType} на ({hex.Q},{hex.R}) без стройки");
                float score = AiConfig.Current.ResourceScrapBaseWeight + EconomyHexScore(player, hex) + AiGoalScorer.IncomeBehindBonus(player);
                results.Add(AiDecision.Move(collector, target, task, score));
            }
            return results;
        }

        // Экономика · Задача 2's own prep step — only offered once TryStartResourcesScrapCandidates
        // itself finds no ready solo collector for a given hex's type, so the two tiers never both
        // fire for the same hex the same step. See AiEconomyPlanner.FindCollectorDetachPlan for the
        // two ways this can resolve; a fresh-army plan additionally needs CreateArmyApCost checked
        // here (the planner itself is pure/AP-agnostic, same "checked before proposing" rule every
        // other candidate in this file follows).
        private static List<AiDecision> TryStartCollectorDetachCandidates(PlayerSetupData player, PlayerRoot root, AiResourcePool pool)
        {
            var results = new List<AiDecision>();
            var alreadyTargeted = new HashSet<HexCoord>(AiTaskRegistry.TasksFor(player)
                .Where(t => t.Kind == AiTaskKind.ResourcesScrap)
                .Select(t => t.TargetHex));
            HexCoord garrisonHex = GarrisonHexFor(player);

            foreach (HexCoord hex in HexResourceBonusRegistry.AllBonusHexes())
            {
                if (!AiMapMemory.IsResourceHexKnown(player, hex) || alreadyTargeted.Contains(hex))
                    continue;
                ResourceType? resourceType = AiEconomyPlanner.DominantResourceType(hex);
                if (resourceType == null || HasExtractionFacility(hex, resourceType.Value))
                    continue;
                if (AiEconomyPlanner.FindNearestSoloCollector(player, hex, resourceType.Value, pool) != null)
                    continue; // already have one ready to walk — nothing to detach

                AiEconomyPlanner.CollectorDetachPlan? plan = AiEconomyPlanner.FindCollectorDetachPlan(
                    player, resourceType.Value, garrisonHex, pool);
                if (plan == null)
                    continue;
                if (plan.Value.MergeTarget == null && !root.CanSpendActionPoints(ArmyActions.CreateArmyApCost))
                    continue;

                results.Add(AiDecision.DetachCollector(plan.Value, resourceType.Value, AiConfig.Current.ResourceScrapDetachScore));
            }
            return results;
        }

        // Drives an active Задача 2 task one stage forward — mirrors AdvanceEconomyTask's own
        // shape (safety check, then travel-or-done) but with no resource/AP gate at all on
        // arrival: the whole point is that scrapping costs nothing, so there's no "still saving
        // up" stage to wait through. Once arrived, this returns null every turn from then on —
        // the passive per-turn payout itself lives entirely in GameTurnController.
        // CollectArmyIncomeAt, outside the AI decision loop, so there's nothing left to decide
        // while the task just sits there being useful. Two ways out: a KNOWN threat wanders
        // within EconomySafetyRadius (same leash BuildFacility uses), or a real facility
        // eventually gets built on this exact hex — "добыча продолжается пока там не будет
        // построена добывающая facility" — at which point the collector is simply freed to be
        // picked up by whatever candidate wants it next.
        private static AiDecision AdvanceResourcesScrapTask(PlayerSetupData player, PlayerRoot root, AiTask task)
        {
            if (task.Army?.Controller == null || !ArmyRegistry.AllForOwner(player).Contains(task.Army))
            {
                AiTaskRegistry.Remove(player, task);
                return null;
            }

            if (AiMapMemory.HasKnownEnemyWithin(player, task.TargetHex, AiConfig.Current.economySafetyRadius))
            {
                AiDebugLog.Write($"[AI] {player.Nickname}: {task.Army.Name} — известная армия в {AiConfig.Current.economySafetyRadius} "
                    + $"хексах от ({task.TargetHex.Q},{task.TargetHex.R}), задача «Экономика» (добыча) отменена.");
                AiTaskRegistry.Remove(player, task);
                return null;
            }

            if (task.ResourceType.HasValue && HasExtractionFacility(task.TargetHex, task.ResourceType.Value))
            {
                AiDebugLog.Write($"[AI] {player.Nickname}: {task.Army.Name} — на ({task.TargetHex.Q},{task.TargetHex.R}) "
                    + "построена добывающая facility, задача «Экономика» (добыча) завершена, юнит свободен.");
                AiTaskRegistry.Remove(player, task);
                return null;
            }

            if (!task.Army.Hex.Equals(task.TargetHex))
            {
                if (task.Army.CurrentMovement <= 0)
                    return null;
                if (!task.Army.HasActivatedThisTurn && !root.CanSpendActionPoints(task.Army.ActivationApCost))
                    return null;
                var target = new AiScoutPlanner.ScoutTarget(task.TargetHex, 0f,
                    $"задача «Экономика»: {task.Army.Name} идёт добывать {task.ResourceType} на "
                        + $"({task.TargetHex.Q},{task.TargetHex.R}) без стройки");
                float score = AiConfig.Current.ResourceScrapBaseWeight + EconomyHexScore(player, task.TargetHex) + AiGoalScorer.IncomeBehindBonus(player);
                return AiDecision.Move(task.Army, target, task, score);
            }

            return null; // arrived and collecting — nothing left to decide, see this method's own comment
        }

        // ---- Cards / reserve / draw / base upkeep (Менеджмент's own "мелкие" steps) ----

        // One candidate per affordable Unit/Hero/Recce card in hand — Base/Facility cards are
        // skipped entirely, same as before (see the class's own Decide comment, point 4). Every
        // card gets its own role read straight off AiManagementPlanner.IsRecceCard/cardType (not
        // just the first Recce card found in hand, unlike the old fixed-tier version) — a second
        // Recce card in hand now correctly gets routed as a solo Recce party too, instead of
        // falling through to plain Unit/Hero placement rules. Placement itself (who has room,
        // whether it's even affordable) lives in AiManagementPlanner.FindPlacement — this only
        // assigns the Score and builds the AiDecision.
        private static List<AiDecision> TryPlayCardCandidates(PlayerSetupData player, PlayerRoot root, AiHandData hand)
        {
            var results = new List<AiDecision>();
            if (hand == null)
                return results;

            foreach (CardData card in hand.Hand)
            {
                if (!AiManagementPlanner.IsUnitOrHeroCard(card))
                    continue;
                AiManagementPlanner.CardRole role = AiManagementPlanner.IsRecceCard(card) ? AiManagementPlanner.CardRole.Recce
                    : card.Definition.cardType == CardType.Hero ? AiManagementPlanner.CardRole.Hero
                    : AiManagementPlanner.CardRole.Unit;
                float score = AiConfig.Current.managementBaseWeight
                    + (role == AiManagementPlanner.CardRole.Recce ? AiConfig.Current.playRecceCardBonus : 0f);
                AiManagementPlanner.CardPlacement? placement = AiManagementPlanner.FindPlacement(player, root, card, role);
                if (placement.HasValue)
                    results.Add(AiDecision.PlayCard(placement.Value.ExistingArmy, card, role, score));
            }
            return results;
        }

        // Менеджмент · капасити гарнизона — see AiManagementPlanner.FindGarrisonOverflow's own
        // comment for why this moves just enough members to open one slot rather than an
        // arbitrary batch. Gated on ArmyActions.CreateArmyApCost here, before proposing, same
        // "checked before proposing" rule every other candidate in this file already follows.
        private static AiDecision TryGarrisonSplitCandidate(PlayerRoot root, ArmyData garrison)
        {
            if (garrison == null || !root.CanSpendActionPoints(ArmyActions.CreateArmyApCost))
                return null;
            IReadOnlyList<UnitData> overflow = AiManagementPlanner.FindGarrisonOverflow(garrison);
            return overflow != null && overflow.Count > 0 ? AiDecision.SplitGarrison(garrison, overflow, AiConfig.Current.managementReorgScore) : null;
        }

        // Менеджмент · передача юнитов между армиями в базе — see
        // AiManagementPlanner.FindConsolidationMove's own comment for scope/exclusions.
        private static AiDecision TryConsolidationCandidate(PlayerSetupData player, ArmyData garrison)
        {
            HexCoord garrisonHex = GarrisonHexFor(player);
            AiManagementPlanner.ConsolidationMove? move = AiManagementPlanner.FindConsolidationMove(player, garrisonHex, garrison);
            return move.HasValue ? AiDecision.Consolidate(move.Value, AiConfig.Current.managementReorgScore) : null;
        }

        private static IEnumerator PerformDecision(PlayerSetupData player, AiDecision decision, AiTurnContext ctx)
        {
            switch (decision.Kind)
            {
                case AiActionKind.MoveArmy:
                    yield return MoveArmyRoutine(player, decision, ctx);
                    break;
                case AiActionKind.PlayCard:
                    yield return PlayCardRoutine(player, decision, ctx);
                    break;
                case AiActionKind.ReserveArmy:
                    yield return ReserveArmyRoutine(player, ctx);
                    break;
                case AiActionKind.DrawCard:
                    yield return DrawCardRoutine(player, ctx);
                    break;
                case AiActionKind.BuildFacility:
                    yield return BuildFacilityRoutine(player, decision, ctx);
                    break;
                case AiActionKind.SplitGarrisonArmy:
                    yield return SplitGarrisonArmyRoutine(player, decision, ctx);
                    break;
                case AiActionKind.ConsolidateUnits:
                    yield return ConsolidateUnitsRoutine(player, decision, ctx);
                    break;
                case AiActionKind.DetachCollector:
                    yield return DetachCollectorRoutine(player, decision, ctx);
                    break;
                case AiActionKind.Wait:
                    yield return WaitStep(ctx);
                    break;
            }
        }

        private static IEnumerator MoveArmyRoutine(PlayerSetupData player, AiDecision decision, AiTurnContext ctx)
        {
            ArmyData army = decision.ExistingArmy;
            if (army?.Controller == null)
                yield break;

            AiDebugLog.Write($"[AI] {player.Nickname}: {army.Name} (movement={army.CurrentMovement}/{army.MaxMovement}) "
                + $"из ({army.Hex.Q},{army.Hex.R}) идёт к ({decision.TargetHex.Q},{decision.TargetHex.R}) — {decision.Reason}.");

            yield return PanTo(ctx, army.Hex);
            // Read-only — a human could otherwise drag units around inside the popup while it's
            // only meant to show what the AI is doing (map-click input is blocked during another
            // player's turn, but nothing gates in-panel dragging on its own — see
            // ArmyViewerModalUI.ShowReadOnly's own comment).
            if (ctx.ArmyViewerModal != null)
                ctx.ArmyViewerModal.ShowReadOnly(army);
            yield return WaitStep(ctx);

            HexCoord destination = decision.TargetHex;
            yield return PanTo(ctx, destination);

            HexCoord before = army.Hex;
            ctx.HexSelection?.IssueMoveOrder(army.Controller, destination);
            if (army.Controller != null)
                yield return new WaitUntil(() => !army.Controller.IsMoving);

            if (ctx.ArmyViewerModal != null)
                ctx.ArmyViewerModal.Hide();

            AiDebugLog.Write(army.Hex.Equals(before)
                ? $"[AI] {player.Nickname}: {army.Name} не смог дойти до цели (нет пути, очков хода, или бой в пути) — остался на ({army.Hex.Q}, {army.Hex.R})."
                : $"[AI] {player.Nickname}: {army.Name} прибыл на ({army.Hex.Q}, {army.Hex.R}).");

            yield return WaitStep(ctx);
        }

        private static IEnumerator PlayCardRoutine(PlayerSetupData player, AiDecision decision, AiTurnContext ctx)
        {
            PlayerRoot root = PlayerRootRegistry.FindFor(player);
            if (root == null)
                yield break;

            ArmyData targetArmy = decision.ExistingArmy;
            HexCoord hex = targetArmy?.Hex ?? GarrisonHexFor(player);
            yield return PanTo(ctx, hex);

            if (targetArmy == null)
            {
                targetArmy = ArmyActions.CreateArmy(player, hex, ctx.StartingDeckCatalog?.GetCatalog(player.Faction), ctx.HexSelection);
                if (targetArmy == null)
                {
                    AiDebugLog.Write($"[AI] {player.Nickname}: не хватило AP на новую армию под карту {decision.Card.Definition.displayName}.");
                    yield break;
                }
                AiDebugLog.Write($"[AI] {player.Nickname}: создаёт новую армию {targetArmy.Name} под карту {decision.Card.Definition.displayName}.");
            }

            if (ctx.ArmyViewerModal != null)
                ctx.ArmyViewerModal.ShowReadOnly(targetArmy);
            yield return WaitStep(ctx);

            bool deployed = ArmyActions.DeployUnitFromCard(decision.Card.Definition, player, targetArmy, root, ctx.HexSelection, out string failReason);
            if (deployed)
            {
                AiHandData hand = AiHandRegistry.GetOrCreate(player, ctx.StartingDeckCatalog, ctx.StartingHandSize);
                hand?.Hand.Remove(decision.Card);
                AiDebugLog.Write($"[AI] {player.Nickname}: {decision.Card.Definition.displayName} вступает в {targetArmy.Name} — {decision.Reason}.");
            }
            else
            {
                AiDebugLog.Write($"[AI] {player.Nickname}: не смог развернуть {decision.Card.Definition.displayName} — {failReason}");
            }

            if (ctx.ArmyViewerModal != null)
                ctx.ArmyViewerModal.Hide();
            yield return WaitStep(ctx);
        }

        // Экономика · Задача 1's own last stage — the hero has already arrived (see
        // AdvanceEconomyTask), so this just calls the same HexSelectionController.
        // TryBuildExtractionFacility a human clicking the resource-action button would, and
        // either closes out the task or counts a failed attempt toward AiEconomyPlanner.
        // MaxBuildAttempts (see AiTask's own comment on why that's a blunt safeguard rather than
        // a precise diagnosis).
        private static IEnumerator BuildFacilityRoutine(PlayerSetupData player, AiDecision decision, AiTurnContext ctx)
        {
            AiTask task = decision.Task;
            ArmyData army = task?.Army;
            if (army?.Controller == null || ctx.GameConfig?.extractionFacilityCards == null || !task.ResourceType.HasValue)
            {
                AiResourceReservation.Release(task);
                AiTaskRegistry.Remove(player, task);
                yield break;
            }

            yield return PanTo(ctx, army.Hex);
            if (ctx.ArmyViewerModal != null)
                ctx.ArmyViewerModal.ShowReadOnly(army);
            yield return WaitStep(ctx);

            CardDefinition definition = (int)task.ResourceType.Value < ctx.GameConfig.extractionFacilityCards.Length
                ? ctx.GameConfig.extractionFacilityCards[(int)task.ResourceType.Value]
                : null;
            bool built = definition != null && ctx.HexSelection.TryBuildExtractionFacility(definition, task.TargetHex, player);
            if (built)
            {
                AiDebugLog.Write($"[AI] {player.Nickname}: {army.Name} построил объект добычи {task.ResourceType} "
                    + $"на ({task.TargetHex.Q},{task.TargetHex.R}) — задача «Экономика» завершена.");
                AiResourceReservation.Release(task);
                AiTaskRegistry.Remove(player, task);
            }
            else
            {
                task.BuildAttempts++;
                AiDebugLog.Write($"[AI] {player.Nickname}: не смог построить {task.ResourceType} "
                    + $"на ({task.TargetHex.Q},{task.TargetHex.R}) — попытка {task.BuildAttempts}.");
                if (task.BuildAttempts >= AiEconomyPlanner.MaxBuildAttempts)
                {
                    AiDebugLog.Write($"[AI] {player.Nickname}: отказывается от задачи «Экономика» на "
                        + $"({task.TargetHex.Q},{task.TargetHex.R}) после {task.BuildAttempts} неудачных попыток.");
                    AiResourceReservation.Release(task);
                    AiTaskRegistry.Remove(player, task);
                }
            }

            if (ctx.ArmyViewerModal != null)
                ctx.ArmyViewerModal.Hide();
            yield return WaitStep(ctx);
        }

        private static IEnumerator ReserveArmyRoutine(PlayerSetupData player, AiTurnContext ctx)
        {
            HexCoord hex = GarrisonHexFor(player);
            yield return PanTo(ctx, hex);

            ArmyData army = ArmyActions.CreateArmy(player, hex, ctx.StartingDeckCatalog?.GetCatalog(player.Faction), ctx.HexSelection);
            AiDebugLog.Write(army != null
                ? $"[AI] {player.Nickname}: создаёт резервную армию {army.Name} про запас."
                : $"[AI] {player.Nickname}: не хватило AP на резервную армию.");
            // Flips which of Reserve/Draw is preferred next time, regardless of success — see
            // AiManagementPlanner.NotifyFallbackUsed's own comment.
            AiManagementPlanner.NotifyFallbackUsed(player, AiManagementPlanner.FallbackKind.ReserveArmy);

            yield return WaitStep(ctx);
        }

        private static IEnumerator DrawCardRoutine(PlayerSetupData player, AiTurnContext ctx)
        {
            PlayerRoot root = PlayerRootRegistry.FindFor(player);
            AiHandData hand = AiHandRegistry.GetOrCreate(player, ctx.StartingDeckCatalog, ctx.StartingHandSize);
            if (root != null && hand != null && root.CanSpendActionPoints(ctx.DrawApCost))
            {
                CardData card = hand.DrawOne();
                if (card != null)
                {
                    root.SpendActionPoints(ctx.DrawApCost);
                    AiDebugLog.Write($"[AI] {player.Nickname}: берёт карту — {card.Definition.displayName}.");
                }
            }
            AiManagementPlanner.NotifyFallbackUsed(player, AiManagementPlanner.FallbackKind.DrawCard);
            yield return WaitStep(ctx);
        }

        // Менеджмент · капасити гарнизона — see AiManagementPlanner.FindGarrisonOverflow and
        // TryGarrisonSplitCandidate. Creates the new army first (bails if that alone is
        // unaffordable, nothing else has happened yet) then moves every planned unit into it —
        // free of extra AP, since a freshly created army hasn't activated this turn (see
        // ArmyActions.TransferMember's own comment).
        private static IEnumerator SplitGarrisonArmyRoutine(PlayerSetupData player, AiDecision decision, AiTurnContext ctx)
        {
            ArmyData garrison = decision.ExistingArmy;
            yield return PanTo(ctx, decision.TargetHex);

            ArmyData newArmy = ArmyActions.CreateArmy(player, decision.TargetHex, ctx.StartingDeckCatalog?.GetCatalog(player.Faction), ctx.HexSelection);
            if (newArmy == null)
            {
                AiDebugLog.Write($"[AI] {player.Nickname}: не хватило AP на новую армию для перегруженного гарнизона.");
                yield break;
            }

            int moved = 0;
            foreach (UnitData unit in decision.UnitsToMove)
            {
                if (ArmyActions.TransferMember(unit, garrison, newArmy, ctx.HexSelection, out string failReason))
                    moved++;
                else
                    AiDebugLog.Write($"[AI] {player.Nickname}: не смог перевести {unit.Name} из гарнизона — {failReason}");
            }
            AiDebugLog.Write($"[AI] {player.Nickname}: гарнизон был полон — {moved} юнит(ов) выделены в новую армию {newArmy.Name}.");

            if (ctx.ArmyViewerModal != null)
                ctx.ArmyViewerModal.ShowReadOnly(newArmy);
            yield return WaitStep(ctx);
        }

        // Менеджмент · передача юнитов между армиями в базе — see
        // AiManagementPlanner.FindConsolidationMove and TryConsolidationCandidate.
        private static IEnumerator ConsolidateUnitsRoutine(PlayerSetupData player, AiDecision decision, AiTurnContext ctx)
        {
            AiManagementPlanner.ConsolidationMove move = decision.ConsolidationMove;
            yield return PanTo(ctx, move.Source.Hex);

            bool moved = ArmyActions.TransferMember(move.Unit, move.Source, move.Target, ctx.HexSelection, out string failReason);
            if (moved)
            {
                AiDebugLog.Write($"[AI] {player.Nickname}: {decision.Reason}.");
                ctx.HexSelection?.DeleteArmyIfEmptied(move.Source);
            }
            else
            {
                AiDebugLog.Write($"[AI] {player.Nickname}: не смог объединить {move.Unit.Name} — {failReason}");
            }

            if (ctx.ArmyViewerModal != null)
                ctx.ArmyViewerModal.ShowReadOnly(move.Target);
            yield return WaitStep(ctx);
        }

        // Экономика · Задача 2's own prep step — see AiEconomyPlanner.CollectorDetachPlan's own
        // comment for the two shapes decision.MergeTarget can take. Either way ExistingArmy
        // (Source) keeps CollectorUnit and becomes the solo army TryStartResourcesScrapCandidates
        // picks up next step; only the OTHER members ever move.
        private static IEnumerator DetachCollectorRoutine(PlayerSetupData player, AiDecision decision, AiTurnContext ctx)
        {
            ArmyData source = decision.ExistingArmy;
            UnitData collector = decision.CollectorUnit;
            yield return PanTo(ctx, source.Hex);

            if (decision.MergeTarget != null)
            {
                ArmyData target = decision.MergeTarget;
                List<UnitData> others = source.Members.Where(m => m != collector).ToList();
                int moved = 0;
                foreach (UnitData member in others)
                {
                    if (ArmyActions.TransferMember(member, source, target, ctx.HexSelection, out string failReason))
                        moved++;
                    else
                        AiDebugLog.Write($"[AI] {player.Nickname}: не смог перевести {member.Name} из {source.Name} "
                            + $"в {target.Name} — {failReason}");
                }
                AiDebugLog.Write($"[AI] {player.Nickname}: {decision.Reason} ({moved}/{others.Count} юнит(ов) переведено).");
            }
            else
            {
                ArmyData newArmy = ArmyActions.CreateArmy(player, source.Hex, ctx.StartingDeckCatalog?.GetCatalog(player.Faction), ctx.HexSelection);
                if (newArmy == null)
                {
                    AiDebugLog.Write($"[AI] {player.Nickname}: не хватило AP на новую армию для {collector.Name} — добыча отложена.");
                    yield break;
                }
                if (ArmyActions.TransferMember(collector, source, newArmy, ctx.HexSelection, out string failReason))
                    AiDebugLog.Write($"[AI] {player.Nickname}: {decision.Reason}.");
                else
                    AiDebugLog.Write($"[AI] {player.Nickname}: не смог выделить {collector.Name} — {failReason}");
            }

            if (ctx.ArmyViewerModal != null)
                ctx.ArmyViewerModal.Hide();
            yield return WaitStep(ctx);
        }

        private static HexCoord GarrisonHexFor(PlayerSetupData player)
        {
            ArmyData garrison = ArmyRegistry.AllForOwner(player).FirstOrDefault(a => a.IsGarrison);
            return garrison != null ? garrison.Hex : default;
        }

        private static ArmyData GarrisonArmyFor(PlayerSetupData player)
        {
            return ArmyRegistry.AllForOwner(player).FirstOrDefault(a => a.IsGarrison);
        }

        // Per the project owner's own call: an AI turn no longer pans the camera to what it's
        // doing at all (used to glide there every one of the 8 call sites below, ~1.2s each) —
        // watching an AI turn play out was costing more time than the AI's own decisions did.
        private static IEnumerator PanTo(AiTurnContext ctx, HexCoord hex)
        {
            yield break;
        }

        private static IEnumerator WaitStep(AiTurnContext ctx)
        {
            yield return new WaitForSeconds(UnityEngine.Random.Range(ctx.MinStepDelay, ctx.MaxStepDelay));
        }

        private static void LogHand(PlayerSetupData player, AiHandData hand)
        {
            if (hand == null)
                return;
            string cards = hand.Hand.Count > 0
                ? string.Join(", ", hand.Hand.Select(c => c.Definition != null ? c.Definition.displayName : "?"))
                : "пусто";
            AiDebugLog.Write($"[AI] {player.Nickname}: смотрит колоду — рука: {cards}.");
        }

        // Every persistent AiTask still standing at the start of this turn — the "уровень
        // подзадачи" state Decide's own per-step candidate dump doesn't otherwise show, since a
        // task only produces a fresh candidate once it's actually re-evaluated this step (see
        // Decide's own class comment). Silent when there's nothing active, same as LogHand's own
        // "пусто" case not needing a separate empty-state line.
        private static void LogActiveTasks(PlayerSetupData player)
        {
            IReadOnlyList<AiTask> tasks = AiTaskRegistry.TasksFor(player);
            if (tasks.Count == 0)
                return;
            string list = string.Join("; ", tasks.Select(t =>
                $"{t.Kind}:{t.Army?.Name ?? "?"}→({t.TargetHex.Q},{t.TargetHex.R})"));
            AiDebugLog.Write($"[AI] {player.Nickname}: активные задачи ({tasks.Count}) — {list}.");
        }
    }
}
