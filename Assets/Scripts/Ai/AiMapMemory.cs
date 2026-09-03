using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.Cards;
using Game.Combat;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai
{
    // Honest per-player memory of hex CONTENT — the piece VisionSystem itself explicitly does
    // NOT keep (see its own class comment: "Content has no memory either way and re-hides the
    // instant vision leaves"). Subscribes to VisionSystem.VisibilityChanged and, on every
    // recompute, snapshots whatever's on `player`'s own currently-visible hexes into four
    // permanent-until-corrected stores: which hexes are known to carry a resource bonus (and,
    // as of the Разведка Задача 2 pass, which ResourceType — reading the type off an already-
    // VISIBLE hex isn't the cheat AiEconomyPlanner.DominantResourceType's own caller guards
    // against elsewhere, since a real player would see the bonus icon the moment fog lifts too),
    // where an enemy/neutral army was last actually seen, which hexes carry a known active Hex
    // Event with a real guard (see KnownEventGuardDefenseAt), and (2026-08-24, section 3.2) which
    // hexes carry a known building and its own last-observed owner (KnownBuildings). Per the
    // project owner's own "Видимость с памятью" principle — stale info is never auto-expired,
    // only overwritten by a fresh observation of that SAME hex (see OnVisibilityChanged's own
    // `sightings.Remove` branch) — with two narrow exceptions: a PLAYER-owned army sighting still
    // expires after AiConfig.enemySightingMemoryTurns turns (a physical NEUTRAL army sighting does
    // NOT — see OnTurnStarted's own comment, 2026-08-24 fix), and an event's own guard being
    // consumed corrects immediately, but ONLY for a player CURRENTLY watching that hex (see
    // OnEventConsumed's own comment, same 2026-08-24 fix) — everyone else still only learns about
    // it by re-observing the hex later, same as any other correction here.
    //
    // Deliberately narrow in scope — only the slices AiGoalScorer/AiScoutPlanner/AiTurnController/
    // RaidWeakerArmyTask actually need honesty for right now (resource hexes + type, enemy
    // armies, event guards). Other players' own resource stockpiles stay the project's own
    // documented cheat exception (see AiGoalScorer's own IncomeBehindBonus) and never route
    // through here.
    //
    // Spec item 18 boundary (2026-08-28 P1): this store is the ONLY sanctioned source of a
    // concrete enemy POSITION for Оборона — AiDefencePlanner.FindActiveThreatSighting builds its
    // intercept target purely from KnownEnemySightingsNear here. The live-ArmyData Defence cheats
    // (AiDefencePlanner.CheatEstimateRaiderThreat / DynamicPatrolUrgencyScore) may sense that a
    // sector is threatened and size the patrol response, but they return only scalars and must
    // never turn a hidden army into a targetable hex; that always goes through this memory.
    public static class AiMapMemory
    {
        private class EnemySighting
        {
            // The physical army's own stable ArmyData.Id — this is now the dictionary key in
            // EnemySightings below (2026-08-23, project owner's own call), so a moved army's
            // sighting is updated IN PLACE under this same Id rather than left behind as an orphan
            // record under its old Hex while a second, independent entry accumulates at its new
            // Hex. Also carried here (not just as the dictionary key) so every read site can still
            // get back to it without a reverse lookup.
            public int ArmyId;
            // No longer the dictionary key (see ArmyId above) — now just this sighting's own last-
            // observed position, read the same way everywhere a caller needs "where was this
            // recorded".
            public HexCoord Hex;
            public PlayerSetupData Owner;
            public string Name;
            public int MemberCount;
            public float DefenseSum;
            public float AttackSum;
            public List<WorthIt.DefenderProfile> Defenders;
            // True if any member observed in this sighting carries an AntiAirRules-recognized AA
            // ability — read by AiAviationSupport.KnownAaExposure (AirStrike/AirRecon's own
            // per-ROUTE risk scan, see that method's own comment; the old global "seen anywhere on
            // the map" AirRecon gate that used to read this field directly was removed 2026-08-26,
            // project owner's own spec item 4). Computed once at observation time, same honesty rule
            // as every other field here — a hidden army's own AA is simply unknown, ordinary fog
            // risk, not something this flags.
            public bool HasAntiAir;
            // The global turn (see _currentTurn/OnTurnStarted below) this sighting was last
            // actually (re)observed — drives expiry in ExpireStaleSightings. Stamped, not left at
            // its default, even for a sighting recorded before the very first OnTurnStarted call
            // (initial army placement) — that default (0) is exactly right, since turn numbering
            // itself starts at 1 (see GameTurnController.TurnNumber).
            public int SeenTurn;
            // Best Recce radius / spot strength observed among this sighting's VISIBLE members
            // (2026-08-27, стелс §2) — lets VisitHexTask.ScoreCandidate apply its hidden-scout
            // detection-risk penalty only where this army could ACTUALLY roll a stealth
            // challenge (co-located, or adjacent with radius>0 AND spot>0 — mirrors
            // StealthSystem.SourcePool), instead of penalising every enemy army within
            // scoutFleeRadius regardless of whether it can see stealth at all. Same "видимость с
            // памятью" honesty rule as every other field here — a hidden member's own Recce is
            // simply unknown.
            public int RecceRadius;
            public int RecceSpotStrength;
        }

        public readonly struct KnownEnemySighting
        {
            // The sighted army's stable ArmyData.Id — carried through from the internal
            // EnemySighting (which is keyed by it). Lets a consumer track one physical army across
            // moves: Strategy V2's AiReconMemory keys its longer observation history by this so a
            // stale last-known position is matched to the SAME army, not to a hex the army left.
            public readonly int ArmyId;
            public readonly HexCoord Hex;
            public readonly PlayerSetupData Owner;
            public readonly string Name;
            public readonly int MemberCount;
            public readonly float DefenseSum;
            public readonly float AttackSum;
            public readonly IReadOnlyList<WorthIt.DefenderProfile> Defenders;
            public readonly bool HasAntiAir;
            // Best Recce radius / spot strength among the sighting's visible members (стелс §2) —
            // see EnemySighting.RecceRadius' own comment. 0/0 for an army with no Recce at all.
            public readonly int RecceRadius;
            public readonly int RecceSpotStrength;
            // The global turn this sighting was last actually (re)observed — carried through from
            // EnemySighting.SeenTurn (see its own comment). Lets a consumer age a last-known
            // position: Strategy V2's Recon surveillance planner scores a stale contact by
            // (currentTurn - SeenTurn). 0 for a sighting recorded before the first OnTurnStarted
            // (initial placement) — turn numbering starts at 1, so that default reads as "very old".
            public readonly int SeenTurn;

            public KnownEnemySighting(HexCoord hex, PlayerSetupData owner, string name, int memberCount, float defenseSum, float attackSum,
                IReadOnlyList<WorthIt.DefenderProfile> defenders, bool hasAntiAir = false, int recceRadius = 0, int recceSpotStrength = 0,
                int seenTurn = 0, int armyId = 0)
            {
                ArmyId = armyId;
                Hex = hex;
                Owner = owner;
                Name = name;
                MemberCount = memberCount;
                DefenseSum = defenseSum;
                AttackSum = attackSum;
                Defenders = defenders;
                HasAntiAir = hasAntiAir;
                RecceRadius = recceRadius;
                RecceSpotStrength = recceSpotStrength;
                SeenTurn = seenTurn;
            }

            // Could this remembered army actually roll a stealth-detection challenge against a
            // hidden unit standing on `hex`? Mirrors StealthSystem.SourcePool: co-located →
            // always (pool ≥ 1); exactly adjacent → only with radius>0 AND spot>0 (an r1s0
            // source reveals the hex but never detects stealth); 2+ hexes → never.
            public bool CanDetectStealthAt(HexCoord hex)
            {
                int dist = HexGridMath.Distance(Hex, hex);
                if (dist == 0)
                    return true;
                if (dist == 1)
                    return RecceRadius > 0 && RecceSpotStrength > 0;
                return false;
            }
        }

        // Aggregate Attack/Defense sums plus the per-unit DefenderProfile list WorthIt.CanDamageAll
        // needs for its coverage check (see that method's own comment) — shared shape for both a
        // Hex Event's card-stat guard (KnownEventGuards below) and a physical army sighting
        // (EnemySighting above reuses the same three numbers/list, just not through this struct
        // directly since it's already its own class). `Name` — the sighted army's own ArmyData.Name,
        // or a Hex Event guard's own HexEventRegistry.Entry.GuardArmyName; never a per-unit read (see
        // DefenderProfile's own comment — this file only ever remembers the OTHER side as an
        // aggregate), added 2026-08-22 purely for AiAggressionPlanner's own "not enough force" log
        // line (project owner's own report) to name the target instead of just its numbers.
        public readonly struct GuardStrength
        {
            public readonly float Defense;
            public readonly float Attack;
            public readonly IReadOnlyList<WorthIt.DefenderProfile> Defenders;
            public readonly string Name;

            public GuardStrength(float defense, float attack, IReadOnlyList<WorthIt.DefenderProfile> defenders, string name = null)
            {
                Defense = defense;
                Attack = attack;
                Defenders = defenders;
                Name = name;
            }
        }

        // HexCoord -> the dominant ResourceType last observed there (see AiEconomyPlanner.
        // DominantResourceType) — a hex only ever enters this dictionary once its bonus has
        // actually been seen, same honesty rule as everything else here.
        private static readonly Dictionary<PlayerSetupData, Dictionary<HexCoord, ResourceType>> KnownResourceHexes =
            new Dictionary<PlayerSetupData, Dictionary<HexCoord, ResourceType>>();
        // Keyed by ArmyData.Id, NOT HexCoord (changed 2026-08-23, project owner's own call — see
        // EnemySighting.ArmyId's own comment): a hex-keyed store left a moved army's old-hex
        // sighting orphaned forever (until its own turns-based expiry) alongside a second, fresher
        // entry at its new hex, and AiDefencePlanner's Active branch had no reason to prefer the
        // fresher one, so it could march the defender to a stale last-seen position even while the
        // same army sat plainly visible somewhere else. Keying by the army's own stable identity
        // means the SAME dictionary slot gets overwritten on every re-sighting, move included, so
        // there's only ever one live record per physical army.
        private static readonly Dictionary<PlayerSetupData, Dictionary<int, EnemySighting>> EnemySightings =
            new Dictionary<PlayerSetupData, Dictionary<int, EnemySighting>>();
        // HexCoord -> guard's own card-stat strength, for every Hex Event this player has ever
        // SEEN while it still had an unconsumed guard — Агрессия's own "known event with guard"
        // half of RaidWeakerArmyTask's target pool (see that class's own FindTarget). Same
        // "видимость с памятью" honesty rule as everything else here: a hex only enters this once
        // actually visible AND HexEventRegistry.HasActiveEvent(hex) AND it carries a real guard
        // (an unguarded event is never a combat target, nothing to remember here for it).
        private static readonly Dictionary<PlayerSetupData, Dictionary<HexCoord, GuardStrength>> KnownEventGuards =
            new Dictionary<PlayerSetupData, Dictionary<HexCoord, GuardStrength>>();

        // Per-hex last-observed building snapshot (2026-08-24, "память тумана войны не
        // соответствует правилам 3.1–3.2" fix, section 3.2 — project owner's own report) — RaidWeaker
        // ArmyTask used to read BuildingRegistry.FindAt/AllBuildings LIVE for every OTHER player's
        // buildings, so an enemy building's true current owner (captured/destroyed/rebuilt by
        // anyone, anywhere, any time) was always instantly known regardless of whether this actor
        // had ever looked at that hex again since. Same "видимость с памятью" rule as
        // KnownResourceHexes/EnemySightings above — a hex only enters (or gets corrected in) this
        // dictionary once actually VISIBLE, and stays exactly as last observed forever after,
        // never auto-expired.
        private class BuildingSighting
        {
            public HexCoord Hex;
            public PlayerSetupData Owner;
            public bool IsStartingCitadel;
            // Union of every placed Facility's own Abilities, as of this sighting (e.g.
            // UnitAbilities.CollectHuman/Energy/Materials/Tech) — ResourcesScrapTask.
            // HasExtractionFacility's own memory-based read (2026-08-24 fix). A facility's own
            // ability set is fixed once placed (never changes independent of a real, observable
            // world event — an upgrade still keeps the same ability, just a higher UpgradeLevel
            // this snapshot doesn't need), so this stays honestly "as last observed" the same way
            // Owner/IsStartingCitadel already do.
            public HashSet<string> FacilityAbilities;
        }

        private static readonly Dictionary<PlayerSetupData, Dictionary<HexCoord, BuildingSighting>> KnownBuildings =
            new Dictionary<PlayerSetupData, Dictionary<HexCoord, BuildingSighting>>();

        public readonly struct KnownBuilding
        {
            public readonly HexCoord Hex;
            public readonly PlayerSetupData Owner;
            public readonly bool IsStartingCitadel;
            public readonly IReadOnlyCollection<string> FacilityAbilities;

            public KnownBuilding(HexCoord hex, PlayerSetupData owner, bool isStartingCitadel, IReadOnlyCollection<string> facilityAbilities)
            {
                Hex = hex;
                Owner = owner;
                IsStartingCitadel = isStartingCitadel;
                FacilityAbilities = facilityAbilities;
            }

            public bool HasFacilityWithAbility(string ability) => FacilityAbilities != null && FacilityAbilities.Contains(ability);
        }

        // A recorded scout retreat (VisitHexTask.TryFlee) — deliberately OUTLIVES the
        // EnemySighting that triggered it (enemySightingMemoryTurns is only 2 turns; a scout that
        // retreats home and stops observing the threat lets that sighting go stale well before the
        // enemy army has actually moved on, so relying on sighting memory alone had the scout walk
        // straight back into the same still-there army every few turns — project owner's own
        // 2026-08-24 report). One zone per triggering sighting hex, not per scout — any scout
        // (this player's own) approaching the same area is turned away, not just the one that first
        // found it.
        private class ScoutDangerZone
        {
            public HexCoord Center;
            public int Radius;
            public int AvoidUntilTurn;
        }

        private static readonly Dictionary<PlayerSetupData, List<ScoutDangerZone>> ScoutDangerZones =
            new Dictionary<PlayerSetupData, List<ScoutDangerZone>>();

        // Разведка · Авиация (AiTaskKind.AirRecon) — hex -> the global turn an AirRecon sortie was
        // last sent toward it (AiAviationSupport.ContinueSortie stamps this every outbound step).
        // Purpose-built for AirReconTask.FindReconHex's own anti-loop cooldown (project owner's own
        // spec — "AirRecon не должен бесконечно летать в один stale-гекс"): a hex flown to recently
        // is not offered as a recon target again for AiConfig.airReconTargetCooldownTurns turns
        // unless a known enemy army/building still sits on it. One entry per hex, re-stamped on a
        // repeat sortie. Never auto-expired here — FindReconHex compares against the current turn
        // itself (see WasAirReconnedWithin) and simply stops caring once the window has passed.
        private static readonly Dictionary<PlayerSetupData, Dictionary<HexCoord, int>> AirReconTargets =
            new Dictionary<PlayerSetupData, Dictionary<HexCoord, int>>();

        // Агрессия · from-scratch raid — hex -> the global turn a fresh raid assembly against it
        // was last rejected as non-viable (RaidWeakerArmyTask.EvaluateAssemblablePlan: no hero
        // obtainable, composition can't cover every defender, or the strongest force we could
        // realistically assemble still wins below raidMinimumWinChance). Purpose-built for
        // AiAggressionPlanner.TryRaidAssembleCandidates' own pre-allocation gate — within
        // AiConfig.raidPlanRejectCooldownTurns turns the hex is not re-projected (or re-logged) as
        // a new-raid target, so the AI doesn't burn a Decide step every turn re-deriving the same
        // "0% win chance" verdict it already reached. One entry per hex, re-stamped on a repeat
        // rejection. Never auto-expired here — WasRaidPlanRejectedWithin compares against the
        // current turn. Existing raid tasks and a ready idle army are never gated by this.
        private static readonly Dictionary<PlayerSetupData, Dictionary<HexCoord, int>> RaidPlanRejected =
            new Dictionary<PlayerSetupData, Dictionary<HexCoord, int>>();

        private static bool _subscribed;
        // Global game turn (GameTurnController.TurnNumber, same one AiTurnContext.TurnNumber
        // snapshots) as of the most recent OnTurnStarted call — used only to stamp/expire
        // EnemySighting.SeenTurn (see that field's own comment). Not a live reference, just a
        // plain int updated once per AI player's own turn, same shape as AiTurnContext.TurnNumber
        // itself.
        private static int _currentTurn;

        // Idempotent — safe to call every new-game setup without risking a doubled subscription
        // (see CitadelSetupController, which calls this alongside VisionSystem.Clear/Configure).
        public static void EnsureSubscribed()
        {
            if (_subscribed)
                return;
            VisionSystem.VisibilityChanged += OnVisibilityChanged;
            VisionSystem.VisibleContentChanged += OnVisibleContentChanged;
            // A hidden unit entering/leaving stealth, or a personal detection lapsing, changes
            // what each player can honestly see without any vision-radius change — re-snapshot
            // so a now-hidden enemy drops out of current sightings and a freshly-revealed one
            // enters (spec §8: no stale-memory targeting of a unit that's hidden again now).
            StealthSystem.StealthChanged += OnStealthChanged;
            // The event's own guard just got beaten for real — only a player CURRENTLY watching
            // the hex gets its memory corrected right here; everyone else only learns about it by
            // actually re-observing the hex later (see OnEventConsumed's own comment, 2026-08-24
            // fix).
            HexEventRegistry.EventConsumed += OnEventConsumed;
            _subscribed = true;
        }

        // Content changed under an already-visible hex (arrival, departure, capture). This is
        // intentionally separate from a vision-radius change; the existing snapshot routine is
        // kept as the single source of truth, but no longer runs on every animation step whose
        // reveal area stayed identical.
        private static void OnVisibleContentChanged(PlayerSetupData player, HexCoord hex)
        {
            OnVisibilityChanged(player);
        }

        private static void OnStealthChanged()
        {
            foreach (PlayerSetupData player in Game.Core.GameSession.Players ?? new List<PlayerSetupData>())
                OnVisibilityChanged(player);
        }

        public static void Clear()
        {
            KnownResourceHexes.Clear();
            EnemySightings.Clear();
            KnownEventGuards.Clear();
            KnownBuildings.Clear();
            ScoutDangerZones.Clear();
            AirReconTargets.Clear();
            RaidPlanRejected.Clear();
            _currentTurn = 0;
        }

        // Called once, right at the top of AiTurnController.RunTurn, before that turn's own
        // Decide loop ever reads memory — stamps _currentTurn for every EnemySighting recorded
        // from this point on (OnVisibilityChanged below) AND expires `actor`'s own enemy-army
        // sightings last (re)observed more than AiConfig.enemySightingMemoryTurns turns ago (see
        // that field's own comment — resource hexes/event guards are NOT touched here, both stay
        // permanent-until-corrected). Scoped to `actor` alone rather than sweeping every player's
        // memory at once — this only ever needs to be accurate for the player whose own Decide
        // loop is about to read it; every other player's memory gets swept the same way at the
        // top of their own next RunTurn instead.
        public static void OnTurnStarted(PlayerSetupData actor, int turnNumber)
        {
            _currentTurn = turnNumber;
            if (actor == null)
                return;

            if (EnemySightings.TryGetValue(actor, out Dictionary<int, EnemySighting> sightings))
            {
                List<int> stale = null;
                foreach (KeyValuePair<int, EnemySighting> kv in sightings)
                {
                    // Physical NEUTRAL armies never expire by elapsed turns (2026-08-24, "память
                    // тумана войны не соответствует правилам 3.1–3.2" fix, section 3.1 — project
                    // owner's own report): only a player-owned sighting is time-bounded here.
                    // A neutral sighting instead stays exactly as last observed until this same
                    // hex is actually re-observed (OnVisibilityChanged's own stale-removal/
                    // overwrite logic below already handles that correction honestly — a hex seen
                    // empty now clears it, a different army seen there overwrites it), matching
                    // "узнаёт об уничтожении только после повторного наблюдения" rather than
                    // silently forgetting a still-real neutral garrison just because nobody's
                    // looked at it in enemySightingMemoryTurns turns.
                    if (kv.Value.Owner != null && kv.Value.Owner.IsNeutral)
                        continue;
                    if (turnNumber - kv.Value.SeenTurn > AiConfig.enemySightingMemoryTurns)
                        (stale ?? (stale = new List<int>())).Add(kv.Key);
                }
                if (stale != null)
                    foreach (int armyId in stale)
                    {
                        AiDebugLog.Write($"[AI] {actor.Nickname}: memory — army sighting \"{sightings[armyId].Name}\" "
                            + $"at ({sightings[armyId].Hex.Q},{sightings[armyId].Hex.R}) expired after "
                            + $"{AiConfig.enemySightingMemoryTurns} turns.");
                        sightings.Remove(armyId);
                    }
            }

            if (ScoutDangerZones.TryGetValue(actor, out List<ScoutDangerZone> zones))
                zones.RemoveAll(z => turnNumber > z.AvoidUntilTurn);
        }

        // Called by VisitHexTask.TryFlee the moment a retreat actually triggers — `center` is the
        // triggering sighting's own hex (not the fleeing scout's), so the zone sits on the actual
        // threat regardless of which direction a scout approached it from. Repeated calls for a
        // center already inside an existing zone just extend that zone's own AvoidUntilTurn rather
        // than piling up duplicate overlapping entries.
        public static void MarkScoutDanger(PlayerSetupData actor, HexCoord center, int radius, int avoidUntilTurn)
        {
            if (actor == null)
                return;
            if (!ScoutDangerZones.TryGetValue(actor, out List<ScoutDangerZone> zones))
            {
                zones = new List<ScoutDangerZone>();
                ScoutDangerZones[actor] = zones;
            }

            ScoutDangerZone existing = zones.Find(z => z.Center.Equals(center));
            if (existing != null)
            {
                existing.Radius = radius;
                if (avoidUntilTurn > existing.AvoidUntilTurn)
                    existing.AvoidUntilTurn = avoidUntilTurn;
                return;
            }
            zones.Add(new ScoutDangerZone { Center = center, Radius = radius, AvoidUntilTurn = avoidUntilTurn });
        }

        // VisitHexTask's own FindTarget/FindNextSafeStep read this to keep a Recce scout out of a
        // recently-fled-from area even after the EnemySighting that first triggered the retreat has
        // gone stale (see ScoutDangerZones' own comment) — cooldown-bounded, not permanent, so the
        // sector opens back up once AvoidUntilTurn passes (OnTurnStarted purges it above).
        public static bool IsScoutDangerous(PlayerSetupData actor, HexCoord hex)
        {
            return ScoutDangerZones.TryGetValue(actor, out List<ScoutDangerZone> zones)
                && zones.Any(z => HexGridMath.Distance(z.Center, hex) <= z.Radius);
        }

        // Stamps `hex` as the target an AirRecon sortie is currently flying toward, at
        // `turnNumber` — see AirReconTargets' own comment. Called every outbound step from
        // AiAviationSupport.ContinueSortie so the cooldown counts from the sortie's last real
        // progress toward the hex, not merely its launch turn.
        public static void RecordAirReconTarget(PlayerSetupData actor, HexCoord hex, int turnNumber)
        {
            if (actor == null)
                return;
            if (!AirReconTargets.TryGetValue(actor, out Dictionary<HexCoord, int> targets))
                AirReconTargets[actor] = targets = new Dictionary<HexCoord, int>();
            targets[hex] = turnNumber;
        }

        // True if an AirRecon sortie was last sent toward `hex` fewer than `cooldownTurns` turns
        // ago (relative to `currentTurn`). AirReconTask.FindReconHex uses this to stop re-proposing
        // the same stale hex over and over — the caller still applies the "unless a known enemy
        // army/building is there" exception itself.
        public static bool WasAirReconnedWithin(PlayerSetupData actor, HexCoord hex, int currentTurn, int cooldownTurns)
        {
            return AirReconTargets.TryGetValue(actor, out Dictionary<HexCoord, int> targets)
                && targets.TryGetValue(hex, out int turn)
                && currentTurn - turn < cooldownTurns;
        }

        // Stamps `hex` as a from-scratch raid target that failed AiAggressionPlanner's own
        // pre-allocation viability gate this turn — see RaidPlanRejected's own comment.
        public static void MarkRaidPlanRejected(PlayerSetupData actor, HexCoord hex, int turnNumber)
        {
            if (actor == null)
                return;
            if (!RaidPlanRejected.TryGetValue(actor, out Dictionary<HexCoord, int> hexes))
                RaidPlanRejected[actor] = hexes = new Dictionary<HexCoord, int>();
            hexes[hex] = turnNumber;
        }

        // True if a fresh raid assembly against `hex` was rejected as non-viable fewer than
        // `cooldownTurns` turns ago (relative to `currentTurn`). TryRaidAssembleCandidates checks
        // this before re-projecting the target, so it doesn't re-run the same doomed math (and
        // re-log it) every Decide step within the cooldown window.
        public static bool WasRaidPlanRejectedWithin(PlayerSetupData actor, HexCoord hex, int currentTurn, int cooldownTurns)
        {
            return RaidPlanRejected.TryGetValue(actor, out Dictionary<HexCoord, int> hexes)
                && hexes.TryGetValue(hex, out int turn)
                && currentTurn - turn < cooldownTurns;
        }

        private static void OnVisibilityChanged(PlayerSetupData player)
        {
            if (player == null)
                return;

            if (!KnownResourceHexes.TryGetValue(player, out Dictionary<HexCoord, ResourceType> resources))
            {
                resources = new Dictionary<HexCoord, ResourceType>();
                KnownResourceHexes[player] = resources;
            }
            if (!EnemySightings.TryGetValue(player, out Dictionary<int, EnemySighting> sightings))
            {
                sightings = new Dictionary<int, EnemySighting>();
                EnemySightings[player] = sightings;
            }
            if (!KnownEventGuards.TryGetValue(player, out Dictionary<HexCoord, GuardStrength> eventGuards))
            {
                eventGuards = new Dictionary<HexCoord, GuardStrength>();
                KnownEventGuards[player] = eventGuards;
            }
            if (!KnownBuildings.TryGetValue(player, out Dictionary<HexCoord, BuildingSighting> buildings))
            {
                buildings = new Dictionary<HexCoord, BuildingSighting>();
                KnownBuildings[player] = buildings;
            }

            foreach (HexCoord hex in VisionSystem.VisibleHexesFor(player))
            {
                ResourceType? dominant = AiEconomyPlanner.DominantResourceType(hex);
                if (dominant.HasValue)
                    resources[hex] = dominant.Value;

                // IsEngageable(a, player) — a hidden-from-`player` enemy (an army every member
                // of which is in stealth and undetected) is not a current sighting at all
                // (spec §8), and a mixed army is remembered by its VISIBLE members only.
                // HexEventRegistry.IsEventGuardArmy — the ArmyData a ground army's Explore spawns
                // for a Hex Event guard is deliberately NOT a physical sighting: it's transient
                // (torn down the moment its fight ends), and the event's guard is already tracked
                // as a card-stat entry below (KnownEventGuards). Recording it here would leak it
                // into AllKnownNeutralSightings — where AirStrikeTask would pick it up as an
                // air-strike target and keep flying sorties at it even after it despawned (memory
                // only self-corrects on re-observation), even though aviation never interacts with
                // a Hex Event at all (project owner's own rule).
                ArmyData enemy = ArmyRegistry.AllAt(hex).FirstOrDefault(a => a.Owner != player
                    && BattleInitiator.IsEngageable(a, player) && !HexEventRegistry.IsEventGuardArmy(hex, a));
                if (enemy != null)
                {
                    List<UnitData> nonHero = enemy.Members.Where(m => !m.IsHero && !StealthSystem.IsHiddenFrom(m, player)).ToList();
                    int visibleMemberCount = enemy.Members.Count(m => !StealthSystem.IsHiddenFrom(m, player));
                    // Keyed by the army's own stable Id (see EnemySightings' own comment) — if this
                    // same army was last recorded at a DIFFERENT hex, this overwrites that record in
                    // place instead of leaving it behind as an orphan under its old Hex.
                    if (sightings.TryGetValue(enemy.Id, out EnemySighting previous) && !previous.Hex.Equals(hex))
                        AiDebugLog.Write($"[AI] {player.Nickname}: memory — army \"{enemy.Name}\" id={enemy.Id} relocated "
                            + $"({previous.Hex.Q},{previous.Hex.R}) → ({hex.Q},{hex.R}).");
                    else if (enemy.Owner != null && enemy.Owner.IsNeutral && !sightings.ContainsKey(enemy.Id))
                        AiDebugLog.Write($"[AI] {player.Nickname}: memory — neutral \"{enemy.Name}\" remembered at ({hex.Q},{hex.R}).");
                    sightings[enemy.Id] = new EnemySighting
                    {
                        ArmyId = enemy.Id,
                        Hex = hex,
                        Owner = enemy.Owner,
                        Name = enemy.Name,
                        MemberCount = visibleMemberCount,
                        DefenseSum = nonHero.Sum(m => m.Defense),
                        AttackSum = nonHero.Sum(m => m.Attack),
                        // Full per-unit snapshot (2026-08-22, project owner's own call: "если мы
                        // когда-то видели армию значит мы видели её состав и знаем всё о ней кроме
                        // текущего состояния") — Attack/Initiative alongside Defense/CeramicArmor/
                        // TypeTags now, so WorthIt's own full-roster Monte Carlo (WinChance's
                        // DefenderProfile-list overload) can actually play this army out round by
                        // round instead of only reading its aggregate sums. HitPointsCurrent
                        // (2026-08-26, air-strike-memory fix — was HitPointsMax): this loop only
                        // ever runs over a hex `player` can see RIGHT NOW (see the `foreach
                        // (HexCoord hex in VisionSystem.VisibleHexesFor(player))` above), so the
                        // damage on it is exactly as real an observed fact as its composition —
                        // freezing it at last-observed value (never auto-healed, only corrected by
                        // a later re-observation) matches the same "видимость с памятью" honesty
                        // rule every other field here already follows, rather than singling HP out
                        // for an "assume it healed" exception. There is no in-field HP regen in
                        // this game (only UnitRepair, base-side) for that assumption to have been
                        // protecting against.
                        Defenders = nonHero.Select(m => new WorthIt.DefenderProfile(m.Defense, m.HasAbility(UnitAbilities.CeramicArmor),
                            m.TypeTags.ToList(), m.Attack, m.HitPointsCurrent, m.Initiative)).ToList(),
                        // Scanned over the FULL roster (not just nonHero above) — nothing rules out
                        // a hero carrying an AA ability, and this flag only ever feeds a
                        // conservative "don't fly recon here" gate, never a combat estimate, so
                        // there's no reason to narrow it the way the DefenderProfile list above does.
                        HasAntiAir = enemy.Members.Any(m => !StealthSystem.IsHiddenFrom(m, player) && AntiAirRules.TryGetRadius(m, out _)),
                        SeenTurn = _currentTurn,
                        RecceRadius = enemy.Members.Where(m => !StealthSystem.IsHiddenFrom(m, player))
                            .Select(m => AbilityParams.GetBestRecceRadius(m)).DefaultIfEmpty(0).Max(),
                        RecceSpotStrength = enemy.Members.Where(m => !StealthSystem.IsHiddenFrom(m, player))
                            .Select(m => AbilityParams.GetBestRecceSpotStrength(m)).DefaultIfEmpty(0).Max(),
                    };
                }
                else
                {
                    // Freshly observed and empty now — corrects any stale sighting rather than
                    // leaving it to linger (see the class's own "исправляет только новое
                    // наблюдение" comment). Covers the army-actually-died case; an army that merely
                    // MOVED away already got its own sightings[] slot overwritten in place above
                    // once its new hex was processed (same loop, order-independent — see
                    // EnemySightings' own comment), so there's nothing left here to find in that
                    // case. Scans by Hex rather than a key lookup since the dictionary is keyed by
                    // ArmyId now, not HexCoord.
                    int? staleArmyId = null;
                    foreach (KeyValuePair<int, EnemySighting> kv in sightings)
                    {
                        if (kv.Value.Hex.Equals(hex))
                        {
                            staleArmyId = kv.Key;
                            break;
                        }
                    }
                    if (staleArmyId.HasValue)
                    {
                        EnemySighting stale = sightings[staleArmyId.Value];
                        if (stale.Owner != null && stale.Owner.IsNeutral)
                            AiDebugLog.Write($"[AI] {player.Nickname}: memory — neutral \"{stale.Name}\" at "
                                + $"({hex.Q},{hex.R}) corrected (gone on re-observation).");
                        sightings.Remove(staleArmyId.Value);
                    }
                }

                HexEventRegistry.Entry eventEntry = HexEventRegistry.HasActiveEvent(hex) ? HexEventRegistry.FindAt(hex) : null;
                if (eventEntry != null && eventEntry.ResolvedGuardMembers.Count > 0)
                {
                    // Same flat card-stat sum WorthIt/AiEventPlanner.ShouldExplore already use —
                    // the guard is never a live ArmyData until Explore is chosen (see
                    // HexEventRegistry.Entry's own comment), so card stats are all there is to
                    // read.
                    var guardMembers = eventEntry.ResolvedGuardMembers
                        .Where(g => g.card != null && g.card.cardType != CardType.Hero).ToList();
                    float defense = guardMembers.Sum(g => g.card.defenseRating * g.count);
                    float attack = guardMembers.Sum(g => g.card.attack * g.count);
                    // One DefenderProfile per PHYSICAL copy (repeated by g.count), not one per
                    // card type — a card's own stats are already fully known deterministically
                    // (it's a static card-guard, not fog-of-war memory of a moving army), but
                    // WorthIt's full-roster Monte Carlo needs one real combatant/HP-pool per copy
                    // to actually play a "3 Grunts" guard out as three separate units, the same way
                    // a real fight against them would. CanDamageAll's own coverage check doesn't
                    // care about the duplication (it only asks "is there a counter for this profile
                    // anywhere", repeats are harmless there).
                    var defenders = guardMembers.SelectMany(g => Enumerable.Repeat(new WorthIt.DefenderProfile(g.card.defenseRating,
                        g.card.grantedAbilities != null && g.card.grantedAbilities.Contains(UnitAbilities.CeramicArmor),
                        g.card.unitTypeTags, g.card.attack, g.card.hitPoints, g.card.initiative), g.count)).ToList();
                    eventGuards[hex] = new GuardStrength(defense, attack, defenders, eventEntry.GuardArmyName);
                }
                else
                {
                    eventGuards.Remove(hex);
                }

                // Building snapshot (2026-08-24, section 3.2 fix — see KnownBuildings' own class
                // comment) — a real, direct read of BuildingRegistry, but only ever for a hex this
                // loop already confirmed is actually VISIBLE this call, exactly the same
                // "honest right now, stale afterward until re-observed" shape every other store in
                // this method already follows for resource hexes/army sightings/event guards.
                BuildingData building = BuildingRegistry.FindAt(hex);
                if (building != null)
                {
                    bool wasKnown = buildings.TryGetValue(hex, out BuildingSighting previousBuilding);
                    if (!wasKnown)
                        AiDebugLog.Write($"[AI] {player.Nickname}: memory — building \"{building.Name}\" "
                            + $"(owner={(building.Owner != null ? building.Owner.Nickname : "none")}) remembered at ({hex.Q},{hex.R}).");
                    else if (previousBuilding.Owner != building.Owner)
                        AiDebugLog.Write($"[AI] {player.Nickname}: memory — building at ({hex.Q},{hex.R}) corrected, owner "
                            + $"{(previousBuilding.Owner != null ? previousBuilding.Owner.Nickname : "none")} → "
                            + $"{(building.Owner != null ? building.Owner.Nickname : "none")}.");
                    var facilityAbilities = new HashSet<string>();
                    foreach (FacilityData facility in building.FacilitySlots)
                        if (facility != null)
                            facilityAbilities.UnionWith(facility.Abilities);
                    buildings[hex] = new BuildingSighting
                    {
                        Hex = hex, Owner = building.Owner, IsStartingCitadel = building.IsStartingCitadel, FacilityAbilities = facilityAbilities,
                    };
                }
                else
                {
                    if (buildings.ContainsKey(hex))
                        AiDebugLog.Write($"[AI] {player.Nickname}: memory — building at ({hex.Q},{hex.R}) corrected (gone on re-observation).");
                    buildings.Remove(hex);
                }
            }
        }

        // The event's own guard just got beaten for real (reward claimed) — a genuine world-state
        // change, but (2026-08-24 fix, "память тумана войны не соответствует правилам 3.1–3.2",
        // project owner's own report) no longer force-corrected into EVERY player's memory the
        // instant it happens regardless of whether they can currently see the hex — a player not
        // watching right now must only learn about it by actually re-observing the hex later, same
        // "видимость с памятью" rule as everything else here. Only a player CURRENTLY seeing the
        // hex gets an explicit nudge here at all: OnVisibilityChanged's own eventGuards[hex]
        // correction only fires on a vision RECOMPUTE, which "the guard I'm already looking at just
        // got beaten" doesn't by itself trigger, so without this a watching player would keep
        // believing a guard is still there until their vision happens to recompute for some other
        // reason.
        private static void OnEventConsumed(HexCoord hex)
        {
            foreach (KeyValuePair<PlayerSetupData, Dictionary<HexCoord, GuardStrength>> kv in KnownEventGuards)
                if (VisionSystem.IsVisible(kv.Key, hex))
                    kv.Value.Remove(hex);
        }

        // A hex's resource bonus counts as "known" the moment it's ever been merely VISIBLE, not
        // necessarily visited — matches how AiScoutPlanner's own isUndiscoveredResource bonus
        // already treats discovery (fogged vs visible, not visited vs unvisited).
        public static bool IsResourceHexKnown(PlayerSetupData actor, HexCoord hex)
        {
            return KnownResourceHexes.TryGetValue(actor, out Dictionary<HexCoord, ResourceType> set) && set.ContainsKey(hex);
        }

        // Every known resource hex and its last-observed dominant type — the whole-map read
        // behind IsResourceHexKnown, for the Strategy V2 WorldAnalysis scan (Game.Ai.V2), which
        // needs the set itself (opportunity map + per-resource economy weighting), not just a
        // per-hex membership test. Same honesty rule as everything else here — only ever hexes
        // this player has actually seen the bonus on.
        public static IEnumerable<KeyValuePair<HexCoord, ResourceType>> AllKnownResourceHexes(PlayerSetupData actor)
        {
            return KnownResourceHexes.TryGetValue(actor, out Dictionary<HexCoord, ResourceType> set)
                ? (IEnumerable<KeyValuePair<HexCoord, ResourceType>>)set
                : System.Array.Empty<KeyValuePair<HexCoord, ResourceType>>();
        }

        public static bool HasKnownEnemyWithin(PlayerSetupData actor, HexCoord center, int radius)
        {
            return EnemySightings.TryGetValue(actor, out Dictionary<int, EnemySighting> sightings)
                && sightings.Values.Any(s => HexGridMath.Distance(center, s.Hex) <= radius);
        }

        // Same read as HasKnownEnemyWithin, narrowed to sightings whose owner is neutral —
        // Экономика · Задача 1's own "don't build near a neutral garrison" check
        // (AiConfig.neutralBuildTriggerRadius), which cares about neutrals specifically rather than
        // any known hostile army the way HasKnownEnemyWithin itself does.
        public static bool HasKnownNeutralWithin(PlayerSetupData actor, HexCoord center, int radius)
        {
            return EnemySightings.TryGetValue(actor, out Dictionary<int, EnemySighting> sightings)
                && sightings.Values.Any(s => s.Owner != null && s.Owner.IsNeutral
                    && HexGridMath.Distance(center, s.Hex) <= radius);
        }

        // Every known neutral-army hex on the whole map, no radius — RaidWeakerArmyTask's own
        // target pool isn't wavefront/radius-bounded like Разведка's (see that class's own class
        // comment), it just scores every known target by raw distance from the citadel.
        public static IEnumerable<KnownEnemySighting> AllKnownNeutralSightings(PlayerSetupData actor)
        {
            if (!EnemySightings.TryGetValue(actor, out Dictionary<int, EnemySighting> sightings))
                yield break;
            foreach (EnemySighting sighting in sightings.Values)
                if (sighting.Owner != null && sighting.Owner.IsNeutral)
                    yield return new KnownEnemySighting(sighting.Hex, sighting.Owner, sighting.Name, sighting.MemberCount, sighting.DefenseSum,
                        sighting.AttackSum, sighting.Defenders, sighting.HasAntiAir, sighting.RecceRadius, sighting.RecceSpotStrength,
                        sighting.SeenTurn, sighting.ArmyId);
        }

        // Every known non-neutral-army hex on the whole map, no radius — AiDefencePlanner's own
        // Patrol target picker (FindPatrolTarget/RandomKnownEnemyHex), same "знаем направление, не
        // содержимое хекса" cheat the project owner explicitly sanctioned for Patrol's first target
        // of a fresh cycle. Mirrors AllKnownNeutralSightings' own shape, just the opposite owner
        // filter.
        public static IEnumerable<KnownEnemySighting> AllKnownEnemySightings(PlayerSetupData actor)
        {
            if (!EnemySightings.TryGetValue(actor, out Dictionary<int, EnemySighting> sightings))
                yield break;
            foreach (EnemySighting sighting in sightings.Values)
                if (sighting.Owner != null && !sighting.Owner.IsNeutral)
                    yield return new KnownEnemySighting(sighting.Hex, sighting.Owner, sighting.Name, sighting.MemberCount, sighting.DefenseSum,
                        sighting.AttackSum, sighting.Defenders, sighting.HasAntiAir, sighting.RecceRadius, sighting.RecceSpotStrength,
                        sighting.SeenTurn, sighting.ArmyId);
        }

        // HasObservedEnemyAntiAir (AirRecon's own former global "any AA seen anywhere" gate)
        // removed 2026-08-26 (project owner's own spec item 4) — see EnemySighting.HasAntiAir's own
        // comment for what replaced it.

        public static IEnumerable<KnownEnemySighting> KnownEnemySightingsNear(PlayerSetupData actor,
            IReadOnlyList<HexCoord> ownHexes, int radius)
        {
            if (!EnemySightings.TryGetValue(actor, out Dictionary<int, EnemySighting> sightings))
                yield break;

            foreach (EnemySighting sighting in sightings.Values)
                if (ownHexes.Any(own => HexGridMath.Distance(own, sighting.Hex) <= radius))
                    yield return new KnownEnemySighting(sighting.Hex, sighting.Owner, sighting.Name, sighting.MemberCount, sighting.DefenseSum,
                        sighting.AttackSum, sighting.Defenders, sighting.HasAntiAir, sighting.RecceRadius, sighting.RecceSpotStrength,
                        sighting.SeenTurn, sighting.ArmyId);
        }

        // One specific hex's own last-known sighting, if any — RaidWeakerArmyTask's own
        // RequiredStrengthAt/IsStillValidTarget need exactly this hex, not a radius scan. Scans by
        // sighting.Hex rather than a dictionary key lookup since EnemySightings is keyed by ArmyId
        // now, not HexCoord (see that dictionary's own comment) — at most one live sighting can
        // ever have a given Hex at a time (each write overwrites its army's own single slot), so
        // the first match is the only match.
        public static KnownEnemySighting? KnownEnemySightingAt(PlayerSetupData actor, HexCoord hex)
        {
            if (!EnemySightings.TryGetValue(actor, out Dictionary<int, EnemySighting> sightings))
                return null;
            foreach (EnemySighting sighting in sightings.Values)
                if (sighting.Hex.Equals(hex))
                    return new KnownEnemySighting(hex, sighting.Owner, sighting.Name, sighting.MemberCount, sighting.DefenseSum,
                        sighting.AttackSum, sighting.Defenders, sighting.HasAntiAir, sighting.RecceRadius, sighting.RecceSpotStrength,
                        sighting.SeenTurn, sighting.ArmyId);
            return null;
        }

        public static float KnownGarrisonDefenseAt(PlayerSetupData actor, HexCoord hex)
        {
            if (!EnemySightings.TryGetValue(actor, out Dictionary<int, EnemySighting> sightings))
                return 0f;
            foreach (EnemySighting sighting in sightings.Values)
                if (sighting.Hex.Equals(hex))
                    return sighting.DefenseSum;
            return 0f;
        }

        // Null = no known active guarded event at this hex (never seen one, or it's since been
        // consumed — see OnEventConsumed). RaidWeakerArmyTask's own event-guard half of a target's
        // required strength (see that class's own FindTarget/RequiredStrengthAt — takes the max of
        // this and KnownGarrisonDefenseAt for a hex, not their sum, since a physical neutral army
        // sharing this hex and this event's own card-guard are two separate fights, never fought
        // at once).
        public static GuardStrength? KnownEventGuardStrengthAt(PlayerSetupData actor, HexCoord hex)
        {
            return KnownEventGuards.TryGetValue(actor, out Dictionary<HexCoord, GuardStrength> eventGuards)
                && eventGuards.TryGetValue(hex, out GuardStrength strength)
                ? strength
                : (GuardStrength?)null;
        }

        public static float? KnownEventGuardDefenseAt(PlayerSetupData actor, HexCoord hex) => KnownEventGuardStrengthAt(actor, hex)?.Defense;

        // Same guard, its own card-stat Attack sum instead of Defense — WorthIt.Score's own "how
        // hard would the guard hit back" half (see RaidWeakerArmyTask.RequiredStrengthAt).
        public static float? KnownEventGuardAttackAt(PlayerSetupData actor, HexCoord hex) => KnownEventGuardStrengthAt(actor, hex)?.Attack;

        // Every hex this player has ever seen an active guarded event on — RaidWeakerArmyTask's
        // own candidate-gatherer needs to enumerate these the same way it enumerates
        // EnemySightings via KnownEnemySightingsNear, just without a radius (Агрессия's own
        // target pool isn't wavefront-bounded — see that class's own class comment).
        public static IEnumerable<HexCoord> KnownEventGuardHexes(PlayerSetupData actor)
        {
            return KnownEventGuards.TryGetValue(actor, out Dictionary<HexCoord, GuardStrength> eventGuards)
                ? eventGuards.Keys
                : Enumerable.Empty<HexCoord>();
        }

        // One specific hex's own last-known building snapshot, if any — null means either no
        // building has ever been observed there, or the hex was last observed WITHOUT one (see
        // KnownBuildings' own class comment) — RaidWeakerArmyTask.IsStillValidTarget's own use.
        public static KnownBuilding? KnownBuildingAt(PlayerSetupData actor, HexCoord hex)
        {
            if (!KnownBuildings.TryGetValue(actor, out Dictionary<HexCoord, BuildingSighting> buildings)
                || !buildings.TryGetValue(hex, out BuildingSighting sighting))
                return null;
            return new KnownBuilding(sighting.Hex, sighting.Owner, sighting.IsStartingCitadel, sighting.FacilityAbilities);
        }

        // Every building this player has ever observed anywhere on the map, as last seen —
        // RaidWeakerArmyTask's own FindTarget/HasAnythingToRaid/FindCaptureStepDestination replace
        // their old live BuildingRegistry.AllBuildings() scan with this (2026-08-24 fix, section
        // 3.2) so an enemy building's true current owner is never known further than this player's
        // own last look at that specific hex.
        public static IEnumerable<KnownBuilding> AllKnownBuildings(PlayerSetupData actor)
        {
            if (!KnownBuildings.TryGetValue(actor, out Dictionary<HexCoord, BuildingSighting> buildings))
                yield break;
            foreach (BuildingSighting sighting in buildings.Values)
                yield return new KnownBuilding(sighting.Hex, sighting.Owner, sighting.IsStartingCitadel, sighting.FacilityAbilities);
        }

        // How many individual non-hero members, across every currently-known ARMY sighting for
        // `actor` (physical armies only — EnemySightings, not KnownEventGuards' own card-stat
        // guards, which aren't really "an enemy army" in the sense this counts), carry `tag` —
        // AiManagementPlanner's own counter-tech PlayCard scoring reads this (Hyperkinetic once
        // enough known Armored targets are on record, Pyrokinetic for Bio — see that class's own
        // comment) to prefer a card that would actually counter what's already been scouted.
        // Same "видимость с памятью" honesty as every other read here — only ever counts a
        // sighting this player has actually observed, corrected/overwritten the same way
        // DefenseSum/AttackSum already are, never the true enemy roster.
        public static int KnownEnemyTypeTagCount(PlayerSetupData actor, UnitTypeTag tag)
        {
            if (!EnemySightings.TryGetValue(actor, out Dictionary<int, EnemySighting> sightings))
                return 0;
            int count = 0;
            foreach (EnemySighting sighting in sightings.Values)
            {
                if (sighting.Defenders == null)
                    continue;
                foreach (WorthIt.DefenderProfile defender in sighting.Defenders)
                    if (defender.TypeTags.Contains(tag))
                        count++;
            }
            return count;
        }
    }
}
