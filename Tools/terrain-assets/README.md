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


AridSteppe uses its own imagegen sources in `Sources/AridSteppe/`, referenced against the
original `AridSteppe/Desert_01.png` for brown-grey ochre soil, dusty shrubs and fine stones.
The Desert sources supplied only the footprint/layout reference. No recolouring of final
Desert tiles was used. Extract and inspect with:

```sh
python3 Tools/terrain-assets/extract.py --biome AridSteppe
python3 Tools/terrain-assets/preview.py --biome AridSteppe
```

The default remains Desert. `preview.py` prefixes generated diagnostic names by biome.
See `Docs/terrain-complexes/arid-obstacle-fix-report.md` for prompts, ownership, validation
and the separate Unity acceptance work.

Boiling mud and giant machine wreck use separate built-in imagegen sources for each biome.
Exact prompts are in `Sources/MudAndWreck-Prompts.md`. Mud has seven atlas phases and two
synchronous parts; the static wreck samples a straight three-hex chain. Both use the existing
hex mesh fade, including their internal edges. Each template requests one placement and
allows six rotations on Desert, Sand dunes or Rock desert. Replacement textures/frames
are configured per biome in GameConfig; no runtime code or extra terrain store is needed.

```sh
python3 Tools/terrain-assets/validate.py
```

Validation additionally needs PyYAML. Offline mud GIFs show all seven phases, not Unity capture.
