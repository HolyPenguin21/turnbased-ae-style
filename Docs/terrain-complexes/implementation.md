# Terrain complexes — implementation and validation

Base: master `e9dbb6451541ceea78deb1b7c4a83902826c5718`.
Branch: `feature/terrain-complexes`.

## Behavior

The **Desert biome** is configured with two acid lake pairs and one three-cell bent canyon per
map, up to 64 attempts per complex. Failed attempts leave all terrain assignments unchanged.
Arid retains its authored palette. Templates are authored in the existing biome settings and
reference the existing terrain entries by name. A template accepts 2–3 connected axial offsets,
allowed underlying types, selected rotations 0–5, count, retry limit, animation FPS, and equal
frame sequences per part. Compact triangular groups are supported and covered by tests; no
triangular canyon art is shipped in this first set.

City ruins, mountain ranges and content-bearing cells are excluded by the default templates.
Every placement checks the entire remaining passable ground graph, rejecting the entire complex
if it disconnects the map. Generation runs before setup content. Resources, neutral armies,
start sites and events only use ground-passable cells; the shared yield calculator also returns
zero on blocked terrain.

`HexMap.CanEnter` owns terrain admission. `blocksGroundMovement` defaults to false, preserving
mountains and all old terrain. Path search and forward/reverse cost fields cannot exempt a terrain
obstacle as a destination. Air routes keep flat costs and fly/stop over these cells. Movement
rechecks each next cell and commits activation only after a legal affordable first entry exists;
a rejected stale first step consumes no MP, AP or activation Energy. No battle math changed.
Retreat neighbor selection delegates to the same admission rule.

Ground Recon's direct neighbor/lookahead choices, frontier flood and reaction choices exclude
obstacles. Snapshot pricing uses actual route costs for scouts, vantage cells, assault and
reinforcement; unreachable routes are not substituted with straight distance. Vision/risk distances
remain geometric. `SafeStepPathing` air costs bypass its ground-only caches. Map snapshot reuse
checks both map identity and `PathingVersion`. Call `SetTerrainAt` after runtime terrain edits,
including edits to a shared terrain entry, to advance the revision.

The combined hex mesh and `HexBlend.shader` keep their original six-edge alpha band. Per-complex
part materials carry separate square textures; UV rotation follows the rotated footprint.
`MapTerrainAnimator` is persistent after the generator destroys itself and changes material textures
only. Each complex has one phase clock. It owns generated materials across regeneration/unload.
Frames do not mutate map data, route revisions, vision, labels, highlights or render sorting.

## Assets

`Assets/Textures/Terrain/Desert/Complexes/` contains fourteen lake images (two parts × seven phases)
and three static canyon parts, all 512×512. Source images were generated with imagegen against
`Desert_01.png`; the actual gameplay reference screenshot was inspected. Final parts are extracted
in a consistent world coordinate system from one complete source image for each phase, so both
parts agree at their shared edge. No stitched multi-cell layer is drawn in the game.

`Tools/terrain-assets/Sources/` retains the selected generated sources. `extract.py` performs only
slicing/resampling, no painting, tinting or alpha edits. `preview.py` produces the two diagnostic
images in this folder using the actual texture UV convention and configured blend=0.15/alpha=0.95.
**These are offline diagnostic renders, not Unity screenshots.** URP colour space/camera angle,
fog and markers are not emulated. The original mesh/shader files were not modified.

## Completed validation

- 22 focused checks executed against actual algorithm sources using managed Unity API substitutes:
  default mountains, ground/air admission, zero resource yield, detour and unreachable paths,
  forbidden terminal exemption, forward/reverse/air fields, terrain revision, atomic pair placement,
  edge/reserved/overlap/protected rejection, narrow bridge connectivity, six rotations of snake and
  triangle, disconnected/duplicate shapes, unequal frames, seven-phase looping, synchronized parts
  without revision changes, ground cache invalidation, air/ground cache isolation, and stale first
  movement step without activation or MP charge.
- Full project source compile compared with the base using the repository's Unity reference
  assemblies and engine stubs: 27 existing stub/UI/editor errors in both revisions, **0 new errors**.
  This differential check is not a clean Unity build.
- Unity YAML reference/type checks passed for GameConfig; new texture GUID references, packed
  rotations, square dimensions, frame counts and metadata verified separately.
- Offline visual inspection: lake/canyon share their internal drawing, all internal and external
  soft dark seams remain visible beside ordinary desert.

## Outstanding Unity acceptance — do not mark complete without it

Unity 6000.5.4f1 is not installed in this environment. No Unity screenshot or live PlayMode result
was produced. The full existing EditMode/AI suite and resource-bank integration were not executed.
Two additional tests in `TerrainComplexTests` cover fully blocked ground retreat (air is allowed)
and scout pricing of an unreachable target; these require real project collaborators in Unity and
were compiled but not run here.

1. Run EditMode suite, especially TerrainComplexTests, route-cache, Recon, Attack, aviation and
   deployment/transaction tests. Run movement PlayMode tests.
2. Start the Desert biome at Small and Huge sizes; verify all players have ground access and starts,
   events, resources, garrisons and bases never occupy a lake/canyon.
3. At the actual in-game zoom, capture lake and canyon beside ordinary desert; inspect all six-edge
   fades, rotations, shore/pattern continuity, animation phase and texture brightness. Check two
   lake pairs: each pair stays synchronized, phases may differ between pairs.
4. Ground orders/preview must detour or reject the lake before payment. Alter the first route cell
   during selection settling and confirm no activation/AP/MP/Energy charge.
5. Air overflight and stopping: verify existing fuel/contact/MP behavior remains intact.
6. Block every ground retreat neighbor and verify the existing no-retreat outcome. Exercise AI
   scouts/builders/assault around a canyon and refresh terrain mid-turn through SetTerrainAt.
7. Verify FOW, coordinates, markers and highlight order while frames advance, and verify animation
   continues after HexMapGenerator is destroyed.
