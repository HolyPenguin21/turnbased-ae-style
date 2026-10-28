#!/usr/bin/env python3
"""Run actual frozen-tree codec/MapObjectVisual with engine-only object/rendering surfaces."""
import os
import json
from pathlib import Path
import shutil
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent

def main():
    dotnet = os.environ.get('DOTNET') or shutil.which('dotnet')
    if not dotnet:
        raise SystemExit('Set DOTNET to a .NET 8+ SDK executable.')
    dotnet = Path(dotnet).resolve()
    latest = lambda values: max(values, key=lambda p: tuple(int(x) for x in p.name.split('.')))
    sdk = latest(p for p in (dotnet.parent / 'sdk').iterdir() if (p / 'Roslyn/bincore/csc.dll').is_file())
    pack = latest((dotnet.parent / 'packs/Microsoft.NETCore.App.Ref').iterdir())
    refs = next((pack / 'ref').iterdir())
    runtime = latest((dotnet.parent / 'shared/Microsoft.NETCore.App').iterdir())
    with tempfile.TemporaryDirectory(prefix='match-visual-roles-') as temporary:
        output = Path(temporary)
        sources = ['Map/MapObjectVisual.cs', 'Map/MapObjectVisual.Persistence.cs', 'Map/MapObjectVisual.SaveSource.cs',
                   'Saves/MatchPresentationData.cs', 'Saves/MatchPresentationVisualCodec.cs', 'Saves/MatchSaveJson.cs']
        sources = [HERE.parents[2] / 'Assets/Scripts' / source for source in sources] + [HERE / 'Adapters.cs', HERE / 'Program.cs']
        arguments = ['-nologo', '-nowarn:0649,0414', '-target:exe', '-out:' + str(output / 'checks.dll')]
        arguments += ['-r:' + str(p) for p in refs.glob('*.dll')] + [str(p) for p in sources]
        response = output / 'compile.rsp'
        response.write_text('\n'.join('"' + value + '"' for value in arguments))
        result = subprocess.run([str(dotnet), str(sdk / 'Roslyn/bincore/csc.dll'), '@' + str(response)], cwd=output)
        if result.returncode:
            return result.returncode
        (output / 'checks.runtimeconfig.json').write_text(json.dumps({'runtimeOptions': {'tfm': refs.name,
            'framework': {'name': 'Microsoft.NETCore.App', 'version': runtime.name}}}))
        return subprocess.run([str(dotnet), str(output / 'checks.dll')], cwd=output).returncode

if __name__ == '__main__':
    raise SystemExit(main())
