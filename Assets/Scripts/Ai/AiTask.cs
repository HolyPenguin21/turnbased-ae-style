using System.Collections.Generic;
using System.Linq;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Ai
{
    // Level-1 goal categories that run as persistent, CONCURRENT task pools — distinct from
    // AiGoalKind (see AiGoal.cs), which is a single competitive pick among Defend/Destroy/Hunt/
    // Expand (see AiGoalScorer's own class comment on why those genuinely compete for one slot).
    // Recon/Economy/Management don't compete that way: several tasks across all three run in the
    // same turn, each on its own army — the only limit is per-Kind concurrency (see
    // AiTaskRegistry.CountActive) and AiResourcePool's own no-double-booking guard, not a score
    // comparison between categories.
    public enum AiTaskCategory
    {
        Reconnaissance,
        Economy,
        Management,
    }

    // One concrete subtask type per the AI architecture doc's "название, состав армии, цель,
    // условия" structure — Kind IS the "название" (a fixed catalog, not free text); AiTask's own
    // Army/TargetHex fields are "состав армии"/"цель"; "условия" (movement left, AP affordable,
    // composition still eligible) are evaluated live by whichever AiTurnController tier owns
    // that Kind, never cached on the task itself — the same "непрерывная переоценка" principle
    // the doc's own section 04.2.2 already documents for task execution in general.
    public enum AiTaskKind
    {
        VisitHex, // Разведка · Задача 1
        ScoutResourceHex, // Разведка · Задача 2
        BuildFacility, // Экономика · Задача 1
        ResourcesScrap, // Экономика · Задача 2 — добыча без постройки facility, see AiEconomyPlanner's own comment
        DrawCard, // Менеджмент
        ReserveArmy, // Менеджмент
    }

    public static class AiTaskCatalog
    {
        public static AiTaskCategory CategoryOf(AiTaskKind kind)
        {
            switch (kind)
            {
                case AiTaskKind.VisitHex:
                case AiTaskKind.ScoutResourceHex:
                    return AiTaskCategory.Reconnaissance;
                case AiTaskKind.BuildFacility:
                case AiTaskKind.ResourcesScrap:
                    return AiTaskCategory.Economy;
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

        // BuildFacility: which resource the facility being built yields. ScoutResourceHex: which
        // resource type this trip is hunting for (see AiTurnController's own WantedResourceType).
        public ResourceType? ResourceType;

        // BuildFacility only — mirrors the old AiEconomyTask's own blunt safeguard against a
        // permanently-stuck task (hex claimed by someone else, facility slot full) rather than a
        // precise diagnosis; see AiEconomyPlanner.MaxBuildAttempts.
        public int BuildAttempts;

        public string Reason;

        // VisitHex/ScoutResourceHex only — set whenever TryFleeTarget's flight-to-garrison
        // candidate was used to advance this task, cleared again the very next continuation
        // regardless of outcome. Caps fleeing to a single consecutive turn: the project owner's
        // own call that marching all the way home is "too harsh" for a scout — one turn away
        // from a known threat, then back to normal target search (which may propose fleeing
        // again next turn if the threat is still there, rather than committing to a multi-turn
        // trip). See AiTurnController.TryFleeTarget.
        public bool FledLastTurn;

        public AiTaskCategory Category => AiTaskCatalog.CategoryOf(Kind);
    }

    // Per-player pool of in-flight tasks — the "уровень подзадачи" store the AI architecture doc
    // now names explicitly (see AI_ARCHITECTURE.html section 01). Replaces the old single-slot
    // AiEconomyTaskRegistry: several tasks of the same Kind can be active at once, up to whatever
    // cap AiTurnController's own starters enforce (MaxConcurrentVisitHex/
    // MaxConcurrentScoutResourceHex) by calling CountActive first.
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

        // Preemption's other half (see AiTurnController's own economy-preemption comment) —
        // drops whatever task `army` currently holds, freeing it for a higher-priority Kind. The
        // dropped task is simply gone, not resumed from a checkpoint: neither VisitHex nor
        // ScoutResourceHex track meaningful partial progress beyond the army's own live position
        // (VisionSystem.Visited/AiMapMemory already remember whatever ground was actually
        // covered along the way), so the next replan just proposes a fresh target the normal
        // way — that IS the project owner's own "будет помечено как невыполненное": logged at
        // the preemption call site, not stored as a lingering status flag nothing would read.
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
