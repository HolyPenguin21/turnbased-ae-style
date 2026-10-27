using System;
using System.Collections.Generic;
using Game.Core;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Setup;
using Game.Terrain;

// Native rendering and scene setup are unavailable in this managed harness. The real display,
// registry, restore coordinator and quiet scope sources are compiled by run.py. These adapters
// expose only their external dependencies; none implements marker reconciliation or yield rules.
namespace UnityEngine
{
    public class SerializeField : Attribute { }
    public class RequireComponent : Attribute { public RequireComponent(Type type) { } }
    public class MinAttribute : Attribute { public MinAttribute(float value) { } }
    public struct Vector2 { public float x, y; }
    public struct Vector3
    {
        public Vector3(float x, float y, float z) { }
        public static Vector3 operator +(Vector3 left, Vector3 right) => left;
        public static Vector3 operator *(Vector3 value, float scale) => value;
    }
    public class Transform { public Vector3 position; }
    public class GameObject
    {
        public bool active;
        public void SetActive(bool value) { active = value; }
    }
    public class MonoBehaviour
    {
        public GameObject gameObject = new GameObject();
        public Transform transform = new Transform();
        public static readonly Dictionary<Type, object> Components = new Dictionary<Type, object>();
        public T GetComponent<T>() => (T)Components[typeof(T)];
        protected static T Instantiate<T>(T prefab, Transform parent) where T : new() => new T();
        protected static void Destroy(object value) { }
        protected static void DestroyImmediate(object value) { }
    }
    public static class Application { public static bool isPlaying = true; }
}
namespace Game.Players { public class PlayerSetupData { } }
namespace Game.Cards
{
    public class CardDefinition { }
    public class EventDefinition
    {
        public List<RewardEntry> rewards = new List<RewardEntry>();
        public bool CanVisit(PlayerSetupData player) => true;
    }
    public class RewardEntry { public object type; public string cardKey; public ResourceYields resources; }
}
namespace Game.Core
{
    public class GameConfig
    {
        public EventMarkerVisual eventMarkerPrefab = new EventMarkerVisual();
        public UnityEngine.Vector2 eventIconOffset;
        public ResourceIconVisual resourceIconPrefab = new ResourceIconVisual();
        public float resourceIconSpacing, resourceRowOffset;
    }
    public class ProfileScope : IDisposable
    {
        public ProfileScope(string name) { }
        public void Dispose() { }
    }
    public static class GameSession
    {
        public static IMatchRestoreSource PendingRestore;
        public static void ValidateRestore(IMatchRestoreSource source, GameConfig config) { }
        public static void CompleteRestore(IMatchRestoreSource source) { PendingRestore = null; }
    }
}
namespace Game.Turns
{
    public class GameTurnState { }
    public class GameTurnController
    {
        public event Action TurnStateChanged;
        public Action BeforeResume;
        public void ResumeGame(GameTurnState state)
        {
            if (MatchRestoreScope.IsRestoring) throw new Exception("Resume inside quiet scope");
            BeforeResume?.Invoke();
            TurnStateChanged?.Invoke();
        }
    }
}
namespace Game.Terrain
{
    public class TerrainTypeEntry
    {
        public bool blocksGroundMovement;
        public ResourceYields resourceYields = new ResourceYields();
    }
}
namespace Game.Map
{
    public class ArmyData { public PlayerSetupData Owner; public string Name; }
    public class HexSelectionController { public void ValidateRememberedVisualCoverage() { } }
    public class HexMap : UnityEngine.MonoBehaviour
    {
        public float OuterRadius = 1;
        public HexCoord Hex = new HexCoord(0, 0), BonusOnlyHex = new HexCoord(1, 0);
        public TerrainTypeEntry Terrain = new TerrainTypeEntry();
        public IEnumerable<HexCoord> AllCoords { get { yield return Hex; yield return BonusOnlyHex; } }
        public bool TryGetTerrainAt(HexCoord coord, out TerrainTypeEntry terrain)
        {
            terrain = coord.Equals(Hex) ? Terrain : new TerrainTypeEntry();
            return true;
        }
        public UnityEngine.Vector3 HexToWorld(HexCoord hex) => new UnityEngine.Vector3();
    }
    // Observe native presentation writes without reimplementing the display owners.
    public static class PresentationWrites { public static Action BeforeWrite; }
    public class EventMarkerVisual : UnityEngine.MonoBehaviour
    {
        public void SetSortingOrder(int order) { PresentationWrites.BeforeWrite?.Invoke(); }
    }
    public class ResourceIconVisual : UnityEngine.MonoBehaviour
    {
        public int Amount;
        public void SetResource(ResourceType type, int amount, bool known)
        {
            PresentationWrites.BeforeWrite?.Invoke();
            Amount = amount;
        }
        public void SetAmountKnown(bool value) { }
    }
    public static class MapSortingOrder { public const int EventIcon = 1; }
    public static class VisionSystem
    {
        public static PlayerSetupData CurrentViewer;
        public static event Action<PlayerSetupData> VisibilityChanged;
        public static bool IsVisibleToCurrentViewer(HexCoord coord) => true;
        public static bool HasEverSeenByCurrentViewer(HexCoord coord) => true;
        public static bool IsVisitedByCurrentViewer(HexCoord coord) => true;
        public static void NotifyContentChanged(HexCoord coord) { }
    }
}
namespace Game.Setup
{
    // The file decoder and unrelated entity/turn graph are outside this focused compile.
    public interface IMatchRestoreSource
    {
        MatchIdentityState Identity { get; }
        Game.Turns.GameTurnState Turn { get; }
        void RestoreWorld(HexMap map);
        void RestoreKnowledge();
        void RestorePresentation(HexSelectionController selection);
    }
    public class MatchIdentityState { public void Reserve() { } }
}
