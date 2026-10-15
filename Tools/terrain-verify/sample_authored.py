#!/usr/bin/env python3
"""Sample real authored map physics with the actual managed project sources; no rendering."""
import yaml,json,os,shutil,subprocess,tempfile,argparse,re
from pathlib import Path
root=Path(__file__).resolve().parents[2]
parser=argparse.ArgumentParser(description="Exercise actual authored terrain and resource planners without Unity rendering.")
parser.add_argument('--seeds',type=int,default=4)
parser.add_argument('--actual-starts',action='store_true',help='Invoke actual setup methods and sample a legal human neighbor choice.')
parser.add_argument('--players',type=int,choices=[2,3,4],help='Limit the sampled player count (campaigns use 2).')
parser.add_argument('--ordinary',action='store_true',help='Sample ordinary-match budgets without regional inputs.')
args=parser.parse_args()
if args.seeds < 1: parser.error('--seeds must be positive')
dotnet=Path(os.environ.get('DOTNET_BIN',shutil.which('dotnet') or 'dotnet')).resolve()
if not dotnet.is_file(): raise SystemExit('Install .NET 8 or set DOTNET_BIN.')
env=dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT='1')
dot=dotnet.parent; sdk=sorted((dot/'sdk').glob('8.*'))[-1]
subprocess.run([str(dotnet),str(sdk/'MSBuild.dll'),str(root/'Tools/terrain-verify/TerrainVerify.csproj'),'/restore','/t:Build','/v:q','/m:1'],check=True,env=env)
work=tempfile.TemporaryDirectory(prefix='authored-terrain-')
workpath=Path(work.name)
s=(root/'Assets/Config/GameConfig.asset').read_text();s='\n'.join(l for l in s.splitlines() if not l.startswith(('%','---')))
authored=yaml.safe_load(s)['MonoBehaviour']
d=authored['mapGeneration']
def scalar(v):
 if v is None:return "null"
 if isinstance(v,str):return json.dumps(v)
 if isinstance(v,bool):return str(v).lower()
 return str(v)+'f' if isinstance(v,float) else str(v)
def fields(obj,keys):return ','.join(k+'='+scalar(obj[k]) for k in keys if k in obj)
def complex_(c):
 keys=['name','terrainName','count','placementAttempts','exclusiveGroup','minCenterFraction','maxCenterFraction','framesPerSecond']
 values=fields(c,keys)+',useInGeneration='+str(bool(c.get('useInGeneration',1))).lower()+',randomizeRotation='+str(bool(c.get('randomizeRotation',0))).lower()
 values+=',allowedTerrainNames=new string[]{'+','.join(scalar(x) for x in c['allowedTerrainNames'])+'}'
 values+=',parts=new TerrainComplexPart[]{'+','.join('new TerrainComplexPart{offset=new Vector2Int('+str(p['offset']['x'])+','+str(p['offset']['y'])+'),frames=new Texture2D[]{'+','.join('new Texture2D(2,2)' if f['fileID'] else 'null' for f in p['frames'])+'}}' for p in c['parts'])+'}'
 return 'new TerrainComplexTemplate{'+values+'}'
def palette(p):
 return fields(p,['mountainsTerrainName','mountainRangeCount','mountainRangeLength'])+',terrainTypes=new List<TerrainTypeEntry>{'+','.join('new TerrainTypeEntry{'+fields(t,['terrainName','baselineWeight','moveCost','defenseModifier'])+',blocksGroundMovement='+str(bool(t['blocksGroundMovement'])).lower()+'}' for t in p['terrainTypes'])+'},complexes=new List<TerrainComplexTemplate>{'+','.join(complex_(c) for c in p['complexes'])+'}'
init='new MapGenerationSettings{'+fields(d,['radius','complexCount','impassableEdgeMarginRings'])+','+palette(d)+',desertOverride=new BiomeTerrainSet{'+palette(d['desertOverride'])+'}}'
# Reuse the exact current setup method bodies in a disposable probe. Only its visual
# callback is a no-op; there is no second authored implementation of the start rules.
setup=(root/'Assets/Scripts/Setup/CitadelSetupController.cs').read_text()
def method(name):
 matches=list(re.finditer(r'^        private (?:static )?[^\n]+ '+name+r'\(', setup, re.M))
 if len(matches)!=1: raise RuntimeError('Expected one setup method: '+name)
 start=matches[0].start(); opening=setup.index('{',matches[0].end());depth=1;end=opening+1
 while depth:
  depth += (setup[end]=='{')-(setup[end]=='}');end+=1
 return setup[start:end]
probe='\n'.join(method(name) for name in ['AssignStartingHexes','PickFarEnough','PickFarthestFrom',
 'BucketByAngleAroundCenter','AngleAroundCenterDegrees','GetEdgeEligibleHexes','GetFieldCorners',
 'GetCityRuinsHexes','IsSelectable','BuildValidHexes'])
probe='''class StartProbe {
 private HexMap map; private StartInputs gameConfig=new StartInputs();
 private Dictionary<PlayerSetupData,HexCoord> _startHexes=new Dictionary<PlayerSetupData,HexCoord>();
 private HashSet<HexCoord> _validHexes=new HashSet<HexCoord>();
 public StartProbe(HexMap value){map=value;}
 private void SpawnRegionHighlight(PlayerSetupData player,HexCoord hex){}
 public List<HexCoord> Pick(int count){var players=Enumerable.Range(0,count).Select(_=>new PlayerSetupData()).ToList();AssignStartingHexes(players);if(_startHexes.Count!=count)throw new Exception("Insufficient actual starts");var starts=players.Select(p=>_startHexes[p]).ToList();BuildValidHexes(starts[0]);var legal=_validHexes.ToList();starts[0]=legal[UnityEngine.Random.Range(0,legal.Count)];return starts;}
 class StartInputs { INPUTS }
 METHODS
}
'''.replace('INPUTS', ';'.join('public int '+k+'='+str(authored[k]) for k in ['maxEdgeDistance','minCityRuinsDistance','cornerExclusionRadius'])+';').replace('METHODS',probe)
prog='''using System;using System.Linq;using System.Reflection;using System.Collections.Generic;using Game.Core;using Game.Map;using Game.Terrain;using Game.Setup;using Game.HexGrid;using UnityEngine;using Game.Players;using Random=UnityEngine.Random;
class AuthoredSample {
static int Main(){try{return Run();}catch(Exception ex){Console.Error.WriteLine(ex.Message);return 1;}} static int Run(){ var config=new GameConfig{mapGeneration=INIT};var go=new GameObject("sample");var map=go.AddComponent<HexMap>();var gen=go.AddComponent<HexMapGenerator>();var flags=BindingFlags.NonPublic|BindingFlags.Instance;typeof(HexMapGenerator).GetField("gameConfig",flags).SetValue(gen,config);int cases=0;
foreach(Biome biome in Enum.GetValues(typeof(Biome)))for(int radius=5;radius<=8;radius++)for(int players=2;players<=4;players++)for(int seed=0;seed<4;seed++){
 UnityEngine.Random.InitState(seed);var palette=config.mapGeneration.ResolveBiome(biome);typeof(HexMapGenerator).GetField("_activeRadius",flags).SetValue(gen,radius);typeof(HexMapGenerator).GetField("_activeBiome",flags).SetValue(gen,palette);var coords=HexGridMath.HexesInRange(new HexCoord(0,0),radius).ToList();var assign=(Dictionary<HexCoord,int>)typeof(HexMapGenerator).GetMethod("AssignTerrainTypes",flags).Invoke(gen,new object[]{coords});typeof(HexMapGenerator).GetMethod("PlaceComplexes",flags).Invoke(gen,new object[]{coords,assign});map.SetData(radius,1,assign.ToDictionary(p=>p.Key,p=>palette.terrainTypes[p.Value]));int r=radius-seed%2;
 var starts=players==2?new List<HexCoord>{new HexCoord(-r,0),new HexCoord(r,0)}:players==3?new List<HexCoord>{new HexCoord(r,0),new HexCoord(-r,r),new HexCoord(0,-r)}:new List<HexCoord>{new HexCoord(r,0),new HexCoord(-r,0),new HexCoord(0,r),new HexCoord(0,-r)};
 if(ACTUAL_STARTS) starts=new StartProbe(map).Pick(players);
 foreach(ResourceSpecialization profile in Enum.GetValues(typeof(ResourceSpecialization)))foreach(ResourceRichness richness in Enum.GetValues(typeof(ResourceRichness))){HexResourceBonusRegistry.Clear();var budget=MapGenerationSettings.ResourceBudget(players,radius,ORDINARY ? (RegionResourceConditions?)null : new RegionResourceConditions(profile,richness));var plan=MatchResourcePlacement.Plan(map,starts,budget);if(plan.Near.Any(n=>n.Count!=3)||plan.Far.Points.Count!=budget.FarTarget||plan.Far.TechHexes.Count!=budget.TechTarget){Console.WriteLine("CITADELS "+string.Join(";",starts.Select(h=>h.Q+","+h.R)));Console.WriteLine("FAR candidates "+string.Join(";",coords.Where(h=>map.CanEnter(h)&&starts.All(c=>HexGridMath.Distance(c,h)>=4)).Select(h=>h.Q+","+h.R)));Console.WriteLine("NEAR "+string.Join(";",plan.Near.SelectMany(n=>n).Select(p=>p.Hex.Q+","+p.Hex.R)));throw new Exception($"Incomplete {biome} R{radius} P{players} seed{seed} {profile} {richness}: near{string.Join(",",plan.Near.Select(n=>n.Count))} far{plan.Far.Points.Count}/{budget.FarTarget}");}cases++;}}
Console.WriteLine($"PASS authored biome placement: {cases} plans; both biomes, R5–8, P2–4, every profile/richness, four seeds, edge/inward starts. Real AssignTerrainTypes/PlaceComplexes; placeholder texture references, no rendering.");return 0;}}
'''.replace('INIT',init)
prog+=probe
prog=prog.replace('ACTUAL_STARTS',str(args.actual_starts).lower()).replace('ORDINARY',str(args.ordinary).lower())
if args.players: prog=prog.replace('int players=2;players<=4','int players='+str(args.players)+';players<='+str(args.players))
if args.ordinary: prog=prog.replace('Enum.GetValues(typeof(ResourceSpecialization))','new[]{ResourceSpecialization.Balanced}').replace('Enum.GetValues(typeof(ResourceRichness))','new[]{ResourceRichness.Normal}')
prog=prog.replace('P2–4', 'P'+str(args.players) if args.players else 'P2–4').replace('every profile/richness', 'ordinary budgets' if args.ordinary else 'every profile/richness')
prog=prog.replace('edge/inward starts', 'actual setup and human choices' if args.actual_starts else 'edge/inward starts')
prog=prog.replace('seed<4','seed<'+str(args.seeds)).replace('four seeds',str(args.seeds)+' seeds')
(workpath/'AuthoredSample.cs').write_text(prog)
dot=dotnet.parent; sdk=sorted((dot/'sdk').glob('8.*'))[-1]; refpack=sorted((dot/'packs/Microsoft.NETCore.App.Ref').glob('8.*'))[-1];refs=list((refpack/'ref/net8.0').glob('*.dll'))
buildout=root/'Tools/terrain-verify/bin/Debug/net8.0';out=workpath/'bin'
shutil.copytree(buildout,out,ignore=shutil.ignore_patterns('AuthoredSample*'))
args=['-nologo','-target:exe','-out:'+str(out/'AuthoredSample.dll'),'-langversion:9','-nostdlib+']+['-r:'+str(p) for p in refs]+['-r:'+str(out/'TerrainVerify.dll'),str(workpath/'AuthoredSample.cs')]
(workpath/'authored.rsp').write_text('\n'.join('"'+a+'"' for a in args));(out/'AuthoredSample.runtimeconfig.json').write_text((out/'TerrainVerify.runtimeconfig.json').read_text())

subprocess.run([str(dotnet),str(sdk/'Roslyn/bincore/csc.dll'),'@'+str(workpath/'authored.rsp')],check=True,env=env)
subprocess.run([str(dotnet),str(out/'AuthoredSample.dll')],check=True,env=env)
work.cleanup()
