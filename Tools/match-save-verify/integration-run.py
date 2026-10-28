#!/usr/bin/env python3
"""Execute actual full file codecs and scene restore transaction with explicit native-only adapters.
No codec/source/validation implementation is replaced. Unity rendering and activation remain untested.
"""
import json, os, re, shutil, subprocess, tempfile
from pathlib import Path
ROOT = Path(__file__).resolve().parents[2]
HERE = Path(__file__).resolve().parent
DOTNET = Path(os.environ['DOTNET']).resolve()
SDK = sorted((DOTNET.parent/'sdk').glob('8.*'))[-1]
REF = sorted((DOTNET.parent/'packs/Microsoft.NETCore.App.Ref').glob('8.*'))[-1]/'ref/net8.0'
PACKAGES = Path(os.environ['NUGET_PACKAGES'])
ENGINE = PACKAGES/'unityengine.modules/2021.3.33/lib/netstandard2.0'
UI = next((PACKAGES/'unity3d.unityengine.ui/2020.3.21').rglob('UnityEngine.UI.dll'))
WORK = Path(os.environ.get('MATCH_INTEGRATION_WORK', tempfile.mkdtemp(prefix='match-file-integration-')))
WORK.mkdir(parents=True, exist_ok=True)
SOURCE = WORK/'runtime'
if SOURCE.exists(): shutil.rmtree(SOURCE)
shutil.copytree(ROOT/'Assets/Scripts', SOURCE)
def edit(path, change):
    file=SOURCE/path; old=file.read_text(); new=change(old)
    if old==new: raise RuntimeError('Native adapter no longer matches '+path)
    file.write_text(new)
def body(text, signature, replacement):
    begin=text.index('{', text.index(signature)); depth=1; end=begin+1
    while depth:
        if text[end]=='{': depth+=1
        elif text[end]=='}': depth-=1
        end+=1
    return text[:begin]+'{'+replacement+'}'+text[end:]
for file in SOURCE.rglob('*.cs'):
    text=file.read_text().replace('FindObjectsInactive.Include, FindObjectsSortMode.None','FindObjectsSortMode.None')
    text=re.sub(r'(FindObjectsByType<[^>]+>\()FindObjectsInactive\.(?:Include|Exclude)(\))',r'\1FindObjectsSortMode.None\2',text)
    # Reference DLL Object equality is a native throw body. There are no native objects or
    # destroyed Unity handles in this managed fixture: use CLR identity for null checks only.
    text=re.sub(r'(?<![\w?.])([A-Za-z_]\w*(?:\??\.[A-Za-z_]\w*)*)\s*([!=]=)\s*null',lambda m:('!' if m[2]=='!=' else '')+'object.ReferenceEquals('+m[1]+', null)',text)
    text=text.replace('Time.frameCount','IntegrationNative.Frame')
    file.write_text(text)
# Native reference Vector3 constructor throws; supply only its three field assignments.
edit('HexGrid/HexGridMath.cs',lambda t:t.replace('return new Vector3(x, 0f, z);','return new Vector3 { x = x, y = 0f, z = z };'))
edit('Terrain/MapGenerationSettings.cs',lambda t:re.sub(r'new Color\([^)]*\)', 'default(Color)', t))
edit('Saves/MatchFileRestoreSource.cs',lambda t:t.replace('requestedConfig != config','!object.ReferenceEquals(requestedConfig, config)'))
edit('Setup/CitadelSetupController.Restore.cs',lambda t:t.replace('Debug.LogException(exception);','System.Console.WriteLine(exception);'))
edit('Campaign/CampaignMatchBridge.cs',lambda t:t.replace('Application.persistentDataPath','IntegrationNative.ProfileDirectory'))
edit('Map/PlayerRoot.cs',lambda t:t.replace('new GameObject(name).AddComponent<PlayerRoot>()','IntegrationNative.New<PlayerRoot>()').replace('root.Color = PlayerColorPalette.Colors[setup.ColorIndex];','root.Color = default;'))
edit('Map/HexMap.cs',lambda t:t.replace('public class HexMap : MonoBehaviour\n    {','public class HexMap : MonoBehaviour\n    {\n        public new T GetComponent<T>() => default;'))
edit('Turns/GameTurnController.cs',lambda t:body(body(t,'private void InitializeMatchPresentation()',''), 'private void RecomputeBlockedState()', ''))
edit('UI/Utilities/UIFocusUtility.cs',lambda t:body(t,'public static bool IsTextFieldFocused()', 'return false;'))
edit('Saves/MatchSaveAssetManifest.cs',lambda t:re.sub(r'private static MatchSaveAssetManifest Manifest => Resources.Load<MatchSaveAssetManifest>\(ResourceName\)\s*\?\? throw new InvalidOperationException\([^;]+;', 'private static MatchSaveAssetManifest Manifest => IntegrationNative.Manifest;', t))
# Palette values are renderer-only. Keep the actual palette length for validation.
edit('Styles/PlayerColorPalette.cs',lambda t: re.sub(r'public static readonly Color\[\] Colors\s*=\s*\{.*?\};', 'public static readonly Color[] Colors = new Color[6];', t, flags=re.S))
stub=(ROOT/'Tools/ai-verify/EngineStubs.cs').read_text()
stub=stub.replace('    public enum TextAlignmentOptions','    public enum VerticalAlignmentOptions { Top,Middle,Bottom,Baseline,Geometry,Capline }\n    public enum TextAlignmentOptions')
stub=stub.replace('public TextAlignmentOptions alignment { get; set; }','public TextAlignmentOptions alignment { get; set; } public VerticalAlignmentOptions verticalAlignment {get;set;}')
stub=stub.replace('public TMP_Text captionText { get; set; }','public TMP_Text captionText { get; set; } public TMP_Text itemText {get;set;} public UnityEngine.RectTransform template {get;set;}')
(WORK/'EngineStubs.cs').write_text(stub)
math=(ROOT/'Tools/ai-verify/TestRunStubs.cs').read_text().replace('    public const float Epsilon','    public static float Log10(float value) => (float)System.Math.Log10(value);\n    public const float Epsilon')
(WORK/'Math.cs').write_text(math)
refs=[*REF.glob('*.dll'), *ENGINE.glob('*.dll'), UI]
def compile(name, sources, references):
    rsp=WORK/(name+'.rsp'); rsp.write_text('\n'.join('"'+str(x)+'"' for x in ['-nologo','-target:exe','-langversion:9','-unsafe','-define:UNITY_EDITOR;UNITY_6000_0_OR_NEWER','-nowarn:0436,0618,0649,0414,0169,0219,0162,0168,8632','-out:'+str(WORK/(name+'.dll')),*['-r:'+str(r) for r in references],*sources]))
    subprocess.run([str(DOTNET),str(SDK/'Roslyn/bincore/csc.dll'),'@'+str(rsp)],check=True)
    (WORK/(name+'.runtimeconfig.json')).write_text(json.dumps({'runtimeOptions':{'tfm':'net8.0','framework':{'name':'Microsoft.NETCore.App','version':'8.0.0'}}}))
compile('Integration', [*SOURCE.rglob('*.cs'), WORK/'EngineStubs.cs', WORK/'Math.cs', HERE/'integration-program.cs'], refs)
for file in [*ENGINE.glob('*.dll'), UI]: shutil.copy2(file,WORK/file.name)
compile('ReferenceAdapter',[ROOT/'Tools/save-preparation-verify/ai/ReferenceAdapter.cs'],list(REF.glob('*.dll')))
subprocess.run([str(DOTNET),str(WORK/'ReferenceAdapter.dll'),*[str(WORK/file.name) for file in ENGINE.glob('*.dll')]],check=True)
subprocess.run([str(DOTNET),str(WORK/'Integration.dll')],check=True)
