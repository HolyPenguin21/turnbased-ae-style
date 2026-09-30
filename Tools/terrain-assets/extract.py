"""Extract independent square hex textures from generated artwork.
No paint/colour/alpha edits: the runtime mesh alone owns hex clipping and edge fade.
Requires Pillow. Source squares share a single world coordinate scale, so portions on
adjacent hex edges agree even though each texture remains a separate file.
"""
from pathlib import Path
import math
from PIL import Image
ROOT = Path(__file__).resolve().parents[2]
SOURCE = Path(__file__).resolve().parent / 'Sources'
OUT = ROOT / 'Assets/Textures/Terrain/Desert/Complexes'
OUT.mkdir(parents=True, exist_ok=True)

def extract(square, x, y, radius, output):
    w, h = square.size
    box = ((x-radius)*w, (y-radius)*h, (x+radius)*w, (y+radius)*h)
    square.transform((512, 512), Image.Transform.EXTENT, box, Image.Resampling.BICUBIC).save(output)

atlas = Image.open(SOURCE/'AcidLake_7PhaseSource.png').convert('RGB')
assert atlas.width == atlas.height and atlas.width % 3 == 0
size = atlas.width // 3
radius = .32 / 1.5
for frame in range(7):
    col, row = frame % 3, frame // 3
    square = atlas.crop((col*size, row*size, (col+1)*size, (row+1)*size))
    for part, (x,y) in enumerate([(.32,.62), (.64,.62-math.sqrt(3)*radius/2)], 1):
        extract(square, x, y, radius, OUT/f'AcidLake_Part{part}_{frame:02}.png')
canyon = Image.open(SOURCE/'Canyon_Source.png').convert('RGB')
for part,(x,y) in enumerate([(.35,.75),(.65,.75-math.sqrt(3)*.1),(.65,.75-math.sqrt(3)*.3)],1):
    extract(canyon,x,y,.2,OUT/f'Canyon_Part{part}.png')
print('Extracted 14 lake frames and 3 canyon parts:', OUT)
