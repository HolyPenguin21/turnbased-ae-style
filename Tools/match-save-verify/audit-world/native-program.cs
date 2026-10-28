using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Game.Ai;
using Game.Aviation;
using Game.Cards;
using Game.Core;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Saves;

// Matches the full runtime integration runner's native construction adapter only.
// All ability parsing, save capture, transport and decode behavior is production code.
internal static class IntegrationNative
{
    internal static int Frame = 100;
    internal static MatchSaveAssetManifest Manifest;
    internal static T New<T>() where T : class
    {
        var value = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
        for (Type type = typeof(T); type != null && type.Assembly == typeof(GameConfig).Assembly; type = type.BaseType)
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                if (field.FieldType.IsGenericType && field.FieldType.GetConstructor(Type.EmptyTypes) != null)
                    field.SetValue(value, Activator.CreateInstance(field.FieldType));
        if (value is PlayerRoot root) root.RestoreEconomyState(new PlayerEconomyState());
        return value;
    }
}
internal static class WorldAuditProgram
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static int Main()
    {
        try
        {
            var config = IntegrationNative.New<GameConfig>();
            config.collectionDeckCatalog = IntegrationNative.New<StartingDeckCatalog>();
            config.facilityMarkerPrefab = IntegrationNative.New<MapObjectVisual>();
            var human = new PlayerSetupData { PlayerId = "h", IsHuman = true, Hand = new PlayerHandData(null, 0) };
            var ai = new PlayerSetupData { PlayerId = "a", ColorIndex = 1 };
            var unit = new Game.Units.UnitData { Owner = human, AntiAirRadius = 1 };
            unit.Abilities.Add("AA2"); unit.Abilities.Add("AA");
            var facility = new FacilityData { AntiAirRadius = 1 };
            facility.Abilities.Add("AA2"); facility.Abilities.Add("AA");
            var building = new BuildingData { Owner = human };
            building.FacilitySlots[0] = facility;
            building.Abilities.Add("AA2"); building.Abilities.Add("AA");
            using (Game.Setup.MatchRestoreScope.Begin()) BuildingRegistry.Register(building.Hex, building);
            Check(AntiAirRules.TryGetRadius(unit, out int before) && before == 2, "source AA radius");
            var capture = new MatchWorldCapture(config, new[] { human, ai }); capture.IncludeUnit(unit);
            var json = MatchSaveJson.Serialize(capture.Finish());
            var graph = MatchWorldCodec.Decode(MatchSaveJson.Deserialize<MatchWorldData>(json), config, Game.Setup.MatchIdentityState.Capture());
            var restored = graph.Unit(unit.RuntimeId);
            Check(AntiAirRules.TryGetRadius(restored, out int after) && after == before,
                "unit AA radius changed " + before + " -> " + after);
            var buildings = (Dictionary<HexCoord, BuildingData>)typeof(MatchWorldGraph).GetField("buildings", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(graph);
            var restoredBuilding = buildings[building.Hex];
            Check(AntiAirRules.TryGetRadius(restoredBuilding.Abilities, 1, out int buildingRadius) && buildingRadius == 2,
                "building AA order changed");
            var restoredFacility = restoredBuilding.FacilitySlots[0];
            Check(AntiAirRules.TryGetRadius(restoredFacility.Abilities, restoredFacility.AntiAirRadius, out int facilityRadius) && facilityRadius == 2,
                "facility AA radius changed");
            Console.WriteLine("PASS real full-runtime capture/JSON/decode and AntiAirRules: unit, building and facility ability order");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL world audit: " + ex); return 1; }
    }
}
