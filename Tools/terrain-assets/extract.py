"""Extract independent square hex textures from generated artwork.
No paint/colour/alpha edits: the runtime mesh alone owns hex clipping and edge fade.
Requires Pillow. Source squares share a single world coordinate scale, so portions on
adjacent hex edges agree even though each texture remains a separate file.
"""
from pathlib import Path
import math
import argparse
import yaml
from PIL import Image
ROOT = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser()
parser.add_argument('--biome', choices=['Desert', 'AridSteppe'], default='Desert')
parser.add_argument('--group', choices=['AcidLake', 'Canyon', 'BoilingMud', 'GiantMachineWreck'])
args = parser.parse_args()
# Legacy multi-hex atlases cannot reproduce the new footprints. Fail before writing
# any texture rather than silently restoring the removed parts or replacing imported art.
raw=(ROOT/'Assets/Config/GameConfig.asset').read_text()
settings=yaml.safe_load('\n'.join(line for line in raw.splitlines() if not line.startswith(('%','---'))))['MonoBehaviour']['mapGeneration']
palette=settings if args.biome=='AridSteppe' else settings['desertOverride']
legacy={'AcidLake':('Acid lake',2),'BoilingMud':('Boiling mud field',2),'GiantMachineWreck':('Giant machine wreck',3)}
for family,(name,part_count) in legacy.items():
    if args.group not in (None,family): continue
    template=next(t for t in palette['complexes'] if t['terrainName']==name)
    if len(template['parts']) != part_count:
        raise SystemExit(f'{family}: legacy atlas has {part_count} parts, but config uses {len(template["parts"])}. Supply new source art; existing runtime textures were not changed.')
SOURCE = Path(__file__).resolve().parent / 'Sources'
if args.biome == 'AridSteppe': SOURCE = SOURCE / 'AridSteppe'
OUT = ROOT / 'Assets/Textures/Terrain' / args.biome / 'Complexes'
OUT.mkdir(parents=True, exist_ok=True)

def extract(square, x, y, radius, output):
    w, h = square.size
    box = ((x-radius)*w, (y-radius)*h, (x+radius)*w, (y+radius)*h)
    square.transform((512, 512), Image.Transform.EXTENT, box, Image.Resampling.BICUBIC).save(output)

if args.group in (None, 'AcidLake'):
    atlas = Image.open(SOURCE/'AcidLake_7PhaseSource.png').convert('RGB')
    assert atlas.width == atlas.height and atlas.width % 3 == 0
    size = atlas.width // 3
    # Source-image sampling only: keep every outer coast inside the existing hex mesh.
    radius = .28 if args.biome == 'AridSteppe' else .27
    first_center = (.27, .57) if args.biome == 'AridSteppe' else (.27, .655)
    centers = [first_center, (first_center[0] + 1.5*radius, first_center[1]-math.sqrt(3)*radius/2)]
    for frame in range(7):
        col, row = frame % 3, frame // 3
        square = atlas.crop((col*size, row*size, (col+1)*size, (row+1)*size))
        for part, (x,y) in enumerate(centers, 1):
            extract(square, x, y, radius, OUT/f'AcidLake_Part{part}_{frame:02}.png')

if args.group in (None, 'Canyon'):
    canyon = Image.open(SOURCE/'Canyon_Source.png').convert('RGB')
    for part,(x,y) in enumerate([(.35,.75),(.65,.75-math.sqrt(3)*.1),(.65,.75-math.sqrt(3)*.3)],1):
        extract(canyon,x,y,.2,OUT/f'Canyon_Part{part}.png')
    print('Extracted 14 lake frames and 3 canyon parts:', OUT)

    # New complexes use the same continuous source sampling as lakes/canyons.

if args.group in (None, 'BoilingMud'):
    atlas = Image.open(SOURCE/'BoilingMud_7PhaseSource.png').convert('RGB')
    assert atlas.width == atlas.height and atlas.width % 3 == 0
    size = atlas.width // 3
    radius = .32 / 1.5
    for frame in range(7):
        col, row = frame % 3, frame // 3
        square = atlas.crop((col*size, row*size, (col+1)*size, (row+1)*size))
        for part, (x,y) in enumerate([(.32,.62), (.64,.62-math.sqrt(3)*radius/2)], 1):
            extract(square,x,y,radius,OUT/f'BoilingMud_Part{part}_{frame:02}.png')

if args.group in (None, 'GiantMachineWreck'):
    wreck = Image.open(SOURCE/'GiantMachineWreck_Source.png').convert('RGB')
    for part in range(3):
        extract(wreck,.20+.30*part,.72-math.sqrt(3)*.1*part,.2,OUT/f'GiantMachineWreck_Part{part+1}.png')
    print('Extracted 14 mud frames and 3 wreck parts:', OUT)


