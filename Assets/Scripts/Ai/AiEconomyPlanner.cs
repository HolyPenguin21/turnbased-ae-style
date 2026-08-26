using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
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
        public static int MaxBuildAttempts => AiConfig.maxBuildAttempts;

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
                if (!AiArmyRoles.IsHeroLed(army) || IsProtectedFromEconomyPreemption(player, army))
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

        // Экономика · Задача 1's own preemption path (see this method's own class comment above,
        // and TryStartEconomyCandidates' own PreemptedTask handling) may still reach into a hero
        // currently on routine Разведка — "его текущее задание можно отложить" — but NOT into an
        // active Агрессия campaign or Оборона's own citadel task (2026-08-23, project owner's own
        // call: "BuildFacility запрещено preempt'ить активные Raid, BuildBase и DefendCitadel;
        // разрешён preempt Recon"). RaidWeakerArmy/BuildBase represent real committed military
        // investment — an assembling/travelling raid force, a base under active construction — and
        // DefendCitadel's own hero-led defender is the one actually holding the capital; none of the
        // three must ever be interrupted by a routine facility build, unlike a scout that simply
        // resumes VisitHex once un-preempted.
        // Checked centrally here (both FindNearestHero's own caller AND BuildFacilityTask.RankHex's
        // internal HeroTravelCostScore route through this same method) rather than only at
        // TryStartEconomyCandidates' own PreemptedTask assignment, so a protected hero never even
        // gets picked as the "nearest" one in the first place — RankHex's own hex ranking stays
        // consistent with which hero would actually be usable if that hex won.
        private static bool IsProtectedFromEconomyPreemption(PlayerSetupData player, ArmyData army)
        {
            AiTask task = AiTaskRegistry.TaskFor(player, army);
            return task != null && (task.Kind == AiTaskKind.RaidWeakerArmy || task.Kind == AiTaskKind.BuildBase
                || task.Kind == AiTaskKind.DefendCitadel);
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
                if (army.IsGarrison || army.IsPrison || army.Members.Count != 1
                    || army.Members[0].IsAviation || !army.Members[0].HasAbility(ability))
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

        // See FindCollectorDetachPlan below — Source is the (still multi-member) army Unit is
        // currently sitting in, MergeTarget is where its escort goes once Unit is pulled out (null
        // means "spin up a fresh army instead", see AiDecision.DetachCollector's own comment).
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

        // Экономика · Задача 2's own prerequisite step whenever no ready solo collector exists yet
        // (see FindNearestSoloCollector) — the project owner's own two ways to end up with an army
        // holding just the collector: two of the player's own armies already share the GARRISON
        // hex and "reform" so the collector's escort moves OUT into the other one (MergeTarget !=
        // null, Source itself becomes the solo army — nothing new spawns), or the collector alone
        // splits OUT into a freshly created army instead (MergeTarget == null — same shape as
        // AiManagementPlanner.FindGarrisonOverflow's own split, just for one specific unit rather
        // than the overflow tail). `pool` so a source mid-Разведка/Экономика this step is never
        // touched.
        //
        // Restricted to `source.Hex.Equals(garrisonHex)` (project owner's own 2026-08-23 call) —
        // this used to scan EVERY hex where two of the player's own armies happened to share
        // ground, which meant a real, active field army (mid-raid, mid-scout, anywhere at all)
        // could get its own collector yanked out from under it the instant it merged with another
        // friendly army for any reason. A collector only ever gets detached from inside the actual
        // barracks/garrison hex now, same "hex with Barracks" concept AiTurnController.
        // GarrisonHexFor already names. Combined with the AiTaskRegistry.TaskFor exclusion below
        // (an active army is never even a CANDIDATE source, not just deprioritized — see
        // TryStartCollectorDetachCandidates' own comment on why the old penalty-only approach
        // wasn't enough), an army genuinely off doing something stays untouchable either way.
        public static CollectorDetachPlan? FindCollectorDetachPlan(PlayerSetupData player, ResourceType type,
            HexCoord garrisonHex, AiResourcePool pool)
        {
            string ability = UnitAbilities.CollectAbilityFor(type);
            foreach (ArmyData source in pool.AvailableArmies())
            {
                if (source.IsPrison || source.IsAirfield || AviationRules.IsAirArmy(source)
                    || !source.Hex.Equals(garrisonHex) || AiTaskRegistry.TaskFor(player, source) != null)
                    continue;
                UnitData unit = source.Members.FirstOrDefault(m => !m.IsAviation && m.HasAbility(ability));
                if (unit == null || source.Members.Count == 1)
                    continue; // already solo — FindNearestSoloCollector's own job, not a detach

                int escortCount = source.Members.Count - 1;
                ArmyData mergeTarget = ArmyRegistry.AllForOwner(player).FirstOrDefault(a =>
                    a != source && !a.IsPrison && !a.IsAirfield && !AviationRules.IsAirArmy(a)
                    && a.Hex.Equals(garrisonHex) && a.Capacity - a.Members.Count >= escortCount);
                if (mergeTarget != null)
                    return new CollectorDetachPlan(source, unit, mergeTarget);

                return new CollectorDetachPlan(source, unit, null);
            }
            return null;
        }

        // ---- Экономика · Задача 1 (Постройка добывающей facility) ----

        // "если найден хекс с ресурсами, то туда нужно отправить ближайшего героя" — the project
        // owner's own spec. Restructured 2026-08-19: first picks a single best known free resource
        // hex internally (BuildFacilityTask.RankHex — scarcity/no-income pressure, never exposed to
        // Decide, see that method's own comment), THEN runs the usual actor lookup for that one hex
        // alone (BuildFacilityTask.FindActor) — INCLUDING a hero another task already claimed. A
        // candidate for a hero already mid-Разведка carries PreemptedTask so AiTurnController.
        // Decide's own Commit step can drop that old task IF (and only if) this specific candidate
        // ends up winning the step's arbitration — "его текущее задание можно отложить в угоду
        // экономии времени... а потом вернуться к отложенному заданию (которое будет помечено как
        // невыполненное)".
        //
        // A hero already on a DIFFERENT BuildFacility task is normally never offered a candidate
        // here — that would only shuffle the same work around. The one exception: the internally-
        // picked hex is currently MORE scarce than what that hero is already building (see
        // BuildFacilityTask.RankHex's own scarcity term) — still offered, so the project owner's
        // own deficit-switch rule ("может сменить цель постройки ... если найдётся дефицитный
        // ресурс") can fire through this exact same preemption path. Nothing is actually lost by
        // switching — a reservation is just an accounting claim (see AiResourceReservation's own
        // class comment), released in Commit like any other preempted task, not a real spend.
        public static List<AiDecision> TryStartEconomyCandidates(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            HashSet<ArmyData> stuckScouts, AiResourcePool pool)
        {
            var results = new List<AiDecision>();
            var alreadyTargeted = new HashSet<HexCoord>(AiTaskRegistry.TasksFor(player)
                .Where(t => t.Kind == AiTaskKind.BuildFacility)
                .Select(t => t.TargetHex));
            // "BuildBase и BuildFacility резервируют один хекс разными армиями" fix (2026-08-24,
            // project owner's own report) — a hex an active BuildBase task already has as its own
            // TargetHex is off-limits for a brand-new BuildFacility task too (see
            // AiAggressionPlanner.TryStartBuildBaseCandidates' own PreemptedHexTask handling for the
            // reverse direction: BuildBase is free to claim a hex FROM an existing BuildFacility
            // task, since the base can absorb the hex's resource bonus itself once built). Excluded
            // from the internal ranking pre-pass below, same as alreadyTargeted, rather than
            // filtered later — no reason to ever rank a hex that can't actually be started on.
            alreadyTargeted.UnionWith(AiTaskRegistry.TasksFor(player)
                .Where(t => t.Kind == AiTaskKind.BuildBase)
                .Select(t => t.TargetHex));
            // Caps how many BuildFacility tasks run at once (project owner's own 2026-08-19 call —
            // several concurrent builds were each reserving toward a different resource type,
            // between them locking card play out of all four at once). Checked below, not as a
            // blanket early-return, because the scarcity-switch branch further down (isScarcer)
            // redirects a hero ALREADY on a BuildFacility task rather than adding a new one — that
            // swap must stay exempt or a hero mid-build could never retarget a newly-found scarcer
            // hex once the cap is reached.
            bool atCap = AiTaskRegistry.CountActive(player, AiTaskKind.BuildFacility) >= AiConfig.maxConcurrentBuildFacility;

            // Internal-only pre-pass (project owner's own 2026-08-19 call) — WHICH known free hex
            // to build on next is Economy's own business (scarcity/no-income pressure) and must
            // never leak into the cross-category score every OTHER category's candidate actually
            // competes against — see BuildFacilityTask.RankHex/ScoreHex's own comments. Picks
            // exactly one hex; everything below runs for that hex alone — same "internal scan picks
            // one winner, THEN score against everything else" split VisitHexTask.FindTarget already
            // uses for Разведка.
            HexCoord? bestHex = null;
            ResourceType? bestResourceType = null;
            float bestRank = float.NegativeInfinity;
            foreach (HexCoord candidate in HexResourceBonusRegistry.AllBonusHexes())
            {
                // Memory-based building check (2026-08-24 fix, section 3.2 — see BuildFacilityTask.
                // HasAnythingToBuild's own matching comment), not a live BuildingRegistry read.
                if (!AiMapMemory.IsResourceHexKnown(player, candidate) || AiMapMemory.KnownBuildingAt(player, candidate).HasValue
                    || alreadyTargeted.Contains(candidate))
                    continue;
                // A known neutral within neutralBuildTriggerRadius rules this hex out as a build
                // site before a task is ever started — same radius AdvanceEconomyTask's own cancel
                // check uses (see AiConfig.neutralBuildTriggerRadius's own comment), so a hex that
                // passes here never turns around and cancels itself again right after starting.
                if (BuildFacilityTask.HasAdjacentNeutralThreat(player, candidate))
                    continue;
                // A known REAL enemy within economySafetyRadius of the TARGET hex also rules it out
                // at this same pre-pass stage (2026-08-22, project owner's own call — a hard entry
                // gate, symmetric to the neutral check above, not merely AdvanceEconomyTask's own
                // strength-reactive attack-or-flee once a task is already under way): no point ever
                // registering a task whose own build site a known enemy could immediately threaten.
                if (BuildFacilityTask.HasEnemyThreat(player, candidate))
                    continue;
                ResourceType? candidateType = DominantResourceType(candidate);
                if (candidateType == null)
                    continue;
                float rank = BuildFacilityTask.RankHex(player, root, candidate, candidateType.Value, ctx.Map, pool.Hand);
                if (bestHex == null || rank > bestRank)
                {
                    bestHex = candidate;
                    bestResourceType = candidateType;
                    bestRank = rank;
                }
            }
            if (bestHex == null)
                return results;

            HexCoord hex = bestHex.Value;
            ResourceType resourceType = bestResourceType.Value;

            NearestHeroPick pick = BuildFacilityTask.FindActor(player, hex);
            if (pick.GarrisonHero != null)
            {
                // Closest hero to this hex is stuck inside the Garrison stockpile (see
                // FindNearestHeroAnywhere's own comment) — detach it into its own army first, same
                // two-step shape TryStartCollectorDetachCandidates already uses for Задача 2's own
                // solo-collector prep. The NEXT Decide() step finds the resulting hero-led army
                // through the normal FindNearestHero path and picks up the actual build/travel from
                // there.
                // Same HasEnemyThreat gate BuildFacilityTask already applies to the TARGET hex
                // — a hero stepping out of the garrison solo, escort-less, is exactly as
                // vulnerable as one already travelling, so a known enemy sitting right next to
                // the garrison itself must block the detach too (the project owner's own
                // "герой застрял в гарнизоне... опять таки имеет смысл если рядом нет врагов"
                // qualifier) — better left safely stockpiled than handed to the enemy.
                //
                // Reuses an already-idle, empty deployable army sitting at the garrison hex when one
                // exists — same AiArmyRoles.IsEmptyDeployableArmy scan every other planner's own
                // "forming" lookup already uses (see AiAggressionPlanner/AiDefencePlanner's own
                // TryStart*Candidates) — rather than always spawning a brand-new one (2026-08-21 fix,
                // project owner's own report: a hex that gets picked, detached into, then cancelled
                // again — see AiConfig.neutralBuildTriggerRadius's own comment on the matching radius
                // fix — used to leave a fresh, never-reused empty army shell behind EVERY time,
                // instead of recycling the one the last attempt already abandoned).
                //
                // Scored the same as every other travelling Задача 1 step now (BuildFacilityTask.
                // TravelScore, see economyHeroDetachScore's own removal note in AiConfig) — the
                // base task score alone already reliably wins arbitration, no separate dedicated
                // number needed.
                if (!atCap && root.CanSpendActionPoints(ArmyActions.CreateArmyApCost)
                    && !BuildFacilityTask.HasEnemyThreat(player, pick.Garrison.Hex))
                {
                    ArmyData reuse = pool.AvailableArmies()
                        .FirstOrDefault(a => AiArmyRoles.IsEmptyDeployableArmy(a) && a.Hex.Equals(pick.Garrison.Hex));
                    AiDecision detach = AiDecision.SplitGarrison(pick.Garrison, new[] { pick.GarrisonHero }, reuse,
                        BuildFacilityTask.TravelScore(player, root, hex, resourceType, ctx.Map),
                        $"{pick.GarrisonHero.Name} — pulled out of the garrison to lead a build at ({hex.Q},{hex.R})",
                        AiTaskCategory.Economy);
                    detach.EconomyBuildHex = hex;
                    detach.EconomyResourceType = resourceType;
                    results.Add(detach);
                }
                return results;
            }

            ArmyData hero = pick.Army;
            if (hero == null || stuckScouts.Contains(hero))
                return results;
            // Same hard entry gate as the GarrisonHero branch above (HasEnemyThreat on
            // pick.Garrison.Hex) — a known REAL enemy within economySafetyRadius of the hero's OWN
            // current hex (wherever it's actually standing right now, garrison or already deployed
            // in the field) blocks starting/redirecting a BuildFacility task from here at all.
            if (BuildFacilityTask.HasEnemyThreat(player, hero.Hex))
                return results;

            AiTask existingTask = AiTaskRegistry.TaskFor(player, hero);
            if (existingTask != null && existingTask.Kind == AiTaskKind.BuildFacility)
            {
                // Same target hex as the task this hero already holds → never a real redirect,
                // whatever isScarcer would say — `hex` should already be excluded by
                // `alreadyTargeted` above for exactly this reason, but this is the one place a
                // same-hex "swap" would actually do damage (see Commit's own self-preemption
                // guard for why), so it's re-checked directly here too rather than trusting that
                // upstream exclusion alone. 2026-08-23 fix (project owner's own report): a hero
                // ended up pulled off its own in-progress BuildFacility task and immediately
                // reassigned a brand-new one at the SAME hex, resetting BuildAttempts/
                // StartedWithNoIncome/reservation state for no actual gain.
                if (hex.Equals(existingTask.TargetHex))
                    return results;
                // Commitment margin (2026-08-23, project owner's own report/spec) — a bare
                // isScarcer (any amount scarcer at all) used to redirect a hero away from an
                // in-progress build the instant a marginally scarcer hex turned up, with no regard
                // for how much travel/setup was already sunk into the CURRENT one (the project
                // owner's own report: a hero already standing ON its build site, "saving up
                // resources to build Energy", got yanked to a brand-new Human site one decision
                // later). The required scarcity gap now grows with how committed the current task
                // already is — see EconomyCommitmentMargin below — so a small lead is enough to
                // redirect a hero that's barely started, but a hero already on-site (worse, already
                // holding a near-complete resource reservation) needs a genuinely large deficit
                // elsewhere before abandoning real sunk cost makes sense.
                int margin = EconomyCommitmentMargin(player, root, ctx, hero, existingTask);
                bool isScarcer = existingTask.ResourceType.HasValue
                    && root.GetResource(resourceType) < root.GetResource(existingTask.ResourceType.Value) - margin;
                if (!isScarcer)
                    return results; // already building elsewhere and not scarcer enough to outweigh its own sunk commitment
                // isScarcer redirects this SAME task's own hero to a different hex — a swap,
                // not a net-new slot — so it proceeds even at cap (see atCap's own comment).
            }
            else if (atCap)
            {
                return results; // cap reached and this hero has no BuildFacility task to redirect — would be a brand-new slot
            }

            var task = new AiTask
            {
                Kind = AiTaskKind.BuildFacility,
                Army = hero,
                TargetHex = hex,
                ResourceType = resourceType,
                StartedWithNoIncome = !BuildFacilityTask.HasIncomeSource(player, resourceType),
            };
            AiDecision decision = AdvanceEconomyTask(player, root, ctx, task);
            // decision.Task null means AdvanceEconomyTask's own threat-reaction fired instead of
            // actually starting anything (see its own "known enemy too strong" flee branch) — this
            // was only ever a HYPOTHETICAL probe ("would this hero be safe building here"), `task`
            // itself was never registered. Bailing here (2026-08-21 fix, live log report: a hero
            // mid-RaidWeakerArmy kept getting yanked home at economyBaseWeight every turn for a
            // build that was never actually going to happen) — PreemptedTask must never be set on a
            // candidate that isn't actually starting real Economy work, or it silently cancels
            // whatever the hero's own real task legitimately had it doing.
            if (decision == null || decision.Task == null)
                return results;
            decision.PreemptedTask = existingTask;
            results.Add(decision);
            return results;
        }

        // See TryStartEconomyCandidates' own isScarcer comment — how much scarcer a rival hex's
        // own resource type must be before it's worth abandoning `existingTask`'s already-sunk
        // progress. Four stages, each strictly more committed than the last: not yet left the
        // garrison (barely started — small margin), en route (real travel already spent — medium),
        // arrived and sitting on the build site itself (large — see the project owner's own report
        // this whole mechanism exists for), and arrived with the build's own resource cost already
        // fully reserved (see AiResourceReservation.IsFullyReserved — the build is one spend away
        // from actually landing, so only a drastic deficit elsewhere should ever throw that away).
        // First-pass placeholder values (AiConfig.economyCommitmentMargin*), same as every other
        // freshly-added tunable in this codebase — flagged for the project owner's own tuning.
        private static int EconomyCommitmentMargin(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx, ArmyData hero, AiTask existingTask)
        {
            if (!hero.Hex.Equals(existingTask.TargetHex))
            {
                HexCoord garrisonHex = AiTurnController.GarrisonHexFor(player);
                return hero.Hex.Equals(garrisonHex) ? AiConfig.economyCommitmentMarginSelected : AiConfig.economyCommitmentMarginEnRoute;
            }

            CardDefinition definition = ctx.GameConfig?.extractionFacilityCards != null && existingTask.ResourceType.HasValue
                && (int)existingTask.ResourceType.Value < ctx.GameConfig.extractionFacilityCards.Length
                ? ctx.GameConfig.extractionFacilityCards[(int)existingTask.ResourceType.Value]
                : null;
            bool nearlyPaidFor = definition != null && AiResourceReservation.IsFullyReserved(existingTask, definition.resourceCost);
            return nearlyPaidFor ? AiConfig.economyCommitmentMarginReserved : AiConfig.economyCommitmentMarginArrived;
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

            // Neutrals — never fled from, only a hard cancel (not a temporary retreat, unlike
            // Разведка's own tasks): a neutral guarding the hex was never a good build spot to
            // begin with (see BuildFacilityTask's own class comment). Re-checked every call — a
            // neutral wandering within range AFTER the task started cancels it outright, same
            // radius TryStartEconomyCandidates' own pre-pass filter already rules the hex out on
            // for a brand-new task (this method runs on that too), so a hex that ever gets this far
            // should never immediately cancel itself right back out — a better, unguarded hex gets
            // tried next step only for a neutral that's genuinely moved in since.
            if (BuildFacilityTask.HasNeutralThreat(player, task.TargetHex))
            {
                if (AiTaskRegistry.TasksFor(player).Contains(task))
                    AiDebugLog.Write($"[AI] {player.Nickname}: \"{task.Army.Name}\" — a known neutral army within "
                        + $"{AiConfig.neutralBuildTriggerRadius} hexes of ({task.TargetHex.Q},{task.TargetHex.R}), "
                        + "Economy task cancelled.");
                AiResourceReservation.Release(task);
                AiTaskRegistry.Remove(player, task);
                return null;
            }

            // Real (non-neutral) enemies — reaction now depends on strength, via WorthIt (project
            // owner's own 2026-08-19 combat-reaction spec, see BuildFacilityTask's own class
            // comment): a solo hero (BuildFacilityTask.IsSolo) needs no special case here — zero
            // Attack/Defense on its own means ArmyBeats always reads it as outmatched, same as an
            // escorted army genuinely too weak for the sighting. Stronger-or-solo: retreat to the
            // citadel AND cancel; weaker AND within this turn's own movement reach: attack it
            // instead of continuing to the build site THIS step (task stays registered, re-
            // evaluated fresh next step); weaker but out of reach: no reaction at all, carries on
            // toward the build as normal.
            AiMapMemory.KnownEnemySighting? enemyThreat = BuildFacilityTask.NearbyRealEnemyThreat(player, task.TargetHex);
            if (enemyThreat.HasValue)
            {
                bool weBeatThem = BuildFacilityTask.ArmyBeats(task.Army, enemyThreat.Value, ctx.Map);

                if (!weBeatThem)
                {
                    if (AiTaskRegistry.TasksFor(player).Contains(task))
                        AiDebugLog.Write($"[AI] {player.Nickname}: \"{task.Army.Name}\" — a known enemy army within "
                            + $"{AiConfig.economySafetyRadius} hexes of ({task.TargetHex.Q},{task.TargetHex.R}) "
                            + (BuildFacilityTask.IsSolo(task.Army) ? "and the hero can't fight alone" : "is stronger")
                            + ", Economy task cancelled.");
                    AiResourceReservation.Release(task);
                    AiTaskRegistry.Remove(player, task);

                    HexCoord garrisonHex = AiTurnController.GarrisonHexFor(player);
                    if (task.Army.Hex.Equals(garrisonHex) || !AiTurnController.CanIssueMoveNow(root, task.Army, ctx.Map, garrisonHex))
                        return null;
                    var fleeTarget = new AiScoutPlanner.ScoutTarget(garrisonHex, 0f,
                        "a known enemy army is too strong — retreats to the citadel");
                    return AiDecision.Move(task.Army, fleeTarget, null, AiConfig.economyBaseWeight, AiTaskCategory.Economy);
                }

                if (HexGridMath.Distance(task.Army.Hex, enemyThreat.Value.Hex) <= task.Army.CurrentMovement)
                {
                    if (!AiTurnController.CanIssueMoveNow(root, task.Army, ctx.Map, enemyThreat.Value.Hex))
                        return null;
                    return AiDecision.Move(task.Army, enemyThreat.Value.Hex,
                        $"\"{task.Army.Name}\" attacks a known weaker army at ({enemyThreat.Value.Hex.Q},{enemyThreat.Value.Hex.R}) "
                            + "on its way to build",
                        task, AiConfig.economyExecuteScore, AiTaskCategory.Economy);
                }
                // weaker but out of this turn's movement reach — no reaction, carries on below
            }

            CardDefinition definition = ctx.GameConfig?.extractionFacilityCards != null && task.ResourceType.HasValue
                && (int)task.ResourceType.Value < ctx.GameConfig.extractionFacilityCards.Length
                ? ctx.GameConfig.extractionFacilityCards[(int)task.ResourceType.Value]
                : null;
            // Reservation starts once this turn's OWN movement budget guarantees arrival THIS
            // turn (distance <= MaxMovement — arriving this turn or already there), not a turn (or
            // more) earlier while still genuinely mid-walk (project owner's own 2026-08-19 call,
            // reaffirmed 2026-08-23 after briefly trying "wait until actually arrived" and walking
            // it back — see git history). Starting any earlier locked card play out of that
            // resource type for however long the whole multi-turn trip took; starting any later
            // (only once Army.Hex.Equals(TargetHex) is already true) left the stockpile fully
            // exposed to every OTHER AI spend for the entire final approach, right up to the one
            // turn we can actually GUARANTEE arrival — under-reserving exactly when it matters
            // most. `freeNow` inside TopUp only ever reads root's REAL current balance (already
            // credited by GameTurnController.CollectResourceIncome, which always runs before any
            // AI decision this turn) — never a projected/average income rate — so nothing reserved
            // here is ever theoretical, only whatever's guaranteed already in hand right now.
            bool willArriveThisTurn = HexGridMath.Distance(task.Army.Hex, task.TargetHex) <= task.Army.MaxMovement;
            if (definition != null && willArriveThisTurn)
                AiResourceReservation.TopUp(root, player, task, definition.resourceCost);

            if (!task.Army.Hex.Equals(task.TargetHex))
            {
                if (task.Army.CurrentMovement <= 0)
                    return null;
                if (!task.Army.HasActivatedThisTurn && !root.CanSpendActionPoints(task.Army.ActivationApCost))
                    return null;

                // A bare solo hero has no way to react to an enemy it stumbles onto, so
                // FindNextVisitedStep still routes it one hex at a time through only-already-
                // visited ground for that case (never straight through the fog, even toward a
                // target the AI otherwise already knows about via AiMapMemory) — see
                // HexPathfinder.FindPath's own blockHex and FindNextVisitedStep's own comment.
                // An escorted army can now fight back (see this method's own threat-reaction
                // block above) so it paths straight through the fog like any other combat-capable
                // army. Either way `targetHex` itself is exempted: that's the one hex this trip is
                // always allowed to step onto without having visited it first.
                HexCoord? nextStep = FindNextVisitedStep(ctx.Map, task.Army, task.TargetHex);
                if (nextStep == null)
                    return null; // no currently-known safe route yet — wait for more of the map to be scouted
                var target = new AiScoutPlanner.ScoutTarget(nextStep.Value, 0f,
                    $"carries the hero to build {task.ResourceType}");
                float score = BuildFacilityTask.TravelScore(player, root, task.TargetHex, task.ResourceType.Value, ctx.Map);
                return AiDecision.Move(task.Army, target, task, score, AiTaskCategory.Economy);
            }

            // Arrived — one more validity check before spending anything: if this build was ever
            // only justified by "no income source at all" (task.StartedWithNoIncome, see AiTask's
            // own comment) and some OTHER building has since started producing task.ResourceType (a
            // card played elsewhere, a second BuildFacility task finishing first, etc. — however
            // many turns this trip took), the original deficit is already closed and finishing this
            // one would just be a redundant duplicate. Deliberately NOT re-derived from the current
            // scarcity value alone — a build that always had income but was justified purely by a
            // low stockpile (StartedWithNoIncome false) must NOT be cancelled here just because
            // HasIncomeSource is (and always was) true. Cancels and frees the hero instead — same
            // release+remove shape every other hard-cancel branch above uses (project owner's own
            // 2026-08-23 call).
            if (task.StartedWithNoIncome && BuildFacilityTask.HasIncomeSource(player, task.ResourceType.Value))
            {
                if (AiTaskRegistry.TasksFor(player).Contains(task))
                    AiDebugLog.Write($"[AI] {player.Nickname}: \"{task.Army.Name}\" — {task.ResourceType} now has an "
                        + "income source elsewhere, deficit already closed, Economy task at "
                        + $"({task.TargetHex.Q},{task.TargetHex.R}) cancelled.");
                AiResourceReservation.Release(task);
                AiTaskRegistry.Remove(player, task);
                return null;
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
                    ? "saving up resources and short on AP"
                    : shortOnResources ? "saving up resources" : "short on AP";
                return AiDecision.Wait(task, $"\"{task.Army.Name}\" is on-site, {reason} "
                    + $"to build {task.ResourceType} at ({task.TargetHex.Q},{task.TargetHex.R}) — waiting");
            }

            // "BuildFacility Execute" — a flat AiConfig.economyExecuteScore now (2026-08-23,
            // project owner's own ladder spec: "= 120 фиксированно, а не 115 + IncomeBonus. Так
            // шкала будет проще и предсказуемее"), decoupled from ScoreHex entirely — arriving and
            // actually building this turn always wins arbitration outright, regardless of how far
            // behind on income the player happens to be right now.
            return AiDecision.BuildFacility(task, AiConfig.economyExecuteScore);
        }

        // One step of a BuildFacility hero's own route toward `targetHex` — for a SOLO hero (see
        // BuildFacilityTask.IsSolo) still hard-restricted to hexes `army`'s owner has already
        // VISITED (see VisionSystem.IsVisited; broader than just currently visible), `targetHex`
        // itself always exempted since that's the one hex this trip is inherently allowed to
        // arrive at unvisited — a bare hero has no way to react to an enemy it stumbles onto (see
        // AdvanceEconomyTask's own threat-reaction comment), so it must never be routed blind
        // through the fog. An ESCORTED army can now fight back (ArmyBeats/attack-or-flee, same
        // comment) so it's free to path straight through the fog like any other combat-capable
        // army (project owner's own 2026-08-19 call). Null if no such route exists yet (fully
        // boxed in by fog on every side, solo case only) — AdvanceEconomyTask's own caller treats
        // that as "nothing to do this step", same as any other unaffordable/blocked move
        // candidate, rather than falling back to the unsafe direct route.
        private static HexCoord? FindNextVisitedStep(HexMap map, ArmyData army, HexCoord targetHex)
        {
            System.Func<HexCoord, bool> blockHex = BuildFacilityTask.IsSolo(army)
                ? hex => !hex.Equals(targetHex) && !VisionSystem.IsVisited(army.Owner, hex)
                : (System.Func<HexCoord, bool>)null;
            // Routed through the shared AiTurnController.FindAffordableStep (2026-08-23 fix) —
            // this method used to hand back a next step with no movement-cost check at all, so a
            // hero with too little movement left for the next terrain hex still got a real
            // MoveArmy order, which the movement system then rejected outright.
            return AiTurnController.FindAffordableStep(map, army, targetHex, blockHex);
        }

        // Экономика · Задача 1's own "nothing left to build" fallback — a hero-led army with no
        // active task, sitting somewhere other than the garrison, while BuildFacilityTask.
        // HasAnythingToBuild says there's no known free resource hex left anywhere to build on.
        // No dedicated score any more (economyReturnHomeScore removed 2026-08-19, project owner's
        // own call) — an idle hero with nothing left to build doesn't need an inflated priority to
        // walk home, just the same flat economyWaitScore floor an arrived-but-still-saving task
        // already uses.
        public static List<AiDecision> TryEconomyReturnHomeCandidates(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            HashSet<ArmyData> stuckScouts)
        {
            var results = new List<AiDecision>();
            if (BuildFacilityTask.HasAnythingToBuild(player))
                return results;

            // Multi-base-aware since 2026-08-24 (project owner's own report) — per-army nearest own
            // base, not always the starting citadel (same reasoning as VisitHexTask.TryFlee/
            // AiAggressionPlanner's own raid-retreat helpers); already-at-ANY-own-base counts as
            // home too, not just the citadel specifically.
            foreach (ArmyData army in ArmyRegistry.AllForOwner(player))
            {
                if (!BuildFacilityTask.IsEligibleComposition(army) || army.Controller == null
                    || stuckScouts.Contains(army) || AiTurnController.OwnGarrisonHexes(player).Contains(army.Hex)
                    || AiTaskRegistry.TaskFor(player, army) != null)
                    continue;
                HexCoord homeHex = AiTurnController.NearestOwnGarrisonHex(player, army.Hex);
                if (!AiTurnController.CanIssueMoveNow(root, army, ctx.Map, homeHex))
                    continue;
                var target = new AiScoutPlanner.ScoutTarget(homeHex, 0f,
                    "nothing left to build — returns to nearest base");
                results.Add(AiDecision.Move(army, target, null, AiConfig.economyWaitScore, AiTaskCategory.Economy));
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
        //
        // Internal-only pre-pass (project owner's own 2026-08-23 call, same "which hex first"
        // split BuildFacilityTask.RankHex already uses for Задача 1 — see ResourcesScrapTask.
        // RankHex's own comment) — picks exactly one (hex, resourceType) winner by distance from
        // the nearest available collector plus resourceType scarcity; only that one hex is tried
        // for an actual actor/candidate below. Previously every matching hex got its own
        // candidate, all tied on the exact same ScoreHex (IncomeBehindBonus alone carries no
        // hex-specific term) — arbitration between them silently fell back to registry
        // enumeration order rather than any real preference.
        public static List<AiDecision> TryStartResourcesScrapCandidates(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx, AiResourcePool pool)
        {
            var results = new List<AiDecision>();
            var alreadyTargeted = new HashSet<HexCoord>(AiTaskRegistry.TasksFor(player)
                .Where(t => t.Kind == AiTaskKind.ResourcesScrap)
                .Select(t => t.TargetHex));

            HexCoord? bestHex = null;
            ResourceType? bestType = null;
            float bestRank = float.NegativeInfinity;
            foreach (HexCoord hex in HexResourceBonusRegistry.AllBonusHexes())
            {
                if (!AiMapMemory.IsResourceHexKnown(player, hex) || alreadyTargeted.Contains(hex))
                    continue;
                ResourceType? resourceType = DominantResourceType(hex);
                if (resourceType == null || ResourcesScrapTask.HasExtractionFacility(player, hex, resourceType.Value))
                    continue;

                float rank = ResourcesScrapTask.RankHex(player, root, hex, resourceType.Value, pool);
                if (bestHex == null || rank > bestRank)
                {
                    bestHex = hex;
                    bestType = resourceType;
                    bestRank = rank;
                }
            }
            if (bestHex == null)
                return results;

            HexCoord targetHex = bestHex.Value;
            ResourceType type = bestType.Value;
            ArmyData collector = ResourcesScrapTask.FindActor(player, targetHex, type, pool);
            if (collector == null || !AiTurnController.CanIssueMoveNow(root, collector, ctx.Map, targetHex))
                return results;

            var task = new AiTask { Kind = AiTaskKind.ResourcesScrap, Army = collector, TargetHex = targetHex, ResourceType = type };
            var target = new AiScoutPlanner.ScoutTarget(targetHex, 0f,
                $"\"{collector.Name}\" goes to collect {type} at ({targetHex.Q},{targetHex.R}) without building");
            // "Scrap Collect" — this move lands the collector on the target hex THIS turn, the same
            // "about to finish" moment BuildFacility's own Execute/economyExecuteScore covers (see
            // that constant's own comment) — collection itself starts passively the instant it
            // arrives (AdvanceResourcesScrapTask returns null from then on), so THIS is the one
            // decision that actually needs to win arbitration for it to happen on schedule.
            bool arrivesThisTurn = HexGridMath.Distance(collector.Hex, targetHex) <= collector.CurrentMovement;
            float score = arrivesThisTurn ? AiConfig.economyExecuteScore : ResourcesScrapTask.TravelScore(player, ctx.Map);
            results.Add(AiDecision.Move(collector, target, task, score, AiTaskCategory.Economy));
            return results;
        }

        // Экономика · Задача 2's own prep step — only offered once TryStartResourcesScrapCandidates
        // itself finds no ready solo collector for a given hex's type, so the two tiers never both
        // fire for the same hex the same step. See FindCollectorDetachPlan for the two ways this
        // can resolve (now hard-restricted to the garrison hex, and to sources with no active task
        // at all — an army genuinely off doing something is never even offered as a source any
        // more, not just deprioritized, project owner's own 2026-08-23 call); a fresh-army plan
        // additionally needs CreateArmyApCost checked here (the planner itself is pure/AP-agnostic,
        // same "checked before proposing" rule every other candidate in this file follows).
        //
        // Same internal-ranking pre-pass as TryStartResourcesScrapCandidates above (project
        // owner's own 2026-08-23 call) — picks one (hex, resourceType) winner via
        // ResourcesScrapTask.RankHex before ever looking for a plan, rather than trying every
        // matching hex in registry order.
        public static List<AiDecision> TryStartCollectorDetachCandidates(PlayerSetupData player, PlayerRoot root, HexMap map, AiResourcePool pool)
        {
            var results = new List<AiDecision>();
            var alreadyTargeted = new HashSet<HexCoord>(AiTaskRegistry.TasksFor(player)
                .Where(t => t.Kind == AiTaskKind.ResourcesScrap)
                .Select(t => t.TargetHex));
            HexCoord garrisonHex = AiTurnController.GarrisonHexFor(player);

            HexCoord? bestHex = null;
            ResourceType? bestType = null;
            float bestRank = float.NegativeInfinity;
            foreach (HexCoord hex in HexResourceBonusRegistry.AllBonusHexes())
            {
                if (!AiMapMemory.IsResourceHexKnown(player, hex) || alreadyTargeted.Contains(hex))
                    continue;
                ResourceType? resourceType = DominantResourceType(hex);
                if (resourceType == null || ResourcesScrapTask.HasExtractionFacility(player, hex, resourceType.Value))
                    continue;
                if (ResourcesScrapTask.FindActor(player, hex, resourceType.Value, pool) != null)
                    continue; // already have one ready to walk — nothing to detach

                float rank = ResourcesScrapTask.RankHex(player, root, hex, resourceType.Value, pool);
                if (bestHex == null || rank > bestRank)
                {
                    bestHex = hex;
                    bestType = resourceType;
                    bestRank = rank;
                }
            }
            if (bestHex == null)
                return results;

            CollectorDetachPlan? plan = FindCollectorDetachPlan(player, bestType.Value, garrisonHex, pool);
            if (plan == null)
                return results;
            if (plan.Value.MergeTarget == null && !root.CanSpendActionPoints(ArmyActions.CreateArmyApCost))
                return results;

            // Same score as ordinary Задача 2 travel (2026-08-24 fix, "зрелость экономики
            // применена только к BuildFacility") — used to be a bare, never-penalized
            // AiConfig.ResourceScrapDetachScore constant, so a detach step kept full priority even
            // once HasMatureEconomy would have shaved ResourcesScrapTask.TravelScore itself down.
            results.Add(AiDecision.DetachCollector(plan.Value, bestType.Value, ResourcesScrapTask.TravelScore(player, map)));
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
        public static AiDecision AdvanceResourcesScrapTask(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx, AiTask task)
        {
            if (task.Army?.Controller == null || !ArmyRegistry.AllForOwner(player).Contains(task.Army))
            {
                AiTaskRegistry.Remove(player, task);
                return null;
            }

            if (ResourcesScrapTask.HasEnemyThreat(player, task.TargetHex))
            {
                AiDebugLog.Write($"[AI] {player.Nickname}: \"{task.Army.Name}\" — a known army within {AiConfig.economySafetyRadius} "
                    + $"hexes of ({task.TargetHex.Q},{task.TargetHex.R}), Economy task (scrapping) cancelled.");
                AiTaskRegistry.Remove(player, task);
                return null;
            }

            if (task.ResourceType.HasValue && ResourcesScrapTask.HasExtractionFacility(player, task.TargetHex, task.ResourceType.Value))
            {
                AiDebugLog.Write($"[AI] {player.Nickname}: \"{task.Army.Name}\" — an extraction facility was built at "
                    + $"({task.TargetHex.Q},{task.TargetHex.R}), Economy task (scrapping) complete, unit freed.");
                AiTaskRegistry.Remove(player, task);
                return null;
            }

            if (!task.Army.Hex.Equals(task.TargetHex))
            {
                if (!AiTurnController.CanIssueMoveNow(root, task.Army, ctx.Map, task.TargetHex))
                    return null;
                var target = new AiScoutPlanner.ScoutTarget(task.TargetHex, 0f,
                    $"\"{task.Army.Name}\" goes to collect {task.ResourceType} at "
                        + $"({task.TargetHex.Q},{task.TargetHex.R}) without building");
                // "Scrap Collect" — see TryStartResourcesScrapCandidates' own matching comment.
                bool arrivesThisTurn = HexGridMath.Distance(task.Army.Hex, task.TargetHex) <= task.Army.CurrentMovement;
                float score = arrivesThisTurn ? AiConfig.economyExecuteScore : ResourcesScrapTask.TravelScore(player, ctx.Map);
                return AiDecision.Move(task.Army, target, task, score, AiTaskCategory.Economy);
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
            if (ctx.ShowArmyModal && ctx.ArmyViewerModal != null)
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
                AiDebugLog.Write($"[AI] {player.Nickname}: \"{army.Name}\" built a {task.ResourceType} extraction facility "
                    + $"at ({task.TargetHex.Q},{task.TargetHex.R}) — Economy task complete.{delta}");
                AiResourceReservation.Release(task);
                AiTaskRegistry.Remove(player, task);
            }
            else
            {
                task.BuildAttempts++;
                AiDebugLog.Write($"[AI] {player.Nickname}: couldn't build {task.ResourceType} "
                    + $"at ({task.TargetHex.Q},{task.TargetHex.R}) — attempt {task.BuildAttempts}.");
                if (task.BuildAttempts >= MaxBuildAttempts)
                {
                    AiDebugLog.Write($"[AI] {player.Nickname}: abandons the Economy task at "
                        + $"({task.TargetHex.Q},{task.TargetHex.R}) after {task.BuildAttempts} failed attempts.");
                    AiResourceReservation.Release(task);
                    AiTaskRegistry.Remove(player, task);
                }
            }

            if (ctx.ShowArmyModal && ctx.ArmyViewerModal != null)
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

            PlayerRoot root = PlayerRootRegistry.FindFor(player);
            int ap0 = root != null ? root.ActionPoints : 0;
            int human0 = root != null ? root.GetResource(ResourceType.Human) : 0;
            int energy0 = root != null ? root.GetResource(ResourceType.Energy) : 0;
            int materials0 = root != null ? root.GetResource(ResourceType.Materials) : 0;
            int tech0 = root != null ? root.GetResource(ResourceType.Tech) : 0;

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
                        AiDebugLog.Write($"[AI] {player.Nickname}: couldn't transfer {member.Name} from \"{source.Name}\" "
                            + $"to \"{target.Name}\" — {failReason}");
                }
                string mergeDelta = root != null ? AiTurnController.ResourceDeltaSuffix(root, ap0, human0, energy0, materials0, tech0) : null;
                AiDebugLog.Write($"[AI] {player.Nickname}: {decision.Reason} ({moved}/{others.Count} unit(s) transferred).{mergeDelta}");
            }
            else
            {
                ArmyData newArmy = ArmyActions.CreateArmy(player, source.Hex, ctx.StartingDeckCatalog?.GetCatalog(player.Faction), ctx.HexSelection);
                if (newArmy == null)
                {
                    AiDebugLog.Write($"[AI] {player.Nickname}: not enough AP for a new army for {collector.Name} — collecting postponed.");
                    yield break;
                }
                if (ArmyActions.TransferMember(collector, source, newArmy, ctx.HexSelection, out string failReason))
                {
                    string splitDelta = root != null ? AiTurnController.ResourceDeltaSuffix(root, ap0, human0, energy0, materials0, tech0) : null;
                    AiDebugLog.Write($"[AI] {player.Nickname}: {decision.Reason}.{splitDelta}");
                }
                else
                    AiDebugLog.Write($"[AI] {player.Nickname}: couldn't detach {collector.Name} — {failReason}");
            }

            if (ctx.ShowArmyModal && ctx.ArmyViewerModal != null)
                ctx.ArmyViewerModal.Hide();
            yield return AiTurnController.WaitStep(ctx);
        }
    }
}
