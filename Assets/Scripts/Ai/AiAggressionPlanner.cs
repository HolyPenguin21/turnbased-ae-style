using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai
{
    // Level-1 category orchestration for Агрессия (AiTaskCategory.Aggression) — the
    // continue/start/recall/return-home tiers AiTurnController.Decide gathers candidates from
    // each step, same role AiScoutPlanner/AiEconomyPlanner/AiManagementPlanner already play for
    // their own categories, plus this category's own execution routines
    // (RequestRaidArmyRoutine/AssembleRaidForceRoutine — see AiTurnController's own class comment
    // on the execution split). Behavioral specifics (target selection, composition eligibility,
    // threat reaction) live one level down, on RaidWeakerArmyTask itself — see its own class
    // comment; this class only sequences calls into it and turns the results into AiDecision/
    // AiTask, the same split every other category already follows.
    public static class AiAggressionPlanner
    {
        // ---- Агрессия · Задача 1 (Зачистка нейтралов/эвентов) ----
        // Full redesign, 2026 — see RaidWeakerArmyTask's own class comment for what changed and
        // why (target/composition/threat reaction all live there now). Replaces AiAggressionPlanner
        // (the old, pre-redesign class of the same name) entirely.

        // Advances an ALREADY-committed task — threat reaction first (own copy, unlike Разведка's
        // one-turn flee this is a ONE-WAY retreat, see AiTask.Retreating's own comment), then
        // "is the target still even real" (AiTaskRegistry stays honest the moment memory says the
        // objective is gone, no forced march home needed for that case), then the actual
        // travel/attack move. Never handles an UNREADY (still-assembling) task — that's
        // TryRaidAssembleCandidates' own job; this returns null for one so the assemble tier gets
        // a clear turn instead of silently competing with it.
        public static AiDecision TryContinueRaidTask(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx, AiTask task)
        {
            if (task.Army?.Controller == null || !ArmyRegistry.AllForOwner(player).Contains(task.Army))
            {
                AiTaskRegistry.Remove(player, task);
                return null;
            }

            HexCoord garrisonHex = AiTurnController.GarrisonHexFor(player);

            if (task.Retreating)
            {
                if (task.Army.Hex.Equals(garrisonHex))
                {
                    AiTaskRegistry.Remove(player, task);
                    return null;
                }
                if (task.Army.CurrentMovement <= 0 || (!task.Army.HasActivatedThisTurn && !root.CanSpendActionPoints(task.Army.ActivationApCost)))
                    return null;
                return AiDecision.Move(task.Army, garrisonHex, "задача «Агрессия»: переукомплектация — возвращается в гарнизон",
                    task, AiConfig.Current.aggressionBaseWeight);
            }

            AiMapMemory.KnownEnemySighting? threat = RaidWeakerArmyTask.NearbyThreat(player, task.Army.Hex);
            if (threat.HasValue)
            {
                float threatDefense = threat.Value.DefenseSum + WorthIt.HexDefenseBonus(threat.Value.Hex, ctx.Map);
                if (task.Army.CurrentMovement <= 0 || (!task.Army.HasActivatedThisTurn && !root.CanSpendActionPoints(task.Army.ActivationApCost)))
                    return null;

                if (!RaidWeakerArmyTask.IsReady(task.Army, threatDefense, threat.Value.AttackSum, threat.Value.Defenders))
                {
                    // Already home — nothing to retreat TO. A move-to-self here used to log as a
                    // failed march and strand the task in stuckScouts for the rest of the turn
                    // (the project owner's own "Bastion Guard" report, 2026-08-17) — stand and
                    // defend instead (see AiTask.DefendingCitadel's own comment).
                    if (task.Army.Hex.Equals(garrisonHex))
                    {
                        task.DefendingCitadel = true;
                        task.TargetHex = threat.Value.Hex;
                        return null; // TryRaidAssembleCandidates' own defense tier picks up recruitment from here
                    }
                    task.Retreating = true;
                    AiDebugLog.Write($"[AI] {player.Nickname}: {task.Army.Name} — известная вражеская армия сильнее, "
                        + "задача «Агрессия» переходит в переукомплектацию.");
                    return AiDecision.Move(task.Army, garrisonHex, "задача «Агрессия»: враг сильнее — отступает",
                        task, AiConfig.Current.aggressionBaseWeight);
                }

                task.TargetHex = threat.Value.Hex;
                task.DefendingCitadel = false; // strong enough now — ordinary counter-attack, no special handling needed
                return AiDecision.Move(task.Army, threat.Value.Hex, "задача «Агрессия»: контратакует известную армию рядом",
                    task, AiConfig.Current.aggressionBaseWeight + AiConfig.Current.raidCounterAttackBonus);
            }

            if (!RaidWeakerArmyTask.IsStillValidTarget(player, task.TargetHex))
            {
                // Objective already resolved (someone else got it, memory corrected) — done, no
                // need to force a march home for this one.
                AiTaskRegistry.Remove(player, task);
                return null;
            }

            RaidWeakerArmyTask.ThreatStrength required = RaidWeakerArmyTask.RequiredStrengthAt(player, task.TargetHex, ctx.Map);
            if (!RaidWeakerArmyTask.IsReady(task.Army, required))
            {
                if (task.Army.Hex.Equals(garrisonHex))
                    return null; // still assembling — TryRaidAssembleCandidates' own turn to act
                // Got weaker mid-travel (combat losses) — no re-assembly support away from the
                // garrison, regroup instead.
                task.Retreating = true;
                return null;
            }

            if (task.Army.CurrentMovement <= 0 || (!task.Army.HasActivatedThisTurn && !root.CanSpendActionPoints(task.Army.ActivationApCost)))
                return null;

            // raidCommittedBonus — see its own AiConfig comment: this is the READY, already-
            // travelling leg of an active Агрессия task ("на задании"), which must reliably
            // outrank Разведка's own routine scouting moves, not just usually win on points.
            float score = AiConfig.Current.aggressionBaseWeight + AiConfig.Current.raidCommittedBonus
                + RaidWeakerArmyTask.ScoreForContinuation(player, task.Army, task.TargetHex);
            return AiDecision.Move(task.Army, task.TargetHex, $"задача «Агрессия»: атакует цель на ({task.TargetHex.Q},{task.TargetHex.R})",
                task, score);
        }

        // Both "start a brand new raid" and "recruit the next member into an already-forming one"
        // — same decision shape (AiDecision.AssembleRaidForce), same execution routine, only
        // difference is whether `task` is a fresh, not-yet-registered AiTask or an existing one
        // (AiTurnController.Commit's own generic `!Contains` check handles either transparently,
        // same as every other task kind). Композиция — see RaidWeakerArmyTask's own class
        // comment: either an already-idle army that alone already beats the target (dispatched
        // directly, no assembly at all — see FindReadyIdleArmy), or a hero-led force built up one
        // recruit at a time from whatever's currently sitting at the garrison hex (garrison stock
        // itself, or an idle army recalled there — see TryRaidRecallCandidates below).
        public static List<AiDecision> TryRaidAssembleCandidates(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx, AiResourcePool pool)
        {
            var results = new List<AiDecision>();
            HexCoord garrisonHex = AiTurnController.GarrisonHexFor(player);

            // Every hex an active (non-retreating) Агрессия task is already targeting, ready or
            // still assembling alike — collected here so the "start a new one" tier below never
            // hands a second, independent army the SAME target a first one already committed to
            // (see RaidWeakerArmyTask.FindTarget's own comment on the excludeHexes it takes).
            var activeTargets = new HashSet<HexCoord>();

            // Continue every already-registered, not-yet-ready task.
            foreach (AiTask task in AiTaskRegistry.TasksFor(player).Where(t => t.Kind == AiTaskKind.RaidWeakerArmy && !t.Retreating).ToList())
            {
                activeTargets.Add(task.TargetHex);
                if (task.Army == null || !task.Army.Hex.Equals(garrisonHex))
                    continue; // travelling or retreating — TryContinueRaidTask's own turn, not this tier's
                RaidWeakerArmyTask.ThreatStrength required = RaidWeakerArmyTask.RequiredStrengthAt(player, task.TargetHex, ctx.Map);
                if (RaidWeakerArmyTask.IsReady(task.Army, required))
                    continue; // ready — TryContinueRaidTask picks it up from here

                if (!WorthIt.Beats(RaidWeakerArmyTask.MaxPossibleAttack(player, task.Army, pool), required.Defense))
                {
                    // Not yet, not never — MaxPossibleAttack only reflects the pool as it stands
                    // THIS step (see its own comment: "just needs more turns of assembling" is the
                    // whole point of this check, not an automatic death sentence). Stays registered
                    // and keeps its garrison slot/activeTargets claim — no candidate this step, but
                    // the project owner's own call: don't discard the hero(es) already committed and
                    // restart from scratch with a fresh target next step (that was burning 2-3 raid
                    // attempts a turn on the same unreachable ceiling, see AiDebug.log 2026-08-16).
                    // Once the pool's own total attack grows (more units played/drawn elsewhere —
                    // AiManagementPlanner.TryPlayCardCandidates's own unitCardBacklogWeight now
                    // pushes that along), this same check re-passes on its own and FindRecruitAt
                    // below resumes normally.
                    AiDebugLog.Write($"[AI] {player.Nickname}: {task.Army.Name} — цели пока не хватает силы даже при "
                        + "полной сборке, задача «Агрессия» ждёт подкрепления.");
                    continue;
                }

                UnitData recruit = RaidWeakerArmyTask.FindRecruitAt(player, garrisonHex, task.Army, pool, out ArmyData source);
                if (recruit == null || source == null || !task.Army.HasRoom || ctx.WouldRevisitArmy(recruit, task.Army))
                    continue; // nothing to recruit (or full) this step — waits for a recall/next card
                results.Add(AiDecision.AssembleRaidForce(source, recruit, task.Army, task,
                    AiConfig.Current.aggressionBaseWeight + AiConfig.Current.raidAssembleBonus));
            }

            // ---- Оборона цитадели (temporary — see AiTask.DefendingCitadel's own TODO) ----
            // Trigger: a real (non-neutral) enemy army sighted within raidThreatRadius of the
            // garrison itself (RaidWeakerArmyTask.NearbyThreat, the same primitive
            // TryContinueRaidTask already reacts to for an in-flight task) — never speculative,
            // always an actually-observed AiMapMemory sighting. Exempt from maxConcurrentRaid
            // entirely below: defending the player's own base is never optional busywork the
            // concurrency cap should be allowed to starve.
            AiMapMemory.KnownEnemySighting? citadelThreat = RaidWeakerArmyTask.NearbyThreat(player, garrisonHex);
            if (citadelThreat.HasValue && !AiTaskRegistry.TasksFor(player).Any(t => t.Kind == AiTaskKind.RaidWeakerArmy
                    && t.DefendingCitadel && !t.Retreating && t.TargetHex.Equals(citadelThreat.Value.Hex)))
            {
                RaidWeakerArmyTask.ThreatStrength defenseRequired =
                    RaidWeakerArmyTask.RequiredStrengthAt(player, citadelThreat.Value.Hex, ctx.Map);

                ArmyData readyDefender = RaidWeakerArmyTask.FindReadyIdleArmy(player, defenseRequired, pool);
                if (readyDefender != null)
                {
                    var readyDefenseTask = new AiTask
                    {
                        Kind = AiTaskKind.RaidWeakerArmy, Army = readyDefender,
                        TargetHex = citadelThreat.Value.Hex, DefendingCitadel = true,
                    };
                    results.Add(AiDecision.Move(readyDefender, citadelThreat.Value.Hex,
                        $"задача «Агрессия»: оборона цитадели — атакует известную армию у ({citadelThreat.Value.Hex.Q},{citadelThreat.Value.Hex.R})",
                        readyDefenseTask,
                        AiConfig.Current.aggressionBaseWeight + AiConfig.Current.citadelDefenseBonus + AiConfig.Current.raidReadyArmyBonus));
                }
                else
                {
                    ArmyData defenseForming = pool.AvailableArmies()
                        .FirstOrDefault(a => AiArmyRoles.IsEmptyDeployableArmy(a) && a.Hex.Equals(garrisonHex));
                    if (defenseForming != null)
                    {
                        var defenseTask = new AiTask
                        {
                            Kind = AiTaskKind.RaidWeakerArmy, Army = defenseForming,
                            TargetHex = citadelThreat.Value.Hex, DefendingCitadel = true,
                        };
                        UnitData defenseRecruit = RaidWeakerArmyTask.FindRecruitAt(player, garrisonHex, defenseForming, pool, out ArmyData defenseSource);
                        if (defenseRecruit != null && defenseSource != null && !ctx.WouldRevisitArmy(defenseRecruit, defenseForming))
                            results.Add(AiDecision.AssembleRaidForce(defenseSource, defenseRecruit, defenseForming, defenseTask,
                                AiConfig.Current.aggressionBaseWeight + AiConfig.Current.citadelDefenseBonus));
                    }
                    else if (root.CanSpendActionPoints(ArmyActions.CreateArmyApCost))
                    {
                        results.Add(AiDecision.RequestRaidArmy(AiConfig.Current.aggressionBaseWeight + AiConfig.Current.citadelDefenseBonus
                            + AiConfig.Current.raidRequestArmyPenalty));
                    }
                }
            }

            // Start a new ORDINARY raid, up to the concurrency cap — citadel defense above is
            // exempt (its own gate is "not already defending against this exact threat", not the
            // numeric cap), so it never gets starved by, or counts against, ordinary raid slots.
            if (AiTaskRegistry.TasksFor(player).Count(t => t.Kind == AiTaskKind.RaidWeakerArmy && !t.DefendingCitadel)
                    >= AiConfig.Current.maxConcurrentRaid)
                return results;

            ArmyData garrison = AiTurnController.GarrisonArmyFor(player);
            if (garrison == null)
                return results;
            RaidWeakerArmyTask.RaidTarget? target = RaidWeakerArmyTask.FindTarget(player, garrison, ctx.Map, activeTargets);
            if (!target.HasValue)
                return results;

            ArmyData readyArmy = RaidWeakerArmyTask.FindReadyIdleArmy(player, target.Value.Threat, pool);
            if (readyArmy != null)
            {
                var readyTask = new AiTask { Kind = AiTaskKind.RaidWeakerArmy, Army = readyArmy, TargetHex = target.Value.Hex };
                results.Add(AiDecision.Move(readyArmy, target.Value, readyTask,
                    AiConfig.Current.aggressionBaseWeight + target.Value.Score + AiConfig.Current.raidReadyArmyBonus));
                return results;
            }

            ArmyData forming = pool.AvailableArmies().FirstOrDefault(a => AiArmyRoles.IsEmptyDeployableArmy(a) && a.Hex.Equals(garrisonHex));
            if (forming == null)
            {
                if (root.CanSpendActionPoints(ArmyActions.CreateArmyApCost))
                    results.Add(AiDecision.RequestRaidArmy(AiConfig.Current.aggressionBaseWeight + AiConfig.Current.raidRequestArmyPenalty));
                return results;
            }

            var newTask = new AiTask { Kind = AiTaskKind.RaidWeakerArmy, Army = forming, TargetHex = target.Value.Hex };
            UnitData firstRecruit = RaidWeakerArmyTask.FindRecruitAt(player, garrisonHex, forming, pool, out ArmyData firstSource);
            if (firstRecruit != null && firstSource != null && !ctx.WouldRevisitArmy(firstRecruit, forming))
                results.Add(AiDecision.AssembleRaidForce(firstSource, firstRecruit, forming, newTask,
                    AiConfig.Current.aggressionBaseWeight + AiConfig.Current.raidAssembleBonus));
            return results;
        }

        // An idle army anywhere else on the map, while at least one Агрессия task is currently
        // assembling (needs recruits) — walks toward the garrison so TryRaidAssembleCandidates can
        // fold its members in once it arrives (same-hex only, like every reorg move in this
        // codebase). No Task of its own (same shape as AiScoutPlanner.TryReturnHomeCandidates) —
        // it's prep work for whichever raid task happens to still need bodies once it gets there,
        // not committed to one in particular.
        public static List<AiDecision> TryRaidRecallCandidates(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            AiResourcePool pool, HashSet<ArmyData> stuckScouts)
        {
            var results = new List<AiDecision>();
            bool anyAssembling = AiTaskRegistry.TasksFor(player).Any(t => t.Kind == AiTaskKind.RaidWeakerArmy && !t.Retreating
                && t.Army != null && !RaidWeakerArmyTask.IsReady(t.Army, RaidWeakerArmyTask.RequiredStrengthAt(player, t.TargetHex, ctx.Map)));
            if (!anyAssembling)
                return results;

            HexCoord garrisonHex = AiTurnController.GarrisonHexFor(player);
            foreach (ArmyData army in pool.AvailableArmies())
            {
                if (army.IsGarrison || army.IsPrison || army.Members.Count == 0 || army.Hex.Equals(garrisonHex)
                    || army.CurrentMovement <= 0 || army.Controller == null || stuckScouts.Contains(army))
                    continue;
                if (!army.HasActivatedThisTurn && !root.CanSpendActionPoints(army.ActivationApCost))
                    continue;

                var target = new AiScoutPlanner.ScoutTarget(garrisonHex, 0f, "задача «Агрессия»: возвращается в гарнизон для сборки состава");
                results.Add(AiDecision.Move(army, target, null, AiConfig.Current.raidRecallScore));
            }
            return results;
        }

        // Оборона цитадели's own emergency reinforcement (temporary — see AiTask.DefendingCitadel's
        // own TODO). TryRaidRecallCandidates above already treats a DefendingCitadel task as
        // "assembling" (its own gate is Kind==RaidWeakerArmy && !Retreating && !IsReady, which a
        // defense task satisfies as-is) — so idle armies already get walked home for free, no
        // change needed there. What's missing is pulling an army off an ACTIVE task elsewhere:
        // only fires once idle reinforcement genuinely can't cover the defending army's own
        // ceiling, and even then only pulls a field army off routine work (never another
        // ready-to-strike raid, another citadel defense, or an already-retreating one) — same
        // "cancel and redirect" primitive AiEconomyPlanner.TryStartEconomyCandidates already
        // established via AiDecision.PreemptedTask (see AiTurnController.Commit's own handling —
        // untouched, reused as-is).
        public static List<AiDecision> TryCitadelDefensePreemptCandidates(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx, AiResourcePool pool)
        {
            var results = new List<AiDecision>();
            AiTask defenseTask = AiTaskRegistry.TasksFor(player)
                .FirstOrDefault(t => t.Kind == AiTaskKind.RaidWeakerArmy && t.DefendingCitadel && !t.Retreating);
            if (defenseTask == null || defenseTask.Army == null)
                return results;

            RaidWeakerArmyTask.ThreatStrength required = RaidWeakerArmyTask.RequiredStrengthAt(player, defenseTask.TargetHex, ctx.Map);
            if (RaidWeakerArmyTask.IsReady(defenseTask.Army, required))
                return results; // already strong enough — normal continuation/counter-attack handles the rest

            if (WorthIt.Beats(RaidWeakerArmyTask.MaxPossibleAttack(player, defenseTask.Army, pool), required.Defense))
                return results; // idle armies alone would already cover it — TryRaidRecallCandidates is already pulling them home

            HexCoord garrisonHex = AiTurnController.GarrisonHexFor(player);
            foreach (ArmyData army in ArmyRegistry.AllForOwner(player))
            {
                if (army == defenseTask.Army || army.IsGarrison || army.IsPrison || army.Members.Count == 0
                    || army.Hex.Equals(garrisonHex) || army.Controller == null || army.CurrentMovement <= 0
                    || !BattleInitiator.IsCombatCapable(army) || AiArmyRoles.IsScoutCapable(army))
                    continue;

                AiTask existingTask = AiTaskRegistry.TaskFor(player, army);
                // Never poach an army already committed to ITS OWN ready-to-strike raid, another
                // citadel defense, or already mid-retreat — only routine travel work (VisitHex,
                // BuildFacility, an assembling/not-yet-ready raid) yields to the emergency.
                if (existingTask != null && (existingTask.DefendingCitadel || existingTask.Retreating
                    || (existingTask.Kind == AiTaskKind.RaidWeakerArmy
                        && RaidWeakerArmyTask.IsReady(army, RaidWeakerArmyTask.RequiredStrengthAt(player, existingTask.TargetHex, ctx.Map)))))
                    continue;
                if (!army.HasActivatedThisTurn && !root.CanSpendActionPoints(army.ActivationApCost))
                    continue;

                var target = new AiScoutPlanner.ScoutTarget(garrisonHex, 0f,
                    "задача «Агрессия»: оборона цитадели — отзывается для защиты");
                AiDecision decision = AiDecision.Move(army, target, task: null, AiConfig.Current.citadelDefensePreemptScore);
                decision.PreemptedTask = existingTask;
                results.Add(decision);
            }
            return results;
        }

        // RaidWeakerArmyTask's own "nothing left to raid" fallback — a combat-capable field army
        // with no active task, sitting away from the garrison, once RaidWeakerArmyTask.
        // HasAnythingToRaid says there's no known neutral/event/enemy-building target left
        // anywhere on the map (own dedicated gate, same shape as AiEconomyPlanner.
        // TryEconomyReturnHomeCandidates' own HasAnythingToBuild check — see that method's own
        // comment for why this is a coarse, reachability-blind existence check rather than a
        // per-army one: an army that simply wasn't THIS step's pick for a target that DOES still
        // exist elsewhere is left alone rather than sent home prematurely, same reasoning
        // TryRaidRecallCandidates' own anyAssembling gate follows). Recce-composition armies are
        // excluded — their own VisitHex task already owns that lifecycle
        // (TryFlee, their own goal-met handling); this is only for a raid army left jobless once
        // its own target is gone (the project owner's own report, 2026-08-16: it just sat there
        // forever instead).
        public static List<AiDecision> TryRaidReturnHomeCandidates(PlayerSetupData player, PlayerRoot root,
            AiResourcePool pool, HashSet<ArmyData> stuckScouts)
        {
            var results = new List<AiDecision>();
            if (RaidWeakerArmyTask.HasAnythingToRaid(player))
                return results;

            HexCoord garrisonHex = AiTurnController.GarrisonHexFor(player);
            foreach (ArmyData army in pool.AvailableArmies())
            {
                if (army.IsGarrison || army.IsPrison || army.Hex.Equals(garrisonHex) || army.CurrentMovement <= 0
                    || army.Controller == null || stuckScouts.Contains(army) || AiArmyRoles.IsScoutCapable(army)
                    || !BattleInitiator.IsCombatCapable(army))
                    continue;
                if (!army.HasActivatedThisTurn && !root.CanSpendActionPoints(army.ActivationApCost))
                    continue;

                var target = new AiScoutPlanner.ScoutTarget(garrisonHex, 0f, "рейдить больше нечего — возвращается в гарнизон");
                results.Add(AiDecision.Move(army, target, null, AiConfig.Current.raidReturnHomeScore));
            }
            return results;
        }

        // ---- Постбоевая переоценка (раненая армия ждёт подкрепление или идёт домой чиниться) ----

        // A critically wounded (see RaidWeakerArmyTask.IsCriticallyWounded — ≤50% HP on some
        // non-hero member) idle field army, regardless of whether other raid targets still exist
        // anywhere (deliberately NOT gated on HasAnythingToRaid, unlike TryRaidReturnHomeCandidates
        // right above — that gate is exactly why a wounded army used to sit forever: nothing routed
        // it home while raiding elsewhere was still possible). Decides, per the project owner's own
        // spec, whether it's cheaper (in AP × turns-to-travel) to march the whole army home itself,
        // or to wait in place for a single non-hero courier from the garrison — see GoHomeApCost/
        // WaitApCost below. Skips an army a RaidReinforce task already has a courier en route to
        // (TargetArmy match, not Army — that field is the courier, see AiTask.TargetArmy's own
        // comment).
        public static List<AiDecision> TryRaidRegroupCandidates(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            AiResourcePool pool, HashSet<ArmyData> stuckScouts)
        {
            var results = new List<AiDecision>();
            HexCoord garrisonHex = AiTurnController.GarrisonHexFor(player);

            foreach (ArmyData army in pool.AvailableArmies())
            {
                if (army.IsGarrison || army.IsPrison || army.Hex.Equals(garrisonHex) || army.Controller == null
                    || stuckScouts.Contains(army) || AiArmyRoles.IsScoutCapable(army) || !BattleInitiator.IsCombatCapable(army))
                    continue;
                if (!RaidWeakerArmyTask.IsCriticallyWounded(army))
                    continue;
                if (AiTaskRegistry.TasksFor(player).Any(t => t.Kind == AiTaskKind.RaidReinforce && t.TargetArmy == army))
                    continue; // already has a courier dispatched or en route

                RaidWeakerArmyTask.RaidTarget? target = RaidWeakerArmyTask.FindTarget(player, army, ctx.Map);
                UnitData recruit = target.HasValue ? RaidWeakerArmyTask.FindNonHeroRecruitAt(garrisonHex, pool, army) : null;
                bool canDispatch = recruit != null && root.CanSpendActionPoints(ArmyActions.CreateArmyApCost);

                bool goHome = !target.HasValue || !canDispatch;
                if (!goHome)
                {
                    int toGarrison = HexGridMath.Distance(army.Hex, garrisonHex);
                    float goHomeApCost = ApRoundTrip(army.ActivationApCost, army.MaxMovement, toGarrison,
                        HexGridMath.Distance(garrisonHex, target.Value.Hex));
                    float waitApCost = ApOneWay(recruit.ActivationApCost, recruit.MoveMax, toGarrison);
                    goHome = goHomeApCost <= waitApCost;
                }

                if (goHome)
                {
                    if (army.CurrentMovement <= 0 || (!army.HasActivatedThisTurn && !root.CanSpendActionPoints(army.ActivationApCost)))
                        continue;
                    var homeTarget = new AiScoutPlanner.ScoutTarget(garrisonHex, 0f,
                        "задача «Агрессия»: критически ранена — возвращается на базу чиниться");
                    results.Add(AiDecision.Move(army, homeTarget, task: null, AiConfig.Current.aggressionBaseWeight));
                }
                else
                {
                    var reinforceTask = new AiTask { Kind = AiTaskKind.RaidReinforce, TargetArmy = army, TargetHex = army.Hex };
                    results.Add(AiDecision.DispatchReinforcement(AiTurnController.GarrisonArmyFor(player), recruit, reinforceTask,
                        AiConfig.Current.raidReinforceDispatchScore));
                }
            }
            return results;
        }

        // AP cost of `mover` travelling `d1` hexes then `d2` more (there-and-back through the
        // garrison, for the "go home itself" option) — one activation's worth of AP per turn the
        // trip takes, MoveMax capping how far one turn covers. Same rough "hex distance, not real
        // pathfinding" precision every other candidate-scoring helper in this codebase already
        // uses (see RaidWeakerArmyTask.ProximityScore) — good enough to compare two options, not
        // meant to predict the exact real AP spend.
        private static float ApRoundTrip(int activationApCost, int moveMax, int d1, int d2) =>
            activationApCost * (TravelTurns(d1, moveMax) + TravelTurns(d2, moveMax));

        private static float ApOneWay(int activationApCost, int moveMax, int distance) =>
            activationApCost * TravelTurns(distance, moveMax);

        private static int TravelTurns(int distance, int moveMax) =>
            moveMax > 0 ? (int)System.Math.Ceiling((double)distance / moveMax) : distance;

        // Drives an active RaidReinforce task — Army is the courier, left null by
        // TryRaidRegroupCandidates until DispatchReinforcementRoutine actually spawns it (nothing
        // reads this task in between those two steps, see that routine's own comment). Travels
        // toward TargetHex (the wounded army's own hex, fixed at dispatch — it deliberately never
        // moves while a courier is inbound, see TryRaidRegroupCandidates' own skip check) same as
        // any other travel-stage task, then hands off to the swap once arrived.
        public static AiDecision AdvanceReinforceTask(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx, AiTask task)
        {
            if (task.Army == null)
                return null; // just dispatched this same turn — DispatchReinforcementRoutine hasn't run yet

            if (task.Army.Controller == null || !ArmyRegistry.AllForOwner(player).Contains(task.Army)
                || task.TargetArmy?.Controller == null || !ArmyRegistry.AllForOwner(player).Contains(task.TargetArmy))
            {
                AiTaskRegistry.Remove(player, task);
                return null;
            }

            if (!task.Army.Hex.Equals(task.TargetHex))
            {
                if (task.Army.CurrentMovement <= 0 || (!task.Army.HasActivatedThisTurn && !root.CanSpendActionPoints(task.Army.ActivationApCost)))
                    return null;
                var moveTarget = new AiScoutPlanner.ScoutTarget(task.TargetHex, 0f,
                    $"задача «Агрессия»: подкрепление идёт к {task.TargetArmy.Name}");
                return AiDecision.Move(task.Army, moveTarget, task, AiConfig.Current.aggressionBaseWeight);
            }

            return AiDecision.ReinforceSwap(task, AiConfig.Current.aggressionBaseWeight + AiConfig.Current.raidCommittedBonus);
        }

        // ---- Execution ----

        // Агрессия · сборка состава с нуля, шаг 1 — see AiDecision.RequestRaidArmy's own comment.
        public static IEnumerator RequestRaidArmyRoutine(PlayerSetupData player, AiTurnContext ctx)
        {
            HexCoord hex = AiTurnController.GarrisonHexFor(player);
            yield return AiTurnController.PanTo(ctx, hex);

            ArmyData army = ArmyActions.CreateArmy(player, hex, ctx.StartingDeckCatalog?.GetCatalog(player.Faction), ctx.HexSelection);
            AiDebugLog.Write(army != null
                ? $"[AI] {player.Nickname}: задача «Агрессия» — создаёт пустую армию {army.Name} под сборку рейда."
                : $"[AI] {player.Nickname}: задача «Агрессия» — не хватило AP на новую армию под сборку.");

            yield return AiTurnController.WaitStep(ctx);
        }

        // Агрессия · сборка состава с нуля, шаг 2 — see AiDecision.AssembleRaidForce's own
        // comment. `source` may itself go empty once its recruit leaves (a solo recalled idle
        // army, unlike garrison stock which never runs out this easily) — cleaned up the same way
        // AiManagementPlanner.ConsolidateUnitsRoutine already does, rather than leaving an empty
        // husk army behind.
        public static IEnumerator AssembleRaidForceRoutine(PlayerSetupData player, AiDecision decision, AiTurnContext ctx)
        {
            ArmyData source = decision.ExistingArmy;
            ArmyData formingArmy = decision.MergeTarget;
            UnitData unit = decision.CollectorUnit;
            yield return AiTurnController.PanTo(ctx, source.Hex);

            if (ArmyActions.TransferMember(unit, source, formingArmy, ctx.HexSelection, out string failReason))
            {
                AiDebugLog.Write($"[AI] {player.Nickname}: {decision.Reason}.");
                if (!source.IsGarrison)
                    ctx.HexSelection?.DeleteArmyIfEmptied(source);
                // Feeds the cross-category oscillation guard (see AiTurnContext.WouldRevisitArmy's
                // own comment) — same "only a landed move counts" rule ConsolidateUnitsRoutine follows.
                ctx.RecordArmyVisit(unit, source, formingArmy);
            }
            else
            {
                AiDebugLog.Write($"[AI] {player.Nickname}: не смог собрать состав «Агрессия» — {failReason}");
            }

            if (ctx.ArmyViewerModal != null)
                ctx.ArmyViewerModal.ShowReadOnly(formingArmy);
            yield return AiTurnController.WaitStep(ctx);
        }

        // Постбоевая переоценка, шаг 1 — single-step create+populate, same shape as
        // AiEconomyPlanner.DetachCollectorRoutine's own from-scratch branch (no separate "spawn an
        // empty army, recruit into it next step" dance needed here — this task never needs the
        // fresh army to exist before this exact step). Fills in decision.Task.Army once the courier
        // is real — the task was already registered by AiTurnController.Commit's own generic code
        // with Army still null; AiTask is a plain mutable class, so setting it here afterward is
        // safe (nothing reads task.Army between registration and this routine actually running).
        public static IEnumerator DispatchReinforcementRoutine(PlayerSetupData player, AiDecision decision, AiTurnContext ctx)
        {
            ArmyData garrison = decision.ExistingArmy;
            UnitData recruit = decision.CollectorUnit;
            yield return AiTurnController.PanTo(ctx, garrison.Hex);

            ArmyData courier = ArmyActions.CreateArmy(player, garrison.Hex, ctx.StartingDeckCatalog?.GetCatalog(player.Faction), ctx.HexSelection);
            if (courier == null)
            {
                AiDebugLog.Write($"[AI] {player.Nickname}: не хватило AP на курьера для {decision.Task.TargetArmy.Name}.");
                AiTaskRegistry.Remove(player, decision.Task);
                yield break;
            }

            if (ArmyActions.TransferMember(recruit, garrison, courier, ctx.HexSelection, out string failReason))
            {
                decision.Task.Army = courier;
                AiDebugLog.Write($"[AI] {player.Nickname}: {decision.Reason}.");
            }
            else
            {
                AiDebugLog.Write($"[AI] {player.Nickname}: не смог отправить курьера — {failReason}");
                AiTaskRegistry.Remove(player, decision.Task);
            }

            yield return AiTurnController.WaitStep(ctx);
        }

        // Постбоевая переоценка, шаг 2 — courier just arrived at the wounded army's own hex.
        // Wounded-out-first, recruit-in-second (see this class's own plan notes on why: pulling the
        // critically wounded members out of TargetArmy FIRST frees the capacity slots the incoming
        // recruit needs, rather than briefly exceeding Capacity by trying it the other way around).
        // TargetArmy (now stronger, still fielded) is left exactly where it stands — the next step's
        // TryRaidAssembleCandidates/TryRaidRegroupCandidates re-evaluate it fresh like any other
        // idle army. Army (now carrying only the rescued wounded) is left idle too — TryRaidRegroup
        // Candidates picks it up again next step, most likely choosing "go home" now that it's
        // sitting right where the rendezvous was.
        public static IEnumerator ReinforceSwapRoutine(PlayerSetupData player, AiDecision decision, AiTurnContext ctx)
        {
            AiTask task = decision.Task;
            ArmyData courier = task.Army;
            ArmyData wounded = task.TargetArmy;
            yield return AiTurnController.PanTo(ctx, courier.Hex);

            List<UnitData> criticallyWounded = wounded.Members.Where(m => !m.IsHero && m.HitPointsCurrent <= m.HitPointsMax / 2).ToList();
            int movedOut = 0;
            foreach (UnitData member in criticallyWounded)
            {
                if (ArmyActions.TransferMember(member, wounded, courier, ctx.HexSelection, out string failReason))
                    movedOut++;
                else
                    AiDebugLog.Write($"[AI] {player.Nickname}: не смог перевести {member.Name} в курьера — {failReason}");
            }

            List<UnitData> recruits = courier.Members.Where(m => m != null && !criticallyWounded.Contains(m)).ToList();
            int movedIn = 0;
            foreach (UnitData recruit in recruits)
            {
                if (ArmyActions.TransferMember(recruit, courier, wounded, ctx.HexSelection, out string failReason))
                    movedIn++;
                else
                    AiDebugLog.Write($"[AI] {player.Nickname}: не смог влить {recruit.Name} в {wounded.Name} — {failReason}");
            }

            AiDebugLog.Write($"[AI] {player.Nickname}: {courier.Name} встретился с {wounded.Name} — "
                + $"{movedOut} раненых на выход, {movedIn} подкрепления влилось.");
            AiTaskRegistry.Remove(player, task);

            if (ctx.ArmyViewerModal != null)
                ctx.ArmyViewerModal.Hide();
            yield return AiTurnController.WaitStep(ctx);
        }
    }
}
