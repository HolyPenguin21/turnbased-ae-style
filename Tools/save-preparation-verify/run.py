#!/usr/bin/env python3
"""Managed policy/turn tests, all-C# syntax, optional runtime binding against cached old Unity refs.
No native Unity execution. All compatibility changes are in disposable verification copies.
"""
import argparse, json, os, re, shutil, subprocess, tempfile
from pathlib import Path
ROOT = Path(__file__).resolve().parents[2]
HERE = Path(__file__).resolve().parent
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--dotnet', default=os.environ.get('DOTNET_BIN') or shutil.which('dotnet'))
parser.add_argument('--packages', default=os.environ.get('NUGET_PACKAGES') or str(Path.home()/'.nuget/packages'))
parser.add_argument('--binding', action='store_true')
args = parser.parse_args()
if not args.dotnet: raise SystemExit('Set DOTNET_BIN or pass --dotnet to a .NET 8 SDK executable.')
dotnet = Path(args.dotnet).resolve()
sdk = sorted((dotnet.parent/'sdk').glob('8.*'))[-1]
refpack = sorted((dotnet.parent/'packs/Microsoft.NETCore.App.Ref').glob('8.*'))[-1]
refs = list((refpack/'ref/net8.0').glob('*.dll'))
work = Path(os.environ.get('SAVE_VERIFY_WORK', str(Path(tempfile.gettempdir())/'save-preparation-verify')))
work.mkdir(parents=True, exist_ok=True)
def compile(name, files, references=refs, extra=(), executable=True):
    output = work/(name+'.dll')
    options = ['-nologo','-langversion:9','-target:'+('exe' if executable else 'library'),'-out:'+str(output),
        '-nowarn:0436,0618,0649,0414,0169,0219,0162,0168,8632',*extra]
    rsp = work/(name+'.rsp')
    rsp.write_text('\n'.join('"'+str(x)+'"' for x in [*options,*['-r:'+str(r) for r in references],*files]))
    result = subprocess.run([str(dotnet),str(sdk/'Roslyn/bincore/csc.dll'),'@'+str(rsp)],capture_output=True,text=True)
    (work/(name+'.log')).write_text(result.stdout+result.stderr)
    if result.returncode:
        print((result.stdout+result.stderr)[-12000:])
        raise SystemExit(result.returncode)
    (work/(name+'.runtimeconfig.json')).write_text(json.dumps({'runtimeOptions':{'tfm':'net8.0','framework':{'name':'Microsoft.NETCore.App','version':refpack.name}}}))
    return output
for name, source in [('PolicyChecks',['Setup/MatchSaveEligibility.cs','Setup/MatchRestoreScope.cs']),('TurnChecks',['Turns/GameTurnState.cs'])]:
    out = compile(name,[HERE/(name+'.cs'),*[ROOT/'Assets/Scripts'/p for p in source]])
    subprocess.run([str(dotnet),str(out)],check=True)
roslyn = sdk/'Roslyn/bincore'
out = compile('SyntaxChecks',[ROOT/'Tools/responsive-ui-verify/SyntaxChecks.cs'], refs+[roslyn/'Microsoft.CodeAnalysis.dll',roslyn/'Microsoft.CodeAnalysis.CSharp.dll'])
for name in ['Microsoft.CodeAnalysis.dll','Microsoft.CodeAnalysis.CSharp.dll']:shutil.copy2(roslyn/name,work/name)
subprocess.run([str(dotnet),str(out),str(ROOT/'Assets')],check=True)
if args.binding:
    packages=Path(args.packages)
    engine=packages/'unityengine.modules/2021.3.33/lib/netstandard2.0'
    ui=list((packages/'unity3d.unityengine.ui/2020.3.21').rglob('UnityEngine.UI.dll'))
    if not engine.is_dir() or not ui:raise SystemExit('Restore the packages in Tools/ai-verify/Check.csproj.template, or pass --packages to their cache.')
    # Old package API adapters. These are never imported into the Unity project.
    stub=(ROOT/'Tools/ai-verify/EngineStubs.cs').read_text()
    stub=stub.replace('    public enum TextAlignmentOptions','    public enum VerticalAlignmentOptions { Top,Middle,Bottom,Baseline,Geometry,Capline }\n    public enum TextAlignmentOptions')
    stub=stub.replace('public TextAlignmentOptions alignment { get; set; }','public TextAlignmentOptions alignment { get; set; } public VerticalAlignmentOptions verticalAlignment {get;set;}')
    stub=stub.replace('public TMP_Text captionText { get; set; }','public TMP_Text captionText { get; set; } public TMP_Text itemText {get;set;} public UnityEngine.RectTransform template {get;set;}')
    (work/'EngineStubs.cs').write_text(stub)
    baseline=work/'base'
    if baseline.exists():shutil.rmtree(baseline)
    baseline.mkdir()
    archived=subprocess.run(['git','archive','HEAD','Assets/Scripts'],cwd=ROOT,capture_output=True,check=True)
    import io,tarfile
    with tarfile.open(fileobj=io.BytesIO(archived.stdout)) as archive:archive.extractall(baseline,filter='data')
    for label,src in [('baseline',baseline/'Assets/Scripts'),('current',ROOT/'Assets/Scripts')]:
        dest=work/label
        if dest.exists():shutil.rmtree(dest)
        shutil.copytree(src,dest)
        for f in dest.rglob('*.cs'):
            s=f.read_text()
            s=s.replace('FindObjectsInactive.Include, FindObjectsSortMode.None','FindObjectsSortMode.None')
            s=re.sub(r'(FindObjectsByType<[^>]+>\()FindObjectsInactive\.(?:Include|Exclude)(\))',r'\1FindObjectsSortMode.None\2',s)
            f.write_text(s)
        compile(label,[*dest.rglob('*.cs'),work/'EngineStubs.cs'],refs+list(engine.glob('*.dll'))+[ui[0]],['-define:UNITY_EDITOR;UNITY_6000_0_OR_NEWER'],False)
        print(label+': runtime binding passed (cached Unity 2021 refs + explicit package adapters)')
    nunit=packages/'nunit/3.14.0/lib/netstandard2.0/nunit.framework.dll'
    changed=subprocess.run(['git','diff','--name-only','HEAD'],cwd=ROOT,capture_output=True,text=True,check=True).stdout.splitlines()
    changed+=subprocess.run(['git','ls-files','--others','--exclude-standard'],cwd=ROOT,capture_output=True,text=True,check=True).stdout.splitlines()
    tests=[ROOT/p for p in sorted(set(changed)) if p.startswith('Assets/Editor/Tests/') and p.endswith('.cs') and not p.endswith('/DeckCollectionTests.cs')]
    print('DeckCollectionTests requires native Editor builders; its schema-version assertion remains for Unity.')
    if tests and nunit.is_file():
        compile('ChangedTests',[*dest.rglob('*.cs'),work/'EngineStubs.cs',*tests],refs+list(engine.glob('*.dll'))+[ui[0],nunit],['-define:UNITY_INCLUDE_TESTS;UNITY_EDITOR;UNITY_6000_0_OR_NEWER'],False)
        print(str(len(tests))+': changed/new EditMode test files bind successfully (not executed in Unity)')
print('Native Unity compilation, scene startup, JsonUtility and EditMode execution still require Unity 6000.5.4f1.')
