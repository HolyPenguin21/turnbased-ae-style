using System.Collections.Generic;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Units;

namespace Game.Ai
{
    // The unified arbiter's own comparison unit — every candidate any Level-1 category planner
    // (AiScoutPlanner/AiEconomyPlanner/AiManagementPlanner/AiAggressionPlanner) proposes each step
    // is one of these, all scored on the same shared scale (see AiTurnController.Decide's own
    // class comment). Top-level and public (moved out of AiTurnController, which used to nest this
    // privately) so every category planner can build one directly instead of only the orchestrator
    // itself.
    public enum AiActionKind
    {
        MoveArmy,
        PlayCard,
        ReserveArmy,
        DrawCard,
        BuildFacility,
        RepairUnit,
        SplitGarrisonArmy,
        CollapseAssembly,
        ConsolidateUnits,
        ConsolidateSwap,
        DetachCollector,
        SpawnReconArmy,
        AssembleRecceScout,
        RequestRaidArmy,
        AssembleRaidForce,
        DispatchReinforcement,
        ReinforceSwap,
        RequestDefendArmy,
        ActiveDefenceForce,
        StrengthenDefenceForce,
        BuildBase,
        SeedNewBaseGarrison,
        DispatchBaseReinforcement,
        DepositReinforcement,
        Wait,
        Pass,
    }

    public class AiDecision
    {
        public AiActionKind Kind;
        public ArmyData ExistingArmy;
        public HexCoord TargetHex;
        public CardData Card;
        public string Reason;
        // Which Level-1 task category this candidate belongs to (see AiTaskCategory's own class
        // comment) — set explicitly by every factory below, either from the AiTask it carries
        // (Category is task-derived there, since e.g. Wait/RepairUnit run under more than one
        // real Kind→Category mapping) or from an explicit `category` parameter for the handful of
        // action kinds genuinely shared across categories (MoveArmy/PlayCard/SplitGarrisonArmy —
        // see AiTurnController's own class comment on why those three alone are category-agnostic).
        // Null only for AiDecision.None — Pass never represents work done under any one category,
        // so AiTurnController's own log lines simply omit the tag for it.
        public AiTaskCategory? Category;
        // SplitGarrisonArmy only — the garrison members SplitGarrisonArmyRoutine moves into
        // MergeTarget (see GarrisonReorgTask.FindGarrisonOverflow).
        public IReadOnlyList<UnitData> UnitsToMove;
        // CollapseAssembly only — see GarrisonReorgTask.FindCollapseMove.
        public GarrisonReorgTask.CollapseMove CollapseMove;
        // ConsolidateUnits only — see GarrisonReorgTask.FindReorgMove.
        public GarrisonReorgTask.ConsolidationMove ConsolidationMove;
        // ConsolidateSwap only — see GarrisonReorgTask.FindReorgSwap.
        public GarrisonReorgTask.SwapMove SwapMove;
        // DetachCollector only — see AiEconomyPlanner.FindCollectorDetachPlan. CollectorUnit
        // stays in ExistingArmy (Source) either way; MergeTarget null means DetachCollector
        // Routine creates a fresh army at Source's own hex instead (only ever proposed when
        // that hex IS the player's own garrison hex — see TryStartCollectorDetachCandidates).
        // SplitGarrisonArmy reuses this same null-means-create-fresh convention for its own
        // destination — see GarrisonReorgTask.FindGarrisonOverflowDestination.
        public UnitData CollectorUnit;
        public ArmyData MergeTarget;
        // SplitGarrisonArmy (Economy hero-detach only, see AiEconomyPlanner.TryStartEconomyCandidates'
        // own GarrisonHero branch) — where/what the split-off hero should build, once her new army
        // actually exists. SplitGarrisonArmyRoutine registers her BuildFacility task itself the
        // moment that happens, instead of leaving a window where the fresh, task-less, hero-led army
        // sits fully "available" for Aggression/Defence's own recruiters to poach before Economy's
        // own next step ever gets to claim her (project owner's own 2026-08-21 report — exactly this
        // race let a raid force grab a hero mid-detach, before she ever took a single step toward the
        // build site).
        public HexCoord? EconomyBuildHex;
        public ResourceType? EconomyResourceType;
        // Set whenever this decision advances/starts a persistent AiTask (every MoveArmy
        // decision under Разведка/Экономика, and every BuildFacility decision) — null for
        // PlayCard/ReserveArmy/DrawCard/Pass, which never persist one (see AiTaskKind's own
        // comment on DrawCard/ReserveArmy).
        public AiTask Task;

        // The unified arbiter's own comparison key (see AiTurnController.Decide's own class
        // comment) — every candidate gathered this step gets one, on the same shared scale, and
        // Decide picks the single highest. Left at its default 0f for AiDecision.None (Pass never
        // competes against anything — it's only ever produced when the candidate list is empty).
        public float Score;

        // Economy-start candidates only (see AiEconomyPlanner.TryStartEconomyCandidates) — the
        // OTHER task the hero this candidate wants would have to give up. Removed only if THIS
        // candidate actually wins Decide's own arbitration (see AiTurnController.Commit) — generating
        // the candidate must never itself preempt anything, since most candidates built in a given
        // step lose.
        public AiTask PreemptedTask;

        // BuildBase-start candidates only (2026-08-24, "BuildBase и BuildFacility резервируют один
        // хекс разными армиями" fix, see AiAggressionPlanner.TryStartBuildBaseCandidates) — a
        // DIFFERENT hero's own BuildFacility task that already claims this same target hex, given
        // up in favor of BuildBase actually founding a base there instead. Kept as its own field
        // rather than reusing PreemptedTask (which, on this same candidate, may already carry an
        // in-progress Raid task belonging to the SAME army being redirected to build) — the two
        // preemptions are independent and can both apply to one BuildBase candidate at once. Same
        // "only removed if this candidate wins" rule as PreemptedTask — see AiTurnController.Commit.
        public AiTask PreemptedHexTask;

        public static AiDecision Move(ArmyData army, HexCoord hex, string reason, AiTask task, float score, AiTaskCategory category) => new AiDecision
        {
            Kind = AiActionKind.MoveArmy, ExistingArmy = army, TargetHex = hex, Reason = reason, Task = task, Score = score, Category = category,
        };

        public static AiDecision Move(ArmyData army, AiScoutPlanner.ScoutTarget target, AiTask task, float score, AiTaskCategory category) =>
            Move(army, target.Hex, target.Reason, task, score, category);

        public static AiDecision Move(ArmyData army, RaidWeakerArmyTask.RaidTarget target, AiTask task, float score, AiTaskCategory category) =>
            Move(army, target.Hex, target.Reason, task, score, category);

        public static AiDecision BuildFacility(AiTask task, float score) => new AiDecision
        {
            Kind = AiActionKind.BuildFacility, ExistingArmy = task.Army, TargetHex = task.TargetHex, Task = task, Score = score, Category = task.Category,
            Reason = $"builds a {task.ResourceType} extraction facility at ({task.TargetHex.Q},{task.TargetHex.R})",
        };

        // Менеджмент · Починка юнита — see AiManagementPlanner.AdvanceRepairTask/RepairUnitRoutine.
        public static AiDecision RepairUnit(AiTask task, float score) => new AiDecision
        {
            Kind = AiActionKind.RepairUnit, ExistingArmy = task.Army, TargetHex = task.TargetHex, Task = task, Score = score, Category = task.Category,
            Reason = $"repairs {task.TargetUnit.Name} in \"{task.Army.Name}\" at ({task.TargetHex.Q},{task.TargetHex.R})",
        };

        // Экономика · Задача 1's own visible stand-down — see EconomyWaitScore's own comment.
        // No-op on purpose: WaitRoutine touches neither the army nor the task, so the SAME
        // task just gets re-evaluated fresh next turn (AiResourceReservation keeps topping up
        // meanwhile — see AiEconomyPlanner.AdvanceEconomyTask).
        public static AiDecision Wait(AiTask task, string reason) => new AiDecision
        {
            Kind = AiActionKind.Wait, ExistingArmy = task.Army, TargetHex = task.TargetHex, Task = task,
            Score = AiConfig.economyWaitScore, Reason = reason, Category = task.Category,
        };

        // Экономика · Задача 2's own prerequisite step — see AiEconomyPlanner.
        // CollectorDetachPlan's own comment for the two shapes `plan` can take. No Task here:
        // same one-shot-reorg shape as SplitGarrison/ConsolidateUnits, not a persistent
        // AiTaskRegistry entry — the collector becomes tracked only once TryStartResourcesScrap
        // Candidates picks it up as an already-solo army next step.
        public static AiDecision DetachCollector(AiEconomyPlanner.CollectorDetachPlan plan, ResourceType type, float score) => new AiDecision
        {
            Kind = AiActionKind.DetachCollector, ExistingArmy = plan.Source, CollectorUnit = plan.Unit,
            MergeTarget = plan.MergeTarget, TargetHex = plan.Source.Hex, Score = score, Category = AiTaskCategory.Economy,
            Reason = plan.MergeTarget != null
                ? $"detaches {plan.Unit.Name} from \"{plan.Source.Name}\" to collect {type} — "
                    + $"the rest of the group moves into \"{plan.MergeTarget.Name}\""
                : $"detaches {plan.Unit.Name} from \"{plan.Source.Name}\" to collect {type} — new army at the garrison",
        };

        // Разведка · сборка Recce-состава, шаг 1 — see AiScoutPlanner's own
        // TryStartReconAssemblyCandidatesFor comment. Deliberately its own AiActionKind rather
        // than reusing ReserveArmy — Менеджмент's own Reserve/Draw alternation
        // (AiManagementPlanner.NotifyFallbackUsed) must never flip because Разведка needed a
        // body, those are unrelated bookkeeping.
        public static AiDecision RequestReconArmy(float score) => new AiDecision
        {
            Kind = AiActionKind.SpawnReconArmy, Score = score, Category = AiTaskCategory.Reconnaissance,
            Reason = "no free empty army for a Recce composition — requesting a new one",
        };

        // Разведка · сборка Recce-состава, шаг 2b — a Recce-tagged unit/hero already deployed
        // but buried inside a bigger army on the SAME hex as an already-existing empty army
        // (see AiScoutPlanner.FindBuriedRecceUnit's own comment on how rare this actually is)
        // moves into that empty army, becoming a solo scout composition on the spot.
        public static AiDecision AssembleRecceScout(AiScoutPlanner.BuriedRecceUnit buried, ArmyData emptyArmy, float score) => new AiDecision
        {
            Kind = AiActionKind.AssembleRecceScout, ExistingArmy = buried.Source, CollectorUnit = buried.Unit,
            MergeTarget = emptyArmy, TargetHex = emptyArmy.Hex, Score = score, Category = AiTaskCategory.Reconnaissance,
            Reason = $"{buried.Unit.Name} already carries Recce but isn't solo — transferring into \"{emptyArmy.Name}\"",
        };

        // Агрессия · сборка состава с нуля, шаг 1 — same shape as RequestReconArmy, own
        // AiActionKind/log so debug output doesn't say "задача «Разведка»" for an Агрессия
        // spawn.
        public static AiDecision RequestRaidArmy(float score) => new AiDecision
        {
            Kind = AiActionKind.RequestRaidArmy, Score = score, Category = AiTaskCategory.Aggression,
            Reason = "no free empty army to assemble into — requesting a new one",
        };

        // Оборона · сборка состава с нуля, шаг 1 — same shape as RequestRaidArmy, own
        // AiActionKind/log so debug output doesn't say "задача «Агрессия»" for an Оборона spawn.
        // `homeHex` (carried via the shared TargetHex field) — which of the player's own garrisoned
        // hexes to spawn the new empty army at; RequestDefendArmyRoutine reads it back out, since
        // that routine (unlike most others) doesn't otherwise receive the AiDecision itself.
        public static AiDecision RequestDefendArmy(HexCoord homeHex, float score) => new AiDecision
        {
            Kind = AiActionKind.RequestDefendArmy, TargetHex = homeHex, Score = score, Category = AiTaskCategory.Defence,
            Reason = "no free empty army to assemble into — requesting a new one",
        };

        // Агрессия · сборка состава с нуля, шаг 2 — one recruit (hero or non-hero unit,
        // whichever RaidWeakerArmyTask.FindRecruitAt picked) moves from wherever it's
        // currently sitting (garrison stock or an idle army already at the same hex) into the
        // forming raid force. Unlike AssembleRecceScout, this DOES carry a Task — see
        // AiAggressionPlanner.TryRaidAssembleCandidates' own comment for why "start" and
        // "continue" are the same decision shape here.
        public static AiDecision AssembleRaidForce(ArmyData source, UnitData unit, ArmyData formingArmy, AiTask task, float score) => new AiDecision
        {
            Kind = AiActionKind.AssembleRaidForce, ExistingArmy = source, CollectorUnit = unit,
            MergeTarget = formingArmy, TargetHex = formingArmy.Hex, Task = task, Score = score, Category = task.Category,
            Reason = $"{unit.Name} joins \"{formingArmy.Name}\"",
        };

        // Оборона · same one-recruit-at-a-time assembly as AssembleRaidForce above (identical
        // execution — see AiTurnController's own dispatch, both kinds route to
        // AiAggressionPlanner.AssembleRaidForceRoutine), just its own AiActionKind/log so debug
        // output never says "AssembleRaidForce" for an Оборона composition — same reasoning as
        // RequestDefendArmy's own split from RequestRaidArmy above.
        public static AiDecision ActiveDefenceForce(ArmyData source, UnitData unit, ArmyData formingArmy, AiTask task, float score) => new AiDecision
        {
            Kind = AiActionKind.ActiveDefenceForce, ExistingArmy = source, CollectorUnit = unit,
            MergeTarget = formingArmy, TargetHex = formingArmy.Hex, Task = task, Score = score, Category = task.Category,
            Reason = $"{unit.Name} joins \"{formingArmy.Name}\"",
        };

        // Оборона · full-but-insufficient defence force gets a direct 1-for-1 upgrade instead of
        // stalling — see AiDefencePlanner.TryStrengthenCandidate's own comment. Reuses
        // GarrisonReorgTask.SwapMove/ArmyActions.SwapMembers, same no-free-slot-needed technique
        // ConsolidateSwap/ReinforceSwap already rely on — own AiActionKind/log so debug output
        // doesn't read as a Menedzhment or Агрессия move.
        public static AiDecision StrengthenDefenceForce(GarrisonReorgTask.SwapMove move, AiTask task, float score) => new AiDecision
        {
            Kind = AiActionKind.StrengthenDefenceForce, ExistingArmy = move.ArmyA, TargetHex = move.ArmyA.Hex,
            SwapMove = move, Task = task, Score = score, Category = task.Category,
            Reason = move.Reason,
        };

        // Агрессия · подкрепление раненой армии, шаг 1 — see AiAggressionPlanner.
        // TryRaidRegroupCandidates/DispatchReinforcementRoutine. task.TargetArmy is already known
        // (the wounded army being rescued); task.Army is deliberately left null here — the routine
        // fills it in once the courier army actually exists. `source` — the army `recruit` is
        // actually a member of right now (RaidWeakerArmyTask.FindNonHeroRecruitAt's own out
        // param), NOT necessarily the nearest garrison (2026-08-24 P0 fix, project owner's own
        // report: recruit and source used to be picked independently — same home hex, but not
        // guaranteed the same army object — so DispatchReinforcementRoutine's own TransferMember
        // could reject a recruit that wasn't actually a member of the army passed here).
        public static AiDecision DispatchReinforcement(ArmyData source, UnitData recruit, AiTask task, float score) => new AiDecision
        {
            Kind = AiActionKind.DispatchReinforcement, ExistingArmy = source, CollectorUnit = recruit,
            TargetHex = task.TargetArmy.Hex, Task = task, Score = score, Category = task.Category,
            Reason = $"{recruit.Name} is dispatched as reinforcement to \"{task.TargetArmy.Name}\"",
        };

        // Агрессия · подкрепление раненой армии, шаг 2 — courier has arrived at task.TargetHex
        // (the wounded army's rendezvous hex); see AiAggressionPlanner.ReinforceSwapRoutine for the
        // actual composition swap this triggers.
        public static AiDecision ReinforceSwap(AiTask task, float score) => new AiDecision
        {
            Kind = AiActionKind.ReinforceSwap, ExistingArmy = task.Army, TargetHex = task.TargetHex, Task = task, Score = score, Category = task.Category,
            Reason = $"\"{task.Army.Name}\" has arrived — picking up the wounded from \"{task.TargetArmy.Name}\"",
        };

        // Агрессия · Задача 2 — the army has arrived at task.TargetHex; see
        // AiAggressionPlanner.BuildBaseRoutine for the actual card play this triggers.
        public static AiDecision BuildBase(AiTask task, float score) => new AiDecision
        {
            Kind = AiActionKind.BuildBase, ExistingArmy = task.Army, TargetHex = task.TargetHex, Task = task, Score = score, Category = task.Category,
            Reason = $"\"{task.Army.Name}\" has arrived — founds a new base at ({task.TargetHex.Q},{task.TargetHex.R})",
        };

        // Агрессия · Задача 2's own AwaitingGarrisonSeed phase (Feature 2, 2026-08-24) — see
        // AiTask.AwaitingGarrisonSeed's own comment and AiAggressionPlanner.AdvanceGarrisonSeed.
        // Reuses GarrisonReorgTask.ConsolidationMove's own shape (Source/Unit/Target/Reason) rather
        // than inventing a new struct — this is exactly the same "move one unit from army A into
        // army B" primitive ConsolidateUnitsRoutine already executes, just triggered from Агрессия's
        // own task continuation instead of GarrisonReorgTask's end-of-turn drain, so it needs its
        // own AiActionKind/execution routine to also close the BuildBase task out once the transfer
        // lands (ConsolidateUnitsRoutine itself has no notion of a task to close).
        public static AiDecision SeedNewBaseGarrison(GarrisonReorgTask.ConsolidationMove move, AiTask task, float score) => new AiDecision
        {
            Kind = AiActionKind.SeedNewBaseGarrison, ExistingArmy = move.Source, TargetHex = move.Target.Hex,
            ConsolidationMove = move, Task = task, Score = score, Category = task.Category,
            Reason = move.Reason,
        };

        // Оборона · SecureBase, шаг 1 — see SecureBaseTask.BuildDecision/AiOperations.
        // DispatchBaseReinforcementRoutine. Same shape as DispatchReinforcement above (own
        // AiActionKind/log purely so debug output says "SecureBase", not "Агрессия") — `source` is
        // the DONOR garrison (nearest own base with a spareable non-hero, per
        // SecureBaseTask.FindReinforcementSource), task.Army is left null here until the routine
        // actually spawns the courier.
        public static AiDecision DispatchBaseReinforcement(ArmyData source, UnitData recruit, AiTask task, float score) => new AiDecision
        {
            Kind = AiActionKind.DispatchBaseReinforcement, ExistingArmy = source, CollectorUnit = recruit,
            TargetHex = task.HomeHex, Task = task, Score = score, Category = task.Category,
            Reason = $"{recruit.Name} is dispatched from \"{source.Name}\" to secure the base at ({task.HomeHex.Q},{task.HomeHex.R})",
        };

        // Оборона · SecureBase, шаг 2 — courier has arrived at task.HomeHex; see
        // AiOperations.DepositReinforcementRoutine for the actual transfer into the base's own
        // garrison this triggers. Reuses GarrisonReorgTask.ConsolidationMove's own shape, same
        // reasoning as SeedNewBaseGarrison above.
        public static AiDecision DepositReinforcement(GarrisonReorgTask.ConsolidationMove move, AiTask task, float score) => new AiDecision
        {
            Kind = AiActionKind.DepositReinforcement, ExistingArmy = move.Source, TargetHex = move.Target.Hex,
            ConsolidationMove = move, Task = task, Score = score, Category = task.Category,
            Reason = move.Reason,
        };

        // `category` — Менеджмент's own TryPlayCardCandidates and Разведка's own
        // TryStartReconAssemblyCandidatesFor both build this same AiActionKind.PlayCard, for
        // disjoint card sets (Recce cards never reach Менеджмент's own candidates any more — see
        // TryPlayCardCandidates' own comment) — an explicit parameter here rather than inferring
        // it from `role` still keeps that genuinely up to the caller.
        public static AiDecision PlayCard(ArmyData existing, CardData card, AiManagementPlanner.CardRole role, float score,
            AiTaskCategory category) => new AiDecision
        {
            Kind = AiActionKind.PlayCard,
            ExistingArmy = existing,
            Card = card,
            Score = score,
            Category = category,
            Reason = existing != null
                ? $"reinforces \"{existing.Name}\" with card {card.Definition.displayName}{RoleLabel(role)}"
                : $"new army for card {card.Definition.displayName}{RoleLabel(role)}",
        };

        private static string RoleLabel(AiManagementPlanner.CardRole role)
        {
            switch (role)
            {
                case AiManagementPlanner.CardRole.Recce: return " (Recce, solo)";
                case AiManagementPlanner.CardRole.Hero: return " (hero)";
                default: return "";
            }
        }

        // Менеджмент · капасити гарнизона — see GarrisonReorgTask.FindGarrisonOverflow/
        // FindGarrisonOverflowDestination. `destination` null means SplitGarrisonArmyRoutine
        // spawns a fresh reserve army instead of reusing an existing one. Also reused by
        // Экономика · Задача 1's own hero-detach prep step (see AiEconomyPlanner.
        // TryStartEconomyCandidates and AiEconomyPlanner.FindNearestHeroAnywhere) — same "pull
        // specific unit(s) out of garrison into a fresh/existing army" primitive either way, just
        // a different `reason` for why — `category` defaults to Management (the common case) and
        // that Economy call site passes AiTaskCategory.Economy explicitly. `score` defaults to 0f
        // — the Management call site (AiManagementPlanner.TryGarrisonSplitCandidate) never competes
        // in arbitration any more (see AiTurnController.RunGarrisonReorgPhase's own comment) and
        // just omits it, while the Economy call site still passes its own real, competing score.
        public static AiDecision SplitGarrison(ArmyData garrison, IReadOnlyList<UnitData> unitsToMove, ArmyData destination, float score = 0f,
            string reason = null, AiTaskCategory category = AiTaskCategory.Management) => new AiDecision
        {
            Kind = AiActionKind.SplitGarrisonArmy,
            ExistingArmy = garrison,
            TargetHex = garrison.Hex,
            UnitsToMove = unitsToMove,
            MergeTarget = destination,
            Score = score,
            Category = category,
            Reason = reason ?? (destination != null
                ? $"garrison is full — moving {unitsToMove.Count} unit(s) into \"{destination.Name}\""
                : $"garrison is full — splitting off a new army ({unitsToMove.Count} unit(s))"),
        };

        // Менеджмент · CollapseTemporaryAssembly — see GarrisonReorgTask.FindCollapseMove. One
        // decision covers the WHOLE still-forming task-army's roster at once (atomic — FindCollapseMove
        // only ever returns a move once it's already verified every member will fit), never
        // competes in arbitration (same reasoning as Consolidate/Swap below — this only runs from
        // AiTurnController.RunGarrisonReorgPhase's own end-of-turn drain).
        public static AiDecision Collapse(GarrisonReorgTask.CollapseMove move) => new AiDecision
        {
            Kind = AiActionKind.CollapseAssembly,
            ExistingArmy = move.Source,
            TargetHex = move.Source.Hex,
            CollapseMove = move,
            Category = AiTaskCategory.Management,
            Reason = move.Reason,
        };

        // Менеджмент · передача юнитов между армиями в базе — see GarrisonReorgTask.FindReorgMove.
        // No score parameter (removed 2026-08-20 along with AiConfig.managementGarrisonBalanceScore)
        // — this never competes in arbitration any more, see AiTurnController.RunGarrisonReorgPhase.
        // Reason comes straight from the move itself (2026-08-20) — GarrisonReorgTask now has four
        // tiers or more that can produce a ConsolidationMove (hero-capacity-expansion prep,
        // lone-army fold into garrison OR another field army, garrison/army strength balance, and
        // composition balance), each with its own wording, so re-deriving a reason here from just
        // Source/Target.IsGarrison stopped being able to tell them apart.
        public static AiDecision Consolidate(GarrisonReorgTask.ConsolidationMove move) => new AiDecision
        {
            Kind = AiActionKind.ConsolidateUnits,
            ExistingArmy = move.Source,
            TargetHex = move.Source.Hex,
            ConsolidationMove = move,
            Category = AiTaskCategory.Management,
            Reason = move.Reason,
        };

        // Менеджмент · обмен юнитов между армиями в базе — see GarrisonReorgTask.FindReorgSwap.
        // Only ever tried once FindReorgMove itself comes up empty this same call (see
        // AiManagementPlanner.TryConsolidationCandidate) — a plain move covers everything a swap
        // could, whenever a plain move is actually possible at all.
        public static AiDecision Swap(GarrisonReorgTask.SwapMove move) => new AiDecision
        {
            Kind = AiActionKind.ConsolidateSwap,
            ExistingArmy = move.ArmyA,
            TargetHex = move.ArmyA.Hex,
            SwapMove = move,
            Category = AiTaskCategory.Management,
            Reason = move.Reason,
        };

        // `homeHex` (carried via the shared TargetHex field, same convention as RequestDefendArmy
        // above) — which of the player's own garrisoned hexes to spawn the reserve army at;
        // ReserveArmyRoutine reads it back out since it doesn't otherwise receive the AiDecision.
        public static AiDecision Reserve(HexCoord homeHex, int currentSpare, float score) => new AiDecision
        {
            Kind = AiActionKind.ReserveArmy,
            TargetHex = homeHex,
            Score = score,
            Category = AiTaskCategory.Management,
            Reason = $"spare reserve army ({currentSpare + 1}/{AiConfig.maxSpareArmies})",
        };

        public static AiDecision Draw(float score) => new AiDecision
        {
            Kind = AiActionKind.DrawCard,
            Score = score,
            Category = AiTaskCategory.Management,
            Reason = "hand is played out",
        };

        public static AiDecision None(string reason) => new AiDecision { Kind = AiActionKind.Pass, Reason = reason };
    }
}
