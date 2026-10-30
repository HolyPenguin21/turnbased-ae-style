// Managed adapters for focused owner-state tests. Compiled only by run.py, never by Unity.
// Native visuals, AI stats, carrier creation, authored catalogs and initiative rules are adapters;
// the state owners, reference validation, movement/activation data and registry code are real.
using System;
using System.Collections.Generic;
using Game.Map;
using Game.Players;
using Game.Units;
using Game.HexGrid;
namespace UnityEngine
{
    public static class Debug
    {
        public static int NotificationFailures;
        public static void LogException(Exception error) { NotificationFailures++; Console.Error.WriteLine(error); }
    }
    public class Object { public static void Destroy(object value) {} public static void DestroyImmediate(object value) {} }
    public class MonoBehaviour { public object gameObject; }
    public class ScriptableObject : Object {}
    public class GameObject { public GameObject(string name) {} public T AddComponent<T>() where T : new() => new T(); }
    public class Sprite {}
    public struct Color { public static Color grey => default; public static Color white => default; }
    public struct Vector3 { public float x, z; public Vector3(float x, float y, float z) { this.x = x; this.z = z; } }
    public static class Mathf
    {
        public static float Sqrt(float a) => (float)Math.Sqrt(a);
        public static float Round(float a) => (float)Math.Round(a);
        public static int RoundToInt(float a) => (int)Math.Round(a);
        public static float Abs(float a) => Math.Abs(a);
        public static int Abs(int a) => Math.Abs(a);
        public static int Min(int a, int b) => Math.Min(a, b);
        public static int Max(int a, int b) => Math.Max(a, b);
    }
    public class MinAttribute : Attribute { public MinAttribute(float value) {} }
    public class TextAreaAttribute : Attribute {}
    public class HideInInspector : Attribute {}
    public class CreateAssetMenuAttribute : Attribute { public string fileName, menuName; }
}
namespace Game.Players { public enum Faction { None } }
namespace Game.Cards
{
    public enum CardType { Hero, Unit }
    public class MatchLoadout {}
    public class BlueprintQuota {}
    public class TacticHand {}
    public class PlayerHandData {}
    public class CardDefinition { public CardType cardType; public int commandRating, activationApCost, apCost; public ResourceCost resourceCost; }
    public class ResourceCost { public int human, energy, materials, tech; }
    public class FactionCardCatalog { public static CardDefinition ResolveAcross(object catalogs, string key, bool warn, object owner) => null; }
    public class ArmyTagAttribute : Attribute {}
    public struct PredictedEquipmentState { public Dictionary<EquipmentStat, int> Stats; }
    public struct AttachmentResourceState {}
    public enum EquipmentStat { MoveMax, Fate, CommandRating }
    public static class EquipmentSystem
    {
        public static void ReconcileResourceRefill(UnitData unit, EquipmentStat stat) {}
        // Only unattached capacity is part of this focused world/identity harness.
        // Attachment-adjusted deployment is exercised against the real EquipmentSystem.
        public static PredictedEquipmentState Project(CardDefinition host, CardDefinition equipment, CardDefinition mutator)
        {
            if (equipment != null || mutator != null) throw new NotSupportedException("Use the real attachment contract fixture.");
            return new PredictedEquipmentState { Stats = new Dictionary<EquipmentStat, int> { [EquipmentStat.CommandRating] = host.commandRating } };
        }
    }
    public static class UnitAbilities { public const string Transport = "Transport", Barracks = "Barracks"; }
    public static class AbilityParams
    {
        public static int GetBestRecceRadius(object source) => 0;
        public static int GetBestRecceSpotStrength(object source) => 0;
        public static int GetStealthLevel(UnitData unit) => 1;
    }
}
namespace Game.Map
{
    public class MapObjectVisual { public object gameObject; public void SetColor(UnityEngine.Color color) {} }
    public class ArmyController { public bool IsMoving; public HexCoord CurrentHex; public MapObjectVisual Visual; }
    public class HexSelectionController { public void RestackArmiesOn(HexCoord hex, object context) {} public void CreateArmyMarker(ArmyData army) {} }
    public class FacilityData
    {
        public string Name;
        public int StructurePointsCurrent, StructurePointsMax, Defense, Fate, Attack, AntiAirRadius;
        public UnitData AntiAirGunner;
        public HashSet<string> Abilities = new HashSet<string>();
    }
    public class BuildingData
    {
        public string Name; public HexCoord Hex; public PlayerSetupData Owner;
        public bool IsBase, IsStartingCitadel; public MapObjectVisual Visual;
        public FacilityData[] FacilitySlots = new FacilityData[5];
        public HashSet<string> Abilities = new HashSet<string>();
        public bool HasAbility(string ability) => Abilities.Contains(ability);
    }
    public static class PlayerRootRegistry { public static PlayerRoot FindFor(PlayerSetupData owner) => null; }
}
namespace Game.Map
{
    // Gameplay commands are outside this preparation-only compile; unexpected use fails.
    public static class ArmyActions
    {
        public static void ChangeOwner(ArmyData army, PlayerSetupData owner) => throw new NotSupportedException();
        public static void DeleteArmyIfEmptied(ArmyData army, bool force = false) => throw new NotSupportedException();
        public static bool ReorderMembers(ArmyData army, IReadOnlyList<UnitData> order, out string failure) => throw new NotSupportedException();
    }
}
namespace Game.Aviation
{
    public enum AirStrikePolicy {}
    public static class AviationRules
    {
        public static bool IsAirArmy(ArmyData army) => army.IsAirArmy;
        public static bool IsAirfield(ArmyData army) => army.IsAirfield;
        public static int EffectiveMoveCurrent(UnitData unit) => unit.MoveCurrent;
    }
    public static class AviationActions
    {
        // A visible adapter side effect lets the restore-scope test detect an unwanted call.
        public static void EnsureCarrierContainers(ArmyData army)
        { foreach (var unit in army.Members) if (unit.AirfieldCapacity > 0) unit.CarrierAirfield = new ArmyData(); }
        public static void RemoveCarrierContainers(UnitData unit) {}
        public static void LandWingsBeforeCarrierDeparts(ArmyData army) {}
        public static void ReturnStaleAviationAt(HexCoord hex, BuildingData building) => throw new NotSupportedException();
    }
}
namespace Game.Ai
{
    public static class AiMatchStats { public static void RecordBuilt(BuildingData building) {} public static void RecordBuildingTaken(bool captured, PlayerSetupData oldOwner, PlayerSetupData newOwner) {} }
    public static class AiDebugLog { public static void Write(string line) {} }
}
namespace Game.Combat
{
    public struct ChallengeResult { public int AttackerSuccesses, DefenderSuccesses; }
    public static class ChallengeResolver { public static ChallengeResult Resolve(int attack, int defense) => default; }
    public static class BattleInitiator { public static bool CanInitiateContact(ArmyData army) => true; public static bool DefendsBuildingAgainst(ArmyData army, PlayerSetupData mover) => false; }
}
namespace Game.Core { public class GameConfig { public int armyVisionRadius, buildingVisionRadius; } public static class GameSession { public static List<PlayerSetupData> Players; } }
namespace Game.Styles
{
    public static class PlayerColorPalette { public static UnityEngine.Color[] Colors = new UnityEngine.Color[10]; }
    public static class MapFlagAnimator { public static void ApplyFaction(object visual, object faction) {} }
}
namespace Game.Turns
{
    public static class InitiativeRules
    {
        public const int MaxBonusTokens = 9;
        public static int CumulativeUnitsForTokenCount(int count) => count * (count + 1) / 2;
        public static int NextBonusTokenCost(int count) => count + 1;
    }
}
namespace Game.Terrain { public class TerrainTypeEntry { public bool blocksGroundMovement; public Game.Economy.ResourceYields resourceYields; } }
