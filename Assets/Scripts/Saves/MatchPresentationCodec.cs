using System;
using System.Collections.Generic;
using Game.Cards;
using Game.Core;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Setup;
using Game.Units;
using UnityEngine;

namespace Game.Saves
{
    internal static partial class MatchPresentationCodec
    {
        internal static MatchPresentationData Capture(HexSelectionController selection, GameConfig config,
            Action<ArmyData> includeArmy, Action<UnitData> includeUnit, Action<CardData> includeCard,
            Action<PlayerSetupData> includePlayer = null)
        {
            if (selection == null || config == null) throw new ArgumentNullException("Missing presentation capture owner.");
            // Historical display armies do not enter the live entity graph. Their old
            // owners still belong to the player graph, including a neutral owner whose last
            // live army/event was removed while this observation remained behind fog.
            if (VisionSystem.CurrentViewer != null) includePlayer?.Invoke(VisionSystem.CurrentViewer);
            HumanVisualMemory.ForEachRememberedBuilding((viewer, hex) => includePlayer?.Invoke(viewer));
            HumanVisualMemory.ForEachRememberedArmy((viewer, sighting) =>
            {
                includePlayer?.Invoke(viewer);
                includePlayer?.Invoke(sighting.Army.Owner);
                foreach (var member in sighting.Army.Members)
                {
                    includePlayer?.Invoke(member.Owner);
                    if (member.CapturedFrom != null) includePlayer?.Invoke(member.CapturedFrom);
                }
            });
            var result = new MatchPresentationData();
            var vision = VisionSystem.CaptureState();
            result.CurrentViewerId = vision.CurrentViewerId;
            foreach (var state in vision.Players)
                result.Vision.Add(new MatchVisionData { PlayerId = state.PlayerId, Visible = Hexes(state.Visible),
                    Visited = Hexes(state.Visited), EverSeen = Hexes(state.EverSeen) });
            var snapshots = new Dictionary<string, ArmyData>();
            int next = 0;
            var memory = HumanVisualMemory.CaptureState(army =>
            {
                string key = (++next).ToString(System.Globalization.CultureInfo.InvariantCulture);
                snapshots.Add(key, army); return key;
            });
            result.ObserverMatch = memory.ObserverMatch;
            foreach (var state in memory.Players)
            {
                var entry = new MatchHumanMemoryData { PlayerId = state.PlayerId, Buildings = Hexes(state.KnownBuildings) };
                foreach (var sighting in state.ArmySightings)
                    entry.Armies.Add(CaptureArmy(sighting.SourceArmyId, sighting.Hex, snapshots[sighting.DisplaySnapshotKey], config));
                result.Memory.Add(entry);
            }
            var frozen = new Dictionary<string, MatchFrozenVisualData>();
            var references = selection.CaptureRememberedVisuals(visual =>
            {
                string key = (++next).ToString(System.Globalization.CultureInfo.InvariantCulture);
                frozen.Add(key, CaptureVisual(visual));
                return new MapVisualSnapshotReference { TypeKey = "frozen-tree-v1", SnapshotKey = key };
            });
            foreach (var state in references.Buildings)
            {
                var item = frozen[state.Appearance.SnapshotKey]; item.ViewerId = state.ViewerId;
                item.Q = state.Hex.Q; item.R = state.Hex.R; result.Visuals.Add(item);
            }
            foreach (var state in references.Armies)
            {
                var item = frozen[state.Appearance.SnapshotKey]; item.ViewerId = state.ViewerId; item.IsArmy = true;
                item.SourceArmyId = state.SourceArmyId; item.Q = state.Hex.Q; item.R = state.Hex.R; result.Visuals.Add(item);
            }
            return result;
        }
        private static List<MatchHexData> Hexes(IEnumerable<HexCoord> source)
        {
            var result = new List<MatchHexData>();
            foreach (var hex in source) result.Add(new MatchHexData { Q = hex.Q, R = hex.R });
            return result;
        }
        private static List<HexCoord> Hexes(IEnumerable<MatchHexData> source)
        {
            var result = new List<HexCoord>();
            foreach (var hex in source) result.Add(new HexCoord(hex.Q, hex.R));
            return result;
        }
        private static MatchRememberedArmyData CaptureArmy(int sourceId, HexCoord hex, ArmyData army, GameConfig config)
        {
            if (army == null || army.Id != -1 || ArmyRegistry.IsRegistered(army)) throw new ArgumentException("Expected historical display army.");
            var data = new MatchRememberedArmyData { SourceArmyId = sourceId, Q = hex.Q, R = hex.R,
                OwnerId = StateReference.PlayerId(army.Owner), Name = army.Name, IsGarrison = army.IsGarrison,
                IsPrison = army.IsPrison, IsAirfield = army.IsAirfield, IsAirArmy = army.IsAirArmy, Activated = army.HasActivatedThisTurn,
                HasDefenseBonus = army.VisualSnapshotDefenseBonus.HasValue, DefenseBonus = army.VisualSnapshotDefenseBonus.GetValueOrDefault(),
                HasConstructionDefense = army.VisualSnapshotConstructionDefense.HasValue, ConstructionDefense = army.VisualSnapshotConstructionDefense.GetValueOrDefault(),
                HasCurrentMovement = army.VisualSnapshotCurrentMovement.HasValue, CurrentMovement = army.VisualSnapshotCurrentMovement.GetValueOrDefault(),
                HasMaxMovement = army.VisualSnapshotMaxMovement.HasValue, MaxMovement = army.VisualSnapshotMaxMovement.GetValueOrDefault() };
            foreach (var unit in army.Members)
            {
                var entry = new MatchRememberedUnitData { RuntimeId = unit.RuntimeId, Name = unit.Name, OwnerId = StateReference.PlayerId(unit.Owner),
                    CapturedFromId = unit.CapturedFrom == null ? null : StateReference.PlayerId(unit.CapturedFrom),
                    ArtKey = MatchSaveAssets.SpriteKey(unit.Art), DetailArtKey = MatchSaveAssets.SpriteKey(unit.DetailArt),
                    EquipmentKey = CardKey(unit.Equipment, config), MutatorKey = CardKey(unit.Mutator, config),
                    BerserkStacks = unit.BerserkStacks, BerserkDefenseLost = unit.BerserkDefenseLost, DrillAttackBonus = unit.DrillAttackBonus,
                    MoveMax = unit.MoveMax, MoveCurrent = unit.MoveCurrent, ActivationApCost = unit.ActivationApCost, ApCost = unit.ApCost,
                    HasOriginalCost = unit.OriginalResourceCost != null, HasRepairCost = unit.RepairResourceCost != null,
                    OriginalCost = Cost(unit.OriginalResourceCost) ?? new ResourceCost(), RepairCost = Cost(unit.RepairResourceCost) ?? new ResourceCost(), IsHero = unit.IsHero,
                    IsPrisoner = unit.IsPrisoner, IsAviation = unit.IsAviation, CommandRating = unit.CommandRating,
                    Fate = unit.Fate, FateMax = unit.FateMax, Attack = unit.Attack, Defense = unit.Defense, Range = unit.Range,
                    HitPointsMax = unit.HitPointsMax, HitPointsCurrent = unit.HitPointsCurrent, Row = (int)unit.Row,
                    Initiative = unit.Initiative, AirfieldCapacity = unit.AirfieldCapacity };
                entry.Abilities.AddRange(unit.Abilities);
                foreach (var tag in unit.TypeTags) entry.TypeTags.Add((int)tag);
                data.Members.Add(entry);
            }
            return data;
        }
        private static ResourceCost Cost(ResourceCost value) => value == null ? null : new ResourceCost
            { human = value.human, energy = value.energy, materials = value.materials, tech = value.tech };
        private static CardDefinition Card(string key, GameConfig config)
        {
            if (string.IsNullOrEmpty(key)) return null;
            CardDefinition result = null;
            if (config?.collectionDeckCatalog?.catalogs != null)
                result = FactionCardCatalog.ResolveAcross(config.collectionDeckCatalog.catalogs, key, false);
            if (config?.extractionFacilityCards != null) foreach (var card in config.extractionFacilityCards)
                if (card != null && card.authoredKey == key)
                {
                    if (result != null && !ReferenceEquals(card, result)) throw new ArgumentException("Duplicate authored card: " + key);
                    result = card;
                }
            return result ?? throw new ArgumentException("Unresolved historical card definition: " + key);
        }
        private static string CardKey(CardDefinition card, GameConfig config)
        {
            if (card == null) return null;
            if (string.IsNullOrEmpty(card.authoredKey) || !ReferenceEquals(Card(card.authoredKey, config), card))
                throw new ArgumentException("Historical card requires a canonical authored identity.");
            return card.authoredKey;
        }
        internal static void Validate(MatchPresentationData data, GameConfig config,
            Func<string, PlayerSetupData> player, HexMapLayout layout, MatchIdentityState identity)
        {
            if (data?.Vision == null || data.Memory == null || data.Visuals == null || config == null
                || layout?.Cells == null || identity == null) throw new ArgumentException("Missing presentation graph.");
            identity.Validate();
            var map = new HashSet<HexCoord>();
            foreach (var cell in layout.Cells)
                if (cell != null) map.Add(new HexCoord(cell.Q, cell.R));
            var viewers = new Dictionary<string, HashSet<HexCoord>>(StringComparer.Ordinal);
            foreach (var entry in data.Vision)
            {
                if (entry == null) throw new ArgumentException("Missing vision entry.");
                StateReference.Player(entry.PlayerId, player);
                if (viewers.ContainsKey(entry.PlayerId)) throw new ArgumentException("Duplicate vision player.");
                var visible = ValidateHexes(entry.Visible, null);
                var visited = ValidateHexes(entry.Visited, map);
                var seen = ValidateHexes(entry.EverSeen, null);
                if (!visible.IsSubsetOf(seen) || !visited.IsSubsetOf(seen)) throw new ArgumentException("Invalid vision tiers.");
                viewers.Add(entry.PlayerId, seen);
            }
            if (!string.IsNullOrEmpty(data.CurrentViewerId))
            {
                StateReference.Player(data.CurrentViewerId, player);
                if (!viewers.ContainsKey(data.CurrentViewerId)) throw new ArgumentException("Current viewer has no vision state.");
            }
            var memoryPlayers = new HashSet<string>(StringComparer.Ordinal);
            var expected = new HashSet<(string, bool, int, int, int)>();
            foreach (var entry in data.Memory)
            {
                if (entry?.Buildings == null || entry.Armies == null) throw new ArgumentException("Missing human memory entry.");
                var owner = StateReference.Player(entry.PlayerId, player);
                if (owner.IsNeutral || !(owner.IsHuman || data.ObserverMatch) || !memoryPlayers.Add(entry.PlayerId)
                    || !viewers.TryGetValue(entry.PlayerId, out var seen)) throw new ArgumentException("Invalid human memory viewer.");
                foreach (var hex in ValidateHexes(entry.Buildings, map))
                {
                    if (!seen.Contains(hex)) throw new ArgumentException("Remembered building outside observed map.");
                    expected.Add((entry.PlayerId, false, 0, hex.Q, hex.R));
                }
                var sourceIds = new HashSet<int>();
                foreach (var army in entry.Armies)
                {
                    if (army == null || army.Members == null || army.SourceArmyId < 0 || army.SourceArmyId > identity.ArmyHighWaterMark
                        || !sourceIds.Add(army.SourceArmyId) || !map.Contains(new HexCoord(army.Q, army.R)) || !seen.Contains(new HexCoord(army.Q, army.R)))
                        throw new ArgumentException("Invalid historical army identity or location.");
                    StateReference.Player(army.OwnerId, player);
                    if (army.HasCurrentMovement && army.CurrentMovement < 0 || army.HasMaxMovement && army.MaxMovement < 0)
                        throw new ArgumentException("Invalid historical army movement.");
                    var unitIds = new HashSet<int>();
                    foreach (var unit in army.Members)
                    {
                        if (unit == null || unit.RuntimeId <= 0 || unit.RuntimeId > identity.UnitHighWaterMark || !unitIds.Add(unit.RuntimeId)
                            || unit.Abilities == null || unit.TypeTags == null || !Enum.IsDefined(typeof(BattleRow), unit.Row)
                            || unit.MoveMax < 0 || unit.MoveCurrent < 0 || unit.ActivationApCost < 0 || unit.AirfieldCapacity < 0)
                            throw new ArgumentException("Invalid historical unit.");
                        StateReference.Player(unit.OwnerId, player);
                        if (!string.IsNullOrEmpty(unit.CapturedFromId)) StateReference.Player(unit.CapturedFromId, player);
                        MatchSaveAssets.ResolveSprite(unit.ArtKey); MatchSaveAssets.ResolveSprite(unit.DetailArtKey);
                        Card(unit.EquipmentKey, config); Card(unit.MutatorKey, config);
                        ValidateCost(unit.OriginalCost); ValidateCost(unit.RepairCost);
                        var abilities = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var ability in unit.Abilities)
                            if (string.IsNullOrEmpty(ability) || !abilities.Add(ability)) throw new ArgumentException("Invalid historical ability.");
                        var tags = new HashSet<int>();
                        foreach (int tag in unit.TypeTags)
                            if (!Enum.IsDefined(typeof(UnitTypeTag), tag) || !tags.Add(tag)) throw new ArgumentException("Invalid historical type tag.");
                    }
                    expected.Add((entry.PlayerId, true, army.SourceArmyId, army.Q, army.R));
                }
            }
            foreach (var visual in data.Visuals)
            {
                if (visual == null || !expected.Remove((visual.ViewerId, visual.IsArmy, visual.IsArmy ? visual.SourceArmyId : 0, visual.Q, visual.R)))
                    throw new ArgumentException("Unexpected or duplicate remembered appearance.");
                ValidateVisual(visual);
            }
            if (expected.Count != 0) throw new ArgumentException("Missing frozen remembered appearance coverage.");
        }
        private static HashSet<HexCoord> ValidateHexes(List<MatchHexData> values, HashSet<HexCoord> map)
        {
            if (values == null) throw new ArgumentException("Missing vision hex list.");
            var result = new HashSet<HexCoord>();
            foreach (var value in values)
                if (value == null || (map != null && !map.Contains(new HexCoord(value.Q, value.R))) || Math.Abs((long)value.Q) > 1000000 || Math.Abs((long)value.R) > 1000000 || !result.Add(new HexCoord(value.Q, value.R)))
                    throw new ArgumentException("Unknown or duplicate knowledge hex.");
            return result;
        }
        private static void ValidateCost(ResourceCost value)
        {
            if (value == null || value.human < 0 || value.energy < 0 || value.materials < 0 || value.tech < 0)
                throw new ArgumentException("Invalid historical resource cost.");
        }
        internal static void RestoreKnowledge(MatchPresentationData data, GameConfig config, Func<string, PlayerSetupData> player)
        {
            var vision = new VisionState { CurrentViewerId = string.IsNullOrEmpty(data.CurrentViewerId) ? null : data.CurrentViewerId };
            foreach (var entry in data.Vision)
                vision.Players.Add(new PlayerVisionState { PlayerId = entry.PlayerId, Visible = Hexes(entry.Visible),
                    Visited = Hexes(entry.Visited), EverSeen = Hexes(entry.EverSeen) });
            var memory = new HumanVisualMemoryState { ObserverMatch = data.ObserverMatch };
            var snapshots = new Dictionary<string, ArmyData>(); int next = 0;
            foreach (var entry in data.Memory)
            {
                var state = new HumanPlayerMemoryState { PlayerId = entry.PlayerId, KnownBuildings = Hexes(entry.Buildings) };
                foreach (var army in entry.Armies)
                {
                    string key = (++next).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    snapshots.Add(key, RestoreArmy(army, config, player));
                    state.ArmySightings.Add(new HumanArmySightingState { SourceArmyId = army.SourceArmyId,
                        Hex = new HexCoord(army.Q, army.R), DisplaySnapshotKey = key });
                }
                memory.Players.Add(state);
            }
            VisionSystem.RestoreState(vision, player);
            HumanVisualMemory.RestoreState(memory, player, key => snapshots[key]);
        }
        private static ArmyData RestoreArmy(MatchRememberedArmyData data, GameConfig config, Func<string, PlayerSetupData> player)
        {
            var army = ArmyData.CreateVisualSnapshot();
            army.Name = data.Name; army.Hex = new HexCoord(data.Q, data.R); army.Owner = StateReference.Player(data.OwnerId, player);
            army.IsGarrison = data.IsGarrison; army.IsPrison = data.IsPrison; army.IsAirfield = data.IsAirfield; army.IsAirArmy = data.IsAirArmy;
            army.VisualSnapshotDefenseBonus = data.HasDefenseBonus ? data.DefenseBonus : (int?)null;
            army.VisualSnapshotConstructionDefense = data.HasConstructionDefense ? data.ConstructionDefense : (int?)null;
            army.VisualSnapshotCurrentMovement = data.HasCurrentMovement ? data.CurrentMovement : (int?)null;
            army.VisualSnapshotMaxMovement = data.HasMaxMovement ? data.MaxMovement : (int?)null;
            foreach (var entry in data.Members)
            {
                var unit = UnitData.CreateProjection(entry.RuntimeId);
                unit.Name = entry.Name; unit.Owner = StateReference.Player(entry.OwnerId, player);
                unit.CapturedFrom = string.IsNullOrEmpty(entry.CapturedFromId) ? null : StateReference.Player(entry.CapturedFromId, player);
                unit.Art = MatchSaveAssets.ResolveSprite(entry.ArtKey); unit.DetailArt = MatchSaveAssets.ResolveSprite(entry.DetailArtKey);
                unit.Equipment = Card(entry.EquipmentKey, config); unit.Mutator = Card(entry.MutatorKey, config);
                unit.BerserkStacks = entry.BerserkStacks; unit.BerserkDefenseLost = entry.BerserkDefenseLost; unit.DrillAttackBonus = entry.DrillAttackBonus;
                unit.MoveMax = entry.MoveMax; unit.MoveCurrent = entry.MoveCurrent; unit.ActivationApCost = entry.ActivationApCost; unit.ApCost = entry.ApCost;
                unit.OriginalResourceCost = entry.HasOriginalCost ? Cost(entry.OriginalCost) : null; unit.RepairResourceCost = entry.HasRepairCost ? Cost(entry.RepairCost) : null;
                unit.IsHero = entry.IsHero; unit.IsPrisoner = entry.IsPrisoner; unit.IsAviation = entry.IsAviation;
                unit.CommandRating = entry.CommandRating; unit.Fate = entry.Fate; unit.FateMax = entry.FateMax;
                unit.Attack = entry.Attack; unit.Defense = entry.Defense; unit.Range = entry.Range; unit.HitPointsMax = entry.HitPointsMax;
                unit.HitPointsCurrent = entry.HitPointsCurrent; unit.Row = (BattleRow)entry.Row; unit.Initiative = entry.Initiative;
                unit.AirfieldCapacity = entry.AirfieldCapacity;
                unit.Abilities.UnionWith(entry.Abilities);
                foreach (int tag in entry.TypeTags) unit.TypeTags.Add((UnitTypeTag)tag);
                army.Members.Add(unit);
            }
            if (data.Activated) army.MarkActivated();
            return army;
        }
    }
}
