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
    // Level-1 category for Экономика (AiTaskCategory.Economy) — actor lookups here are per-task
    // (FindNearestHero for BuildFacilityTask, FindNearestSoloCollector/FindCollectorDetachPlan for
    // ResourcesScrapTask). Also holds the continue/start/return-home orchestration
    // AiTurnController.Decide gathers candidates from each step, same role AiScoutPlanner/
    // AiManagementPlanner/AiAggressionPlanner play for their own categories — task-specific
    // behavior (composition eligibility, concrete hex scoring) lives one level down, in
    // BuildFacilityTask/ResourcesScrapTask. Also holds this category's own execution routines
    // (BuildFacilityRoutine/DetachCollectorRoutine) — see AiTurnController's own class comment on
    // why execution is split the same way planning is, except for the couple of AiActionKinds
    // (MoveArmy/PlayCard) genuinely shared across categories.
    public static class AiEconomyPlanner
    {
        // See AiConfig.maxBuildAttempts — moved there so it's tunable without recompiling.
        public static int MaxBuildAttempts => AiConfig.Current.maxBuildAttempts;

        // "герой с армией или без (разведчик)" — the project owner's own Задача 1 spec: any
        // hero-led army qualifies (bare, Recce-carrying, or already escorted — only being a hero
        // at all matters, since only a hero can build an extraction facility), nearest to
        // `targetHex` wins. Deliberately NOT restricted to idle armies — AiTurnController's own
        // preemption path (see its class comment) is what lets this reach into an army another
        // task already claimed, per the owner's own "нужно брать ближайшего героя ... его
        // текущее задание можно отложить" call.
        public static ArmyData FindNearestHero(PlayerSetupData player, HexCoord targetHex)
        {
            ArmyData best = null;
            int bestDistance = int.MaxValue;
            foreach (ArmyData army in ArmyRegistry.AllForOwner(player))
            {
                if (!AiArmyRoles.IsHeroLed(army))
                    continue;
                int distance = HexGridMath.Distance(army.Hex, targetHex);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = army;
                }
            }
            return best;
        }

        // FindNearestHero's own blind spot — AiArmyRoles.IsHeroLed always excludes IsGarrison, so
        // a hero currently folded into the literal Garrison stockpile (see AiManagementPlanner.
        // FindConsolidationMove's own "lone hero waits for an escort" branch, or a Hero card
        // deposited straight into garrison per AiManagementPlanner.FindPlacement) never turns up
        // there even when it's the actual closest hero to `targetHex` (the project owner's own
        // "герой застрял в гарнизоне, невидим для стройки" report). Compares FindNearestHero's
        // own best pick against the garrison's own distance and returns whichever is closer.
        // GarrisonHero non-null means the caller must detach that specific unit out of Garrison
        // first (see AiTurnController.TryStartEconomyCandidates) before it can lead an Economy
        // trip; Army non-null means it's already its own force, ready to go as-is; both null means
        // no hero exists anywhere for this player.
        public readonly struct NearestHeroPick
        {
            public readonly ArmyData Army;
            public readonly UnitData GarrisonHero;
            public readonly ArmyData Garrison;

            public NearestHeroPick(ArmyData army, UnitData garrisonHero, ArmyData garrison)
            {
                Army = army;
                GarrisonHero = garrisonHero;
                Garrison = garrison;
            }
        }

        public static NearestHeroPick FindNearestHeroAnywhere(PlayerSetupData player, HexCoord targetHex)
        {
            ArmyData bestArmy = FindNearestHero(player, targetHex);
            int bestDistance = bestArmy != null ? HexGridMath.Distance(bestArmy.Hex, targetHex) : int.MaxValue;

            ArmyData garrison = ArmyRegistry.AllForOwner(player).FirstOrDefault(a => a.IsGarrison);
            UnitData garrisonHero = garrison?.Members.FirstOrDefault(m => m.IsHero);
            if (garrisonHero != null && HexGridMath.Distance(garrison.Hex, targetHex) < bestDistance)
                return new NearestHeroPick(null, garrisonHero, garrison);

            return new NearestHeroPick(bestArmy, null, null);
        }

        // Whichever ResourceType the hex's own bonus yields the most of — ties broken by enum
        // order, which is fine since a hex only ever carries one meaningful bonus in practice.
        // Null if the hex carries no bonus at all (shouldn't happen for a target ScoreExpandEconomy
        // itself picked, but callers shouldn't assume that without checking).
        public static ResourceType? DominantResourceType(HexCoord hex)
        {
            ResourceYields bonus = HexResourceBonusRegistry.GetBonus(hex);
            if (bonus == null)
                return null;

            ResourceType best = ResourceType.Human;
            int bestAmount = 0;
            foreach (ResourceType type in (ResourceType[])Enum.GetValues(typeof(ResourceType)))
            {
                int amount = bonus.Get(type);
                if (amount > bestAmount)
                {
                    bestAmount = amount;
                    best = type;
                }
            }
            return bestAmount > 0 ? best : (ResourceType?)null;
        }

        // Экономика · Задача 2's own "who does this task" half — a unit carrying the matching
        // CollectX ability (Game.Cards.UnitAbilities.CollectHuman/Energy/Materials/Tech, same
        // tag a Facility's own ability uses — see GameTurnController.CollectArmyIncomeAt, the
        // passive per-turn payout this task's whole point is to go stand on) already alone in its
        // own army — no detach needed, just walk it there. `pool` so an army another task already
        // claimed this step is never double-booked (see AiResourcePool's own class comment).
        public static ArmyData FindNearestSoloCollector(PlayerSetupData player, HexCoord targetHex, ResourceType type,
            AiResourcePool pool)
        {
            string ability = UnitAbilities.CollectAbilityFor(type);
            ArmyData best = null;
            int bestDistance = int.MaxValue;
            foreach (ArmyData army in pool.AvailableArmies())
            {
                if (army.IsGarrison || army.IsPrison || army.Members.Count != 1 || !army.Members[0].HasAbility(ability))
                    continue;
                int distance = HexGridMath.Distance(army.Hex, targetHex);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = army;
                }
            }
            return best;
        }

        // Экономика · Задача 2's own prerequisite step whenever no ready solo collector exists yet
        // (see FindNearestSoloCollector) — the project owner's own two ways to end up with an army
        // holding just the collector: two of the player's own armies already share a hex and
        // "reform" so the collector's escort moves OUT into the other one (MergeTarget != null,
        // Source itself becomes the solo army — nothing new spawns), or Source is sitting at the
        // player's own base/garrison hex and the collector alone splits OUT into a freshly created
        // army instead (MergeTarget == null — same shape as AiManagementPlanner.
        // FindGarrisonOverflow's own split, just for one specific unit rather than the overflow
        // tail). `pool` so a source mid-Разведка/Экономика this step is never touched.
        public readonly struct CollectorDetachPlan
        {
            public readonly ArmyData Source;
            public readonly UnitData Unit;
            public readonly ArmyData MergeTarget;

            public CollectorDetachPlan(ArmyData source, UnitData unit, ArmyData mergeTarget)
            {
                Source = source;
                Unit = unit;
                MergeTarget = mergeTarget;
            }
        }

        public static CollectorDetachPlan? FindCollectorDetachPlan(PlayerSetupData player, ResourceType type,
            HexCoord garrisonHex, AiResourcePool pool)
        {
            string ability = UnitAbilities.CollectAbilityFor(type);
            foreach (ArmyData source in pool.AvailableArmies())
            {
                if (source.IsPrison)
                    continue;
                UnitData unit = source.Members.FirstOrDefault(m => m.HasAbility(ability));
                if (unit == null || source.Members.Count == 1)
                    continue; // already solo — FindNearestSoloCollector's own job, not a detach

                int escortCount = source.Members.Count - 1;
                ArmyData mergeTarget = ArmyRegistry.AllForOwner(player).FirstOrDefault(a =>
                    a != source && !a.IsPrison && a.Hex.Equals(source.Hex) && a.Capacity - a.Members.Count >= escortCount);
                if (mergeTarget != null)
                    return new CollectorDetachPlan(source, unit, mergeTarget);

                if (source.Hex.Equals(garrisonHex))
                    return new CollectorDetachPlan(source, unit, null);
            }
            return null;
        }

        // ---- Экономика · Задача 1 (Постройка добывающей facility) ----

        // "если найден хекс с ресурсами, то туда нужно отправить ближайшего героя" — the project
        // owner's own spec: tries every free known resource hex not already targeted by an
        // existing BuildFacility task, and for each, the nearest hero overall (see
        // BuildFacilityTask.FindActor) — INCLUDING a hero another task already claimed. A
        // candidate for a hero already mid-Разведка carries PreemptedTask so AiTurnController.
        // Decide's own Commit step can drop that old task IF (and only if) this specific candidate
        // ends up winning the step's arbitration — "его текущее задание можно отложить в угоду
        // экономии времени... а потом вернуться к отложенному заданию (которое будет помечено как
        // невыполненное)".
        //
        // A hero already on a DIFFERENT BuildFacility task is normally never offered a candidate
        // here — that would only shuffle the same work around. The one exception: a candidate hex
        // whose own resource type is currently MORE scarce than what that hero is already building
        // (see BuildFacilityTask.ScoreHex's own scarcity term) is still offered, so the project
        // owner's own deficit-switch rule ("может сменить цель постройки ... если найдётся
        // дефицитный ресурс") can fire through this exact same preemption path. Nothing is
        // actually lost by switching — a reservation is just an accounting claim (see
        // AiResourceReservation's own class comment), released in Commit like any other preempted
        // task, not a real spend.
        public static List<AiDecision> TryStartEconomyCandidates(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
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
                ResourceType? resourceType = DominantResourceType(hex);
                if (resourceType == null)
                    continue;

                NearestHeroPick pick = BuildFacilityTask.FindActor(player, hex);
                if (pick.GarrisonHero != null)
                {
                    // Closest hero to this hex is stuck inside the Garrison stockpile (see
                    // FindNearestHeroAnywhere's own comment) — detach it into its own fresh army
                    // first, same two-step shape TryStartCollectorDetachCandidates already uses
                    // for Задача 2's own solo-collector prep. The NEXT Decide() step finds the
                    // resulting hero-led army through the normal FindNearestHero path and picks
                    // up the actual build/travel from there.
                    // Same HasEnemyThreat gate BuildFacilityTask already applies to the TARGET hex
                    // — a hero stepping out of the garrison solo, escort-less, is exactly as
                    // vulnerable as one already travelling, so a known enemy sitting right next to
                    // the garrison itself must block the detach too (the project owner's own
                    // "герой застрял в гарнизоне... опять таки имеет смысл если рядом нет врагов"
                    // qualifier) — better left safely stockpiled than handed to the enemy.
                    if (root.CanSpendActionPoints(ArmyActions.CreateArmyApCost)
                        && !BuildFacilityTask.HasEnemyThreat(player, pick.Garrison.Hex))
                        results.Add(AiDecision.SplitGarrison(pick.Garrison, new[] { pick.GarrisonHero }, null,
                            AiConfig.Current.economyHeroDetachScore,
                            $"задача «Экономика»: {pick.GarrisonHero.Name} — забирается из гарнизона, чтобы возглавить стройку у ({hex.Q},{hex.R})"));
                    continue;
                }

                ArmyData hero = pick.Army;
                if (hero == null || stuckScouts.Contains(hero))
                    continue;

                AiTask existingTask = AiTaskRegistry.TaskFor(player, hero);
                if (existingTask != null && existingTask.Kind == AiTaskKind.BuildFacility)
                {
                    bool isScarcer = existingTask.ResourceType.HasValue
                        && root.GetResource(resourceType.Value) < root.GetResource(existingTask.ResourceType.Value);
                    if (!isScarcer)
                        continue; // already building elsewhere and not scarcer — would only shuffle work, not add any
                }

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
        // touches it). Checked first in AiTurnController.Decide's own continuation order — a task
        // already standing at its target should finish building immediately, not wait behind
        // Разведка. Also doubles as TryStartEconomyCandidates' own first-turn gate — a brand new
        // task is run through here immediately, so the safety/reservation checks below apply
        // identically whether this is turn one or turn ten of the task.
        public static AiDecision AdvanceEconomyTask(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx, AiTask task)
        {
            if (task.Army?.Controller == null || !ArmyRegistry.AllForOwner(player).Contains(task.Army))
            {
                // Bound army is gone (lost in combat, etc.) — self-heal rather than leave a dead
                // reference parked in the registry forever; a fresh task can start again later.
                AiResourceReservation.Release(task);
                AiTaskRegistry.Remove(player, task);
                return null;
            }

            // Both threat checks are a hard cancel now, not a temporary retreat (unlike Разведка's
            // own tasks) — a BuildFacility hero has nothing better to fall back to mid-build, and
            // a neutral guarding the hex was never a good build spot to begin with (see
            // BuildFacilityTask's own class comment). Re-checked every call — a threat wandering
            // within range AFTER the task started cancels it outright, same shape
            // TryStartEconomyCandidates' own filtering effectively gets for free (it calls this
            // same method on a brand-new task), so a better, unguarded hex gets tried next step.
            if (BuildFacilityTask.HasNeutralThreat(player, task.TargetHex))
            {
                if (AiTaskRegistry.TasksFor(player).Contains(task))
                    AiDebugLog.Write($"[AI] {player.Nickname}: {task.Army.Name} — известная нейтральная армия в "
                        + $"{AiConfig.Current.neutralBuildAvoidRadius} хексах от ({task.TargetHex.Q},{task.TargetHex.R}), "
                        + "задача «Экономика» отменена.");
                AiResourceReservation.Release(task);
                AiTaskRegistry.Remove(player, task);
                return null;
            }
            if (BuildFacilityTask.HasEnemyThreat(player, task.TargetHex))
            {
                if (AiTaskRegistry.TasksFor(player).Contains(task))
                    AiDebugLog.Write($"[AI] {player.Nickname}: {task.Army.Name} — известная вражеская армия в "
                        + $"{AiConfig.Current.economySafetyRadius} хексах от ({task.TargetHex.Q},{task.TargetHex.R}), "
                        + "задача «Экономика» отменена.");
                AiResourceReservation.Release(task);
                AiTaskRegistry.Remove(player, task);
                return null;
            }

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
                float score = AiConfig.Current.economyBaseWeight
                    + BuildFacilityTask.ScoreHex(player, root, task.TargetHex, task.ResourceType.Value);
                return AiDecision.Move(task.Army, target, task, score);
            }

            if (definition == null)
                return null;

            // Belt-and-suspenders on top of IsFullyReserved's own virtual ledger: a direct read of
            // PlayerRoot's real stockpile, the exact same check TryBuildExtractionFacility itself
            // makes (see ResourceCost.CanAfford) — so the AI never even attempts a build the game
            // would actually reject. Per the project owner's own report, the ledger check alone
            // still let a build attempt through short on real resources (task.BuildAttempts
            // ticking up for no reason visible in the reservation log); simplest fix is refusing
            // to build unless BOTH agree, and just waiting on the hex instead.
            bool shortOnResources = !AiResourceReservation.IsFullyReserved(task, definition.resourceCost)
                || !definition.resourceCost.CanAfford(root);
            bool shortOnAp = !root.CanSpendActionPoints(definition.apCost);

            // Checked and reported independently (not "AP first, resources only if AP was fine")
            // — otherwise a step where something else already spent this turn's AP masks a
            // resource shortfall that was there the whole time, misreporting the real blocker as
            // AP when materials/energy/human were actually the reason the build couldn't happen
            // yet (see the project owner's own report of exactly this from the debug log).
            if (shortOnResources || shortOnAp)
            {
                string reason = shortOnResources && shortOnAp
                    ? "копит ресурсы и не хватает AP"
                    : shortOnResources ? "копит ресурсы" : "не хватает AP";
                return AiDecision.Wait(task, $"задача «Экономика»: {task.Army.Name} на месте, {reason} "
                    + $"чтобы построить {task.ResourceType} на ({task.TargetHex.Q},{task.TargetHex.R}) — ждёт");
            }

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

        // Экономика · Задача 1's own "nothing left to build" fallback — a hero-led army with no
        // active task, sitting somewhere other than the garrison, while BuildFacilityTask.
        // HasAnythingToBuild says there's no known free resource hex left anywhere to build on.
        // Same idea as AiScoutPlanner.TryReturnHomeCandidates just above, own dedicated score
        // (economyReturnHomeScore) since the two situations aren't equally urgent — a fragile
        // escort-less hero protects itself first (see managementReturnHomeScore's own comment).
        public static List<AiDecision> TryEconomyReturnHomeCandidates(PlayerSetupData player, PlayerRoot root,
            HashSet<ArmyData> stuckScouts)
        {
            var results = new List<AiDecision>();
            HexCoord garrisonHex = AiTurnController.GarrisonHexFor(player);
            if (BuildFacilityTask.HasAnythingToBuild(player))
                return results;

            foreach (ArmyData army in ArmyRegistry.AllForOwner(player))
            {
                if (!BuildFacilityTask.IsEligibleComposition(army) || army.CurrentMovement <= 0 || army.Controller == null
                    || stuckScouts.Contains(army) || army.Hex.Equals(garrisonHex) || AiTaskRegistry.TaskFor(player, army) != null)
                    continue;
                if (!army.HasActivatedThisTurn && !root.CanSpendActionPoints(army.ActivationApCost))
                    continue;
                var target = new AiScoutPlanner.ScoutTarget(garrisonHex, 0f,
                    "строить больше нечего — возвращается в цитадель");
                results.Add(AiDecision.Move(army, target, null, AiConfig.Current.economyReturnHomeScore));
            }
            return results;
        }

        // ---- Экономика · Задача 2 (Добыча без постройки facility) ----

        // "если найден такой хекс и такой юнит есть в составе одной из армий" — the project
        // owner's own spec: an already-solo collector (see ResourcesScrapTask.FindActor)
        // walks straight to the nearest still-unclaimed free known resource hex matching its own
        // ability, no BuildingRegistry occupancy check at all (unlike Задача 1 — sharing a hex with
        // a BuildFacility hero, or even that hex's own future facility, is fine; see
        // AdvanceResourcesScrapTask's own comment on why the two tasks coexist on purpose).
        // ResourceScrapBaseWeight alone (no readiness bonus like BuildFacilityReadyBonus) since
        // there's no separate "arrived, now commit" step — arriving IS the whole task.
        public static List<AiDecision> TryStartResourcesScrapCandidates(PlayerSetupData player, AiResourcePool pool)
        {
            var results = new List<AiDecision>();
            var alreadyTargeted = new HashSet<HexCoord>(AiTaskRegistry.TasksFor(player)
                .Where(t => t.Kind == AiTaskKind.ResourcesScrap)
                .Select(t => t.TargetHex));

            foreach (HexCoord hex in HexResourceBonusRegistry.AllBonusHexes())
            {
                if (!AiMapMemory.IsResourceHexKnown(player, hex) || alreadyTargeted.Contains(hex))
                    continue;
                ResourceType? resourceType = DominantResourceType(hex);
                if (resourceType == null || ResourcesScrapTask.HasExtractionFacility(hex, resourceType.Value))
                    continue;

                ArmyData collector = ResourcesScrapTask.FindActor(player, hex, resourceType.Value, pool);
                if (collector == null || collector.CurrentMovement <= 0)
                    continue;

                var task = new AiTask { Kind = AiTaskKind.ResourcesScrap, Army = collector, TargetHex = hex, ResourceType = resourceType };
                var target = new AiScoutPlanner.ScoutTarget(hex, 0f,
                    $"задача «Экономика»: {collector.Name} идёт добывать {resourceType} на ({hex.Q},{hex.R}) без стройки");
                float score = AiConfig.Current.ResourceScrapBaseWeight + ResourcesScrapTask.ScoreHex(player);
                results.Add(AiDecision.Move(collector, target, task, score));
            }
            return results;
        }

        // Экономика · Задача 2's own prep step — only offered once TryStartResourcesScrapCandidates
        // itself finds no ready solo collector for a given hex's type, so the two tiers never both
        // fire for the same hex the same step. See FindCollectorDetachPlan for the two ways this
        // can resolve; a fresh-army plan additionally needs CreateArmyApCost checked here (the
        // planner itself is pure/AP-agnostic, same "checked before proposing" rule every other
        // candidate in this file follows).
        public static List<AiDecision> TryStartCollectorDetachCandidates(PlayerSetupData player, PlayerRoot root, AiResourcePool pool)
        {
            var results = new List<AiDecision>();
            var alreadyTargeted = new HashSet<HexCoord>(AiTaskRegistry.TasksFor(player)
                .Where(t => t.Kind == AiTaskKind.ResourcesScrap)
                .Select(t => t.TargetHex));
            HexCoord garrisonHex = AiTurnController.GarrisonHexFor(player);

            foreach (HexCoord hex in HexResourceBonusRegistry.AllBonusHexes())
            {
                if (!AiMapMemory.IsResourceHexKnown(player, hex) || alreadyTargeted.Contains(hex))
                    continue;
                ResourceType? resourceType = DominantResourceType(hex);
                if (resourceType == null || ResourcesScrapTask.HasExtractionFacility(hex, resourceType.Value))
                    continue;
                if (ResourcesScrapTask.FindActor(player, hex, resourceType.Value, pool) != null)
                    continue; // already have one ready to walk — nothing to detach

                CollectorDetachPlan? plan = FindCollectorDetachPlan(player, resourceType.Value, garrisonHex, pool);
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
        public static AiDecision AdvanceResourcesScrapTask(PlayerSetupData player, PlayerRoot root, AiTask task)
        {
            if (task.Army?.Controller == null || !ArmyRegistry.AllForOwner(player).Contains(task.Army))
            {
                AiTaskRegistry.Remove(player, task);
                return null;
            }

            if (ResourcesScrapTask.HasEnemyThreat(player, task.TargetHex))
            {
                AiDebugLog.Write($"[AI] {player.Nickname}: {task.Army.Name} — известная армия в {AiConfig.Current.economySafetyRadius} "
                    + $"хексах от ({task.TargetHex.Q},{task.TargetHex.R}), задача «Экономика» (добыча) отменена.");
                AiTaskRegistry.Remove(player, task);
                return null;
            }

            if (task.ResourceType.HasValue && ResourcesScrapTask.HasExtractionFacility(task.TargetHex, task.ResourceType.Value))
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
                float score = AiConfig.Current.ResourceScrapBaseWeight + ResourcesScrapTask.ScoreHex(player);
                return AiDecision.Move(task.Army, target, task, score);
            }

            return null; // arrived and collecting — nothing left to decide, see this method's own comment
        }

        // ---- Execution ----

        // Экономика · Задача 1's own last stage — the hero has already arrived (see
        // AdvanceEconomyTask), so this just calls the same HexSelectionController.
        // TryBuildExtractionFacility a human clicking the resource-action button would, and either
        // closes out the task or counts a failed attempt toward MaxBuildAttempts (see AiTask's own
        // comment on why that's a blunt safeguard rather than a precise diagnosis).
        public static IEnumerator BuildFacilityRoutine(PlayerSetupData player, AiDecision decision, AiTurnContext ctx)
        {
            AiTask task = decision.Task;
            ArmyData army = task?.Army;
            if (army?.Controller == null || ctx.GameConfig?.extractionFacilityCards == null || !task.ResourceType.HasValue)
            {
                AiResourceReservation.Release(task);
                AiTaskRegistry.Remove(player, task);
                yield break;
            }

            yield return AiTurnController.PanTo(ctx, army.Hex);
            if (ctx.ArmyViewerModal != null)
                ctx.ArmyViewerModal.ShowReadOnly(army);
            yield return AiTurnController.WaitStep(ctx);

            CardDefinition definition = (int)task.ResourceType.Value < ctx.GameConfig.extractionFacilityCards.Length
                ? ctx.GameConfig.extractionFacilityCards[(int)task.ResourceType.Value]
                : null;
            PlayerRoot root = PlayerRootRegistry.FindFor(player);
            int ap0 = root != null ? root.ActionPoints : 0;
            int human0 = root != null ? root.GetResource(ResourceType.Human) : 0;
            int energy0 = root != null ? root.GetResource(ResourceType.Energy) : 0;
            int materials0 = root != null ? root.GetResource(ResourceType.Materials) : 0;
            int tech0 = root != null ? root.GetResource(ResourceType.Tech) : 0;
            bool built = definition != null && ctx.HexSelection.TryBuildExtractionFacility(definition, task.TargetHex, player);
            if (built)
            {
                string delta = AiTurnController.ResourceDeltaSuffix(root, ap0, human0, energy0, materials0, tech0);
                AiDebugLog.Write($"[AI] {player.Nickname}: {army.Name} построил объект добычи {task.ResourceType} "
                    + $"на ({task.TargetHex.Q},{task.TargetHex.R}) — задача «Экономика» завершена.{delta}");
                AiResourceReservation.Release(task);
                AiTaskRegistry.Remove(player, task);
            }
            else
            {
                task.BuildAttempts++;
                AiDebugLog.Write($"[AI] {player.Nickname}: не смог построить {task.ResourceType} "
                    + $"на ({task.TargetHex.Q},{task.TargetHex.R}) — попытка {task.BuildAttempts}.");
                if (task.BuildAttempts >= MaxBuildAttempts)
                {
                    AiDebugLog.Write($"[AI] {player.Nickname}: отказывается от задачи «Экономика» на "
                        + $"({task.TargetHex.Q},{task.TargetHex.R}) после {task.BuildAttempts} неудачных попыток.");
                    AiResourceReservation.Release(task);
                    AiTaskRegistry.Remove(player, task);
                }
            }

            if (ctx.ArmyViewerModal != null)
                ctx.ArmyViewerModal.Hide();
            yield return AiTurnController.WaitStep(ctx);
        }

        // Экономика · Задача 2's own prep step — see CollectorDetachPlan's own comment for the two
        // shapes decision.MergeTarget can take. Either way ExistingArmy (Source) keeps
        // CollectorUnit and becomes the solo army TryStartResourcesScrapCandidates picks up next
        // step; only the OTHER members ever move.
        public static IEnumerator DetachCollectorRoutine(PlayerSetupData player, AiDecision decision, AiTurnContext ctx)
        {
            ArmyData source = decision.ExistingArmy;
            UnitData collector = decision.CollectorUnit;
            yield return AiTurnController.PanTo(ctx, source.Hex);

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
            yield return AiTurnController.WaitStep(ctx);
        }
    }
}
