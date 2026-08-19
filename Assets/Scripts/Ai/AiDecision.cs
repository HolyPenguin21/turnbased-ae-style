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
        ConsolidateUnits,
        DetachCollector,
        SpawnReconArmy,
        AssembleRecceScout,
        RequestRaidArmy,
        AssembleRaidForce,
        DispatchReinforcement,
        ReinforceSwap,
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
        // SplitGarrisonArmy only — the garrison members SplitGarrisonArmyRoutine moves into
        // MergeTarget (see GarrisonReorgTask.FindGarrisonOverflow).
        public IReadOnlyList<UnitData> UnitsToMove;
        // ConsolidateUnits only — see GarrisonReorgTask.FindReorgMove.
        public GarrisonReorgTask.ConsolidationMove ConsolidationMove;
        // DetachCollector only — see AiEconomyPlanner.FindCollectorDetachPlan. CollectorUnit
        // stays in ExistingArmy (Source) either way; MergeTarget null means DetachCollector
        // Routine creates a fresh army at Source's own hex instead (only ever proposed when
        // that hex IS the player's own garrison hex — see TryStartCollectorDetachCandidates).
        // SplitGarrisonArmy reuses this same null-means-create-fresh convention for its own
        // destination — see GarrisonReorgTask.FindGarrisonOverflowDestination.
        public UnitData CollectorUnit;
        public ArmyData MergeTarget;
        // PlayCard only, Hero role — non-null means ExistingArmy is a FULL plain army
        // (AiManagementPlanner.FindPlacement's own last-resort tier picked it anyway): the
        // project owner's own "должен уметь выкладывать карты героев прямо в готовую армию"
        // spec. PlayCardRoutine evicts this member to the garrison FIRST, opening the room the
        // hero card then deploys into, in the same action — see AiManagementPlanner.CardPlacement.
        public UnitData EvictUnit;
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

        public static AiDecision Move(ArmyData army, HexCoord hex, string reason, AiTask task, float score) => new AiDecision
        {
            Kind = AiActionKind.MoveArmy, ExistingArmy = army, TargetHex = hex, Reason = reason, Task = task, Score = score,
        };

        public static AiDecision Move(ArmyData army, AiScoutPlanner.ScoutTarget target, AiTask task, float score) =>
            Move(army, target.Hex, target.Reason, task, score);

        public static AiDecision Move(ArmyData army, RaidWeakerArmyTask.RaidTarget target, AiTask task, float score) =>
            Move(army, target.Hex, target.Reason, task, score);

        public static AiDecision BuildFacility(AiTask task, float score) => new AiDecision
        {
            Kind = AiActionKind.BuildFacility, ExistingArmy = task.Army, TargetHex = task.TargetHex, Task = task, Score = score,
            Reason = $"задача «Экономика»: строит объект добычи {task.ResourceType} на ({task.TargetHex.Q},{task.TargetHex.R})",
        };

        // Менеджмент · Починка юнита — see AiManagementPlanner.AdvanceRepairTask/RepairUnitRoutine.
        public static AiDecision RepairUnit(AiTask task, float score) => new AiDecision
        {
            Kind = AiActionKind.RepairUnit, ExistingArmy = task.Army, TargetHex = task.TargetHex, Task = task, Score = score,
            Reason = $"задача «Экономика»: чинит {task.TargetUnit.Name} в {task.Army.Name} на ({task.TargetHex.Q},{task.TargetHex.R})",
        };

        // Экономика · Задача 1's own visible stand-down — see EconomyWaitScore's own comment.
        // No-op on purpose: WaitRoutine touches neither the army nor the task, so the SAME
        // task just gets re-evaluated fresh next turn (AiResourceReservation keeps topping up
        // meanwhile — see AiEconomyPlanner.AdvanceEconomyTask).
        public static AiDecision Wait(AiTask task, string reason) => new AiDecision
        {
            Kind = AiActionKind.Wait, ExistingArmy = task.Army, TargetHex = task.TargetHex, Task = task,
            Score = AiConfig.Current.economyWaitScore, Reason = reason,
        };

        // Экономика · Задача 2's own prerequisite step — see AiEconomyPlanner.
        // CollectorDetachPlan's own comment for the two shapes `plan` can take. No Task here:
        // same one-shot-reorg shape as SplitGarrison/ConsolidateUnits, not a persistent
        // AiTaskRegistry entry — the collector becomes tracked only once TryStartResourcesScrap
        // Candidates picks it up as an already-solo army next step.
        public static AiDecision DetachCollector(AiEconomyPlanner.CollectorDetachPlan plan, ResourceType type, float score) => new AiDecision
        {
            Kind = AiActionKind.DetachCollector, ExistingArmy = plan.Source, CollectorUnit = plan.Unit,
            MergeTarget = plan.MergeTarget, TargetHex = plan.Source.Hex, Score = score,
            Reason = plan.MergeTarget != null
                ? $"задача «Экономика»: выделяет {plan.Unit.Name} из {plan.Source.Name} для добычи {type} — "
                    + $"остальной состав переходит в {plan.MergeTarget.Name}"
                : $"задача «Экономика»: выделяет {plan.Unit.Name} из {plan.Source.Name} для добычи {type} — новая армия в гарнизоне",
        };

        // Разведка · сборка Recce-состава, шаг 1 — see AiScoutPlanner's own
        // TryStartReconAssemblyCandidatesFor comment. Deliberately its own AiActionKind rather
        // than reusing ReserveArmy — Менеджмент's own Reserve/Draw alternation
        // (AiManagementPlanner.NotifyFallbackUsed) must never flip because Разведка needed a
        // body, those are unrelated bookkeeping.
        public static AiDecision RequestReconArmy(float score) => new AiDecision
        {
            Kind = AiActionKind.SpawnReconArmy, Score = score,
            Reason = "задача «Разведка»: нет свободной пустой армии под Recce-состав — запрашивает новую",
        };

        // Разведка · сборка Recce-состава, шаг 2b — a Recce-tagged unit/hero already deployed
        // but buried inside a bigger army on the SAME hex as an already-existing empty army
        // (see AiScoutPlanner.FindBuriedRecceUnit's own comment on how rare this actually is)
        // moves into that empty army, becoming a solo scout composition on the spot.
        public static AiDecision AssembleRecceScout(AiScoutPlanner.BuriedRecceUnit buried, ArmyData emptyArmy, float score) => new AiDecision
        {
            Kind = AiActionKind.AssembleRecceScout, ExistingArmy = buried.Source, CollectorUnit = buried.Unit,
            MergeTarget = emptyArmy, TargetHex = emptyArmy.Hex, Score = score,
            Reason = $"задача «Разведка»: {buried.Unit.Name} — уже с Recce, но не соло — переводит в {emptyArmy.Name}",
        };

        // Агрессия · сборка состава с нуля, шаг 1 — same shape as RequestReconArmy, own
        // AiActionKind/log so debug output doesn't say "задача «Разведка»" for an Агрессия
        // spawn.
        public static AiDecision RequestRaidArmy(float score) => new AiDecision
        {
            Kind = AiActionKind.RequestRaidArmy, Score = score,
            Reason = "задача «Агрессия»: нет свободной пустой армии под сборку — запрашивает новую",
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
            MergeTarget = formingArmy, TargetHex = formingArmy.Hex, Task = task, Score = score,
            Reason = $"задача «Агрессия»: {unit.Name} присоединяется к {formingArmy.Name}",
        };

        // Агрессия · подкрепление раненой армии, шаг 1 — see AiAggressionPlanner.
        // TryRaidRegroupCandidates/DispatchReinforcementRoutine. task.TargetArmy is already known
        // (the wounded army being rescued); task.Army is deliberately left null here — the routine
        // fills it in once the courier army actually exists.
        public static AiDecision DispatchReinforcement(ArmyData garrison, UnitData recruit, AiTask task, float score) => new AiDecision
        {
            Kind = AiActionKind.DispatchReinforcement, ExistingArmy = garrison, CollectorUnit = recruit,
            TargetHex = task.TargetArmy.Hex, Task = task, Score = score,
            Reason = $"задача «Агрессия»: {recruit.Name} отправляется подкреплением к {task.TargetArmy.Name}",
        };

        // Агрессия · подкрепление раненой армии, шаг 2 — courier has arrived at task.TargetHex
        // (the wounded army's rendezvous hex); see AiAggressionPlanner.ReinforceSwapRoutine for the
        // actual composition swap this triggers.
        public static AiDecision ReinforceSwap(AiTask task, float score) => new AiDecision
        {
            Kind = AiActionKind.ReinforceSwap, ExistingArmy = task.Army, TargetHex = task.TargetHex, Task = task, Score = score,
            Reason = $"задача «Агрессия»: {task.Army.Name} на месте — забирает раненых из {task.TargetArmy.Name}",
        };

        public static AiDecision PlayCard(ArmyData existing, CardData card, AiManagementPlanner.CardRole role, float score,
            UnitData evictUnit = null) => new AiDecision
        {
            Kind = AiActionKind.PlayCard,
            ExistingArmy = existing,
            Card = card,
            EvictUnit = evictUnit,
            Score = score,
            Reason = evictUnit != null
                ? $"освобождает место в {existing.Name} (выводит {evictUnit.Name} в гарнизон) под героя {card.Definition.displayName}"
                : existing != null
                    ? $"пополняет {existing.Name} картой {card.Definition.displayName}{RoleLabel(role)}"
                    : $"новая армия под карту {card.Definition.displayName}{RoleLabel(role)}",
        };

        private static string RoleLabel(AiManagementPlanner.CardRole role)
        {
            switch (role)
            {
                case AiManagementPlanner.CardRole.Recce: return " (Recce, соло)";
                case AiManagementPlanner.CardRole.Hero: return " (герой)";
                default: return "";
            }
        }

        // Менеджмент · капасити гарнизона — see GarrisonReorgTask.FindGarrisonOverflow/
        // FindGarrisonOverflowDestination. `destination` null means SplitGarrisonArmyRoutine
        // spawns a fresh reserve army instead of reusing an existing one. Also reused by
        // Экономика · Задача 1's own hero-detach prep step (see AiEconomyPlanner.
        // TryStartEconomyCandidates and AiEconomyPlanner.FindNearestHeroAnywhere) — same "pull
        // specific unit(s) out of garrison into a fresh/existing army" primitive either way, just
        // a different `reason` for why.
        public static AiDecision SplitGarrison(ArmyData garrison, IReadOnlyList<UnitData> unitsToMove, ArmyData destination, float score,
            string reason = null) => new AiDecision
        {
            Kind = AiActionKind.SplitGarrisonArmy,
            ExistingArmy = garrison,
            TargetHex = garrison.Hex,
            UnitsToMove = unitsToMove,
            MergeTarget = destination,
            Score = score,
            Reason = reason ?? (destination != null
                ? $"задача «Менеджмент»: гарнизон полон — переводит {unitsToMove.Count} юнит(ов) в {destination.Name}"
                : $"задача «Менеджмент»: гарнизон полон — выделяет новую армию ({unitsToMove.Count} юнит(ов))"),
        };

        // Менеджмент · передача юнитов между армиями в базе — see
        // GarrisonReorgTask.FindReorgMove.
        public static AiDecision Consolidate(GarrisonReorgTask.ConsolidationMove move, float score) => new AiDecision
        {
            Kind = AiActionKind.ConsolidateUnits,
            ExistingArmy = move.Source,
            TargetHex = move.Source.Hex,
            ConsolidationMove = move,
            Score = score,
            // "одиночка" is only actually true for the fold/pair/rebalance tiers (Source really
            // is a lone/settled FIELD army) — the two garrison-pull tiers (FindHeroEscortFrom
            // Garrison/FindPlainArmyReinforcement/FindHeroPromotion) move stock straight OUT of
            // the garrison stockpile instead, which used to get mislabeled "одиночка" too and
            // read as if the unit had somehow been a standalone army the whole time (this is
            // exactly what made diagnosing the project owner's own "Bastion Guard" report harder
            // than it needed to be — see GarrisonReorgTask's own tier comments).
            Reason = move.Target.IsGarrison
                ? $"задача «Менеджмент»: {move.Unit.Name} — одиночка, передаётся в гарнизон"
                : move.Source.IsGarrison
                    ? $"задача «Менеджмент»: {move.Unit.Name} выходит из гарнизона в {move.Target.Name}"
                    : $"задача «Менеджмент»: {move.Unit.Name} — одиночка, объединяется с {move.Target.Name}",
        };

        public static AiDecision Reserve(int currentSpare, float score) => new AiDecision
        {
            Kind = AiActionKind.ReserveArmy,
            Score = score,
            Reason = $"задача «Менеджмент»: резервная армия про запас ({currentSpare + 1}/{AiConfig.Current.maxSpareArmies})",
        };

        public static AiDecision Draw(float score) => new AiDecision
        {
            Kind = AiActionKind.DrawCard,
            Score = score,
            Reason = "задача «Менеджмент»: рука разыграна или недоступна по AP/ресурсам — остаточный добор",
        };

        public static AiDecision None(string reason) => new AiDecision { Kind = AiActionKind.Pass, Reason = reason };
    }
}
