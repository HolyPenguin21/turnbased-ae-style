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
        // Development (P0, 2026-08-28, project owner's own spec) — Research + Production combined,
        // a single strategic vector standing at the same level as the five above so R&D never
        // competes as if it were Management. Future Research/Production AiTaskKinds map here in
        // CategoryOf. It is a full strategy-layer axis (AiStrategyDirector) and takes the
        // per-candidate axis tilt in AiStrategyLayer.Adjust, but is deliberately NOT a sixth equal
        // share of AiTurnBudget's fixed AP pool — see AiTurnBudget.Categories / OverBudgetRatio.
        Development,
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

        AirStrike, // Агрессия · Авиация — launch already-deployed aircraft, strike a known enemy
                    // target, and land safely at any owned airfield. See AirStrikeTask's own class
                    // comment for target selection/eligibility and AiAggressionPlanner.
                    // TryStartAirStrikeCandidates/TryContinueAirStrikeTask for orchestration. Never
                    // recruits — a sortie's whole roster is whatever's already at the airfield or
                    // already airborne (see AiTask.AirOutbound's own comment).

        AirRecon, // Разведка · Авиация — fallback-only: flies toward the enemy to reveal
                    // information when Агрессия has no actionable AirStrike target and the economy
                    // isn't resource-starved. See AirReconTask's own class comment and
                    // AiScoutPlanner.TryStartAirReconCandidates/TryContinueAirReconTask.

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
        //
        // Architectural boundary (2026-08-28 P1, project owner's own spec item 14): this task is
        // ONLY ever a MoveArmy toward home — it competes for AP in Decide's own arbiter exactly
        // like any other travel task and does NOTHING else. Every local, AP-free reorg primitive
        // (Collapse / garrison Overflow-split / Consolidate / Swap) lives EXCLUSIVELY in
        // AiTurnController.RunGarrisonReorgPhase, which runs once as the very last thing a turn
        // does. The two never overlap: ReturnForConsolidation only brings a stray army to the
        // hex where RunGarrisonReorgPhase's own lone-army-fold tier will absorb it on a later turn.
        ReturnForConsolidation,

        // Development (P0, 2026-08-28, project owner's own spec) — Research + Production combined
        // under one Level-1 planner (AiDevelopmentPlanner). A positioning task: it binds the
        // chosen Researcher/Assembler hero's army, walks it to the chosen Lab/Factory hex
        // (TargetHex), and once co-located hands off to a RunResearchProduction decision that
        // runs the headless Challenge (ResearchProductionSystem.RollChallenge) and, on success,
        // mints the produced card into AiHandData. The task is removed as soon as one Challenge
        // has been attempted (win or lose) or the hero/facility stops qualifying — a fresh
        // evaluation next turn re-decides whether to develop again. Maps to
        // AiTaskCategory.Development in AiTaskCatalog.CategoryOf.
        Develop,
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

    // AirStrike/AirRecon only (2026-08-26 multi-turn aviation spec, point 9) — the explicit name
    // for what AiTask.AirOutbound's own bool already encodes. Kept as a derived property on AiTask
    // rather than a second stored field on purpose — the spec's own "не должно существовать двух
    // независимых источников истины" — AirOutbound stays the one real field every existing
    // ContinueSortie/LaunchRoutine/TryStartAir* call site already reads and writes; this enum is
    // purely a clearer name for the same state, for new code and diagnostics to read by.
    // LoiterAtTarget added 2026-08-26 (repeat-strike spec) — AirStrike only, a multi-turn sortie
    // that already landed its first strike, deliberately staying parked on ActionHex to repeat the
    // attack once HasAirAttackedThisTurn resets on the next turn, before finally heading home.
    // AirRecon never uses this value — see AiTask.AirMissionPhase's own comment.
    public enum AiAirMissionPhase
    {
        ToAction,
        LoiterAtTarget,
        Returning,
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
                case AiTaskKind.AirStrike:
                    return AiTaskCategory.Aggression;
                case AiTaskKind.AirRecon:
                    return AiTaskCategory.Reconnaissance;
                case AiTaskKind.Develop:
                    return AiTaskCategory.Development;
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

        // RaidWeakerArmy only (2026-08-24 fix, "новая WorthIt-телеметрия логируется на каждом
        // шаге движения", project owner's own report) — AiAggressionPlanner.TryContinueRaidTask's
        // own diagnostic win-chance log used to fire on EVERY call (every movement step of a
        // multi-step trip, all within the same real turn), so one raid could print 5+ identical
        // "raid win chance ~89%" lines back to back with nothing actually different between them.
        // These four together are a COARSE fingerprint the log line compares itself against —
        // target hex, member count, summed Attack+Defense, and the threat's own Defense — logged
        // again once real game turn moves on, or once that fingerprint changes. Deliberately not a
        // complete change detector (2026-08-24 follow-up note, project owner's own report): army
        // HP, the threat's own Attack, or swapping one unit for a different one with the same
        // summed power all slip through unnoticed. Harmless for gameplay (the throttle is purely a
        // log-volume concern, nothing here gates a real decision) — good enough for "once per turn"
        // as it stands; only worth tightening if a real desync between what's logged and what's
        // actually happening turns up in practice.
        public int LastBattleEstimateLoggedTurn = -1;
        public HexCoord LastBattleEstimateTargetHex;
        public int LastBattleEstimateArmyMemberCount = -1;
        public float LastBattleEstimateArmyPower = float.NaN;
        public float LastBattleEstimateThreatDefense = float.NaN;

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

        // RaidWeakerArmy only (2026-08-26, project owner's own "не держать рейд бесконечно на
        // недостижимой цели" spec, item 5) — TryRaidAssembleCandidates' own stall-detection
        // snapshot: this task's own army member count, TargetHex, whether a recruit/hero card was
        // actually available to add, and the win chance against its current target, as of the last
        // time ANY of the four genuinely differed from the call before. RaidStallSinceTurn is the
        // turn number that snapshot was last refreshed — lazily initialized on this task's very
        // first evaluation (starts at -1, which always reads as "changed" the first time it's
        // checked, so a brand-new task can never read as already stalled). See AiConfig.
        // raidStallTurns for the elapsed-turns threshold and TryRaidAssembleCandidates for how this
        // also doubles as the "not enough force" log's own dedup key (an unchanged snapshot within
        // the same turn skips re-printing the identical line).
        public int RaidStallSinceTurn = -1;
        public int RaidStallMemberCount = -1;
        public HexCoord RaidStallTarget;
        public bool RaidStallHadRecruit;
        public float RaidStallWinChance = -1f;
        public int RaidLastLoggedTurn = -1;

        // RaidWeakerArmy only (2026-08-27, project owner's own log audit) — which
        // AiTurnContext.TurnNumber this raid task was first created on, stamped once at creation and
        // never moved. Unlike RaidStallSinceTurn (which resets on every recruit/retarget and so
        // only ever catches a raid with NOTHING available to add), this is a flat wall-clock: once
        // ctx.TurnNumber - RaidAssembleStartedTurn reaches AiConfig.raidAssembleMaxTurns and the
        // force still isn't IsReady, TryRaidAssembleCandidates abandons the target regardless of
        // whether one more recruit happens to be available — the "grows one body a turn forever
        // against an unwinnable camp" case the recruit-gated watchdog structurally can't see.
        public int RaidAssembleStartedTurn = -1;

        // Set (>= 0) when an AiOperation has adopted this task as one of its assets (2026-08-27
        // operations layer). The operation owns the task's TARGET and LIFECYCLE while this is set —
        // AiAggressionPlanner's own retarget-shop and stall/deadline watchdogs skip an
        // operation-owned raid task entirely (AiOperationPlanner re-points and abandons it instead,
        // per the operation's phase machine), and AiTurnController.Decide boosts every candidate
        // carrying an operation-owned task by AiConfig.operationDirectiveBoost. Cleared back to -1
        // the moment the operation completes or aborts, handing the task back to the ordinary
        // planners on the usual footing.
        public int OperationId = -1;

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

        // AirStrike/AirRecon only — the owned airfield the sortie is currently committed to
        // landing at, chosen fresh by AiAviationSupport.TryPlanSortie/TryPlanSortiePreferForward
        // Landing/TryReplan every time the plan is (re)validated, per the spec's "any owned
        // airfield, never hard-coded to the launch airfield, never held onto once a better one is
        // reachable" rule (2026-08-26 sharpened further, project owner's own spec item 2) — never
        // trusted stale across a replan, same as every other recomputed-live field on this class.
        public HexCoord LandingHex;

        // AirStrike/AirRecon only — true while this sortie is still flying OUTBOUND toward the
        // objective (TargetHex is the enemy hex to strike / the recon hex to reveal); false once
        // that leg is done (target resolved/gone, or the recon hex reached), at which point
        // TargetHex is repointed at LandingHex for the return leg. A ONE-WAY flip, same shape as
        // Retreating above, just aviation's own copy since the trigger differs (finishing a leg,
        // not reacting to a threat) — set once per leg-transition by TryContinueAirStrikeTask/
        // TryContinueAirReconTask, never toggled back.
        // AirStrike/AirRecon only — the real stored phase (2026-08-26 repeat-strike spec flips
        // which of AirMissionPhase/AirOutbound is the source of truth: LoiterAtTarget is a genuine
        // third state AirOutbound's own bool cannot represent, so THIS field is now authoritative —
        // AirOutbound below is the derived alias instead, kept only because every pre-existing
        // ContinueSortie/LaunchRoutine/TryStartAir* call site already reads/writes it as a bool and
        // none of them need to know about LoiterAtTarget specifically (it collapses to "not
        // outbound" for all of them, same as Returning). AirRecon never sets this to
        // LoiterAtTarget — see that enum's own comment.
        public AiAirMissionPhase AirMissionPhase = AiAirMissionPhase.ToAction;

        // AirStrike/AirRecon only — derived from AirMissionPhase above, never a second source of
        // truth (multi-turn aviation spec, point 9). True only for ToAction; both LoiterAtTarget and
        // Returning read as false here, since every existing reader of this bool only ever needed
        // to distinguish "still heading to the objective" from "everything else."
        public bool AirOutbound
        {
            get => AirMissionPhase == AiAirMissionPhase.ToAction;
            set => AirMissionPhase = value ? AiAirMissionPhase.ToAction : AiAirMissionPhase.Returning;
        }

        // AirStrike only (2026-08-26 repeat-strike spec) — how many times this sortie has actually
        // struck its target hex so far (the first strike, a side effect of the move that lands the
        // army on ActionHex, counts as 1 the moment that arrival is observed — see
        // AiAggressionPlanner.TryContinueAirStrikeTask). Capped at AiConfig.maxStrikesPerSortie —
        // once reached, TryEnterLoiterAtTarget/TryContinueLoiterAtTarget refuse to hold the army over
        // the target any longer, regardless of remaining fuel margin (spec point 10: "расширение до
        // трёх и более ударов должно быть отдельным балансным решением"). AirRecon never uses this.
        public int AirStrikesCompleted;

        // AirStrike/AirRecon only (2026-08-26 multi-turn aviation spec) — true while this sortie's
        // current committed plan is a AiAviationSupport.MultiTurnSortie (a route spanning more than
        // one real game turn) rather than a same-turn AiAviationSupport.Sortie. Set fresh by
        // AiAviationSupport.ContinueSortie every single step, purely descriptive/for logging — never
        // trusted to decide anything, same "recomputed live, never stale" rule every other
        // AiAviationSupport-owned field on this task already follows.
        public bool IsMultiTurnSortie;

        // Develop only (P0, 2026-08-28) — which of the two flows this positioning task runs once
        // the hero reaches TargetHex. Pinned at creation (a hero carrying BOTH Researcher and
        // Assembler could serve either Facility, so this isn't re-derived from the hero's tags),
        // read by AiDevelopmentPlanner.TryContinueDevelopTask. Null for every other Kind.
        public ResearchProductionMode? DevelopMode;

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
