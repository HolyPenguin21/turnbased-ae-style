`Sources/` contains the selected imagegen artwork, generated against the project's original
`Desert_01.png`. Seven lake phase panels form a 3×3 source atlas (last two unused); the canyon
uses one continuous square source. Art direction: flat top-down sandy beige, subdued olive water,
matte low contrast, no painted hex boundaries or raised board edges. The canyon follows
(0,0) → (1,0) → (1,1). Final prompts required source positions and a consistent fine desert
texture without text/UI, with only faint water ripples changing between lake phases.

```sh
python3 Tools/terrain-assets/extract.py
python3 Tools/terrain-assets/preview.py
```

Requires Pillow; diagnostic rendering also uses NumPy. Extraction outputs separate 512×512 square
textures. The actual runtime mesh clips each hex and owns alpha blending. Diagnostic images are
not Unity screenshots and must not be used as proof of live engine QA.
