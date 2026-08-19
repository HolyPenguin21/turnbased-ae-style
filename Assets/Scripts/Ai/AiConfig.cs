using UnityEngine;

namespace Game.Ai
{
    // Single tunable-numbers asset for every static AI class (AiTurnController, AiScoutPlanner,
    // AiEconomyPlanner, AiGoalScorer, AiArmyRoles) — same "one asset, referenced wherever needed"
    // idea as Game.Core.GameConfig, but for AI tuning specifically so a designer can retune the AI
    // without touching code/recompiling. Loaded lazily via Resources rather than wired as a
    // [SerializeField] on some scene object (the way GameConfig itself is) — nothing needs to
    // remember to drag this into an inspector field, and every pure stateless static class
    // reading it (several calls deep, no natural place to carry an instance reference) just reads
    // AiConfig.Current directly. Requires exactly one asset at Assets/Resources/AiConfig.asset —
    // create it via Assets/Create/Game/AI Config, in a folder named "Resources" (any depth under
    // Assets), keeping the file itself named "AiConfig".
    [CreateAssetMenu(fileName = "AiConfig", menuName = "Game/AI Config")]
    public class AiConfig : ScriptableObject
    {
        private static AiConfig _current;

        public static AiConfig Current => _current != null ? _current : (_current = Resources.Load<AiConfig>("AiConfig"));

        [Header("Turn Loop")]
        // Guards against an accidental infinite loop — not a real gameplay limit, just a safety
        // net (a normal turn resolves in well under this many steps).
        public int maxStepsPerTurn = 12;

        [Header("Task Arbiter — Category Base Weights")]
        // Every candidate action a turn could take gets a Score in this same shared space, and
        // the single highest-scoring one wins the step (see AiTurnController.Decide). Tuned so
        // the everyday case still lands in the old Economy > Recon > Management order, without
        // hard-coding that order — a weak Economy target and a strong Recon one (e.g.
        // raidCounterAttackBonus) CAN cross.
        public float economyBaseWeight = 200f;
        public float reconBaseWeight = 150f;
        // Above economyBaseWeight on purpose — moving an army to attack costs more AP overall
        // (activation + the eventual fight) than either Экономика or Разведка's own moves, so a
        // viable raid should generally win the arbiter once one exists at all. The project owner's
        // own note: this is also the natural seed for a LATER AP-reservation mechanic (saving up
        // toward wanting to win the initiative roll at turn start) — not built yet, just why this
        // number sits where it does.
        public float aggressionBaseWeight = 220f;
        public float managementBaseWeight = 50f;

        [Header("Разведка — Задача 1 (Посещение хекса)")]
        public int maxConcurrentVisitHex = 3;
        // How far past the map's own nearest still-unvisited hex (measured from the citadel) a
        // Задача 1 candidate is still allowed to be, so visiting sweeps outward from the citadel
        // "as a wave" rather than beelining for whatever's farthest. Агрессия no longer shares
        // this band (RaidWeakerArmyTask's own target pool isn't wavefront-bounded at all — see
        // its own class comment) — VisitHexTask is the only user now.
        public int visitRingBand = 3;
        // scoutProximityWeight is also reused by RaidWeakerArmyTask's own ProximityScore —
        // "closer to the mover" means the same thing whether the target is unexplored fog or a
        // known raid target.
        public float scoutProximityWeight = 5f;
        public float freshNeighborWeight = 4f;
        public float citadelDistancePenalty = 3f;

        [Header("Разведка — приоритет передвижения по ходам")]
        // From this turn on, a Recce army's own routine MoveArmy score (Задача 1/2 only — see
        // AiTurnController.ReconMoveWeight, the sole reader) starts tapering off instead of
        // staying flat at reconBaseWeight forever. Early game, scouting SHOULD win the arbiter
        // over everything else — there's nothing else worth doing with a fresh map. Once the
        // early reveal rush is over, though, a Recce army kept outbidding Агрессия/Экономика's
        // own bigger-army moves turn after turn purely because reconBaseWeight (150) plus a good
        // freshNeighborWeight hit could still clear aggressionBaseWeight (220) minus that raid's
        // own citadel-distance penalty (the project owner's own report, 2026-08-16, AiDebug.log:
        // scouting hops kept winning against a raid army already mid-attack). Assembly/request
        // candidates (SpawnReconArmy/AssembleRecceScout/RequestReconArmy) are untouched — the AI
        // should keep building its scout pipeline even once actually walking it around stops
        // being the AP priority.
        public int reconPriorityDecayStartTurn = 5;
        // Flat reduction per turn past reconPriorityDecayStartTurn, subtracted from
        // reconBaseWeight only for a MoveArmy candidate — never below reconPriorityDecayFloor.
        public float reconPriorityDecayPerTurn = 15f;
        // A scouting move should still beat outright idleness (managementFallbackHighScore/Low)
        // even once fully decayed — this floor sits comfortably above those.
        public float reconPriorityDecayFloor = 60f;

        [Header("Разведка — сборка Recce-состава")]
        // Added on top of reconBaseWeight (negative) — no empty army anywhere to receive a Recce
        // card/unit yet. Below a real recon move, generally above Менеджмент's own flat Reserve
        // fallback (managementFallbackHighScore/Low) so a genuine Разведка need still wins.
        public float reconRequestArmyPenalty = -100f;
        // Added on top of reconBaseWeight (negative) — an empty army exists, waiting on a
        // matching Recce card from hand. Smaller than reconRequestArmyPenalty (closer to done),
        // and comfortably above AiManagementPlanner's own flat playRecceCardBonus placement so
        // Разведка's own need for THIS card outranks Менеджмент's opportunistic one.
        public float reconRequestCardPenalty = -60f;
        // Added on top of reconBaseWeight (positive) — a Recce-tagged unit/hero is already
        // deployed and sitting on the SAME hex as an empty army, just buried inside a bigger
        // army; see AiScoutPlanner.FindBuriedRecceUnit for why this is rare in practice.
        public float reconAssembleBonus = 20f;

        [Header("Агрессия — Задача 1 (Зачистка нейтралов/эвентов)")]
        // Target = a known neutral army and/or the Hex Event it may be guarding — composition is
        // no longer a fixed predicate (see RaidWeakerArmyTask's own class comment): assembled/
        // picked via WorthIt against whichever target is chosen, up to this many raid tasks
        // running at once.
        public int maxConcurrentRaid = 2;
        // Added on top of the normal proximity score for a candidate whose target is ALREADY
        // beatable by an existing idle army as-is (RaidWeakerArmyTask's own "fast path" — no
        // assembly needed) — a ready win this turn should generally outrank a target that still
        // needs several turns of assembling first.
        public float raidReadyArmyBonus = 20f;
        // A known non-neutral (real player) army within raidThreatRadius of the raiding army's own
        // hex — if our current force still beats it, added on top of the normal score for
        // attacking THAT army this turn instead of the original neutral/event target (see
        // RaidWeakerArmyTask's own threat-reaction comment); if our force does NOT beat it, this
        // task retreats to the garrison instead — no bonus applies to that branch at all, it isn't
        // a scored candidate, just a forced redirect.
        public float raidCounterAttackBonus = 30f;
        public int raidThreatRadius = 2;
        // Added on top of the normal continuation score (AiTurnController.TryContinueRaidTask's
        // "advance toward target" leg only) once a raid task is READY and actually travelling —
        // per the user's own call: an army already "на задании" (mid-mission) must reliably keep
        // moving ahead of Разведка's own routine scouting hops turn after turn, not just win the
        // arbiter when everything else happens to line up. RaidWeakerArmyTask.ScoreForContinuation
        // already drops the citadel-distance penalty for this same reason (see its own comment) —
        // this bonus is the flat top-up on top of that, sized to clear even a fully-fresh
        // (pre-reconPriorityDecayStartTurn) recon score at its own best case.
        public float raidCommittedBonus = 40f;
        // Recall step's own score — an idle army elsewhere on the map walking back toward the
        // garrison to be folded into an assembling raid force (see RaidWeakerArmyTask's own
        // "Композиция" comment). Deliberately modest: real progress (an actual attack move) should
        // always outrank prep work, but prep work still needs to actually happen instead of never
        // winning arbitration against every other candidate every single step.
        public float raidRecallScore = 30f;
        // Раздел 5 — "рейд экономики" — a temporary, testing-only extension bundled into THIS task
        // rather than its own future category (the project owner's own call, 2026): a known enemy
        // (non-neutral) Base building with no known guard, or one whose known guard our current
        // force already beats, is also offered as a candidate target, same WorthIt gate as any
        // neutral/event target. Expect this pair to move to its own dedicated task later.
        public float raidBuildingUndefendedBonus = 15f;
        public float raidBuildingGuardedWeakerBonus = 10f;
        // Сборка с нуля (AiTurnController.TryRaidAssembleCandidates) — own dedicated numbers,
        // same shape as Разведка's reconRequestArmyPenalty/reconAssembleBonus but not shared with
        // them (each task's own copy, established pattern by now).
        public float raidRequestArmyPenalty = -100f;
        public float raidAssembleBonus = 20f;
        // AiTurnController.TryRaidReturnHomeCandidates' own fallback — same idea as Экономика's
        // own economyReturnHomeScore (a taskless field army with nothing left in its own category
        // anywhere on the map walks home instead of sitting wherever it last stopped), own
        // dedicated number since the two situations aren't equally urgent. Gated on
        // RaidWeakerArmyTask.HasAnythingToRaid returning false — a raid army that simply wasn't
        // this step's pick for a target that DOES still exist elsewhere is left alone rather than
        // sent home prematurely (the project owner's own report, 2026-08-16: a raid army that won
        // its fight and had nothing left to chase just sat there forever).
        public float raidReturnHomeScore = 40f;
        // TryRaidRegroupCandidates' own dispatch step (AiDecision.DispatchReinforcement) — a
        // critically wounded field army chose to wait rather than march home itself (cheaper per
        // its own AP/distance comparison), so a single non-hero courier peels off from the
        // garrison. Set comparably to raidAssembleBonus's own tier (aggressionBaseWeight +
        // raidAssembleBonus) so it competes evenly with normal raid-force assembly rather than
        // always losing or always winning against it.
        public float raidReinforceDispatchScore = 240f;

        [Header("Агрессия — Оборона цитадели (temporary, see AiTask.DefendingCitadel)")]
        // Added on top of aggressionBaseWeight for assembling/recruiting into a DefendingCitadel
        // task — own dedicated number, parallel to raidAssembleBonus, kept separate since the two
        // situations (routine raid buildup vs. a real threat sitting next to the player's own
        // base) aren't equally urgent and may need to diverge later.
        public float citadelDefenseBonus = 40f;
        // TryCitadelDefensePreemptCandidates' own score — pulling an army off whatever ACTIVE task
        // it's currently doing to instead march home and defend the citadel. Set well above
        // routine VisitHex/BuildFacility travel scores so a genuine "idle reinforcement won't be
        // enough" emergency reliably wins, comparable in weight-class to raidReinforceDispatchScore.
        public float citadelDefensePreemptScore = 260f;

        [Header("Разведка — реакция на угрозу (Задача 1)")]
        // A known enemy army within this many hexes of a scout's own current hex reroutes it
        // toward the garrison for one turn instead of whatever Задача 1 would otherwise propose.
        // Neutral armies never trigger this — see VisitHexTask.TryFlee.
        public int scoutFleeRadius = 2;
        public float scoutFleeBonus = 50f;

        [Header("Экономика — Задача 1 (Постройка facility)")]
        // A BuildFacility task already standing at its target, able to afford building right now —
        // flat, since the hero already fully committed to this specific hex.
        public float buildFacilityReadyBonus = 100f;

        [Header("Менеджмент — Починка юнита")]
        // Owned by AiManagementPlanner, not AiEconomyPlanner — see AiTask.cs's own AiTaskKind.
        // RepairUnit comment for why. Deliberately its own tier, not economyBaseWeight — cheap, so
        // it should usually beat a typical unpressured PlayCard score (managementBaseWeight + a
        // small role bonus, roughly 65-90 — see AiManagementPlanner.TryPlayCardCandidates), but
        // well below economyBaseWeight itself and below a heavily hand-backlogged PlayCard score,
        // so real pressure to play a card still wins on its own without any bespoke ordering rule
        // (see AiManagementPlanner.WouldBlockAffordableCard for the one explicit exception —
        // repair still yields for a turn if paying for it would specifically make a pricier,
        // otherwise-affordable Unit/Hero card unaffordable).
        public float repairUnitBaseWeight = 90f;
        // Never start, and never continue, a BuildFacility task while a known NEUTRAL army sits
        // within this many hexes of the target — a neutral guarding the area isn't a threat to
        // react to, it's simply a bad spot to commit a facility to (see BuildFacilityTask.
        // HasNeutralThreat). Cancels the task outright (same as picking a different hex never
        // having been offered in the first place) so a better, unguarded hex gets picked instead —
        // same hard-cancel treatment a known ENEMY within economySafetyRadius now also gets (see
        // BuildFacilityTask.HasEnemyThreat); Задача 1 no longer has a temporary one-turn retreat
        // like Разведка's own tasks do, the project owner's own call that a hero mid-build has
        // nothing better to fall back to anyway.
        public int neutralBuildAvoidRadius = 2;
        // Blunt safeguard against a permanently-stuck task (hex claimed by someone else, facility
        // slot full).
        public int maxBuildAttempts = 3;
        // Added to a candidate hex's own score, one resource type's own current stockpile at a
        // time (BuildFacilityTask.ScoreHex) — a scarcer type (lower root.GetResource) scores
        // higher, same "строим то, чего меньше всего в закромах" heuristic Разведка · Задача 2's
        // own WantedResourceType already uses, just applied directly to Задача 1's own hex
        // candidates instead of gating what Разведка goes looking for. Strong enough that a hero
        // ALREADY mid-build for a less scarce type can be preempted by a newly-known hex of a
        // scarcer type (see TryStartEconomyCandidates's own scarcity-switch comment) — reservations
        // are just an accounting claim (see AiResourceReservation's own class comment), never an
        // actual spend, so releasing one to redirect a hero costs nothing but travel time.
        public float buildScarcityWeight = 1f;
        // Flat score for a resourceType with NO current income source at all (see
        // BuildFacilityTask.HasIncomeSource) — replaces buildScarcityWeight's own stockpile term
        // entirely rather than adding to it, so a type sitting on a large one-off event stockpile
        // but genuinely un-mined still always outranks any type that already has SOME income,
        // regardless of that stockpile's size (project owner's own call, 2026-08-17: deficit must
        // be judged by income first, current stock only second).
        public float buildNoIncomeBonus = 100f;
        // A hero-led army with no active task, nothing left anywhere to build (BuildFacilityTask.
        // HasAnythingToBuild), and not already at the garrison — walks home instead of sitting
        // wherever it last stopped, same idea as Разведка's own TryReturnHomeCandidates but its
        // own separate number since the two situations aren't equally urgent.
        public float economyReturnHomeScore = 40f;
        // A hero sitting solo inside the Garrison stockpile is invisible to AiEconomyPlanner.
        // FindNearestHero's own AiArmyRoles.IsHeroLed scan (that always excludes IsGarrison) —
        // this is the prep step that pulls it out into its own fresh army first (see
        // AiEconomyPlanner.FindNearestHeroAnywhere/TryStartEconomyCandidates), mirroring
        // ResourceScrapDetachScore's own "prep step for the OTHER Economy task" shape. Above
        // ordinary Менеджмент housekeeping, below the actual walk/build steps that follow it next
        // turn (the project owner's own "герой застрял в гарнизоне, невидим для стройки" report).
        public float economyHeroDetachScore = 90f;

        [Header("Экономика — Задача 2 (ResourcesScrap)")]
        // Added on top of economyBaseWeight — scrapping via a unit's own CollectX ability costs no
        // AP/resources, so it should generally win the arbiter over a Задача 1 candidate.
        public float resourceScrapBaseWeightBonus = 20f;
        // Added on top of managementReorgScore — comfortably above ordinary garrison upkeep, but
        // below the actual walk/build steps of either Economy task.
        public float resourceScrapDetachScoreBonus = 10f;
        // Never start, and never continue, a ResourcesScrap task while a known enemy army sits
        // within this many hexes of the target. Shared with Задача 1's own enemy-threat check
        // (BuildFacilityTask.HasEnemyThreat) — one "how close is too close for Economy" number for
        // both tasks, each still free to get its own value later if that turns out wrong.
        public int economySafetyRadius = 2;

        [Header("Менеджмент")]
        // "не надо их плодить каждый ход, одной-двух армий про запас должно хватить".
        public int maxSpareArmies = 2;
        // Garrison stops accepting fresh PlayCard deposits (Unit or Hero card alike) once only
        // THIS many slots remain open — "если в гарнизоне уже заканчивается место (остаётся один
        // слот), юниты перераспределяются в резервную армию" (the project owner's own spec).
        // FindGarrisonOverflow's own eviction trigger already aims for this exact same
        // "Capacity - 1" equilibrium from the other direction (evicts down to one free slot), so
        // garrison settles there either way.
        public int garrisonReservedSlots = 1;
        // "лучше держать несколько героев в гарнизоне, чем плодить слабые армии" — the project
        // owner's own spec: N hero-led armies splitting whatever combat strength exists can never
        // ALL average more than 1/N of the whole (an even split is the best case), so this
        // directly caps how many can exist at once — see MaxActiveHeroArmies below for the actual
        // derived count, and GarrisonReorgTask.CanSupportAnotherHeroArmy for where it's enforced.
        // 0.35 → at most 2 (a three-way split would floor around 33%, under the 35% floor).
        public float minArmyStrengthShare = 0.35f;
        // Derived from minArmyStrengthShare above — see its own comment for the math. At least 1,
        // so a single hero can always lead something even if the share were configured absurdly
        // high.
        public int MaxActiveHeroArmies => System.Math.Max(1, (int)(1f / minArmyStrengthShare));
        // AiArmyRoles.IsSoloHeroAwaitingEscort's own fallback move — protecting this fragile,
        // escort-less hero outranks every OTHER Менеджмент action.
        public float managementReturnHomeScore = 100f;
        // Экономика · Задача 2's own detach-prerequisite base (see ResourceScrapDetachScore) —
        // no longer read by garrison-overflow/consolidation, see managementGarrisonBalanceScore
        // for that. Kept above PlayCard on purpose: an Economy collector detach is a real
        // in-progress task, not idle housekeeping.
        public float managementReorgScore = 80f;
        // A Recce card grows the scout pipeline, so it's worth a small nudge over an otherwise-
        // equal Unit/Hero card.
        public float playRecceCardBonus = 20f;
        // Added per additional unplayed plain Unit card sitting in hand beyond the first (see
        // TryPlayCardCandidates's own comment) — a growing backlog itself raises the urgency of
        // playing ANY of them, rather than staying pinned at a flat managementBaseWeight forever
        // and routinely losing the tie-break to RequestRaidArmy/SpawnReconArmy's own flat 50 (the
        // project owner's own "AI won't deploy units" report — cards just piled up in hand turn
        // after turn). 10 → a hand of 5 unplayed Unit cards already outscores those 50-flat
        // infra-creation candidates (managementBaseWeight 50 + 10×4 = 90) without needing to touch
        // Economy/Recon's own much higher range.
        public float unitCardBacklogWeight = 10f;
        // A non-Recce Hero card's own equivalent of playRecceCardBonus — before this a Hero sat at
        // a permanently flat managementBaseWeight with no nudge at all, so it lost the tie-break to
        // literally any Unit card the moment the Unit backlog reached 2 (the project owner's own
        // "ИИ не использует героев из руки" report — logged hand snapshots showed a hero sitting
        // unplayed for 5+ turns straight while Unit cards kept getting drawn AND played ahead of
        // it). A flat nudge so a single hero in hand still outranks a similarly-fresh Unit card.
        // Deliberately smaller than the original fix's 20 (see the project owner's own 2026-08-17
        // follow-up report: at the old 20 + the old Hero-only heroCardBacklogWeight(15), a hand
        // with just 3 heroes already outscored a Recce card's own flat playRecceCardBonus even with
        // reconHandDemandBacklogDamping applied) — this is the shaved-down replacement, kept small
        // rather than removed outright so the original starvation fix doesn't regress.
        public float playHeroCardBonus = 15f;
        // Hero's own backlog growth now reuses unitCardBacklogWeight directly instead of its own
        // separate (and steeper) constant — see playHeroCardBonus's own comment for why a Hero
        // growing its backlog FASTER than a Unit was part of what let a handful of heroes outscore
        // Разведка's own Recce-card demand. Counted separately from unplayedUnitCards still (own
        // pile, own urgency) — only the per-card weight is now shared, not the count.
        //
        // Multiplies this shared weight down (not to zero — a stalled Разведка assembly still
        // shouldn't stall Менеджмент's own backlog pressure completely) whenever Разведка already
        // has an active SpawnReconArmy/AssembleRecceScout candidate this same step — i.e. it's
        // already mid-pursuit of a matching Recce card from this exact hand. Without this, a hand
        // that piles up several plain Unit/Hero cards drowns out a flat Recce card in the backlog
        // race even though playing the Recce card ALSO relieves the hand — see the project owner's
        // own 2026-08-17 report (Scrapper sitting unplayed behind AT Infantry purely because
        // unitCardBacklogWeight had already stacked past playRecceCardBonus). Damping alone still
        // isn't airtight against a large enough Hero/Unit pile, which is what
        // TryPlayCardCandidates's own reconHandDemandActive Recce score bump (matching Разведка's
        // OWN valuation of the identical situation — reconBaseWeight + reconRequestCardPenalty,
        // see AiScoutPlanner.TryStartReconAssemblyCandidatesFor's own PlayCard branch) is for.
        public float reconHandDemandBacklogDamping = 0.5f;
        // Hero/Unit PlayCard alternation (see AiManagementPlanner's own "Разыгрывание карты —
        // чередование ролей" section, IsCardRoleCoolingDown/NotifyCardRolePlayed) — multiplies a
        // role's own bonus+backlog terms down for the step right after THAT role's own card just
        // got played, until the OTHER role gets one played instead. The project owner's own
        // 2026-08-17 follow-up: with a hand holding several of both, the AI kept exhausting every
        // Hero card back-to-back before touching a single Unit (or vice versa) purely because
        // whichever role still had the taller backlog pile always kept winning — this makes
        // playing ONE card of a role cool that same role off for a turn, so a hand with 3 heroes
        // and 3 units alternates between them turn to turn instead. Partial, not full suppression
        // — same reasoning as reconHandDemandBacklogDamping: if the OTHER role has nothing left in
        // hand at all, this role still has to keep winning, just at a discount.
        public float cardRoleAlternationDamping = 0.5f;
        // Garrison-overflow rebalance / lone-army consolidation — deliberately BELOW PlayCard's
        // own managementBaseWeight (a hero/unit card in hand must always get played before the AI
        // starts reshuffling its own base stock) and ABOVE both Reserve/Draw fallback tiers below
        // (idle housekeeping still beats doing literally nothing with leftover AP). Used to share
        // managementReorgScore (80, well above PlayCard) — that let the split→consolidate cycle
        // (garrison overflows → new army spun up → that new lone army folds straight back into
        // the now-open garrison slot → overflows again) eat the whole turn's step budget before a
        // hero card ever got a chance to be proposed as the winner (see the project owner's own
        // "ИИ перестал создавать героев" / "21 пустая армия" report).
        public float managementGarrisonBalanceScore = 20f;
        // Leftover-AP fallbacks (Reserve army / draw a card) — whichever AiManagementPlanner.
        // IsPreferred says is due next gets High, the other gets Low, so the two alternate turn by
        // turn.
        public float managementFallbackHighScore = 15f;
        public float managementFallbackLowScore = 5f;
        // An arrived BuildFacility task that still can't build (short on AP/still saving up) — a
        // deliberately tiny score so real work always wins, but this still beats a silent Pass.
        public float economyWaitScore = 1f;

        [Header("Army Roles (AiArmyRoles)")]
        // AiArmyRoles.IsMakeshiftScoutCapable's own lower bound — filled to at least Hero+2 (or as
        // full as a lower-CommandRating hero's own Capacity allows).
        public int makeshiftScoutMinMembers = 3; // hero + 2

        // Экономика · Задача 2's own base weight — see resourceScrapBaseWeightBonus's own comment.
        public float ResourceScrapBaseWeight => economyBaseWeight + resourceScrapBaseWeightBonus;

        // Экономика · Задача 2's own detach-prerequisite score — see
        // resourceScrapDetachScoreBonus's own comment.
        public float ResourceScrapDetachScore => managementReorgScore + resourceScrapDetachScoreBonus;
    }
}
