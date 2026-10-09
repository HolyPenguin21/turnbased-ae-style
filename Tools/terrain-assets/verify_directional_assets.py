"""Check configured pre-lit terrain variants and their actual imported PNG references."""
from pathlib import Path
import hashlib, json, re

root=Path(__file__).resolve().parents[2]
manifest=json.loads((root/'Docs/terrain-complexes/directional-assets/offsets.json').read_text())
config=(root/'Assets/Config/GameConfig.asset').read_text()
arid,desert=config.split('    desertOverride:',1)
assert 'randomizeRotation: 1' not in arid
blocks=re.findall(r'      - name: (.+?)\n(.*?)(?=      - name: |      mountainsTerrainName:)',desert,re.S)
centerpieces=[(name,body) for name,body in blocks if 'exclusiveGroup: centerpiece' in body]
assert len(centerpieces)==12
counts={}
refs=set()
for family,entry in manifest['families'].items():
 label='Canyon snake' if family=='Canyon' else 'Desert Giant machine wreck'
 counts[family]=0
 for variant in entry['variants']:
  name=label+f' directional {variant["angle_degrees"]:03d}'
  body=next(body for n,body in centerpieces if n==name)
  assert 'randomizeRotation: 0' in body
  counts[family]+=int(re.search(r'        count: (\d+)',body)[1])
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
   assert '  textureType: 0' in meta and '    sRGBTexture: 1' in meta
   assert all(f'    wrap{x}: 1' in meta for x in 'UVW')
   assert p['guid'] not in refs
   refs.add(p['guid'])
assert counts=={'Canyon':6,'GiantMachineWreck':6}
assert len(refs)==30
assert 'randomizeRotation: 1' not in desert
print('PASS: 12 connected orientation templates; 30 PNG hashes/GUIDs/settings; family weights 6:6; UV rotation disabled.')
