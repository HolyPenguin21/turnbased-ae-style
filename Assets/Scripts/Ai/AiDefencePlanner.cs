using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.Cards;
using Game.Combat;
using Game.Core;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai
{
    // Level-1 category orchestration for Оборона (AiTaskCategory.Defence) — full redesign
    // 2026-08-21 (project owner's own spec), replacing the single reactive "attack a known threat"
    // shape from the previous pass (2026-08-20, split out of Агрессия), with per-posture
    // triggers/composition retuned again 2026-08-22 (project owner's own explicit follow-up spec —
    // this comment describes the CURRENT shape, not the 2026-08-21 one). ONE persistent
    // AiTaskKind.DefendCitadel task/army now cycles through three Posture values every time it's
    // re-evaluated (see AiTask.AiDefencePosture and BuildPostureDecision below), same
    // "непрерывная переоценка" principle every other task in this codebase already follows:
    //
    //   Patrol — TRIGGER: CheatEstimateRaiderThreat finds a real scout/raider-shaped enemy army
    //   within AiConfig.defenceReactionRadius(5) of THIS task's own home hex specifically (see
    //   PatrolThreatPresent) — nothing at all found means Оборона does not start or grow a task on
    //   this home this step, full stop (2026-08-22 — before this, Patrol started unconditionally,
    //   the project owner's own "оркестратор не должен был получить задачи от защиты" report).
    //   Per-home since 2026-08-23 (project owner's own report) — used to pool every base's own
    //   territory into one shared cheat-scan, so a scout near a second base could make the
    //   citadel's own task start/grow a patrol against a threat nowhere near the citadel at all
    //   (same fix, same reasoning, as DynamicPatrolUrgencyScore's own `guardedHexes` below and
    //   FindActiveThreatSighting's own filter-before-rank fix — see those methods' own comments).
    //   COMPOSITION: fixed, not threat-sized any more — AiConfig.defencePatrolMinUnits(2) non-hero
    //   members, hero optional (see PatrolCompositionReady). Once ready, visits this player's own
    //   extraction-facility hexes within AiConfig.patrolRadius, looking for enemy scouts/weak
    //   raiders it can beat on its own. Ends (task removed, army sent home) once a full cycle is
    //   covered with nothing left nearby.
    //
    //   Active — TRIGGER: a known non-neutral army within AiConfig.defenceReactionRadius of ANY of
    //   this player's own Base-tagged hexes (AiMapMemory — "видел или видит", per the project
    //   owner's own spec), full stop — no beatable/reachable-in-turns gate on the trigger itself any
    //   more (2026-08-22, replaces the old defenceReachTurns reachability check, which is gone).
    //   ALSO must stay within AiConfig.defenceChaseAbandonRadius(6) of the task's own HomeHex (the
    //   citadel, or a later-founded base for its own task — NEVER the pursuing army's own current
    //   hex), re-checked fresh every step (2026-08-22, own follow-up spec — "не надо преследовать
    //   если армия врага ушла дальше шести хексов от цитадели") — the base-radius half above only
    //   gates STARTING a chase; this half is what makes an already-committed one give up once the
    //   target's own last-known position drifts too far from home, instead of adaptively closing an
    //   arbitrarily large distance forever. Still memory-based, same as every sighting read in this
    //   file — an army chasing a since-vanished sighting just finishes its current step and finds
    //   nobody there, no special detection needed — "видим или встречаемся = сражение" (a real
    //   engagement only ever comes from actually seeing or reaching the enemy, never from memory alone
    //   re-targeting the chase), same shape RaidWeakerArmyTask's own "attacks by memory" already
    //   follows (see FindActiveThreatSighting). 2026-08-23 fix (project owner's own report):
    //   FindActiveThreatSighting used to rank EVERY sighting near ANY base by strength first and
    //   only THEN check the homeHex/chase-abandon-radius filter — with two bases far enough apart,
    //   a strong sighting near a sibling base could win that global ranking and then fail the
    //   filter, returning null outright even though a weaker sighting genuinely near THIS home
    //   existed and should have triggered Active on its own. The homeHex filter now applies BEFORE
    //   ranking by strength, so a real in-range threat is never masked by a stronger irrelevant one.
    //   COMPOSITION: dynamic against THIS SPECIFIC sighted army — WorthIt.MeetsWinChance must clear
    //   AiConfig.defenceActiveWinChance(0.6, i.e. 60/40 — equal armies read as exactly 0.5) before
    //   the task actually moves to intercept; short of that it keeps assembling AT HOME (never sets
    //   out under-strength — TryStartDefenceCandidatesFor's own recruit/merge/strengthen tiers all
    //   gate on the army still sitting at homeHex), sized against the sighting instead of Patrol's
    //   fixed target. A Patrol-postured army that clears this converts to Active IN PLACE (same
    //   task, same army).
    //
    //   Turtle — IsUnderSiege (a live, per-player predicate, not a fourth task) says a known threat
    //   sits within AiConfig.siegeRadius(4) of the citadel and beats whatever this task currently
    //   fields (RaidWeakerArmyTask.IsReady — see this class's own "все сравнения только через
    //   WorthIt" note below). Overrides Patrol/Active outright: march home (avoiding a buffer zone
    //   around the threat, see AiTurnController.FindPathStepAvoidingZone — same primitive
    //   AiAggressionPlanner's own forced raid-recall reuses), mass at the garrison, keep recruiting
    //   via the same "start" tier below. Recomputed fresh every call — lifts the instant the threat
    //   weakens/leaves, which naturally falls back through Active (a sortie, "может резко выйти") to
    //   Patrol, no separate "exit turtle" logic needed. Mechanics unchanged from the 2026-08-21 pass
    //   (project owner's own confirmation, 2026-08-22 — "специфические правила уже были
    //   реализованы"), only the underlying strength comparison moved to WorthIt (see below).
    //
    // IsUnderSiege is also read directly by AiAggressionPlanner — a real siege force-recalls every
    // active raid task (Retreating = true) and suppresses starting new ones, the project owner's
    // own explicit scope (Economy/Recon are NOT touched here, they already flee on their own terms).
    //
    // "Все сравнения армий на карте должны происходить только через worth it" (project owner's own
    // explicit call, 2026-08-22): every army-vs-threat strength comparison in this file now bottoms
    // out in WorthIt.WinChance/MeetsWinChance — either directly (Active's own composition gate,
    // above) or indirectly, since RaidWeakerArmyTask.IsReady (Turtle/local-encounter/wounded-retreat/
    // preempt, all reused unchanged below) now delegates its own two-sided edge check to
    // WorthIt.WinChance internally instead of keeping a second copy of the same math (verified
    // algebraically equivalent to the old inline formula — see RaidWeakerArmyTask.IsReady's own
    // comment). Patrol's own fixed member-count target (PatrolCompositionReady) is the one
    // deliberate exception — it's a composition-size rule, not an army-vs-army strength comparison,
    // so WorthIt has nothing to say about it.
    //
    // Recruit-management plumbing (FindRecruitAt/FindCoLocatedMergeRecruit) is still reused from
    // RaidWeakerArmyTask exactly as the 2026-08-21 pass established — none of it depends on how
    // "ready" gets decided. The one deliberate
    // exception is CheatEstimateRaiderThreat below (project owner's own explicit "можем
    // почитерить" call) — isolated in its own method so it's easy to find/remove later if that
    // changes. Its role narrowed 2026-08-22: it used to also size Patrol's own composition target;
    // now it's purely Patrol's TRIGGER (see PatrolThreatPresent), composition being fixed instead.
    public static class AiDefencePlanner
    {
        private static readonly ResourceType[] AllResourceTypes =
        {
            ResourceType.Human, ResourceType.Energy, ResourceType.Materials, ResourceType.Tech,
        };

        // ---- Live per-player predicates (no stored state — see this class's own comment) ----

        // Turtle's own trigger — a known non-neutral army within AiConfig.siegeRadius of the
        // citadel, stronger (WorthIt) than the current DefendCitadel task's own army, or the bare
        // garrison if no task exists yet (a siege can start before Оборона has ever fielded
        // anything). Read by AiDefencePlanner itself (see BuildPostureDecision) AND by
        // AiAggressionPlanner (force-recall active raids, suppress new ones) — never cached, cheap
        // enough to recompute every call, same as RaidWeakerArmyTask.NearbyThreat.
        public static bool IsUnderSiege(PlayerSetupData player, AiTurnContext ctx)
        {
            AiMapMemory.KnownEnemySighting? threat = SiegeThreat(player);
            if (!threat.HasValue)
                return false;

            // Filtered to the CITADEL's own task (HomeHex match) — with a second base able to field
            // its own separate DefendCitadel task now, an unfiltered FirstOrDefault could just as
            // easily pick the base's task/army here, sizing "is the CITADEL under siege" against the
            // wrong defender entirely. Same fix as TryDefencePreemptCandidates' own lookup below.
            HexCoord citadelHex = AiTurnController.GarrisonHexFor(player);
            AiTask defenceTask = AiTaskRegistry.TasksFor(player)
                .FirstOrDefault(t => t.Kind == AiTaskKind.DefendCitadel && t.HomeHex.Equals(citadelHex));
            ArmyData reference = defenceTask?.Army ?? AiTurnController.GarrisonArmyFor(player);
            float hexBonus = WorthIt.HexDefenseBonus(threat.Value.Hex, ctx.Map);
            return !RaidWeakerArmyTask.IsReady(reference, threat.Value.DefenseSum + hexBonus, threat.Value.AttackSum,
                threat.Value.Defenders, hexBonus);
        }

        // Turtle's own threat sighting itself, not just IsUnderSiege's own bool read —
        // AiAggressionPlanner's forced raid-recall needs the actual hex to build its own avoid-zone
        // around (see AiTurnController.FindPathStepAvoidingZone). Same scan IsUnderSiege/
        // BuildPostureDecision's own Turtle branch already run; kept as one shared private lookup so
        // all three can never disagree on which sighting counts as "the" siege threat.
        private static AiMapMemory.KnownEnemySighting? SiegeThreat(PlayerSetupData player) =>
            StrongestSightingNear(player, new[] { AiTurnController.GarrisonHexFor(player) }, AiConfig.siegeRadius);

        public static HexCoord? SiegeThreatHex(PlayerSetupData player) => SiegeThreat(player)?.Hex;

        // The strongest known non-neutral sighting within `radius` of ANY of `hexes` — same
        // "strongest, not first" rule as RaidWeakerArmyTask.NearbyThreat's own 2026-08-20 fix, just
        // generalized to several own hexes at once (RaidWeakerArmyTask.NearbyThreat itself stays a
        // single-hex convenience wrapper other callers still use).
        private static AiMapMemory.KnownEnemySighting? StrongestSightingNear(PlayerSetupData player, IReadOnlyList<HexCoord> hexes, int radius)
        {
            AiMapMemory.KnownEnemySighting? strongest = null;
            foreach (AiMapMemory.KnownEnemySighting sighting in AiMapMemory.KnownEnemySightingsNear(player, hexes, radius))
            {
                if (sighting.Owner == null || sighting.Owner.IsNeutral)
                    continue;
                if (!strongest.HasValue || sighting.DefenseSum > strongest.Value.DefenseSum)
                    strongest = sighting;
            }
            return strongest;
        }

        // Active's own TRIGGER (2026-08-22, project owner's own spec — "видел или видит армию", in
        // radius) — strongest known sighting within AiConfig.defenceReactionRadius of ANY of this
        // player's own Base-tagged hexes. No beatable/reachable filter any more (that used to make
        // this the SAME check as "is Active ready to move"; those are now two separate questions —
        // see this class's own header comment).
        //
        // ALSO within AiConfig.defenceChaseAbandonRadius of `homeHex` (2026-08-22, own follow-up
        // spec — "не надо преследовать если армия врага ушла дальше шести хексов от цитадели") —
        // the base-radius half above only ever gates whether a chase is worth STARTING; this half is
        // what makes an already-committed chase actually stop once the target's own last-known
        // position drifts too far from THIS task's own home, instead of adaptively closing an
        // arbitrarily large gap forever. Measured from `homeHex`, deliberately NOT from the pursuing
        // army's own current hex — an army well out on the way to a still-in-range sighting isn't
        // penalized just for being far from home itself right now, only the TARGET's own distance
        // matters. Still memory-based like every known-sighting read in this file (recomputed fresh
        // every call — a fresher/closer sighting can still supersede the current target the same way
        // it always could), so a target that's since moved away entirely (this whole check failing)
        // isn't distinguished from one that's simply gone quiet: either way the army finishes
        // whatever step is already under way and finds nobody there, same "видим или встречаемся =
        // сражение" shape RaidWeakerArmyTask's own "attacks by memory" already follows (see its own
        // class comment) — a real engagement only ever comes from actually seeing or reaching the
        // enemy, never from this trigger check itself. So a second base's own task never reacts to a
        // sighting that's technically near SOME base but nowhere near ITS OWN home specifically.
        //
        // Null covers "no base yet", "nothing sighted nearby", and "too far from home to chase"
        // alike; callers treat all three the same way (fall through to Patrol).
        //
        // 2026-08-23 fix (project owner's own report): used to call StrongestSightingNear across
        // ALL of `baseHexes` first — picking the single globally-strongest sighting near ANY base —
        // and only THEN check whether that one sighting was within defenceChaseAbandonRadius of
        // THIS home. With two bases far enough apart that no sighting can be near both at once, a
        // strong sighting near a DIFFERENT base would win that global "strongest" pick and then fail
        // the homeHex distance check, returning null outright — even when a weaker sighting genuinely
        // near THIS home (and so well within range) existed and would have triggered Active on its
        // own. The homeHex filter now applies BEFORE ranking by strength, so a real, in-range threat
        // for this specific home is never masked by a stronger but irrelevant one near a sibling base.
        private static AiMapMemory.KnownEnemySighting? FindActiveThreatSighting(PlayerSetupData player, HexCoord homeHex)
        {
            List<HexCoord> baseHexes = BuildingRegistry.AllBuildings()
                .Where(b => b.Owner == player && b.IsBase)
                .Select(b => b.Hex).ToList();
            if (baseHexes.Count == 0)
                return null;

            AiMapMemory.KnownEnemySighting? strongest = null;
            foreach (AiMapMemory.KnownEnemySighting sighting in AiMapMemory.KnownEnemySightingsNear(player, baseHexes, AiConfig.defenceReactionRadius))
            {
                if (sighting.Owner == null || sighting.Owner.IsNeutral)
                    continue;
                if (HexGridMath.Distance(homeHex, sighting.Hex) > AiConfig.defenceChaseAbandonRadius)
                    continue; // near a DIFFERENT base, too far from THIS home to be its own threat
                if (!strongest.HasValue || sighting.DefenseSum > strongest.Value.DefenseSum)
                    strongest = sighting;
            }
            return strongest;
        }

        // Read-only accessor for AiManagementPlanner's own card-selection Defence-need bonus
        // (2026-08-23, project owner's own "generalize RaidNeedBonus into TaskNeedBonus" spec) —
        // exposes the exact same sighting FindActiveThreatSighting/BuildPostureDecision already act
        // on for THIS home, so Менеджмент's own Unit-card pre-pass can never disagree with what
        // Defence itself is actually reacting to (null the same "nothing sighted, or Patrol's own
        // fixed headcount target instead" case every other caller of FindActiveThreatSighting reads).
        public static AiMapMemory.KnownEnemySighting? CurrentActiveThreat(PlayerSetupData player, HexCoord homeHex) =>
            FindActiveThreatSighting(player, homeHex);

        // AirStrikeTask's own urgency term (2026-08-26, air-strike scoring rework, project owner's
        // own spec section 5 — "срочность цели") — whether `targetHex` IS the live threat this
        // player's own Defence tier is already reacting to: the citadel's own siege threat
        // (SiegeThreatHex) or an active-defence threat sighting for one of this player's own
        // Base-tagged hexes (CurrentActiveThreat, same per-base scan FindActiveThreatSighting's own
        // comment already runs). Reuses those two read-only predicates directly — no second,
        // air-strike-only threat scan of its own, per the rework spec's own "не вводить отдельное
        // всеведение" constraint. `isCitadelThreat` lets the caller weigh the two cases differently
        // (a citadel threat outranks an ordinary base threat) without re-deriving which one fired.
        public static bool IsUrgentAirStrikeTarget(PlayerSetupData player, HexCoord targetHex, out bool isCitadelThreat)
        {
            isCitadelThreat = SiegeThreatHex(player)?.Equals(targetHex) ?? false;
            if (isCitadelThreat)
                return true;

            foreach (BuildingData building in BuildingRegistry.AllBuildings())
            {
                if (building.Owner != player || !building.IsBase)
                    continue;
                AiMapMemory.KnownEnemySighting? threat = CurrentActiveThreat(player, building.Hex);
                if (threat.HasValue && threat.Value.Hex.Equals(targetHex))
                    return true;
            }
            return false;
        }

        // Whether `army` is currently strong enough to actually intercept `sighting` — Active's own
        // COMPOSITION gate (AiConfig.defenceActiveWinChance, 60/40 dynamic against this specific
        // known army), routed through WorthIt.MeetsWinChance directly per this class's own "only
        // WorthIt" rule. Shared by BuildPostureDecision's own attack-now check and
        // TryStartDefenceCandidatesFor's own "still assembling toward this sighting" gate so the two
        // can never disagree on what "ready" means for the same sighting.
        private static bool MeetsActiveComposition(ArmyData army, AiMapMemory.KnownEnemySighting sighting, HexMap map)
        {
            float hexBonus = WorthIt.HexDefenseBonus(sighting.Hex, map);
            return WorthIt.MeetsWinChance(army, sighting.DefenseSum, sighting.AttackSum, sighting.Defenders,
                AiConfig.defenceActiveWinChance, hexBonus);
        }

        // Patrol's own TRIGGER (2026-08-22, project owner's own spec — "тригерится только если чит
        // проверка сработала") — CheatEstimateRaiderThreat found an actual scout/raider-shaped enemy
        // army within its own scan (defenceReactionRadius of `homeHex`). Presence only; the returned
        // Defense/Attack sums themselves no longer size anything (Patrol's own composition target is
        // now fixed — see PatrolCompositionReady). Per-home since 2026-08-23 (see
        // CheatEstimateRaiderThreat's own comment for why) — `homeHex` threads straight through.
        private static bool PatrolThreatPresent(PlayerSetupData player, HexCoord homeHex)
        {
            RaidWeakerArmyTask.ThreatStrength threat = CheatEstimateRaiderThreat(player, homeHex);
            return threat.Defense > 0f || threat.Attack > 0f;
        }

        // Patrol's own fixed COMPOSITION target (2026-08-22, project owner's own spec — "два юнита
        // или герой + два юнита") — a plain member-count rule, not an army-vs-army comparison, so it
        // deliberately does NOT go through WorthIt (nothing there to compare against). A hero is
        // welcome but never required to count as ready.
        private static bool PatrolCompositionReady(ArmyData army) =>
            army != null && army.Members.Count(m => !m.IsHero) >= AiConfig.defencePatrolMinUnits;

        // The project owner's own sanctioned cheat ("можем немного почитерить... собрать
        // усредненный патруль который по силе перекрывает рейд/разведчиков армии врага") — the ONE
        // place in all of Оборона that reads real enemy ArmyData directly instead of through
        // AiMapMemory's fog-of-war-honest sightings. Scans every other player's own small/scout-
        // shaped armies (member count within AiArmyRoles' own makeshiftScoutMinMembers ceiling —
        // roughly "a hero + 2" or smaller, never a real main force) currently within
        // AiConfig.defenceReactionRadius of `homeHex`, and returns the strongest one found, as a
        // ThreatStrength Patrol's own composition can be sized against.
        // How strong the DefendCitadel task's own composition should be right now IS this call,
        // directly — no separate blended "real sighting near a base" branch any more (2026-08-22,
        // project owner's own call): this cheat now reads live data scoped to the same radius the
        // honest AiMapMemory check used to cover, so it's already at least as informed and the
        // second check was pure redundancy. Also means Patrol never over-builds against a threat
        // that isn't actually anywhere near this player's own territory (see the project owner's
        // own spec — "будет знать что что-то есть рядом с базой, не будет знать где конкретно"):
        // nothing within range of a base collapses this to 0, so a fresh, unpressured patrol can
        // field almost any composition as "ready" instead of chasing the single strongest scout
        // the enemy fields anywhere on the whole map. Recomputed on every new patrol assembly
        // (never cached) — accounts for the enemy strengthening, or simply moving into or out of
        // range, over the course of the match.
        // Scoped to a single `homeHex` since 2026-08-23 (project owner's own report) — used to scan
        // ALL of this player's own base hexes at once regardless of which home's task was asking, so
        // a scout sitting near Base2 could make PatrolThreatPresent read true for the Citadel's own
        // task too, starting/growing a Citadel patrol against a threat that isn't actually anywhere
        // near the Citadel at all. Each home's task now only ever cheats-scans its own neighborhood,
        // matching this class's own "каждая база обходит свою территорию" principle that
        // FindPatrolTarget already follows for the honest (non-cheat) side of Patrol.
        private static RaidWeakerArmyTask.ThreatStrength CheatEstimateRaiderThreat(PlayerSetupData player, HexCoord homeHex)
        {
            float bestDefense = 0f;
            float bestAttack = 0f;
            List<WorthIt.DefenderProfile> bestDefenders = null;

            foreach (PlayerSetupData other in GameSession.Players ?? Enumerable.Empty<PlayerSetupData>())
            {
                if (other == null || other == player || other.IsNeutral)
                    continue;
                foreach (ArmyData army in ArmyRegistry.AllForOwner(other))
                {
                    // Deliberately NOT stealth-filtered (project owner's own call) — this is
                    // the sanctioned cheat, it reads live ArmyData including hidden units, so
                    // "something is near my base" registers even for a hidden scout. It only
                    // ever TRIGGERS a patrol (see PatrolThreatPresent); the patrol then has to
                    // actually run the ordinary detection challenge to find the scout, or not.
                    if (army.IsGarrison || army.IsPrison || army.Members.Count == 0
                        || army.Members.Count > AiConfig.makeshiftScoutMinMembers)
                        continue; // only scout/raid-shaped compositions, never the enemy's whole main force
                    if (HexGridMath.Distance(homeHex, army.Hex) > AiConfig.defenceReactionRadius)
                        continue; // not currently near THIS home specifically

                    float defense = WorthIt.DefenseSum(army);
                    float attack = WorthIt.AttackSum(army);
                    if (defense + attack <= bestDefense + bestAttack)
                        continue;

                    bestDefense = defense;
                    bestAttack = attack;
                    // Full snapshot, real CURRENT HP (2026-08-22) — this is already the project
                    // owner's own sanctioned cheat (see this method's own top comment: reads live
                    // ArmyData directly, no fog-of-war limitation to respect here), and (since
                    // 2026-08-26) the same value an honest AiMapMemory sighting would itself
                    // capture too — the real current HP is right there on `m`.
                    bestDefenders = army.Members.Where(m => !m.IsHero)
                        .Select(m => new WorthIt.DefenderProfile(m.Defense, m.HasAbility(UnitAbilities.CeramicArmor), m.TypeTags.ToList(),
                            m.Attack, m.HitPointsCurrent, m.Initiative))
                        .ToList();
                }
            }
            return new RaidWeakerArmyTask.ThreatStrength(bestDefense, bestAttack, bestDefenders, 0f);
        }

        // Patrol's own dynamic urgency (2026-08-21, project owner's own "option 2" call, chosen
        // over a flat turn-number ramp) — how urgently an ASSEMBLING/growing Defence force should
        // compete for AP/recruits against other categories right now, given whether there's
        // actually anything real worth defending against. Reads real enemy ArmyData directly, the
        // same sanctioned exception CheatEstimateRaiderThreat already takes (fog-of-war-honest
        // AiMapMemory can't tell us about a threat before OUR OWN army has ever seen it, which
        // defeats the point of reacting proactively). No stored state, same "recomputed fresh every
        // call" rule as IsUnderSiege/FindActiveThreatSighting — proximity alone is the signal, so the score
        // rises and falls on its own as an army approaches or wanders off, with no separate
        // "did it leave" bookkeeping needed. Ranges from AiConfig.defencePatrolScoreFloor (nothing
        // real anywhere near a base/facility hex) up to AiConfig.defencePatrolScore itself
        // (something real is right on top of one) — deliberately never reaches defenceActiveScore's
        // own tier, which stays reserved for an ACTUALLY-CONFIRMED engagement (BuildPostureDecision's
        // own Active/Turtle branches, all gated on a real AiMapMemory sighting, never this cheat).
        // Scoped to a single `homeHex` since 2026-08-23 (project owner's own report, same fix as
        // CheatEstimateRaiderThreat's own — see that method's own comment) — used to pool EVERY base
        // hex plus EVERY patrol-facility hex this player owns into one shared `guardedHexes` set
        // regardless of which home's task was asking, so an enemy sitting near Base2's own facilities
        // could hand the Citadel's own assembling task an urgency score driven entirely by a threat
        // nowhere near the Citadel. `guardedHexes` is now just `homeHex` itself plus this home's own
        // patrol facilities (patrolRadius of `homeHex`, the same scope FindPatrolTarget already
        // patrols for this exact home) — never a sibling base's own territory.
        private static float DynamicPatrolUrgencyScore(PlayerSetupData player, HexCoord homeHex)
        {
            List<HexCoord> guardedHexes = BuildingRegistry.AllBuildings()
                .Where(b => b.Owner == player && (b.Hex.Equals(homeHex)
                    || (IsOwnPatrolFacilityHex(player, b.Hex) && HexGridMath.Distance(homeHex, b.Hex) <= AiConfig.patrolRadius)))
                .Select(b => b.Hex).ToList();
            if (guardedHexes.Count == 0)
                return AiConfig.defencePatrolScoreFloor;

            float closest = 0f; // 0 = nothing in range found yet, 1 = something is right on top of a guarded hex
            foreach (PlayerSetupData other in GameSession.Players ?? Enumerable.Empty<PlayerSetupData>())
            {
                if (other == null || other == player || other.IsNeutral)
                    continue;
                foreach (ArmyData army in ArmyRegistry.AllForOwner(other))
                {
                    if (army.IsGarrison || army.IsPrison || army.Members.Count == 0)
                        continue;
                    // NOT stealth-filtered — same sanctioned-cheat reasoning as
                    // CheatEstimateRaiderThreat above: a hidden scout near a guarded hex still
                    // raises patrol urgency; whether the patrol then actually finds it is the
                    // ordinary detection challenge's job.

                    // Single radius for every real enemy army regardless of shape (2026-08-22,
                    // project owner's own follow-up call — removes the old scout-vs-real-army
                    // split): "если враг в радиусе 8 хексов, то нужен патруль и всё" — any known
                    // enemy army this close to a guarded hex is worth building patrol urgency
                    // against, a scout included.
                    int distance = guardedHexes.Min(h => HexGridMath.Distance(h, army.Hex));
                    if (distance > AiConfig.patrolDangerRadius)
                        continue;

                    closest = System.Math.Max(closest, 1f - (float)distance / AiConfig.patrolDangerRadius);
                }
            }

            return AiConfig.defencePatrolScoreFloor + closest * (AiConfig.defencePatrolScore - AiConfig.defencePatrolScoreFloor);
        }

        // ---- Posture decision — the single shared core (see this class's own comment) ----

        // What `task`'s own army should do RIGHT NOW, given its current inputs — used both by an
        // already-registered task's own continuation (TryContinueDefenceTask) and a freshly-built,
        // not-yet-registered task the moment it's created (TryStartDefenceCandidates decides its
        // very first move inline, same shape RaidWeakerArmyTask.FindTarget's own callers already
        // follow). Mutates task.Posture/task.TargetHex/task.PatrolVisited as it goes (same "AiTask
        // is a plain mutable class" convention AiTask.TargetArmy's own comment already documents)
        // but never touches AiTaskRegistry except for the one genuine end-of-life case (Patrol cycle
        // complete, home, nothing left) — every other "no decision this step" return leaves the task
        // exactly as registered, for TryStartDefenceCandidates' own recruit loop to keep working on.
        private static AiDecision BuildPostureDecision(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx, AiTask task)
        {
            HexCoord citadelHex = AiTurnController.GarrisonHexFor(player);
            HexCoord homeHex = task.HomeHex;

            // Feasibility gate (2026-08-23 fix, project owner's own report — Sable's patrol
            // proposed a 3-AP move with only 2 AP left): this whole method's every real branch
            // below either returns null (nothing to do this step) or a MoveArmy decision — never
            // anything else — so one gate here, ahead of every branch, covers the ActivationApCost
            // half of AiTurnController.CanIssueMoveNow for the whole function at once, instead of
            // duplicating it at each of the half-dozen return points below. The movement-point half
            // still needs checking per-branch, against whichever specific destination that branch
            // actually picks — see each branch's own CanIssueMoveNow call further down.
            if (task.Army.CurrentMovement <= 0 || (!task.Army.HasActivatedThisTurn && !root.CanSpendActionPoints(task.Army.ActivationApCost)))
                return null;

            // Invariant guard (2026-08-26, project owner's own spec item 1 — "наземная задача
            // никогда не назначается авиа-армии"): DefendCitadel is a ground task through and
            // through (Patrol/Active/Turtle all issue plain ground MoveArmy decisions) — every real
            // selection path that could hand it an army (FindReadyIdleDefender, the emergency-
            // preempt field-army recall, every recruit/merge/strengthen tier) now excludes
            // AviationRules.IsAirArmy/IsAirfield explicitly, so this should never actually trip. Kept
            // here anyway as the one shared choke point every DefendCitadel continuation passes
            // through (TryContinueDefenceTask AND the freshly-built-task inline call from
            // TryStartDefenceCandidatesFor both route through this same method) — a cheap safety net
            // that turns a future leak in any one of those selection sites into a loud log line
            // instead of a silently misbehaving aircraft stuck on ground patrol duty.
            if (AviationRules.IsAirArmy(task.Army) || AviationRules.IsAirfield(task.Army))
            {
                AiDebugLog.Write($"[AI] {player.Nickname}: INVARIANT VIOLATION — DefendCitadel task holds aviation "
                    + $"army \"{task.Army.Name}\", refusing to issue a ground decision for it.");
                return null;
            }

            // Turtle only ever applies to the citadel's own task (2026-08-21, project owner's own
            // call) — a later-founded base deliberately gets no siege-level escalation of its own,
            // only the citadel-anchored task ever reads IsUnderSiege at all. A base's own task falls
            // straight through to Active/Patrol below regardless of citadel siege state.
            if (homeHex.Equals(citadelHex) && IsUnderSiege(player, ctx))
            {
                // Turtle supersedes a local retreat already in progress (see below) — its own
                // march-home already achieves "get to safety", more robustly (wider buffer). Cleared
                // here so a stale local-retreat flag never silently resumes once the siege lifts.
                task.Retreating = false;
                task.Posture = AiDefencePosture.Turtle;

                // A real siege outranks Economy's own resource commitment too (2026-08-23, project
                // owner's own call) — releases whatever any still-unfinished BuildFacility task has
                // claimed (see AiResourceReservation's own class comment) so THIS SAME Decide() step's
                // later Management card-play (AiManagementPlanner.FindPlacement's own
                // AiResourceReservation.CanAfford, which reads the live ledger) can spend it on the
                // citadel's defence instead — Decide's own candidate order already runs Economy's
                // AdvanceEconomyTask (TopUp) before this method and Менеджмент's TryPlayCardCandidates
                // after it, so releasing here is what actually opens the window. Not a permanent loss
                // to Economy — AdvanceEconomyTask's own TopUp simply re-claims whatever's still free
                // the next time that task gets a step, same as if nothing here ever spent it.
                foreach (AiTask facilityTask in AiTaskRegistry.TasksFor(player))
                    if (facilityTask.Kind == AiTaskKind.BuildFacility)
                        AiResourceReservation.Release(facilityTask);

                if (task.Army.Hex.Equals(citadelHex))
                    return null; // home, recruiting — TryStartDefenceCandidates' own loop handles it

                HexCoord? nextStep = AiTurnController.FindPathStepAvoidingZone(ctx.Map, task.Army, citadelHex,
                    SiegeThreat(player)?.Hex, AiConfig.defenceRetreatAvoidRadius);
                return nextStep.HasValue
                    ? AiDecision.Move(task.Army, nextStep.Value, "citadel under siege — falls back to mass at the garrison",
                        task, AiConfig.defenceTurtleScore, AiTaskCategory.Defence)
                    : null; // boxed in avoiding the threat — wait rather than walk into it
            }

            // 1.2's own local retreat, already in progress (see the local-threat check further down
            // for where this first gets set) — one-way, same shape Агрессия's own outmatched-threat
            // reaction already uses (AiTask.Retreating), reused here for DefendCitadel too. Recomputes
            // the avoid-hex fresh every call (whatever's actually near the army's own hex RIGHT NOW,
            // not whatever originally triggered the flight) so a stale threat never blocks a route
            // it's no longer actually near.
            if (task.Retreating)
                return ContinueLocalRetreat(player, ctx, task, homeHex);

            // Active's own trigger comes BEFORE the "still assembling" gate below — 2026-08-21 fix
            // (simulation report finding, initially missed in the first fix pass), still true under
            // the 2026-08-22 redesign: a real, currently-sighted threat must never be blocked by
            // Patrol's own composition target still falling short of "ready". Trigger alone (sighted,
            // in radius — FindActiveThreatSighting) no longer implies "attack now"; that's
            // MeetsActiveComposition's own separate job (see this class's own header comment) — not
            // yet strong enough falls through to the assembling gate right below, now sized against
            // THIS sighting instead of Patrol's fixed target.
            AiMapMemory.KnownEnemySighting? active = FindActiveThreatSighting(player, homeHex);
            if (active.HasValue && MeetsActiveComposition(task.Army, active.Value, ctx.Map))
            {
                // First-step movement cost, not covered by this method's own top-of-function AP
                // gate (see that gate's own comment) — if the very next hex toward the threat costs
                // more than CurrentMovement, wait rather than fall through into the unrelated
                // Patrol logic below, which would otherwise silently demote this task off Active
                // for the step.
                if (!AiTurnController.FindAffordableStep(ctx.Map, task.Army, active.Value.Hex).HasValue)
                    return null;
                task.Posture = AiDefencePosture.Active;
                task.TargetHex = active.Value.Hex;
                return AiDecision.Move(task.Army, active.Value.Hex,
                    $"citadel defense — intercepts a known army at ({active.Value.Hex.Q},{active.Value.Hex.R})",
                    task, AiConfig.defenceActiveScore, AiTaskCategory.Defence);
            }

            // "Still assembling at the garrison" gate comes BEFORE the wounded/local-encounter
            // checks below — 2026-08-21 fix (own re-test finding, right after adding those two
            // checks): without this ordering, a task with only its first recruit or two so far,
            // sitting AT the garrison hex with a real threat within patrolLocalThreatRadius (i.e.
            // basically at the doorstep), would read as a "local encounter it can't beat" and set
            // Retreating — which ContinueLocalRetreat then resolves as "already home" and deletes
            // the task OUTRIGHT, destroying the very recruitment in progress instead of just letting
            // it keep growing. Only ever gates the AT-GARRISON case (its own condition already
            // requires `task.Army.Hex.Equals(homeHex)`), so it never blocks the wounded/local
            // checks once the army has actually left on patrol — and any threat this close to the
            // garrison that the army truly can't beat is already a siegeRadius(4)-superset case
            // IsUnderSiege itself would have caught first, so nothing real is lost here.
            //
            // Target sized against `active` when there IS a sighting in range (Active's own dynamic
            // 60/40 composition, same check the branch above just failed), Patrol's own fixed
            // member-count target otherwise (2026-08-22 — see this class's own header comment; no
            // longer CheatEstimateRaiderThreat-sized).
            if (task.Army.Hex.Equals(homeHex))
            {
                bool composedReady = active.HasValue
                    ? MeetsActiveComposition(task.Army, active.Value, ctx.Map)
                    : PatrolCompositionReady(task.Army);
                if (!composedReady)
                {
                    task.Posture = AiDefencePosture.Patrol;
                    return null; // still assembling — TryStartDefenceCandidates' own recruit loop handles it
                }
            }

            // Wounded from a won fight (or any other cause) stands down instead of seeking out a NEW
            // local engagement or resuming ordinary patrol duty — 2026-08-21, project owner's own
            // spec ("если ранены то отступаем... потенциально новая армия патруля(или починенная)").
            // Deliberately checked AFTER the base-anchored Active branch and the assembling gate
            // above (citadel defense, and simply not-yet-fielded-at-all, both take priority over
            // this army's own safety) but BEFORE the local encounter below (a wounded army shouldn't
            // go looking for a SECOND fight on its own initiative).
            if (RaidWeakerArmyTask.IsCriticallyWounded(task.Army))
            {
                task.Retreating = true;
                return ContinueLocalRetreat(player, ctx, task, homeHex);
            }

            // 1.2's own local encounter — a known non-neutral army within patrolLocalThreatRadius of
            // the patrol's OWN current hex (as opposed to FindActiveThreatSighting above, which only
            // ever looks near a Base hex) — 2026-08-21, project owner's own follow-up spec. Beatable →
            // attack it directly (same Active posture/score as the base-anchored case; once it's
            // gone, the very next re-evaluation naturally falls back to ordinary Patrol on its own,
            // no extra bookkeeping needed for "возвращаемся в патруль"). Not beatable → one-way
            // retreat to the garrison, same shape as the wounded branch above.
            AiMapMemory.KnownEnemySighting? nearby = StrongestSightingNear(player, new[] { task.Army.Hex }, AiConfig.patrolLocalThreatRadius);
            if (nearby.HasValue)
            {
                float hexBonus = WorthIt.HexDefenseBonus(nearby.Value.Hex, ctx.Map);
                if (RaidWeakerArmyTask.IsReady(task.Army, nearby.Value.DefenseSum + hexBonus, nearby.Value.AttackSum, nearby.Value.Defenders, hexBonus))
                {
                    // Same first-step-cost gap as the Active branch above — wait rather than fall
                    // through into Patrol below (or wrongly retreat from a fight we can actually win).
                    if (!AiTurnController.FindAffordableStep(ctx.Map, task.Army, nearby.Value.Hex).HasValue)
                        return null;
                    task.Posture = AiDefencePosture.Active;
                    task.TargetHex = nearby.Value.Hex;
                    return AiDecision.Move(task.Army, nearby.Value.Hex,
                        $"patrol — engages a known army right nearby at ({nearby.Value.Hex.Q},{nearby.Value.Hex.R})",
                        task, AiConfig.defenceActiveScore, AiTaskCategory.Defence);
                }

                task.Retreating = true;
                return ContinueLocalRetreat(player, ctx, task, homeHex);
            }

            task.Posture = AiDefencePosture.Patrol;
            task.PatrolVisited ??= new HashSet<HexCoord>();
            // Only mark a hex visited once the army is actually STANDING on one of its own real
            // patrol candidates (arrived last turn) — never the home hex or an arbitrary transit
            // waypoint, both of which would otherwise falsely occupy the set and make
            // FindPatrolTarget's own "visited.Count == 0 → fresh cycle" cheat-direction read think a
            // cycle was already under way from its very first call.
            if (IsOwnPatrolFacilityHex(player, task.Army.Hex))
                task.PatrolVisited.Add(task.Army.Hex);

            // 2026-08-24 fix (project owner's own log-audit report): two DefendCitadel/Patrol tasks
            // at DIFFERENT bases used to plan in total isolation from each other — PatrolVisited
            // lives per-task, so neither army's own cycle ever knew the other existed. On a real
            // multi-base game this converged to both armies picking the SAME nearest facility turn
            // after turn (one active-threat coincidence at first, then the routine Patrol geometry
            // just kept re-syncing on its own afterward) — two armies marching an almost-single-file
            // column around one shared loop instead of splitting into two coverage areas. Gathered
            // fresh every call (never cached) so a task that itself just switched away from Patrol
            // (Active/Turtle/retreating) drops out of the exclusion set the very next evaluation —
            // see FindPatrolTarget's own comment for how these two sets are actually used.
            List<AiTask> otherPatrols = AiTaskRegistry.TasksFor(player)
                .Where(t => t != task && t.Kind == AiTaskKind.DefendCitadel && t.Posture == AiDefencePosture.Patrol)
                .ToList();
            var otherPatrolTargets = new HashSet<HexCoord>(otherPatrols.Select(t => t.TargetHex));
            var otherPatrolVisited = new HashSet<HexCoord>(
                otherPatrols.SelectMany(t => t.PatrolVisited ?? Enumerable.Empty<HexCoord>()));

            HexCoord? target = FindPatrolTarget(player, task.Army, homeHex, task.PatrolVisited,
                otherPatrolTargets, otherPatrolVisited, out bool deconflicted);
            if (target == null)
            {
                if (task.Army.Hex.Equals(homeHex))
                {
                    AiTaskRegistry.Remove(player, task); // cycle complete, home, nothing left — free the army
                    return null;
                }
                if (!AiTurnController.FindAffordableStep(ctx.Map, task.Army, homeHex).HasValue)
                    return null;
                return AiDecision.Move(task.Army, homeHex, "patrol — nothing left to cover, returns to base",
                    task, AiConfig.defencePatrolScore, AiTaskCategory.Defence);
            }

            if (!AiTurnController.FindAffordableStep(ctx.Map, task.Army, target.Value).HasValue)
                return null;

            // AP-cost guard (2026-08-24, project owner's own root-cause report — see
            // AiConfig.defencePatrolMaxApFraction's own comment): this is the routine,
            // no-known-threat branch specifically — every urgent case above (Active intercept,
            // local encounter, wounded retreat, Turtle) already returned its own decision earlier
            // in this method and never reaches here, so this guard can never delay a real reaction,
            // only an ordinary background facility visit. Skipped entirely once the army already
            // activated this turn (ActivationApCost is a one-time-per-turn charge — nothing left to
            // guard against for a second move the same turn).
            if (!task.Army.HasActivatedThisTurn
                && task.Army.ActivationApCost > root.ActionPoints * AiConfig.defencePatrolMaxApFraction)
            {
                return null; // too costly for a low-priority visit this step — stays near home instead
            }

            task.TargetHex = target.Value;
            string reason = deconflicted
                ? $"patrol — visits an extraction facility (deconflicted from another base's patrol around ({homeHex.Q},{homeHex.R}))"
                : "patrol — visits an extraction facility";
            return AiDecision.Move(task.Army, target.Value, reason,
                task, AiConfig.defencePatrolScore, AiTaskCategory.Defence);
        }

        // 1.2's own local retreat — one step per call, hard-avoiding whatever's currently near the
        // army's own hex (recomputed fresh every call, never the original trigger — same
        // "re-evaluate fresh" shape every other travel decision here already follows, so a stale
        // threat never blocks a route it's no longer actually near). Ends the task once home — a
        // fresh (or, once it's idle at base for AiManagementPlanner's own repair pipeline to find,
        // healed) patrol force takes over from there, per the project owner's own "потенциально
        // новая армия патруля(или починеная)" spec — this method itself never spins one back up.
        // Scored at defenceRetreatScore, NOT defencePatrolScore — 2026-08-21 fix (own re-test
        // finding, cross-category calibration pass): a wounded/outmatched army getting to safety is
        // an urgent reaction, not routine background movement, same "1.1 vs Аgрессия's own retreat"
        // shape (aggressionBaseWeight=100 for Агрессия's own outmatched-threat retreat is already
        // the floor for "urgent enough to leave the routine 90-100 tie zone" — see AiConfig's own
        // comment on defenceActiveScore/defencePreemptScore for why routine-tier ties silently lose
        // arbitration order in AiTurnController.Decide). Used to share defenceActiveScore(120) with
        // the attack half of this same local encounter (see BuildPostureDecision) — split into its
        // own defenceRetreatScore(125) 2026-08-23 (project owner's own top-of-arbiter ladder spec):
        // falling back to safety now reads as urgent as Разведка's own scoutFleeBonus tier, one rung
        // above ordinary tactical engagement, not merely tied with it.
        private static AiDecision ContinueLocalRetreat(PlayerSetupData player, AiTurnContext ctx, AiTask task, HexCoord homeHex)
        {
            task.Posture = AiDefencePosture.Patrol;
            if (task.Army.Hex.Equals(homeHex))
            {
                AiTaskRegistry.Remove(player, task);
                return null;
            }

            HexCoord? avoidHex = StrongestSightingNear(player, new[] { task.Army.Hex }, AiConfig.patrolLocalThreatRadius)?.Hex;
            HexCoord? nextStep = AiTurnController.FindPathStepAvoidingZone(ctx.Map, task.Army, homeHex, avoidHex, 0);
            return nextStep.HasValue
                ? AiDecision.Move(task.Army, nextStep.Value, "patrol — falls back to base",
                    task, AiConfig.defenceRetreatScore, AiTaskCategory.Defence)
                : null; // boxed in avoiding the threat — wait rather than walk into it
        }

        // A real, currently-producing extraction facility this player owns — never a Base-tagged
        // building (patrolling your own citadel is a no-op). Shared by FindPatrolTarget's own
        // candidate scan and BuildPostureDecision's own "did we just arrive at one of these"
        // arrival check, so the two definitions of "a patrol stop" can never drift apart.
        private static bool IsOwnPatrolFacilityHex(PlayerSetupData player, HexCoord hex)
        {
            BuildingData building = BuildingRegistry.FindAt(hex);
            return building != null && building.Owner == player && !building.IsBase
                && AllResourceTypes.Any(t => building.CollectedAmount(t) > 0);
        }

        // Next unvisited (this cycle) own extraction-facility hex within AiConfig.patrolRadius of
        // `homeHex` — the task's own AiTask.HomeHex (citadel, or a later-founded base — each
        // DefendCitadel task patrols its OWN base's own neighborhood only, 2026-08-21, project
        // owner's own "каждая пусть обходит свою цитадель" call). A fresh cycle's first target
        // (`visited` empty) is either random, or biased toward whichever known enemy sighting is
        // nearest (random pick among several) — the project owner's own sanctioned "знаем
        // направление, не хекс" cheat, distinct from CheatEstimateRaiderThreat's own composition
        // cheat. Once under way, just the nearest remaining candidate to the army's current hex
        // (home-distance as the tie-break). When the army has no Recce (see AiArmyRoles' own class
        // comment — Recce stands in for "Scout" composition), candidates are narrowed to
        // currently-visible hexes first (1.2.1's own "держаться видимых хексов, ближе к цитадели"
        // spec) — falling back to the full candidate list only if literally nothing unvisited is
        // visible right now, rather than stalling the patrol forever.
        // 2026-08-24 fix (see the call site's own comment for the root cause): `otherPatrolTargets`/
        // `otherPatrolVisited` narrow this task's OWN candidate pool away from whatever other
        // DefendCitadel/Patrol tasks are currently claiming or have already covered, so two bases'
        // patrols split into separate coverage areas instead of converging on the same loop.
        // Deliberately local to Patrol only — Active intercept, Turtle, retreat, and SecureBase never
        // call this method at all, so none of them are affected. Three-pass fallback, never a hard
        // requirement: (1) full cross-task exclusion (neither another patrol's current TargetHex nor
        // anything it's already visited this cycle); (2) if that empties the pool, drop the
        // `visited`-only exclusion but keep dodging the other task's claimed TargetHex — a hex the
        // other army merely passed through once is fair game again before a hex it's headed for
        // right now; (3) if STILL empty, ignore cross-task state entirely and fall back to this
        // task's own ordinary candidate list, so a real coverage-radius squeeze (few facilities, many
        // patrols) never stalls this army waiting for a hex it doesn't actually need to avoid.
        // `deconflicted` reports whether pass (1) or (2) actually removed anything from the raw
        // candidate list — purely for the caller's own diagnostic log, never read for control flow.
        private static HexCoord? FindPatrolTarget(PlayerSetupData player, ArmyData army, HexCoord homeHex, HashSet<HexCoord> visited,
            HashSet<HexCoord> otherPatrolTargets, HashSet<HexCoord> otherPatrolVisited, out bool deconflicted)
        {
            deconflicted = false;
            List<HexCoord> candidates = BuildingRegistry.AllBuildings()
                .Where(b => IsOwnPatrolFacilityHex(player, b.Hex)
                    && HexGridMath.Distance(homeHex, b.Hex) <= AiConfig.patrolRadius
                    && !visited.Contains(b.Hex))
                .Select(b => b.Hex)
                .ToList();
            if (candidates.Count == 0)
                return null;

            List<HexCoord> fullyDeconflicted = candidates
                .Where(h => !otherPatrolTargets.Contains(h) && !otherPatrolVisited.Contains(h)).ToList();
            List<HexCoord> targetOnlyDeconflicted = candidates.Where(h => !otherPatrolTargets.Contains(h)).ToList();
            List<HexCoord> baseCandidates = fullyDeconflicted.Count > 0 ? fullyDeconflicted
                : targetOnlyDeconflicted.Count > 0 ? targetOnlyDeconflicted
                : candidates;
            deconflicted = baseCandidates.Count < candidates.Count;

            List<HexCoord> visible = AbilityParams.ArmyHasAnyRecce(army) ? baseCandidates : baseCandidates.Where(h => VisionSystem.IsVisible(player, h)).ToList();
            List<HexCoord> pool = visible.Count > 0 ? visible : baseCandidates;

            if (visited.Count == 0)
            {
                HexCoord? enemyDirection = RandomKnownEnemyHex(player);
                return enemyDirection.HasValue
                    ? pool.OrderBy(h => HexGridMath.Distance(h, enemyDirection.Value)).First()
                    : pool[UnityEngine.Random.Range(0, pool.Count)];
            }
            return pool.OrderBy(h => HexGridMath.Distance(army.Hex, h)).ThenBy(h => HexGridMath.Distance(homeHex, h)).First();
        }

        private static HexCoord? RandomKnownEnemyHex(PlayerSetupData player)
        {
            List<HexCoord> hexes = AiMapMemory.AllKnownEnemySightings(player).Select(s => s.Hex).ToList();
            return hexes.Count > 0 ? hexes[UnityEngine.Random.Range(0, hexes.Count)] : (HexCoord?)null;
        }

        // Оборона · Задача 2's own Recce pickup — priority shifts from "only what's already idle at
        // the garrison hex" early game to "may also pull Разведка's own idle solo scout, same hex
        // only" once Разведка's own routine movement score starts decaying
        // (AiConfig.reconPriorityDecayStartTurn) — the project owner's own call: Разведка is winding
        // down by then anyway, folding one of its scouts into a patrol beats it sitting idle. Reads
        // only pool.AvailableArmies() (never the raw ArmyRegistry) so an army an active Разведка task
        // is still actually using (claimed by AiTurnController.Decide's own upfront sweep) is never
        // offered here — same cross-category safety every other recruit lookup in this codebase
        // already gets for free.
        private static UnitData FindPatrolRecceCandidate(PlayerSetupData player, int turnNumber, HexCoord hex, AiResourcePool pool, out ArmyData source)
        {
            source = null;
            // 2026-08-26, project owner's own spec item 1 — a Recce-tagged AIRCRAFT (AirRecon is
            // its own separate task/pipeline, see AiArmyRoles.IsSoloRecce's own comment) must never
            // get folded into a ground patrol just because it happens to also carry the Recce
            // ability; excludes both the whole source army (an air army/airfield) and the member
            // itself.
            ArmyData atGarrison = pool.AvailableArmies()
                .FirstOrDefault(a => !a.IsPrison && !AviationRules.IsAirArmy(a) && !AviationRules.IsAirfield(a)
                    && a.Hex.Equals(hex) && a.Members.Any(m => !m.IsAviation && AbilityParams.UnitHasAnyRecce(m)
                    && AiArmyRoles.CanSpareGarrisonMember(player, a, m)));
            if (atGarrison != null)
            {
                source = atGarrison;
                return atGarrison.Members.First(m => !m.IsAviation && AbilityParams.UnitHasAnyRecce(m));
            }

            if (turnNumber < AiConfig.reconPriorityDecayStartTurn)
                return null;

            ArmyData recon = pool.AvailableArmies().FirstOrDefault(a => AiArmyRoles.IsSoloRecce(a) && a.Hex.Equals(hex));
            if (recon == null)
                return null;
            source = recon;
            return recon.Members[0];
        }

        // ---- SecureBase orchestration (2026-08-24, project owner's own spec) ----
        //
        // Thin on purpose — every content decision (trigger, donor/composition pick, phase
        // lifecycle, cancel/complete) lives on SecureBaseTask itself (see its own class comment);
        // this planner only scans for which bases need the task, keeps one task per base (never a
        // duplicate), and forwards whatever AiDecision SecureBaseTask builds to the Arbiter.

        // One AiTask per base needing it, capped at AiConfig.maxConcurrentSecureBase registered at
        // once (same "don't let one category spread across every base" intent every other
        // maxConcurrentX cap in this codebase already enforces). Deliberately does NOT register a
        // task for a base with no donor available yet — same "only ever create the task alongside
        // its own first real dispatch" convention AiAggressionPlanner.TryRaidRegroupCandidates
        // already follows for RaidReinforce (see that method's own comment) — AiTurnController.
        // Commit is what actually adds decision.Task to the registry, and only if this candidate
        // wins arbitration, so a task built here that never wins simply evaporates, no cleanup
        // needed.
        // Per-(player, hex) turn number this method last logged its own "no donor available" line
        // (2026-08-24 P1 log-noise fix, project owner's own report) — a base with genuinely no
        // spareable donor ANYWHERE stays in this exact state every single Decide() step until
        // something changes, so without this the identical line would repeat dozens of times a
        // turn. Not part of AiTask (nothing here is a registered task yet at this point — see this
        // method's own comment on why a donor-less base never gets one) and not reset anywhere —
        // stale entries for a since-secured/lost base are harmless dead weight, same tolerance
        // every other small debug-only lookup table in this codebase already accepts.
        private static readonly Dictionary<(PlayerSetupData, HexCoord), int> NoDonorLoggedTurn =
            new Dictionary<(PlayerSetupData, HexCoord), int>();

        public static List<AiDecision> TryStartSecureBaseCandidates(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx)
        {
            var results = new List<AiDecision>();
            if (AiTaskRegistry.CountActive(player, AiTaskKind.SecureBase) >= AiConfig.maxConcurrentSecureBase)
                return results;

            var alreadyTasked = new HashSet<HexCoord>(AiTaskRegistry.TasksFor(player)
                .Where(t => t.Kind == AiTaskKind.SecureBase).Select(t => t.HomeHex));

            foreach (HexCoord hex in SecureBaseTask.NeedsSecuring(player))
            {
                if (alreadyTasked.Contains(hex))
                    continue;

                var task = new AiTask { Kind = AiTaskKind.SecureBase, HomeHex = hex, TargetHex = hex };
                AiDecision decision = SecureBaseTask.BuildDecision(player, root, ctx, task);
                if (decision == null)
                {
                    var key = (player, hex);
                    if (!NoDonorLoggedTurn.TryGetValue(key, out int lastTurn) || lastTurn != ctx.TurnNumber)
                    {
                        NoDonorLoggedTurn[key] = ctx.TurnNumber;
                        AiDebugLog.Write($"[AI] {player.Nickname}: SecureBase — base at ({hex.Q},{hex.R}) needs "
                            + $"{SecureBaseTask.RequiredDefenders(player, hex)} defender(s) but no donor is available right now.");
                    }
                    continue; // no task registered — retried fresh next step, see this method's own comment
                }
                results.Add(decision);
                break; // one new SecureBase dispatch per step — see maxConcurrentSecureBase's own comment
            }
            return results;
        }

        // Advances an ALREADY-registered SecureBase task — cancel/complete are checked here (the
        // registry lifecycle is this planner's own job), the actual phase decision is
        // SecureBaseTask.BuildDecision's job (shared with TryStartSecureBaseCandidates above, same
        // split TryContinueDefenceTask/TryStartDefenceCandidates already follow for
        // BuildPostureDecision).
        //
        // 2026-08-24 P0 fix (project owner's own report): a lost base used to just remove the task
        // outright, even with a courier (task.Army) already dispatched and possibly carrying a live
        // unit — that courier became a permanently untasked field army nobody would ever route home
        // again. Now redirects it to the nearest still-owned garrison instead (see SecureBaseTask.
        // RedirectToNearestOwnBase's own comment) and immediately re-evaluates under its own new
        // HomeHex this same call, rather than removing the task only for a fresh one to eventually
        // reinvent the same trip. Only removes outright when there's genuinely no courier to save.
        public static AiDecision TryContinueSecureBaseTask(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx, AiTask task)
        {
            if (SecureBaseTask.ShouldCancel(player, task))
            {
                if (task.Army != null)
                {
                    HexCoord lostHex = task.HomeHex;
                    SecureBaseTask.RedirectToNearestOwnBase(player, task);
                    AiDebugLog.Write($"[AI] {player.Nickname}: SecureBase — base at ({lostHex.Q},{lostHex.R}) is no "
                        + $"longer ours, \"{task.Army.Name}\" redirected to ({task.HomeHex.Q},{task.HomeHex.R}) instead "
                        + "of being abandoned in the field.");
                    return SecureBaseTask.BuildDecision(player, root, ctx, task);
                }
                AiDebugLog.Write($"[AI] {player.Nickname}: SecureBase — base at ({task.HomeHex.Q},{task.HomeHex.R}) "
                    + "is no longer ours, task cancelled.");
                AiTaskRegistry.Remove(player, task);
                return null;
            }
            if (SecureBaseTask.IsComplete(player, task, out string reason))
            {
                AiDebugLog.Write($"[AI] {player.Nickname}: SecureBase — {reason}");
                AiTaskRegistry.Remove(player, task);
                return null;
            }
            return SecureBaseTask.BuildDecision(player, root, ctx, task);
        }

        // ---- Orchestration (AiTurnController.Decide's own candidate sources) ----

        // Advances an ALREADY-committed DefendCitadel task — validity/AP/movement gate here, the
        // actual Patrol/Active/Turtle decision is BuildPostureDecision's own job (shared with
        // TryStartDefenceCandidates, see that method's own comment).
        public static AiDecision TryContinueDefenceTask(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx, AiTask task)
        {
            if (task.Army?.Controller == null || !ArmyRegistry.AllForOwner(player).Contains(task.Army))
            {
                AiTaskRegistry.Remove(player, task);
                return null;
            }
            if (task.Army.CurrentMovement <= 0 || (!task.Army.HasActivatedThisTurn && !root.CanSpendActionPoints(task.Army.ActivationApCost)))
                return null;

            return BuildPostureDecision(player, root, ctx, task);
        }

        // Both "start a brand new Оборона force" and "recruit/patch the next member into an already-
        // forming one" for EVERY one of this player's own garrisoned hexes — one DefendCitadel task
        // per home now (see AiConfig.maxConcurrentDefend, raised 1→2 alongside AiTask.HomeHex,
        // 2026-08-21), not the old single shared task. Citadel-first hard tie-break (project owner's
        // own explicit call, chosen over a mere scoring bonus): homes are tried in this fixed order,
        // citadel always first, and whichever home's own tier has ANYTHING to propose this step wins
        // outright — a later base's own assembly is only even evaluated once the citadel's own tier
        // has nothing left to ask for THIS step (already ready, already mid-decision, or genuinely
        // blocked). This is a real ordering guarantee, not a score comparison that merely usually
        // favors the citadel — the two never even compete for the same step's execution slot.
        public static List<AiDecision> TryStartDefenceCandidates(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx, AiResourcePool pool)
        {
            HexCoord citadelHex = AiTurnController.GarrisonHexFor(player);
            foreach (HexCoord homeHex in AiTurnController.OwnGarrisonHexes(player).OrderBy(h => h.Equals(citadelHex) ? 0 : 1))
            {
                List<AiDecision> homeResults = TryStartDefenceCandidatesFor(player, root, ctx, pool, homeHex);
                if (homeResults.Count > 0)
                    return homeResults;
            }
            return new List<AiDecision>();
        }

        // Одна база — see TryStartDefenceCandidates' own comment for the per-home loop/tie-break
        // this is called from. Gated on a real TRIGGER now (2026-08-22, project owner's own spec —
        // see this class's own header comment): with no known sighting near this home AND
        // CheatEstimateRaiderThreat finding nothing either, Оборона proposes nothing at all here —
        // no new task, no new army request, no further recruiting into an already-forming one.
        // `required` — which composition target applies right now: Active's own dynamic 60/40 vs
        // the actual sighted army when there is one (MeetsActiveComposition), Patrol's fixed
        // member-count target otherwise (PatrolCompositionReady) — same split BuildPostureDecision's
        // own assembling gate already uses, kept as one local delegate so the two can never
        // disagree on the same task this same step.
        private static List<AiDecision> TryStartDefenceCandidatesFor(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            AiResourcePool pool, HexCoord homeHex)
        {
            var results = new List<AiDecision>();

            AiMapMemory.KnownEnemySighting? activeSighting = FindActiveThreatSighting(player, homeHex);
            if (!activeSighting.HasValue && !PatrolThreatPresent(player, homeHex))
                return results; // nothing triggers Оборона on this home right now

            bool IsComposedReady(ArmyData army) => activeSighting.HasValue
                ? MeetsActiveComposition(army, activeSighting.Value, ctx.Map)
                : PatrolCompositionReady(army);

            // Recruiting/growing the force under a mere PROXIMITY heuristic (no confirmed sighting
            // yet) is buildup, not urgent — scored off DynamicPatrolUrgencyScore (see that method's
            // own comment), same as before. But once activeSighting actually HAS a value, this is
            // no longer a heuristic guess — it's the exact same confirmed AiMapMemory sighting
            // BuildPostureDecision's own Active branch reacts to below, so the recruit this step
            // would feed the defender is exactly as urgent as the engagement itself (2026-08-23,
            // project owner's own report — see AiConfig.defenceActiveAssemblyScore's own comment
            // for the starvation this fixes: a real threat's own recruit used to still lose to a
            // routine Raid assembly pull at a flat 110 every step).
            float assemblyScore = activeSighting.HasValue ? AiConfig.defenceActiveAssemblyScore : DynamicPatrolUrgencyScore(player, homeHex);

            AiTask existing = AiTaskRegistry.TasksFor(player).FirstOrDefault(t => t.Kind == AiTaskKind.DefendCitadel && t.HomeHex.Equals(homeHex));
            if (existing != null)
            {
                if (existing.Army == null || !existing.Army.Hex.Equals(homeHex))
                    return results; // travelling/patrolling/retreating — TryContinueDefenceTask's own turn

                if (IsComposedReady(existing.Army))
                {
                    existing.StillAssembling = false; // see AiTask.StillAssembling's own comment
                    if (!AbilityParams.ArmyHasAnyRecce(existing.Army))
                    {
                        UnitData recce = FindPatrolRecceCandidate(player, ctx.TurnNumber, homeHex, pool, out ArmyData recceSource);
                        if (recce != null && recceSource != null && existing.Army.HasRoom && !ctx.WouldRevisitArmy(recce, existing.Army)
                            && CanAffordSwapInto(existing.Army, recce))
                            results.Add(AiDecision.ActiveDefenceForce(recceSource, recce, existing.Army, existing, AiConfig.defencePatrolScore));
                    }
                    return results; // ready — TryContinueDefenceTask picks it up from here
                }
                existing.StillAssembling = true;

                UnitData existingRecruit = RaidWeakerArmyTask.FindRecruitAt(player, homeHex, existing.Army, pool, out ArmyData existingSource);
                if (existingRecruit != null && existingSource != null && existing.Army.HasRoom && !ctx.WouldRevisitArmy(existingRecruit, existing.Army)
                    && CanAffordSwapInto(existing.Army, existingRecruit))
                {
                    results.Add(AiDecision.ActiveDefenceForce(existingSource, existingRecruit, existing.Army, existing, assemblyScore));
                    return results;
                }

                // Full but still not IsComposedReady above — FindRecruitAt's own plain add can't
                // help once HasRoom is false, so try a straight upgrade instead (see
                // TryStrengthenCandidate's own comment).
                if (!existing.Army.HasRoom)
                {
                    AiDecision strengthen = TryStrengthenCandidate(player, existing.Army, existing, pool, ctx, homeHex, assemblyScore, activeSighting);
                    if (strengthen != null)
                        results.Add(strengthen);
                }
                return results;
            }

            if (AiTaskRegistry.TasksFor(player).Count(t => t.Kind == AiTaskKind.DefendCitadel) >= AiConfig.maxConcurrentDefend)
                return results;

            ArmyData readyDefender = FindReadyIdleDefender(IsComposedReady, pool);
            if (readyDefender != null)
            {
                var readyTask = new AiTask { Kind = AiTaskKind.DefendCitadel, Army = readyDefender, Posture = AiDefencePosture.Patrol, HomeHex = homeHex };

                // FindReadyIdleDefender only returns an army that already satisfies the current
                // composition requirement (IsComposedReady). Pulling one more unit into it here
                // just re-runs an assembly-tier ActiveDefenceForce recruit and defers the
                // intercept by at least one arbiter step — for Active the army could instead move
                // now at defenceActiveScore. A still-forming army is untouched: it never reaches
                // this branch and keeps its normal FindRecruitAt / TryStrengthenCandidate path.
                AiDecision first = BuildPostureDecision(player, root, ctx, readyTask);
                if (first != null)
                    results.Add(first);
                else if (AiTurnController.CanIssueMoveNow(root, player, readyDefender, ctx.Map, homeHex))
                    results.Add(AiDecision.Move(readyDefender, homeHex, "citadel defense — reports to the garrison",
                        readyTask, AiConfig.defencePatrolScore, AiTaskCategory.Defence));
                return results;
            }

            ArmyData forming = pool.AvailableArmies().FirstOrDefault(a => AiArmyRoles.IsEmptyDeployableArmy(a) && a.Hex.Equals(homeHex));
            if (forming == null)
            {
                bool idleEmptyArmyExistsElsewhere = pool.AvailableArmies().Any(a => AiArmyRoles.IsEmptyDeployableArmy(a));
                if (!idleEmptyArmyExistsElsewhere && root.CanSpendActionPoints(ArmyActions.CreateArmyApCost))
                    results.Add(AiDecision.RequestDefendArmy(homeHex, AiConfig.defencePatrolScore + AiConfig.raidRequestArmyPenalty));
                return results;
            }

            var newTask = new AiTask { Kind = AiTaskKind.DefendCitadel, Army = forming, Posture = AiDefencePosture.Patrol, StillAssembling = true, HomeHex = homeHex };
            UnitData recruit = RaidWeakerArmyTask.FindRecruitAt(player, homeHex, forming, pool, out ArmyData source);
            if (recruit != null && source != null && !ctx.WouldRevisitArmy(recruit, forming) && CanAffordSwapInto(forming, recruit))
                results.Add(AiDecision.ActiveDefenceForce(source, recruit, forming, newTask, assemblyScore));
            return results;
        }

        // Own copy of RaidWeakerArmyTask.FindReadyIdleArmy's own filter (same solo-Recce/solo-hero
        // exclusion, same "strongest first" ordering — see that method's own comment for why both
        // exist), parameterized on `isReady` instead of a fixed ThreatStrength so it can serve
        // either Patrol's fixed count target or Active's dynamic per-sighting one via the same
        // IsComposedReady delegate TryStartDefenceCandidatesFor's own caller already built.
        private static ArmyData FindReadyIdleDefender(System.Func<ArmyData, bool> isReady, AiResourcePool pool)
        {
            return pool.AvailableArmies()
                .Where(a => !a.IsGarrison && !a.IsPrison && a.Members.Count > 0
                    // 2026-08-26, project owner's own spec item 1 — aviation gets only aviation
                    // tasks, never ground patrol/intercept duty (see BuildPostureDecision's own
                    // invariant guard).
                    && !AviationRules.IsAirArmy(a) && !AviationRules.IsAirfield(a)
                    && !AiArmyRoles.IsSoloRecce(a) && !AiArmyRoles.IsSoloHeroAwaitingEscort(a)
                    && isReady(a))
                .OrderByDescending(a => WorthIt.AttackSum(a))
                .FirstOrDefault();
        }

        // Оборона · full-but-insufficient upgrade (project owner's own report: an 8/8 Active
        // Defence force that still fails RaidWeakerArmyTask.IsReady against its own threat used to
        // just sit there forever — FindRecruitAt/HasRoom gate everything the "start"/"continue"
        // tiers above can do once there's no free slot left). Picks THIS army's own weakest
        // non-hero member (Defense+Attack, same "power" yardstick GarrisonReorgTask already uses
        // for its own swap/balance moves) and the single strongest non-hero candidate sitting
        // anywhere else at the garrison hex — garrison stock or a co-located idle/task army alike,
        // same spatial scope FindRecruitAt itself already uses (no cross-map courier trip, this is
        // an instant 1-for-1 trade). A genuine upgrade only — skipped outright if nothing found
        // beats the member it would replace, so this can never thrash a roster back and forth.
        //
        // HP-aware since 2026-08-23 (project owner's own report — bring Defence's own recruit pick
        // in line with RaidWeakerArmyTask.FindRecruitAt's own rule) — same two-pass shape: a
        // critically wounded candidate (IsCriticallyWounded's own ≤50%HP threshold) is only ever
        // offered here once nothing healthy beats `weakest`'s own power. Without this, a half-dead
        // unit that merely had the higher raw Defense+Attack stat could get pulled straight into the
        // defense force ahead of a healthy, otherwise-equal-or-better one — same "wounded stat
        // looks strong on paper but dies early in the real fight" gap FindRecruitAt's own comment
        // already documents for Raid.
        private static AiDecision TryStrengthenCandidate(PlayerSetupData player, ArmyData army, AiTask task,
            AiResourcePool pool, AiTurnContext ctx, HexCoord homeHex, float score, AiMapMemory.KnownEnemySighting? activeSighting)
        {
            UnitData weakest = army.Members.Where(m => !m.IsHero)
                .OrderBy(m => m.Defense + m.Attack).FirstOrDefault();
            if (weakest == null)
                return null;

            UnitData best = FindStrengthenCandidate(player, army, homeHex, weakest, pool, ctx.Map, activeSighting, allowCriticallyWounded: false, out ArmyData bestSource);
            if (best == null)
                best = FindStrengthenCandidate(player, army, homeHex, weakest, pool, ctx.Map, activeSighting, allowCriticallyWounded: true, out bestSource);
            if (best == null || bestSource == null)
                return null;
            if (!CanAffordSwapInto(army, best) || !CanAffordSwapInto(bestSource, weakest))
                return null;
            if (ctx.WouldRevisitArmy(best, army) || ctx.WouldRevisitArmy(weakest, bestSource))
                return null;
            // Same feasibility check StrengthenDefenceForceRoutine's own ArmyActions.SwapMembers
            // call re-runs at execution time (project owner's own report: a candidate whose
            // capacity math didn't actually work kept getting proposed and rejected every single
            // step, burning the whole turn's maxStepsPerTurn budget on one impossible swap) — run
            // it here too so an unfittable swap never becomes a decision in the first place.
            if (!ArmyActions.CanSwapMembers(weakest, army, best, bestSource, out _))
                return null;

            var move = new GarrisonReorgTask.SwapMove(army, weakest, bestSource, best,
                $"\"{army.Name}\" swaps in {best.Name} for {weakest.Name} — still not strong enough for its target, no free slot to just recruit");
            return AiDecision.StrengthenDefenceForce(move, task, score);
        }

        // TryStrengthenCandidate's own single-candidate scan, split out so the HP-preference/
        // fallback shape can be one loop called twice (see that method's own comment) instead of
        // duplicated inline — same "healthy-first, wounded-only-if-nothing-else" two-pass FindRecruitAt
        // already establishes.
        //
        // 2026-08-24 fix (project owner's own report): with a confirmed `activeSighting`, ranking
        // used to be the raw Defense+Attack sum — every swap the log showed (Rust Tank→Scrap
        // Mortar→Rad Brute→Colossus) was a genuine power gain, yet the army never got closer to
        // actually clearing MeetsActiveComposition's own WorthIt.MeetsWinChance gate. Now ranked by
        // the SAME ingredients that gate: WorthIt.CanDamageAll coverage first (closing a false→true
        // gap always wins, a true→false regression is never accepted), then the real WinChance a
        // hypothetical swap would buy, raw power only as the final tie-break. No `activeSighting`
        // (Patrol posture — nothing to size a win chance against) falls back to the original raw-
        // power heuristic unchanged.
        private static UnitData FindStrengthenCandidate(PlayerSetupData player, ArmyData army, HexCoord homeHex, UnitData weakest, AiResourcePool pool,
            HexMap map, AiMapMemory.KnownEnemySighting? activeSighting, bool allowCriticallyWounded, out ArmyData source)
        {
            source = null;
            UnitData best = null;

            if (!activeSighting.HasValue)
            {
                float bestPower = weakest.Defense + weakest.Attack;
                foreach (ArmyData candidate in pool.AvailableArmies())
                {
                    if (candidate == army || candidate.IsPrison || !candidate.Hex.Equals(homeHex))
                        continue;
                    foreach (UnitData unit in candidate.Members)
                    {
                        // 2026-08-26, project owner's own spec item 1 — aviation never becomes
                        // ground defense fodder (the activeSighting branch below already excluded
                        // this; the Patrol/no-sighting branch here was missing it).
                        if (unit.IsHero || AbilityParams.UnitHasAnyRecce(unit) || unit.IsAviation)
                            continue;
                        if (!allowCriticallyWounded && unit.HitPointsCurrent <= unit.HitPointsMax / 2)
                            continue;
                        if (!AiArmyRoles.CanSpareGarrisonMember(player, candidate, unit))
                            continue;
                        float power = unit.Defense + unit.Attack;
                        if (power <= bestPower)
                            continue;
                        bestPower = power;
                        source = candidate;
                        best = unit;
                    }
                }
                return best;
            }

            IReadOnlyList<WorthIt.DefenderProfile> defenders = activeSighting.Value.Defenders;
            float hexBonus = WorthIt.HexDefenseBonus(activeSighting.Value.Hex, map);
            List<UnitData> baseRoster = army.Members.Where(m => !m.IsHero && m != weakest).ToList();
            IEnumerable<UnitData> currentRoster = army.Members.Where(m => !m.IsHero);

            bool baselineCoverage = WorthIt.CanDamageAll(currentRoster, defenders, hexBonus);
            float baselineWinChance = EvaluateWinChance(currentRoster, defenders, activeSighting.Value, hexBonus);

            bool bestCoverage = baselineCoverage;
            float bestWinChance = baselineWinChance;
            float bestPowerTieBreak = weakest.Defense + weakest.Attack;
            bool found = false;

            foreach (ArmyData candidate in pool.AvailableArmies())
            {
                if (candidate == army || candidate.IsPrison || !candidate.Hex.Equals(homeHex))
                    continue;
                foreach (UnitData unit in candidate.Members)
                {
                    if (unit.IsHero || unit.IsAviation || AbilityParams.UnitHasAnyRecce(unit))
                        continue;
                    if (!allowCriticallyWounded && unit.HitPointsCurrent <= unit.HitPointsMax / 2)
                        continue;
                    if (!AiArmyRoles.CanSpareGarrisonMember(player, candidate, unit))
                        continue;

                    List<UnitData> hypothetical = baseRoster.Append(unit).ToList();
                    bool coverage = WorthIt.CanDamageAll(hypothetical, defenders, hexBonus);
                    if (baselineCoverage && !coverage)
                        continue; // never trade away coverage this army already has

                    float winChance = EvaluateWinChance(hypothetical, defenders, activeSighting.Value, hexBonus);
                    float power = unit.Defense + unit.Attack;

                    bool better;
                    if (coverage != bestCoverage)
                        better = coverage; // false→true beats every already-found candidate outright
                    else if (winChance != bestWinChance)
                        better = winChance > bestWinChance;
                    else
                        better = power > bestPowerTieBreak;

                    if (!found || better)
                    {
                        found = true;
                        bestCoverage = coverage;
                        bestWinChance = winChance;
                        bestPowerTieBreak = power;
                        source = candidate;
                        best = unit;
                    }
                }
            }

            if (best == null)
                return null;

            // Only actually worth the swap if it closes a coverage gap outright, or buys a real
            // WinChance gain over what this army can already do today — see
            // AiConfig.defenceSwapMinWinChanceGain's own comment.
            bool coverageClosed = !baselineCoverage && bestCoverage;
            bool winChanceImproved = bestWinChance >= baselineWinChance + AiConfig.defenceSwapMinWinChanceGain;
            if (!coverageClosed && !winChanceImproved)
            {
                source = null;
                return null;
            }
            return best;
        }

        // WorthIt.MeetsWinChance's own "full roster if we have per-unit Defenders, aggregate sum
        // otherwise" branch, reusable against a hypothetical (not-yet-real) roster — WorthIt's own
        // ArmyData-overload WinChance can't be asked about a swap that hasn't happened yet, so this
        // builds the same DefenderProfile snapshot FindStrengthenCandidate needs by hand instead.
        private static float EvaluateWinChance(IEnumerable<UnitData> attackerUnits, IReadOnlyList<WorthIt.DefenderProfile> defenders,
            AiMapMemory.KnownEnemySighting sighting, float hexBonus)
        {
            if (defenders != null && defenders.Count > 0)
            {
                List<WorthIt.DefenderProfile> attackerProfiles = attackerUnits
                    .Select(u => new WorthIt.DefenderProfile(u.Defense, u.HasAbility(UnitAbilities.CeramicArmor),
                        u.TypeTags.ToList(), u.Attack, u.HitPointsCurrent, u.Initiative))
                    .ToList();
                return WorthIt.WinChance(attackerProfiles, defenders, hexBonus);
            }
            float ourAttack = attackerUnits.Sum(u => u.Attack);
            float ourDefense = attackerUnits.Sum(u => u.Defense);
            return WorthIt.WinChance(ourAttack, ourDefense, sighting.AttackSum, sighting.DefenseSum);
        }

        // Same "already-activated armies pay for what joins them" pre-check GarrisonReorgTask.
        // CanAffordTransferInto already runs before proposing a swap/transfer of its own —
        // ArmyActions.SwapMembers/TransferMember re-validate this anyway, but checking here first
        // avoids emitting a decision that's guaranteed to fail outright (2026-08-23 fix, project
        // owner's own report: every ActiveDefenceForce recruit/merge/recce site below this method
        // used to skip this check entirely, so a target that had already activated this turn with
        // no AP left to spare kept getting offered the exact same doomed recruit every remaining
        // step of the turn — AssembleRaidForceRoutine's own TransferMember call would silently
        // fail, nothing about the world changed, and the identical candidate just came right back
        // next step, burning the whole AiConfig.maxStepsPerTurn budget on one impossible move).
        // Now shared by every recruit/merge/recce/siege-strip tier in this class, not just
        // TryStrengthenCandidate's own swap.
        private static bool CanAffordSwapInto(ArmyData target, UnitData unit)
        {
            if (!target.HasActivatedThisTurn)
                return true;
            PlayerRoot targetRoot = PlayerRootRegistry.FindFor(target.Owner);
            return targetRoot != null && targetRoot.CanSpendActionPoints(unit.ActivationApCost);
        }

        // Оборона's own emergency reinforcement — now gated on IsUnderSiege (Turtle only, per the
        // project owner's own call: outside a real siege there's no urgent need to strip another
        // category's task for the citadel's sake). This is the ONLY place in this whole class that
        // ever sets AiDecision.PreemptedTask / reaches past a live task's own claim — ordinary
        // Active(defenceActiveScore=120)/Patrol, at the citadel OR any later-founded base, runs
        // entirely through BuildPostureDecision/TryStartDefenceCandidatesFor instead, which only
        // ever draw from AiResourcePool.AvailableArmies() (armies with no task of their own), never
        // from another category's committed army (2026-08-23, project owner's own explicit call: a
        // routine threat at Base2 must fight with its own Defence-army/assembly, never reach across
        // the map for a Raid or a BuildBase army — only a real Citadel Turtle emergency has that
        // right). IsUnderSiege itself is citadel-only (AiTurnController.GarrisonHexFor is always the
        // starting citadel, see its own definition) — a threat at Base2 alone can never even reach
        // this method's own IsUnderSiege gate above, regardless of score. Only fires once idle
        // reinforcement genuinely can't cover the defending army's own ceiling, and even then
        // only pulls a field army off
        // routine work elsewhere (never another ready-to-strike raid/defense, or an already-
        // retreating raid) — same "cancel and redirect" primitive AiEconomyPlanner.
        // TryStartEconomyCandidates already established via AiDecision.PreemptedTask. One
        // exception to "never another raid" (2026-08-23, project owner's own call): a StillAssembling
        // raid parked right at the citadel — see FindSiegeRaidStripCandidate below — has no
        // offensive sunk cost yet and is fair game, same 130 tier as everything else here. BuildBase
        // is fair game outright, unlike AiEconomyPlanner's own routine-preemption protection for it
        // — see the loop's own comment below.
        public static List<AiDecision> TryDefencePreemptCandidates(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx)
        {
            var results = new List<AiDecision>();
            if (!IsUnderSiege(player, ctx))
                return results;

            // Falls back to the bare garrison when no DefendCitadel task exists yet at all —
            // 2026-08-21 fix (simulation report finding): the very FIRST siege of a game, before
            // Оборона has ever fielded anything, used to get NO emergency reinforcement here at all
            // (this whole method bailed out immediately on `defenceTask == null`), leaving only the
            // much slower one-recruit-per-step RequestDefendArmy pipeline to respond — exactly
            // backwards, the earliest possible siege deserves the strongest response, not the
            // weakest. Same reference IsUnderSiege itself already falls back to.
            //
            // Filtered to the CITADEL's own task specifically (HomeHex match) now that a second
            // base can have its own separate DefendCitadel task — IsUnderSiege is citadel-only (see
            // BuildPostureDecision's own Turtle gate), so emergency preempt must reinforce the
            // citadel's defender, never accidentally grab whichever task FirstOrDefault happened to
            // find.
            HexCoord citadelHexForPreempt = AiTurnController.GarrisonHexFor(player);
            AiTask defenceTask = AiTaskRegistry.TasksFor(player)
                .FirstOrDefault(t => t.Kind == AiTaskKind.DefendCitadel && t.HomeHex.Equals(citadelHexForPreempt));
            ArmyData reference = defenceTask?.Army ?? AiTurnController.GarrisonArmyFor(player);
            if (reference == null)
                return results;

            RaidWeakerArmyTask.ThreatStrength required = CheatEstimateRaiderThreat(player, citadelHexForPreempt);
            if (RaidWeakerArmyTask.IsReady(reference, required))
                return results; // already strong enough — normal continuation (or the garrison itself) handles the rest

            // A StillAssembling Раид sitting right at the citadel is a different case from every
            // exclusion the loop below enforces (project owner's own call, 2026-08-23): a raid
            // that's already ready-to-strike or already retreating stays untouchable (real
            // offensive sunk cost, or already mid-recall on its own), but one that never left —
            // hero home, one or two recruits in, still short of RequiredStrengthAt — has spent
            // nothing offensive yet, and its bodies are already exactly where Defence needs them.
            // Reaches past this step's own AiResourcePool claim on purpose (the raid task claimed
            // this army already, same as FindBuildBaseArmy's own second pass does for Агрессия's
            // BuildBase) — only while defenceTask actually exists (the very-first-siege bootstrap
            // above already falls back to the bare garrison with no task to fold into; that rare
            // case is left to the ordinary recruit tiers instead of special-cased here). Same
            // hero/Recce protection and "strongest first" ordering every other per-unit strip tier
            // here already uses, one member per call like every assembly move in this codebase.
            if (defenceTask != null)
            {
                UnitData siegeRecruit = FindSiegeRaidStripCandidate(player, citadelHexForPreempt, reference, out ArmyData siegeSource);
                if (siegeRecruit != null && siegeSource != null && reference.HasRoom && !ctx.WouldRevisitArmy(siegeRecruit, reference)
                    && CanAffordSwapInto(reference, siegeRecruit))
                    results.Add(AiDecision.ActiveDefenceForce(siegeSource, siegeRecruit, reference, defenceTask, AiConfig.defencePreemptScore));
            }

            // No separate "idle armies alone would eventually cover it" ceiling pre-check any more
            // (2026-08-22, project owner's own call, same simplification RaidWeakerArmyTask's own
            // dead-end gate already went through — see its own class comment): WorthIt.IsReady
            // already answers "is the win chance real", ordinary recruiting already pulls the
            // strongest available units into the defending army on its own (see TryStartDefence
            // Candidates' own recruit/merge tiers below), so a real siege with the defender still
            // not ready always proceeds straight to weighing an emergency field-army recall — no
            // hypothetical-ceiling detour first.
            HexCoord garrisonHex = AiTurnController.GarrisonHexFor(player);
            foreach (ArmyData army in ArmyRegistry.AllForOwner(player))
            {
                if (army == reference || army.IsGarrison || army.IsPrison || army.Members.Count == 0
                    // 2026-08-26, project owner's own spec item 1 — an emergency citadel recall is
                    // still a ground reinforcement; aviation never gets pulled into it (see
                    // BuildPostureDecision's own invariant guard).
                    || AviationRules.IsAirArmy(army) || AviationRules.IsAirfield(army)
                    || army.Hex.Equals(garrisonHex) || army.Controller == null || army.CurrentMovement <= 0
                    || !BattleInitiator.IsCombatCapable(army) || AiArmyRoles.IsSoloRecce(army))
                    continue;

                // BuildBase deliberately has NO exclusion clause here (project owner's own call,
                // 2026-08-23: "от Citadel emergency BuildBase защищать нельзя") — unlike
                // AiEconomyPlanner.IsProtectedFromEconomyPreemption, which shields BuildBase from a
                // routine BuildFacility, a REAL siege must outrank even an outpost one step from
                // execute (buildBaseExecuteScore=120, buildBaseTravelBonus-tier=105 — both already
                // below defencePreemptScore=130). A BuildBase army falls through this whole
                // condition untouched and gets recalled/preempted exactly like any other field army
                // below; Commit's own generic AiTaskRegistry.Remove(decision.PreemptedTask) already
                // clears the old task fully on commit (BuildBase reserves nothing that needs an
                // AiResourceReservation.Release the way BuildFacility does), so Агрессия is free to
                // re-decide whether/where to build again once the siege is over.
                AiTask existingTask = AiTaskRegistry.TaskFor(player, army);
                if (existingTask != null && (existingTask.Kind == AiTaskKind.DefendCitadel || existingTask.Retreating
                    || (existingTask.Kind == AiTaskKind.RaidWeakerArmy
                        && RaidWeakerArmyTask.IsReady(army, RaidWeakerArmyTask.RequiredStrengthAt(player, existingTask.TargetHex, ctx.Map)))))
                    continue;
                if (!AiTurnController.CanIssueMoveNow(root, player, army, ctx.Map, garrisonHex))
                    continue;

                var target = new AiScoutPlanner.ScoutTarget(garrisonHex, 0f, "citadel under siege — recalled to defend");
                AiDecision decision = AiDecision.Move(army, target, task: null, AiConfig.defencePreemptScore, AiTaskCategory.Defence);
                decision.PreemptedTask = existingTask;
                results.Add(decision);
            }
            return results;
        }

        // TryDefencePreemptCandidates' own siege-strip source — the strongest non-hero, non-Recce
        // member of whichever StillAssembling RaidWeakerArmy task's army is sitting on `citadelHex`
        // right now (excludes `excludeArmy`, the defending force itself, in case the two are ever
        // somehow the same army). Deliberately reads AiTaskRegistry directly rather than
        // AiResourcePool.AvailableArmies() — that pool already claimed this army for its own raid
        // task this step (see AiResourcePool's own class comment), and a real siege legitimately
        // outranks that claim. Hero protected same as FindStrengthenCandidate above; no
        // allowCriticallyWounded fallback here — unlike an ordinary recruit search this only ever
        // has the one still-forming army to draw from, so "nothing healthy" just means propose
        // nothing this call rather than settle for a wounded pull.
        private static UnitData FindSiegeRaidStripCandidate(PlayerSetupData player, HexCoord citadelHex, ArmyData excludeArmy, out ArmyData source)
        {
            source = null;
            UnitData best = null;
            float bestAttack = -1f;
            foreach (AiTask task in AiTaskRegistry.TasksFor(player))
            {
                if (task.Kind != AiTaskKind.RaidWeakerArmy || !task.StillAssembling || task.Army == null
                    || task.Army == excludeArmy || !task.Army.Hex.Equals(citadelHex))
                    continue;
                foreach (UnitData unit in task.Army.Members)
                {
                    if (unit.IsHero || AbilityParams.UnitHasAnyRecce(unit) || unit.HitPointsCurrent <= unit.HitPointsMax / 2
                        || unit.Attack <= bestAttack)
                        continue;
                    best = unit;
                    bestAttack = unit.Attack;
                    source = task.Army;
                }
            }
            return best;
        }

        // ---- Execution ----

        // Оборона · сборка состава с нуля, шаг 1 — see AiDecision.RequestDefendArmy's own comment.
        // `homeHex` — which of the player's own garrisoned hexes to spawn at (the citadel, or a
        // later-founded base — see TryStartDefenceCandidatesFor, the only caller that ever proposes
        // this decision, carried through AiDecision.TargetHex since this routine doesn't otherwise
        // receive the AiDecision itself).
        public static IEnumerator RequestDefendArmyRoutine(PlayerSetupData player, AiTurnContext ctx, HexCoord homeHex)
        {
            HexCoord hex = homeHex;
            yield return AiTurnController.PanTo(ctx, hex);

            PlayerRoot root = PlayerRootRegistry.FindFor(player);
            int ap0 = root != null ? root.ActionPoints : 0;
            int human0 = root != null ? root.GetResource(ResourceType.Human) : 0;
            int energy0 = root != null ? root.GetResource(ResourceType.Energy) : 0;
            int materials0 = root != null ? root.GetResource(ResourceType.Materials) : 0;
            int tech0 = root != null ? root.GetResource(ResourceType.Tech) : 0;
            // Feature 4A (2026-08-24) — same disposable-empty-shell reuse AiAggressionPlanner.
            // RequestRaidArmyRoutine's own comment describes, applied here too (see
            // GarrisonReorgTask.FindDisposableEmptyArmyAt's own comment).
            ArmyData reused = GarrisonReorgTask.FindDisposableEmptyArmyAt(player, hex);
            ArmyData army = reused ?? ArmyActions.CreateArmy(player, hex, ctx.StartingDeckCatalog?.GetCatalog(player.Faction), ctx.HexSelection);
            string delta = root != null ? AiTurnController.ResourceDeltaSuffix(root, ap0, human0, energy0, materials0, tech0) : null;
            AiDebugLog.Write(reused != null
                ? $"[AI] {player.Nickname}: Defence task — reuses empty army \"{reused.Name}\" to assemble a defense force instead of spending AP on a new one."
                : army != null
                    ? $"[AI] {player.Nickname}: Defence task — creates empty army \"{army.Name}\" to assemble a defense force.{delta}"
                    : $"[AI] {player.Nickname}: Defence task — not enough AP for a new army to assemble into.");

            yield return AiTurnController.WaitStep(ctx);
        }

        // Оборона · full-but-insufficient upgrade — see TryStrengthenCandidate's own comment. Same
        // direct ArmyActions.SwapMembers trade AiManagementPlanner.ConsolidateSwapRoutine already
        // uses for GarrisonReorgTask.SwapMove, just this category's own log/oscillation-guard copy.
        public static IEnumerator StrengthenDefenceForceRoutine(PlayerSetupData player, AiDecision decision, AiTurnContext ctx)
        {
            GarrisonReorgTask.SwapMove move = decision.SwapMove;
            yield return AiTurnController.PanTo(ctx, move.ArmyA.Hex);

            PlayerRoot root = PlayerRootRegistry.FindFor(player);
            int ap0 = root != null ? root.ActionPoints : 0;
            int human0 = root != null ? root.GetResource(ResourceType.Human) : 0;
            int energy0 = root != null ? root.GetResource(ResourceType.Energy) : 0;
            int materials0 = root != null ? root.GetResource(ResourceType.Materials) : 0;
            int tech0 = root != null ? root.GetResource(ResourceType.Tech) : 0;
            bool swapped = ArmyActions.SwapMembers(move.UnitA, move.ArmyA, move.UnitB, move.ArmyB, ctx.HexSelection, out string failReason);
            if (swapped)
            {
                string delta = root != null ? AiTurnController.ResourceDeltaSuffix(root, ap0, human0, energy0, materials0, tech0) : null;
                AiDebugLog.Write($"[AI] {player.Nickname}: {decision.Reason}.{delta}");
                ctx.RecordArmyVisit(move.UnitA, move.ArmyA, move.ArmyB);
                ctx.RecordArmyVisit(move.UnitB, move.ArmyB, move.ArmyA);
                // DefendCitadel's own stall clock — see AiTask.AssemblyProgressTurn's own comment.
                if (decision.Task != null)
                    decision.Task.AssemblyProgressTurn = ctx.TurnNumber;
            }
            else
            {
                AiDebugLog.Write($"[AI] {player.Nickname}: couldn't strengthen \"{move.ArmyA.Name}\" with {move.UnitB.Name} — {failReason}");
            }

            if (ctx.ShowArmyModal && ctx.ArmyViewerModal != null)
                ctx.ArmyViewerModal.ShowReadOnly(move.ArmyA);
            yield return AiTurnController.WaitStep(ctx);
        }
    }
}
