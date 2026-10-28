using System;
using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Ai
{
    public static partial class AiMapMemory
    {
        // Preparation boundary, not a file format. Foreign owners are saved by ID; roster
        // profiles are the already observed values, never rebuilt from the current world.
        internal readonly struct OwnedObservation<T>
        {
        // Exact detached save reconstruction; no planning, registration or normalization.
        internal OwnedObservation(bool __matchSave, string __OwnerId, T __Observation)
        {
            OwnerId = __OwnerId;
            Observation = __Observation;
        }

            internal readonly string OwnerId;
            internal readonly T Observation;
            internal OwnedObservation(string ownerId, T observation)
            { OwnerId = ownerId; Observation = observation; }
        }

        internal sealed class PlayerMemoryState
        {
        // Exact detached save reconstruction; no planning, registration or normalization.
        internal PlayerMemoryState(bool __matchSave, global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.AiMapMemory.KnownResourceHex> __Resources, global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.AiMapMemory.OwnedObservation<global::Game.Ai.AiMapMemory.KnownEnemySighting>> __Enemies, global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.AiMapMemory.OwnedObservation<global::Game.Ai.AiMapMemory.KnownAirSighting>> __Aircraft, global::System.Collections.Generic.IReadOnlyList<global::Game.Ai.AiMapMemory.OwnedObservation<global::Game.Ai.AiMapMemory.KnownBuilding>> __Buildings, global::System.Collections.Generic.IReadOnlyDictionary<global::Game.HexGrid.HexCoord, global::Game.Ai.AiMapMemory.GuardStrength> __Guards, global::System.Collections.Generic.IReadOnlyDictionary<global::Game.HexGrid.HexCoord, bool> __Events, global::System.Collections.Generic.IReadOnlyDictionary<global::Game.HexGrid.HexCoord, int> __StructureLosses, global::System.Collections.Generic.IReadOnlyList<int> __DestroyedArmies, global::System.Collections.Generic.IReadOnlyList<global::Game.HexGrid.HexCoord> __RefutedCitadels)
        {
            Resources = __Resources;
            Enemies = __Enemies;
            Aircraft = __Aircraft;
            Buildings = __Buildings;
            Guards = __Guards;
            Events = __Events;
            StructureLosses = __StructureLosses;
            DestroyedArmies = __DestroyedArmies;
            RefutedCitadels = __RefutedCitadels;
        }

            internal readonly IReadOnlyList<KnownResourceHex> Resources;
            internal readonly IReadOnlyList<OwnedObservation<KnownEnemySighting>> Enemies;
            internal readonly IReadOnlyList<OwnedObservation<KnownAirSighting>> Aircraft;
            internal readonly IReadOnlyList<OwnedObservation<KnownBuilding>> Buildings;
            internal readonly IReadOnlyDictionary<HexCoord, GuardStrength> Guards;
            internal readonly IReadOnlyDictionary<HexCoord, bool> Events;
            internal readonly IReadOnlyDictionary<HexCoord, int> StructureLosses;
            internal readonly IReadOnlyList<int> DestroyedArmies;
            internal readonly IReadOnlyList<HexCoord> RefutedCitadels;

            internal PlayerMemoryState(PlayerSetupData player, Func<PlayerSetupData, string> playerId)
            {
                Resources = AiPersistenceCollections.FreezeList(Values(KnownResourceHexes, player)
                    .Select(x => new KnownResourceHex(x.Hex, x.DominantType, x.Yield)));
                Enemies = AiPersistenceCollections.FreezeList(Values(EnemySightings, player)
                    .Select(x => new OwnedObservation<KnownEnemySighting>(Id(x.Owner, playerId), Copy(x, null))));
                Aircraft = AiPersistenceCollections.FreezeList(Values(AirSightings, player)
                    .Select(x => new OwnedObservation<KnownAirSighting>(Id(x.Owner, playerId),
                        new KnownAirSighting(x.Hex, null, x.SeenTurn, x.IsGroundArmy, x.Roster))));
                Buildings = AiPersistenceCollections.FreezeList(Values(KnownBuildings, player)
                    .Select(x => new OwnedObservation<KnownBuilding>(Id(x.Owner, playerId), Copy(x, null))));
                Guards = AiPersistenceCollections.Freeze(FreezeStore(KnownEventGuards, player)
                    .Select(x => new KeyValuePair<HexCoord, GuardStrength>(x.Key, new GuardStrength(
                        x.Value.Defense, x.Value.Attack, x.Value.Defenders, x.Value.Name,
                        AiPersistenceCollections.FreezeCommander(x.Value.Commander), x.Value.RewardTier))));
                Events = FreezeStore(KnownEventCompleted, player);
                StructureLosses = FreezeStore(LostStructures, player);
                DestroyedArmies = AiPersistenceCollections.FreezeList(WitnessedDestroyedArmies.TryGetValue(player, out var ids)
                    ? (IEnumerable<int>)ids : Array.Empty<int>());
                RefutedCitadels = AiPersistenceCollections.FreezeList(RefutedStartingCitadels.TryGetValue(player, out var hexes)
                    ? (IEnumerable<HexCoord>)hexes : Array.Empty<HexCoord>());
            }
        }

        internal sealed class PersistentState
        {
        // Exact detached save reconstruction; no planning, registration or normalization.
        internal PersistentState(bool __matchSave, int __CurrentTurn, global::System.Collections.Generic.IReadOnlyDictionary<string, global::Game.Ai.AiMapMemory.PlayerMemoryState> __Players)
        {
            CurrentTurn = __CurrentTurn;
            Players = __Players;
        }

            internal readonly int CurrentTurn;
            internal readonly IReadOnlyDictionary<string, PlayerMemoryState> Players;
            internal PersistentState(int currentTurn, IReadOnlyDictionary<string, PlayerMemoryState> players)
            { CurrentTurn = currentTurn; Players = players; }
        }

        internal static PersistentState CapturePersistentState(Func<PlayerSetupData, string> playerId)
        {
            if (playerId == null) throw new ArgumentNullException(nameof(playerId));
            var players = KnownResourceHexes.Keys.Concat(EnemySightings.Keys).Concat(AirSightings.Keys)
                .Concat(KnownBuildings.Keys).Concat(KnownEventGuards.Keys).Concat(KnownEventCompleted.Keys)
                .Concat(LostStructures.Keys).Concat(WitnessedDestroyedArmies.Keys).Concat(RefutedStartingCitadels.Keys)
                .Distinct();
            return new PersistentState(_currentTurn, AiPersistenceCollections.Freeze(players.Select(p =>
                new KeyValuePair<string, PlayerMemoryState>(AiPersistenceCollections.CapturePlayerId(p, playerId), new PlayerMemoryState(p, playerId)))));
        }

        internal static void RestorePersistentState(PersistentState state,
            Func<string, PlayerSetupData> player, HexMap map = null)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (player == null) throw new ArgumentNullException(nameof(player));
            // Bind all identities before clearing; a broken save cannot partially replace memory.
            var owners = new Dictionary<string, PlayerSetupData>();
            foreach (string id in state.Players.Keys.Concat(state.Players.Values.SelectMany(p =>
                p.Enemies.Select(x => x.OwnerId).Concat(p.Aircraft.Select(x => x.OwnerId))
                    .Concat(p.Buildings.Select(x => x.OwnerId))).Where(x => x != null)).Distinct())
                owners.Add(id, AiPersistenceCollections.ResolvePlayer(id, player));
            PlayerSetupData Resolve(string id) => id != null ? owners[id] : null;
            int highestArmyId = state.Players.Values.SelectMany(p => p.DestroyedArmies
                .Concat(p.Enemies.Select(x => x.Observation.ArmyId))
                .Concat(p.Aircraft.Select(x => x.Observation.ArmyId))).DefaultIfEmpty(-1).Max();
            if (highestArmyId >= 0) ArmyData.ReserveIdentity(highestArmyId);
            Clear();
            _currentTurn = state.CurrentTurn;
            foreach (var row in state.Players)
            {
                PlayerSetupData owner = owners[row.Key];
                PlayerMemoryState p = row.Value;
                KnownResourceHexes[owner] = p.Resources.ToDictionary(x => x.Hex,
                    x => new KnownResourceHex(x.Hex, x.DominantType, x.Yield));
                EnemySightings[owner] = p.Enemies.ToDictionary(x => x.Observation.ArmyId,
                    x => Copy(x.Observation, Resolve(x.OwnerId)));
                AirSightings[owner] = p.Aircraft.ToDictionary(x => x.Observation.ArmyId,
                    x => new KnownAirSighting(x.Observation.Hex, Resolve(x.OwnerId), x.Observation.SeenTurn,
                        x.Observation.IsGroundArmy, x.Observation.Roster));
                KnownBuildings[owner] = p.Buildings.ToDictionary(x => x.Observation.Hex,
                    x => Copy(x.Observation, Resolve(x.OwnerId)));
                KnownEventGuards[owner] = p.Guards.ToDictionary(x => x.Key, x => x.Value);
                KnownEventCompleted[owner] = p.Events.ToDictionary(x => x.Key, x => x.Value);
                LostStructures[owner] = p.StructureLosses.ToDictionary(x => x.Key, x => x.Value);
                WitnessedDestroyedArmies[owner] = new HashSet<int>(p.DestroyedArmies);
                RefutedStartingCitadels[owner] = new HashSet<HexCoord>(p.RefutedCitadels);
                BumpRouteAndKnowledge(owner);
            }
            // Subscribing does not observe: startup must restore vision without publishing a
            // synthetic observation of the loaded world. Cache revisions intentionally renew.
            EnsureSubscribed(map);
        }

        private static string Id(PlayerSetupData owner, Func<PlayerSetupData, string> playerId) =>
            owner == null ? null : AiPersistenceCollections.CapturePlayerId(owner, playerId);
        private static IEnumerable<T> Values<K, T>(Dictionary<PlayerSetupData, Dictionary<K, T>> stores,
            PlayerSetupData player) => stores.TryGetValue(player, out var store) ? store.Values : Enumerable.Empty<T>();
        private static IReadOnlyDictionary<K, T> FreezeStore<K, T>(
            Dictionary<PlayerSetupData, Dictionary<K, T>> stores, PlayerSetupData player) =>
            AiPersistenceCollections.Freeze(stores.TryGetValue(player, out var store) ? store : new Dictionary<K, T>());
        private static KnownEnemySighting Copy(KnownEnemySighting x, PlayerSetupData owner) =>
            new KnownEnemySighting(x.Hex, owner, x.Name, x.MemberCount, x.DefenseSum, x.AttackSum,
                x.Defenders, x.HasAntiAir, x.RecceRadius, x.RecceSpotStrength, x.SeenTurn, x.ArmyId,
                x.IsGarrison, AiPersistenceCollections.FreezeCommander(x.Commander), x.IsAir, x.MaxMovement);
        private static KnownBuilding Copy(KnownBuilding x, PlayerSetupData owner) =>
            new KnownBuilding(x.Hex, owner, x.IsStartingCitadel,
                x.FacilityAbilities == null ? null : Array.AsReadOnly(x.FacilityAbilities.ToArray()),
                x.CollectedAmounts == null ? null : Array.AsReadOnly(x.CollectedAmounts.ToArray()),
                x.FreeFacilitySlots, x.IsBase, x.Defense, x.SeenTurn, x.ExplosionStructures);
    }
}
