using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Aviation;
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
        // Kept alongside StartingDeckCatalog/etc (all read from this same source, see From)
        // purely so RunTurn can push live hand updates to CardHandUI.RefreshAiHandDebugIfShowing
        // — a no-op whenever debugWatchAiTurns isn't showing this player's hand, so this never
        // needs to know that flag itself.
        public CardHandUI HumanCardHandUI;
        public StartingDeckCatalog StartingDeckCatalog;
        public int StartingHandSize;
        public int DrawApCost;
        // Research/Production catalog (P0, 2026-08-28) — the same ScriptableObject
        // ResearchProductionModalUI pages through for the human, threaded in so
        // AiDevelopmentPlanner can read the offered card lists headlessly. Null when the scene has
        // no modal wired — Development then simply never fires.
        public ResearchProductionCatalog ResearchProductionCatalog;
        // Shared hand capacity (spec §10) — CardHandUI.MaxHandSize, pushed onto each AI player's
        // AiHandData in RunTurn so a won Research/Production Challenge can't mint into a full hand.
        public int HandCapacity = 10;
        public float StepDelay = 0.5f;
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

        // PlayCard candidates that already failed to actually deploy THIS turn (2026-08-26 P1
        // fix, project owner's own report) — an aviation card wrongly routed into a non-aviation
        // candidate pipeline kept re-scoring itself and re-failing PlayCardRoutine's own deploy
        // call every further step, burning the whole turn's step budget on the same doomed
        // candidate. A fresh, empty set every turn, same shape as UnitVisitedArmies above — a
        // card is free to fail and be retried NEXT turn once whatever made it fail may have
        // changed (a place freed up, AP replenished), only a same-turn repeat is blocked. Every
        // PlayCard candidate source (AiManagementPlanner.TryPlayCardCandidates, AiScoutPlanner's
        // own Recce pipeline, AiAggressionPlanner.TryHeroCardForRaid) must skip a card in here;
        // PlayCardRoutine adds to it the moment its own deploy call reports failure.
        public readonly HashSet<CardData> FailedPlayCardsThisTurn = new HashSet<CardData>();

        // Turn-scoped Research/Production attempts (spec §11) — one entry per (hero, mode, card)
        // combination already Challenged this turn, win or lose. AiDevelopmentPlanner skips any
        // combination in here, so a failed Challenge's spent resources can't be burned again by
        // the same combination re-winning arbitration on a later step this same turn. Fresh and
        // empty every turn (From builds a new AiTurnContext per RunTurn), so the option returns
        // automatically next turn.
        public readonly HashSet<(UnitData Hero, ResearchProductionMode Mode, CardDefinition Card)> DevelopmentAttemptsThisTurn
            = new HashSet<(UnitData, ResearchProductionMode, CardDefinition)>();

        public bool HasTriedDevelopment(UnitData hero, ResearchProductionMode mode, CardDefinition card)
            => DevelopmentAttemptsThisTurn.Contains((hero, mode, card));

        public void RecordDevelopmentAttempt(UnitData hero, ResearchProductionMode mode, CardDefinition card)
            => DevelopmentAttemptsThisTurn.Add((hero, mode, card));

        public static AiTurnContext From(RtsCameraController camera, HexMap map, HexSelectionController hexSelection,
            CardHandUI humanCardHand, float stepDelay,
            GameConfig gameConfig, int turnNumber,
            ResearchProductionCatalog researchProductionCatalog = null)
        {
            return new AiTurnContext
            {
                Camera = camera,
                Map = map,
                HexSelection = hexSelection,
                HumanCardHandUI = humanCardHand,
                StartingDeckCatalog = humanCardHand != null ? humanCardHand.StartingDeckCatalog : null,
                StartingHandSize = humanCardHand != null ? humanCardHand.StartingHandSize : 0,
                DrawApCost = humanCardHand != null ? humanCardHand.DrawApCost : 2,
                ResearchProductionCatalog = researchProductionCatalog,
                HandCapacity = humanCardHand != null ? humanCardHand.MaxHandSize : 10,
                StepDelay = stepDelay,
                GameConfig = gameConfig,
                TurnNumber = turnNumber,
            };
        }
    }

    // Optional out-channel for MoveArmyRoutine, filled BEFORE it awaits async battle/event
    // resolution. A caller that needs to know "did a fight / hex event actually happen on this
    // step" cannot recover it from world state afterwards: MoveArmyRoutine only returns once
    // IsBattleActive has gone false again, and an AI mover's hex events resolve synchronously
    // (no popup), so by return time every trace of the contact may be gone. V2's TaskExecutor
    // passes one of these; every V1 call site leaves it null and is unaffected.
    public sealed class AiMoveExecutionTrace
    {
        public MoveOrderResult MoveResult;   // exactly what IssueMoveOrder returned
        public bool BattleOccurred;          // a fight was open (or the post-move safety net opened one) on this step
        public bool HexEventOccurred;        // a clean Hex Event resolved (explored or skipped) during this step
        public bool ReachedDestination;      // the mover ended on decision.TargetHex
        public HexCoord EndHex;              // where the mover PHYSICALLY stopped — captured before any battle, survives the mover's death
        public bool EnteredStealthThisStep;  // a solo Recce slipped into stealth before this move (V2 step-7 "made progress" signal)
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

            // Pass the shared hand cap (spec §10) straight into GetOrCreate — it applies it on
            // creation AND re-applies it (SetCapacity) on every later turn, so the cap is a real
            // invariant from the hand's first card, never a window between construction and a
            // post-hoc field assignment where a starting-hand draw could overflow it.
            AiHandData hand = AiHandRegistry.GetOrCreate(player, ctx.StartingDeckCatalog, ctx.StartingHandSize, ctx.HandCapacity);

            // ---- AI Strategy V2 fork (2026-08-29) — see Game.Ai.V2 / AiStrategyV2Pipeline.cs ----
            // V1 (everything below this block) is the shipping default. V2 is a parallel,
            // independently switchable pipeline behind AiConfig.aiStrategyV2Enabled. The two NEVER
            // both run in one AI turn: with the flag set, V2 owns the turn end to end and RunTurn
            // returns right here. V1 code below is left fully intact for method-by-method porting
            // into V2 — do NOT delete it. Placed after AiMapMemory.OnTurnStarted + hand creation so
            // V2 inherits the same freshly-expired memory and hand the V1 path would have seen.
            if (AiConfig.aiStrategyV2Enabled)
            {
                yield return Game.Ai.V2.Pipeline.RunTurn(player, root, hand, ctx);
                onDone?.Invoke();
                yield break;
            }

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

            // Strategic layer (2026-08-27) — one per-turn assessment of what this player wants to
            // be doing (AiStrategyDirector), then an AP/resource split by those desires
            // (AiTurnBudget). Both live exactly as long as `pool` does: computed once here, threaded
            // through every Decide call this turn, and `budget` accumulates spend across steps.
            // Decide tilts each candidate's score by its category's axis + how far that category is
            // over budget (AiStrategyLayer.Adjust). A no-op at AiConfig.strategyAxisGain = 0.
            AiStrategyAssessment strategy = AiStrategyDirector.Evaluate(player, root, hand, ctx);
            var budget = new AiTurnBudget(root.ActionPoints, strategy);
            AiDebugLog.Write($"[AI] {player.Nickname}: {budget.DebugLine()}");

            // Operations layer (2026-08-27) — advance/retire active multi-turn campaigns and start
            // a new Offensive when the strategic posture calls for it, BEFORE the step loop so the
            // raid task it adopts is already pinned to its objective for this turn's Decide calls.
            AiOperationPlanner.AssessAll(player, root, ctx, strategy);

            // Per-Kind tally for the turn-end summary below — Pass excluded (it only ever ends the
            // turn, never itself represents work done).
            var actionCounts = new Dictionary<AiActionKind, int>();

            for (int step = 0; step < AiConfig.maxStepsPerTurn; step++)
            {
                AiDecision decision = Decide(player, root, hand, ctx, stuckScouts, pool, strategy, budget);
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
                int apBeforeDecision = root.ActionPoints;
                yield return PerformDecision(player, decision, ctx);
                // Attribute this step's actual AP spend to its category so AiTurnBudget can throttle
                // a category that's blowing its share (see AiStrategyLayer.Adjust). AP-free steps
                // (AssembleRaidForce, DrawCard, ...) record nothing.
                if (decision.Category.HasValue)
                    budget.RecordSpend(decision.Category.Value, apBeforeDecision - root.ActionPoints);
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
        // citadel, now gated on IsUnderSiege) plus its own SecureBase tier (AiDefencePlanner.
        // TryStartSecureBaseCandidates, 2026-08-24 — a separate persistent task per non-citadel base
        // whose own garrison isn't AiArmyRoles.IsBaseGarrisonSecure yet; see SecureBaseTask's own
        // class comment for the full trigger/lifecycle) — each still respects its own cap
        // (MaxConcurrentVisitHex/MaxConcurrentRaid/maxConcurrentDefend/maxConcurrentSecureBase) before generating any
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
            HashSet<ArmyData> stuckScouts, AiResourcePool pool, AiStrategyAssessment strategy, AiTurnBudget budget)
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
            // Before the raid-assemble tiers below (TryRaidAssembleCandidates) so an army that
            // arrives home this step is released back into the pool in time for them to fold it in.
            foreach (AiTask task in AiTaskRegistry.TasksFor(player).Where(t => t.Kind == AiTaskKind.ReturnForRaidAssembly).ToList())
            {
                if (stuckScouts.Contains(task.Army))
                    continue;
                AiDecision decision = AiAggressionPlanner.AdvanceReturnForRaidAssemblyTask(player, root, ctx, task, pool);
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
            foreach (AiTask task in AiTaskRegistry.TasksFor(player).Where(t => t.Kind == AiTaskKind.SecureBase).ToList())
            {
                if (stuckScouts.Contains(task.Army))
                    continue;
                AiDecision decision = AiDefencePlanner.TryContinueSecureBaseTask(player, root, ctx, task);
                if (decision != null)
                    candidates.Add(decision);
            }
            foreach (AiTask task in AiTaskRegistry.TasksFor(player).Where(t => t.Kind == AiTaskKind.AirStrike).ToList())
            {
                if (stuckScouts.Contains(task.Army))
                    continue;
                AiDecision decision = AiAggressionPlanner.TryContinueAirStrikeTask(player, root, ctx, task, pool);
                if (decision != null)
                    candidates.Add(decision);
            }
            foreach (AiTask task in AiTaskRegistry.TasksFor(player).Where(t => t.Kind == AiTaskKind.AirRecon).ToList())
            {
                if (stuckScouts.Contains(task.Army))
                    continue;
                AiDecision decision = AiScoutPlanner.TryContinueAirReconTask(player, root, ctx, task);
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
            foreach (AiTask task in AiTaskRegistry.TasksFor(player).Where(t => t.Kind == AiTaskKind.Develop).ToList())
            {
                if (stuckScouts.Contains(task.Army))
                    continue;
                AiDecision decision = AiDevelopmentPlanner.TryContinueDevelopTask(player, root, ctx, hand, task);
                if (decision != null)
                    candidates.Add(decision);
            }

            candidates.AddRange(AiScoutPlanner.TryReturnHomeCandidates(player, root, ctx, stuckScouts));
            candidates.AddRange(AiEconomyPlanner.TryEconomyReturnHomeCandidates(player, root, ctx, stuckScouts));
            candidates.AddRange(AiEconomyPlanner.TryStartEconomyCandidates(player, root, ctx, stuckScouts, pool));
            candidates.AddRange(AiEconomyPlanner.TryStartResourcesScrapCandidates(player, root, ctx, pool));
            candidates.AddRange(AiEconomyPlanner.TryStartCollectorDetachCandidates(player, root, ctx.Map, pool));
            candidates.AddRange(AiScoutPlanner.TryStartVisitCandidates(player, root, ctx, pool, stuckScouts));
            candidates.AddRange(AiAggressionPlanner.TryRaidAssembleCandidates(player, root, ctx, hand, pool));
            candidates.AddRange(AiAggressionPlanner.TryRaidRecallCandidates(player, root, ctx, pool, stuckScouts));
            candidates.AddRange(AiAggressionPlanner.TryRaidReturnHomeCandidates(player, root, ctx, pool, stuckScouts));
            candidates.AddRange(AiAggressionPlanner.TryRaidRegroupCandidates(player, root, ctx, pool, stuckScouts));
            candidates.AddRange(AiAggressionPlanner.TryStartBuildBaseCandidates(player, root, ctx, hand, pool));
            candidates.AddRange(AiOperationPlanner.EmitDirectives(player, root, ctx));
            candidates.AddRange(AiDefencePlanner.TryStartSecureBaseCandidates(player, root, ctx));
            candidates.AddRange(AiDefencePlanner.TryStartDefenceCandidates(player, root, ctx, pool));
            candidates.AddRange(AiDefencePlanner.TryDefencePreemptCandidates(player, root, ctx));
            candidates.AddRange(AiScoutPlanner.TryStartReconAssemblyCandidates(player, root, ctx, hand, pool));
            // AirRecon's own gate condition 1 (see AiScoutPlanner.TryStartAirReconCandidates' own
            // comment) needs THIS step's AirStrike start-candidates specifically — captured here,
            // before AirRecon is asked, rather than re-deriving them a second time.
            List<AiDecision> airStrikeCandidates = AiAggressionPlanner.TryStartAirStrikeCandidates(player, root, ctx, pool);
            candidates.AddRange(airStrikeCandidates);
            candidates.AddRange(AiScoutPlanner.TryStartAirReconCandidates(player, root, ctx, pool, airStrikeCandidates));

            foreach (AiTask task in AiTaskRegistry.TasksFor(player).Where(t => t.Kind == AiTaskKind.RepairUnit).ToList())
            {
                AiDecision decision = AiManagementPlanner.AdvanceRepairTask(player, root, hand, task);
                if (decision != null)
                    candidates.Add(decision);
            }
            candidates.AddRange(AiDevelopmentPlanner.TryStartDevelopmentCandidates(player, root, ctx, hand, pool));
            candidates.AddRange(AiManagementPlanner.TryStartRepairCandidates(player, root, hand));
            candidates.AddRange(AiManagementPlanner.TryPlayCardCandidates(player, root, hand, ctx));
            candidates.AddRange(AiManagementPlanner.TrySupportCardCandidates(player, root, hand, ctx));
            candidates.AddRange(AiManagementPlanner.GatherFallbackCandidates(player, root, hand, ctx));

            // Strategic tilt (2026-08-27) — nudge every categorized candidate by its desire axis
            // and its category's over-budget penalty BEFORE the dump log and the arbiter, so both
            // see the same adjusted numbers. Additive and bounded (see AiStrategyLayer.Adjust) —
            // never a hard gate, a genuinely urgent candidate still wins. Pass (Category null) is
            // untouched. Operation-owned candidates are exempted from the AP-budget penalty (pass
            // budget: null) but still ride the axis tilt: the operation already cleared its own
            // feasibility / siege / deadline / hopelessness checks and is a durable multi-turn
            // commitment, so the shared Aggression over-budget penalty (up to -30) must not be
            // able to sink it below routine work before the operation boost is even applied.
            if (AiConfig.strategyLayerEnabled)
                foreach (AiDecision candidate in candidates)
                    if (candidate.Category.HasValue)
                        candidate.Score = AiStrategyLayer.Adjust(
                            candidate.Score, candidate.Category.Value, strategy,
                            AiOperationPlanner.IsOperationTask(candidate.Task) ? null : budget);

            // Commitment layer (2026-08-28) — a narrow, per-task sunk-cost bump for an already
            // registered, non-operation RaidWeakerArmy task that has finished assembling: it must
            // not keep losing the step to routine reconnaissance scoring a few points above a raid
            // the AI already paid to build and is cleared to launch. Runs AFTER the strategic tilt
            // (it operates on the tilted score) and BEFORE the operations boost. No-op for
            // everything else — see AiCommitmentLayer.Adjust.
            foreach (AiDecision candidate in candidates)
                candidate.Score = AiCommitmentLayer.Adjust(player, candidate);

            // Operations boost (2026-08-27) — a candidate advancing an operation-owned task rides a
            // flat bump on top of the strategic tilt so the campaign's own work stays reliably
            // ahead of unrelated routine candidates. Routine only, and capped at strategyExemptScore-1
            // (119): the boost must never push operation work into the tactical/emergency band and
            // swamp a genuine Defence Active / Scout Flee / Turtle. Candidates already at/above the
            // exempt line are tactical themselves and are left untouched.
            foreach (AiDecision candidate in candidates)
                if (AiOperationPlanner.IsOperationTask(candidate.Task)
                    && candidate.Score < AiConfig.strategyExemptScore)
                    candidate.Score = Mathf.Min(
                        candidate.Score + AiConfig.operationDirectiveBoost,
                        AiConfig.strategyExemptScore - 1f);

            // Recovery floor (2026-08-28 P0) — the LAST word on a candidate's Score, applied after
            // every common modifier above (strategic tilt, Management AP-budget penalty, commitment
            // layer, operations boost). A DrawCard flagged IsRecoveryDraw is the AI's only way out
            // of an empty-hand soft-lock (see AiManagementPlanner.GatherFallbackCandidates' own
            // invariant); those shared modifiers can otherwise sink it below AiConfig.passScore and
            // the AI then passes every remaining step this turn with cards still in the deck. Raises
            // it just above the Pass baseline — never lowers a draw that somehow scored higher, and
            // deliberately a small value so a genuine Defence/Scout emergency still outranks it.
            foreach (AiDecision candidate in candidates)
                if (candidate.IsRecoveryDraw && candidate.Score < AiConfig.recoveryDrawMinScore)
                    candidate.Score = AiConfig.recoveryDrawMinScore;

            AiDebugLog.Write($"[AI] {player.Nickname}: {candidates.Count} candidate(s) — {DescribeCandidates(candidates)}");

            // Unified arbiter (see Decide's own class comment). Strictly-greater Score wins outright;
            // on an EXACT Score tie the winner is decided by CompareTieBreak, a centralized
            // deterministic key — never by which planner tier happened to AddRange its candidate
            // first (2026-08-28 P0: with a sixth Level-1 category and the strategic clamp folding
            // more candidates onto the same value, that hidden dependency on planner call order
            // would otherwise decide an increasing share of steps). Decisions with different Scores
            // are completely unaffected.
            // Pass is a real baseline candidate, not just the empty-list fallback (2026-08-28, see
            // AiConfig.passScore) — the arbiter is seeded with it, and a routine candidate takes
            // the step only by scoring STRICTLY above passScore. An exact tie at the baseline stays
            // Pass: the CompareTieBreak branch is guarded on `best.Kind != Pass` so a candidate
            // scored exactly at the floor can't displace the seed. Before this, a Pass was only
            // ever synthesized when `candidates` was literally empty, so any candidate at all —
            // including ones driven well below zero by the strategic tilt / AP-budget penalty —
            // still beat the non-existent alternative.
            AiDecision best = AiDecision.None("placeholder — replaced below", AiConfig.passScore);
            foreach (AiDecision candidate in candidates)
                if (candidate.Score > best.Score
                    || (candidate.Score == best.Score && best.Kind != AiActionKind.Pass
                        && CompareTieBreak(candidate, best) < 0))
                    best = candidate;

            if (best.Kind == AiActionKind.Pass)
            {
                bool anyCardInHand = hand != null && hand.Hand.Count > 0;
                return AiDecision.None(
                    candidates.Count == 0
                        ? (anyCardInHand
                            ? "not enough AP for anything available"
                            : "nothing to do — armies busy, hand/AP has nothing to offer")
                        : "every available action scored at or below pass utility",
                    AiConfig.passScore);
            }

            Commit(player, best, pool, ctx.Map);
            return best;
        }

        // Centralized deterministic tie-break for Decide's own arbiter (2026-08-28 P0) — invoked
        // ONLY when two candidates carry a bit-for-bit identical Score, so it can never reorder
        // anything the Score itself already separates. Returns <0 when `a` should be preferred over
        // `b`. Every key component is derived purely from the candidate's own content (category,
        // action kind, the army/hex/card it names, finally its human-readable reason) and never
        // from list position, so the outcome is stable no matter what order the category planners
        // were queried in. Category is compared first so a tie between, say, a Defence and a
        // Management candidate resolves the same way every turn; the later components only matter
        // for two same-category candidates that also happen to share a Score.
        private static int CompareTieBreak(AiDecision a, AiDecision b)
        {
            int ca = a.Category.HasValue ? (int)a.Category.Value : int.MaxValue;
            int cb = b.Category.HasValue ? (int)b.Category.Value : int.MaxValue;
            if (ca != cb) return ca.CompareTo(cb);

            if (a.Kind != b.Kind) return ((int)a.Kind).CompareTo((int)b.Kind);

            int cmp = string.CompareOrdinal(a.ExistingArmy?.Name ?? "", b.ExistingArmy?.Name ?? "");
            if (cmp != 0) return cmp;

            if (a.TargetHex.Q != b.TargetHex.Q) return a.TargetHex.Q.CompareTo(b.TargetHex.Q);
            if (a.TargetHex.R != b.TargetHex.R) return a.TargetHex.R.CompareTo(b.TargetHex.R);

            cmp = string.CompareOrdinal(a.Card?.Definition?.displayName ?? "", b.Card?.Definition?.displayName ?? "");
            if (cmp != 0) return cmp;

            return string.CompareOrdinal(a.Reason ?? "", b.Reason ?? "");
        }

        // Deferred mutation for whichever candidate Decide's own arbiter just picked — every
        // OTHER candidate built this step (still-unregistered AiTask objects, PreemptedTask
        // references) is simply discarded here, never touching AiTaskRegistry/AiResourcePool at
        // all. Keeps MaxConcurrentVisitHex/MaxConcurrentRaid honest — generating N scored
        // candidates this step must never register more than the ONE that actually wins.
        private static void Commit(PlayerSetupData player, AiDecision decision, AiResourcePool pool, HexMap map)
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

            // BuildBase claiming a hex a DIFFERENT hero's own BuildFacility task already has —
            // see AiDecision.PreemptedHexTask's own comment. Same release-then-remove shape as
            // PreemptedTask right above, just a second independent slot (a BuildBase candidate
            // redirected off an in-progress Raid can carry both preemptions on one decision).
            if (decision.PreemptedHexTask != null)
            {
                AiDebugLog.Write($"[AI] {player.Nickname}: \"{decision.PreemptedHexTask.Army.Name}\" pulled off its "
                    + $"\"{decision.PreemptedHexTask.Kind}\" task — BuildBase claims "
                    + $"({decision.PreemptedHexTask.TargetHex.Q},{decision.PreemptedHexTask.TargetHex.R}) instead "
                    + "— the old task is marked unfinished.");
                if (decision.PreemptedHexTask.Kind == AiTaskKind.BuildFacility)
                    AiResourceReservation.Release(decision.PreemptedHexTask);
                AiTaskRegistry.Remove(player, decision.PreemptedHexTask);
            }

            if (decision.Task != null && !AiTaskRegistry.TasksFor(player).Contains(decision.Task))
            {
                AiTaskRegistry.Add(player, decision.Task);
                pool.ClaimArmy(decision.ExistingArmy);
                if (decision.Task.Kind == AiTaskKind.BuildFacility)
                {
                    AiDebugLog.Write($"[AI] {player.Nickname}: starts Economy task — \"{decision.ExistingArmy.Name}\" heads out "
                        + $"to build {decision.Task.ResourceType} at ({decision.Task.TargetHex.Q},{decision.Task.TargetHex.R})."
                        + HandDemandLogSuffix(player, decision.Task.ResourceType, pool.Root, pool.Hand));
                    // Moved here from AiEconomyPlanner.TryStartEconomyCandidates' own pre-pass
                    // (2026-08-24 follow-up fix, "новые диагностические строки снова будут
                    // спамить лог") — that pre-pass ran every step for every player regardless of
                    // whether a candidate actually started, so this used to print once per Decide
                    // CALL, not once per real Economy start. Logged only here, alongside the actual
                    // task-start line, so a log reader sees the income snapshot right next to the
                    // decision it explains instead of scattered across every unrelated step.
                    AiDebugLog.Write($"[AI] {player.Nickname}: Economy income — "
                        + $"Human={AiGoalScorer.IncomeFor(player, ResourceType.Human, map)}, "
                        + $"Energy={AiGoalScorer.IncomeFor(player, ResourceType.Energy, map)}, "
                        + $"Materials={AiGoalScorer.IncomeFor(player, ResourceType.Materials, map)}, "
                        + $"Tech={AiGoalScorer.IncomeFor(player, ResourceType.Tech, map)} "
                        + $"(mature={AiGoalScorer.HasMatureEconomy(player, AiConfig.economyMatureIncomePerType, map)}).");
                }
                else if (decision.Task.Kind == AiTaskKind.BuildBase)
                {
                    // Moved here from AiAggressionPlanner.TryStartBuildBaseCandidates (2026-08-24
                    // follow-up fix, project owner's own report: "новые диагностические строки
                    // снова будут спамить лог") — that method only ever GENERATES a candidate, most
                    // of which lose Decide's own arbitration and never actually start; logging
                    // there printed once per candidate BUILT, not once per task that actually
                    // started. This runs exactly once, only for the candidate that wins and gets
                    // registered here.
                    float strength = WorthIt.AttackSum(decision.ExistingArmy) + WorthIt.DefenseSum(decision.ExistingArmy);
                    string preemptedNote = decision.PreemptedTask != null
                        ? $", preempted its own \"{decision.PreemptedTask.Kind}\" task"
                          + (decision.Task.PreemptedRaidForBuildBase
                              ? " — fresh raid assembly now suppressed until this base is built (Задача 5)" : "")
                        : "";
                    AiDebugLog.Write($"[AI] {player.Nickname}: BuildBase actor selected — \"{decision.ExistingArmy.Name}\" "
                        + $"(strength={strength:0.#}, weakest eligible) heads to "
                        + $"({decision.Task.TargetHex.Q},{decision.Task.TargetHex.R}){preemptedNote}.");
                }
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
                    //
                    // Architectural boundary (2026-08-28 P1, spec item 14): these three
                    // Try*Candidate calls are the SOLE entry points for local, AP-free army
                    // reorganization (Collapse / Overflow-split / Consolidate / Swap), and they are
                    // reached from NOWHERE but this loop — never from Decide's own arbiter, never
                    // from a persistent task's continuation. AiTaskKind.ReturnForConsolidation is a
                    // plain travel task that only walks a stray army to a garrison hex; the actual
                    // folding-in happens right here, on a later turn, once it has arrived.
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
                // shell exactly like any other empty army. Releases the task's own resource
                // reservation FIRST (project owner's own follow-up review) — a stale BuildFacility/
                // BuildBase task can be sitting on an accumulated AiResourceReservation entry, and
                // simply dropping it from AiTaskRegistry without a matching Release leaves that
                // entry alive in AiResourceReservation's own ReservedByTask dictionary until the
                // next full Clear(): harmless to THIS turn's TotalReservedExcluding reads (a task
                // no longer in AiTaskRegistry is never iterated there), but a dead key violates the
                // "a finished task releases its reservation" contract every other completion path in
                // this codebase already follows.
                if (army.Members.Count == 0)
                {
                    AiTask staleTask = AiTaskRegistry.TaskFor(player, army);
                    if (staleTask != null)
                    {
                        AiResourceReservation.Release(staleTask);
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
                case AiActionKind.PlayFacilityCard:
                    yield return AiManagementPlanner.PlayFacilityCardRoutine(player, decision, ctx);
                    break;
                case AiActionKind.AttachEquipment:
                    yield return AiManagementPlanner.AttachEquipmentRoutine(player, decision, ctx);
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
                case AiActionKind.DispatchBaseReinforcement:
                    yield return AiOperations.DispatchBaseReinforcementRoutine(player, decision, ctx);
                    break;
                case AiActionKind.DepositReinforcement:
                    yield return AiOperations.DepositReinforcementRoutine(player, decision, ctx);
                    break;
                case AiActionKind.LaunchAirStrike:
                    yield return AiAviationSupport.LaunchRoutine(player, decision, ctx, AiTaskKind.AirStrike);
                    break;
                case AiActionKind.LaunchAirRecon:
                    yield return AiAviationSupport.LaunchRoutine(player, decision, ctx, AiTaskKind.AirRecon);
                    break;
                case AiActionKind.RunResearchProduction:
                    yield return AiDevelopmentPlanner.RunResearchProductionRoutine(player, decision, ctx);
                    break;
                case AiActionKind.ExecuteAirStrikeAtCurrentHex:
                    yield return AiAggressionPlanner.RepeatAirStrikeRoutine(player, decision, ctx);
                    break;
                case AiActionKind.Wait:
                    yield return WaitStep(ctx);
                    break;
            }
        }

        // Internal, not private — AiAviationSupport.LaunchRoutine also calls this directly to
        // execute a freshly launched sortie's first real step in the same, indivisible decision
        // (2026-08-26 P1 fix — see that method's own comment), not just from PerformDecision's
        // own dispatch switch below.
        // Would an army of `player`'s finishing its move on `hex` capture the Base / destroy
        // the facility there for free — i.e. there's a foreign-owned building and nobody of
        // that owner's still on the hex to defend it (the exact condition BuildingRegistry.
        // CaptureOrDestroyIfUndefended acts on). Used to decide whether a hidden AI scout
        // must drop stealth before this move (a hidden unit can't take anything).
        private static bool WouldTakeOverBuildingAt(HexCoord hex, PlayerSetupData player)
        {
            BuildingData building = BuildingRegistry.FindAt(hex);
            if (building == null || building.Owner == null || building.Owner == player)
                return false;
            foreach (ArmyData resident in ArmyRegistry.AllAt(hex))
                if (resident.Owner == building.Owner && BattleInitiator.IsEngageable(resident, player))
                    return false;
            return true;
        }

        internal static IEnumerator MoveArmyRoutine(PlayerSetupData player, AiDecision decision, AiTurnContext ctx,
            AiMoveExecutionTrace trace = null)
        {
            ArmyData army = decision.ExistingArmy;
            if (army?.Controller == null)
                yield break;

            AiDebugLog.Write($"[AI] {player.Nickname}: \"{army.Name}\" (movement={army.CurrentMovement}/{army.MaxMovement}) "
                + $"from ({army.Hex.Q},{army.Hex.R}) heads to ({decision.TargetHex.Q},{decision.TargetHex.R}) — {decision.Reason}.");

            yield return PanTo(ctx, army.Hex);
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
            // A hidden unit can't take a hex/base/facility (stealth design §5) — so if this
            // move ends on an enemy/neutral building nobody's left to defend, the AI must
            // drop stealth first, whatever the task, or the scout would just walk on and
            // capture nothing. (An undefended building only — a defended one is a fight the
            // solo scout stays hidden and out of.)
            bool wantsBuildingTakeover = WouldTakeOverBuildingAt(destination, player);

            // Safe-first stealth rule (stealth design §8): a solo reconnaissance army whose
            // sole member carries Stealth4 slips into stealth before it moves, provided it still
            // has 1 AP to spend and isn't already committed to a job a hidden unit can't finish
            // (raid/defence/capture — those tasks are never IsSoloRecce anyway, but the Kind check
            // keeps it explicit) and this specific move isn't a building takeover.
            //
            // Spec item 17 (2026-08-28 P1): NO LONGER unconditional. Stealth now costs its 1 AP
            // only when AiScoutStealthPolicy.MoveWarrantsStealth says this step carries a real
            // detection risk (a known enemy near the scout or its next hex) or heads into a
            // cooling scout-danger zone — a scout crossing known-safe ground stays visible and
            // keeps the AP for another discovery move. Entry is 1 AP per unit; voluntary exit is
            // free. The rule is the shared, layer-neutral primitive both V1 (here) and V2
            // (ProvisioningManager / TaskExecutor) call — see AiScoutStealthPolicy's own comment.
            if (!army.HasActivatedThisTurn && AiArmyRoles.IsSoloRecce(army) && !wantsBuildingTakeover
                && (decision.Task == null || decision.Task.Kind == AiTaskKind.VisitHex)
                && root != null
                && AiScoutStealthPolicy.MoveWarrantsStealth(player, army, destination))
            {
                UnitData scout = army.Members[0];
                if (Game.Map.StealthSystem.CanEnterStealth(scout))
                {
                    // Stealth entry costs 1 AP ON TOP of this move's own ActivationApCost (the army
                    // hasn't activated yet — see the guard above). Only slip into stealth if the
                    // turn can still afford BOTH; if it can afford the scouting move but not
                    // move + stealth, the scout still goes out, just visible (stealth design §8 —
                    // never skip the discovery move itself just to stay hidden).
                    if (root.CanSpendActionPoints(army.ActivationApCost + 1))
                    {
                        root.SpendActionPoints(1);
                        Game.Map.StealthSystem.EnterStealth(scout);
                        if (trace != null)
                            trace.EnteredStealthThisStep = true;
                        AiDebugLog.Write($"[AI] {player.Nickname}: \"{army.Name}\" enters stealth before scouting (-1 AP).");
                    }
                    else
                    {
                        AiDebugLog.Write($"[AI] {player.Nickname}: \"{army.Name}\" skips stealth — insufficient AP for activation + stealth.");
                    }
                }
            }
            // The mirror: this army is being moved for something a hidden unit can't finish
            // (a raid/capture/patrol move ending in contact, or an undefended building
            // takeover on arrival) — drop stealth on any hidden member now, immediately
            // before the action, never earlier (stealth design §8). Free.
            // Develop is excluded alongside VisitHex (P0, 2026-08-28): walking a Researcher/
            // Assembler toward a Lab/Factory is a plain repositioning march, not a contact action
            // — the reveal (Research only; never Production) happens at the Challenge itself, in
            // AiDevelopmentPlanner.RunResearchProductionRoutine (spec §12), not here.
            else if (wantsBuildingTakeover || (decision.Task != null
                && decision.Task.Kind != AiTaskKind.VisitHex && decision.Task.Kind != AiTaskKind.Develop))
            {
                foreach (UnitData member in army.Members.ToList())
                    if (member.IsHidden)
                    {
                        // §4 — the ONLY voluntary AI scout reveal path. Canonical ExitStealth,
                        // only immediately before a contact/takeover action, never during
                        // Explore/Develop repositioning. Tagged so a debug run can prove why a
                        // hidden scout became visible; ordinary movement emits no such line.
                        Game.Map.StealthSystem.ExitStealth(member);
                        string reason = wantsBuildingTakeover
                            ? "capture_or_destroy_building"
                            : $"pre_action_{decision.Task.Kind}";
                        AiDebugLog.Write($"[AI] {player.Nickname}: \"{army.Name}\" ScoutStealthExit "
                            + $"reason={reason} (immediately before the action)");
                    }
            }

            // Hex Event seam (V2 TaskExecutor only). IssueMoveOrder calls back the instant THIS
            // move claims its stop for a clean Hex Event — the one place that knows for certain
            // "this step walked into an event", ownership-scoped to this move and this hex. Not
            // recoverable afterwards: an AI mover's event resolves synchronously with no popup,
            // and a Skip fires no registry state change on a re-visit.
            HexCoord before = army.Hex;
            MoveOrderResult moveResult = ctx.HexSelection != null
                ? ctx.HexSelection.IssueMoveOrder(army.Controller, destination,
                    trace != null ? new System.Action<HexCoord>(_ => trace.HexEventOccurred = true) : null)
                : MoveOrderResult.CannotMove;
            if (trace != null)
                trace.MoveResult = moveResult;

            // The launch-Energy reservation AiAviationSupport.LaunchRoutine placed on this task
            // (2026-08-26 P1 fix) only ever needs to survive until this exact moment — IssueMoveOrder
            // just spent the REAL ActivationEnergyCost from root (or found it already spent, army
            // already activated earlier this turn), either way there's nothing left for the
            // reservation to protect. Release is idempotent (a no-op if nothing was ever reserved,
            // e.g. every ordinary ground-army move, or a continuation step after the first), so this
            // never needs a success/failure check on moveResult itself.
            if (decision.Task != null && (decision.Task.Kind == AiTaskKind.AirStrike || decision.Task.Kind == AiTaskKind.AirRecon))
                AiResourceReservation.Release(decision.Task);

            if (army.Controller != null)
                yield return new WaitUntil(() => !army.Controller.IsMoving);

            // Physical arrival — captured NOW, before any battle below can destroy the mover, so a
            // caller can still tell where the scout got to (and count the step) even if it dies.
            if (trace != null)
            {
                trace.EndHex = army.Hex;
                trace.ReachedDestination = army.Hex.Equals(destination);
            }

            // Post-move safety net (2026-08-24 P0 fix, project owner's own report — a real
            // sighting on (6,3): a contact left unresolved by onComplete's own TryBeginBattleAt
            // call — most commonly DelayedBattleRegistry.IsHexPending being true for an unrelated
            // pairing already reserved at this same hex — used to just sit there "coexisting"
            // with nothing showing on screen, so the WaitUntil right below saw !IsBattleActive
            // immediately and let this army's turn step count as done. The pair then stayed
            // unresolved (both LockedInCombat, per HexSelectionController's own gate) for however
            // many player-turns until GameTurnController's end-of-ROUND sweep finally forced it.
            // Re-asserting TryBeginBattleAt here, now that the reservation that blocked it a
            // moment ago may have already cleared this same pass, closes that gap without waiting
            // for the round boundary. A no-op (Pending/NoContact/MoverCannotFight) whenever
            // onComplete already fully handled it, which is still the overwhelmingly common case.
            // Capture "did a fight actually happen" HERE, while it is still observable — before the
            // WaitUntil(!IsBattleActive) below spins until every trace of it is gone. For an AI
            // mover a contact-triggered fight is already open by this point (onComplete ran
            // synchronously before IsMoving flipped false), and hex events resolve synchronously
            // with no popup, so this is the last moment a caller can learn a contact occurred.
            bool battleObserved = ctx.HexSelection != null && ctx.HexSelection.IsBattleActive;
            if (ctx.HexSelection != null && army.Controller != null && !ctx.HexSelection.IsBattleActive)
            {
                BattleStartResult safetyResult = ctx.HexSelection.TryBeginBattleAt(army.Hex, army);
                if (safetyResult == BattleStartResult.Started)
                {
                    battleObserved = true;
                    AiDebugLog.Write($"[AI] {player.Nickname}: \"{army.Name}\" had unresolved contact at "
                        + $"({army.Hex.Q}, {army.Hex.R}) after moving — battle started by the post-move safety check.");
                }
            }
            if (trace != null)
                trace.BattleOccurred = battleObserved;

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

            // AiTask.VisitLastProgressTurn's own stall watchdog (2026-08-24) — a real hex change is
            // "progress" whether it came from routine scouting or a flee step; only actually moving
            // resets the clock, a no-op order (moveResult != success, army.Hex == before) never does.
            if (!army.Hex.Equals(before) && decision.Task != null && decision.Task.Kind == AiTaskKind.VisitHex)
                decision.Task.VisitLastProgressTurn = ctx.TurnNumber;

            // §5 — record the actual step for a solo scout so V2 route ranking can prefer fresh
            // ground over re-treading this trail. Bounded; never blocks a hex.
            if (!army.Hex.Equals(before) && AiArmyRoles.IsSoloRecce(army))
                Game.Ai.V2.ScoutTrailRegistry.RecordStep(player, army.Id, before, army.Hex);

            yield return WaitStep(ctx);
        }

        private static IEnumerator PlayCardRoutine(PlayerSetupData player, AiDecision decision, AiTurnContext ctx)
        {
            PlayerRoot root = PlayerRootRegistry.FindFor(player);
            if (root == null)
                yield break;

            // Aviation branch — same reason CardHandUI.TryDeployUnitOrHero's own human drag-drop
            // never lets an aviation card reach the ordinary garrison-deposit path below (see that
            // method's own comment): AviationActions.TryDeployFromCard finds/creates the airfield
            // container itself (EnsureAirfield), so there's no "existing army or spawn a fresh one"
            // choice to make here the way every other card role has. decision.TargetHex carries
            // AiManagementPlanner.FindAviationPlacement's own chosen airfield hex (see AiDecision.
            // PlayCard's own comment on why ExistingArmy stays null for this role).
            if (decision.Card.Definition.isAviation)
            {
                yield return PanTo(ctx, decision.TargetHex);
                int apAv0 = root.ActionPoints;
                int humanAv0 = root.GetResource(ResourceType.Human);
                int energyAv0 = root.GetResource(ResourceType.Energy);
                int materialsAv0 = root.GetResource(ResourceType.Materials);
                int techAv0 = root.GetResource(ResourceType.Tech);

                // sourceCard: the hand instance — a Research/Production-created card then pays
                // activationApCost and skips its already-paid ResourceCost (spec §5); an ordinary
                // card is unaffected.
                bool aviationDeployed = AviationActions.TryDeployFromCard(decision.Card.Definition, player, root, ctx.HexSelection,
                    decision.TargetHex, out string aviationFailReason, decision.Card.Equipment, decision.Card);
                if (aviationDeployed)
                {
                    AiHandData aviationHand = AiHandRegistry.GetOrCreate(player, ctx.StartingDeckCatalog, ctx.StartingHandSize);
                    aviationHand?.Hand.Remove(decision.Card);
                    string aviationDelta = ResourceDeltaSuffix(root, apAv0, humanAv0, energyAv0, materialsAv0, techAv0);
                    AiDebugLog.Write($"[AI] {player.Nickname}: {decision.Card.Definition.displayName} stored at the airfield "
                        + $"({decision.TargetHex.Q},{decision.TargetHex.R}) — {decision.Reason}.{aviationDelta}");
                }
                else
                {
                    ctx.FailedPlayCardsThisTurn.Add(decision.Card);
                    AiDebugLog.Write($"[AI] {player.Nickname}: couldn't store {decision.Card.Definition.displayName} — {aviationFailReason}");
                }
                yield return WaitStep(ctx);
                yield break;
            }

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

            yield return WaitStep(ctx);

            // sourceCard: the hand instance — a Research/Production-created card then pays
            // activationApCost and skips its already-paid ResourceCost (spec §5); an ordinary
            // card behaves exactly as before.
            bool deployed = ArmyActions.DeployUnitFromCard(decision.Card.Definition, player, targetArmy, root, ctx.HexSelection,
                out string failReason, decision.Card.Equipment, decision.Card);
            if (deployed)
            {
                AiHandData hand = AiHandRegistry.GetOrCreate(player, ctx.StartingDeckCatalog, ctx.StartingHandSize);
                hand?.Hand.Remove(decision.Card);
                // See AiManagementPlanner.NotifyCardRolePlayed's own comment — only flips the
                // Hero/Unit alternation state once the card has actually deployed, not on a
                // merely-proposed candidate.
                AiManagementPlanner.NotifyCardRolePlayed(player, AiManagementPlanner.RoleOf(decision.Card));
                string delta = ResourceDeltaSuffix(root, ap0, human0, energy0, materials0, tech0);
                AiDebugLog.Write($"[AI] {player.Nickname}: {decision.Card.Definition.displayName} joins \"{targetArmy.Name}\" "
                    + $"at ({targetArmy.Hex.Q},{targetArmy.Hex.R}) — {decision.Reason}.{delta}");
            }
            else
            {
                ctx.FailedPlayCardsThisTurn.Add(decision.Card);
                AiDebugLog.Write($"[AI] {player.Nickname}: couldn't deploy {decision.Card.Definition.displayName} — {failReason}");
            }

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
            // An air army's real per-hex charge is always 1, regardless of terrain (see
            // AviationRules.MovementCost) — routing it through the ground-weighted Dijkstra search
            // can hand back a longer detour around expensive terrain than the true shortest air
            // route, and wrongly reject a step that's actually affordable (2026-08-26 fix, project
            // owner's own report). flatCost makes the search itself rank routes the same way an
            // aircraft actually pays for them.
            bool isAirArmy = AviationRules.IsAirArmy(army);
            HexPath path = HexPathfinder.FindPath(map, army.Hex, destination, blockHex: blockHex, flatCost: isAirArmy);
            if (path == null || path.Hexes.Count < 2)
                return null;
            HexCoord step = path.Hexes[1];
            map.TryGetTerrainAt(step, out TerrainTypeEntry entry);
            int terrainCost = entry != null ? entry.moveCost : 1;
            int cost = AviationRules.MovementCost(army, terrainCost);
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
        // together the three things IssueMoveOrder itself independently enforces before it ever
        // accepts an order: FindAffordableStep's own movement-point/path check for the FIRST step
        // only (never the whole route — a multi-turn journey is fine, a step this Decide() call
        // can't even begin is not), plus the one-time ActivationApCost an army not yet activated
        // this turn also has to afford out of the shared AP pool, plus (2026-08-26 fix, project
        // owner's own report) that same activation's ActivationEnergyCost — zero for every ground
        // army, but real for an air army about to launch, and IssueMoveOrder rejects the order for
        // it same as a missing AP would. Read through AiResourceReservation.Available (never
        // root.GetResource directly), the same reservation-aware figure CanAffordLaunch already
        // uses, so this can't double-spend Energy another active task already claimed. Every
        // category's candidate sites now route through this one helper instead of each growing its
        // own copy of either check — since all three conditions are things execution already
        // independently requires, a candidate this rejects would always have failed at
        // IssueMoveOrder anyway, so gating it here only ever removes a doomed candidate from
        // arbitration, never a viable one.
        // `reservationOwner` — the AirStrike/AirRecon task this exact army/move belongs to, if
        // any (2026-08-26 P1 fix, project owner's own report). AiAviationSupport.LaunchRoutine
        // reserves this army's own ActivationEnergyCost the instant its task is created (so no
        // OTHER task can spend it out from under a not-yet-activated sortie), but that same
        // reservation used to count against THIS check too — at Energy exactly equal to the
        // activation cost, Available() came back 0 (root's Energy minus the task's own claim on
        // it) and the army could never take its first step at all, despite the Energy genuinely
        // being there. Passing the task here excludes only ITS OWN reservation from the read,
        // never anyone else's — a rival task's claim still counts exactly as before.
        internal static bool CanIssueMoveNow(PlayerRoot root, PlayerSetupData player, ArmyData army, HexMap map, HexCoord destination,
            AiTask reservationOwner = null) =>
            root != null && army != null && FindAffordableStep(map, army, destination).HasValue
                && (army.HasActivatedThisTurn || (root.CanSpendActionPoints(army.ActivationApCost)
                    && AiResourceReservation.Available(root, player, ResourceType.Energy, reservationOwner) >= army.ActivationEnergyCost));

        // Problem 4 (2026-08-24, project owner's own report): a raw armies=X→Y count in the
        // turn-ends line can't tell fragmentation (a growing pile of small leftover armies) apart
        // from healthy growth (a few well-formed ones) — Halden went 4→7→10 armies over two turns
        // with no way to see WHAT those were. Diagnostic-only instrumentation: read-only over
        // whatever RunTurn already produced this turn, never touches scoring or creation limits.
        //
        // 2026-08-26 split (project owner's own report): the original single breakdown only
        // excluded IsGarrison/IsPrison, so an ordinary ground army, a formed air army, a taskless
        // returned aircraft, an active-sortie aircraft, and an airfield's own storage container all
        // landed in the SAME armies/units/avgUnitsPerArmy count — a taskless single-aircraft air
        // army read as a "solo orphaned" ground army, and an empty airfield container could read as
        // a "reserved empty army". Now two independent blocks, each over its own filtered roster, so
        // the ground block alone is what fragmentation analysis should read.
        private static string BuildArmyBreakdownLog(PlayerSetupData player, int turnNumber)
        {
            return $"{BuildGroundArmyBreakdown(player, turnNumber)}\n\n{BuildAviationBreakdown(player, turnNumber)}";
        }

        // Part 1 — ground field armies only: not Garrison/Prison (as before) and now also not an
        // airfield container or a formed air army (AviationRules.IsAirfield/IsAirArmy — the same
        // two predicates every AiArmyRoles ground-role check already excludes on). This is the only
        // roster fragmentation analysis should read.
        private static string BuildGroundArmyBreakdown(PlayerSetupData player, int turnNumber)
        {
            List<ArmyData> armies = ArmyRegistry.AllForOwner(player)
                .Where(a => !a.IsGarrison && !a.IsPrison && !AviationRules.IsAirfield(a) && !AviationRules.IsAirArmy(a))
                .ToList();
            int totalUnits = armies.Sum(a => a.Members.Count);

            // Task-status split — independent of roster size, sums to armies.Count: "active" (an
            // AiTask other than ReturnForConsolidation owns it), "returning" (ReturnForConsolidation
            // — see Feature 4B's own comment on AiTaskKind.ReturnForConsolidation), "idle" (no task
            // at all).
            int returning = armies.Count(a => AiTaskRegistry.TaskFor(player, a)?.Kind == AiTaskKind.ReturnForConsolidation);
            int active = armies.Count(a => AiTaskRegistry.TaskFor(player, a) != null) - returning;
            int idle = armies.Count - active - returning;

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
            // 0 members: "spare" — within GatherFallbackCandidates' own maxSpareArmies buffer,
            // task-less (the deliberately kept reserve, see IsDisposableEmptyArmy's own comment);
            // "reusable" — task-less and beyond that buffer (IsDisposableEmptyArmy true — exactly
            // what RunEmptyArmyCleanup just tried to hand out for reuse or dispose of); "orphaned" —
            // everything else at 0 members (e.g. still task-claimed but its roster emptied out from
            // under it — IsDisposableEmptyArmy deliberately leaves a task-owned shell alone).
            int emptyReusable = armies.Count(a => GarrisonReorgTask.IsDisposableEmptyArmy(player, a));
            int emptySpare = armies.Count(a => a.Members.Count == 0 && AiTaskRegistry.TaskFor(player, a) == null)
                - emptyReusable;
            int emptyOrphaned = emptyArmies - emptySpare - emptyReusable;

            // 1 member: "tasked" — an active AiTask OTHER than ReturnForConsolidation owns it (a
            // courier, a raid/defence recruit still solo, a scout mid-VisitHex, ...); "recce" —
            // task-less and AiArmyRoles.IsSoloRecce (left alone by design, see
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

            return $"[AI] {player.Nickname} ground breakdown (turn {turnNumber}):\n"
                + $"armies={armies.Count}, units={totalUnits}\n"
                + $"active={active}, idle={idle}, returning={returning}\n"
                + $"empty: spare={emptySpare}, reusable={emptyReusable}, orphaned={emptyOrphaned}\n"
                + $"solo: tasked={soloTasked}, recce={soloRecce}, returning={soloReturning}, orphaned={soloOrphaned}\n"
                + $"2+={multiArmies}";
        }

        // Part 2 — aviation armies plus (part 3) their airfield infrastructure, folded into the
        // same block since both read off the same OwnedAirfieldHexes set. Formed air armies
        // (AviationRules.IsAirArmy) are split by what currently owns each one: an active AirStrike/
        // AirRecon AiTask, or task-less. A task-less air army sitting on one of this player's own
        // airfield hexes (AviationRules.IsOwnedAirfieldAt) is "landed ready" — a normal, complete
        // sortie waiting for its next task, never "orphaned" (see this method's own class comment on
        // AiAviationSupport for why a landed group is a deliberate, reusable state, not a leak).
        // A task-less air army anywhere else is the genuinely suspicious case — airborne with no
        // task driving it, i.e. "stranded" — see AiAviationSupport.ContinueSortie's own "holds
        // position" fallback for the one legitimate way this happens (no safe route home this
        // step), which should clear itself the very next turn once a route opens back up.
        private static string BuildAviationBreakdown(PlayerSetupData player, int turnNumber)
        {
            List<ArmyData> airArmies = ArmyRegistry.AllForOwner(player).Where(a => AviationRules.IsAirArmy(a)).ToList();
            int aircraft = airArmies.Sum(a => a.Members.Count);
            int activeStrike = airArmies.Count(a => AiTaskRegistry.TaskFor(player, a)?.Kind == AiTaskKind.AirStrike);
            int activeRecon = airArmies.Count(a => AiTaskRegistry.TaskFor(player, a)?.Kind == AiTaskKind.AirRecon);
            int tasklessLanded = airArmies.Count(a => AiTaskRegistry.TaskFor(player, a) == null
                && AviationRules.IsOwnedAirfieldAt(a.Hex, player));
            int tasklessAirborne = airArmies.Count(a => AiTaskRegistry.TaskFor(player, a) == null) - tasklessLanded;

            // Infrastructure (part 3) — airfield containers (AviationRules.IsAirfield) are never a
            // ground/air army in their own right, only a stored-aircraft box; "slots" reads the same
            // capacity/occupancy AviationRules.FreeAirfieldCapacity already enforces at deploy time,
            // never a second, independently-drifting count.
            int storedAircraft = ArmyRegistry.AllForOwner(player).Where(a => AviationRules.IsAirfield(a)).Sum(a => a.Members.Count);
            List<HexCoord> airfieldHexes = AiAviationSupport.OwnedAirfieldHexes(player).ToList();
            int totalSlots = airfieldHexes.Sum(hex => AviationRules.AirfieldCapacityAt(hex, player));

            return $"[AI] {player.Nickname} aviation breakdown (turn {turnNumber}):\n"
                + $"air armies={airArmies.Count}, aircraft={aircraft}\n"
                + $"active strike={activeStrike}, active recon={activeRecon}\n"
                + $"landed ready={tasklessLanded}, airborne stranded={tasklessAirborne}\n"
                + $"stored aircraft={storedAircraft}\n"
                + $"airfields={airfieldHexes.Count}, slots={storedAircraft}/{totalSlots} used";
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
            yield return new WaitForSeconds(ctx.StepDelay);
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
            // VisitHex gets its own extra suffix (2026-08-24, project owner's own ask) — army's
            // CURRENT hex, FledOnTurn, and Reason, so two scouts sharing the same TargetHex (e.g.
            // both fleeing to the same nearby garrison — a legitimate shared retreat point, see
            // VisitHexTask.TryFlee's own comment, not a deconfliction bug) read as visibly distinct
            // in the log instead of looking like an unexplained duplicate.
            string list = string.Join("; ", tasks.Select(t => t.Kind == AiTaskKind.VisitHex
                ? $"{t.Kind}({t.Category}):\"{t.Army?.Name ?? "?"}\"@({t.Army?.Hex.Q},{t.Army?.Hex.R})→({t.TargetHex.Q},{t.TargetHex.R})"
                    + $" fled={t.FledOnTurn} reason=\"{t.Reason}\""
                : $"{t.Kind}({t.Category}):\"{t.Army?.Name ?? "?"}\"→({t.TargetHex.Q},{t.TargetHex.R})"));
            AiDebugLog.Write($"[AI] {player.Nickname}: active tasks ({tasks.Count}) — {list}.");
        }
    }
}
