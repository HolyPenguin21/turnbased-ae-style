using System.Collections.Generic;
using Game.Cards;
using Game.HexGrid;
using Game.Players;
using Game.Units;

namespace Game.Map
{
    // Player-facing fog memory only (humans, and every player in an AI-vs-AI observer match). This deliberately does not feed AI decisions; AiMapMemory
    // remains the sole source for those. A moving enemy is stored as an immutable last-observed
    // position/roster until the observing human's own next turn ends. Stationary neutrals and
    // buildings remain known until corrected by another observation, never by hidden live data.
    public static partial class HumanVisualMemory
    {
        public sealed class ArmySighting
        {
            public int SourceArmyId { get; }
            public HexCoord Hex { get; }
            public ObservedArmy Observed { get; }
            public ArmyData Army => Observed.CreateDisplayArmy();

            internal ArmySighting(int sourceArmyId, HexCoord hex, ObservedArmy army)
            {
                SourceArmyId = sourceArmyId;
                Hex = hex;
                Observed = army;
            }
        }

        private static readonly Dictionary<PlayerSetupData, Dictionary<int, ArmySighting>> ArmySightings =
            new Dictionary<PlayerSetupData, Dictionary<int, ArmySighting>>();
        private static readonly Dictionary<PlayerSetupData, HashSet<HexCoord>> KnownBuildingHexes =
            new Dictionary<PlayerSetupData, HashSet<HexCoord>>();

        // True for a match with no human at all (AI-vs-AI observer): every player can then be the
        // map viewer in turn (VisionSystem.CurrentViewer), so each of them keeps the same
        // last-seen memory a human does. Set by GameTurnController.BeginGame.
        public static bool ObserverMatch { get; set; }

        public static bool Tracks(PlayerSetupData viewer)
        {
            return viewer != null && !viewer.IsNeutral && (viewer.IsHuman || ObserverMatch);
        }

        public static void Clear()
        {
            ArmySightings.Clear();
            KnownBuildingHexes.Clear();
            ObserverMatch = false;
        }

        public static void ObserveArmy(PlayerSetupData viewer, ArmyData army, HexCoord observedHex)
        {
            if (Game.Setup.MatchRestoreScope.IsRestoring || !Tracks(viewer) || army == null)
                return;
            // An army entirely hidden from this viewer (stealth) was never observed: remembering
            // it would plant an empty-roster "last seen" marker once it walks into fog.
            if (StealthSystem.ArmyFullyHiddenFrom(army, viewer))
                return;
            StoreObservation(viewer, army.Id, ObservedArmy.Capture(army, observedHex, viewer));
        }
        internal static void StoreObservation(PlayerSetupData viewer, int sourceArmyId, ObservedArmy observed)
        {
            if (Game.Setup.MatchRestoreScope.IsRestoring || !Tracks(viewer) || observed == null) return;
            if (!ArmySightings.TryGetValue(viewer, out Dictionary<int, ArmySighting> sightings))
                ArmySightings[viewer] = sightings = new Dictionary<int, ArmySighting>();
            sightings[sourceArmyId] = new ArmySighting(sourceArmyId, observed.Hex, observed);
        }

        public static bool TryGetArmySighting(PlayerSetupData viewer, int armyId, out ArmySighting sighting)
        {
            sighting = null;
            return Tracks(viewer)
                && ArmySightings.TryGetValue(viewer, out Dictionary<int, ArmySighting> sightings)
                && sightings.TryGetValue(armyId, out sighting);
        }

        public static void ReconcileVisibleHex(PlayerSetupData viewer, HexCoord hex, IEnumerable<int> armyIdsPresent)
        {
            if (!Tracks(viewer)
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
            {
                if (ArmySightings.TryGetValue(viewer, out var sightings))
                {
                    var expired = new List<int>();
                    foreach (var entry in sightings)
                        if (entry.Value.Observed.Owner?.IsNeutral != true) expired.Add(entry.Key);
                    foreach (int id in expired) sightings.Remove(id);
                    if (sightings.Count == 0) ArmySightings.Remove(viewer);
                }
                MapObservationSystem.ExpireArmies(viewer);
            }
        }

        internal static UnitData CopyObservedUnit(UnitData source)
        {
            var snapshot = UnitData.CreateProjection(source.RuntimeId);
            CopySnapshotUnit(source, snapshot);
            return snapshot;
        }

        private static void CopySnapshotUnit(UnitData source, UnitData snapshot)
        {
            // Visual copies retain references to authored art/definitions only.
            snapshot.Name = source.Name;
            snapshot.Owner = source.Owner;
            snapshot.BerserkStacks = source.BerserkStacks;
            snapshot.BerserkDefenseLost = source.BerserkDefenseLost;
            snapshot.DrillAttackBonus = source.DrillAttackBonus;
            snapshot.MoveMax = source.MoveMax;
            // Display projections store usable MP directly; no live sortie/transport links
            // are replayed when this historical unit is read or restored.
            snapshot.MoveCurrent = Game.Aviation.AviationRules.EffectiveMoveCurrent(source);
            snapshot.ActivationApCost = source.ActivationApCost;
            snapshot.ApCost = source.ApCost;
            snapshot.OriginalResourceCost = SnapshotResourceCost(source.OriginalResourceCost);
            snapshot.RepairResourceCost = SnapshotResourceCost(source.RepairResourceCost);
            snapshot.IsHero = source.IsHero;
            snapshot.CommandRating = source.CommandRating;
            snapshot.Fate = source.Fate;
            snapshot.FateMax = source.FateMax;
            snapshot.Art = source.Art;
            snapshot.DetailArt = source.DetailArt;
            snapshot.Equipment = source.Equipment;
            snapshot.Mutator = source.Mutator;
            snapshot.Attack = source.Attack;
            snapshot.Defense = source.Defense;
            snapshot.Range = source.Range;
            snapshot.HitPointsMax = source.HitPointsMax;
            snapshot.HitPointsCurrent = source.HitPointsCurrent;
            snapshot.Row = source.Row;
            snapshot.Initiative = source.Initiative;
            snapshot.IsPrisoner = source.IsPrisoner;
            snapshot.CapturedFrom = source.CapturedFrom;
            // AviationRules.IsAirArmy reads this per member to classify the containing
            // ArmyData snapshot — without it a remembered air army's own members would all
            // read as ordinary ground units under fog. ObservedArmy separately captures
            // container flags that cannot be derived from composition.
            snapshot.IsAviation = source.IsAviation;
            snapshot.AirfieldCapacity = source.AirfieldCapacity;

            foreach (string ability in source.Abilities)
                snapshot.Abilities.Add(ability);
            foreach (UnitTypeTag tag in source.TypeTags)
                snapshot.TypeTags.Add(tag);
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
            if (Game.Setup.MatchRestoreScope.IsRestoring || !Tracks(viewer))
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
            return Tracks(viewer)
                && KnownBuildingHexes.TryGetValue(viewer, out HashSet<HexCoord> buildings)
                && buildings.Contains(hex);
        }
    }
}
