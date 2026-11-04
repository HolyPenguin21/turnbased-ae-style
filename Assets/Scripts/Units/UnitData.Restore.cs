using System;
using Game.Cards;
using Game.Map;

namespace Game.Units
{
    [Serializable] public sealed class UnitCarrierLinksState
    {
        public SerializableNullableInt AirfieldArmyId;
        public SerializableNullableInt FlightDeckArmyId;
    }
    public partial class UnitData
    {
        // Load path: replace the authored part of a restored body with the current content of
        // CatalogSource (project owner, 2026-11-04 — saves survive balance and art patches).
        // Kept as saved: identity, owner, IsHero (neutral spawns choose it apart from the card),
        // aviation kind, airfield capacity (stored aircraft may not exceed it), the rolled
        // repair cost and all match state. Aviation launch values are authored only on the
        // FromDefinition path (OriginatingCard), so other bodies keep their saved ones.
        public void RebaseOnCatalog()
        {
            CardDefinition source = CatalogSource;
            if (source == null) return;
            EquipmentSystem.RebaseOnCatalog(this, source);
            Name = source.displayName;
            Art = source.art;
            DetailArt = source.detailArt != null ? source.detailArt : source.art;
            TypeTags.Clear();
            if (source.unitTypeTags != null) TypeTags.UnionWith(source.unitTypeTags);
            ApCost = source.apCost;
            OriginalResourceCost = source.resourceCost == null ? null : new ResourceCost
            {
                human = source.resourceCost.human, energy = source.resourceCost.energy,
                materials = source.resourceCost.materials, tech = source.resourceCost.tech,
            };
            if (ReferenceEquals(OriginatingCard, source))
            {
                LaunchEnergyCost = source.launchEnergyCost;
                AntiAirRadius = System.Math.Max(1, source.antiAirRadius);
            }
        }

        public static int IdentityHighWaterMark => System.Threading.Volatile.Read(ref _nextRuntimeId);
        public static void ReserveIdentity(int id)
        {
            if (id <= 0 || id == int.MaxValue) throw new ArgumentOutOfRangeException(nameof(id));
            int issued;
            while ((issued = System.Threading.Volatile.Read(ref _nextRuntimeId)) < id
                && System.Threading.Interlocked.CompareExchange(ref _nextRuntimeId, id, issued) != issued) { }
        }
        public UnitCarrierLinksState CaptureCarrierLinksState() => new UnitCarrierLinksState
        {
            AirfieldArmyId = CarrierAirfield?.Id, FlightDeckArmyId = CarrierFlightDeck?.Id,
        };
        public void RestoreCarrierLinksState(UnitCarrierLinksState state, Func<int, ArmyData> resolveArmy)
        {
            if (state == null || state.AirfieldArmyId.HasValue && state.AirfieldArmyId == state.FlightDeckArmyId)
                throw new ArgumentException("Invalid carrier container links.");
            var airfield = state.AirfieldArmyId.HasValue ? StateReference.Army(state.AirfieldArmyId.Value, resolveArmy) : null;
            var deck = state.FlightDeckArmyId.HasValue ? StateReference.Army(state.FlightDeckArmyId.Value, resolveArmy) : null;
            CarrierAirfield = airfield; CarrierFlightDeck = deck;
        }
        public UnitMovementState CaptureMovementState() => new UnitMovementState
        {
            PermanentMaximum = _moveMax, LegacyCurrent = _moveCurrent, Spent = _moveSpent,
            TransportCarrierId = _transportCarrier?.RuntimeId, TransportArmyId = _transportArmy?.Id,
        };
        public void RestoreMovementState(UnitMovementState state, Func<int, UnitData> resolveUnit, Func<int, ArmyData> resolveArmy)
        {
            if (state == null || state.PermanentMaximum < 0 || state.Spent < 0
                || state.TransportCarrierId.HasValue != state.TransportArmyId.HasValue)
                throw new ArgumentException("Invalid movement state.");
            var carrier = state.TransportCarrierId.HasValue ? StateReference.Unit(state.TransportCarrierId.Value, resolveUnit) : null;
            var army = state.TransportArmyId.HasValue ? StateReference.Army(state.TransportArmyId.Value, resolveArmy) : null;
            _moveMax = state.PermanentMaximum; _moveCurrent = state.LegacyCurrent; _moveSpent = state.Spent;
            _transportCarrier = carrier; _transportArmy = army;
        }
    }
}
