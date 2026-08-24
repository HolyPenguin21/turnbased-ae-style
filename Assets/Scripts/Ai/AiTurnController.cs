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
using Game.Terrain;
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
        // Dev-only: gates every ArmyViewerModal.ShowReadOnly/Hide call below (see GameTurnController.
        // debugShowAiArmyModal's own comment) — off by default because the modal popping open/closed
        // on every single AI step (one MoveArmy per step, several steps per turn) reads as constant
        // flicker rather than something watchable; the AiDebugLog file/console line already logged
        // alongside every one of these same routines covers "what did the AI just do" without it.
        public bool ShowArmyModal;
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

        // Called once, by AiTurnController.RunGarrisonReorgPhase, right before its own drain loop
        // starts — 2026-08-21 fix, project owner's own report. UnitVisitedArmies' own history was
        // built up to stop the MAIN per-step Decide loop from undoing itself across several steps
        // in the SAME turn (see that field's own comment — the Recce shuttling bug this was
        // generalized from). RunGarrisonReorgPhase runs exactly once, as the very last thing a turn
        // does, with nothing left this turn that could ever read a leftover main-loop visit again —
        // so a unit the main loop moved earlier this turn (e.g. a fresh raid/defense recruit) has
        // no real same-turn round-trip risk left to protect against by staying blocked from an
        // end-of-turn garrison fold: nothing will try to pull it back OUT again until next turn's
        // own fresh evaluation regardless of what this phase does with it now. Cleared, not left
        // alone, specifically so this phase's OWN drain loop (which still runs several iterations,
        // see maxGarrisonReorgStepsPerTurn) keeps protecting itself from tier-vs-tier ping-pong
        // WITHIN this same call — a unit this phase itself folds into the garrison this iteration
        // still can't be immediately shoved back out to some field army by a later iteration the
        // same call, only the STALE main-loop history is discarded.
        public void ClearVisitedArmiesForReorgPhase() => UnitVisitedArmies.Clear();

        public static AiTurnContext From(RtsCameraController camera, HexMap map, HexSelectionController hexSelection,
            ArmyViewerModalUI armyViewerModal, CardHandUI humanCardHand, float minStepDelay, float maxStepDelay,
            GameConfig gameConfig, int turnNumber, bool showArmyModal)
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
                ShowArmyModal = showArmyModal,
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
    // Economy, AiManagementPlanner/Management, AiAggressionPlanner/Aggression, AiDefencePlanner/
    // Defence). Each exposes the
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
        // arbiter base weights, per-task radii/caps/bonuses) now lives on AiConfig, read directly
        // (AiConfig.xxx) at each use site — see that class for what each field means and why
        // (comments preserved there). Plain static consts (converted 2026-08-19 from a
        // ScriptableObject/Resources.Load asset, see AiConfig's own class comment).

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
        // "(score, Category)" suffix shared by every candidate entry below — Category is null
        // only for AiDecision.None (Pass), which never appears in this per-step candidate dump
        // (see Decide's own comment: None is returned directly, never added to `candidates`), so
        // the null case here is purely defensive.
        private static string CategoryTag(AiTaskCategory? category) => category.HasValue ? $", {category.Value}" : "";

        private static string DescribeCandidates(List<AiDecision> candidates)
        {
            // (Score, Text) rather than a plain string list so the collapsed PlayCard×N entry can
            // still be sorted alongside the rest by its own representative score (its max — the
            // one PlayCard actually competes with in Decide's own arbitration) instead of always
            // landing wherever iteration happened to reach it.
            var parts = new List<(float Score, string Text)>();
            List<AiDecision> playCards = null;
            foreach (AiDecision c in candidates)
            {
                if (c.Kind == AiActionKind.PlayCard)
                {
                    playCards ??= new List<AiDecision>();
                    playCards.Add(c);
                    continue;
                }
                parts.Add((c.Score, $"{c.Kind}({Fmt(c.Score)}{CategoryTag(c.Category)}) {c.Reason}"));
            }
            if (playCards != null)
            {
                float maxScore = playCards.Max(c => c.Score);
                parts.Add((maxScore, playCards.Count == 1
                    ? $"PlayCard({Fmt(playCards[0].Score)}{CategoryTag(playCards[0].Category)}) {playCards[0].Reason}"
                    : $"PlayCard×{playCards.Count}({Fmt(playCards.Min(c => c.Score))}-{Fmt(maxScore)}) — cards in hand"));
            }
            return string.Join(" | ", parts.OrderByDescending(p => p.Score).Select(p => p.Text));
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

            // Right before anything this turn can read AiMapMemory — expires this player's own
            // stale enemy-army sightings (see AiMapMemory.OnTurnStarted's own comment) so Decide's
            // whole loop below, not just some of it, sees the same freshly-expired memory.
            AiMapMemory.OnTurnStarted(player, ctx.TurnNumber);

            AiHandData hand = AiHandRegistry.GetOrCreate(player, ctx.StartingDeckCatalog, ctx.StartingHandSize);
            int startArmies = ArmyRegistry.AllForOwner(player).Count(a => !a.IsGarrison && !a.IsPrison);
            int startHuman = root.GetResource(ResourceType.Human);
            int startEnergy = root.GetResource(ResourceType.Energy);
            int startMaterials = root.GetResource(ResourceType.Materials);
            int startTech = root.GetResource(ResourceType.Tech);
            string apBonusSuffix = root.LastApFromApBonus > 0 && !string.IsNullOrEmpty(root.LastApBonusSources)
                ? $" [{root.LastApBonusSources}]"
                : string.Empty;
            AiDebugLog.Write($"[AI] === {player.Nickname}'s turn begins (turn {ctx.TurnNumber}) — AP={root.ActionPoints} "
                + $"(initiative={root.LastApFromInitiative}, prison=+{root.LastApFromPrisonBonus}, AP bonus=+{root.LastApFromApBonus}{apBonusSuffix}), "
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

            for (int step = 0; step < AiConfig.maxStepsPerTurn; step++)
            {
                AiDecision decision = Decide(player, root, hand, ctx, stuckScouts, pool);
                AiDebugLog.Write($"[AI] {player.Nickname}: step {step + 1}/{AiConfig.maxStepsPerTurn} — decided {decision.Kind} "
                    + $"(score {Fmt(decision.Score)}{CategoryTag(decision.Category)}) — {decision.Reason}.");
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

            yield return RunGarrisonReorgPhase(player, ctx, actionCounts);

            int endArmies = ArmyRegistry.AllForOwner(player).Count(a => !a.IsGarrison && !a.IsPrison);
            string actionsSummary = actionCounts.Count > 0
                ? string.Join(", ", actionCounts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}×{kv.Value}"))
                : "no actions";
            // hand/deck counts added 2026-08-24 (project owner's own ask) right alongside "AP
            // left=" — a turn that ends with AP still unspent is exactly the case where it also
            // matters whether that was because the hand/deck had nothing left to spend it on.
            AiDebugLog.Write($"[AI] === {player.Nickname}'s turn ends (turn {ctx.TurnNumber}) — AP left={root.ActionPoints}, "
                + $"hand={hand?.Hand.Count ?? 0}, deck left={hand?.RemainingDeckCount ?? 0}, "
                + $"armies={startArmies}→{endArmies}, human={startHuman}→{root.GetResource(ResourceType.Human)}, "
                + $"energy={startEnergy}→{root.GetResource(ResourceType.Energy)}, "
                + $"materials={startMaterials}→{root.GetResource(ResourceType.Materials)}, "
                + $"tech={startTech}→{root.GetResource(ResourceType.Tech)} — actions: {actionsSummary} ===");
            AiDebugLog.Write(BuildArmyBreakdownLog(player, ctx.TurnNumber));
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
        // AiAggressionPlanner/AiDefencePlanner) rather than deciding anything itself:
        // 1) Continue in-flight AiTask work — one candidate per active task, across
        // BuildFacility/ResourcesScrap/VisitHex/RaidWeakerArmy/RaidReinforce/DefendCitadel/
        // ReturnForConsolidation. A task whose army is in `stuckScouts` (already failed to move
        // this turn) contributes no candidate, never retried this step. RepairUnit's own
        // continuation is gathered separately, down in the Менеджмент block below (bullet 4) — it
        // never moves the army at all (see AiManagementPlanner.AdvanceRepairTask's own comment),
        // so it's grouped with that category's other candidates instead of this travel-stage loop.
        // ReturnForConsolidation (2026-08-24 P0 fix, see that AiTaskKind's own comment) is the odd
        // one out here — unlike every other Kind in this loop, it's never STARTED by a "start new"
        // tier further down; AiTurnController.RunStrandedArmyRecovery registers it directly at the
        // very end of a PREVIOUS turn (Feature 4B), so this loop only ever continues one already
        // sitting in the registry, exactly like every other task here does once past its own
        // start-up step.
        // 2) AiScoutPlanner.TryReturnHomeCandidates — AiArmyRoles.IsSoloHeroAwaitingEscort's own
        // fallback; this composition never gets a Разведка task of its own any more, so it just
        // walks home to wait for an escort. AiEconomyPlanner.TryEconomyReturnHomeCandidates is
        // Экономика's own analogue — a hero-led army with no active task, once
        // BuildFacilityTask.HasAnythingToBuild says there's nothing left anywhere to build.
        // 3) Start new AiTask work — Экономика (see AiEconomyPlanner.TryStartEconomyCandidates'
        // own comment on why it's allowed to preempt a Разведка task's army), Разведка Задача 1,
        // Агрессия Задача 1 (AiAggressionPlanner.TryRaidAssembleCandidates — see
        // RaidWeakerArmyTask's own class comment) plus its own recall/regroup/return-home steps
        // (TryRaidRecallCandidates/TryRaidReturnHomeCandidates/TryRaidRegroupCandidates — the
        // regroup one routes a critically wounded field army home or to a courier rendezvous, see
        // its own comment; TryRaidAssembleCandidates also suppresses starting any NEW raid, and
        // TryContinueRaidTask force-recalls every ACTIVE one, whenever AiDefencePlanner.
        // IsUnderSiege says the citadel is under real threat), and Оборона (AiDefencePlanner.
        // TryStartDefenceCandidates — full redesign 2026-08-21, ONE persistent DefendCitadel task
        // cycling through Patrol/Active/Turtle postures every re-evaluation, starting even with no
        // threat in sight at all now) plus its own preempt step (AiDefencePlanner.
        // TryDefencePreemptCandidates — pulls a field army off an ACTIVE task to reinforce the
        // citadel, now gated on IsUnderSiege) — each still respects its own cap
        // (MaxConcurrentVisitHex/MaxConcurrentRaid/maxConcurrentDefend) before generating any
        // candidate at all.
        // 4) Менеджмент's own base upkeep — Починка юнита (continuation + TryStartRepairCandidates
        // — owned here rather than Экономика because it needs the exact same hand read
        // TryPlayCardCandidates does, see AiManagementPlanner's own comment on that section), then
        // "выложить карту из руки" candidates, one per affordable Unit/Hero/Recce card in hand (see
        // AiArmyRoles's own class comment for the three shapes cards map to; Base/Facility cards are
        // skipped entirely — see AiManagementPlanner.TryPlayCardCandidates). Garrison-overflow split
        // and lone-army consolidation (GarrisonReorgTask.FindGarrisonOverflow/FindReorgMove) are
        // deliberately NOT gathered here any more (2026-08-20, project owner's own call) — see
        // RunGarrisonReorgPhase, called once from RunTurn after this whole per-step loop is done.
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
                AiDecision decision = AiEconomyPlanner.AdvanceResourcesScrapTask(player, root, ctx, task);
                if (decision != null)
                    candidates.Add(decision);
            }
            foreach (AiTask task in AiTaskRegistry.TasksFor(player).Where(t => t.Kind == AiTaskKind.RaidWeakerArmy).ToList())
            {
                if (stuckScouts.Contains(task.Army))
                    continue;
                AiDecision decision = AiAggressionPlanner.TryContinueRaidTask(player, root, ctx, task, pool);
                if (decision != null)
                    candidates.Add(decision);
            }
            foreach (AiTask task in AiTaskRegistry.TasksFor(player).Where(t => t.Kind == AiTaskKind.RaidReinforce).ToList())
            {
                AiDecision decision = AiAggressionPlanner.AdvanceReinforceTask(player, root, ctx, task);
                if (decision != null)
                    candidates.Add(decision);
            }
            foreach (AiTask task in AiTaskRegistry.TasksFor(player).Where(t => t.Kind == AiTaskKind.BuildBase).ToList())
            {
                if (stuckScouts.Contains(task.Army))
                    continue;
                AiDecision decision = AiAggressionPlanner.TryContinueBuildBaseTask(player, root, ctx, hand, task);
                if (decision != null)
                    candidates.Add(decision);
            }
            foreach (AiTask task in AiTaskRegistry.TasksFor(player).Where(t => t.Kind == AiTaskKind.DefendCitadel).ToList())
            {
                if (stuckScouts.Contains(task.Army))
                    continue;
                AiDecision decision = AiDefencePlanner.TryContinueDefenceTask(player, root, ctx, task);
                if (decision != null)
                    candidates.Add(decision);
            }
            foreach (AiTask task in AiTaskRegistry.TasksFor(player).Where(t => t.Kind == AiTaskKind.ReturnForConsolidation).ToList())
            {
                if (stuckScouts.Contains(task.Army))
                    continue;
                AiDecision decision = AiManagementPlanner.AdvanceReturnForConsolidationTask(player, root, ctx, task);
                if (decision != null)
                    candidates.Add(decision);
            }

            candidates.AddRange(AiScoutPlanner.TryReturnHomeCandidates(player, root, ctx, stuckScouts));
            candidates.AddRange(AiEconomyPlanner.TryEconomyReturnHomeCandidates(player, root, ctx, stuckScouts));
            candidates.AddRange(AiEconomyPlanner.TryStartEconomyCandidates(player, root, ctx, stuckScouts, pool));
            candidates.AddRange(AiEconomyPlanner.TryStartResourcesScrapCandidates(player, root, ctx, pool));
            candidates.AddRange(AiEconomyPlanner.TryStartCollectorDetachCandidates(player, root, pool));
            candidates.AddRange(AiScoutPlanner.TryStartVisitCandidates(player, root, ctx, pool, stuckScouts));
            candidates.AddRange(AiAggressionPlanner.TryRaidAssembleCandidates(player, root, ctx, hand, pool));
            candidates.AddRange(AiAggressionPlanner.TryRaidRecallCandidates(player, root, ctx, pool, stuckScouts));
            candidates.AddRange(AiAggressionPlanner.TryRaidReturnHomeCandidates(player, root, ctx, pool, stuckScouts));
            candidates.AddRange(AiAggressionPlanner.TryRaidRegroupCandidates(player, root, ctx, pool, stuckScouts));
            candidates.AddRange(AiAggressionPlanner.TryStartBuildBaseCandidates(player, root, ctx, hand, pool));
            candidates.AddRange(AiDefencePlanner.TryStartDefenceCandidates(player, root, ctx, pool));
            candidates.AddRange(AiDefencePlanner.TryDefencePreemptCandidates(player, root, ctx));
            candidates.AddRange(AiScoutPlanner.TryStartReconAssemblyCandidates(player, root, ctx, hand, pool));

            foreach (AiTask task in AiTaskRegistry.TasksFor(player).Where(t => t.Kind == AiTaskKind.RepairUnit).ToList())
            {
                AiDecision decision = AiManagementPlanner.AdvanceRepairTask(player, root, hand, task);
                if (decision != null)
                    candidates.Add(decision);
            }
            candidates.AddRange(AiManagementPlanner.TryStartRepairCandidates(player, root, hand));
            candidates.AddRange(AiManagementPlanner.TryPlayCardCandidates(player, root, hand, ctx));
            candidates.AddRange(AiManagementPlanner.GatherFallbackCandidates(player, root, hand, ctx));

            AiDebugLog.Write($"[AI] {player.Nickname}: {candidates.Count} candidate(s) — {DescribeCandidates(candidates)}");

            AiDecision best = null;
            foreach (AiDecision candidate in candidates)
                if (best == null || candidate.Score > best.Score)
                    best = candidate;

            if (best == null)
            {
                bool anyCardInHand = hand != null && hand.Hand.Count > 0;
                return AiDecision.None(anyCardInHand
                    ? "not enough AP for anything available"
                    : "nothing to do — armies busy, hand/AP has nothing to offer");
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
            // A "preemption" that's actually just this SAME task continuing — same army, same
            // Kind, same TargetHex — is never a real preemption, whatever candidate-generation
            // path produced it (2026-08-23 fix, project owner's own report/spec: a self-recommit
            // like this must never unfinished-mark or remove→add a task that was never actually
            // interrupted, since that resets BuildAttempts/StartedWithNoIncome/reservation and any
            // other future per-task counter for zero real change). decision.Task is always a
            // freshly-built object at this point (every planner re-derives its own candidate task
            // fresh each step — see e.g. AiEconomyPlanner.TryStartEconomyCandidates), never the
            // literal same instance as PreemptedTask, so this compares by identity fields instead
            // of by reference. The already-registered PreemptedTask instance is simply left alone;
            // decision.Task is discarded, same as any other non-winning candidate's leftovers.
            if (decision.PreemptedTask != null && decision.Task != null
                && decision.PreemptedTask.Army == decision.Task.Army && decision.PreemptedTask.Kind == decision.Task.Kind
                && decision.PreemptedTask.TargetHex.Equals(decision.Task.TargetHex))
            {
                return;
            }

            if (decision.PreemptedTask != null)
            {
                // decision.TargetHex is only this step's own next move-step (see
                // AiEconomyPlanner.AdvanceEconomyTask's own FindNextVisitedStep) — decision.Task.TargetHex
                // is the actual build destination the preemption is FOR, which can be several hexes
                // further out; logging the former here used to misreport where the build was headed.
                // decision.Task itself is null for AiDefencePlanner.TryDefencePreemptCandidates' own
                // siege-recall preemption (2026-08-21 fix, live NPE report — that path pulls an army
                // off another task to rush home, never registers a task of its own) — decision.Reason
                // already fully describes that case ("citadel under siege — recalled to defend"), no
                // separate destination hex to report.
                string forWhat = decision.Task != null
                    ? $"a build at ({decision.Task.TargetHex.Q},{decision.Task.TargetHex.R})"
                    : decision.Reason;
                AiDebugLog.Write($"[AI] {player.Nickname}: \"{decision.PreemptedTask.Army.Name}\" pulled off its "
                    + $"\"{decision.PreemptedTask.Kind}\" task for {forWhat} "
                    + "— the old task is marked unfinished.");
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
                    AiDebugLog.Write($"[AI] {player.Nickname}: starts Economy task — \"{decision.ExistingArmy.Name}\" heads out "
                        + $"to build {decision.Task.ResourceType} at ({decision.Task.TargetHex.Q},{decision.Task.TargetHex.R})."
                        + HandDemandLogSuffix(player, decision.Task.ResourceType, pool.Root, pool.Hand));
            }
        }

        // Менеджмент · сортировка войск (GarrisonReorgTask) — deliberately NOT one of Decide's own
        // arbitrated candidates (2026-08-20, project owner's own call: "задача бесплатная, поэтому
        // ей не с кем конкурировать" — every move GarrisonReorgTask proposes is already gated on
        // real affordability via CanAffordTransferInto, so it never needed to win a score fight to
        // justify running, and a flat managementGarrisonBalanceScore — since removed entirely,
        // there's no Score here any more at all — sat near the bottom of the shared scale, so it
        // could, and per the project owner's own log example did, lose to literally everything else
        // most turns and never run at all). Called once from RunTurn, after the main per-step loop
        // above is fully done (Pass, Wait, or maxStepsPerTurn reached) — the actual last thing a
        // turn does — and drains every reorg move currently available rather than the single move
        // one arbitrated step would have picked, since nothing here competes for a step budget any
        // more. Split (garrison overflow) is tried before consolidate each iteration — overflow
        // eviction is the more urgent of the two (garrison is literally over capacity), consolidate
        // is pure housekeeping.
        private static IEnumerator RunGarrisonReorgPhase(PlayerSetupData player, AiTurnContext ctx, Dictionary<AiActionKind, int> actionCounts)
        {
            // See AiTurnContext.ClearVisitedArmiesForReorgPhase's own comment — this phase runs
            // once, last, so a unit's main-loop move history from earlier THIS turn has nothing
            // left to protect against here; only history from this phase's own drain iterations
            // (recorded fresh below as they land) still matters.
            ctx.ClearVisitedArmiesForReorgPhase();
            // One drain loop per garrison now (citadel first, then any later-founded base — see
            // OwnGarrisonArmies' own comment), 2026-08-21 — the single `GarrisonArmyFor(player)`
            // this used to run against would just silently skip a second base's own garrison
            // (overflow eviction, consolidation) forever once one existed. Each garrison gets its
            // own full maxGarrisonReorgStepsPerTurn budget rather than sharing one, so a busy
            // citadel can never starve a base's own reorg out entirely.
            foreach (ArmyData garrison in OwnGarrisonArmies(player).ToList())
            {
                for (int i = 0; i < AiConfig.maxGarrisonReorgStepsPerTurn; i++)
                {
                    // CollapseTemporaryAssembly → HandleOverflow (unconditional) → IdleBalance — see
                    // GarrisonReorgTask's own class comment for the full three-regime writeup. A
                    // fresh recompute of all three every iteration, not a cached batch — whichever
                    // one fires changes what the very next iteration itself sees.
                    AiDecision decision = AiManagementPlanner.TryCollapseCandidate(player, garrison, ctx)
                        ?? AiManagementPlanner.TryGarrisonSplitCandidate(player, garrison)
                        ?? AiManagementPlanner.TryConsolidationCandidate(player, garrison, ctx);
                    if (decision == null)
                        break;

                    AiDebugLog.Write($"[AI] {player.Nickname}: end-of-turn reorg — {decision.Kind} ({decision.Category}) — {decision.Reason}.");
                    actionCounts.TryGetValue(decision.Kind, out int count);
                    actionCounts[decision.Kind] = count + 1;

                    // No-progress circuit breaker (2026-08-23, project owner's own report — a real
                    // log showed a candidate-feasibility mismatch between a Try*Candidate method and
                    // the transfer routine it feeds into re-proposing the exact same impossible
                    // SplitGarrisonArmy every single drain iteration, burning the entire
                    // maxGarrisonReorgStepsPerTurn budget on one hex for nothing every turn). This is
                    // a safety net, not a substitute for fixing the specific mismatch when one is
                    // found (see FindGarrisonOverflow's own 2026-08-23 fix for the mismatch that
                    // actually triggered this): whichever of Collapse/Split/Consolidate/Swap just ran
                    // is re-checked against the hex's OWN roster (every army at `garrison.Hex`, not
                    // just `garrison` itself — a Consolidate move can land entirely between two field
                    // armies and never touch the garrison's own Members at all) before and after
                    // executing. Identical rosters both times means the decision's own execution
                    // routine silently failed to move anything, and every OTHER tier would just
                    // recompute the identical decision next iteration too (nothing about game state
                    // changed) — so this stops draining THIS garrison for the rest of the phase
                    // rather than spinning through the remaining budget on repeats. Scoped to one
                    // garrison's own loop, not the whole phase — a later-founded base's own drain
                    // still gets its full budget even when the citadel's hits this.
                    string rosterBefore = HexRosterSignature(player, garrison.Hex);
                    yield return PerformDecision(player, decision, ctx);
                    if (HexRosterSignature(player, garrison.Hex) == rosterBefore)
                    {
                        AiDebugLog.Write($"[AI] {player.Nickname}: end-of-turn reorg — {decision.Kind} made no "
                            + $"progress on \"{garrison.Name}\"'s hex — stopping reorg there for the rest of this turn.");
                        break;
                    }
                }
            }

            // Feature 4 (2026-08-24, project owner's own report — turn-30 log showed many field
            // armies at roster size 0 or 1, inflating the army count without adding real strength)
            // — two new stages, run AFTER the existing Collapse→Overflow→IdleBalance per-garrison
            // drain above (unchanged, still runs first — see GarrisonReorgTask's own class comment
            // for that three-way priority). Stage A (empty shells) before Stage B (stranded
            // singles) — same "capacity/cleanup concerns before composition ones" ordering the
            // existing three-way priority already follows. Stage B only DETECTS/REGISTERS a
            // persistent task here as of the 2026-08-24 P0 fix — see RunStrandedArmyRecovery's own
            // comment; the actual walk-home happens on ordinary later turns through
            // AiManagementPlanner.AdvanceReturnForConsolidationTask instead.
            RunEmptyArmyCleanup(player, ctx);
            RunStrandedArmyRecovery(player);
        }

        // Feature 4A (2026-08-24) — sweeps every GarrisonReorgTask.IsDisposableEmptyArmy shell still
        // standing once the ordinary per-garrison drain above is done. Reuse (RequestRaidArmy/
        // RequestDefendArmy/SpawnReconArmy/DispatchReinforcement all now check GarrisonReorgTask.
        // FindDisposableEmptyArmyAt BEFORE spending AP on a fresh ArmyActions.CreateArmy — see each
        // of those routines' own comment) already happened, if it was going to, earlier THIS SAME
        // TURN's main Decide() loop — nothing left here reuses anything, it only disposes of
        // whatever's still surplus. Synchronous (no yield) — nothing here issues a move/spends AP,
        // just registry bookkeeping, same as HexRosterSignature's own read-only helper below.
        private static void RunEmptyArmyCleanup(PlayerSetupData player, AiTurnContext ctx)
        {
            HashSet<HexCoord> ownGarrisonHexes = new HashSet<HexCoord>(OwnGarrisonHexes(player));
            foreach (ArmyData army in ArmyRegistry.AllForOwner(player).ToList())
            {
                // P3 fix (2026-08-24, project owner's own log audit): an army whose roster was wiped
                // out in combat (rather than drained down to 0 by ConsolidateUnitsRoutine/etc.) can
                // still carry a stale AiTask referencing it — that task can never do anything useful
                // with zero members, but IsDisposableEmptyArmy treats "has a task" as "still in use"
                // and leaves it alone forever (see that method's own comment), so the shell survives
                // every ordinary cleanup pass as a permanent orphan. Invalidate the stale task FIRST,
                // then let the normal reserve/surplus/field policy below handle the now-task-less
                // shell exactly like any other empty army.
                if (army.Members.Count == 0)
                {
                    AiTask staleTask = AiTaskRegistry.TaskFor(player, army);
                    if (staleTask != null)
                    {
                        AiTaskRegistry.Remove(player, staleTask);
                        AiDebugLog.Write($"[AI] {player.Nickname}: removes stale {staleTask.Kind} task from empty army \"{army.Name}\".");
                    }
                }

                if (!GarrisonReorgTask.IsDisposableEmptyArmy(player, army))
                    continue;
                string name = army.Name;
                bool inField = !ownGarrisonHexes.Contains(army.Hex);
                // Same disposal primitive every other "empty shell, nobody left inside" case in this
                // codebase already uses (see e.g. AiAggressionPlanner.AssembleRaidForceRoutine/
                // DispatchReinforcementRoutine's own DeleteArmyIfEmptied calls) — deliberately still
                // refuses to tear one down sitting exactly on its own owner's Barracks hex (see that
                // method's own comment), which is fine here too: a shell parked right at a garrison
                // hex is free, instant reuse fodder for the very next RequestRaidArmy/
                // RequestDefendArmy/SpawnReconArmy/DispatchReinforcement that needs one there — the
                // ones actually worth cleaning up are the ones stranded away from any base, and
                // those DO get torn down here.
                ctx.HexSelection?.DeleteArmyIfEmptied(army);
                if (army.Controller == null)
                {
                    // 2026-08-24 P2 fix — a field shell (never at any of this player's own garrison
                    // hexes) gets its own explicit log: unlike a base-hex shell it has zero
                    // CurrentMovement left to ever walk home on its own and no ReturnForConsolidation
                    // task watching it (that task only ever registers for a non-empty stranded army —
                    // see GarrisonReorgTask.FindStrandedWeakArmies), so without this pass it would
                    // otherwise sit there orphaned forever (project owner's own report — see
                    // GarrisonReorgTask.DisposableEmptyArmies' own comment for the root cause this
                    // fixes: the old reserve pick could keep exactly this kind of shell "reserved").
                    AiDebugLog.Write(inField
                        ? $"[AI] {player.Nickname}: end-of-turn cleanup — disposed of orphaned empty army \"{name}\" "
                            + "in the field — it cannot move or return for consolidation."
                        : $"[AI] {player.Nickname}: end-of-turn cleanup — disposed of empty, task-less army \"{name}\".");
                    continue;
                }

                // P1 fix (2026-08-24, project owner's own code-review report): the call above
                // refused this one because it's sitting on its own owner's Barracks hex — fine for
                // an ordinary reuse-buffer shell, but a SURPLUS one (beyond maxSpareArmies) parked
                // there survived this whole pass forever, so the "at most maxSpareArmies empty
                // armies at end of turn" invariant never actually held for the base-hex case. See
                // GarrisonReorgTask.DeleteDisposableArmyAtBase's own comment — same eligibility
                // guard re-verified fresh, just without that one refusal. Tried second, only once
                // the ordinary call above has already declined.
                if (GarrisonReorgTask.DeleteDisposableArmyAtBase(army, player))
                    AiDebugLog.Write($"[AI] {player.Nickname}: end-of-turn cleanup — disposed of surplus empty army "
                        + $"\"{name}\" parked at its own base (beyond the {AiConfig.maxSpareArmies} spare-army reserve).");
            }
        }

        // Feature 4B (2026-08-24) — see GarrisonReorgTask.FindStrandedWeakArmies' own comment. P0
        // fix, same day (project owner's own code-review report): this used to ALSO try to move
        // the army right here — the very END of the turn, after the main Decide() loop had
        // usually already spent nearly all this turn's AP — and simply skipped it with no state
        // saved when the move couldn't be issued right then, despite a comment claiming it
        // "continues moving on subsequent turns" (nothing anywhere actually persisted that
        // intent). Effectively, a stranded army almost never made it home. Now this method only
        // DETECTS stranded armies and REGISTERS a real AiTaskKind.ReturnForConsolidation task for
        // each one that doesn't already have one (FindStrandedWeakArmies' own predicate already
        // excludes any army with an active task, so an army that got one last time this same sweep
        // ran simply won't be offered again) — no movement is attempted in this phase any more.
        // AiManagementPlanner.AdvanceReturnForConsolidationTask (wired into AiTurnController.
        // Decide's own per-step loop, same as every other in-flight task) does the actual walking,
        // over ordinary subsequent turn steps, with real AP/movement budget and real arbitration
        // against everything else competing for that step — see that method's own comment. Once it
        // actually arrives at a garrison hex, GarrisonReorgTask.FindReorgMove's own EXISTING
        // lone-army-fold tier (already running earlier this same phase, see the per-garrison loop
        // above) picks it up automatically on a LATER turn — no new consolidation logic needed
        // here at all, per the project owner's own original spec (unchanged by this fix).
        // Synchronous (no yield) — like RunEmptyArmyCleanup right above, nothing here issues a
        // move/spends AP, just registry bookkeeping.
        private static void RunStrandedArmyRecovery(PlayerSetupData player)
        {
            foreach (ArmyData army in GarrisonReorgTask.FindStrandedWeakArmies(player).ToList())
            {
                HexCoord homeHex = NearestOwnGarrisonHex(player, army.Hex);
                var task = new AiTask { Kind = AiTaskKind.ReturnForConsolidation, Army = army, TargetHex = homeHex };
                AiTaskRegistry.Add(player, task);
                AiDebugLog.Write($"[AI] {player.Nickname}: end-of-turn reorg — \"{army.Name}\" is stranded alone in "
                    + $"the field, registers a ReturnForConsolidation task home to ({homeHex.Q},{homeHex.R}).");
            }
        }

        // RunGarrisonReorgPhase's own circuit breaker — a cheap stand-in for "did anything actually
        // move on this hex", built as a name-keyed string rather than any ArmyData/UnitData object
        // identity, specifically so a freshly created destination army (SplitGarrisonArmyRoutine's
        // own no-existing-destination branch spawns a brand new ArmyData instance every time) still
        // shows up correctly as a change — the "before" string simply won't mention that army's name
        // yet. Approximate by design (unit/army NAME rather than a stable per-unit id — none exists
        // on UnitData right now) — good enough for "did the roster change", not meant for anything
        // identity-sensitive.
        private static string HexRosterSignature(PlayerSetupData player, HexCoord hex)
        {
            return string.Join("|", ArmyRegistry.AllForOwner(player)
                .Where(a => a.Hex.Equals(hex))
                .OrderBy(a => a.Name)
                .Select(a => a.Name + ":" + string.Join(",", a.Members.Select(m => m.Name).OrderBy(n => n))));
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
                    yield return AiManagementPlanner.ReserveArmyRoutine(player, ctx, decision.TargetHex);
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
                case AiActionKind.CollapseAssembly:
                    yield return AiManagementPlanner.CollapseTemporaryAssemblyRoutine(player, decision, ctx);
                    break;
                case AiActionKind.ConsolidateUnits:
                    yield return AiManagementPlanner.ConsolidateUnitsRoutine(player, decision, ctx);
                    break;
                case AiActionKind.ConsolidateSwap:
                    yield return AiManagementPlanner.ConsolidateSwapRoutine(player, decision, ctx);
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
                case AiActionKind.RequestDefendArmy:
                    yield return AiDefencePlanner.RequestDefendArmyRoutine(player, ctx, decision.TargetHex);
                    break;
                case AiActionKind.AssembleRaidForce:
                case AiActionKind.ActiveDefenceForce:
                    // Same execution either way — a single-recruit transfer into a forming army,
                    // Оборона's own ActiveDefenceForce kind exists only so debug output says which
                    // category actually asked for it (see AiDecision.ActiveDefenceForce's own
                    // comment).
                    yield return AiAggressionPlanner.AssembleRaidForceRoutine(player, decision, ctx);
                    break;
                case AiActionKind.BuildBase:
                    yield return AiAggressionPlanner.BuildBaseRoutine(player, decision, ctx);
                    break;
                case AiActionKind.SeedNewBaseGarrison:
                    yield return AiAggressionPlanner.SeedNewBaseGarrisonRoutine(player, decision, ctx);
                    break;
                case AiActionKind.StrengthenDefenceForce:
                    yield return AiDefencePlanner.StrengthenDefenceForceRoutine(player, decision, ctx);
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

            AiDebugLog.Write($"[AI] {player.Nickname}: \"{army.Name}\" (movement={army.CurrentMovement}/{army.MaxMovement}) "
                + $"from ({army.Hex.Q},{army.Hex.R}) heads to ({decision.TargetHex.Q},{decision.TargetHex.R}) — {decision.Reason}.");

            yield return PanTo(ctx, army.Hex);
            // Read-only — a human could otherwise drag units around inside the popup while it's
            // only meant to show what the AI is doing (map-click input is blocked during another
            // player's turn, but nothing gates in-panel dragging on its own — see
            // ArmyViewerModalUI.ShowReadOnly's own comment).
            if (ctx.ShowArmyModal && ctx.ArmyViewerModal != null)
                ctx.ArmyViewerModal.ShowReadOnly(army);
            yield return WaitStep(ctx);

            HexCoord destination = decision.TargetHex;
            yield return PanTo(ctx, destination);

            // Activating a not-yet-activated-this-turn army spends its own ActivationApCost (see
            // every candidate-gathering tier's own root.CanSpendActionPoints check) — snapshotted
            // here, before the order is issued, so the arrival log below can report it the same
            // way every other AP/resource-spending routine in this class does (see
            // ResourceDeltaSuffix's own comment).
            PlayerRoot root = PlayerRootRegistry.FindFor(player);
            int ap0 = root != null ? root.ActionPoints : 0;
            int human0 = root != null ? root.GetResource(ResourceType.Human) : 0;
            int energy0 = root != null ? root.GetResource(ResourceType.Energy) : 0;
            int materials0 = root != null ? root.GetResource(ResourceType.Materials) : 0;
            int tech0 = root != null ? root.GetResource(ResourceType.Tech) : 0;
            HexCoord before = army.Hex;
            MoveOrderResult moveResult = ctx.HexSelection != null
                ? ctx.HexSelection.IssueMoveOrder(army.Controller, destination)
                : MoveOrderResult.CannotMove;
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

            if (ctx.ShowArmyModal && ctx.ArmyViewerModal != null)
                ctx.ArmyViewerModal.Hide();

            // Diagnostic fix (2026-08-24, project owner's own report): this used to print one
            // catch-all "no path, no movement left, or a fight blocked the way" line regardless of
            // which of IssueMoveOrder's own guard clauses actually rejected the order — moveResult
            // (see HexSelectionController.Movement.MoveOrderResult) now names the exact one. A move
            // that reached a DIFFERENT hex than `destination` but still isn't `before` is partial
            // progress (ran out of shared movement points, a mid-path Hex Event, vision-revealed
            // contact, etc.) — never itself a failure, so it stays on the "arrived" branch exactly
            // as before; only "made zero progress at all" gets the reason tag.
            string moveDelta = root != null ? ResourceDeltaSuffix(root, ap0, human0, energy0, materials0, tech0) : null;
            AiDebugLog.Write(army.Hex.Equals(before)
                ? $"[AI] {player.Nickname}: \"{army.Name}\" made no progress toward its target — reason={moveResult} — stayed at ({army.Hex.Q}, {army.Hex.R})."
                : $"[AI] {player.Nickname}: \"{army.Name}\" arrived at ({army.Hex.Q}, {army.Hex.R}).{moveDelta}");

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

            // Snapshotted here, before ANY resource-spending step below (CreateArmy's own AP
            // cost included) — not just before DeployUnitFromCard — so the final delta this
            // routine reports covers the WHOLE action (see this method's own class comment on
            // why every resource-spending routine reports a before/after snapshot).
            int ap0 = root.ActionPoints;
            int human0 = root.GetResource(ResourceType.Human);
            int energy0 = root.GetResource(ResourceType.Energy);
            int materials0 = root.GetResource(ResourceType.Materials);
            int tech0 = root.GetResource(ResourceType.Tech);

            if (targetArmy == null)
            {
                targetArmy = ArmyActions.CreateArmy(player, hex, ctx.StartingDeckCatalog?.GetCatalog(player.Faction), ctx.HexSelection);
                if (targetArmy == null)
                {
                    AiDebugLog.Write($"[AI] {player.Nickname}: not enough AP for a new army for card {decision.Card.Definition.displayName}.");
                    yield break;
                }
                AiDebugLog.Write($"[AI] {player.Nickname}: creates new army \"{targetArmy.Name}\" for card {decision.Card.Definition.displayName}.");
            }

            if (ctx.ShowArmyModal && ctx.ArmyViewerModal != null)
                ctx.ArmyViewerModal.ShowReadOnly(targetArmy);
            yield return WaitStep(ctx);

            bool deployed = ArmyActions.DeployUnitFromCard(decision.Card.Definition, player, targetArmy, root, ctx.HexSelection, out string failReason);
            if (deployed)
            {
                AiHandData hand = AiHandRegistry.GetOrCreate(player, ctx.StartingDeckCatalog, ctx.StartingHandSize);
                hand?.Hand.Remove(decision.Card);
                // See AiManagementPlanner.NotifyCardRolePlayed's own comment — only flips the
                // Hero/Unit alternation state once the card has actually deployed, not on a
                // merely-proposed candidate.
                AiManagementPlanner.NotifyCardRolePlayed(player, AiManagementPlanner.RoleOf(decision.Card));
                string delta = ResourceDeltaSuffix(root, ap0, human0, energy0, materials0, tech0);
                AiDebugLog.Write($"[AI] {player.Nickname}: {decision.Card.Definition.displayName} joins \"{targetArmy.Name}\" — {decision.Reason}.{delta}");
            }
            else
            {
                AiDebugLog.Write($"[AI] {player.Nickname}: couldn't deploy {decision.Card.Definition.displayName} — {failReason}");
            }

            if (ctx.ShowArmyModal && ctx.ArmyViewerModal != null)
                ctx.ArmyViewerModal.Hide();
            yield return WaitStep(ctx);
        }

        // Internal, not private — every Level-1 category planner needs the garrison hex/army as
        // a common Level-0 lookup (VisitHexTask's own TryFlee, AiAggressionPlanner's own
        // assemble/recall/return-home tiers, etc.).
        //
        // Pinned to the STARTING citadel specifically (player.CitadelHexQ/R, set once at game
        // start — see CitadelSetupController.SpawnCitadelMarker/CreateGarrison, which always
        // creates the very first IsGarrison army on that same hex) rather than the old
        // `ArmyRegistry.AllForOwner(player).FirstOrDefault(a => a.IsGarrison)`. That FirstOrDefault
        // was only ever safe while a player could have AT MOST one IsGarrison army; a later-founded
        // Base with Barracks now spawns its own second one (see HexSelectionController.Factory.
        // SpawnBuilding), which made the old lookup pick an arbitrary one of the two depending on
        // ArmyRegistry's own enumeration order — every caller of "the garrison" silently got a
        // coin-flip once a second base existed. Разведка/Экономика deliberately keep reading
        // CitadelHexQ/R directly rather than through this method (project owner's own call — they
        // stay anchored to the original citadel only); everything that DOES need to consider a
        // later-founded base uses NearestOwnGarrisonHex/NearestOwnGarrisonArmy below instead, never
        // this one.
        internal static HexCoord GarrisonHexFor(PlayerSetupData player)
        {
            return player.CitadelHexQ.HasValue && player.CitadelHexR.HasValue
                ? new HexCoord(player.CitadelHexQ.Value, player.CitadelHexR.Value)
                : default;
        }

        internal static ArmyData GarrisonArmyFor(PlayerSetupData player)
        {
            HexCoord citadelHex = GarrisonHexFor(player);
            return ArmyRegistry.AllForOwner(player).FirstOrDefault(a => a.IsGarrison && a.Hex.Equals(citadelHex));
        }

        // Multi-base-aware counterpart — the closest of this player's own garrisoned hexes
        // (the starting citadel, or any later-founded Base with Barracks — see SpawnBuilding) to
        // `fromHex`, ties broken toward the starting citadel (the project owner's own explicit
        // "жёсткий tie-break" call for Агрессия/Оборона — the citadel wins whenever two bases would
        // otherwise compete equally for the same AP/recruits/cards; the actual scoring side of that
        // priority is a later phase, this method only owns the distance tie-break itself). Falls
        // back to GarrisonHexFor/GarrisonArmyFor (the citadel) if no IsGarrison army exists at all
        // yet, same as those two always have.
        internal static HexCoord NearestOwnGarrisonHex(PlayerSetupData player, HexCoord fromHex) =>
            NearestOwnGarrisonArmy(player, fromHex)?.Hex ?? GarrisonHexFor(player);

        // Every one of this player's own garrison armies (the starting citadel's, plus any
        // later-founded Base with Barracks), citadel first — RunGarrisonReorgPhase's own per-
        // garrison drain loop is the first reader; AiManagementPlanner's multi-base routing
        // (FindPlacement/ReserveArmyRoutine) reads OwnGarrisonHexes below instead, since it only
        // needs the hex, not the army itself.
        internal static IEnumerable<ArmyData> OwnGarrisonArmies(PlayerSetupData player)
        {
            HexCoord citadelHex = GarrisonHexFor(player);
            return ArmyRegistry.AllForOwner(player).Where(a => a.IsGarrison)
                .OrderBy(a => a.Hex.Equals(citadelHex) ? 0 : 1);
        }

        // Every one of this player's own garrisoned hexes (the starting citadel, plus any
        // later-founded Base with Barracks) — AiDefencePlanner.TryStartDefenceCandidates' own
        // per-home loop is the first reader, iterating this to give each base its own DefendCitadel
        // task instead of the old single shared one.
        internal static IEnumerable<HexCoord> OwnGarrisonHexes(PlayerSetupData player) =>
            OwnGarrisonArmies(player).Select(a => a.Hex).Distinct();

        internal static ArmyData NearestOwnGarrisonArmy(PlayerSetupData player, HexCoord fromHex)
        {
            HexCoord citadelHex = GarrisonHexFor(player);
            ArmyData best = null;
            int bestDistance = int.MaxValue;
            foreach (ArmyData army in ArmyRegistry.AllForOwner(player).Where(a => a.IsGarrison))
            {
                int distance = HexGridMath.Distance(fromHex, army.Hex);
                // Strict less-than only — a tie (equidistant citadel vs. a later base) keeps
                // whichever was already found first; iterating ArmyRegistry's own order isn't
                // guaranteed citadel-first, so the citadel is checked explicitly below instead of
                // relying on enumeration order for the tie-break.
                if (distance < bestDistance || (distance == bestDistance && army.Hex.Equals(citadelHex)))
                {
                    bestDistance = distance;
                    best = army;
                }
            }
            return best ?? GarrisonArmyFor(player);
        }

        // Shared "one safe step toward `destination`" primitive — HARD-blocks every hex within
        // `avoidRadius` of `avoidCenter` (0 = just that one hex, same shape the original single-
        // hex retreat block already used), always exempting `destination` itself so the final step
        // can still enter it even from inside the buffer (the project owner's own call for
        // AiDefencePlanner's Turtle march-home: the last step may ignore the buffer if it lands the
        // army in the garrison). Returns only the NEXT hex, never the full path — same "re-evaluate
        // fresh next Decide call" shape every other travel decision here already follows. Shared by
        // AiAggressionPlanner (both its own ordinary threat-retreat, radius 0, and Turtle's forced
        // raid-recall, radius AiConfig.defenceRetreatAvoidRadius) and AiDefencePlanner (Turtle's own
        // march to the garrison) so the two can never drift apart on what "avoid the threat while
        // retreating" means.
        internal static HexCoord? FindPathStepAvoidingZone(HexMap map, ArmyData army, HexCoord destination,
            HexCoord? avoidCenter, int avoidRadius)
        {
            System.Func<HexCoord, bool> blockHex = avoidCenter.HasValue
                ? (System.Func<HexCoord, bool>)(hex => !hex.Equals(destination) && HexGridMath.Distance(hex, avoidCenter.Value) <= avoidRadius)
                : null;
            return FindAffordableStep(map, army, destination, blockHex);
        }

        // Centralized "one step toward `destination`, but only if the army can actually afford to
        // enter it this turn" — HexPathfinder.FindPath only guarantees a route EXISTS, never that
        // its first step's terrain cost fits inside army.CurrentMovement. A rough-terrain hex right
        // next to the army can already cost more than everything it has left, and a planner that
        // only checks "does a path exist" (or checks affordability against a DIFFERENT, unblocked
        // path than the one it actually walks) can hand HexSelectionController.Movement.
        // IssueMoveOrder's own matching first-step check a move order it's guaranteed to reject
        // outright — 2026-08-23 fix (project owner's own report, two live cases: a Разведка scout
        // and an Экономика hero both ordered onto a next hex costing more movement than either had
        // left). Every planner that proposes a next-step move candidate now routes through this one
        // method — directly, or via FindPathStepAvoidingZone above — instead of reading
        // path.Hexes[1] by hand, so this only needs fixing once. Null both when no route exists yet
        // and when the only route's first step is already unaffordable — either way "nothing to do
        // this step, retry next step/turn", never "walk as far as the path allows" (that fallback
        // already lives at the IssueMoveOrder layer, for a full-target move like Задача 2's own
        // ResourcesScrap — this helper is only for callers that need the SINGLE next hex).
        internal static HexCoord? FindAffordableStep(HexMap map, ArmyData army, HexCoord destination,
            System.Func<HexCoord, bool> blockHex = null)
        {
            if (map == null || army == null || destination.Equals(army.Hex))
                return null;
            HexPath path = HexPathfinder.FindPath(map, army.Hex, destination, blockHex: blockHex);
            if (path == null || path.Hexes.Count < 2)
                return null;
            HexCoord step = path.Hexes[1];
            map.TryGetTerrainAt(step, out TerrainTypeEntry entry);
            int cost = entry != null ? Mathf.Max(1, entry.moveCost) : 1;
            return army.CurrentMovement >= cost ? step : (HexCoord?)null;
        }

        // Shared MoveArmy candidate-feasibility gate (2026-08-23, project owner's own report):
        // a candidate used to get proposed the moment ANY category planner had a destination in
        // mind, with no check that the mover could actually pay for it right now — two independent
        // real cases, same missing gate: Halden's ReinforceSwap left "Swarm" at 0 AP and the very
        // next Decide() still proposed MoveArmy for it (AiAggressionPlanner.TryRaidAssembleCandidates'
        // own FindReadyIdleArmy branch had no feasibility check at all), and Sable's Defence patrol
        // proposed a 3-AP move while 2 AP remained. IssueMoveOrder (see HexSelectionController.
        // Movement.cs) then silently rejected the order either way, wasting the whole step. Folds
        // together the two things IssueMoveOrder itself independently enforces before it ever
        // accepts an order: FindAffordableStep's own movement-point/path check for the FIRST step
        // only (never the whole route — a multi-turn journey is fine, a step this Decide() call
        // can't even begin is not), plus the one-time ActivationApCost an army not yet activated
        // this turn also has to afford out of the shared AP pool. Every category's candidate sites
        // now route through this one helper instead of each growing its own copy of either check —
        // since both conditions are things execution already independently requires, a candidate
        // this rejects would always have failed at IssueMoveOrder anyway, so gating it here only
        // ever removes a doomed candidate from arbitration, never a viable one.
        internal static bool CanIssueMoveNow(PlayerRoot root, ArmyData army, HexMap map, HexCoord destination) =>
            root != null && army != null && FindAffordableStep(map, army, destination).HasValue
                && (army.HasActivatedThisTurn || root.CanSpendActionPoints(army.ActivationApCost));

        // Problem 4 (2026-08-24, project owner's own report): a raw armies=X→Y count in the
        // turn-ends line can't tell fragmentation (a growing pile of small leftover armies) apart
        // from healthy growth (a few well-formed ones) — Halden went 4→7→10 armies over two turns
        // with no way to see WHAT those were. Diagnostic-only instrumentation: breaks the same
        // non-garrison/non-prison count the turn-ends line already reports down by which Level-1
        // task category currently owns each army (AiTaskRegistry.TaskFor's own Category, "untasked"
        // for an idle army with no active task at all — see AiTask.cs's own AiTaskCategory comment)
        // and by roster size. Never touches scoring or creation limits — read-only over whatever
        // RunTurn already produced this turn.
        private static string BuildArmyBreakdownLog(PlayerSetupData player, int turnNumber)
        {
            List<ArmyData> armies = ArmyRegistry.AllForOwner(player).Where(a => !a.IsGarrison && !a.IsPrison).ToList();
            int totalUnits = armies.Sum(a => a.Members.Count);
            float avgUnitsPerArmy = armies.Count > 0 ? (float)totalUnits / armies.Count : 0f;

            string categoryBreakdown = armies.Count > 0
                ? string.Join(", ", armies
                    .GroupBy(a => AiTaskRegistry.TaskFor(player, a)?.Category)
                    .OrderByDescending(g => g.Count())
                    .Select(g => $"{(g.Key.HasValue ? g.Key.Value.ToString() : "untasked")}={g.Count()}"))
                : "none";

            int emptyArmies = armies.Count(a => a.Members.Count == 0);
            int soloArmies = armies.Count(a => a.Members.Count == 1);
            int multiArmies = armies.Count(a => a.Members.Count >= 2);

            // Feature 4's own extension (2026-08-24, project owner's own ask) — the plain
            // "0=X, 1=Y, 2+=Z" breakdown above already existed (Problem 4, 2026-08-24) but couldn't
            // tell a HEALTHY 0/1-roster army (a deliberately kept spare, a Recce riding solo by
            // design) apart from the genuinely wasteful kind Feature 4A/4B exist to clean up/recover
            // — this splits each of those two buckets further, reusing the exact same predicates
            // Feature 4A/4B's own sweeps already read (GarrisonReorgTask.IsDisposableEmptyArmy/
            // FindStrandedWeakArmies) so this log can never disagree with what those two stages
            // actually did/will do this same phase.
            //
            // 0 members: "reserved" — within GatherFallbackCandidates' own maxSpareArmies buffer,
            // task-less (the deliberately kept reserve, see IsDisposableEmptyArmy's own comment);
            // "reusable" — task-less and beyond that buffer (IsDisposableEmptyArmy true — exactly
            // what RunEmptyArmyCleanup just tried to hand out for reuse or dispose of); "orphaned" —
            // everything else at 0 members (e.g. still task-claimed but its roster emptied out from
            // under it — IsDisposableEmptyArmy deliberately leaves a task-owned shell alone).
            int emptyReusable = armies.Count(a => GarrisonReorgTask.IsDisposableEmptyArmy(player, a));
            int emptyReserved = armies.Count(a => a.Members.Count == 0 && AiTaskRegistry.TaskFor(player, a) == null)
                - emptyReusable;
            int emptyOrphaned = emptyArmies - emptyReserved - emptyReusable;

            // 1 member: "tasked" — an active AiTask OTHER than ReturnForConsolidation owns it (a
            // courier, a raid/defence recruit still solo, a scout mid-VisitHex, ...); "solo-recce"
            // — task-less and AiArmyRoles.IsSoloRecce (left alone by design, see
            // FindStrandedWeakArmies' own comment); "returning" — carries an active
            // ReturnForConsolidation task (2026-08-24 P0 fix: this used to read
            // GarrisonReorgTask.FindStrandedWeakArmies directly, back when RunStrandedArmyRecovery
            // gave these a move-home decision the very same phase this log runs right after — now
            // that Feature 4B registers a real task instead (see that fix's own comment), a
            // just-registered stranded army already has one and FindStrandedWeakArmies itself would
            // report it as gone/tasked, not stranded, by the time this log reads it; reading the
            // task Kind directly instead keeps this bucket meaningful); "orphaned" — the rare
            // remainder (e.g. already sitting AT a garrison hex, task-less, non-Recce — IdleBalance's
            // own lone-army-fold tier earlier this same phase should ordinarily have already folded
            // these in before this log even runs).
            int soloTasked = armies.Count(a => a.Members.Count == 1 && AiTaskRegistry.TaskFor(player, a) != null
                && AiTaskRegistry.TaskFor(player, a).Kind != AiTaskKind.ReturnForConsolidation);
            int soloRecce = armies.Count(a => a.Members.Count == 1 && AiTaskRegistry.TaskFor(player, a) == null && AiArmyRoles.IsSoloRecce(a));
            int soloReturning = armies.Count(a => a.Members.Count == 1
                && AiTaskRegistry.TaskFor(player, a)?.Kind == AiTaskKind.ReturnForConsolidation);
            int soloOrphaned = soloArmies - soloTasked - soloRecce - soloReturning;

            return $"[AI] {player.Nickname}: army breakdown (turn {turnNumber}) — armies={armies.Count} ({categoryBreakdown}), "
                + $"units={totalUnits}, avgUnitsPerArmy={avgUnitsPerArmy:0.0}, roster size: 0={emptyArmies} "
                + $"(reserved={emptyReserved}, reusable={emptyReusable}, orphaned={emptyOrphaned}), 1={soloArmies} "
                + $"(tasked={soloTasked}, solo-recce={soloRecce}, returning={soloReturning}, orphaned={soloOrphaned}), 2+={multiArmies}";
        }

        // Feature 1's own diagnostic (2026-08-24) — BuildFacilityTask.RankHex has no reason string
        // of its own to append "hand demand +N" onto (it's a pure internal ranking float, unlike
        // RaidWeakerArmyTask.FindTarget's own candidates, which already build one) — the nearest
        // place a log reader would actually look to see WHY a given resourceType got picked is this
        // "starts Economy task" line itself (also used by AiManagementPlanner.
        // SplitGarrisonArmyRoutine's own matching hero-detach log), so that's where this appends
        // instead. Reads the exact same AiManagementPlanner.ComputeHandResourceDemand signal
        // RankHex's own HandDemandBonus already folded into the ranking that picked this hex — never
        // recomputes anything new, just makes the existing internal signal visible in the log.
        internal static string HandDemandLogSuffix(PlayerSetupData player, ResourceType? resourceType, PlayerRoot root, AiHandData hand)
        {
            if (!resourceType.HasValue || root == null)
                return "";
            Dictionary<ResourceType, float> demand = AiManagementPlanner.ComputeHandResourceDemand(player, root, hand);
            float amount = demand.TryGetValue(resourceType.Value, out float d) ? d : 0f;
            return amount > 0f ? $" (hand demand +{amount:0})" : "";
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
            return parts.Count > 0 ? $" — resources: {string.Join(", ", parts)}" : null;
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
                : "empty";
            AiDebugLog.Write($"[AI] {player.Nickname}: checks hand ({hand.Hand.Count} in hand, "
                + $"{hand.RemainingDeckCount} left in deck) — {cards}.");
        }

        // Every persistent AiTask still standing at the start of this turn — the "уровень
        // подзадачи" state Decide's own per-step candidate dump doesn't otherwise show, since a
        // task only produces a fresh candidate once it's actually re-evaluated this step (see
        // Decide's own class comment). Silent when there's nothing active, same as LogHand's own
        // "empty" case not needing a separate empty-state line. Each entry is tagged with its own
        // Category (see AiTask.Category) the same way a per-step candidate/decided line is now —
        // this is the one place a persistent task's category shows up even on a turn it produces
        // no fresh candidate at all.
        private static void LogActiveTasks(PlayerSetupData player)
        {
            IReadOnlyList<AiTask> tasks = AiTaskRegistry.TasksFor(player);
            if (tasks.Count == 0)
                return;
            string list = string.Join("; ", tasks.Select(t =>
                $"{t.Kind}({t.Category}):\"{t.Army?.Name ?? "?"}\"→({t.TargetHex.Q},{t.TargetHex.R})"));
            AiDebugLog.Write($"[AI] {player.Nickname}: active tasks ({tasks.Count}) — {list}.");
        }
    }
}
