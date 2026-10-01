# Final terrain complex visual review

Date: 2026-10-01
Repository: HolyPenguin21/turnbased-ae-style
Branch: asset/terrain-complex-palette-normalization
Authored source / unchanged master: c3d52f46ad99cc06f4772b702e4e9b38efbc96c4
Reviewed runtime asset baseline: 3774b1c6106c07063fd556204b6cb59582502fce

## Outcome

Offline visual acceptance is complete for the normalized asset set. No further RGB correction
was warranted after inspecting the latest comparative sheets and assembled complexes.
This accepts biome colour/tonal integration, not identical feature contrast across terrain types.
Unity rendering and live map generation were not run in this environment.

The final asset refinement already present at 3774b1c reduces excess BoilingMud soil/crack
contrast in both biomes. This continuation completes independent review, animation coverage
and source-integrity verification; it does not regenerate geometry or replace authored features.

## Coverage

Each biome was reviewed independently against all 24 configured ordinary textures:
8 Desert, 4 Rock desert, 4 Sand dunes, 4 City ruins and 4 Mountains, including alternatives.
The 16 placement-compatible references provide the immediate soil comparison, while city/mountain
references establish the broader biome style.

| Biome | AcidLake | Canyon | BoilingMud | GiantMachineWreck | Total |
|---|---:|---:|---:|---:|---:|
| AridSteppe | 14 | 3 | 14 | 3 | 34 |
| Desert | 14 | 3 | 14 | 3 | 34 |

Every animated part has seven reviewed phases, including the 06 -> 00 loop.
The independent audit retains all 68 per-file rows in runtime-pixel-audit.csv.
All animated-family contact sheets now show metrics for all 14 files, rather than stopping at eight.

## Visual findings

| Family | AridSteppe | Desert |
|---|---|---|
| AcidLake | Brown-ochre exposed banks fit the arid references; muted olive water remains readable. | Sandy banks fit the brighter desert references; olive water remains distinct. |
| Canyon | Ground matches the brown arid range; dark walls read as depth rather than a global dark cast. | Light sand matches the surrounding range; canyon shadows remain legible. |
| BoilingMud | Latest refinement softens cracked soil; grey-brown mud and bubbles retain their identity. | Sand rim matches the ordinary sand range; mud is locally darker without a broad background cast. |
| GiantMachineWreck | Soil fits the arid palette; metal/debris account for the stronger local detail. | Sand is consistent with the desert palette; rust and wreck shadows remain distinct from the ground. |

Assembled offline diagnostics preserve the narrow dark hex joints, including internal boundaries.
Reviewed phase sheets show no conspicuous whole-background brightness/hue jump.
Mud motion and acid ripples remain visible. Feature silhouettes and the existing footprints
are retained; no smoothing of the entire obstacle to the ordinary terrain contrast was applied.

## How to interpret remaining metric differences

The edge-score algorithm selects the closest 30% of outer-ring pixels; it does not semantically
label soil. Water/shore transitions, cracks, canyon walls and wreckage crossing that ring can
remain in the sample. In particular, AcidLake has little exposed soil, so its higher contrast
score must not alone drive further flattening of water and banks.

The latest numeric report still records higher local edge contrast than ordinary sand for some
families. This is disclosed, not treated as proof of an exact statistical match. The visual
decision uses exposed ground colour and assembled context alongside those measurements.

## Verification

- Normalization is rerun from the verified authored source commit, avoiding cumulative processing.
- Per-file background comparison improves for 68/68 PNGs.
- validate.py verifies both palettes, four templates per biome, configured frames, GUID resolution,
  resource yields and ground passability.
- The source-integrity check verifies unchanged dimensions, image modes and alpha for 68/68 PNGs.
- Asset changes outside the 68 runtime complex PNGs are rejected, protecting config, meta/GUIDs,
  ordinary terrain and runtime code.
- Independent animation metrics now include last-to-first transitions.
- Updated previews include all seven lake and mud phases for both biomes, plus looping GIFs.
- Python syntax checks and the complete GitHub Actions validation pass.

## Evidence

Successful full verification: [GitHub Actions run 36894272956](https://github.com/HolyPenguin21/turnbased-ae-style/actions/runs/36894272956).
Generated audit/preview commit: afeaed5d1ae35078d6e0c78484c4209fdde211fa.
Comparison against 3774b1c confirms zero additional runtime PNG changes: rerunning from authored
sources reproduces the accepted runtime set exactly.


- independent-visual-audit.md
- runtime-pixel-audit.csv
- aridsteppe-reference-terrain-sheet.png / desert-reference-terrain-sheet.png
- Eight family audit sheets, one per family and biome
- aridsteppe-lake-all-frames-offline.png / desert-lake-all-frames-offline.png
- aridsteppe-mud-all-frames-offline.png / desert-mud-all-frames-offline.png
- Lake/mud looping GIFs and static assembled canyon/wreck diagnostics

## Remaining engine acceptance

Unity colour space, URP, camera tilt, fog and live randomized map generation are outside
these offline diagnostics. They remain an engine acceptance check, not an unfinished asset
normalization step. No merge or push to master was performed.
