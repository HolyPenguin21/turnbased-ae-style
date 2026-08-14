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
        // attackOpportunityBonus) CAN cross.
        public float economyBaseWeight = 200f;
        public float reconBaseWeight = 150f;
        public float managementBaseWeight = 50f;

        [Header("Разведка — Задача 1 (Посещение хекса)")]
        public int maxConcurrentVisitHex = 3;
        // How far past the map's own nearest still-unvisited hex (measured from the citadel) a
        // Задача 1 candidate is still allowed to be, so visiting sweeps outward from the citadel
        // "as a wave" rather than beelining for whatever's farthest.
        public int visitRingBand = 3;
        public float scoutProximityWeight = 5f;
        public float freshNeighborWeight = 4f;
        public float citadelDistancePenalty = 3f;
        // Hero+2-3-unit compositions (AiArmyRoles.IsMakeshiftScoutCapable) only — added on top of
        // the normal proximity/fresh-neighbor score when a known-weaker enemy/neutral army sits on
        // the candidate hex, so a cheap win is preferred over walking past it.
        public float attackOpportunityBonus = 30f;
        // AiArmyRoles.IsSoloHeroAwaitingEscort's own hard leash — a couple of hexes around the
        // citadel, fixed, regardless of where the visit wavefront itself has gotten to.
        public int soloHeroHomeRadius = 2;

        [Header("Разведка — Задача 2 (Поиск хекса с ресурсом)")]
        public int maxConcurrentScoutResourceHex = 2;
        // A fraction of the map's own larger side (not a flat hex count) — a proxy for "probably
        // not deep in someone else's territory", checked against the actor's OWN citadel only.
        public float resourceScoutMaxDistanceFraction = 0.5f;

        [Header("Разведка — общая реакция на угрозу (обе задачи)")]
        // A known enemy army within this many hexes of a scout's own current hex reroutes it
        // toward the garrison for one turn instead of whatever Задача 1/2 would otherwise propose.
        // Neutral armies never trigger this — see AiTurnController.TryFleeTarget.
        public int scoutFleeRadius = 2;
        public float scoutFleeBonus = 50f;

        [Header("Экономика — Задача 1 (Постройка facility)")]
        // A BuildFacility task already standing at its target, able to afford building right now —
        // flat, since the hero already fully committed to this specific hex.
        public float buildFacilityReadyBonus = 100f;
        // Never start, and never continue, a BuildFacility task while a known NEUTRAL army sits
        // within this many hexes of the target — a neutral guarding the area isn't a threat to
        // flee from (see AiTurnController.TryFleeTarget's own neutral exemption), it's simply a
        // bad spot to commit a facility to. Unlike TryFleeTarget's one-turn detour, this cancels
        // the task outright (same as picking a different hex never having been offered in the
        // first place) so a better, unguarded hex gets picked instead.
        public int neutralBuildAvoidRadius = 2;
        // Blunt safeguard against a permanently-stuck task (hex claimed by someone else, facility
        // slot full).
        public int maxBuildAttempts = 3;

        [Header("Экономика — Задача 2 (ResourcesScrap)")]
        // Added on top of economyBaseWeight — scrapping via a unit's own CollectX ability costs no
        // AP/resources, so it should generally win the arbiter over a Задача 1 candidate.
        public float resourceScrapBaseWeightBonus = 20f;
        // Added on top of managementReorgScore — comfortably above ordinary garrison upkeep, but
        // below the actual walk/build steps of either Economy task.
        public float resourceScrapDetachScoreBonus = 10f;
        // Never start, and never continue, a ResourcesScrap task while a known enemy/neutral army
        // sits within this many hexes of the target.
        public int economySafetyRadius = 2;

        [Header("Менеджмент")]
        // "не надо их плодить каждый ход, одной-двух армий про запас должно хватить".
        public int maxSpareArmies = 2;
        // AiArmyRoles.IsSoloHeroAwaitingEscort's own fallback move — protecting this fragile,
        // escort-less hero outranks every OTHER Менеджмент action.
        public float managementReturnHomeScore = 100f;
        // Garrison-overflow split / lone-army consolidation — above PlayCard's own max so clearing
        // the garrison first never costs a PlayCard opportunity that would otherwise have fired.
        public float managementReorgScore = 80f;
        // A Recce card grows the scout pipeline, so it's worth a small nudge over an otherwise-
        // equal Unit/Hero card.
        public float playRecceCardBonus = 20f;
        // Leftover-AP fallbacks (Reserve army / draw a card) — whichever AiManagementPlanner.
        // IsPreferred says is due next gets High, the other gets Low, so the two alternate turn by
        // turn.
        public float managementFallbackHighScore = 15f;
        public float managementFallbackLowScore = 5f;
        // An arrived BuildFacility task that still can't build (short on AP/still saving up) — a
        // deliberately tiny score so real work always wins, but this still beats a silent Pass.
        public float economyWaitScore = 1f;

        [Header("Goal Scoring (AiGoalScorer)")]
        // How far (in hex steps) from the actor's own territory a threat/opportunity still counts.
        // Deliberately coarse — a real reachability check belongs to task execution, not this
        // first-pass scoring gate.
        public int goalScanRadius = 4;

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
