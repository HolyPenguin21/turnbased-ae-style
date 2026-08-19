using System.Collections.Generic;
using System.Linq;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai
{
    // Level-1 categories (see AiTurnController's own 3-level class comment) that run as
    // persistent, CONCURRENT task pools — several tasks across all four categories run in the
    // same turn, each on its own army. The only limit is per-Kind concurrency (see
    // AiTaskRegistry.CountActive) and AiResourcePool's own no-double-booking guard, never a
    // single competitive "pick one category" score comparison — AiGoalScorer used to also hold
    // exactly that kind of competitive pick (AiGoalKind: Defend/Destroy/Hunt/Expand), removed
    // 2026 as log-only dead weight once Defend/Destroy/Hunt never got a task category built for
    // them — see AiGoalScorer's own class comment.
    public enum AiTaskCategory
    {
        Reconnaissance,
        Economy,
        Management,
        Aggression,
    }

    // One concrete subtask type per the AI architecture doc's "название, состав армии, цель,
    // условия" structure — Kind IS the "название" (a fixed catalog, not free text); AiTask's own
    // Army/TargetHex fields are "состав армии"/"цель"; "условия" (movement left, AP affordable,
    // composition still eligible) are evaluated live by whichever AiTurnController tier owns
    // that Kind, never cached on the task itself — the same "непрерывная переоценка" principle
    // the doc's own section 04.2.2 already documents for task execution in general.
    public enum AiTaskKind
    {
        VisitHex, // Разведка · Задача 1 — covers Разведка's whole scope; the old Задача 2
                  // (ScoutResourceHexTask, long-range resource hunting) was removed 2026, the
                  // project owner's own call that VisitHex's citadel-wave coverage already
                  // discovers resource hexes as a side effect of exploring.
        BuildFacility, // Экономика · Задача 1
        ResourcesScrap, // Экономика · Задача 2 — добыча без постройки facility, see AiEconomyPlanner's own comment
        DrawCard, // Менеджмент
        ReserveArmy, // Менеджмент
        RepairUnit, // Менеджмент — починка раненого юнита/героя на своей Base, see AiManagementPlanner's own comment
                     // (owns the hand read AND the base/garrison-hex army oversight this needs — not Экономика's job)
        RaidWeakerArmy, // Агрессия · Задача 1 — see RaidWeakerArmyTask
        RaidReinforce, // Агрессия — critically wounded field army waits for a courier instead of
                        // marching home itself, see AiAggressionPlanner.TryRaidRegroupCandidates
    }

    public static class AiTaskCatalog
    {
        public static AiTaskCategory CategoryOf(AiTaskKind kind)
        {
            switch (kind)
            {
                case AiTaskKind.VisitHex:
                    return AiTaskCategory.Reconnaissance;
                case AiTaskKind.BuildFacility:
                case AiTaskKind.ResourcesScrap:
                    return AiTaskCategory.Economy;
                // RepairUnit falls through to the default Management case below.
                case AiTaskKind.RaidWeakerArmy:
                case AiTaskKind.RaidReinforce:
                    return AiTaskCategory.Aggression;
                default:
                    return AiTaskCategory.Management;
            }
        }
    }

    // A persistent subtask instance bound to one army — survives across AI turns until it
    // completes, gets abandoned (too many failed build attempts), or its army is preempted by a
    // higher-priority task (see AiTaskRegistry.Remove's own comment on why that's not a
    // resumable pause). DrawCard/ReserveArmy never actually get instantiated as one of these —
    // both always resolve within the same turn they're picked (see AiTurnController.Decide's
    // own Менеджмент tail), so persisting them would track nothing real.
    public class AiTask
    {
        public AiTaskKind Kind;
        public ArmyData Army;
        public HexCoord TargetHex;

        // BuildFacility/ResourcesScrap: which resource the facility/collector yields.
        public ResourceType? ResourceType;

        // RepairUnit only — which specific wounded member of Army this task is healing (Army
        // alone isn't enough since an army can have more than one wounded unit at once — see
        // AiManagementPlanner.TryStartRepairCandidates, one task per wounded unit).
        public UnitData TargetUnit;

        // RaidReinforce only — the critically wounded field army a courier is on its way to
        // rescue. Army is the COURIER here (left null at creation until AiAggressionPlanner.
        // DispatchReinforcementRoutine actually spawns it — see that method's own comment for why
        // mutating this field post-registration is safe), TargetHex is fixed at the wounded army's
        // hex at dispatch time (its rendezvous point — it deliberately stays put, see
        // TryRaidRegroupCandidates).
        public ArmyData TargetArmy;

        // BuildFacility only — mirrors the old AiEconomyTask's own blunt safeguard against a
        // permanently-stuck task (hex claimed by someone else, facility slot full) rather than a
        // precise diagnosis; see AiEconomyPlanner.MaxBuildAttempts.
        public int BuildAttempts;

        public string Reason;

        // VisitHex only (see VisitHexTask.TryFlee) — a ONE-TURN, resumable retreat: set whenever a
        // flight-to-garrison candidate was used to advance the task, cleared again the very next
        // continuation regardless of outcome, so a persistent threat produces "flee, resume, flee,
        // resume, ..." rather than a full march home. BuildFacility/ResourcesScrap don't use this
        // field at all — Экономика's own threat reaction is a hard cancel, nothing to flag.
        // RaidWeakerArmy doesn't use it either — see Retreating below, a different, one-way shape.
        public bool FledLastTurn;

        // RaidWeakerArmy only (see RaidWeakerArmyTask's own "Поведение" comment) — unlike
        // FledLastTurn's resumable one-turn detour, this is a ONE-WAY commitment: once true (an
        // outmatched threat, a target that stopped being known, or a dead-end assembly — see
        // AiTurnController.TryContinueRaidTask), every future continuation walks straight to the
        // garrison and NEVER resumes the original target, until it arrives and the task simply
        // ends there (freeing the army for a fresh raid task later, on the usual footing).
        public bool Retreating;

        // RaidWeakerArmy only — set when TryContinueRaidTask's own threat check finds a real
        // enemy army near the garrison WHILE task.Army is already standing on it (nothing to
        // retreat to — see that method's own comment; the project owner's own "Bastion Guard"
        // report, 2026-08-17, was this exact case producing a move-to-self no-op that stranded
        // the task instead). TargetHex becomes the threat's own hex rather than the usual
        // neutral/event/building pool. Exempts this task from AiConfig.maxConcurrentRaid (see
        // AiAggressionPlanner.TryRaidAssembleCandidates) and unlocks the stronger recruitment
        // tier that will pull an army off an ACTIVE task elsewhere, not just an idle one (see
        // AiAggressionPlanner.TryCitadelDefensePreemptCandidates) — defending the player's own
        // base outranks routine work. Temporary home for this reaction: per the project owner's
        // own call, this conceptually belongs in a proper Оборона/Border-Defense AiTaskCategory
        // once one exists (AI_ARCHITECTURE.html's own roadmap already earmarks it), not bolted
        // onto Агрессия forever — bolted on for now rather than building that category speculatively.
        public bool DefendingCitadel;

        public AiTaskCategory Category => AiTaskCatalog.CategoryOf(Kind);
    }

    // Per-player pool of in-flight tasks — the "уровень подзадачи" store the AI architecture doc
    // now names explicitly (see AI_ARCHITECTURE.html section 01). Replaces the old single-slot
    // AiEconomyTaskRegistry: several tasks of the same Kind can be active at once, up to whatever
    // cap each Level-1 planner's own starters enforce (e.g. AiScoutPlanner's own
    // MaxConcurrentVisitHex) by calling CountActive first.
    public static class AiTaskRegistry
    {
        private static readonly Dictionary<PlayerSetupData, List<AiTask>> ByPlayer =
            new Dictionary<PlayerSetupData, List<AiTask>>();

        public static void Clear() => ByPlayer.Clear();

        public static IReadOnlyList<AiTask> TasksFor(PlayerSetupData player) =>
            player != null && ByPlayer.TryGetValue(player, out List<AiTask> list)
                ? list
                : (IReadOnlyList<AiTask>)System.Array.Empty<AiTask>();

        public static AiTask TaskFor(PlayerSetupData player, ArmyData army) =>
            army != null ? TasksFor(player).FirstOrDefault(t => t.Army == army) : null;

        public static int CountActive(PlayerSetupData player, AiTaskKind kind) =>
            TasksFor(player).Count(t => t.Kind == kind);

        public static void Add(PlayerSetupData player, AiTask task)
        {
            if (player == null || task == null)
                return;
            if (!ByPlayer.TryGetValue(player, out List<AiTask> list))
            {
                list = new List<AiTask>();
                ByPlayer[player] = list;
            }
            list.Add(task);
        }

        // Preemption's other half (see AiEconomyPlanner's own economy-preemption comment) —
        // drops whatever task `army` currently holds, freeing it for a higher-priority Kind. The
        // dropped task is simply gone, not resumed from a checkpoint: VisitHex doesn't track
        // meaningful partial progress beyond the army's own live position (VisionSystem.Visited/
        // AiMapMemory already remember whatever ground was actually covered along the way), so the
        // next replan just proposes a fresh target the normal way — that IS the project owner's own
        // "будет помечено как невыполненное": logged at the preemption call site, not stored as a
        // lingering status flag nothing would read.
        public static void Remove(PlayerSetupData player, ArmyData army)
        {
            if (player == null || army == null || !ByPlayer.TryGetValue(player, out List<AiTask> list))
                return;
            list.RemoveAll(t => t.Army == army);
        }

        public static void Remove(PlayerSetupData player, AiTask task)
        {
            if (player != null && task != null && ByPlayer.TryGetValue(player, out List<AiTask> list))
                list.Remove(task);
        }
    }
}
