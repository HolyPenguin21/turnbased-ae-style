#!/usr/bin/env python3
"""Portable cache regression/measurement runner. Native Unity UI is deliberately excluded.

Reuses the existing world-codec verification adapters, only in disposable source copies.
Requires DOTNET (SDK 8 executable) and NUGET_PACKAGES with the ai-verify dependencies.
"""
import argparse
import io
import os
from pathlib import Path
import shutil
import subprocess
import tarfile

ROOT = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--work', type=Path, required=True, help='disposable output directory')
parser.add_argument('--baseline', help='read runtime sources from this git revision')
mode = parser.add_mutually_exclusive_group()
mode.add_argument('--performance', action='store_true', help='measure caches instead of running regression fixtures')
mode.add_argument('--bottom-up', action='store_true', help='measure cache layers, live callers and invalidation boundaries')
parser.add_argument('--where', help='optional NUnit filter')
args = parser.parse_args()
work = args.work.resolve()
work.mkdir(parents=True, exist_ok=True)
if work.is_relative_to(ROOT) or ROOT.is_relative_to(work):
    parser.error('work must be outside the repository and must not contain it')

source_root = ROOT
if args.baseline:
    source_root = work / 'baseline'
    source_root.mkdir(exist_ok=True)
    revision = subprocess.check_output(['git', 'rev-parse', '--verify', '--end-of-options',
        args.baseline + '^{commit}'], cwd=ROOT, text=True).strip()
    if (source_root / 'Assets').exists():
        shutil.rmtree(source_root / 'Assets')
    archive = subprocess.run(['git', 'archive', revision, 'Assets/Scripts'],
        cwd=ROOT, check=True, stdout=subprocess.PIPE).stdout
    with tarfile.open(fileobj=io.BytesIO(archive)) as files:
        files.extractall(source_root, filter='data')
    if not (source_root / 'Tools').exists():
        (source_root / 'Tools').symlink_to(ROOT / 'Tools', target_is_directory=True)

os.environ['MATCH_INTEGRATION_WORK'] = str(work)
# Disable tier transitions so successive deck sizes do not accidentally measure different
# JIT tiers. Both baseline and working-tree assemblies are compiled with optimizations.
os.environ['DOTNET_TieredCompilation'] = '0'
script = ROOT / 'Tools/match-save-verify/audit-world/native-run.py'
helper = script.read_text()
marker = "\ncompile('Integration'"
if marker not in helper:
    raise RuntimeError('World verification adapter entry point changed; review this runner before reuse')
prefix = helper.split(marker, 1)[0]
prefix = prefix.replace('ROOT = Path(__file__).resolve().parents[3]', 'ROOT = Path(' + repr(str(source_root)) + ')')
prefix = prefix.replace("'-unsafe'", "'-unsafe','-optimize+'").replace(
    'UNITY_EDITOR;UNITY_6000_0_OR_NEWER', 'UNITY_INCLUDE_TESTS;UNITY_EDITOR;UNITY_6000_0_OR_NEWER')
scope = {'__file__': str(script)}
exec(compile(prefix, str(script), 'exec'), scope)
source = scope['SOURCE']

# Reference DLLs have no working Vector3 constructors/operators. Substitute exactly the
# managed field arithmetic used by these path tie-breaks; no search/cache rule is replaced.
file = source / 'HexGrid/HexGridMath.cs'
file.write_text(file.read_text().replace('return new Vector3(x, 0f, z);',
    'return new Vector3 { x = x, y = 0f, z = z };'))
file = source / 'HexGrid/HexPathfinder.cs'
text = file.read_text().replace(
    'HexGridMath.AxialToWorld(destination.Q, destination.R, 1f) - startPlane',
    'CacheNativeGeometry.Subtract(HexGridMath.AxialToWorld(destination.Q, destination.R, 1f), startPlane)').replace(
    'HexGridMath.AxialToWorld(h.Q, h.R, 1f) - startPlane',
    'CacheNativeGeometry.Subtract(HexGridMath.AxialToWorld(h.Q, h.R, 1f), startPlane)').replace(
    'lineDir.magnitude', 'Mathf.Sqrt(lineDir.x * lineDir.x + lineDir.y * lineDir.y + lineDir.z * lineDir.z)')
file.write_text(text)
file = source / 'Ai/SafeStepPathing.cs'
file.write_text(file.read_text().replace('map != _cacheMap', '!object.ReferenceEquals(map, _cacheMap)'))
for file in source.rglob('*.cs'):
    text = file.read_text().replace('gameObject.scene.handle.GetRawData()', '(ulong)IntegrationNative.Frame')
    text = text.replace('UnityEngine.Mathf.', 'global::Mathf.')
    if file.name == 'AiDebugLog.cs' and 'private static void WriteCore(' in text:
        text = scope['body'](text, 'private static void WriteCore(', '')
    file.write_text(text)
math = work / 'Math.cs'
math.write_text(math.read_text().replace('public static class Mathf\n{', '''public static class Mathf
{
 public static float Floor(float f) => (float)System.Math.Floor(f);
 public static float MoveTowards(float current,float target,float maxDelta) => System.Math.Abs(target-current)<=maxDelta ? target : current+System.Math.Sign(target-current)*maxDelta;'''))
with (work / 'EngineStubs.cs').open('a') as stream:
    stream.write('''
namespace UnityEngine.TestTools { public static class LogAssert {
 public static void Expect(UnityEngine.LogType type,System.Text.RegularExpressions.Regex match) {} } }
public static class CacheNativeGeometry {
 public static UnityEngine.Vector3 Subtract(UnityEngine.Vector3 a,UnityEngine.Vector3 b) =>
  new UnityEngine.Vector3 { x=a.x-b.x, y=a.y-b.y, z=a.z-b.z }; }
''')

diagnostic = args.performance or args.bottom_up
fixtures = ['Tools/cache-verify/CachePerformanceProbe.cs'] if diagnostic else [
    'Assets/Editor/Tests/AI/' + name + '.cs' for name in (
        'AiActionPriceCacheTests', 'WorthItEstimateCacheTests', 'AiEquipmentDeltaCacheTests',
        'AiRouteCacheIsolationTests', 'AiCombatCacheLifecycleTests', 'AiEconomyAdmissionFingerprintTests',
        'AiTradeFundingTests', 'AiStrategicSpendabilityApTests', 'AiReservationInvariantsTests',
        'AiTurnResourceBookTests', 'AiProductionScoreAlignmentTests', 'AiUnifiedTaskScoreTests',
        'AiEconomyReservationLifecycleTests', 'AiPowerOptimizationTests',
        'AiAviationSortieCycleTests', 'AiTurnPacingAndVerdictKeyTests')]
if args.bottom_up:
    fixtures += ['Tools/cache-verify/BottomUpCacheProbe.cs']
if not diagnostic:
    fixtures += ['Assets/Editor/Tests/AI/AiEquipmentEfficiencyTests.cs']
if not diagnostic:
    # Existing cross-fixture helper dependencies; their tests remain visible in the result.
    fixtures += ['Assets/Editor/Tests/Cards/AttachmentSlotTests.cs',
        'Assets/Editor/Tests/Cards/AttachmentContentTestData.cs',
        'Assets/Editor/Tests/Combat/ExchangeMemoAndProfileEqualityTests.cs',
        'Assets/Editor/Tests/Map/MovementEnvironmentTests.cs']
copies = []
for path in fixtures:
    original = ROOT / path
    copy = work / original.name
    if args.baseline and not diagnostic:
        blob = subprocess.run(['git', 'show', f'{revision}:{path}'], cwd=ROOT,
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        if blob.returncode != 0:
            # A new fixture can refer to APIs introduced by the current revision.
            if path == 'Assets/Editor/Tests/AI/AiActionPriceCacheTests.cs':
                continue
            raise RuntimeError(blob.stderr.strip())
        text = blob.stdout
    else:
        text = original.read_text()
    if copy.name == 'AiRouteCacheIsolationTests.cs':
        text = text.replace('_mapObject = new UnityEngine.GameObject("route-profile-map");\n            HexMap map = _mapObject.AddComponent<HexMap>();',
            'HexMap map = IntegrationNative.New<HexMap>();').replace('UnityEngine.Object.DestroyImmediate(_mapObject);', '')
    if copy.name == 'MovementEnvironmentTests.cs':
        text = text.replace('_object = new GameObject("MovementEnvironmentTests");\n            _map = _object.AddComponent<HexMap>();',
            '_map = IntegrationNative.New<HexMap>();').replace('Object.DestroyImmediate(_object);', '')
    if copy.name in ('AiActionPriceCacheTests.cs', 'AiTurnResourceBookTests.cs'):
        text = text.replace('UnityEngine.Object.DestroyImmediate(root.gameObject);', 'System.GC.KeepAlive(root);')
    if copy.name == 'AiTradeFundingTests.cs':
        text = text.replace('if (root != null) Object.DestroyImmediate(root.gameObject);', '')
    if copy.name == 'CachePerformanceProbe.cs':
        text = text.replace('return new UnityEngine.GameObject("cache-benchmark-map").AddComponent<Game.Map.HexMap>();',
            'return IntegrationNative.New<Game.Map.HexMap>();').replace('UnityEngine.Object.DestroyImmediate(map.gameObject);', 'GC.KeepAlive(map);')
    if copy.name == 'BottomUpCacheProbe.cs':
        text = text.replace('return new UnityEngine.GameObject("bottom-up-map").AddComponent<HexMap>();',
            'return IntegrationNative.New<HexMap>();').replace('UnityEngine.Object.DestroyImmediate(map.gameObject);', 'GC.KeepAlive(map);')
        text = text.replace('UnityEngine.Object.DestroyImmediate(root.gameObject);', 'GC.KeepAlive(root);')
    copy.write_text(text)
    copies.append(copy)

program = (ROOT / 'Tools/match-save-verify/audit-world/native-program.cs').read_text().split('internal static class WorldAuditProgram')[0]
program += '''public static class CacheMain {
 public static int Main(string[] args) => new NUnitLite.AutoRun(typeof(CacheMain).Assembly).Execute(args); }'''
(work / 'Program.cs').write_text(program)
packages = Path(os.environ['NUGET_PACKAGES'])
nunit = packages / 'nunit/3.14.0/lib/netstandard2.0/nunit.framework.dll'
nunitlite = packages / 'nunitlite/3.14.0/lib/netstandard2.0/nunitlite.dll'
scope['compile']('Caches', [*source.rglob('*.cs'), work/'EngineStubs.cs', work/'Math.cs', work/'Program.cs', *copies],
    [*scope['refs'], nunit, nunitlite])
references = [*scope['ENGINE'].glob('*.dll'), scope['UI'], nunit, nunitlite]
for file in references:
    shutil.copy2(file, work / file.name)
scope['compile']('ReferenceAdapter', [ROOT/'Tools/save-preparation-verify/ai/ReferenceAdapter.cs'], list(scope['REF'].glob('*.dll')))
subprocess.run([str(scope['DOTNET']), str(work/'ReferenceAdapter.dll'),
    *[str(work/f.name) for f in references if f not in (nunit, nunitlite)]], check=True)
command = [str(scope['DOTNET']), str(work/'Caches.dll'), '--labels=Off', '--workers=0', '--result='+str(work/'result.xml')]
if args.where:
    command += ['--where', args.where]
elif args.bottom_up:
    command += ['--where', 'class == Game.EditorTests.BottomUpCacheProbe']
test_cwd = ROOT
if not diagnostic:
    # Tests read authored card catalogs without AssetDatabase. A sparse source checkout
    # excludes the catalogs, and a baseline must read the matching revision's assets.
    catalog_paths = [Path(f'Assets/Cards/{faction}/CardCatalog_{faction}.asset')
        for faction in ('Neutral', 'IronConcord', 'TheAshen', 'TheVessels')]
    if args.baseline or any(not (ROOT / path).is_file() for path in catalog_paths):
        test_cwd = work / 'authored-fixtures'
        for path in catalog_paths:
            target = test_cwd / path
            target.parent.mkdir(parents=True, exist_ok=True)
            if not args.baseline and (ROOT / path).is_file():
                shutil.copy2(ROOT / path, target)
            else:
                with target.open('wb') as output:
                    ref = revision if args.baseline else 'HEAD'
                    subprocess.run(['git', 'show', f'{ref}:{path.as_posix()}'], cwd=ROOT,
                        stdout=output, check=True)
raise SystemExit(subprocess.run(command, cwd=test_cwd).returncode)
