"""Check configured pre-lit terrain variants and their actual imported PNG references."""
from pathlib import Path
import hashlib, json, re

root=Path(__file__).resolve().parents[2]
manifest=json.loads((root/'Docs/terrain-complexes/directional-assets/offsets.json').read_text())
config=(root/'Assets/Config/GameConfig.asset').read_text()
arid,desert=config.split('    desertOverride:',1)
assert 'randomizeRotation: 1' not in arid
blocks=re.findall(r'      - contentKey: [^\n]+\n        name: (.+?)\n(.*?)(?=      - contentKey: |      mountainsTerrainName:)',desert,re.S)
centerpieces=[(name,body) for name,body in blocks if 'exclusiveGroup: centerpiece' in body]
assert len(centerpieces)==2
assert {name for name, _ in centerpieces} == {
 'Desert Giant machine wreck directional 000',
 'Desert Giant machine wreck directional 060',
}
arid_wrecks=re.findall(r'    - contentKey: [^\n]+\n      name: (.+?)\n(.*?)(?=    - contentKey: |    complexCount:)',arid,re.S)
arid_wrecks=[body for _,body in arid_wrecks if 'terrainName: Giant machine wreck\n' in body]
assert len(arid_wrecks)==1
assert 'useInGeneration: 1' in arid_wrecks[0]
assert 'randomizeRotation: 0' in arid_wrecks[0]
assert re.findall(r'offset: \{x: (-?\d+), y: (-?\d+)\}',arid_wrecks[0]) == [('0','0'),('1','0')]
assert "Deep canyon" not in desert
counts={}
refs=set()
for family,entry in manifest['families'].items():
 if family == 'Canyon': continue  # Historical manifest; Desert canyon assets were removed.
 assert [v['angle_degrees'] for v in entry['variants']] == [0,60]
 label='Desert Giant machine wreck'
 counts[family]=0
 for variant in entry['variants']:
  name=label+f' directional {variant["angle_degrees"]:03d}'
  body=next(body for n,body in centerpieces if n==name)
  assert 'terrainName: Giant machine wreck\n' in body
  assert 'useInGeneration: 1' in body, name
  assert 'randomizeRotation: 0' in body
  weight=int(re.search(r'        count: (\d+)',body)[1])
  assert weight==1, name
  counts[family]+=weight
  actual=re.findall(r'offset: \{x: (-?\d+), y: (-?\d+)\}\n          frames:\n          - \{fileID: 2800000, guid: ([a-f0-9]{32}), type: 3\}',body)
  expected=[(str(p['offset']['x']),str(p['offset']['y']),p['guid']) for p in variant['parts']]
  assert actual==expected, name
  cells={(p['offset']['x'],p['offset']['y']) for p in variant['parts']}
  assert len(cells)==len(variant['parts']) and 1<=len(cells)<=3
  seen={next(iter(cells))}
  while True:
   expanded=seen|{(q+dq,r+dr) for q,r in seen for dq,dr in [(1,0),(0,1),(-1,1),(-1,0),(0,-1),(1,-1)] if (q+dq,r+dr) in cells}
   if expanded==seen: break
   seen=expanded
  assert seen==cells, name
  for p in variant['parts']:
   file=root/p['file']; data=file.read_bytes()
   assert data[:8]==b'\x89PNG\r\n\x1a\n'
   assert hashlib.sha256(data).hexdigest()==p['sha256']
   meta=(root/(p['file']+'.meta')).read_text()
   assert re.search(r'^guid: (.+)$',meta,re.M)[1]==p['guid']
   assert re.search(r'^  textureType: (0|8)$', meta, re.M) and '    sRGBTexture: 1' in meta
   assert all(f'    wrap{x}: 1' in meta for x in 'UVW')
   assert p['guid'] not in refs
   refs.add(p['guid'])
assert counts=={'GiantMachineWreck':2}
assert len(refs)==4
expected_files={p['file'] for v in manifest['families']['GiantMachineWreck']['variants'] for p in v['parts']}
directional=root/'Assets/Textures/Terrain/Desert/Complexes/Directional'
assert {p.relative_to(root).as_posix() for p in directional.glob('GiantMachineWreck_*.png')} == expected_files
assert {p.relative_to(root).as_posix() for p in directional.glob('GiantMachineWreck_*.png.meta')} == {p+'.meta' for p in expected_files}
assert 'randomizeRotation: 1' not in desert
print('PASS: Desert wreck directions 000/060; 4 PNG hashes/GUIDs/settings; no extra wreck assets; single fixed Arid wreck; Desert canyon absent; UV rotation disabled.')
