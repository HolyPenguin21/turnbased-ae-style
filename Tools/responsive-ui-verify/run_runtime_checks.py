#!/usr/bin/env python3
"""Run source checks and real C# hand math without requiring a native Unity editor.

Usage: python Tools/responsive-ui-verify/run_runtime_checks.py --dotnet /path/to/dotnet
No dependencies are downloaded. This is not a Unity compile or an interaction test run.
"""
import argparse
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]
HERE = Path(__file__).resolve().parent


def run(*args):
    subprocess.run([str(a) for a in args], cwd=ROOT, check=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', default=shutil.which('dotnet'))
    args = parser.parse_args()
    run(sys.executable, HERE / 'test_runtime_layout.py')
    paths = ['Assets/Scenes/MainMenu.unity', 'Assets/Scenes/Game.unity',
             'Assets/Prefabs/UI/Rows/PlayerRow.prefab', 'Assets/Prefabs/UI/Collection/CollectionCard.prefab']
    for validator in ['verify_full.js', 'verify_types.js']:
        run('node', ROOT / 'Tools/unity-yaml-verify' / validator, *paths)
    if not args.dotnet:
        raise SystemExit('C# checks require --dotnet pointing to an installed .NET 8 SDK')
    dotnet = Path(args.dotnet).resolve()
    sdk_root = dotnet.parent
    csc = sorted((sdk_root / 'sdk').glob('8.*/Roslyn/bincore/csc.dll'))[-1]
    refs = sorted((sdk_root / 'packs/Microsoft.NETCore.App.Ref').glob('8.*/ref/net8.0'))[-1]
    version = sorted((sdk_root / 'shared/Microsoft.NETCore.App').glob('8.*'))[-1].name
    with tempfile.TemporaryDirectory(prefix='responsive-ui-') as folder:
        work = Path(folder)
        references = ['-r:' + str(p) for p in refs.glob('*.dll')]
        for name, source, extra in [
            ('GeometryChecks', HERE / 'GeometryChecks.cs',
             [str(ROOT / 'Assets/Scripts/UI/Cards/CardRowGeometry.cs')]),
            ('SyntaxChecks', HERE / 'SyntaxChecks.cs',
             ['-r:' + str(csc.parent / 'Microsoft.CodeAnalysis.dll'),
              '-r:' + str(csc.parent / 'Microsoft.CodeAnalysis.CSharp.dll')])]:
            response = work / (name + '.rsp')
            response.write_text('\n'.join('"' + s + '"' for s in
                ['-nologo', '-target:exe', '-out:' + str(work / (name + '.dll')),
                 *references, str(source), *extra]))
            run(dotnet, csc, '@' + str(response))
            (work / (name + '.runtimeconfig.json')).write_text(json.dumps({'runtimeOptions': {
                'tfm': 'net8.0', 'framework': {'name': 'Microsoft.NETCore.App', 'version': version}}}))
        for name in ['Microsoft.CodeAnalysis.dll', 'Microsoft.CodeAnalysis.CSharp.dll']:
            shutil.copy2(csc.parent / name, work / name)
        run(dotnet, work / 'GeometryChecks.dll')
        run(dotnet, work / 'SyntaxChecks.dll', ROOT / 'Assets')


if __name__ == '__main__':
    main()
