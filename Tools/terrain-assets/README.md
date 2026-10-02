## Current generation layout

See `Docs/terrain-complexes/generation-options-and-footprints.md` for the current configuration:
AcidLake and BoilingMud are single hexes, Wreck has two hexes, and Canyon has three.
Each biome template has `Use In Generation`; `Complex Count = 0` still disables all.
AcidLake uses imported, closed-shore bubble animation at 6.25 FPS. Mud and wreck art is
provisional pending replacement. `preview.py` reads the actual configured footprint.

The atlas extraction and normalization history below describes the former multi-hex
sources. `extract.py` refuses those obsolete footprints before writing any textures;
do not run legacy extraction to recreate the imported single-hex lake. The
`--normalization-base` option checks historical palette-only edits, not this footprint
and asset replacement change.

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

## Palette normalization

Generated complex textures must be normalized after extraction so each biome keeps its own visual
range. The normalizer reads every ordinary terrain texture configured for that biome (including
alternative textures), builds an independent AridSteppe/Desert reference envelope, and fits one
shared OKLab transform per complex family. Desert, Sand dunes and Rock desert receive extra target
weight because complexes can actually border those terrains. Distinctive acid/mud/canyon/wreck pixels receive no forced feature correction; terrain-like
ground receives the main correction. Animated-family edge refinement compresses broad soil
shading, with additional crack-detail attenuation for BoilingMud.

Animation frames are deliberately never corrected independently: AcidLake and BoilingMud use one
transform for the whole 14-file set in a biome, preventing brightness or hue flicker between phases.
The script preserves image dimensions and alpha.

```sh
python3 Tools/terrain-assets/normalize_clusters.py --write --report
python3 Tools/terrain-assets/validate.py
```

The report and before/after contact sheets are written to `Docs/terrain-complexes/`. After replacing
or regenerating cluster art, run normalization before accepting the assets.

## Final offline acceptance

The reviewed asset baseline is `3774b1c`; the coverage/integrity update is `2014d7a`.
See `Docs/terrain-complexes/final-visual-review.md` for the final review and its scope.

The complete audit includes all 68 runtime PNGs, 24 configured ordinary references per biome,
all seven frames of each animated family, and the last-to-first loop transition.
`runtime-pixel-audit.csv` retains all per-file full-image and edge metrics.
Preview produces lake and mud GIFs and seven-frame contact sheets for both biomes.

The edge score is a colour-distance heuristic, not an acceptance threshold: the closest 30%
of pixels can still contain shoreline, cracks, canyon walls or wreckage. Inspect the exposed
soil and assembled complexes before adjusting a feature merely to reduce this number.

For an integrity check against authored source PNGs (the base commit must be locally available):

```sh
python Tools/terrain-assets/audit_clusters.py
python Tools/terrain-assets/validate.py --normalization-base c3d52f46ad99cc06f4772b702e4e9b38efbc96c4
```

This verifies unchanged dimensions, image modes and alpha for 68 PNGs, and rejects other
asset/config/GUID changes. Normalization workflows restore source PNGs from this base before
processing, avoiding cumulative correction. Offline acceptance is separate from Unity rendering.

