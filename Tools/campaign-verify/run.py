#!/usr/bin/env python3
"""Run campaign backend tests on .NET 8. Native Unity rendering/JsonUtility are NOT tested."""
import json,os,shutil,subprocess,sys,tempfile
from pathlib import Path
repo=Path(__file__).resolve().parents[2]
here=Path(__file__).resolve().parent
work=Path(os.environ.get('CAMPAIGN_VERIFY_WORK',Path(tempfile.gettempdir())/'campaign-verify'))
dotnet=Path(os.environ.get('DOTNET_BIN',shutil.which('dotnet') or '/tmp/campaign-dotnet/dotnet'))
if not dotnet.exists():sys.exit('Install .NET 8, or set DOTNET_BIN to its executable.')
sdk=sorted((dotnet.parent/'sdk').glob('8.*'))[-1]
work.mkdir(parents=True,exist_ok=True)
# Restore dependencies using the project's exact verification package versions.
project=(repo/'Tools/ai-verify/Check.csproj.template').read_text().replace('$(SrcAssets)',str(work/'empty'))
(work/'Restore.csproj').write_text(project)
assets=work/'obj/project.assets.json'
if not assets.exists():
 subprocess.run([str(dotnet),str(sdk/'MSBuild.dll'),str(work/'Restore.csproj'),'/t:Restore','/v:q','/m:1'],check=True)
source=work/'src'
if source.exists():shutil.rmtree(source)
shutil.copytree(repo/'Assets/Scripts',source/'Assets/Scripts')
# Old-reference API gaps are adapted ONLY in the disposable verification copy.
for p in (source/'Assets/Scripts').rglob('*.cs'):
 s=p.read_text().replace('FindObjectsInactive.Include, FindObjectsSortMode.None','FindObjectsSortMode.None').replace('FindObjectsInactive.Include)','FindObjectsSortMode.None)').replace('FindObjectsInactive.Exclude)','FindObjectsSortMode.None)')
 if p.name in ['CampaignState.cs','CampaignGeometry.cs','CampaignMapGenerator.cs']:s=s.replace('Vector2','CampaignTestVector2')
 p.write_text(s)
engine=(repo/'Tools/ai-verify/EngineStubs.cs').read_text().replace('public TMP_Text captionText { get; set; }','public TMP_Text captionText { get; set; }\n public TMP_Text itemText { get; set; }')
(work/'EngineStubs.cs').write_text(engine)
math=(repo/'Tools/ai-verify/TestRunStubs.cs').read_text();i=math.rfind('}')
math=math[:i]+''' public static float Log10(float v) => (float)System.Math.Log10(v);
 public static float SmoothDamp(float current,float target,ref float velocity,float smoothTime,float maxSpeed=float.PositiveInfinity,float deltaTime=.016f)=>target;
'''+math[i:]
(work/'TestRunStubs.cs').write_text(math)
(work/'CampaignSystemTests.cs').write_text((repo/'Assets/Editor/CampaignSystemTests.cs').read_text().replace('Vector2','CampaignTestVector2'))
for name in ['PortableJson.cs','PortableVector.cs','TestRunner.cs','ExtraStubs.cs','Experiments.cs']:shutil.copy(here/name,work/name)
out=work/'bin';out.mkdir(exist_ok=True)
refpack=sorted((dotnet.parent/'packs/Microsoft.NETCore.App.Ref').glob('8.*'))[-1]
refs=list((refpack/'ref/net8.0').glob('*.dll'))
restored=json.loads(assets.read_text());pkgroot=Path(next(iter(restored['packageFolders'])))
for key,value in next(iter(restored['targets'].values())).items():
 for rel in value.get('compile',{}):
  if rel.endswith('.dll'):
   p=pkgroot/key.lower()/rel;refs.append(p);shutil.copy(p,out/p.name)
files=[p for p in (source/'Assets/Scripts').rglob('*.cs') if p.name not in ['CampaignUI.cs','CampaignMapView.cs','CampaignRegionGraphic.cs','CampaignArrowGraphic.cs']]
files+=[work/p for p in ['EngineStubs.cs','TestRunStubs.cs','PortableJson.cs','PortableVector.cs','ExtraStubs.cs','TestRunner.cs','Experiments.cs','CampaignSystemTests.cs']]
args=['-nologo','-debug:portable','-target:exe','-out:'+str(out/'CampaignTests.dll'),'-langversion:9','-unsafe','-nostdlib+','-define:UNITY_INCLUDE_TESTS;UNITY_EDITOR;UNITY_2021_3_OR_NEWER;UNITY_6000_0_OR_NEWER','-nowarn:0436,0618,0649,0414,0169,0219,0162,0168,8632']
args+=['-r:'+str(p) for p in refs]+[str(p) for p in files]
(work/'compile.rsp').write_text('\n'.join('"'+arg+'"' for arg in args))
subprocess.run([str(dotnet),str(sdk/'Roslyn/bincore/csc.dll'),'@'+str(work/'compile.rsp')],check=True)
(out/'CampaignTests.runtimeconfig.json').write_text(json.dumps({'runtimeOptions':{'tfm':'net8.0','framework':{'name':'Microsoft.NETCore.App','version':refpack.name}}}))
subprocess.run([sys.executable,str(here/'extract_decks.py'),str(work/'real-decks.json')],cwd=repo,check=True)
print('Cloud logic tests: managed vector/JSON adapters; Unity scenes and native serialization NOT verified.',flush=True)
subprocess.run([str(dotnet),str(out/'CampaignTests.dll'),str(work/'real-decks.json')],check=True)
