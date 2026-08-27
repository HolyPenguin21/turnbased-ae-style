using System.Collections.Generic;
using Game.Cards;
using Game.HexGrid;
using Game.Players;
using Game.Units;

namespace Game.Map
{
    // Player-facing fog memory only. This deliberately does not feed AI decisions; AiMapMemory
    // remains the sole source for those. A moving enemy is stored as an immutable last-observed
    // position/roster until the observing human's own next turn ends; stationary buildings
    // remain known until the same hex is observed again and found empty.
    public static class HumanVisualMemory
    {
        public sealed class ArmySighting
        {
            public int SourceArmyId { get; }
            public HexCoord Hex { get; }
            public ArmyData Army { get; }

            internal ArmySighting(int sourceArmyId, HexCoord hex, ArmyData army)
            {
                SourceArmyId = sourceArmyId;
                Hex = hex;
                Army = army;
            }
        }

        private static readonly Dictionary<PlayerSetupData, Dictionary<int, ArmySighting>> ArmySightings =
            new Dictionary<PlayerSetupData, Dictionary<int, ArmySighting>>();
        private static readonly Dictionary<PlayerSetupData, HashSet<HexCoord>> KnownBuildingHexes =
            new Dictionary<PlayerSetupData, HashSet<HexCoord>>();
        private static readonly HashSet<HexCoord> EmptyHexes = new HashSet<HexCoord>();

        public static void Clear()
        {
            ArmySightings.Clear();
            KnownBuildingHexes.Clear();
        }

        public static void ObserveArmy(PlayerSetupData viewer, ArmyData army, HexCoord observedHex)
        {
            if (viewer == null || !viewer.IsHuman || army == null)
                return;
            if (!ArmySightings.TryGetValue(viewer, out Dictionary<int, ArmySighting> sightings))
            {
                sightings = new Dictionary<int, ArmySighting>();
                ArmySightings[viewer] = sightings;
            }
            sightings[army.Id] = new ArmySighting(army.Id, observedHex, SnapshotArmy(army, observedHex, viewer));
        }

        public static bool TryGetArmySighting(PlayerSetupData viewer, int armyId, out ArmySighting sighting)
        {
            sighting = null;
            return viewer != null && viewer.IsHuman
                && ArmySightings.TryGetValue(viewer, out Dictionary<int, ArmySighting> sightings)
                && sightings.TryGetValue(armyId, out sighting);
        }

        public static void ReconcileVisibleHex(PlayerSetupData viewer, HexCoord hex, IEnumerable<int> armyIdsPresent)
        {
            if (viewer == null || !viewer.IsHuman
                || !ArmySightings.TryGetValue(viewer, out Dictionary<int, ArmySighting> sightings))
                return;

            var present = new HashSet<int>(armyIdsPresent ?? System.Array.Empty<int>());
            var stale = new List<int>();
            foreach (KeyValuePair<int, ArmySighting> entry in sightings)
                if (entry.Value.Hex.Equals(hex) && !present.Contains(entry.Key))
                    stale.Add(entry.Key);
            foreach (int armyId in stale)
                sightings.Remove(armyId);
        }

        public static void EndTurn(PlayerSetupData viewer)
        {
            if (viewer != null)
                ArmySightings.Remove(viewer);
        }

        private static ArmyData SnapshotArmy(ArmyData source, HexCoord observedHex, PlayerSetupData viewer)
        {
            ArmyData snapshot = ArmyData.CreateVisualSnapshot();
            snapshot.Name = source.Name;
            snapshot.Hex = observedHex;
            snapshot.Owner = source.Owner;
            snapshot.IsGarrison = source.IsGarrison;
            snapshot.IsPrison = source.IsPrison;
            // IsAirfield has no composition to recover it from (unlike IsAirArmy, which
            // AviationRules derives fresh from every member's own IsAviation flag below) — an
            // airfield can be empty, so it must be copied explicitly or a remembered one would
            // read back as a plain empty army.
            snapshot.IsAirfield = source.IsAirfield;
            snapshot.HasActivatedThisTurn = source.HasActivatedThisTurn;
            // Individual stealth (see Game.Map.StealthSystem): the "last seen" roster only
            // remembers the members this viewer could actually see — a member still hidden
            // from them was never part of what they observed.
            foreach (UnitData member in source.Members)
                if (!StealthSystem.IsHiddenFrom(member, viewer))
                    snapshot.Members.Add(SnapshotUnit(member));
            return snapshot;
        }

        private static UnitData SnapshotUnit(UnitData source)
        {
            var snapshot = new UnitData
            {
                Name = source.Name,
                Owner = source.Owner,
                BerserkStacks = source.BerserkStacks,
                BerserkDefenseLost = source.BerserkDefenseLost,
                MoveMax = source.MoveMax,
                MoveCurrent = source.MoveCurrent,
                ActivationApCost = source.ActivationApCost,
                ApCost = source.ApCost,
                OriginalResourceCost = SnapshotResourceCost(source.OriginalResourceCost),
                RepairResourceCost = SnapshotResourceCost(source.RepairResourceCost),
                IsHero = source.IsHero,
                CommandRating = source.CommandRating,
                Fate = source.Fate,
                FateMax = source.FateMax,
                Art = source.Art,
                DetailArt = source.DetailArt,
                Attack = source.Attack,
                Defense = source.Defense,
                Resistance = source.Resistance,
                Range = source.Range,
                HitPointsMax = source.HitPointsMax,
                HitPointsCurrent = source.HitPointsCurrent,
                Row = source.Row,
                Initiative = source.Initiative,
                IsPrisoner = source.IsPrisoner,
                CapturedFrom = source.CapturedFrom,
                // AviationRules.IsAirArmy reads this per member to classify the containing
                // ArmyData snapshot — without it a remembered air army's own members would all
                // read as ordinary ground units under fog (see SnapshotArmy's own IsAirfield
                // comment for the other half of this: that flag can't self-heal from composition).
                IsAviation = source.IsAviation,
            };
            foreach (string ability in source.Abilities)
                snapshot.Abilities.Add(ability);
            foreach (UnitTypeTag tag in source.TypeTags)
                snapshot.TypeTags.Add(tag);
            return snapshot;
        }

        private static ResourceCost SnapshotResourceCost(ResourceCost source)
        {
            return source == null ? null : new ResourceCost
            {
                human = source.human,
                energy = source.energy,
                materials = source.materials,
                tech = source.tech,
            };
        }

        public static void ObserveBuilding(PlayerSetupData viewer, HexCoord hex, bool exists)
        {
            if (viewer == null || !viewer.IsHuman)
                return;
            if (!KnownBuildingHexes.TryGetValue(viewer, out HashSet<HexCoord> buildings))
            {
                if (!exists)
                    return;
                buildings = new HashSet<HexCoord>();
                KnownBuildingHexes[viewer] = buildings;
            }

            if (exists)
                buildings.Add(hex);
            else
                buildings.Remove(hex);
        }

        public static bool IsBuildingKnown(PlayerSetupData viewer, HexCoord hex)
        {
            return viewer != null && viewer.IsHuman
                && KnownBuildingHexes.TryGetValue(viewer, out HashSet<HexCoord> buildings)
                && buildings.Contains(hex);
        }

        public static IEnumerable<HexCoord> BuildingsKnownBy(PlayerSetupData viewer)
        {
            return viewer != null && viewer.IsHuman
                && KnownBuildingHexes.TryGetValue(viewer, out HashSet<HexCoord> buildings)
                ? buildings
                : EmptyHexes;
        }
    }
}
