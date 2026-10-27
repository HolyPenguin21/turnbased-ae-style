using System.Collections.Generic;
using System.Linq;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Ai
{
    // Honest per-player memory of hex CONTENT — the piece VisionSystem itself does NOT keep
    // (content re-hides the instant vision leaves). Every vision recompute copies what is on the
    // player's currently visible hexes into permanent-until-corrected stores: resource hexes with
    // their last observed effective yield, the last sighting of every enemy/neutral army (ground
    // and air), buildings with their last observed owner, and the Hex Events the player met in
    // person. "Видимость с памятью": stale knowledge is never auto-expired, only overwritten by a
    // fresh observation of that SAME hex — with narrow exceptions: a roaming player-owned army
    // sighting expires after AiConfig.enemySightingMemoryTurns (neutrals and garrisons never do),
    // a destroyed army is forgotten by its witnesses, and the claimant of an event and a player
    // watching it learn of its consumption at once.
    //
    // This is the one sanctioned source of concrete foreign positions and rosters for the AI:
    // Strategy V2 (WorldAnalysis, Recon, Aggression, SafeStepPathing route blockers) reads it,
    // never live ArmyData of other players.
    //
    // Files: AiMapMemory.cs — stores, revisions, subscriptions, turn lifecycle;
    // .Records.cs — the public read-only records; .Observation.cs — every write from vision and
    // events; .Queries.cs — every read.
    public static partial class AiMapMemory
    {
        // ---- Stores (per player) ----

        private static readonly Dictionary<PlayerSetupData, Dictionary<int, KnownAirSighting>> AirSightings =
            new Dictionary<PlayerSetupData, Dictionary<int, KnownAirSighting>>();
        private static readonly Dictionary<PlayerSetupData, Dictionary<HexCoord, KnownResourceHex>> KnownResourceHexes =
            new Dictionary<PlayerSetupData, Dictionary<HexCoord, KnownResourceHex>>();
        // Keyed by ArmyData.Id, NOT HexCoord: the same slot is overwritten on every re-sighting,
        // move included, so there is only ever one live record per physical army.
        private static readonly Dictionary<PlayerSetupData, Dictionary<int, KnownEnemySighting>> EnemySightings =
            new Dictionary<PlayerSetupData, Dictionary<int, KnownEnemySighting>>();
        // 2026-10-21 (playtest Vex/Mordak) — field armies this player SAW destroyed (a battle it
        // fought or watched, a retreat with no way out). Vex wiped Mud Crawl #33 at T20, yet the
        // Recon history kept it as a threat drifting toward its Citadel until T22 and called a
        // marching Attack army home. Army ids are never reused (ArmyData._nextId), so the mark is
        // permanent; a garrison container survives its battle and is never marked here.
        private static readonly Dictionary<PlayerSetupData, HashSet<int>> WitnessedDestroyedArmies =
            new Dictionary<PlayerSetupData, HashSet<int>>();
        // Playtest 2026-10-22 — every structure this player lost to someone else's action
        // (BuildingRegistry.StructureLost) and the turn it happened. The owner knows its own loss
        // at once; Recon looks there for the saboteur that is still standing on the ruin.
        private static readonly Dictionary<PlayerSetupData, Dictionary<HexCoord, int>> LostStructures =
            new Dictionary<PlayerSetupData, Dictionary<HexCoord, int>>();
        // The guard of every Hex Event this player met in person (HexEventRegistry.MarkDiscovered)
        // while it is still active and really guarded. An unguarded event has no entry here.
        private static readonly Dictionary<PlayerSetupData, Dictionary<HexCoord, GuardStrength>> KnownEventGuards =
            new Dictionary<PlayerSetupData, Dictionary<HexCoord, GuardStrength>>();
        // What a player knows about each Hex Event it has PERSONALLY encountered; true = confirmed
        // completed. Absent = unknown: being in vision range never adds a hex here, and a missing
        // guard entry in KnownEventGuards is NOT proof of completion (the event may be unguarded).
        private static readonly Dictionary<PlayerSetupData, Dictionary<HexCoord, bool>> KnownEventCompleted =
            new Dictionary<PlayerSetupData, Dictionary<HexCoord, bool>>();
        private static readonly Dictionary<PlayerSetupData, Dictionary<HexCoord, KnownBuilding>> KnownBuildings =
            new Dictionary<PlayerSetupData, Dictionary<HexCoord, KnownBuilding>>();
        // Opponent starting-Citadel coordinates this player has already seen empty (see
        // ObserveBuilding).
        private static readonly Dictionary<PlayerSetupData, HashSet<HexCoord>> RefutedStartingCitadels =
            new Dictionary<PlayerSetupData, HashSet<HexCoord>>();

        private static bool _subscribed;
        private static HexMap _map;
        // Global game turn as of the most recent OnTurnStarted — stamps SeenTurn of new records.
        private static int _currentTurn;

        public static bool GroundTerrainBlocked(HexCoord hex) => _map != null && !_map.CanEnter(hex);

        private static Dictionary<TKey, TValue> StoreFor<TKey, TValue>(
            Dictionary<PlayerSetupData, Dictionary<TKey, TValue>> stores, PlayerSetupData player)
        {
            if (!stores.TryGetValue(player, out Dictionary<TKey, TValue> store))
                stores[player] = store = new Dictionary<TKey, TValue>();
            return store;
        }

        private static bool TryStore<TKey, TValue>(Dictionary<PlayerSetupData, Dictionary<TKey, TValue>> stores,
            PlayerSetupData player, out Dictionary<TKey, TValue> store)
        {
            store = null;
            return player != null && stores.TryGetValue(player, out store);
        }

        private static IEnumerable<PlayerSetupData> SessionPlayers() =>
            Game.Core.GameSession.Players ?? Enumerable.Empty<PlayerSetupData>();

        // The memory layer classifies ownerless physical encounter armies with explicit neutrals;
        // Raid legality remains owned by RaidObjectiveEvaluator in V2, never called upward here.
        private static bool IsNeutralSightingOwner(PlayerSetupData owner) =>
            owner == null || owner.IsNeutral;

        // ---- Revisions ----

        // Player-scoped revision of exactly the memory that can change SafeStepPathing blockers.
        // One player's observation must never invalidate another player's route cache. The high
        // 32 bits are a global Clear() epoch; the low 32 bits are this player's own revision, so
        // clearing a reused session invalidates every old cache without sharing normal writes.
        private static long _routeMemoryEpoch;
        private static readonly Dictionary<PlayerSetupData, uint> RouteMemoryVersions =
            new Dictionary<PlayerSetupData, uint>();

        public static long RouteMemoryVersionFor(PlayerSetupData player)
        {
            uint local = player != null && RouteMemoryVersions.TryGetValue(player, out uint version)
                ? version : 0u;
            return (_routeMemoryEpoch << 32) | local;
        }

        private static void BumpRouteMemoryVersion(PlayerSetupData player)
        {
            if (player == null)
                return;
            uint current = RouteMemoryVersions.TryGetValue(player, out uint version) ? version : 0u;
            RouteMemoryVersions[player] = current + 1u;
        }

        // Snapshot invalidation is player-scoped: another player's observation must not force this
        // AI to rebuild Known/MapKnowledge. This is only a revision of the memory already owned
        // here; it adds no subscription, cached snapshot or alternate observation path.
        private static readonly Dictionary<PlayerSetupData, int> KnowledgeVersions =
            new Dictionary<PlayerSetupData, int>();
        private static int _knowledgeVersionSeed;

        public static int KnowledgeVersionFor(PlayerSetupData player) =>
            player != null && KnowledgeVersions.TryGetValue(player, out int version)
                ? version : _knowledgeVersionSeed;

        // Observation metadata (V2 IntelAge) invalidates strategic snapshots, not route blockers.
        internal static void InvalidateObservedKnowledge(PlayerSetupData player) =>
            BumpKnowledgeVersion(player);

        private static void BumpKnowledgeVersion(PlayerSetupData player)
        {
            if (player == null)
                return;
            KnowledgeVersions[player] = KnowledgeVersionFor(player) + 1;
        }

        private static void BumpRouteAndKnowledge(PlayerSetupData player)
        {
            BumpRouteMemoryVersion(player);
            BumpKnowledgeVersion(player);
        }

        // ---- Subscriptions ----

        // Idempotent — safe to call every new-game setup without risking a doubled subscription
        // (see CitadelSetupController, which calls this right after VisionSystem.Configure).
        public static void EnsureSubscribed(HexMap map = null)
        {
            if (map != null)
                _map = map;
            if (_subscribed)
                return;
            VisionSystem.VisibilityChanged += OnVisionStateChanged;
            VisionSystem.VisibleContentChanged += OnVisibleContentChanged;
            // Capture/destruction publishes this BEFORE the former owner's building-derived
            // vision is recomputed away. Consume that honest observation window so fog memory
            // records the new owner/destruction instead of preserving the old owner forever.
            BuildingRegistry.VisualStateChanged += OnBuildingVisualStateChanged;
            // A hidden unit entering/leaving stealth, or a personal detection lapsing, changes
            // what each player can honestly see without any vision-radius change — re-snapshot
            // so a now-hidden enemy drops out of current sightings and a freshly-revealed one
            // enters (spec §8: no stale-memory targeting of a unit that's hidden again now).
            StealthSystem.StealthChanged += OnStealthChanged;
            // The event's own guard just got beaten for real (see OnEventConsumed).
            HexEventRegistry.EventConsumed += OnEventConsumed;
            // The only way an event enters memory: this player's own ground army met it.
            HexEventRegistry.EventDiscovered += OnEventDiscovered;
            // A field army wiped in a battle (or destroyed on a retreat with no way out) is gone
            // for every player who fought or saw it; nobody else learns it.
            BattleEngine.EncounterFinalized += OnEncounterFinalized;
            BattleEngine.ArmyRetreated += OnArmyRetreated;
            BuildingRegistry.StructureLost += OnStructureLost;
            _subscribed = true;
        }

        // VisionSystem emits this only when visible/visited sets change. A newly visited empty
        // hex must refresh MapKnowledge even when object memory and this turn's IntelAge match.
        private static void OnVisionStateChanged(PlayerSetupData player)
        {
            if (Game.Setup.MatchRestoreScope.IsRestoring) return;
            BumpKnowledgeVersion(player);
            OnVisibilityChanged(player);
        }

        // Content changed under an already-visible hex (arrival, departure, capture) — separate
        // from a vision-radius change, so the snapshot does not rerun on every animation step
        // whose reveal area stayed identical.
        private static void OnVisibleContentChanged(PlayerSetupData player, HexCoord hex)
        {
            if (Game.Setup.MatchRestoreScope.IsRestoring) return;
            OnVisibilityChanged(player);
        }

        private static void OnBuildingVisualStateChanged(HexCoord hex, BuildingData building)
        {
            if (Game.Setup.MatchRestoreScope.IsRestoring) return;
            // VisualStateChanged is deliberately emitted before BuildingRegistry recomputes the
            // old/new owners' vision. Only players whose CURRENT pre-change visibility includes
            // this hex receive the memory write; players out of sight learn nothing.
            foreach (PlayerSetupData player in SessionPlayers())
                if (player != null && VisionSystem.IsVisible(player, hex))
                    OnVisibleContentChanged(player, hex);
        }

        private static void OnStealthChanged()
        {
            if (Game.Setup.MatchRestoreScope.IsRestoring) return;
            foreach (PlayerSetupData player in SessionPlayers())
                OnVisibilityChanged(player);
        }

        private static void OnStructureLost(HexCoord hex, PlayerSetupData formerOwner)
        {
            if (Game.Setup.MatchRestoreScope.IsRestoring) return;
            if (formerOwner == null)
                return;
            StoreFor(LostStructures, formerOwner)[hex] = _currentTurn;
            BumpKnowledgeVersion(formerOwner);
        }

        // The most recent structure `player` lost on or after `sinceTurn` (ties: the hex nearest
        // `near`, then by coordinates), or false when none.
        public static bool TryRecentStructureLoss(PlayerSetupData player, int sinceTurn, HexCoord near,
            out HexCoord hex)
        {
            hex = default;
            if (!TryStore(LostStructures, player, out Dictionary<HexCoord, int> lost))
                return false;
            bool found = false;
            int bestTurn = int.MinValue;
            foreach (KeyValuePair<HexCoord, int> kv in lost)
            {
                if (kv.Value < sinceTurn)
                    continue;
                bool better = !found || kv.Value > bestTurn
                    || kv.Value == bestTurn && (HexGridMath.Distance(kv.Key, near) < HexGridMath.Distance(hex, near)
                        || HexGridMath.Distance(kv.Key, near) == HexGridMath.Distance(hex, near)
                            && (kv.Key.Q < hex.Q || kv.Key.Q == hex.Q && kv.Key.R < hex.R));
                if (!better)
                    continue;
                found = true;
                bestTurn = kv.Value;
                hex = kv.Key;
            }
            return found;
        }

        // Whether `player` saw this enemy army destroyed. Read by the V2 Recon history so a dead
        // army stops being a remembered contact (AiReconMemory.Observe / Historical).
        public static bool IsWitnessedDestroyed(PlayerSetupData player, int armyId) =>
            player != null && WitnessedDestroyedArmies.TryGetValue(player, out HashSet<int> ids)
            && ids.Contains(armyId);

        private static void OnEncounterFinalized(BattleState state, BattleEncounterFinalization result)
        {
            if (Game.Setup.MatchRestoreScope.IsRestoring) return;
            if (state == null)
                return;
            foreach (ArmyData army in new[] { state.Attacker, state.Defender })
                if (army != null && army.Members.Count == 0)
                    RecordDestroyed(army, state.BattleHex, state.Attacker, state.Defender);
        }

        private static void OnArmyRetreated(BattleState state, ArmyData army, BattleRetreatApplication result)
        {
            if (Game.Setup.MatchRestoreScope.IsRestoring) return;
            if (army != null && result.Destroyed)
                RecordDestroyed(army, state?.BattleHex ?? army.Hex, state?.Attacker, state?.Defender);
        }

        // The players who learn the death: both battle sides' owners and anyone seeing the hex.
        internal static void RecordDestroyed(ArmyData army, HexCoord hex, ArmyData attacker, ArmyData defender)
        {
            if (army.IsGarrison || army.Owner == null)
                return;
            foreach (PlayerSetupData player in SessionPlayers())
            {
                if (player == null || player == army.Owner)
                    continue;
                bool witness = attacker?.Owner == player || defender?.Owner == player
                    || VisionSystem.IsVisible(player, hex);
                if (!witness)
                    continue;
                if (!WitnessedDestroyedArmies.TryGetValue(player, out HashSet<int> ids))
                    WitnessedDestroyedArmies[player] = ids = new HashSet<int>();
                if (!ids.Add(army.Id))
                    continue;
                bool removed = TryStore(EnemySightings, player, out var sightings) && sightings.Remove(army.Id);
                removed |= TryStore(AirSightings, player, out var air) && air.Remove(army.Id);
                AiDebugLog.Write($"[AI] {player.Nickname}: memory - army \"{army.Name}\" id={army.Id} "
                    + $"seen destroyed at ({hex.Q},{hex.R}); forgotten as a contact.");
                if (removed)
                    BumpRouteMemoryVersion(player);
                BumpKnowledgeVersion(player);
            }
        }

        private static HashSet<HexCoord> RefutedStartingCitadelsFor(PlayerSetupData player)
        {
            if (!RefutedStartingCitadels.TryGetValue(player, out HashSet<HexCoord> set))
                RefutedStartingCitadels[player] = set = new HashSet<HexCoord>();
            return set;
        }

        private static bool IsOpponentStartingCitadel(PlayerSetupData player, HexCoord hex)
        {
            foreach (PlayerSetupData p in SessionPlayers())
                if (p != null && p != player && p.CitadelHexQ == hex.Q && p.CitadelHexR == hex.R)
                    return true;
            return false;
        }

        // ---- Lifecycle ----

        public static void Clear()
        {
            // A reused player/snapshot must never see the same revision after a session reset.
            // Keep the next baseline above every revision issued in the previous session.
            if (KnowledgeVersions.Count > 0)
                _knowledgeVersionSeed = System.Math.Max(_knowledgeVersionSeed,
                    KnowledgeVersions.Values.Max());
            _knowledgeVersionSeed++;
            KnownResourceHexes.Clear();
            EnemySightings.Clear();
            WitnessedDestroyedArmies.Clear();
            LostStructures.Clear();
            AirSightings.Clear();
            KnownEventGuards.Clear();
            KnownEventCompleted.Clear();
            KnownBuildings.Clear();
            RefutedStartingCitadels.Clear();
            KnowledgeVersions.Clear();
            RouteMemoryVersions.Clear();
            _currentTurn = 0;
            _map = null;
            _routeMemoryEpoch++;
        }

        // Called at the top of AiTurnController.RunTurn, before the turn reads memory: stamps
        // _currentTurn for every record written from now on and expires `actor`'s own roaming
        // player-owned army sightings last (re)observed more than AiConfig.enemySightingMemoryTurns
        // turns ago. Scoped to `actor` alone — every other
        // player's memory is swept the same way at the top of its own turn.
        public static void OnTurnStarted(PlayerSetupData actor, int turnNumber)
        {
            _currentTurn = turnNumber;
            if (actor == null)
                return;

            if (EnemySightings.TryGetValue(actor, out Dictionary<int, KnownEnemySighting> sightings))
            {
                // A physical NEUTRAL army never expires by elapsed turns: it stays as last observed
                // until its hex is re-observed ("узнаёт об уничтожении только после повторного
                // наблюдения"). A player-owned GARRISON is building-bound: elapsed turns are no
                // evidence it is gone, and expiring it turned "not re-looked at" into "undefended"
                // for every site-defender read (2026-09-25 audit F1).
                List<int> stale = sightings
                    .Where(kv => !IsNeutralSightingOwner(kv.Value.Owner) && !kv.Value.IsGarrison
                        && turnNumber - kv.Value.SeenTurn > AiConfig.enemySightingMemoryTurns)
                    .Select(kv => kv.Key).ToList();
                foreach (int armyId in stale)
                {
                    KnownEnemySighting s = sightings[armyId];
                    AiDebugLog.Write($"[AI] {actor.Nickname}: memory - army sighting \"{s.Name}\" "
                        + $"at ({s.Hex.Q},{s.Hex.R}) expired after {AiConfig.enemySightingMemoryTurns} turns.");
                    sightings.Remove(armyId);
                }
                if (stale.Count > 0)
                    BumpRouteAndKnowledge(actor);
            }
        }
    }
}
