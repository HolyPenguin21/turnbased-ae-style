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
    // The writes of AiMapMemory: what a player genuinely sees right now is copied into memory.
    // Only currently visible hexes are reconciled; fogged knowledge is never touched here.
    public static partial class AiMapMemory
    {
#if UNITY_INCLUDE_TESTS
        internal static void RefreshVisibleForTest(PlayerSetupData player) =>
            OnVisibilityChanged(player);

        // Seeds an honest remembered observation without native scene visibility.
        internal static void RememberForTest(PlayerSetupData player, KnownEnemySighting sighting)
        {
            StoreFor(EnemySightings, player)[sighting.ArmyId] = sighting;
            BumpRouteAndKnowledge(player);
        }

        internal static void RememberBuildingForTest(PlayerSetupData player, KnownBuilding building)
        {
            StoreFor(KnownBuildings, player)[building.Hex] = building;
            BumpRouteAndKnowledge(player);
        }

        internal static void ForgetSightingsForTest(PlayerSetupData player)
        {
            EnemySightings.Remove(player);
            BumpRouteAndKnowledge(player);
        }
#endif

        private static void OnVisibilityChanged(PlayerSetupData player)
        {
            if (player == null)
                return;

            Dictionary<HexCoord, KnownResourceHex> resources = StoreFor(KnownResourceHexes, player);
            Dictionary<int, KnownAirSighting> airSightings = StoreFor(AirSightings, player);
            Dictionary<int, KnownEnemySighting> sightings = StoreFor(EnemySightings, player);
            Dictionary<HexCoord, KnownBuilding> buildings = StoreFor(KnownBuildings, player);

            // RouteMemoryVersion bumps only when route blockers changed: a known army's presence or
            // position, a known guard's presence, a known building's existence or owner (foreign
            // structures are blocked for every non-Attack AI movement). This runs on EVERY vision
            // recompute; bumping unconditionally forced SafeStepPathing's O(map) blocker snapshot to
            // rebuild on nearly every pathing call. KnowledgeVersion (WorldAnalysis's snapshot
            // refresh gate) stays coarse: any changed record counts.
            bool routeChanged = false;
            bool knowledgeChanged = false;
            foreach (HexCoord hex in VisionSystem.VisibleHexesFor(player))
            {
                knowledgeChanged |= ObserveResource(resources, hex);
                knowledgeChanged |= ObserveAir(player, airSightings, hex);
                ObserveArmies(player, sightings, hex, ref routeChanged, ref knowledgeChanged);

                // Only an event this player already met in person is re-read (never a merely
                // visible one) - see ApplyEventKnowledge. A known guard changes whether an otherwise
                // undefended foreign structure is a safe transit hex (KnownGroundArrival).
                knowledgeChanged |= ApplyEventKnowledge(player, hex, false, out bool guardPresenceChanged);
                routeChanged |= guardPresenceChanged;

                ObserveBuilding(player, buildings, hex, ref routeChanged, ref knowledgeChanged);
            }
            if (knowledgeChanged)
                BumpKnowledgeVersion(player);
            if (routeChanged)
                BumpRouteMemoryVersion(player);
        }

        // Reading the effective yield off a visible hex is no cheat; reconstructing it later from the
        // live map would be. Returns true when the record changed.
        private static bool ObserveResource(Dictionary<HexCoord, KnownResourceHex> resources, HexCoord hex)
        {
            ResourceType? dominant = HexResourceBonusRegistry.DominantResourceType(hex);
            if (!dominant.HasValue || _map == null || !_map.TryGetTerrainAt(hex, out var terrain))
                return false;
            var next = new KnownResourceHex(hex, dominant.Value, HexResourceBonusRegistry.EffectiveYield(terrain, hex));
            bool changed = !resources.TryGetValue(hex, out KnownResourceHex previous) || !previous.SameContent(next);
            resources[hex] = next;
            return changed;
        }

        private static bool ObserveAir(PlayerSetupData player, Dictionary<int, KnownAirSighting> airSightings, HexCoord hex)
        {
            bool changed = false;
            var observedIds = new HashSet<int>();
            foreach (ArmyData army in AviationCombatPresenter.FindAirStrikeTargetsAt(hex, player))
            {
                var visible = army.Members.Where(u => !StealthSystem.IsHiddenFrom(u, player)).ToList();
                var roster = new AviationCombatEstimator.DefendingAirArmy(army.Id,
                    visible.Select(WorthIt.FromLiveUnit).ToList(),
                    visible.Select(u => u.IsHero ? u.Fate : 0).ToList(),
                    visible.IndexOf(army.Commander));
                var sighting = new KnownAirSighting(hex, army.Owner, _currentTurn,
                    BattleInitiator.IsEngageable(army, player), roster);
                observedIds.Add(army.Id);
                if (!airSightings.TryGetValue(army.Id, out var prior) || !SameAirSighting(prior, sighting))
                    changed = true;
                airSightings[army.Id] = sighting;
            }
            foreach (int id in airSightings.Where(kv => kv.Value.Hex.Equals(hex)
                && !observedIds.Contains(kv.Key)).Select(kv => kv.Key).ToList())
            {
                airSightings.Remove(id);
                changed = true;
            }
            return changed;
        }

        // A hex can contain several armies (ArmyRegistry's explicit contract). Every engageable
        // physical army is observed independently by id, never transient event guards; hidden
        // members are filtered per unit (StealthSystem.IsHiddenFrom). Then every old identity no
        // longer visible on THIS hex is dropped — not merely the first, and not an army already
        // re-sighted elsewhere — which also corrects a replaced or defeated neutral while a
        // different army still occupies the hex.
        private static void ObserveArmies(PlayerSetupData player, Dictionary<int, KnownEnemySighting> sightings,
            HexCoord hex, ref bool routeChanged, ref bool knowledgeChanged)
        {
            var observedIds = new HashSet<int>();
            foreach (ArmyData enemy in ArmyRegistry.AllAt(hex))
            {
                if (enemy.Owner == player || !BattleInitiator.IsEngageable(enemy, player)
                    || HexEventRegistry.IsEventGuardArmy(hex, enemy))
                    continue;
                observedIds.Add(enemy.Id);
                bool wasKnown = sightings.TryGetValue(enemy.Id, out KnownEnemySighting previous);
                if (wasKnown && !previous.Hex.Equals(hex))
                    AiDebugLog.Write($"[AI] {player.Nickname}: memory - army \"{enemy.Name}\" id={enemy.Id} relocated "
                        + $"({previous.Hex.Q},{previous.Hex.R}) → ({hex.Q},{hex.R}).");
                else if (IsNeutralSightingOwner(enemy.Owner) && !wasKnown)
                    AiDebugLog.Write($"[AI] {player.Nickname}: memory - neutral \"{enemy.Name}\" remembered at ({hex.Q},{hex.R}).");
                // Route blockers depend on the known army's hex, not its roster or observation turn.
                if (!wasKnown || !previous.Hex.Equals(hex))
                    routeChanged = true;
                KnownEnemySighting next = Observe(player, enemy, hex);
                if (!wasKnown || !previous.SameContent(next))
                    knowledgeChanged = true;
                sightings[enemy.Id] = next;
            }

            List<int> staleIds = sightings
                .Where(kv => kv.Value.Hex.Equals(hex) && !observedIds.Contains(kv.Key))
                .Select(kv => kv.Key).ToList();
            foreach (int staleId in staleIds)
            {
                KnownEnemySighting stale = sightings[staleId];
                if (IsNeutralSightingOwner(stale.Owner))
                    AiDebugLog.Write($"[AI] {player.Nickname}: memory - neutral \"{stale.Name}\" at "
                        + $"({hex.Q},{hex.R}) corrected (gone on re-observation).");
                sightings.Remove(staleId);
                routeChanged = true;
                knowledgeChanged = true;
            }
        }

        // One army as `player` sees it now: only visible members supply any fact.
        private static KnownEnemySighting Observe(PlayerSetupData player, ArmyData enemy, HexCoord hex)
        {
            List<UnitData> visible = enemy.Members.Where(m => !StealthSystem.IsHiddenFrom(m, player)).ToList();
            List<UnitData> bodies = visible.Where(m => m.IsGroundCombatant).ToList();
            return new KnownEnemySighting(hex, enemy.Owner, enemy.Name, visible.Count,
                bodies.Sum(m => m.Defense), bodies.Sum(m => m.Attack),
                visible.Select(WorthIt.FromLiveUnit).ToList(),
                hasAntiAir: visible.Any(m => AntiAirRules.TryGetRadius(m, out _)),
                recceRadius: visible.Select(m => AbilityParams.GetBestRecceRadius(m)).DefaultIfEmpty(0).Max(),
                recceSpotStrength: visible.Select(m => AbilityParams.GetBestRecceSpotStrength(m)).DefaultIfEmpty(0).Max(),
                seenTurn: _currentTurn,
                armyId: enemy.Id,
                isGarrison: enemy.IsGarrison,
                commander: enemy.Commander != null && !StealthSystem.IsHiddenFrom(enemy.Commander, player)
                    ? WorthIt.SideCommander.Of(visible) : default,
                isAir: !enemy.IsGarrison && !enemy.IsAirfield && !enemy.IsPrison && visible.All(m => m.IsAviation),
                maxMovement: visible.Select(AviationRules.EffectiveMoveMax).DefaultIfEmpty(1).Min());
        }

        // A direct read of BuildingRegistry, but only for a hex visible right now; afterwards the
        // record stays as observed until this same hex is re-observed.
        private static void ObserveBuilding(PlayerSetupData player, Dictionary<HexCoord, KnownBuilding> buildings,
            HexCoord hex, ref bool routeChanged, ref bool knowledgeChanged)
        {
            BuildingData building = BuildingRegistry.FindAt(hex);
            if (building == null)
            {
                if (buildings.Remove(hex))
                {
                    AiDebugLog.Write($"[AI] {player.Nickname}: memory - building at ({hex.Q},{hex.R}) corrected (gone on re-observation).");
                    routeChanged = true;
                    knowledgeChanged = true;
                }
                // T01 — the first honest look at an opponent's sanctioned starting-Citadel
                // coordinates finding NO structure refutes the location-only Attack objective
                // (AttackObjectiveEvaluator reads MapKnowledge.EverSeenHexSet). Nothing in the
                // memory itself changes then, so without this revision the snapshot would keep
                // its stale ever-seen set for the rest of the turn. Once per hex per session.
                else if (IsOpponentStartingCitadel(player, hex) && RefutedStartingCitadelsFor(player).Add(hex))
                    knowledgeChanged = true;
                return;
            }

            bool wasKnown = buildings.TryGetValue(hex, out KnownBuilding previous);
            if (!wasKnown || previous.Owner != building.Owner)
                routeChanged = true;
            if (!wasKnown)
                AiDebugLog.Write($"[AI] {player.Nickname}: memory - building \"{building.Name}\" "
                    + $"(owner={NameOf(building.Owner)}) remembered at ({hex.Q},{hex.R}).");
            else if (previous.Owner != building.Owner)
                AiDebugLog.Write($"[AI] {player.Nickname}: memory - building at ({hex.Q},{hex.R}) corrected, owner "
                    + $"{NameOf(previous.Owner)} → {NameOf(building.Owner)}.");

            // T08 — slot contents are recorded only for this player's own building.
            bool ownBuilding = building.Owner == player;
            HashSet<string> facilityAbilities = null;
            if (ownBuilding)
            {
                facilityAbilities = new HashSet<string>();
                foreach (FacilityData facility in building.FacilitySlots)
                    if (facility != null)
                        facilityAbilities.UnionWith(facility.Abilities);
            }
            var collectedAmounts = new int[UnitAbilities.CollectAbilities.Length];
            for (int i = 0; i < collectedAmounts.Length; i++)
                collectedAmounts[i] = building.CollectedAmount((ResourceType)i);
            var next = new KnownBuilding(hex, building.Owner, building.IsStartingCitadel, facilityAbilities,
                collectedAmounts,
                freeFacilitySlots: ownBuilding
                    ? Enumerable.Range(0, building.UnlockedFacilitySlots).Count(i => building.FacilitySlots[i] == null)
                    : 0,
                isBase: building.IsBase,
                defense: building.Defense,
                seenTurn: _currentTurn,
                // Only public structure stats of foreign buildings. Their internal facility
                // slots remain private; own slots may be priced for friendly-fire risk.
                explosionStructures: ExplosionAttack.StructureProfiles(building, includeFacilities: ownBuilding));
            if (!wasKnown || !previous.SameContent(next))
                knowledgeChanged = true;
            buildings[hex] = next;
        }

        private static string NameOf(PlayerSetupData owner) => owner != null ? owner.Nickname : "none";

        // The event's own guard just got beaten for real (reward claimed). The claimant learns of its
        // own completion at once, army or no army left after the fight; any other player only if it
        // already knows this event AND sees the hex now — a vision recompute does not follow by
        // itself, so a watching player would otherwise keep believing the guard is there. Everyone
        // else learns by re-observing the hex later ("видимость с памятью").
        private static void OnEventConsumed(HexCoord hex)
        {
            PlayerSetupData consumer = HexEventRegistry.FindAt(hex)?.ConsumedBy;
            var holders = new List<PlayerSetupData>(KnownEventCompleted.Keys);
            if (consumer != null && !holders.Contains(consumer))
                holders.Add(consumer);
            foreach (PlayerSetupData player in holders)
            {
                if (player != consumer && !VisionSystem.IsVisible(player, hex))
                    continue;
                PublishEventKnowledge(player, hex, player == consumer);
            }
        }

        // The authoritative handler recorded a personal encounter: remember the event (and its
        // guard, if any) for exactly this player. Idempotent - a repeat changes nothing.
        private static void OnEventDiscovered(HexCoord hex, PlayerSetupData player)
        {
            if (player == null || player.IsNeutral)
                return;
            PublishEventKnowledge(player, hex, true);
        }

        // A re-observation of a hex the player can currently see: confirms completion of, or
        // refreshes, an event it already knows. No effect for an unknown event.
        public static void ObserveEventAt(PlayerSetupData player, HexCoord hex)
        {
            if (player != null && VisionSystem.IsVisible(player, hex))
                PublishEventKnowledge(player, hex, false);
        }

        private static void PublishEventKnowledge(PlayerSetupData player, HexCoord hex, bool discover)
        {
            if (!ApplyEventKnowledge(player, hex, discover, out bool guardPresenceChanged))
                return;
            BumpKnowledgeVersion(player);
            if (guardPresenceChanged)
                BumpRouteMemoryVersion(player);
        }

        // The single reader of the hidden event data on behalf of memory. It touches the registry
        // only for an event `player` knows already (or is just discovering), so a hex that is merely
        // visible never reveals its guard. Returns true when stored knowledge changed.
        private static bool ApplyEventKnowledge(PlayerSetupData player, HexCoord hex, bool discover,
            out bool guardPresenceChanged)
        {
            guardPresenceChanged = false;
            Dictionary<HexCoord, bool> states = StoreFor(KnownEventCompleted, player);
            Dictionary<HexCoord, GuardStrength> guards = StoreFor(KnownEventGuards, player);

            bool known = states.TryGetValue(hex, out bool completed);
            if (completed)
                return false; // confirmed completion is final
            if (!known && (!discover || !HexEventRegistry.IsKnownBy(hex, player)))
                return false;

            HexEventRegistry.Entry entry = HexEventRegistry.FindAt(hex);
            bool hadGuard = guards.TryGetValue(hex, out GuardStrength previousGuard);
            bool changed = !known;
            if (entry == null || entry.Consumed)
            {
                states[hex] = true;
                guards.Remove(hex);
                guardPresenceChanged = hadGuard;
                return true;
            }

            states[hex] = false;
            if (entry.ResolvedGuardMembers.Count > 0)
            {
                GuardStrength guard = BuildEventGuard(entry);
                guards[hex] = guard;
                guardPresenceChanged = !hadGuard;
                changed |= !hadGuard || !previousGuard.SameContent(guard);
            }
            else if (hadGuard)
            {
                guards.Remove(hex);
                guardPresenceChanged = true;
                changed = true;
            }
            return changed;
        }

        private static GuardStrength BuildEventGuard(HexEventRegistry.Entry eventEntry)
        {
            // Flat card-stat sums — the guard is never a live ArmyData until Explore is chosen
            // (see HexEventRegistry.Entry), so card stats are all there is to read.
            var guardMembers = eventEntry.ResolvedGuardMembers
                .Where(g => g.card != null && g.card.cardType != CardType.Hero).ToList();
            float defense = guardMembers.Sum(g => g.card.defenseRating * g.count);
            float attack = guardMembers.Sum(g => g.card.attack * g.count);
            // One DefenderProfile per PHYSICAL copy (repeated by g.count), not one per card type:
            // WorthIt's full-roster Monte Carlo needs one real combatant/HP-pool per copy to play a
            // "3 Grunts" guard out as three separate units. CanDamageAll's coverage check does not
            // care about the duplication.
            var defenders = guardMembers.SelectMany(g => Enumerable.Repeat(new WorthIt.DefenderProfile(g.card.defenseRating,
                g.card.grantedAbilities != null && g.card.grantedAbilities.Contains(UnitAbilities.CeramicArmor),
                g.card.unitTypeTags, g.card.attack, g.card.hitPoints, g.card.initiative,
                g.card.grantedAbilities, range: g.card.range), g.count)).ToList();
            return new GuardStrength(defense, attack, defenders, eventEntry.GuardArmyName,
                HexEventGuardEstimate.GuardCommander(eventEntry), HexEventGuardEstimate.RewardTier(eventEntry));
        }
    }
}
