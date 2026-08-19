using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai
{
    // Level-1 category for Разведка (AiTaskCategory.Reconnaissance) — holds this category's own
    // shared primitives (ScoutTarget/BuriedRecceUnit, also reused by AiAggressionPlanner's own
    // raid-recall candidates) AND the continue/start/return-home orchestration
    // AiTurnController.Decide gathers candidates from each step, same role AiEconomyPlanner/
    // AiManagementPlanner/AiAggressionPlanner play for their own categories, plus this category's
    // own execution routines (SpawnReconArmyRoutine/AssembleRecceScoutRoutine — see
    // AiTurnController's own class comment on the execution split). Task-specific behavior
    // (composition eligibility, concrete target-finding) lives one level down, in VisitHexTask —
    // this class only sequences calls into it and turns the results into AiDecision/AiTask.
    //
    // Разведка used to also run a second task (ScoutResourceHexTask, Задача 2 — long-range,
    // non-stepping resource-hex hunting) — removed 2026, the project owner's own call that
    // VisitHexTask's citadel-wave coverage already discovers resource hexes as a side effect of
    // exploring, so a dedicated task chasing the same information was redundant.
    public static class AiScoutPlanner
    {
        public readonly struct ScoutTarget
        {
            public readonly HexCoord Hex;
            public readonly float Score;
            public readonly string Reason;

            public ScoutTarget(HexCoord hex, float score, string reason)
            {
                Hex = hex;
                Score = score;
                Reason = reason;
            }
        }

        public readonly struct BuriedRecceUnit
        {
            public readonly ArmyData Source;
            public readonly UnitData Unit;

            public BuriedRecceUnit(ArmyData source, UnitData unit)
            {
                Source = source;
                Unit = unit;
            }
        }

        // Разведка's own assembly step (AI architecture doc, section 02 · 2.1) — a Recce-tagged
        // unit/hero already deployed but buried inside a bigger, non-scout army instead of
        // standing solo. Rare in practice: every other planner keeps a Recce member solo forever
        // on purpose (see GarrisonReorgTask.FindReorgMove's own IsLoneArmyAtBase
        // exclusion) — this only ever fires after something outside the AI's own logic put one
        // there (a human drag-drop, say). Only ever proposes a member sitting on the SAME hex as
        // `emptyArmyHex` — no cross-hex army-merge primitive exists in this codebase (ArmyActions.
        // TransferMember is same-hex only, like every other reorg move here), so a buried member
        // elsewhere is simply never offered rather than scored down (see AiTurnController's own
        // TryStartReconAssemblyCandidatesFor comment).
        public static BuriedRecceUnit? FindBuriedRecceUnit(PlayerSetupData player, bool wantHero, HexCoord emptyArmyHex)
        {
            foreach (ArmyData source in ArmyRegistry.AllForOwner(player))
            {
                if (source.IsPrison || !source.Hex.Equals(emptyArmyHex))
                    continue;
                UnitData unit = source.Members.FirstOrDefault(m => m.HasAbility(UnitAbilities.Recce) && m.IsHero == wantHero);
                if (unit == null || (!source.IsGarrison && source.Members.Count == 1))
                    continue; // already solo — already IsScoutCapable, nothing to assemble
                return new BuriedRecceUnit(source, unit);
            }
            return null;
        }

        // Задача 1's own MoveArmy weight — reconBaseWeight tapering off past
        // AiConfig.reconPriorityDecayStartTurn (see that field's own comment). Only ever called at
        // the point reconBaseWeight would otherwise be added to a target's own Score — the two
        // MoveArmy sites below (TryContinueVisitTask/TryStartVisitCandidates).
        // SpawnReconArmy/AssembleRecceScout/RequestReconArmy keep reading
        // AiConfig.Current.reconBaseWeight directly, untouched by this taper.
        private static float ReconMoveWeight(AiTurnContext ctx)
        {
            AiConfig config = AiConfig.Current;
            int turnsPast = ctx.TurnNumber - config.reconPriorityDecayStartTurn;
            if (turnsPast <= 0)
                return config.reconBaseWeight;
            float decayed = config.reconBaseWeight - turnsPast * config.reconPriorityDecayPerTurn;
            return Math.Max(decayed, config.reconPriorityDecayFloor);
        }

        // ---- Разведка · Задача 1 (Посещение хекса) ----

        public static AiDecision TryContinueVisitTask(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx, AiTask task)
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
            ScoutTarget? target = VisitHexTask.TryFlee(player, task.Army, task)
                ?? VisitHexTask.FindTarget(player, task.Army, ctx.Map);
            if (!target.HasValue)
            {
                AiTaskRegistry.Remove(player, task);
                return null;
            }
            task.TargetHex = target.Value.Hex;
            return AiDecision.Move(task.Army, target.Value, task, ReconMoveWeight(ctx) + target.Value.Score);
        }

        // Composition/target logic both live on VisitHexTask now (IsEligibleComposition/
        // FindTarget) — this orchestrator step just gathers eligible armies and turns the
        // class's own proposal into an AiDecision/AiTask.
        public static List<AiDecision> TryStartVisitCandidates(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            AiResourcePool pool, HashSet<ArmyData> stuckScouts)
        {
            var results = new List<AiDecision>();
            if (AiTaskRegistry.CountActive(player, AiTaskKind.VisitHex) >= AiConfig.Current.maxConcurrentVisitHex)
                return results;

            foreach (ArmyData army in pool.AvailableArmies())
            {
                if (!VisitHexTask.IsEligibleComposition(army) || army.CurrentMovement <= 0 || army.Controller == null || stuckScouts.Contains(army))
                    continue;
                if (!army.HasActivatedThisTurn && !root.CanSpendActionPoints(army.ActivationApCost))
                    continue;

                // No task exists yet to carry FledLastTurn — always free to flee on first
                // encounter, then stamp the flag onto the freshly created task below so the very
                // next continuation call (see TryContinueVisitTask) already honours the one-turn
                // cap.
                ScoutTarget? fleeTarget = VisitHexTask.TryFlee(player, army, null);
                ScoutTarget? target = fleeTarget ?? VisitHexTask.FindTarget(player, army, ctx.Map);
                if (!target.HasValue)
                    continue;

                var task = new AiTask
                {
                    Kind = AiTaskKind.VisitHex, Army = army, TargetHex = target.Value.Hex, Reason = target.Value.Reason,
                    FledLastTurn = fleeTarget.HasValue,
                };
                results.Add(AiDecision.Move(army, target.Value, task, ReconMoveWeight(ctx) + target.Value.Score));
            }
            return results;
        }

        // AiArmyRoles.IsSoloHeroAwaitingEscort's own other half: once it has no active Задача 1
        // task and nothing left nearby to visit, walk it back to the garrison instead of leaving
        // it wherever it happened to stop — that's where AiManagementPlanner.TryPlayCardCandidates'
        // own Unit-role routing will actually find it the next time an escort card is affordable.
        // A no-op once it's already there.
        public static List<AiDecision> TryReturnHomeCandidates(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            HashSet<ArmyData> stuckScouts)
        {
            var results = new List<AiDecision>();
            HexCoord garrisonHex = AiTurnController.GarrisonHexFor(player);
            foreach (ArmyData army in ArmyRegistry.AllForOwner(player))
            {
                if (!AiArmyRoles.IsSoloHeroAwaitingEscort(army) || army.CurrentMovement <= 0 || army.Controller == null
                    || stuckScouts.Contains(army) || army.Hex.Equals(garrisonHex) || AiTaskRegistry.TaskFor(player, army) != null)
                    continue;
                if (!army.HasActivatedThisTurn && !root.CanSpendActionPoints(army.ActivationApCost))
                    continue;
                var target = new ScoutTarget(garrisonHex, 0f,
                    "рядом нечего посетить — возвращается в цитадель дожидаться подкрепления");
                results.Add(AiDecision.Move(army, target, null, AiConfig.Current.managementReturnHomeScore));
            }
            return results;
        }

        // ---- Разведка · сборка Recce-состава (юнит-Recce / герой-Recce) ----
        // The project owner's own call: card placement for a Recce composition shouldn't be a
        // Менеджмент leftover any more (AiManagementPlanner.FindPlacement/TryPlayCardCandidates
        // still exist and still propose the SAME PlayCard action for the same card at
        // managementBaseWeight+playRecceCardBonus — this is a competing, Разведка-flavored
        // proposal for it, not a replacement; only the higher-scoring one ever actually commits).
        // One candidate per step per template (Unit-Recce, Hero-Recce), whichever prerequisite is
        // still missing:
        //   1) no empty deployable army anywhere → RequestReconArmy (score BELOW a real recon
        //      move, but generally ABOVE Менеджмент's own flat Reserve fallback).
        //   2) an empty army exists, and a Recce-tagged unit/hero of this shape is already
        //      deployed but buried inside a bigger army on the SAME hex as that empty army →
        //      AssembleRecceScout (see FindBuriedRecceUnit — rare in practice, every other
        //      planner keeps Recce solo forever). A buried member on a DIFFERENT hex is never
        //      offered — no cross-hex army-merge primitive exists — so that state simply
        //      contributes no candidate this step, same as "no card in hand" below.
        //   3) an empty army exists, nothing buried to assemble, and hand holds a matching
        //      Recce-tagged card → PlayCard (same action AiManagementPlanner.FindPlacement would
        //      have proposed anyway, just scored on Разведка's own scale).
        // Once assembled (AiArmyRoles.IsScoutCapable true), TryStartVisitCandidates already claims
        // the army directly — no further step here.
        public static List<AiDecision> TryStartReconAssemblyCandidates(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            AiHandData hand, AiResourcePool pool)
        {
            var results = new List<AiDecision>();
            results.AddRange(TryStartReconAssemblyCandidatesFor(player, root, ctx, hand, pool, wantHero: false));
            results.AddRange(TryStartReconAssemblyCandidatesFor(player, root, ctx, hand, pool, wantHero: true));
            // Both shapes independently fall back to "no empty army anywhere → request one" when
            // neither has its own idle composition yet — same score, same reason, since
            // SpawnReconArmyRoutine creates one shape-agnostic empty army either can claim next
            // step. Collapse to a single candidate; a second identical one only inflates Decide's
            // own candidate count without offering the arbiter a real alternative.
            bool seenSpawnRequest = false;
            results.RemoveAll(r =>
            {
                if (r.Kind != AiActionKind.SpawnReconArmy)
                    return false;
                if (seenSpawnRequest)
                    return true;
                seenSpawnRequest = true;
                return false;
            });
            return results;
        }

        private static List<AiDecision> TryStartReconAssemblyCandidatesFor(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            AiHandData hand, AiResourcePool pool, bool wantHero)
        {
            var results = new List<AiDecision>();

            // Every Разведка task slot already spoken for — no point assembling a scout with
            // nowhere for it to actually work this turn (or any turn soon).
            if (AiTaskRegistry.CountActive(player, AiTaskKind.VisitHex) >= AiConfig.Current.maxConcurrentVisitHex)
                return results;

            // Already have an idle, ready-to-go composition of this exact shape?
            // TryStartVisitCandidates already claims it directly next — nothing to assemble.
            if (pool.AvailableArmies().Any(a => AiArmyRoles.IsScoutCapable(a) && a.Members.Any(m => m.IsHero == wantHero)))
                return results;

            ArmyData emptyArmy = pool.AvailableArmies().FirstOrDefault(AiArmyRoles.IsEmptyDeployableArmy);
            if (emptyArmy == null)
            {
                // Trigger-gated, not speculative: only request a new empty army if hand actually
                // HOLDS a matching Recce card right now (project owner's own call — every task's
                // "start" candidate should read off actually-observed state: map/hand/resources,
                // never "might show up later"). Without this, RequestReconArmy used to fire purely
                // off "no empty army exists" regardless of hand content, outbidding
                // AiManagementPlanner's own spare-army fallback (which reaches the exact same empty
                // army as a side effect, just without a speculative purpose attached) even when no
                // Recce card was anywhere in sight.
                if (FindMatchingRecceCard(hand, wantHero) != null && root.CanSpendActionPoints(ArmyActions.CreateArmyApCost))
                    results.Add(AiDecision.RequestReconArmy(AiConfig.Current.reconBaseWeight + AiConfig.Current.reconRequestArmyPenalty));
                return results;
            }

            BuriedRecceUnit? buried = FindBuriedRecceUnit(player, wantHero, emptyArmy.Hex);
            if (buried.HasValue && !ctx.WouldRevisitArmy(buried.Value.Unit, emptyArmy))
            {
                results.Add(AiDecision.AssembleRecceScout(buried.Value, emptyArmy, AiConfig.Current.reconBaseWeight + AiConfig.Current.reconAssembleBonus));
                return results;
            }

            CardData card = FindMatchingRecceCard(hand, wantHero);
            if (card == null)
                return results; // no candidate this step — Разведка simply isn't competitive right now

            AiManagementPlanner.CardPlacement? placement = AiManagementPlanner.FindPlacement(player, root, card, AiManagementPlanner.CardRole.Recce);
            if (placement.HasValue)
                results.Add(AiDecision.PlayCard(placement.Value.ExistingArmy, card, AiManagementPlanner.CardRole.Recce,
                    AiConfig.Current.reconBaseWeight + AiConfig.Current.reconRequestCardPenalty));
            return results;
        }

        // Shared by both the pre-emptive "request an empty army" gate and the later "play the
        // card" step below — same match rule (Recce-tagged, hero/unit shape matching `wantHero`),
        // so the two can never disagree on what counts as a usable Recce card.
        private static CardData FindMatchingRecceCard(AiHandData hand, bool wantHero) =>
            hand?.Hand.FirstOrDefault(c => AiManagementPlanner.IsRecceCard(c)
                && c.Definition.cardType == (wantHero ? CardType.Hero : CardType.Unit));

        // ---- Execution ----

        // Разведка · сборка Recce-состава, шаг 1's own execution — plain ArmyActions.CreateArmy,
        // same primitive AiManagementPlanner.ReserveArmyRoutine uses, just without touching that
        // class's own Reserve/Draw alternation (see AiDecision.RequestReconArmy's own comment on
        // why).
        public static IEnumerator SpawnReconArmyRoutine(PlayerSetupData player, AiTurnContext ctx)
        {
            HexCoord hex = AiTurnController.GarrisonHexFor(player);
            yield return AiTurnController.PanTo(ctx, hex);

            ArmyData army = ArmyActions.CreateArmy(player, hex, ctx.StartingDeckCatalog?.GetCatalog(player.Faction), ctx.HexSelection);
            AiDebugLog.Write(army != null
                ? $"[AI] {player.Nickname}: задача «Разведка» — создаёт пустую армию {army.Name} под Recce-состав."
                : $"[AI] {player.Nickname}: задача «Разведка» — не хватило AP на новую армию под Recce-состав.");

            yield return AiTurnController.WaitStep(ctx);
        }

        // Разведка · сборка Recce-состава, шаг 2b's own execution — see AiDecision.
        // AssembleRecceScout's own comment.
        public static IEnumerator AssembleRecceScoutRoutine(PlayerSetupData player, AiDecision decision, AiTurnContext ctx)
        {
            ArmyData source = decision.ExistingArmy;
            ArmyData emptyArmy = decision.MergeTarget;
            UnitData unit = decision.CollectorUnit;
            yield return AiTurnController.PanTo(ctx, source.Hex);

            if (ArmyActions.TransferMember(unit, source, emptyArmy, ctx.HexSelection, out string failReason))
            {
                AiDebugLog.Write($"[AI] {player.Nickname}: {decision.Reason}.");
                // Feeds the cross-category oscillation guard (see AiTurnContext.WouldRevisitArmy's
                // own comment) — same "only a landed move counts" rule ConsolidateUnitsRoutine follows.
                ctx.RecordArmyVisit(unit, source, emptyArmy);
            }
            else
                AiDebugLog.Write($"[AI] {player.Nickname}: не смог собрать Recce-состав — {failReason}");

            if (ctx.ArmyViewerModal != null)
                ctx.ArmyViewerModal.Hide();
            yield return AiTurnController.WaitStep(ctx);
        }
    }
}
