using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Economy;
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
            // VisitHexTask.ScoreCandidate's own frontier/cleanup split (2026-08-24) — true only for
            // a candidate with zero fresh (unvisited) neighbors, picked ONLY as a fallback once no
            // real frontier candidate is left this step. Every other ScoutTarget source (TryFlee,
            // TryReturnHomeCandidates) leaves this false — cleanup is a VisitHexTask.FindTarget-only
            // concept.
            public readonly bool IsCleanup;

            public ScoutTarget(HexCoord hex, float score, string reason, bool isCleanup = false)
            {
                Hex = hex;
                Score = score;
                Reason = reason;
                IsCleanup = isCleanup;
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
        // standing solo. Used to be rare in practice (every other planner kept a Recce member solo
        // forever on purpose); GarrisonReorgTask no longer carves Recce out of its own consolidation
        // sweep as of 2026-08-20 (project owner's own call — see GarrisonReorgTask's own class
        // comment, point 1.2), so a solo Recce idling at the garrison hex between tasks can now get
        // folded into garrison like any other lone army, and this is what pulls it back out again
        // once Разведка actually wants it. Only ever proposes a member sitting on the SAME hex as
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
                // Never reclaim from an army an ACTIVE task already claims — 2026-08-21 fix
                // (simulation report finding): AiDefencePlanner's own Patrol Recce pickup
                // (FindPatrolRecceCandidate) can now deliberately un-solo a Recce unit into a
                // task-claimed DefendCitadel army sitting at this exact hex, and without this guard
                // this method would read it right back out as "buried" the very next step — the
                // same recruit/reclaim ping-pong already fixed once for Агрессия (see
                // RaidWeakerArmyTask.FindRecruitAt's own comment, 2026-08-17) reappearing on the
                // Оборона/Разведка axis. Every other cross-category recruit lookup in this codebase
                // already gets this for free by only ever scanning pool.AvailableArmies(); this one
                // scans the raw ArmyRegistry instead (a fresh empty army has no pool entry yet at
                // the point some of its own callers use this), so the check is explicit here.
                if (AiTaskRegistry.TaskFor(player, source) != null)
                    continue;
                UnitData unit = source.Members.FirstOrDefault(m => m.HasAbility(UnitAbilities.Recce) && m.IsHero == wantHero);
                if (unit == null || (!source.IsGarrison && source.Members.Count == 1))
                    continue; // already solo — already IsSoloRecce, nothing to assemble
                return new BuriedRecceUnit(source, unit);
            }
            return null;
        }

        // Разведка's own shared base weight — reconBaseWeight tapering off past
        // AiConfig.reconPriorityDecayStartTurn (see that field's own comment). Covers every
        // Разведка candidate now: the two MoveArmy sites below (TryContinueVisitTask/
        // TryStartVisitCandidates) AND the assembly/request sites
        // (TryStartReconAssemblyCandidatesFor's own SpawnReconArmy/AssembleRecceScout/PlayCard) —
        // 2026-08-23, project owner's own call: keeping the pipeline candidates flat while the
        // move candidates decayed just meant the AI kept assembling scouts it had already lost
        // interest in walking around.
        // TryFlee's own score is the one exception — see the two MoveArmy call sites, which pass
        // isFlee: true to skip the taper entirely for that candidate.
        private static float ReconMoveWeight(AiTurnContext ctx, bool isFlee = false)
        {
            if (isFlee)
                return AiConfig.reconBaseWeight;
            int turnsPast = ctx.TurnNumber - AiConfig.reconPriorityDecayStartTurn;
            if (turnsPast <= 0)
                return AiConfig.reconBaseWeight;
            float decayed = AiConfig.reconBaseWeight - turnsPast * AiConfig.reconPriorityDecayPerTurn;
            return Math.Max(decayed, AiConfig.reconPriorityDecayFloor);
        }

        // Project owner's own 2026-08-19 rebalance (see AiConfig.reconAggressionSuppressionPenalty's
        // own comment) — while Агрессия has a real committed raid force out working, routine
        // VisitHex scouting cedes a flat amount of ground instead of competing evenly. Replaces the
        // old raidCommittedBonus top-up on Агрессия's own side of the arbiter.
        // Both call sites below skip this penalty on a flee candidate (isFlee) — see AiConfig.
        // scoutFleeBonus's own comment: a scout fleeing a real threat must reliably land at
        // reconBaseWeight+scoutFleeBonus=125, not have a committed raid eat into that.
        private static float AggressionSuppressionPenalty(PlayerSetupData player) =>
            AiTaskRegistry.CountActive(player, AiTaskKind.RaidWeakerArmy) > 0
                ? AiConfig.reconAggressionSuppressionPenalty
                : 0f;

        // ---- Разведка · Задача 1 (Посещение хекса) ----

        // Deconfliction (2026-08-24, project owner's own log audit): every OTHER active VisitHex
        // task's own TargetHex, so VisitHexTask.FindTarget never re-picks a hex a different scout
        // already committed to this same turn (both callers below re-run FindTarget fresh every
        // step — see TryContinueVisitTask's own comment — so without this two independently-
        // scored scouts routinely converge on the identical best wavefront hex). `exclude` is the
        // continuing task itself, never in the result — a task must stay free to re-pick its own
        // in-progress target.
        private static HashSet<HexCoord> ClaimedVisitTargets(PlayerSetupData player, AiTask exclude) =>
            new HashSet<HexCoord>(AiTaskRegistry.TasksFor(player)
                .Where(t => t.Kind == AiTaskKind.VisitHex && t != exclude)
                .Select(t => t.TargetHex));

        // Stall watchdog (2026-08-24, project owner's own root-cause report) — see AiTask.
        // VisitLastProgressTurn's own comment. Only ever called from a branch that's ABOUT to
        // return null for lack of any legal step this call — never short-circuits a call that could
        // still succeed. Removing the task here (rather than just returning null) is what actually
        // frees the army: an untasked army is ordinary TryStartVisitCandidates material again next
        // step, instead of sitting registered against one of only AiConfig.maxConcurrentVisitHex
        // slots forever with nothing advancing it.
        private static bool TryDropIfStalled(PlayerSetupData player, AiTurnContext ctx, AiTask task, string reason)
        {
            if (ctx.TurnNumber - task.VisitLastProgressTurn < AiConfig.visitHexStallTurns)
                return false;
            AiDebugLog.Write($"[AI] {player.Nickname}: \"{task.Army?.Name}\" VisitHex stalled — no progress for "
                + $"{ctx.TurnNumber - task.VisitLastProgressTurn} turns ({reason}), last moved turn "
                + $"{task.VisitLastProgressTurn}, at ({task.Army?.Hex.Q},{task.Army?.Hex.R}), target=({task.TargetHex.Q},{task.TargetHex.R}) — dropping the task.");
            AiTaskRegistry.Remove(player, task);
            return true;
        }

        public static AiDecision TryContinueVisitTask(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx, AiTask task)
        {
            if (task.Army?.Controller == null || !ArmyRegistry.AllForOwner(player).Contains(task.Army))
            {
                AiTaskRegistry.Remove(player, task);
                return null;
            }
            if (task.Army.CurrentMovement <= 0)
            {
                TryDropIfStalled(player, ctx, task, "no movement left");
                return null;
            }
            if (!task.Army.HasActivatedThisTurn && !root.CanSpendActionPoints(task.Army.ActivationApCost))
            {
                TryDropIfStalled(player, ctx, task, "can't afford activation");
                return null;
            }

            // Re-evaluated fresh every call rather than trusting the stored TargetHex — same
            // "непрерывная переоценка" principle the doc's own turn-execution section already
            // documents (an equally-good or better hex may have opened up since the task
            // started). Задача 1 has no single completion hex: it keeps proposing a next
            // unvisited target for as long as one exists nearby (see AiTaskKind's own comment),
            // so "nothing left" is what actually frees the army, not "arrived once".
            // fleeTarget carries a real cross-category score of its own (scoutFleeBonus) that must
            // still reach the final decision; FindTarget's own Score does NOT — see AiConfig.
            // scoutProximityWeight's own comment for why that stays a purely internal ranking term.
            ScoutTarget? fleeTarget = VisitHexTask.TryFlee(player, task.Army, task, ctx.TurnNumber);
            ScoutTarget? target = fleeTarget ?? VisitHexTask.FindTarget(player, task.Army, ctx.Map, ClaimedVisitTargets(player, task));
            if (!target.HasValue)
            {
                AiTaskRegistry.Remove(player, task);
                return null;
            }
            task.TargetHex = target.Value.Hex;

            // Route only through already-visited ground — see VisitHexTask.FindNextSafeStep's own
            // comment. `target.Value.Hex` itself stays the task's own bookkeeping target above;
            // this is only ever how far THIS step's actual move order reaches toward it.
            HexCoord? nextStep = VisitHexTask.FindNextSafeStep(ctx.Map, task.Army, target.Value.Hex);
            if (nextStep == null)
            {
                // Ordinarily re-tried next step, target still valid — but if this has been true for
                // AiConfig.visitHexStallTurns turns running (permanently boxed in, or stuck aiming
                // at a flee/retreat hex it can never actually progress toward), stop occupying a
                // VisitHex slot over it.
                TryDropIfStalled(player, ctx, task, "no safe step toward target");
                return null;
            }

            // A cleanup target (VisitHexTask.FindTarget's own frontier/cleanup split — opens zero
            // fresh neighbors, only ever picked once no real frontier candidate is left) replaces
            // the usual ReconMoveWeight contribution with AiConfig.visitCleanupScore outright,
            // rather than stacking on top of it — see that field's own comment.
            float baseWeight = fleeTarget.HasValue
                ? ReconMoveWeight(ctx, isFlee: true)
                : target.Value.IsCleanup
                    ? AiConfig.visitCleanupScore
                    : ReconMoveWeight(ctx);
            float score = baseWeight
                - (fleeTarget.HasValue ? 0f : AggressionSuppressionPenalty(player))
                + (fleeTarget.HasValue ? fleeTarget.Value.Score : 0f);
            var stepTarget = new ScoutTarget(nextStep.Value, target.Value.Score, target.Value.Reason);
            return AiDecision.Move(task.Army, stepTarget, task, score, AiTaskCategory.Reconnaissance);
        }

        // Composition/target logic both live on VisitHexTask now (IsEligibleComposition/
        // FindTarget) — this orchestrator step just gathers eligible armies and turns the
        // class's own proposal into an AiDecision/AiTask.
        public static List<AiDecision> TryStartVisitCandidates(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            AiResourcePool pool, HashSet<ArmyData> stuckScouts)
        {
            var results = new List<AiDecision>();
            if (AiTaskRegistry.CountActive(player, AiTaskKind.VisitHex) >= AiConfig.maxConcurrentVisitHex)
                return results;

            // Every brand-new candidate built in this same loop below still only ever competes
            // through AiTurnController.Decide's own single-best-of-the-whole-step arbiter (see
            // that method's own comment) — at most one of them ever actually gets registered as a
            // real task, so this only needs to exclude ALREADY-registered VisitHex targets, not
            // targets other candidates in this same loop are about to propose.
            HashSet<HexCoord> claimedTargets = ClaimedVisitTargets(player, null);

            foreach (ArmyData army in pool.AvailableArmies())
            {
                if (!VisitHexTask.IsEligibleComposition(army) || army.CurrentMovement <= 0 || army.Controller == null || stuckScouts.Contains(army))
                    continue;
                if (!army.HasActivatedThisTurn && !root.CanSpendActionPoints(army.ActivationApCost))
                    continue;

                // No task exists yet to carry FledOnTurn — always free to flee on first
                // encounter, then stamp the flag onto the freshly created task below so the very
                // next continuation call (see TryContinueVisitTask) already honours the one-turn
                // cap.
                ScoutTarget? fleeTarget = VisitHexTask.TryFlee(player, army, null, ctx.TurnNumber);
                ScoutTarget? target = fleeTarget ?? VisitHexTask.FindTarget(player, army, ctx.Map, claimedTargets);
                if (!target.HasValue)
                    continue;

                // Same fog-safety restriction as TryContinueVisitTask — see VisitHexTask.
                // FindNextSafeStep's own comment. A brand new task never gets to skip it just
                // because it hasn't started yet.
                HexCoord? nextStep = VisitHexTask.FindNextSafeStep(ctx.Map, army, target.Value.Hex);
                if (nextStep == null)
                    continue; // can't safely take even one step toward it yet — no candidate this step

                var task = new AiTask
                {
                    Kind = AiTaskKind.VisitHex, Army = army, TargetHex = target.Value.Hex, Reason = target.Value.Reason,
                    FledOnTurn = fleeTarget.HasValue ? ctx.TurnNumber : -1,
                    // Stamped to THIS turn, not -1 — see AiTask.VisitLastProgressTurn's own comment:
                    // a brand-new task hasn't stalled yet, so its clock starts now, not "ages ago".
                    VisitLastProgressTurn = ctx.TurnNumber,
                };
                // Same cleanup-replaces-frontier-weight rule as TryContinueVisitTask above.
                float baseWeight = fleeTarget.HasValue
                    ? ReconMoveWeight(ctx, isFlee: true)
                    : target.Value.IsCleanup
                        ? AiConfig.visitCleanupScore
                        : ReconMoveWeight(ctx);
                float score = baseWeight
                    - (fleeTarget.HasValue ? 0f : AggressionSuppressionPenalty(player))
                    + (fleeTarget.HasValue ? fleeTarget.Value.Score : 0f);
                var stepTarget = new ScoutTarget(nextStep.Value, target.Value.Score, target.Value.Reason);
                results.Add(AiDecision.Move(army, stepTarget, task, score, AiTaskCategory.Reconnaissance));
            }
            return results;
        }

        // AiArmyRoles.IsSoloHeroAwaitingEscort's own other half: once it has no active Задача 1
        // task and nothing left nearby to visit, walk it back to the garrison instead of leaving
        // it wherever it happened to stop — that's where AiManagementPlanner.TryPlayCardCandidates'
        // own Unit-role routing will actually find it the next time an escort card is affordable.
        // A no-op once it's already there. Targets the nearest own garrison (starting citadel or
        // any later-founded Base with Barracks — see AiTurnController.NearestOwnGarrisonHex), not
        // always the citadel — unlike SpawnReconArmyRoutine's own spawn point, a solo hero already
        // out scouting should walk home to whichever base is actually closest to it.
        public static List<AiDecision> TryReturnHomeCandidates(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            HashSet<ArmyData> stuckScouts)
        {
            var results = new List<AiDecision>();
            foreach (ArmyData army in ArmyRegistry.AllForOwner(player))
            {
                if (!AiArmyRoles.IsSoloHeroAwaitingEscort(army) || army.CurrentMovement <= 0 || army.Controller == null
                    || stuckScouts.Contains(army) || AiTaskRegistry.TaskFor(player, army) != null)
                    continue;
                HexCoord garrisonHex = AiTurnController.NearestOwnGarrisonHex(player, army.Hex);
                if (army.Hex.Equals(garrisonHex))
                    continue;
                if (!AiTurnController.CanIssueMoveNow(root, army, ctx.Map, garrisonHex))
                    continue;
                var target = new ScoutTarget(garrisonHex, 0f,
                    "nothing nearby to visit — returns to the nearest garrison to wait for an escort");
                results.Add(AiDecision.Move(army, target, null, AiConfig.managementReturnHomeScore, AiTaskCategory.Reconnaissance));
            }
            return results;
        }

        // ---- Разведка · сборка Recce-состава (юнит-Recce / герой-Recce) ----
        // The project owner's own call (2026-08-19 — "ScoutPlanner должен сам решить куда
        // положить своего скаута, менеджеру об этом знать не обязательно"): a Recce card's
        // placement is entirely this category's own concern now, not a Менеджмент leftover.
        // AiManagementPlanner.TryPlayCardCandidates skips every Recce card outright (see its own
        // comment) — this is the ONLY path a Recce card ever gets played through, no competing
        // proposal, no tie-break to win. Playing one still shrinks hand.Hand for whatever
        // Менеджмент's own backlog scoring reads next step, same as any other category playing a
        // card would (see TryPlayCardCandidates' own comment on that cross-effect).
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
        //      Recce-tagged card → PlayCard, straight into that same empty army (own placement
        //      check below, not routed through AiManagementPlanner.FindPlacement at all — that
        //      method no longer knows Recce exists).
        // Once assembled (AiArmyRoles.IsSoloRecce true), TryStartVisitCandidates already claims
        // the army directly — no further step here.
        //
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
            if (AiTaskRegistry.CountActive(player, AiTaskKind.VisitHex) >= AiConfig.maxConcurrentVisitHex)
                return results;

            // Already have an idle, ready-to-go composition of this exact shape?
            // TryStartVisitCandidates already claims it directly next — nothing to assemble.
            if (pool.AvailableArmies().Any(a => AiArmyRoles.IsSoloRecce(a) && a.Members.Any(m => m.IsHero == wantHero)))
                return results;

            // Or does a matching Recce carrier already exist somewhere ELSE — claimed by another
            // task entirely (Экономика's own preemption path, see AiEconomyPlanner.FindNearestHero's
            // own "нужно брать ближайшего героя... его текущее задание можно отложить" call)? Scans
            // the FULL roster, not just pool.AvailableArmies() — that hero is still "ours", just
            // busy for however many turns the other task legitimately needs it, and falls straight
            // back into pool.AvailableArmies() (picked up by TryStartVisitCandidates above, same
            // IsSoloRecce shape) the moment that task lets go — no separate bookkeeping needed here.
            // Checks composition/ability only, not WHICH task holds it, so this survives any current
            // or future preemption source without a repeat fix — project owner's own report: without
            // this, Разведка used to spawn a full replacement Recce EVERY turn a hero spent away
            // building, however many turns that took.
            if (ArmyRegistry.AllForOwner(player).Any(a => AiArmyRoles.IsSoloRecce(a) && a.Members.Any(m => m.IsHero == wantHero)))
                return results;

            // Scoped to this player's own garrisoned (Barracks) hexes only — a player can have
            // several bases (see AiTurnController.OwnGarrisonHexes), and an empty shell sitting on
            // ANY of them is just as reusable as one at the starting citadel; SpawnReconArmyRoutine
            // itself still only ever creates a NEW one at the citadel (Разведка stays anchored
            // there for target-selection purposes, unchanged), this is purely "don't spin up a
            // fresh empty army when a perfectly good one is already sitting at a base" — 2026-08-22
            // fix (project owner's own report): the old unscoped `pool.AvailableArmies().FirstOrDefault
            // (IsEmptyDeployableArmy)` could in principle also match an idle empty army stranded
            // out in the field (not that this alone caused the "recon never leaves" bug — that was
            // FindNextSafeStep, see its own comment — but a stray field army was never actually
            // useful here as a Recce host: it isn't at a Barracks hex a card could deploy into).
            var ownGarrisonHexes = new HashSet<HexCoord>(AiTurnController.OwnGarrisonHexes(player));
            ArmyData emptyArmy = pool.AvailableArmies()
                .FirstOrDefault(a => AiArmyRoles.IsEmptyDeployableArmy(a) && ownGarrisonHexes.Contains(a.Hex));
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
                //
                // Affordability check covers the FULL two-step cost, not just the army itself
                // (project owner's own 2026-08-19 follow-up): spawning an empty army only to find
                // the Recce card unaffordable next step leaves an orphan empty army that
                // AiManagementPlanner's own spare-army fallback would have created anyway, just
                // without a real Разведка purpose behind it. So this mirrors
                // AiManagementPlanner.FindPlacement's own final branch — CreateArmyApCost +
                // EffectiveDeployApCost(definition) AP, AND the card's own resource cost via
                // AiResourceReservation.CanAfford (not definition.resourceCost.CanAfford(root)
                // directly, same reason as FindPlacement — never spend what an active
                // BuildFacility task already claimed).
                CardData recceCard = FindMatchingRecceCard(hand, wantHero);
                if (recceCard != null)
                {
                    CardDefinition definition = recceCard.Definition;
                    int totalApCost = ArmyActions.CreateArmyApCost + ArmyActions.EffectiveDeployApCost(definition);
                    if (root.ActionPoints >= totalApCost && AiResourceReservation.CanAfford(root, player, definition.resourceCost))
                        results.Add(AiDecision.RequestReconArmy(ReconMoveWeight(ctx) + AiConfig.reconRequestArmyPenalty));
                }
                return results;
            }

            BuriedRecceUnit? buried = FindBuriedRecceUnit(player, wantHero, emptyArmy.Hex);
            if (buried.HasValue && !ctx.WouldRevisitArmy(buried.Value.Unit, emptyArmy))
            {
                results.Add(AiDecision.AssembleRecceScout(buried.Value, emptyArmy, ReconMoveWeight(ctx) + AiConfig.reconAssemblePenalty));
                return results;
            }

            CardData card = FindMatchingRecceCard(hand, wantHero);
            if (card == null)
                return results; // no candidate this step — nothing left to place

            // Own placement check, entirely self-contained — a Recce card always deploys into the
            // SAME empty army this method already found above (`emptyArmy`), never routed through
            // AiManagementPlanner.FindPlacement (see this method's own class comment).
            CardDefinition cardDefinition = card.Definition;
            int deployApCost = ArmyActions.EffectiveDeployApCost(cardDefinition);
            if (!root.CanSpendActionPoints(deployApCost) || !AiResourceReservation.CanAfford(root, player, cardDefinition.resourceCost)
                || !AiManagementPlanner.IsAtRequiredBuilding(emptyArmy, player, cardDefinition))
                return results;

            results.Add(AiDecision.PlayCard(emptyArmy, card, AiManagementPlanner.CardRole.Recce,
                ReconMoveWeight(ctx), AiTaskCategory.Reconnaissance));
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

            PlayerRoot root = PlayerRootRegistry.FindFor(player);
            int ap0 = root != null ? root.ActionPoints : 0;
            int human0 = root != null ? root.GetResource(ResourceType.Human) : 0;
            int energy0 = root != null ? root.GetResource(ResourceType.Energy) : 0;
            int materials0 = root != null ? root.GetResource(ResourceType.Materials) : 0;
            int tech0 = root != null ? root.GetResource(ResourceType.Tech) : 0;
            // Feature 4A (2026-08-24) — same disposable-empty-shell reuse AiAggressionPlanner.
            // RequestRaidArmyRoutine's own comment describes, applied here too (see
            // GarrisonReorgTask.FindDisposableEmptyArmyAt's own comment).
            ArmyData reused = GarrisonReorgTask.FindDisposableEmptyArmyAt(player, hex);
            ArmyData army = reused ?? ArmyActions.CreateArmy(player, hex, ctx.StartingDeckCatalog?.GetCatalog(player.Faction), ctx.HexSelection);
            string delta = root != null ? AiTurnController.ResourceDeltaSuffix(root, ap0, human0, energy0, materials0, tech0) : null;
            AiDebugLog.Write(reused != null
                ? $"[AI] {player.Nickname}: Reconnaissance task — reuses empty army \"{reused.Name}\" for a Recce composition instead of spending AP on a new one."
                : army != null
                    ? $"[AI] {player.Nickname}: Reconnaissance task — creates empty army \"{army.Name}\" for a Recce composition.{delta}"
                    : $"[AI] {player.Nickname}: Reconnaissance task — not enough AP for a new army for a Recce composition.");

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

            PlayerRoot root = PlayerRootRegistry.FindFor(player);
            int ap0 = root != null ? root.ActionPoints : 0;
            int human0 = root != null ? root.GetResource(ResourceType.Human) : 0;
            int energy0 = root != null ? root.GetResource(ResourceType.Energy) : 0;
            int materials0 = root != null ? root.GetResource(ResourceType.Materials) : 0;
            int tech0 = root != null ? root.GetResource(ResourceType.Tech) : 0;
            if (ArmyActions.TransferMember(unit, source, emptyArmy, ctx.HexSelection, out string failReason))
            {
                string delta = root != null ? AiTurnController.ResourceDeltaSuffix(root, ap0, human0, energy0, materials0, tech0) : null;
                AiDebugLog.Write($"[AI] {player.Nickname}: {decision.Reason}.{delta}");
                // Feeds the cross-category oscillation guard (see AiTurnContext.WouldRevisitArmy's
                // own comment) — same "only a landed move counts" rule ConsolidateUnitsRoutine follows.
                ctx.RecordArmyVisit(unit, source, emptyArmy);
            }
            else
                AiDebugLog.Write($"[AI] {player.Nickname}: couldn't assemble the Recce composition — {failReason}");

            if (ctx.ShowArmyModal && ctx.ArmyViewerModal != null)
                ctx.ArmyViewerModal.Hide();
            yield return AiTurnController.WaitStep(ctx);
        }
    }
}
