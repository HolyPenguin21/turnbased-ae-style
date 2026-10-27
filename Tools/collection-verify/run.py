#!/usr/bin/env python3
"""Production collection/store/DeckRules tests using managed JSON/catalog dependencies."""
import json, os, shutil, subprocess, sys, tempfile
from pathlib import Path
repo = Path(__file__).resolve().parents[2]
here = Path(__file__).resolve().parent
work = Path(os.environ.get('COLLECTION_VERIFY_WORK', Path(tempfile.gettempdir()) / 'collection-verify'))
dotnet = Path(os.environ.get('DOTNET_BIN', shutil.which('dotnet') or '/tmp/campaign-dotnet/dotnet'))
if not dotnet.is_file(): sys.exit('Install .NET 8 or set DOTNET_BIN.')
sdk = sorted((dotnet.parent / 'sdk').glob('8.*'))[-1]
refpack = sorted((dotnet.parent / 'packs/Microsoft.NETCore.App.Ref').glob('8.*'))[-1]
nunit = Path(os.environ.get('NUNIT_DLL', Path.home() / '.nuget/packages/nunit/3.14.0/lib/netstandard2.0/nunit.framework.dll'))
if not nunit.is_file(): sys.exit('Set NUNIT_DLL to NUnit 3.14.0.')
work.mkdir(parents=True, exist_ok=True)
refs = list((refpack / 'ref/net8.0').glob('*.dll')) + [nunit]
files = [repo / 'Assets/Scripts' / p for p in ['Core/AtomicJsonFile.cs', 'Cards/DeckRules.cs', 'Progression/CollectionProfile.cs', 'Progression/CollectionProfileStore.cs', 'Progression/CollectionService.cs', 'Progression/RewardService.cs']]
files += [here / 'Adapters.cs', here / 'Runner.cs', repo / 'Tools/campaign-verify/PortableJson.cs', repo / 'Assets/Editor/Tests/Cards/LibraryRewardReceiptTests.cs']
args = ['-nologo', '-target:exe', '-out:' + str(work / 'CollectionTests.dll'), '-langversion:9', '-nostdlib+', '-define:UNITY_INCLUDE_TESTS']
args += ['-r:' + str(p) for p in refs] + [str(p) for p in files]
(work / 'compile.rsp').write_text('\n'.join('"' + a + '"' for a in args))
subprocess.run([str(dotnet), str(sdk / 'Roslyn/bincore/csc.dll'), '@' + str(work / 'compile.rsp')], check=True)
shutil.copy(nunit, work / nunit.name)
(work / 'CollectionTests.runtimeconfig.json').write_text(json.dumps({'runtimeOptions': {'tfm': 'net8.0', 'framework': {'name': 'Microsoft.NETCore.App', 'version': refpack.name}}}))
print('Managed adapters: Unity serialization, authored catalogs and scenes need Unity verification.', flush=True)
subprocess.run([str(dotnet), str(work / 'CollectionTests.dll')], check=True)
