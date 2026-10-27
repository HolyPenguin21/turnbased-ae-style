using System;
using System.Collections.Generic;
using Game.HexGrid;
using Game.Players;

namespace Game.Map
{
    // Keys belong to the future appearance codec. TypeKey identifies the marker hierarchy;
    // SnapshotKey identifies its COMPLETE frozen renderer state, not a live entity/prefab id.
    [Serializable] public sealed class MapVisualSnapshotReference
    {
        public string TypeKey;
        public string SnapshotKey;
    }
    [Serializable] public sealed class RememberedBuildingVisualState
    {
        public string ViewerId;
        public HexCoord Hex;
        public MapVisualSnapshotReference Appearance;
    }
    [Serializable] public sealed class RememberedArmyVisualState
    {
        public string ViewerId;
        public int SourceArmyId;
        public HexCoord Hex;
        public MapVisualSnapshotReference Appearance;
    }
    [Serializable] public sealed class RememberedMapVisualState
    {
        public List<RememberedBuildingVisualState> Buildings = new List<RememberedBuildingVisualState>();
        public List<RememberedArmyVisualState> Armies = new List<RememberedArmyVisualState>();
    }

    public partial class HexSelectionController
    {
        // The callback must encode the received FROZEN clone's hierarchy, root pose/scale,
        // sprites, tints, materials, sorting and renderer poses, including its historical flag.
        // It must not resolve a current building/army and derive appearance from that entity.
        public RememberedMapVisualState CaptureRememberedVisuals(
            Func<MapObjectVisual, MapVisualSnapshotReference> captureFrozenAppearance)
        {
            if (captureFrozenAppearance == null) throw new ArgumentNullException(nameof(captureFrozenAppearance));
            ValidateRememberedVisualCoverage();
            var state = new RememberedMapVisualState();
            HumanVisualMemory.ForEachRememberedBuilding((viewer, hex) =>
                state.Buildings.Add(new RememberedBuildingVisualState
                {
                    ViewerId = StateReference.PlayerId(viewer), Hex = hex,
                    Appearance = CaptureAppearance(_rememberedBuildingVisuals[viewer][hex], captureFrozenAppearance)
                }));
            HumanVisualMemory.ForEachRememberedArmy((viewer, sighting) =>
                state.Armies.Add(new RememberedArmyVisualState
                {
                    ViewerId = StateReference.PlayerId(viewer), SourceArmyId = sighting.SourceArmyId, Hex = sighting.Hex,
                    Appearance = CaptureAppearance(_rememberedArmyVisuals[viewer][sighting.SourceArmyId].Visual, captureFrozenAppearance)
                }));
            return state;
        }

        private static MapVisualSnapshotReference CaptureAppearance(MapObjectVisual visual,
            Func<MapObjectVisual, MapVisualSnapshotReference> capture)
        {
            var reference = capture(visual);
            ValidateAppearanceReference(reference);
            return new MapVisualSnapshotReference { TypeKey = reference.TypeKey, SnapshotKey = reference.SnapshotKey };
        }
        private static void ValidateAppearanceReference(MapVisualSnapshotReference reference)
        {
            if (string.IsNullOrEmpty(reference?.TypeKey) || string.IsNullOrEmpty(reference.SnapshotKey))
                throw new ArgumentException("A remembered marker requires a frozen appearance type and snapshot key.");
        }

        // Run after HumanVisualMemory.RestoreState. Decode from saved appearance data/assets
        // only; each decoder result must be a detached marker frozen with
        // MapObjectVisual.FreezeRememberedAppearance. We clone it and retain no codec ownership.
        // Missing/extra appearances and accidental live markers are errors, never a fallback to
        // today's hidden world. All keys/templates are validated before replacing old caches.
        // Import only binds caches and their remembered layout; the normal final turn-state
        // reconciliation decides visibility once the entire restored scene is ready.
        public void RestoreRememberedVisuals(RememberedMapVisualState state,
            Func<string, PlayerSetupData> resolvePlayer,
            Func<MapVisualSnapshotReference, MapObjectVisual> resolveFrozenAppearance)
        {
            if (state?.Buildings == null || state.Armies == null || resolveFrozenAppearance == null)
                throw new ArgumentException("Missing remembered marker state or appearance decoder.");
            var expectedBuildings = new HashSet<(PlayerSetupData, HexCoord)>();
            var expectedArmies = new Dictionary<(PlayerSetupData, int), HumanVisualMemory.ArmySighting>();
            HumanVisualMemory.ForEachRememberedBuilding((viewer, hex) => expectedBuildings.Add((viewer, hex)));
            HumanVisualMemory.ForEachRememberedArmy((viewer, sighting) => expectedArmies.Add((viewer, sighting.SourceArmyId), sighting));
            var buildings = new List<(PlayerSetupData Viewer, HexCoord Hex, MapObjectVisual Template)>();
            var armies = new List<(PlayerSetupData Viewer, HumanVisualMemory.ArmySighting Sighting, MapObjectVisual Template)>();
            foreach (var entry in state.Buildings)
            {
                if (entry == null) throw new ArgumentException("Missing remembered building marker.");
                var viewer = StateReference.Player(entry.ViewerId, resolvePlayer);
                if (!expectedBuildings.Remove((viewer, entry.Hex)))
                    throw new ArgumentException("Unexpected or duplicate remembered building marker.");
                buildings.Add((viewer, entry.Hex, DecodeAppearance(entry.Appearance, resolveFrozenAppearance)));
            }
            foreach (var entry in state.Armies)
            {
                if (entry == null) throw new ArgumentException("Missing remembered army marker.");
                var viewer = StateReference.Player(entry.ViewerId, resolvePlayer);
                if (!expectedArmies.TryGetValue((viewer, entry.SourceArmyId), out var sighting) || !sighting.Hex.Equals(entry.Hex))
                    throw new ArgumentException("Unexpected, duplicate or displaced remembered army marker.");
                expectedArmies.Remove((viewer, entry.SourceArmyId));
                armies.Add((viewer, sighting, DecodeAppearance(entry.Appearance, resolveFrozenAppearance)));
            }
            if (expectedBuildings.Count != 0 || expectedArmies.Count != 0)
                throw new ArgumentException("Saved knowledge is missing a frozen marker appearance.");

            var buildingVisuals = new Dictionary<PlayerSetupData, Dictionary<HexCoord, MapObjectVisual>>();
            var armyVisuals = new Dictionary<PlayerSetupData, Dictionary<int, RememberedArmyVisual>>();
            var created = new List<MapObjectVisual>();
            try
            {
                foreach (var entry in buildings)
                {
                    var visual = entry.Template.CreateFrozenSnapshot(transform); created.Add(visual);
                    if (!buildingVisuals.TryGetValue(entry.Viewer, out var byHex))
                        buildingVisuals.Add(entry.Viewer, byHex = new Dictionary<HexCoord, MapObjectVisual>());
                    byHex.Add(entry.Hex, visual);
                }
                foreach (var entry in armies)
                {
                    var visual = entry.Template.CreateFrozenSnapshot(transform); created.Add(visual);
                    if (!armyVisuals.TryGetValue(entry.Viewer, out var byId))
                        armyVisuals.Add(entry.Viewer, byId = new Dictionary<int, RememberedArmyVisual>());
                    byId.Add(entry.Sighting.SourceArmyId, new RememberedArmyVisual
                        { Hex = entry.Sighting.Hex, Snapshot = entry.Sighting.Army, Visual = visual });
                }
            }
            catch
            {
                foreach (var visual in created) if (visual != null) Destroy(visual.gameObject);
                throw;
            }
            SetRememberedBuildingVisualsVisible(false); SetRememberedArmyVisualsVisible(false);
            foreach (var byHex in _rememberedBuildingVisuals.Values)
                foreach (var visual in byHex.Values) if (visual != null) Destroy(visual.gameObject);
            foreach (var byId in _rememberedArmyVisuals.Values)
                foreach (var remembered in byId.Values) if (remembered.Visual != null) Destroy(remembered.Visual.gameObject);
            _rememberedBuildingVisuals.Clear(); _rememberedBuildingSources.Clear();
            _rememberedArmyVisuals.Clear(); _rememberedArmyVisualsByHex.Clear();
            foreach (var entry in buildingVisuals) _rememberedBuildingVisuals.Add(entry.Key, entry.Value);
            foreach (var entry in armyVisuals)
            {
                _rememberedArmyVisuals.Add(entry.Key, entry.Value);
                foreach (var remembered in entry.Value.Values) AddRememberedArmyToHexIndex(entry.Key, remembered);
            }
            var layoutHexes = new HashSet<(PlayerSetupData, HexCoord)>();
            foreach (var entry in buildings) layoutHexes.Add((entry.Viewer, entry.Hex));
            foreach (var entry in armies) layoutHexes.Add((entry.Viewer, entry.Sighting.Hex));
            foreach (var entry in layoutHexes) ReapplyRememberedLayout(entry.Item1, entry.Item2);
        }

        private static MapObjectVisual DecodeAppearance(MapVisualSnapshotReference reference,
            Func<MapVisualSnapshotReference, MapObjectVisual> decode)
        {
            ValidateAppearanceReference(reference);
            var template = decode(reference);
            if (template == null || !template.IsFrozenRememberedAppearance)
                throw new ArgumentException("Appearance decoder must return a frozen remembered marker, never a live-world marker.");
            return template;
        }

        // The restore coordinator calls this even if its source imported no appearances, so
        // restoring logical knowledge alone cannot silently make historical markers disappear.
        public void ValidateRememberedVisualCoverage()
        {
            HumanVisualMemory.ForEachRememberedBuilding((viewer, hex) =>
            {
                if (!_rememberedBuildingVisuals.TryGetValue(viewer, out var visuals)
                    || !visuals.TryGetValue(hex, out var visual) || visual == null || !visual.IsFrozenRememberedAppearance)
                    throw new InvalidOperationException("Known building has no frozen remembered appearance.");
            });
            HumanVisualMemory.ForEachRememberedArmy((viewer, sighting) =>
            {
                if (!_rememberedArmyVisuals.TryGetValue(viewer, out var visuals)
                    || !visuals.TryGetValue(sighting.SourceArmyId, out var remembered)
                    || remembered == null || !remembered.Hex.Equals(sighting.Hex) || !ReferenceEquals(remembered.Snapshot, sighting.Army)
                    || remembered.Visual == null || !remembered.Visual.IsFrozenRememberedAppearance)
                    throw new InvalidOperationException("Army sighting has no matching frozen remembered appearance.");
            });
        }
    }
}
