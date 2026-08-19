using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Cameras;
using Game.Cards;
using Game.Combat;
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
        // GameTurnController.TurnNumber, read once per RunTurn — for ReconMoveWeight's own
        // priority taper (see its own comment): the only consumer, so this stays a plain int
        // snapshot rather than a live reference to the whole controller.
        public int TurnNumber;
        // Cross-category oscillation guard — every army a unit has actually sat in THIS turn via
        // an AI-issued transfer (WouldRevisitArmy/RecordArmyVisit below). Started as
        // GarrisonReorgTask's own private guard (its FindReorgMove tiers can undo each other
        // within that one class), generalized here after the same shape of bug turned up ACROSS
        // categories: AiAggressionPlanner.AssembleRaidForceRoutine and AiScoutPlanner.
        // AssembleRecceScoutRoutine kept shuttling the same Recce unit back and forth between a
        // raid force and a solo scout composition, neither aware the other had just undone its
        // move, burning turn 8's entire step budget for nothing (see AiDebug.log 2026-08-17). Any
        // routine that transfers a unit between armies can call RecordArmyVisit after a move
        // lands and WouldRevisitArmy before proposing one, the same way GarrisonReorgTask always
        // has. A fresh, empty dictionary every turn (From below constructs a brand new
        // AiTurnContext per RunTurn call, never reused across turns) — a unit is free to revisit
        // an army it sat in last turn, only a same-turn round-trip gets blocked.
        public readonly Dictionary<UnitData, HashSet<ArmyData>> UnitVisitedArmies = new Dictionary<UnitData, HashSet<ArmyData>>();

        // See UnitVisitedArmies' own comment. Keyed by unit only (not unit+turn) since this
        // dictionary itself is already fresh every turn — nothing to distinguish by turn here.
        public bool WouldRevisitArmy(UnitData unit, ArmyData target)
        {
            return unit != null && target != null && UnitVisitedArmies.TryGetValue(unit, out HashSet<ArmyData> visited)
                && visited.Contains(target);
        }

        // Records BOTH ends of a landed move (not just the destination) so a same-turn round trip
        // is caught on whichever leg comes second, regardless of which direction happens to be
        // proposed first.
        public void RecordArmyVisit(UnitData unit, ArmyData source, ArmyData target)
        {
            if (unit == null)
                return;
            if (!UnitVisitedArmies.TryGetValue(unit, out HashSet<ArmyData> visited))
                UnitVisitedArmies[unit] = visited = new HashSet<ArmyData>();
            if (source != null)
                visited.Add(source);
            if (target != null)
                visited.Add(target);
        }

        public static AiTurnContext From(RtsCameraController camera, HexMap map, HexSelectionController hexSelection,
            ArmyViewerModalUI armyViewerModal, CardHandUI humanCardHand, float minStepDelay, float maxStepDelay,
            GameConfig gameConfig, int turnNumber)
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
                TurnNumber = turnNumber,
            };
        }
    }

    // Level 0 of the AI architecture (see AI_ARCHITECTURE.html section 01 and the project owner's
    // own 3-level split): map data + orchestrator with common methods + task ordering. This class
    // owns the turn loop (RunTurn) and the unified per-step arbiter (Decide/Commit — "common
    // methods, task ordering"), plus PerformDecision's own dispatch switch. It never itself
    // decides WHAT to do inside one category — that's Level 1.
    //
    // Execution (the ...Routine coroutines that actually touch ArmyActions/HexSelectionController/
    // UI) is split the same way: MoveArmyRoutine/PlayCardRoutine stay here because they're
    // genuinely category-agnostic (MoveArmy is produced by Разведка/Экономика/Агрессия alike,
    // PlayCard by Менеджмент AND Разведка's own Recce-assembly) — everything else executes exactly
    // one AiActionKind that only one category ever produces, so it lives on that category's own
    // Level-1 planner instead (see PerformDecision's own switch for the full map). PanTo/WaitStep
    // are `internal`, not `private`, so those Level-1 routines can share them.
    //
    // Level 1 — one class per AiTaskCategory (AiScoutPlanner/Reconnaissance, AiEconomyPlanner/
    // Economy, AiManagementPlanner/Management, AiAggressionPlanner/Aggression). Each exposes the
    // continue/start/return-home candidate-gathering methods Decide calls into below, plus
    // whatever primitives are genuinely shared across that category's own Level-2 tasks, plus its
    // own category-specific execution routines (see above).
    //
    // Level 2 — one class per concrete AiTaskKind (VisitHexTask, BuildFacilityTask,
    // ResourcesScrapTask, RaidWeakerArmyTask) — composition eligibility,
    // concrete target-finding/scoring, and any task-specific behavioral quirks live there; the
    // Level-1 planner above it only sequences calls into it.
    //
    // Borrowing/creating armies, drawing cards, and issuing moves all go through the exact same
    // player-agnostic methods a human's own clicks use (Game.Map.ArmyActions,
    // HexSelectionController.IssueMoveOrder) — nothing here mutates game state through a separate
    // path.
    public static class AiTurnController
    {
        // Every tunable number this class used to hold as private consts (turn-loop safety cap,
        // arbiter base weights, per-task radii/caps/bonuses) now lives on AiConfig, read via
        // AiConfig.Current at each use site — see that class for what each field means and why
        // (comments preserved there). One shared asset, tunable without recompiling, same reason
        // Game.Core.GameConfig already exists for gameplay constants generally.

        // Fixed "." decimal point regardless of the machine's own locale — interpolated "{x:0.0}"
        // otherwise follows CultureInfo.CurrentCulture, which prints "162,0" on a Russian-locale
        // machine and reads as ambiguous next to this log's own " | "-separated candidate lists.
        private static string Fmt(float score) => score.ToString("0.0", CultureInfo.InvariantCulture);

        // Decide's own candidate-dump line, one entry per candidate — except PlayCard, which on a
        // typical step is 8-12 near-identical entries (one per card still sitting in hand,
        // barely changing step to step) drowning out the handful of candidates that actually
        // differ from the previous step (MoveArmy/BuildFacility/task work). Collapsed into a
        // single "PlayCard×N(min-max)" summary here; which specific card a PlayCard decision
        // actually was is still fully logged separately if it wins (see RunTurn's own "decided
        // ..." line and PlayCardRoutine's own log line), so nothing observable is lost.
        private static string DescribeCandidates(List<AiDecision> candidates)
        {
            var parts = new List<string>();
            List<AiDecision> playCards = null;
            foreach (AiDecision c in candidates)
            {
                if (c.Kind == AiActionKind.PlayCard)
                {
                    playCards ??= new List<AiDecision>();
                    playCards.Add(c);
                    continue;
                }
                parts.Add($"{c.Kind}({Fmt(c.Score)}) {c.Reason}");
            }
            if (playCards != null)
            {
                parts.Add(playCards.Count == 1
                    ? $"PlayCard({Fmt(playCards[0].Score)}) {playCards[0].Reason}"
                    : $"PlayCard×{playCards.Count}({Fmt(playCards.Min(c => c.Score))}-{Fmt(playCards.Max(c => c.Score))}) — карты в руке");
            }
            return string.Join(" | ", parts);
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
            int startArmies = ArmyRegistry.AllForOwner(player).Count(a => !a.IsGarrison && !a.IsPrison);
            int startHuman = root.GetResource(ResourceType.Human);
            int startEnergy = root.GetResource(ResourceType.Energy);
            int startMaterials = root.GetResource(ResourceType.Materials);
            int startTech = root.GetResource(ResourceType.Tech);
            AiDebugLog.Write($"[AI] === {player.Nickname}'s turn begins (turn {ctx.TurnNumber}) — AP={root.ActionPoints}, "
                + $"armies={startArmies}, human={startHuman}, energy={startEnergy}, materials={startMaterials}, tech={startTech} ===");
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
            // Per-Kind tally for the turn-end summary below — Pass excluded (it only ever ends the
            // turn, never itself represents work done).
            var actionCounts = new Dictionary<AiActionKind, int>();

            for (int step = 0; step < AiConfig.Current.maxStepsPerTurn; step++)
            {
                AiDecision decision = Decide(player, root, hand, ctx, stuckScouts, pool);
                AiDebugLog.Write($"[AI] {player.Nickname}: step {step + 1}/{AiConfig.Current.maxStepsPerTurn} — decided {decision.Kind} "
                    + $"(score {Fmt(decision.Score)}) — {decision.Reason}.");
                if (decision.Kind == AiActionKind.Pass)
                    break;
                actionCounts.TryGetValue(decision.Kind, out int count);
                actionCounts[decision.Kind] = count + 1;
                // Wait only ever wins Decide's arbitration when nothing else scored — same
                // "nothing left this turn" situation Pass handles, just with an actual reason
                // attached (see AiDecision.Wait's own comment) instead of the old silent drop.
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

            int endArmies = ArmyRegistry.AllForOwner(player).Count(a => !a.IsGarrison && !a.IsPrison);
            string actionsSummary = actionCounts.Count > 0
                ? string.Join(", ", actionCounts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}×{kv.Value}"))
                : "нет действий";
            AiDebugLog.Write($"[AI] === {player.Nickname}'s turn ends (turn {ctx.TurnNumber}) — AP left={root.ActionPoints}, "
                + $"armies={startArmies}→{endArmies}, human={startHuman}→{root.GetResource(ResourceType.Human)}, "
                + $"energy={startEnergy}→{root.GetResource(ResourceType.Energy)}, "
                + $"materials={startMaterials}→{root.GetResource(ResourceType.Materials)}, "
                + $"tech={startTech}→{root.GetResource(ResourceType.Tech)} — действия: {actionsSummary} ===");
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
        // more — only Score does; this is just a stable read order). Each call below reaches into
        // the owning Level-1 category planner (AiScoutPlanner/AiEconomyPlanner/AiManagementPlanner/
        // AiAggressionPlanner) rather than deciding anything itself:
        // 1) Continue in-flight AiTask work — one candidate per active task, across
        // BuildFacility/ResourcesScrap/VisitHex/RaidWeakerArmy/RaidReinforce. A task whose army is
        // in `stuckScouts` (already failed to move this turn) contributes no candidate, never
        // retried this step. RepairUnit's own continuation is gathered separately, down in the
        // Менеджмент block below (bullet 4) — it never moves the army at all (see
        // AiManagementPlanner.AdvanceRepairTask's own comment), so it's grouped with that
        // category's other candidates instead of this travel-stage loop.
        // 2) AiScoutPlanner.TryReturnHomeCandidates — AiArmyRoles.IsSoloHeroAwaitingEscort's own
        // fallback; this composition never gets a Разведка task of its own any more, so it just
        // walks home to wait for an escort. AiEconomyPlanner.TryEconomyReturnHomeCandidates is
        // Экономика's own analogue — a hero-led army with no active task, once
        // BuildFacilityTask.HasAnythingToBuild says there's nothing left anywhere to build.
        // 3) Start new AiTask work — Экономика (see AiEconomyPlanner.TryStartEconomyCandidates'
        // own comment on why it's allowed to preempt a Разведка task's army), Разведка Задача 1,
        // Агрессия Задача 1 (AiAggressionPlanner.TryRaidAssembleCandidates — see
        // RaidWeakerArmyTask's own class comment, which also covers its own "Оборона цитадели"
        // sub-tier: a real threat sighted next to the garrison, exempt from MaxConcurrentRaid, see
        // AiTask.DefendingCitadel) plus its own recall/regroup/preempt steps
        // (TryRaidRecallCandidates/TryRaidReturnHomeCandidates/TryRaidRegroupCandidates/
        // TryCitadelDefensePreemptCandidates — the regroup one routes a critically wounded field
        // army home or to a courier rendezvous, the preempt one pulls a field army off an ACTIVE
        // task to defend the citadel once idle reinforcement alone won't cover it, see each one's
        // own comment) — each still respects its own cap (MaxConcurrentVisitHex/MaxConcurrentRaid)
        // before generating any candidate at all.
        // 4) Менеджмент's own base upkeep — garrison-overflow split, lone-army consolidation (see
        // GarrisonReorgTask.FindGarrisonOverflow/FindReorgMove), Починка юнита (continuation +
        // TryStartRepairCandidates — owned here rather than Экономика because it needs the exact
        // same hand read TryPlayCardCandidates does, see AiManagementPlanner's own comment on that
        // section), then "выложить карту из руки" candidates, one per affordable Unit/Hero/Recce
        // card in hand (see AiArmyRoles's own class comment for the three shapes cards map to;
        // Base/Facility cards are skipped entirely — see AiManagementPlanner.TryPlayCardCandidates).
        // 5) Менеджмент's own leftover-AP fallbacks (AiManagementPlanner.GatherFallbackCandidates)
        // — a spare army, a fresh card draw.
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
                AiDecision decision = AiEconomyPlanner.AdvanceEconomyTask(player, root, ctx, task);
                if (decision != null)
                    candidates.Add(decision);
            }
            foreach (AiTask task in AiTaskRegistry.TasksFor(player).Where(t => t.Kind == AiTaskKind.VisitHex).ToList())
            {
                if (stuckScouts.Contains(task.Army))
                    continue;
                AiDecision decision = AiScoutPlanner.TryContinueVisitTask(player, root, ctx, task);
                if (decision != null)
                    candidates.Add(decision);
            }
            foreach (AiTask task in AiTaskRegistry.TasksFor(player).Where(t => t.Kind == AiTaskKind.ResourcesScrap).ToList())
            {
                if (stuckScouts.Contains(task.Army))
                    continue;
                AiDecision decision = AiEconomyPlanner.AdvanceResourcesScrapTask(player, root, task);
                if (decision != null)
                    candidates.Add(decision);
            }
            foreach (AiTask task in AiTaskRegistry.TasksFor(player).Where(t => t.Kind == AiTaskKind.RaidWeakerArmy).ToList())
            {
                if (stuckScouts.Contains(task.Army))
                    continue;
                AiDecision decision = AiAggressionPlanner.TryContinueRaidTask(player, root, ctx, task);
                if (decision != null)
                    candidates.Add(decision);
            }
            foreach (AiTask task in AiTaskRegistry.TasksFor(player).Where(t => t.Kind == AiTaskKind.RaidReinforce).ToList())
            {
                AiDecision decision = AiAggressionPlanner.AdvanceReinforceTask(player, root, ctx, task);
                if (decision != null)
                    candidates.Add(decision);
            }

            candidates.AddRange(AiScoutPlanner.TryReturnHomeCandidates(player, root, ctx, stuckScouts));
            candidates.AddRange(AiEconomyPlanner.TryEconomyReturnHomeCandidates(player, root, stuckScouts));
            candidates.AddRange(AiEconomyPlanner.TryStartEconomyCandidates(player, root, ctx, stuckScouts));
            candidates.AddRange(AiEconomyPlanner.TryStartResourcesScrapCandidates(player, pool));
            candidates.AddRange(AiEconomyPlanner.TryStartCollectorDetachCandidates(player, root, pool));
            candidates.AddRange(AiScoutPlanner.TryStartVisitCandidates(player, root, ctx, pool, stuckScouts));
            candidates.AddRange(AiAggressionPlanner.TryRaidAssembleCandidates(player, root, ctx, pool));
            candidates.AddRange(AiAggressionPlanner.TryRaidRecallCandidates(player, root, ctx, pool, stuckScouts));
            candidates.AddRange(AiAggressionPlanner.TryCitadelDefensePreemptCandidates(player, root, ctx, pool));
            candidates.AddRange(AiAggressionPlanner.TryRaidReturnHomeCandidates(player, root, pool, stuckScouts));
            candidates.AddRange(AiAggressionPlanner.TryRaidRegroupCandidates(player, root, ctx, pool, stuckScouts));
            candidates.AddRange(AiScoutPlanner.TryStartReconAssemblyCandidates(player, root, ctx, hand, pool));
            // Read off the candidates just gathered above, not recomputed — Разведка already
            // being mid-pursuit of a matching Recce card THIS step is exactly what
            // TryPlayCardCandidates's own reconHandDemandActive damping wants to know (see its own
            // comment for why this only quiets the artificial Unit/Hero backlog term, not Recce's
            // own flat bonus).
            bool reconHandDemandActive = candidates.Any(c =>
                c.Kind == AiActionKind.SpawnReconArmy || c.Kind == AiActionKind.AssembleRecceScout);

            ArmyData garrison = GarrisonArmyFor(player);
            AiDecision splitCandidate = AiManagementPlanner.TryGarrisonSplitCandidate(player, garrison);
            if (splitCandidate != null)
                candidates.Add(splitCandidate);
            AiDecision consolidateCandidate = AiManagementPlanner.TryConsolidationCandidate(player, garrison, ctx);
            if (consolidateCandidate != null)
                candidates.Add(consolidateCandidate);

            foreach (AiTask task in AiTaskRegistry.TasksFor(player).Where(t => t.Kind == AiTaskKind.RepairUnit).ToList())
            {
                AiDecision decision = AiManagementPlanner.AdvanceRepairTask(player, root, hand, task);
                if (decision != null)
                    candidates.Add(decision);
            }
            candidates.AddRange(AiManagementPlanner.TryStartRepairCandidates(player, root, hand));
            candidates.AddRange(AiManagementPlanner.TryPlayCardCandidates(player, root, hand, reconHandDemandActive));
            candidates.AddRange(AiManagementPlanner.GatherFallbackCandidates(player, root, hand, ctx));

            AiDebugLog.Write($"[AI] {player.Nickname}: {candidates.Count} кандидат(ов) — {DescribeCandidates(candidates)}");

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
        // all. Keeps MaxConcurrentVisitHex/MaxConcurrentRaid honest — generating N scored
        // candidates this step must never register more than the ONE that actually wins.
        private static void Commit(PlayerSetupData player, AiDecision decision, AiResourcePool pool)
        {
            if (decision.PreemptedTask != null)
            {
                // decision.TargetHex is only this step's own next move-step (see
                // AiEconomyPlanner.AdvanceEconomyTask's own FindNextVisitedStep) — decision.Task.TargetHex
                // is the actual build destination the preemption is FOR, which can be several hexes
                // further out; logging the former here used to misreport where the build was headed.
                AiDebugLog.Write($"[AI] {player.Nickname}: {decision.PreemptedTask.Army.Name} снят с задачи "
                    + $"«{decision.PreemptedTask.Kind}» ради постройки на ({decision.Task.TargetHex.Q},{decision.Task.TargetHex.R}) "
                    + "— старая задача помечена невыполненной.");
                // A preempted BuildFacility task may already hold a reservation (see
                // AiEconomyPlanner.TryStartEconomyCandidates' own scarcity-switch comment) — just
                // an accounting claim, never an actual spend, but it still needs releasing back to
                // the shared pool or those resources would stay locked behind a task that no
                // longer exists.
                if (decision.PreemptedTask.Kind == AiTaskKind.BuildFacility)
                    AiResourceReservation.Release(decision.PreemptedTask);
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
                    yield return AiManagementPlanner.ReserveArmyRoutine(player, ctx);
                    break;
                case AiActionKind.DrawCard:
                    yield return AiManagementPlanner.DrawCardRoutine(player, ctx);
                    break;
                case AiActionKind.BuildFacility:
                    yield return AiEconomyPlanner.BuildFacilityRoutine(player, decision, ctx);
                    break;
                case AiActionKind.RepairUnit:
                    yield return AiManagementPlanner.RepairUnitRoutine(player, decision, ctx);
                    break;
                case AiActionKind.SplitGarrisonArmy:
                    yield return AiManagementPlanner.SplitGarrisonArmyRoutine(player, decision, ctx);
                    break;
                case AiActionKind.ConsolidateUnits:
                    yield return AiManagementPlanner.ConsolidateUnitsRoutine(player, decision, ctx);
                    break;
                case AiActionKind.DetachCollector:
                    yield return AiEconomyPlanner.DetachCollectorRoutine(player, decision, ctx);
                    break;
                case AiActionKind.SpawnReconArmy:
                    yield return AiScoutPlanner.SpawnReconArmyRoutine(player, ctx);
                    break;
                case AiActionKind.AssembleRecceScout:
                    yield return AiScoutPlanner.AssembleRecceScoutRoutine(player, decision, ctx);
                    break;
                case AiActionKind.RequestRaidArmy:
                    yield return AiAggressionPlanner.RequestRaidArmyRoutine(player, ctx);
                    break;
                case AiActionKind.AssembleRaidForce:
                    yield return AiAggressionPlanner.AssembleRaidForceRoutine(player, decision, ctx);
                    break;
                case AiActionKind.DispatchReinforcement:
                    yield return AiAggressionPlanner.DispatchReinforcementRoutine(player, decision, ctx);
                    break;
                case AiActionKind.ReinforceSwap:
                    yield return AiAggressionPlanner.ReinforceSwapRoutine(player, decision, ctx);
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

            // A contact-triggered fight has already been kicked off synchronously by this point
            // (ArmyController.MoveRoutine runs its own onComplete callback — which is where
            // contact detection and battleScreen.Show() live — before IsMoving above ever flips
            // false), but the fight itself, and anything it chains into, resolves asynchronously
            // from here. Wait for all of that to fully close before this army's own turn step
            // counts as done — otherwise RunTurn's per-step loop moves straight on to the NEXT
            // army's decision while a battle (or a chained one, or a Capture Kill Challenge) is
            // still playing out (see HexSelectionController.IsBattleActive's own comment).
            if (ctx.HexSelection != null)
                yield return new WaitUntil(() => !ctx.HexSelection.IsBattleActive);

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

            // decision.EvictUnit — Hero role only (see AiManagementPlanner.FindPlacement's own
            // last-resort tier): targetArmy was FULL when this candidate was built, so its
            // weakest member has to actually leave for the garrison first, or DeployUnitFromCard
            // below (which never itself checks HasRoom — see ArmyActions' own comment) would
            // silently overfill targetArmy past its own Capacity. Bails out of deploying
            // entirely if the eviction itself fails — nothing about root's AP was spent yet at
            // that point, so the card just stays in hand for Decide's next re-evaluation, same
            // "checked before proposing, but state can still shift between propose and execute"
            // case every other AP-gated candidate here already tolerates.
            bool canDeploy = true;
            if (decision.EvictUnit != null)
            {
                ArmyData garrison = GarrisonArmyFor(player);
                string evictFailReason = "гарнизон недоступен";
                bool evicted = garrison != null
                    && ArmyActions.TransferMember(decision.EvictUnit, targetArmy, garrison, ctx.HexSelection, out evictFailReason);
                if (evicted)
                {
                    AiDebugLog.Write($"[AI] {player.Nickname}: освобождает место в {targetArmy.Name} — {decision.EvictUnit.Name} уходит в гарнизон.");
                }
                else
                {
                    canDeploy = false;
                    AiDebugLog.Write($"[AI] {player.Nickname}: не смог освободить место в {targetArmy.Name} для героя "
                        + $"{decision.Card.Definition.displayName} — {evictFailReason}.");
                }
            }

            if (ctx.ArmyViewerModal != null)
                ctx.ArmyViewerModal.ShowReadOnly(targetArmy);
            yield return WaitStep(ctx);

            int ap0 = root.ActionPoints;
            int human0 = root.GetResource(ResourceType.Human);
            int energy0 = root.GetResource(ResourceType.Energy);
            int materials0 = root.GetResource(ResourceType.Materials);
            int tech0 = root.GetResource(ResourceType.Tech);
            string failReason = null;
            bool deployed = canDeploy && ArmyActions.DeployUnitFromCard(decision.Card.Definition, player, targetArmy, root, ctx.HexSelection, out failReason);
            if (deployed)
            {
                AiHandData hand = AiHandRegistry.GetOrCreate(player, ctx.StartingDeckCatalog, ctx.StartingHandSize);
                hand?.Hand.Remove(decision.Card);
                // See AiManagementPlanner.NotifyCardRolePlayed's own comment — only flips the
                // Hero/Unit alternation state once the card has actually deployed, not on a
                // merely-proposed candidate.
                AiManagementPlanner.NotifyCardRolePlayed(player, AiManagementPlanner.RoleOf(decision.Card));
                string delta = ResourceDeltaSuffix(root, ap0, human0, energy0, materials0, tech0);
                AiDebugLog.Write($"[AI] {player.Nickname}: {decision.Card.Definition.displayName} вступает в {targetArmy.Name} — {decision.Reason}.{delta}");
            }
            else
            {
                AiDebugLog.Write($"[AI] {player.Nickname}: не смог развернуть {decision.Card.Definition.displayName} — {failReason}");
            }

            if (ctx.ArmyViewerModal != null)
                ctx.ArmyViewerModal.Hide();
            yield return WaitStep(ctx);
        }

        // Internal, not private — every Level-1 category planner needs the garrison hex/army as
        // a common Level-0 lookup (VisitHexTask's own TryFlee, AiAggressionPlanner's own
        // assemble/recall/return-home tiers, etc.).
        internal static HexCoord GarrisonHexFor(PlayerSetupData player)
        {
            ArmyData garrison = ArmyRegistry.AllForOwner(player).FirstOrDefault(a => a.IsGarrison);
            return garrison != null ? garrison.Hex : default;
        }

        internal static ArmyData GarrisonArmyFor(PlayerSetupData player)
        {
            return ArmyRegistry.AllForOwner(player).FirstOrDefault(a => a.IsGarrison);
        }

        // Trailer for an action's own log line — "what did this actually cost", read as a
        // before/after snapshot around the spend rather than off the CardDefinition/ResourceCost
        // itself, so it reports what really left the stockpile even if the spend path silently
        // charged less than nominal (e.g. a failed build). AP is included alongside the four
        // stockpiled resources since every one of these actions spends AP too, and that's the
        // more common blocker turn to turn (see AiEconomyPlanner's own AP-vs-resources Wait
        // reasons this trailer sits next to in the log). Null when nothing changed (a failed
        // spend, or an action with no cost at all) — callers skip the suffix entirely rather than
        // printing "ресурсы: —". Internal, not private — shared with
        // AiEconomyPlanner.BuildFacilityRoutine, the other AI-executed resource spend.
        internal static string ResourceDeltaSuffix(PlayerRoot root, int ap0, int human0, int energy0, int materials0, int tech0)
        {
            if (root == null)
                return null;
            var parts = new List<string>();
            void Add(string label, int before, int after)
            {
                if (before != after)
                    parts.Add($"{label} {before}→{after}");
            }
            Add("AP", ap0, root.ActionPoints);
            Add("human", human0, root.GetResource(ResourceType.Human));
            Add("energy", energy0, root.GetResource(ResourceType.Energy));
            Add("materials", materials0, root.GetResource(ResourceType.Materials));
            Add("tech", tech0, root.GetResource(ResourceType.Tech));
            return parts.Count > 0 ? $" — ресурсы: {string.Join(", ", parts)}" : null;
        }

        // Per the project owner's own call: an AI turn no longer pans the camera to what it's
        // doing at all (used to glide there every one of the 8 call sites below, ~1.2s each) —
        // watching an AI turn play out was costing more time than the AI's own decisions did.
        // Internal, not private — every Level-1 category planner's own execution routines share
        // this and WaitStep below (see this class's own class comment on the execution split).
        internal static IEnumerator PanTo(AiTurnContext ctx, HexCoord hex)
        {
            yield break;
        }

        internal static IEnumerator WaitStep(AiTurnContext ctx)
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
