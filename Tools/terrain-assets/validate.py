"""Validate authored terrain references without loading Unity. Requires PyYAML/Pillow."""
from pathlib import Path
import re, subprocess
import yaml
from PIL import Image
ROOT = Path(__file__).resolve().parents[2]
raw=(ROOT/'Assets/Config/GameConfig.asset').read_text()
config=yaml.safe_load('\n'.join(line for line in raw.splitlines() if not line.startswith(('%','---'))))['MonoBehaviour']['mapGeneration']
guids={}
for meta in (ROOT/'Assets').rglob('*.meta'):
    match=re.search(r'^guid: ([0-9a-f]+)$',meta.read_text(),re.M)
    if match:
        assert match[1] not in guids, f'Duplicate GUID: {meta}'
        guids[match[1]]=Path(str(meta)[:-5])
expected={'Acid lake':(2,2,7),'Deep canyon':(1,3,1),'Boiling mud field':(1,2,7),'Giant machine wreck':(1,3,1)}
for biome,palette in [('AridSteppe',config),('Desert',config['desertOverride'])]:
    terrain={t['terrainName']:t for t in palette['terrainTypes']}
    assert not terrain['Mountains'].get('blocksGroundMovement',0)
    assert set(t['terrainName'] for t in palette['complexes'])==set(expected)
    for template in palette['complexes']:
        count,parts,frames=expected[template['terrainName']]
        assert template['count']==count and len(template['parts'])==parts
        assert template['rotations']==int('000000000100000002000000030000000400000005000000',8)
        assert set(template['allowedTerrainNames'])=={'Desert','Sand dunes','Rock desert'}
        assert all(not terrain[n].get('blocksGroundMovement',0) for n in template['allowedTerrainNames'])
        entry=terrain[template['terrainName']]
        assert entry['baselineWeight']==0 and entry['blocksGroundMovement']==1
        assert not any(entry['resourceYields'].values())
        assert entry['texture']==template['parts'][0]['frames'][0]
        offsets={(p['offset']['x'],p['offset']['y']) for p in template['parts']}
        assert len(offsets)==parts
        for part in template['parts']:
            assert len(part['frames'])==frames
            for frame in part['frames']:
                path=guids[frame['guid']]
                assert path.is_relative_to(ROOT/'Assets/Textures/Terrain'/biome/'Complexes')
                image=Image.open(path);image.load();assert image.size==(512,512)
    print(f'{biome}: 4 templates, GUIDs, palette, frames, yields and passability valid')
print('Authored asset checks passed; Unity generation/render checks are separate.')
