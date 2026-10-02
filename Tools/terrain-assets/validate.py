"""Validate authored terrain references without loading Unity. Requires PyYAML/Pillow."""
from pathlib import Path
from io import BytesIO
import argparse
import re, subprocess
import yaml
from PIL import Image
ROOT = Path(__file__).resolve().parents[2]
parser=argparse.ArgumentParser()
parser.add_argument('--normalization-base',help='Git commit containing the authored PNGs; verify RGB-only scope against it')
args=parser.parse_args()
raw=(ROOT/'Assets/Config/GameConfig.asset').read_text()
config=yaml.safe_load('\n'.join(line for line in raw.splitlines() if not line.startswith(('%','---'))))['MonoBehaviour']['mapGeneration']
guids={}
for meta in (ROOT/'Assets').rglob('*.meta'):
    match=re.search(r'^guid: ([0-9a-f]+)$',meta.read_text(),re.M)
    if match:
        assert match[1] not in guids, f'Duplicate GUID: {meta}'
        guids[match[1]]=Path(str(meta)[:-5])
expected={'Acid lake':(2,1,7),'Deep canyon':(1,3,1),'Boiling mud field':(1,1,7),'Giant machine wreck':(1,2,1)}
for biome,palette in [('AridSteppe',config),('Desert',config['desertOverride'])]:
    terrain={t['terrainName']:t for t in palette['terrainTypes']}
    assert not terrain['Mountains'].get('blocksGroundMovement',0)
    assert set(t['terrainName'] for t in palette['complexes'])==set(expected)
    for template in palette['complexes']:
        count,parts,frames=expected[template['terrainName']]
        assert template['count']==count and len(template['parts'])==parts
        assert template['useInGeneration'] in (0, 1)
        assert 'rotations' not in template
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
                image=Image.open(path);image.verify()
                image=Image.open(path);image.load()
                assert image.width==image.height and image.width>=512
                if template['terrainName'] != 'Deep canyon': assert image.size==(512,512)
    print(f'{biome}: 4 templates, GUIDs, palette, frames, yields and passability valid')
print('Authored asset checks passed; Unity generation/render checks are separate.')
if args.normalization_base:
    base=args.normalization_base
    subprocess.run(['git','rev-parse','--verify',base+'^{commit}'],cwd=ROOT,check=True,stdout=subprocess.DEVNULL)
    paths=[]
    for biome in ('AridSteppe','Desert'):
        directory=ROOT/'Assets/Textures/Terrain'/biome/'Complexes'
        files=sorted(directory.glob('*.png'))
        assert len(files)==34, f'{biome}: expected 34 runtime PNGs, found {len(files)}'
        paths.extend(files)
    for path in paths:
        relative=path.relative_to(ROOT).as_posix()
        source=subprocess.check_output(['git','show',f'{base}:{relative}'],cwd=ROOT)
        with Image.open(BytesIO(source)) as before, Image.open(path) as after:
            assert before.size==after.size, f'Dimensions changed: {relative}'
            assert before.mode==after.mode, f'Image mode changed: {relative}'
            assert before.convert('RGBA').getchannel('A').tobytes()==after.convert('RGBA').getchannel('A').tobytes(), f'Alpha changed: {relative}'
    allowed={p.relative_to(ROOT).as_posix() for p in paths}
    changed=subprocess.check_output(['git','diff',base,'--name-only','--','Assets'],cwd=ROOT,text=True).splitlines()
    assert set(changed)<=allowed, f'Unexpected asset/config/meta changes: {sorted(set(changed)-allowed)}'
    print(f'Normalization integrity: {len(paths)}/68 dimensions, modes and alpha unchanged; config/GUIDs/other assets unchanged.')

