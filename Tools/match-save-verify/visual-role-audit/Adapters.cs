// Engine surfaces only. Rendering, native callbacks and scene activation require Unity.
using System;
using System.Collections.Generic;
using System.Linq;
namespace UnityEngine {
    [AttributeUsage(AttributeTargets.Field)] public class SerializeField : Attribute {}
    [AttributeUsage(AttributeTargets.Field)] public class RangeAttribute : Attribute { public RangeAttribute(float a,float b){} }
    public class Object { public string name; protected static T Instantiate<T>(T value,Vector3 p,Quaternion r,Transform parent) => throw new NotSupportedException("Native clone outside focused scope"); public static void Destroy(Object o){} public static void DestroyImmediate(Object o){} }
    public class Component : Object { public GameObject gameObject; public Transform transform=>gameObject.transform; public T[] GetComponentsInChildren<T>(bool inactive) where T:Component=>gameObject.GetComponentsInChildren<T>(inactive); }
    public class MonoBehaviour : Component { public bool enabled=true; }
    public class GameObject : Object { readonly List<Component> components=new(); public readonly Transform transform; public bool activeSelf=true; public int layer; public GameObject(string name){this.name=name;transform=new Transform{gameObject=this};} public void SetActive(bool value)=>activeSelf=value; public T AddComponent<T>() where T:Component,new(){var c=new T{gameObject=this};components.Add(c);return c;} public T[] GetComponentsInChildren<T>(bool inactive) where T:Component=>components.OfType<T>().Concat(transform.children.SelectMany(t=>t.gameObject.GetComponentsInChildren<T>(inactive))).ToArray(); }
    public class Transform : Component { public List<Transform> children=new(); Transform parent; public Vector3 localPosition,localScale=Vector3.one; public Quaternion localRotation=Quaternion.identity; public Vector3 position {get=>localPosition;set=>localPosition=value;} public Quaternion rotation{get=>localRotation;set=>localRotation=value;} public Vector3 lossyScale=>localScale; public Vector3 right=>new Vector3(1,0,0); public int childCount=>children.Count; public Transform GetChild(int i)=>children[i]; public void SetParent(Transform p,bool world){parent?.children.Remove(this);parent=p;p?.children.Add(this);} }
    public struct Vector2 { public float x,y; public Vector2(float x,float y){this.x=x;this.y=y;} public static Vector2 zero=>new(); public static float Distance(Vector2 a,Vector2 b)=>0; }
    public struct Vector3 { public float x,y,z; public Vector3(float x,float y,float z){this.x=x;this.y=y;this.z=z;} public static Vector3 one=>new(1,1,1); public static Vector3 operator +(Vector3 a,Vector3 b)=>new(a.x+b.x,a.y+b.y,a.z+b.z); public static Vector3 operator *(Vector3 a,float b)=>new(a.x*b,a.y*b,a.z*b); }
    public struct Quaternion { public float x,y,z,w; public static Quaternion identity=>new(){w=1}; }
    public struct Color {public float r,g,b,a; public Color(float r,float g,float b,float a=1){this.r=r;this.g=g;this.b=b;this.a=a;} public static Color white=>new(1,1,1);}
    public struct Bounds {public Vector3 center,extents;}
    public class Sprite:Object { public Bounds bounds; }
    public class Material:Object {}
    public enum SpriteDrawMode {Simple,Sliced,Tiled} public enum SpriteTileMode {Continuous,Adaptive} public enum SpriteMaskInteraction {None,VisibleInsideMask,VisibleOutsideMask} public enum SpriteSortPoint {Center,Pivot}
    public class SpriteRenderer:Component {public Sprite sprite;public Material sharedMaterial;public Color color;public int sortingLayerID,sortingOrder;public bool enabled=true,flipX,flipY;public SpriteDrawMode drawMode;public SpriteTileMode tileMode;public SpriteMaskInteraction maskInteraction;public SpriteSortPoint spriteSortPoint;public Vector2 size;public float adaptiveModeThreshold;public Bounds bounds;}
    public class Camera:Component {public Vector3 WorldToScreenPoint(Vector3 v)=>v;}
    public static class Mathf {public static int Min(int a,int b)=>Math.Min(a,b); public static float Max(float a,float b)=>Math.Max(a,b);public static float Abs(float v)=>Math.Abs(v);public static float Clamp01(float v)=>Math.Clamp(v,0,1);}
    public static class SortingLayer {public static bool IsValid(int id)=>true;} public static class Application {public static bool isPlaying;}
}
namespace Game.Core {public class GameConfig {}}
namespace Game.Cards {public class ResourceCost {public int human,energy,materials,tech;}}
namespace Game.Players {public class PlayerSetupData {}}
namespace Game.HexGrid {public struct HexCoord {public HexCoord(int q,int r){}}}
namespace Game.Map {
    public class MapFlagAnimator:UnityEngine.MonoBehaviour {}
    public class MapVisualSnapshotReference {public string TypeKey,SnapshotKey;}
    public class RememberedBuildingVisualState {public string ViewerId;public Game.HexGrid.HexCoord Hex;public MapVisualSnapshotReference Appearance;}
    public class RememberedArmyVisualState:RememberedBuildingVisualState {public int SourceArmyId;}
    public class RememberedMapVisualState {public List<RememberedArmyVisualState> Armies=new();public List<RememberedBuildingVisualState> Buildings=new();}
    public class HexSelectionController {public void RestoreRememberedVisuals(RememberedMapVisualState s,Func<string,Game.Players.PlayerSetupData> p,Func<MapVisualSnapshotReference,MapObjectVisual> a)=>throw new NotSupportedException("Scene importer outside focused scope");}
}
namespace Game.Saves {
    internal class MatchSaveStore {internal const int MaximumFileBytes=64*1024*1024;}
    internal static class MatchSaveAssets {internal static string SpriteKey(UnityEngine.Sprite s)=>s?.name;internal static string MaterialKey(UnityEngine.Material m)=>m?.name;internal static UnityEngine.Sprite ResolveSprite(string k)=>string.IsNullOrEmpty(k)?null:new(){name=k};internal static UnityEngine.Material ResolveMaterial(string k)=>string.IsNullOrEmpty(k)?null:new(){name=k};}
}
