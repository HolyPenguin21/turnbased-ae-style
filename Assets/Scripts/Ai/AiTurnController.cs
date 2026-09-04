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

            // ---- Global Map AI = Strategy V2, unconditionally (ARCH-01, 2026-09-04) ----
            // The former V1/V2 fork is gone: V2 owns every AI turn end to end. Placed after
            // AiMapMemory.OnTurnStarted + hand creation so V2 inherits the freshly-expired memory
            // and the capacity-capped hand. The legacy V1 turn body that used to follow this point
            // is deleted in ARCH-01 01D; nothing routes to it any more.
            yield return Game.Ai.V2.Pipeline.RunTurn(player, root, hand, ctx);
            onDone?.Invoke();
            yield break;

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
    }
}
