#!/usr/bin/env python3
"""Managed policy/turn tests, all-C# syntax, optional runtime binding against cached old Unity refs.
No native Unity execution. All compatibility changes are in disposable verification copies.
"""
import argparse, io, json, os, re, shutil, subprocess, tarfile, tempfile
from pathlib import Path
ROOT = Path(__file__).resolve().parents[2]
HERE = Path(__file__).resolve().parent
def resolve_base(root, revision):
    result = subprocess.run(['git', 'rev-parse', '--verify', '--end-of-options', revision+'^{commit}'],
        cwd=root, capture_output=True, text=True)
    if result.returncode:
        raise ValueError(f'{revision!r} is not a valid commit for --base')
    return result.stdout.strip()


def archive_baseline(root, base, destination):
    archived = subprocess.run(['git', 'archive', base, 'Assets/Scripts'],
        cwd=root, capture_output=True, check=True)
    if destination.exists(): shutil.rmtree(destination)
    destination.mkdir(parents=True)
    with tarfile.open(fileobj=io.BytesIO(archived.stdout)) as archive:
        archive.extractall(destination, filter='data')


def changed_tests(root, base):
    changed = subprocess.run(['git', 'diff', '--name-only', '-z', base, '--'],
        cwd=root, capture_output=True, check=True).stdout.split(b'\0')
    changed += subprocess.run(['git', 'ls-files', '--others', '--exclude-standard', '-z'],
        cwd=root, capture_output=True, check=True).stdout.split(b'\0')
    names = {os.fsdecode(path) for path in changed if path}
    return [root/path for path in sorted(names)
        if path.startswith('Assets/Editor/Tests/') and path.endswith('.cs')
        and not path.endswith('/DeckCollectionTests.cs') and (root/path).is_file()]


def require_test_dependency(tests, nunit):
    if tests and not nunit.is_file():
        raise ValueError(f'NUnit is required to bind selected EditMode fixtures: {nunit}')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', default=os.environ.get('DOTNET_BIN') or shutil.which('dotnet'))
    parser.add_argument('--packages', default=os.environ.get('NUGET_PACKAGES') or str(Path.home()/'.nuget/packages'))
    parser.add_argument('--binding', action='store_true')
    parser.add_argument('--base', help='Explicit commit to compare against when binding')
    args = parser.parse_args()
    base = None
    if args.binding:
        if not args.base: parser.error('--base is required with --binding')
        try: base = resolve_base(ROOT, args.base)
        except ValueError as error: parser.error(str(error))
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
        archive_baseline(ROOT, base, baseline)
        print('Baseline commit: '+base)
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
        tests=changed_tests(ROOT, base)
        print('DeckCollectionTests requires native Editor builders; its schema-version assertion remains for Unity.')
        print(str(len(tests))+': changed/new EditMode test files selected against baseline')
        try: require_test_dependency(tests, nunit)
        except ValueError as error: raise SystemExit(str(error))
        if tests:
            compile('ChangedTests',[*dest.rglob('*.cs'),work/'EngineStubs.cs',*tests],refs+list(engine.glob('*.dll'))+[ui[0],nunit],['-define:UNITY_INCLUDE_TESTS;UNITY_EDITOR;UNITY_6000_0_OR_NEWER'],False)
            print(str(len(tests))+': changed/new EditMode test files bind successfully (not executed in Unity)')
    print('Native Unity compilation, scene startup, JsonUtility and EditMode execution still require Unity 6000.5.4f1.')


if __name__ == '__main__':
    main()
