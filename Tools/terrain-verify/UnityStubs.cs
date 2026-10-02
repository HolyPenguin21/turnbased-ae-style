// Minimal managed surface for the algorithm tests only. This does NOT emulate Unity rendering,
// lifecycle/coroutines or aviation gameplay: these require the real EditMode/PlayMode suite.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
namespace UnityEngine
{
    public class Object { public static void Destroy(Object o) {} public static void DestroyImmediate(Object o) {} }
    public class Component : Object { public Transform transform = new Transform(); public GameObject gameObject; public T GetComponent<T>() where T : class => gameObject?.GetComponent<T>(); }
    public class MonoBehaviour : Component { public bool enabled; protected Coroutine StartCoroutine(IEnumerator e) => new Coroutine(); protected void StopCoroutine(Coroutine c) {} }
    public class Coroutine {}
    public class GameObject : Object
    {
        private readonly Dictionary<Type,object> _components = new Dictionary<Type,object>();
        public GameObject(string name) {}
        public T AddComponent<T>() where T:Component,new() { var c=new T{gameObject=this};_components[typeof(T)]=c;return c; }
        public T GetComponent<T>() where T:class => _components.TryGetValue(typeof(T),out var c)?(T)c:null;
    }
    public class Transform { public Vector3 position, localScale; public Vector3 TransformPoint(Vector3 v)=>v; public Vector3 InverseTransformPoint(Vector3 v)=>v; }
    public class MeshFilter : Component {} public class MeshRenderer : Component {}
    public class Texture2D : Object { public Texture2D(int w,int h) {} }
    public class Shader : Object { public static Shader Find(string n)=>new Shader(); }
    public class Material : Object { public Texture2D mainTexture; public Material(Shader s) {} }
    public readonly struct Vector2Int { public readonly int x,y; public Vector2Int(int x,int y){this.x=x;this.y=y;} }
    public struct Color { public float r,g,b,a; public Color(float r,float g,float b,float a=1){this.r=r;this.g=g;this.b=b;this.a=a;} }
    public struct Vector3
    {
        public float x,y,z; public Vector3(float x,float y,float z){this.x=x;this.y=y;this.z=z;}
        public static Vector3 zero=>new Vector3(); public static Vector3 up=>new Vector3(0,1,0);
        public float magnitude=>(float)Math.Sqrt(x*x+y*y+z*z);
        public static Vector3 operator +(Vector3 a,Vector3 b)=>new Vector3(a.x+b.x,a.y+b.y,a.z+b.z);
        public static Vector3 operator -(Vector3 a,Vector3 b)=>new Vector3(a.x-b.x,a.y-b.y,a.z-b.z);
        public static Vector3 operator *(Vector3 a,float b)=>new Vector3(a.x*b,a.y*b,a.z*b);
        public static Vector3 Lerp(Vector3 a,Vector3 b,float t)=>a+(b-a)*t;
    }
    public static class Mathf
    {
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
    public class TooltipAttribute:Attribute{public TooltipAttribute(string n){}}
    public class HeaderAttribute:Attribute{public HeaderAttribute(string n){}}
    public class MinAttribute:Attribute{public MinAttribute(float n){}} public class RangeAttribute:Attribute{public RangeAttribute(float a,float b){}}
}
namespace Game.Players { public class PlayerSetupData {} }
namespace Game.Units { public class UnitData { public int MoveCurrent,MoveMax;public bool IsAviation,HasEmergencyFlightPenalty;} }
namespace Game.Map
{
    public class MapObjectVisual : UnityEngine.Component {}
    public class ArmyData
    {
        public Game.HexGrid.HexCoord Hex;public Game.Players.PlayerSetupData Owner;
        public bool IsAirfield,IsGarrison,IsPrison;public readonly List<Game.Units.UnitData> Members=new List<Game.Units.UnitData>();
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
namespace Game.Core { public struct ProfileScope:IDisposable{public ProfileScope(string s){} public void Dispose(){}} }
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

