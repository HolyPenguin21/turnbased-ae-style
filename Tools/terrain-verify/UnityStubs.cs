// Minimal managed surface for the algorithm tests only. This does NOT emulate Unity rendering,
// lifecycle/coroutines or aviation gameplay: these require the real EditMode/PlayMode suite.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
namespace UnityEngine
{
    public class Object { public string name; public static void Destroy(Object o) {} public static void DestroyImmediate(Object o) {} }
    public class ScriptableObject : Object { public static T CreateInstance<T>() where T:ScriptableObject,new()=>new T(); }
    public class Component : Object { public Transform transform = new Transform(); public GameObject gameObject; public T GetComponent<T>() where T : class => gameObject?.GetComponent<T>(); }
    public class MonoBehaviour : Component { public bool enabled; public bool isActiveAndEnabled => enabled; protected Coroutine StartCoroutine(IEnumerator e) => new Coroutine(); protected void StopCoroutine(Coroutine c) {} }
    public class Coroutine {}
    public class GameObject : Object
    {
        public Transform transform = new Transform();
        private readonly Dictionary<Type,object> _components = new Dictionary<Type,object>();
        public GameObject(string name) {}
        public T AddComponent<T>() where T:Component,new() { var c=new T{gameObject=this};_components[typeof(T)]=c;return c; }
        public T GetComponent<T>() where T:class => _components.TryGetValue(typeof(T),out var c)?(T)c:null;
    }
    public class Transform { public GameObject gameObject; public Vector3 position, localScale; public Vector3 TransformPoint(Vector3 v)=>v; public Vector3 InverseTransformPoint(Vector3 v)=>v; public Transform Find(string name)=>null; public void SetParent(Transform parent,bool worldPositionStays){} }
    public class MeshFilter : Component { public Mesh sharedMesh; }
    public class MeshRenderer : Component { public Material sharedMaterial; public Material[] sharedMaterials; public int sortingOrder; }
    public class BoxCollider : Component { public Vector3 center,size; }
    public enum TextureWrapMode { Clamp }
    public class Texture2D : Object { public TextureWrapMode wrapMode; public Texture2D(int w,int h) {} }
    public class Shader : Object { public static Shader Find(string n)=>new Shader(); }
    public class Material : Object { public Texture2D mainTexture; public Color color; public Material(Shader s) {} }
    public struct Bounds { public Vector3 center,size; public Bounds(Vector3 center,Vector3 size){this.center=center;this.size=size;} public void Encapsulate(Vector3 point){} }
    public class Mesh : Object
    {
        public Vector3[] vertices,normals; public Vector2[] uv; public Color[] colors;
        public Rendering.IndexFormat indexFormat; public int subMeshCount;
        private readonly Dictionary<int,int[]> _triangles=new Dictionary<int,int[]>();
        public void SetVertices(IEnumerable<Vector3> v)=>vertices=v.ToArray();
        public void SetNormals(IEnumerable<Vector3> v)=>normals=v.ToArray();
        public void SetUVs(int channel,IEnumerable<Vector2> v)=>uv=v.ToArray();
        public void SetColors(IEnumerable<Color> v)=>colors=v.ToArray();
        public void SetTriangles(IEnumerable<int> v,int submesh)=>_triangles[submesh]=v.ToArray();
        public int[] GetTriangles(int submesh)=>_triangles[submesh];
        public void RecalculateBounds(){} public void RecalculateTangents(){}
    }
    public static class Random
    {
        public struct State { internal System.Random Rng; }
        private static System.Random _rng=new System.Random(1);
        public static State state { get=>new State {Rng=_rng};set=>_rng=value.Rng; }
        public static void InitState(int seed)=>_rng=new System.Random(seed);
        public static int Range(int min,int max)=>_rng.Next(min,max);
        public static float Range(float min,float max)=>min+(max-min)*(float)_rng.NextDouble();
        public static float value=>(float)_rng.NextDouble();
    }
    public static class Debug { public static void LogWarning(string message)=>System.Console.WriteLine("WARN "+message); }
    public readonly struct Vector2Int { public readonly int x,y; public Vector2Int(int x,int y){this.x=x;this.y=y;} }
    public struct Vector2 { public float x,y; public Vector2(float x,float y){this.x=x;this.y=y;} }
    public struct Color { public static Color white=>new Color(1,1,1,1); public float r,g,b,a; public Color(float r,float g,float b,float a=1){this.r=r;this.g=g;this.b=b;this.a=a;} }
    public struct Vector3
    {
        public float x,y,z; public Vector3(float x,float y,float z){this.x=x;this.y=y;this.z=z;}
        public static Vector3 zero=>new Vector3(); public static Vector3 up=>new Vector3(0,1,0);
        public float sqrMagnitude => x*x+y*y+z*z;
        public static Vector3 Cross(Vector3 a,Vector3 b)=>new Vector3(a.y*b.z-a.z*b.y,a.z*b.x-a.x*b.z,a.x*b.y-a.y*b.x);
        public static float Dot(Vector3 a,Vector3 b)=>a.x*b.x+a.y*b.y+a.z*b.z;
        public float magnitude=>(float)Math.Sqrt(x*x+y*y+z*z);
        public static Vector3 operator +(Vector3 a,Vector3 b)=>new Vector3(a.x+b.x,a.y+b.y,a.z+b.z);
        public static Vector3 operator -(Vector3 a,Vector3 b)=>new Vector3(a.x-b.x,a.y-b.y,a.z-b.z);
        public static Vector3 operator *(Vector3 a,float b)=>new Vector3(a.x*b,a.y*b,a.z*b);
        public static Vector3 Lerp(Vector3 a,Vector3 b,float t)=>a+(b-a)*t;
    }
    public static class Mathf
    {
        public static float Clamp01(float x)=>Math.Max(0,Math.Min(1,x));
        public static int CeilToInt(float x)=>(int)Math.Ceiling(x);
        public static float PerlinNoise(float x,float y)=>0.5f;
        public static float Max(float a,float b)=>Math.Max(a,b);
        public const float Deg2Rad=(float)(Math.PI/180); public static float Sqrt(float x)=>(float)Math.Sqrt(x);
        public static float Sin(float x)=>(float)Math.Sin(x);public static float Cos(float x)=>(float)Math.Cos(x);
        public static float Round(float x)=>(float)Math.Round(x);public static int RoundToInt(float x)=>(int)Math.Round(x);
        public static float Abs(float x)=>Math.Abs(x);public static int Abs(int x)=>Math.Abs(x);
        public static int Min(int a,int b)=>Math.Min(a,b);public static int Max(int a,int b)=>Math.Max(a,b);
        public static int Clamp(int value,int min,int max)=>Math.Max(min,Math.Min(max,value));
    }
    public static class Time { public static double timeAsDouble; public static float deltaTime=1f/60; }
    public static class Application { public static bool isPlaying; }
    public class SerializeField:Attribute{} public class RequireComponent:Attribute{public RequireComponent(params Type[] t){}}
    public class ExecuteAlways:Attribute{} public class ContextMenu:Attribute{public ContextMenu(string n){}}
    public class TooltipAttribute:Attribute{public TooltipAttribute(string n){}}
    public class HeaderAttribute:Attribute{public HeaderAttribute(string n){}}
    public class MinAttribute:Attribute{public MinAttribute(float n){}} public class RangeAttribute:Attribute{public RangeAttribute(float a,float b){}}
}
namespace UnityEngine.Rendering { public enum IndexFormat { UInt32 } }
namespace Game.Players { public class PlayerSetupData {} }
namespace Game.Units { public class UnitData { public int MoveCurrent,MoveMax;public bool IsAviation,HasEmergencyFlightPenalty;} }
namespace Game.Map
{
    public static class MapSortingOrder { public const int Map=0; }
    public static class BuildingRegistry { public static object FindAt(Game.HexGrid.HexCoord h)=>null; }
    public static class ArmyRegistry { public static IEnumerable<ArmyData> AllAt(Game.HexGrid.HexCoord h)=>Array.Empty<ArmyData>(); }
    public static class HexEventRegistry { public static object FindAt(Game.HexGrid.HexCoord h)=>null; }
    public static class HexResourceBonusRegistry { public static object GetBonus(Game.HexGrid.HexCoord h)=>null; }
    public class MapResourceDisplay:UnityEngine.Component { public void RefreshAll(){} }
    public class FogOfWarController:UnityEngine.Component { public void RefreshAll(){} }
    public class MapObjectVisual : UnityEngine.Component { public bool IsVisible = true; }
    public static class VisionSystem { public static Game.Players.PlayerSetupData CurrentViewer; }
    public class ArmyData
    {
        public Game.HexGrid.HexCoord Hex;public Game.Players.PlayerSetupData Owner;
        public bool IsAirfield,IsGarrison,IsPrison; public object PendingAirStrikePolicy; public ArmyController Controller;public readonly List<Game.Units.UnitData> Members=new List<Game.Units.UnitData>();
        public int MaxMovement=>ComputeMaxMovement(Members);
        public static int ComputeMaxMovement(IReadOnlyList<Game.Units.UnitData> members)=>members.Count==0?0:members.Min(u=>u.MoveMax);
        public static int ComputeCurrentMovement(IReadOnlyList<Game.Units.UnitData> members)=>members.Count==0?0:members.Min(u=>u.MoveCurrent);
    }
    public static class StealthSystem{public static bool IsArmyFullyHidden(ArmyData a)=>false;}
}
namespace Game.Aviation
{
    public static class AviationRules
    {
        public static bool IsAirArmy(Game.Map.ArmyData a)=>a!=null&&!a.IsAirfield&&!a.IsGarrison&&!a.IsPrison&&a.Members.Count>0&&a.Members.All(u=>u.IsAviation);
        public static int MovementCost(Game.Map.ArmyData a,int cost)=>IsAirArmy(a)?1:cost;
        public static int EffectiveMoveCurrent(Game.Units.UnitData u)=>u.HasEmergencyFlightPenalty?u.MoveCurrent/2:u.MoveCurrent;
    }
}
namespace Game.Core
{
    public struct ProfileScope:IDisposable{public ProfileScope(string s){} public void Dispose(){}}
    public class GameConfig:UnityEngine.ScriptableObject { public Game.Terrain.MapGenerationSettings mapGeneration; }
    public static class GameSession
    {
        public static int ResolveMapRadius(int fallback)=>fallback;
        public static Game.Terrain.Biome ResolveBiome()=>Game.Terrain.Biome.Arid;
    }
}
namespace Game.Ai
{
    public static class AiMapMemory
    {
        public struct KnownEnemySighting { public Game.HexGrid.HexCoord Hex; }
        public struct KnownBuilding { public Game.HexGrid.HexCoord Hex; public Game.Players.PlayerSetupData Owner; }
        public struct GroundArrival { public bool HasOutcome,Contact; }
        private static long version;
        public static void Clear(){version++;}
        public static long RouteMemoryVersionFor(Game.Players.PlayerSetupData owner)=>version;
        public static IEnumerable<KnownEnemySighting> AllKnownEnemySightings(Game.Players.PlayerSetupData p)=>Array.Empty<KnownEnemySighting>();
        public static IEnumerable<KnownEnemySighting> AllKnownNeutralSightings(Game.Players.PlayerSetupData p)=>Array.Empty<KnownEnemySighting>();
        public static IEnumerable<Game.HexGrid.HexCoord> AllKnownEventGuardHexes(Game.Players.PlayerSetupData p)=>Array.Empty<Game.HexGrid.HexCoord>();
        public static IEnumerable<KnownBuilding> AllKnownBuildings(Game.Players.PlayerSetupData p)=>Array.Empty<KnownBuilding>();
        public static IEnumerable<(Game.HexGrid.HexCoord,int)> ScoutDangerZoneRanges(Game.Players.PlayerSetupData p)=>Array.Empty<(Game.HexGrid.HexCoord,int)>();
        public static GroundArrival KnownGroundArrival(Game.Players.PlayerSetupData p,Game.HexGrid.HexCoord h,bool hidden)=>default;
    }
    public static class AiTurnController
    {
        public static Game.HexGrid.HexCoord? FindAffordableStep(Game.Map.HexMap map,Game.Map.ArmyData army,Game.HexGrid.HexCoord target,
            Func<Game.HexGrid.HexCoord,bool> block,int? current,int? max)=>throw new NotSupportedException("Live AI execution requires Unity tests.");
    }
}

namespace Game.Ai.V2 {}
namespace Game.Combat {}


