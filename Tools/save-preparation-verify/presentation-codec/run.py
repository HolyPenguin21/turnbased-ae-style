#!/usr/bin/env python3
"""Execute production detached human-history codec with existing integration owner adapters.
Native frozen renderer decoding is intentionally excluded; its EditMode fixture requires Unity.
"""
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[2]
spec = importlib.util.spec_from_file_location('integration_runner', HERE.parent / 'integration/run.py')
integration = importlib.util.module_from_spec(spec)
spec.loader.exec_module(integration)

def main():
    dotnet = Path(os.environ.get('DOTNET') or shutil.which('dotnet') or '').resolve()
    if not dotnet.is_file(): raise SystemExit('Set DOTNET to a .NET 8+ SDK executable.')
    sdk = integration.latest([p for p in (dotnet.parent / 'sdk').iterdir() if (p/'Roslyn/bincore/csc.dll').is_file()])
    pack = integration.latest([p for p in (dotnet.parent/'packs/Microsoft.NETCore.App.Ref').iterdir() if (p/'ref').is_dir()])
    refs = integration.latest([p for p in (pack/'ref').iterdir() if p.name.startswith('net')])
    with tempfile.TemporaryDirectory(prefix='presentation-codec-') as temporary:
        output = Path(temporary)
        adapters = (HERE.parent/'integration/PortableAdapters.cs').read_text()
        turn_adapter = adapters.split('public partial class GameTurnController', 1)[1].split('namespace Game.Setup', 1)[0]
        if ' gameConfig;' not in turn_adapter:
            adapters = adapters.replace('public bool IsCombatPresentationActive;', 'public bool IsCombatPresentationActive; public Game.Core.GameConfig gameConfig;')
        adapters = adapters.replace('bool warn, object owner)', 'bool warn, object owner = null)')
        adapters = adapters.replace('public int armyVisionRadius, buildingVisionRadius;', 'public int armyVisionRadius, buildingVisionRadius; public Game.Cards.CardDefinition[] extractionFacilityCards;')
        adapters = adapters.replace('public float x, z; public Vector3', 'public float x, y, z; public Vector3')
        adapters = adapters.replace('this.x = x; this.z = z;', 'this.x = x; this.y = y; this.z = z;')
        adapters = adapters.replace('public struct Color {', 'public struct Color { public float r,g,b,a;')
        (output/'Adapters.cs').write_text(adapters)
        sources = [REPO/'Assets/Scripts'/p for p in integration.SOURCES]
        sources += [REPO/'Assets/Scripts/Saves/MatchPresentationData.cs', REPO/'Assets/Scripts/Saves/MatchPresentationCodec.cs', output/'Adapters.cs', HERE/'Program.cs', HERE.parents[1]/'campaign-verify/PortableJson.cs']
        helper = REPO/'Assets/Scripts/Setup/CitadelSetupController.Restore.cs'
        if helper.exists(): sources.append(helper)
        arguments = ['-nologo','-nowarn:0649,0414','-target:exe','-langversion:9','-out:'+str(output/'checks.dll')]
        arguments += ['-r:'+str(p) for p in refs.glob('*.dll')]
        arguments += [str(p) for p in sources]
        response = output/'compile.rsp'
        response.write_text('\n'.join('"'+x+'"' for x in arguments))
        result = subprocess.run([str(dotnet),str(sdk/'Roslyn/bincore/csc.dll'),'@'+str(response)],cwd=REPO)
        if result.returncode:return result.returncode
        installed = integration.latest([p for p in (dotnet.parent/'shared/Microsoft.NETCore.App').iterdir() if p.is_dir()])
        (output/'checks.runtimeconfig.json').write_text(json.dumps({'runtimeOptions':{'tfm':refs.name,'framework':{'name':'Microsoft.NETCore.App','version':installed.name}}}))
        return subprocess.run([str(dotnet),str(output/'checks.dll')],cwd=REPO).returncode
if __name__ == '__main__': raise SystemExit(main())
