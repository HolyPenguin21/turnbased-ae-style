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

        SecureBase, // Оборона · initial-defence for a fresh/captured/weakened second base — see
                    // SecureBaseTask's own class comment for trigger/lifecycle and
                    // AiDefencePlanner.TryStartSecureBaseCandidates/TryContinueSecureBaseTask for
                    // orchestration. Distinct from DefendCitadel (which never starts without an
                    // actual known/estimated threat nearby — see that Kind's own comment) — this
                    // one reacts to the GARRISON'S OWN roster, not the enemy, and completes the
                    // moment AiArmyRoles.IsBaseGarrisonSecure says the base can stand on its own.

        // Менеджмент — a single stranded, untasked lone field army (GarrisonReorgTask.
        // FindStrandedWeakArmies) walking itself home to the nearest own garrison hex so the
        // existing IdleBalance lone-army-fold tier (FindLoneArmyFoldMove) can pick it up once it
        // arrives. 2026-08-24 P0 fix (project owner's own code-review report on Feature 4B, the
        // original same-day design): the original AiTurnController.RunStrandedArmyRecovery tried
        // to move the army directly, but only from the very END of the turn, after the main
        // Decide() loop had usually already spent nearly all of it — and saved no state at all
        // when the move couldn't be issued right then, despite a comment claiming it "continues
        // moving on subsequent turns". A stranded army effectively never made it home. Now a REAL
        // persistent task, the same way every other multi-turn AI activity in this codebase
        // already works (Raid/Defence/BuildBase/BuildFacility/Recon): registered once by
        // AiTurnController.RunStrandedArmyRecovery (detection/registration only now, no movement
        // of its own any more), advanced every ordinary turn by AiManagementPlanner.
        // AdvanceReturnForConsolidationTask via AiTurnController.Decide's own per-step loop like
        // any other in-flight task, and removed the moment the army actually reaches its home hex
        // — see that method's own comment.
        ReturnForConsolidation,
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
                // RepairUnit and ReturnForConsolidation both fall through to the default
                // Management case below.
                case AiTaskKind.RaidWeakerArmy:
                case AiTaskKind.RaidReinforce:
                case AiTaskKind.BuildBase:
                    return AiTaskCategory.Aggression;
                case AiTaskKind.DefendCitadel:
                case AiTaskKind.SecureBase:
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

        // DefendCitadel — which of this player's own garrisoned hexes (the starting citadel, or a
        // later-founded Base, see AiTurnController.OwnGarrisonHexes/NearestOwnGarrisonHex) this task
        // patrols around/turtles back to. Set once at task creation by TryStartDefenceCandidatesFor's
        // own per-home loop (see AiDefencePlanner.TryStartDefenceCandidates) and never recomputed
        // afterward — a task started at one base stays anchored there even if a closer base were
        // founded later, same "committed, not re-shopped every step" principle TargetHex itself
        // already follows for RaidWeakerArmy.
        //
        // SecureBase (2026-08-24) — reuses this exact same field for the same purpose: the base
        // hex this task is securing, set once at creation (AiDefencePlanner.
        // TryStartSecureBaseCandidates) and never recomputed. TargetHex, separately, is the
        // courier's own current travel destination (always equal to HomeHex here — SecureBase never
        // has a moving target the way DefendCitadel's Active posture does), kept distinct purely so
        // SecureBaseTask's own travel-phase check reads the same "Army.Hex vs TargetHex" shape every
        // other travel-stage task in this codebase already uses.
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

        // BuildBase only (2026-08-23, project owner's own report/spec; turn-boundary fix
        // 2026-08-24 — same bug class as GarrisonSeedStartedTurn below) — the TURN NUMBER
        // (AiTurnContext.TurnNumber) this task first found itself unable to afford the Base card's
        // own AP/resource cost, -1 until then. Stamped once, the first time AiAggressionPlanner.
        // TryContinueBuildBaseTask's own Wait branch is hit; elapsed wait is computed fresh each
        // check as `ctx.TurnNumber - BuildBaseWaitStartedTurn`, never a plain incrementing counter
        // — that method runs once per Decide() STEP, not once per real game turn, so a naive
        // `BuildBaseWaitTurns++` could blow past AiConfig.buildBaseMaxWaitTurns within a single
        // turn (project owner's own playtest report, 2026-08-24: Grimm/Vashti both hit "stuck
        // unable to afford" after 5 CONSECUTIVE STEPS in the same turn, not 5 real turns). A
        // hero-led combat army is expensive to tie up indefinitely on a build the AI can't actually
        // pay for — once real elapsed turns exceeds AiConfig.buildBaseMaxWaitTurns the task gives up
        // and frees the army rather than holding it hostage to a plan that's gone stale (the
        // project owner's own "не держать шестиюнитную армию десять ходов" report).
        public int BuildBaseWaitStartedTurn = -1;

        // BuildFacility only — true if ResourceType had NO income source anywhere at task creation
        // (see BuildFacilityTask.HasIncomeSource), i.e. this build was ever only justified by
        // BuildFacilityTask.ScarcityBonus's own buildNoIncomeBonus branch, not merely "already
        // produced somewhere, just low in stock". Re-checked once the hero actually arrives (see
        // AiEconomyPlanner.AdvanceEconomyTask's own arrival cancel) — if some OTHER source of this
        // same type came online during the (possibly multi-turn) trip, the original justification is
        // gone and the build is now redundant, so the task cancels and frees the hero instead of
        // finishing a build nobody needs any more. Deliberately NOT re-derived from scratch at
        // arrival (current HasIncomeSource alone can't tell "always had income, just scarce" apart
        // from "started with none, gained one since") — this flag is what actually changed.
        public bool StartedWithNoIncome;

        public string Reason;

        // VisitHex only (see VisitHexTask.TryFlee) — a ONE-TURN, resumable retreat: the TURN
        // NUMBER a flight-to-garrison candidate last advanced this task, -1 meaning "never fled
        // (yet)". A persistent threat re-triggers this every turn it's still seen ("flee, resume,
        // flee, resume, ..."), rather than a full march home. Deliberately keyed on the TURN
        // NUMBER, not "the previous continuation call" (that was the old FledLastTurn bool,
        // replaced 2026-08-23 — project owner's own report: AiTurnController.Decide calls
        // TryContinueVisitTask several times within the SAME turn as the army walks its movement
        // budget down, and the old flag reset itself on the very next CALL rather than the next
        // TURN — a scout that fled once could have an ordinary Recon candidate immediately
        // override the retreat before it ever reached safety, in that same turn). While
        // FledOnTurn == the current turn, VisitHexTask.TryFlee keeps proposing the garrison
        // regardless of whether the triggering threat is still in range THIS call (it may have
        // simply fallen out of scoutFleeRadius as the scout moved away) — covers both "still
        // mid-retreat, needs more flee steps this turn" and "already reached the garrison earlier
        // this same turn, must not resume routine scouting until next turn" with one check.
        // BuildFacility/ResourcesScrap don't use this field at all — Экономика's own threat
        // reaction is a hard cancel, nothing to flag. RaidWeakerArmy doesn't use it either — see
        // Retreating below, a different, one-way shape.
        public int FledOnTurn = -1;

        // VisitHex only (2026-08-24, project owner's own root-cause report) — the TURN NUMBER
        // (AiTurnContext.TurnNumber) this task's own army last actually changed hex, whether via a
        // routine scouting step or a flee move alike; stamped once at task creation to the CREATION
        // turn (never -1 — a fresh task hasn't stalled yet, so the watchdog below must not read a
        // never-set field as "ages ago no progress") and again by AiTurnController.MoveArmyRoutine
        // every time a VisitHex army's hex actually differs from where it started this step. Read
        // by AiScoutPlanner.TryContinueVisitTask (AiConfig.visitHexStallTurns) to tell a task truly
        // stuck — no legal step for several real turns running (fully boxed in by fog, permanently
        // unaffordable, or a stale flee target it can no longer progress toward) — apart from one
        // just waiting out this turn's movement/AP budget: same "elapsed = ctx.TurnNumber -
        // lastLandedTurn" stall-clock shape AssemblyProgressTurn/GarrisonSeedStartedTurn already use
        // elsewhere in this class, so a handful of no-progress CALLS inside the same turn can never
        // trip it early — only actual turns passing with nothing moving can.
        public int VisitLastProgressTurn = -1;

        // RaidWeakerArmy and DefendCitadel (2026-08-21 — Оборона's own local retreat, see
        // AiDefencePlanner.BuildPostureDecision, reuses this exact shape rather than inventing a
        // second one). Unlike FledOnTurn's resumable one-turn detour, this is a ONE-WAY
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
        // task-claimed army sitting at the garrison hex is still being built — for Active-posture
        // DefendCitadel specifically, one input (alongside AssemblyProgressTurn below) into whether
        // it's eligible for CollapseTemporaryAssembly's own atomic fold back to the garrison
        // (RaidWeakerArmy is never eligible for that fold at all any more, whatever this flag says —
        // see GarrisonReorgTask.IsCollapseEligible's own comment) — or already a finished force
        // (off-limits to generic Reorg entirely — see GarrisonReorgTask.IsProtectedTaskArmy's own
        // comment) — see GarrisonReorgTask.FindCollapseMove's own comment. Nothing else reads this
        // (except IsCollapseEligible above); it exists purely so
        // GarrisonReorgTask (which has no idea how any one task category scores its own readiness)
        // doesn't need to re-derive it.
        public bool StillAssembling;

        // DefendCitadel only (2026-08-23 fix, project owner's own report/spec) — which
        // AiTurnContext.TurnNumber a recruit/strengthen/merge last actually landed on this task's
        // own army, -1 until the first one ever does. GarrisonReorgTask.FindCollapseMove reads this
        // to tell a genuinely stalled Active-posture assembly (nothing added THIS turn — safe to
        // fold back to the garrison) apart from one making real cross-turn progress (2 units → 4 →
        // 5 over several turns is real progress, not something Collapse should ever be allowed to
        // erase just because the composition target isn't fully met yet). Set by
        // AiAggressionPlanner.AssembleRaidForceRoutine/AiDefencePlanner.StrengthenDefenceForceRoutine
        // on a successful landing only — a failed transfer changes nothing about the roster, so it
        // must not reset the stall clock either. Left at -1 forever for RaidWeakerArmy — Collapse
        // never runs against that Kind at all any more (see FindCollapseMove's own comment), so
        // nothing ever reads it there.
        public int AssemblyProgressTurn = -1;

        // BuildBase only (Feature 2, 2026-08-24, project owner's own report — "captured/built bases
        // sitting undefended and getting flagged 'unguarded'"): true from the moment the building
        // itself finishes constructing (BuildBaseRoutine) until either a garrison-seed transfer
        // actually lands (AiAggressionPlanner.AdvanceGarrisonSeed) or GarrisonSeedStartedTurn's
        // own timeout fires. Before this fix BuildBaseRoutine released/removed the task the INSTANT the building
        // existed, so the builder army was immediately free for Raid/Defence to reclaim, leaving a
        // brand-new empty Garrison — exactly the "enemy building ... unguarded" opportunity
        // RaidWeakerArmyTask.FindTarget's own Section 5 already looks for, just now pointed back at
        // US. See AiAggressionPlanner.TryContinueBuildBaseTask's own AwaitingGarrisonSeed branch.
        public bool AwaitingGarrisonSeed;

        // BuildBase · AwaitingGarrisonSeed's own stale-task escape hatch — the TURN NUMBER
        // (AiTurnContext.TurnNumber) this phase first started (set once, by
        // AiAggressionPlanner.BuildBaseRoutine, the same call that flips AwaitingGarrisonSeed
        // true), -1 until then. 2026-08-24 P1 fix (project owner's own code-review report): this
        // used to be a plain incrementing counter (GarrisonSeedWaitTurns++) bumped every time
        // AiAggressionPlanner.AdvanceGarrisonSeed ran — but that method is called once per
        // Decide() STEP, not once per actual game turn, and a single turn can call it many times
        // as the AI works through its own per-step candidate loop, so garrisonSeedMaxWaitTurns
        // could time out within ONE turn instead of after that many real turns, defeating the
        // whole point of the timeout (a new base could end up abandoned with an empty garrison
        // well before N real turns had actually passed). Elapsed turns are now computed fresh
        // wherever the timeout is checked, as `ctx.TurnNumber - GarrisonSeedStartedTurn` (see
        // AdvanceGarrisonSeed) — no separate "did I already count this turn" bookkeeping needed,
        // the same turn-boundary simplification AiTask.AssemblyProgressTurn already uses for its
        // own stall clock instead of an incrementing counter.
        public int GarrisonSeedStartedTurn = -1;

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
