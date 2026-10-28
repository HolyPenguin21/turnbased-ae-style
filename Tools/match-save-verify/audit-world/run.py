#!/usr/bin/env python3
"""Execute production world codecs and native fixture logic using explicit managed engine adapters.
Tactical JSON is the actual production MatchSaveJson transport; native scene and assets remain adapters.
"""
import importlib.util, json, os, re, subprocess, tempfile
from pathlib import Path
ROOT = Path(__file__).resolve().parents[3]
base = ROOT/'Tools/save-preparation-verify/integration'
spec = importlib.util.spec_from_file_location('integration_runner', base/'run.py')
module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
dotnet = Path(os.environ['DOTNET']).resolve()
packages = Path(os.environ['NUGET_PACKAGES'])
sdk = sorted((dotnet.parent/'sdk').glob('8.*'))[-1]
refpack = sorted((dotnet.parent/'packs/Microsoft.NETCore.App.Ref').glob('8.*'))[-1]
refs = list((refpack/'ref/net8.0').glob('*.dll'))
adapter = (base/'PortableAdapters.cs').read_text()
adapter = adapter.replace('public class Object {', 'public class Object { public static T Instantiate<T>(T value) where T:new()=>new T();')
adapter = adapter.replace('public class Transform {', 'public class Transform { public void SetParent(Transform parent,bool worldPositionStays) {}')
adapter = adapter.replace('public class ScriptableObject : Object {}', 'public class ScriptableObject : Object { public static T CreateInstance<T>() where T:new()=>new T(); }')
adapter = adapter.replace('public class FactionCardCatalog {', 'public class FactionCardCatalog { public MapObjectVisual armyPrefab,airArmyPrefab,basePrefab,citadelPrefab; public UnityEngine.Sprite citadelIcon,facilityIcon;')
adapter = adapter.replace('public struct PredictedEquipmentState {}', '''public readonly struct PredictedEquipmentState { public readonly IReadOnlyDictionary<EquipmentStat,int> Stats; public readonly IReadOnlyList<string> Abilities; public PredictedEquipmentState(IReadOnlyDictionary<EquipmentStat,int> stats,IReadOnlyList<string> abilities) {Stats=stats;Abilities=abilities;} }''')
adapter = adapter.replace('public struct AttachmentResourceState {}', '''public readonly struct AttachmentResourceState { public readonly int HpSpent,MoveSpent,FateSpent,LastHp,LastMove,LastFate; public AttachmentResourceState(int a,int b,int c,int d,int e,int f) {HpSpent=a;MoveSpent=b;FateSpent=c;LastHp=d;LastMove=e;LastFate=f;} }''')
adapter = adapter.replace('public enum EquipmentStat { MoveMax, Fate }', '''public enum EquipmentStat { Attack,Defense,Range,HitPoints,MoveMax,Initiative,ActivationApCost,CommandRating,Fate } public class EquipmentStatChange {public EquipmentStat stat;public int amount; public bool isOverride;}''')
adapter = adapter.replace('public bool IsFrozenRememberedAppearance; public string Appearance;', 'public bool IsFrozenRememberedAppearance; public string Appearance; public MapObjectVisual PersistenceSourcePrefab; public UnityEngine.Sprite PersistenceIcon; public void SetIcon(UnityEngine.Sprite s) {} public void SetSortingOrder(int a,int b) {}')
adapter = adapter.replace('public class ArmyController {', 'public class ArmyController { public void SetData(ArmyData a) {CurrentHex=a.Hex;}')
adapter = adapter.replace('public string Name;\n        public int StructurePointsCurrent', 'public string Name; public UnityEngine.Sprite Art,DetailArt; public int UpgradeLevel;\n        public int StructurePointsCurrent')
adapter = adapter.replace('public string Name; public HexCoord Hex;', 'public string Name; public UnityEngine.Sprite Art,DetailArt; public int Level,StructurePointsCurrent,StructurePointsMax,Defense,Fate,AirfieldCapacity; public bool HasTieredUnlock; public BuildingData(int slots=5) {FacilitySlots=new FacilityData[slots];}\n        public HexCoord Hex;')
adapter = adapter.replace('public enum AirStrikePolicy {}', '''public enum AirStrikePolicyKind {Standard,RaidSupport,DefenceSupport,Transit} public readonly struct AirStrikePolicy {public readonly AirStrikePolicyKind Kind;public readonly int? ExactTargetArmyId;public readonly int MinimumSurvivors;public AirStrikePolicy(AirStrikePolicyKind k,int? id=null,int min=0) {Kind=k;ExactTargetArmyId=id;MinimumSurvivors=min;}}''')
adapter = adapter.replace('public Game.Cards.StartingDeckCatalog collectionDeckCatalog;', 'public Game.Cards.StartingDeckCatalog collectionDeckCatalog; public Game.Cards.ResearchProductionCatalog collectionResearchCatalog; public Game.Cards.CardDefinition[] extractionFacilityCards; public MapObjectVisual facilityMarkerPrefab;')
adapter = adapter.replace('public float OuterRadius=1;', 'public float OuterRadius=1; public void SetData(int ring,float radius,Dictionary<HexCoord,Game.Terrain.TerrainTypeEntry> cells) {}')
adapter = adapter.replace('public const int EventIcon=1;', 'public const int EventIcon=1,BuildingCircle=2,BuildingIcon=3,ArmyCircle=4,ArmyIcon=5;')
adapter += '''
namespace Game.Combat { public static class BattleGrid {public static bool InBounds(int row,int col)=>row>=0&&row<5&&col>=0&&col<5;} }
namespace Game.Saves { internal static class MatchSaveAssets {
 internal static string SpriteKey(UnityEngine.Sprite s)=>s==null?null:throw new System.Exception("Native asset adapter cannot encode sprites");
 internal static UnityEngine.Sprite ResolveSprite(string s)=>string.IsNullOrEmpty(s)?null:throw new System.Exception("Native asset adapter cannot resolve sprites");
 internal static string PrefabKey(Game.Map.MapObjectVisual v)=>v==null?null:throw new System.Exception("Native asset adapter cannot encode prefabs");
 internal static Game.Map.MapObjectVisual ResolvePrefab(string s)=>string.IsNullOrEmpty(s)?null:throw new System.Exception("Native asset adapter cannot resolve prefabs");
 internal static string EventKey(Game.Cards.EventDefinition v)=>throw new System.Exception("Native event manifest not supplied");
 internal static Game.Cards.EventDefinition ResolveEvent(string s)=>throw new System.Exception("Native event manifest not supplied");
} }
'''
program = '''using System; using Game.EditorTests; class CodecProgram { static int Main() { int failures=0; foreach(var method in typeof(MatchCarrierDeckRestoreAuditTests).GetMethods()) { if(method.GetCustomAttributes(typeof(NUnit.Framework.TestAttribute),false).Length==0) continue; var fixture=new MatchCarrierDeckRestoreAuditTests(); try {method.Invoke(fixture,null); Console.WriteLine("PASS "+method.Name);} catch(Exception e) {failures++;Console.WriteLine("FAIL "+method.Name+" "+(e.InnerException??e));} finally {} } return failures==0?0:1;} }'''
with tempfile.TemporaryDirectory(prefix='world-codec-verify-') as tmp:
    work = Path(tmp); (work/'Adapters.cs').write_text(adapter); (work/'Program.cs').write_text(program)
    files = [ROOT/'Assets/Scripts'/x for x in module.SOURCES]
    files += [ROOT/'Assets/Scripts/Saves'/x for x in ['MatchWorldData.cs','MatchWorldCapture.cs','MatchWorldCodec.cs','MatchSaveJson.cs','MatchSaveStore.cs']]
    files += [ROOT/'Assets/Editor/Tests/Core/MatchCarrierDeckRestoreAuditTests.cs', work/'Adapters.cs',work/'Program.cs',ROOT/'Tools/campaign-verify/PortableJson.cs']
    helper = ROOT/'Assets/Scripts/Setup/CitadelSetupController.Restore.cs'
    if helper.exists(): files.append(helper)
    nunit=packages/'nunit/3.14.0/lib/netstandard2.0/nunit.framework.dll'
    args=['-nologo','-target:exe','-langversion:9','-define:UNITY_INCLUDE_TESTS','-nowarn:0649,0414','-out:'+str(work/'checks.dll'),*['-r:'+str(p) for p in [*refs,nunit]],*map(str,files)]
    (work/'compile.rsp').write_text('\n'.join('"'+a+'"' for a in args))
    result=subprocess.run([str(dotnet),str(sdk/'Roslyn/bincore/csc.dll'),'@'+str(work/'compile.rsp')])
    if result.returncode: raise SystemExit(result.returncode)
    import shutil
    shutil.copy2(nunit,work/nunit.name)
    (work/'checks.runtimeconfig.json').write_text(json.dumps({'runtimeOptions':{'tfm':'net8.0','framework':{'name':'Microsoft.NETCore.App','version':refpack.name}}}))
    print('Production world capture/Decode + entities/hand state; tactical JSON transport is real; native scene/assets use explicit Tools adapters.',flush=True)
    raise SystemExit(subprocess.run([str(dotnet),str(work/'checks.dll')]).returncode)
