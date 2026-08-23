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
        Defence,
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
        BuildBase, // Агрессия · Задача 2 — found an additional base toward the known enemy
                    // citadels (2026-08-21, project owner's own spec) — see BuildBaseTask's own
                    // class comment for target selection, AiAggressionPlanner.
                    // TryStartBuildBaseCandidates/TryContinueBuildBaseTask for trigger/composition/
                    // continuation/cancel.
        DefendCitadel, // Оборона — full redesign 2026-08-21 (project owner's own spec), triggers/
                        // composition retuned 2026-08-22: ONE task/army cycling through three
                        // Posture values (see AiDefencePosture) instead of a single reactive "attack
                        // the threat" shape — Patrol (triggers on CheatEstimateRaiderThreat, fixed
                        // composition), Active (triggers on any known sighting within
                        // AiConfig.defenceReactionRadius of a Base hex, dynamic 60/40-vs-that-army
                        // composition — see AiConfig.defenceActiveWinChance), Turtle (a known threat
                        // is inside AiConfig.siegeRadius of the citadel and beats us — see
                        // AiDefencePlanner.IsUnderSiege, a live per-player predicate ALSO read by
                        // AiAggressionPlanner to force-recall active raids, not a fourth task kind
                        // of its own). See AiDefencePlanner's own class comment for the full
                        // trigger/composition breakdown. Split out of Агрессия 2026-08-20 (was
                        // AiTask.DefendingCitadel, a flag bolted onto RaidWeakerArmy) into its own
                        // first-class category; this redesign keeps that same single Kind.
    }

    // DefendCitadel-only — which of the three behaviors this turn's continuation resolves to,
    // recomputed fresh every call (same "непрерывная переоценка" principle as Retreating below),
    // never stored as anything more durable than "what applies THIS step" — see
    // AiDefencePlanner.TryContinueDefenceTask for the actual decision tree.
    public enum AiDefencePosture
    {
        Patrol,
        Active,
        Turtle,
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
                case AiTaskKind.BuildBase:
                    return AiTaskCategory.Aggression;
                case AiTaskKind.DefendCitadel:
                    return AiTaskCategory.Defence;
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

        // DefendCitadel only for now — which of this player's own garrisoned hexes (the starting
        // citadel, or a later-founded Base, see AiTurnController.OwnGarrisonHexes/
        // NearestOwnGarrisonHex) this task patrols around/turtles back to. Set once at task creation
        // by TryStartDefenceCandidatesFor's own per-home loop (see AiDefencePlanner.
        // TryStartDefenceCandidates) and never recomputed afterward — a task started at one base
        // stays anchored there even if a closer base were founded later, same "committed, not
        // re-shopped every step" principle TargetHex itself already follows for RaidWeakerArmy.
        // RaidWeakerArmy/RaidReinforce deliberately don't use this field at all, even though their
        // own retreat/regroup now also targets the nearest own base (2026-08-21) — that case
        // recomputes AiTurnController.NearestOwnGarrisonHex fresh every call instead of storing it
        // here (see AiAggressionPlanner.TryContinueRaidTask's own homeHex), the project owner's own
        // call: a fleeing raid should always head for whichever base is genuinely closest RIGHT NOW,
        // not whichever one it happened to start near — the opposite stability tradeoff from
        // DefendCitadel's own patrol above. Every task kind besides DefendCitadel just carries the
        // default(HexCoord) it's never read.
        public HexCoord HomeHex;

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

        // RaidWeakerArmy and DefendCitadel (2026-08-21 — Оборона's own local retreat, see
        // AiDefencePlanner.BuildPostureDecision, reuses this exact shape rather than inventing a
        // second one). Unlike FledLastTurn's resumable one-turn detour, this is a ONE-WAY
        // commitment: once true (an outmatched threat, a target that stopped being known, a
        // dead-end assembly, or — DefendCitadel only — a locally outmatched encounter or a
        // critically wounded army standing down), every future continuation walks straight to the
        // garrison and NEVER resumes the original target/patrol cycle, until it arrives and the
        // task simply ends there (freeing the army — a fresh task, RaidWeakerArmy or DefendCitadel
        // alike, can claim it again later on the usual footing). DefendCitadel's own Turtle posture
        // (see IsUnderSiege) explicitly clears this on entry — its own march-home already
        // supersedes a local retreat in progress.
        public bool Retreating;

        // DefendCitadel only — which of Patrol/Active/Turtle this task is currently reading as
        // (see AiDefencePosture's own comment). Purely descriptive/for logging between calls —
        // TryContinueDefenceTask recomputes it fresh every step rather than trusting the stored
        // value to decide anything.
        public AiDefencePosture Posture;

        // DefendCitadel · Patrol only — this player's own extraction-facility hexes (within
        // AiConfig.patrolRadius of the citadel) already visited this patrol cycle. Cleared
        // whenever a fresh Patrol cycle starts (task creation, or converting back from
        // Active/Turtle into Patrol with nothing left over from before). Empty/all-covered means
        // "nothing left to patrol" — see AiDefencePlanner.FindPatrolTarget.
        public HashSet<HexCoord> PatrolVisited;

        // RaidWeakerArmy and DefendCitadel only — true from task creation until the composition
        // read as strong enough against whatever this task is being sized against (RaidWeakerArmy: a
        // raid target's defense, via RaidWeakerArmyTask.IsReady; DefendCitadel: Patrol's own fixed
        // member-count target or Active's dynamic WorthIt win-chance vs a sighted army, per
        // AiDefencePlanner's own IsComposedReady), false from that point on. Set every step by
        // TryRaidAssembleCandidates/TryStartDefenceCandidates — same
        // "recomputed fresh, never trusted stale" rule Posture above already follows — so it's
        // never more than one step out of date. GarrisonReorgTask reads this to decide whether a
        // task-claimed army sitting at the garrison hex is still being built (safe to keep folding
        // members into the garrison between recruits) or already a finished force just waiting on
        // AP to depart (must NOT be pulled apart) — see GarrisonReorgTask.FindAssemblingArmyFoldMove's
        // own comment. Nothing else reads this; it exists purely so GarrisonReorgTask (which has no
        // idea how any one task category scores its own readiness) doesn't need to re-derive it.
        public bool StillAssembling;

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
