# Enclosed acid lake shore correction

The user's Unity screenshot rejected the previous palette-only acceptance: water reached
multiple exterior hex sides, and banks did not integrate with their biome.

## Correction

New imagegen source atlases use continuous dry ground around a connected two-lobed olive lake.
Each biome is authored against its original Desert_01.png; seven phases vary only water ripples.
There is no land barrier between the two parts. The existing mesh still renders its dark shared seam.

Source-image sampling is independently fitted for each atlas to place the whole outer coastline
inside the existing hex footprints. Sampling centres remain 1.5 radii apart horizontally and
sqrt(3)/2 radii apart vertically, retaining identical shared world coordinates.
No runtime mesh, configuration, GUID, alpha, camera or game rule changes are needed.

Extraction and normalization can now select AcidLake only. Canyon, BoilingMud and wreck PNGs
remain unchanged. New sources are extracted before normalization, avoiding cumulative correction.

## Verification

validate_lake_shores.py checks all 28 frames: each part has five dry exterior edge bands,
and the shared side has an open central water connection. Its olive-water classifier is
specific to these atlases and is accompanied by assembled visual review, not used as a
general semantic segmentation model.

The existing config/asset validation and source-integrity check protect image dimensions,
modes, alpha, GUIDs and other assets. Independent audit and all-seven-phase previews are refreshed.

Status: full branch CI passed, including all 28 lake frames, shoreline checks, all-complex audit,
config validation, 68/68 alpha/dimension integrity and staged diff checks. Final assembled
previews of both biomes and all seven phases were inspected. Unity engine acceptance remains
unverified; this report does not repeat the previous claim of completed in-game acceptance.

Verification run: https://github.com/HolyPenguin21/turnbased-ae-style/actions/runs/36899608367
AridSteppe exterior olive-water sample max: 0.011; Desert: 0.004 (sparse texture false positives).
Central shared water window: 1.000 in every frame of both parts and biomes.
Only the 28 AcidLake runtime PNGs changed; other runtime textures, config and GUIDs did not.

## Generation record

Built-in imagegen, two biome-specific square 3x3 atlases. Inputs: exact two-hex footprint guide
and the biome's original Desert_01.png. Prompt requirements: seven occupied panels, last two
terrain-only; identical stationary ground and shore; muted olive acid with subtle cyclic ripples;
dry soil at all exterior sides; open water at the shared side; match original soil palette,
texture and lighting; no hex outlines, text, UI, grids, frame, orange bank stripe or raised coast.
