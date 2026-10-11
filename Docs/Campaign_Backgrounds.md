# Campaign background variants

New campaigns select one of ten full-bleed desert landscapes. The choice is stored in `CampaignState.BackgroundId` and remains fixed through turns, battles, save/reload and recovery. Selection uses an independent seeded stream; geometry, ownership and gameplay RNG are unchanged. Consecutive campaigns may legitimately select the same image.

IDs 1–10 map to `Assets/Resources/Campaign/WastelandSurface_01.jpg` … `_10.jpg`. ID 0 keeps the original `WastelandSurface.jpg` for old saves. Each production texture is 1254×1254, opaque RGB JPEG; metadata uses unique GUIDs, clamp wrapping, no mipmaps and a 2048 import cap. Only the selected texture is loaded at runtime. Terrain features are decoration and grant no gameplay effects.

| ID | Landscape | Production asset |
| --- | --- | --- |
| 01 | Dune Sea | WastelandSurface_01.jpg |
| 02 | Riftlands | WastelandSurface_02.jpg |
| 03 | Crater Basin | WastelandSurface_03.jpg |
| 04 | Salt Flats | WastelandSurface_04.jpg |
| 05 | Basalt Ridges | WastelandSurface_05.jpg |
| 06 | Dry Riverlands | WastelandSurface_06.jpg |
| 07 | Mesa Highlands | WastelandSurface_07.jpg |
| 08 | Broken Badlands | WastelandSurface_08.jpg |
| 09 | Wreck Expanse | WastelandSurface_09.jpg |
| 10 | Eroded Plateaus | WastelandSurface_10.jpg |

`Campaign_Backgrounds_ContactSheet.jpg` compares all ten raw textures. `Campaign_Backgrounds_Review.jpg` overlays the same actual seed-7 polygons, faction tints, markers, amber selection and nine-sliced frame over each background. `Campaign_Asset_Composite.jpg` shows the full screen using seed 7's saved background choice (ID 1). These are deterministic CPU asset compositions; native Unity shader interpolation, imports and input still need verification.

## Generation prompts

Created using the built-in imagegen tool, one request per asset. The original `Assets/Resources/Campaign/WastelandSurface.jpg` was the style reference, not an overwrite target. Images were exported to JPEG without changing composition or colors. No regions, faction colors or UI were baked into the backgrounds.

Every request used this common prefix, followed by `Primary request: ` and the matching landscape description below:

```text
Use case: style-transfer. Asset type: production terrain texture for the existing procedural Unity planetary campaign. Input image is STYLE REFERENCE only. Create a completely NEW geography; retain its warm beige/grey-sand/graphite palette, restrained brightness, fine hand-drawn etched terrain detail and subtle shaded pseudo-3D relief. Extremely distant aerial terrain atlas, broad expanse, many small and medium landmarks, low oblique top-down viewing angle matching reference. FULL BLEED SQUARE terrain texture covering the entire canvas edge-to-edge, evenly detailed and evenly lit, no sky, no visible planet edge, no horizon, no vignette, no border, no frame, no UI, no text, no region outlines, no labels, no colored territories, no markers. Not photorealistic, no new saturation, no fantasy colors. Preserve similar fine relief linework and overall luminosity so three translucent faction tints remain readable.
```

### 01 — Dune Sea

```text
A broad diagonal field of fine wind-carved dunes interrupted by low rock islands, scattered small craters and dry channels. Mix dune fields with open readable areas, no huge empty center.
```

### 02 — Riftlands

```text
Several winding deep eroded canyon systems divide sandstone mesas, with crossing dry tributaries, small buttes and sparse crater fields. No single giant canyon dominates.
```

### 03 — Crater Basin

```text
A varied desert plateau with many differently sized old impact craters, scattered rim mountains, fine dry river networks and weathered rocky flats. Avoid one oversized central crater.
```

### 04 — Salt Flats

```text
Pale beige dry salt basins between worn rocky ridges, branching desiccated river beds, small dune pockets and scattered erosion pits. No bright white glare.
```

### 05 — Basalt Ridges

```text
Dark muted graphite basalt ridges and broken volcanic plateaus mixed with tan desert plains, tiny extinct vents and meandering dusty ravines. No glowing lava or bright red.
```

### 06 — Dry Riverlands

```text
An intricate branching dry river network weaving between low terraced mesas, sandy wash fans, isolated buttes and scattered small craters, many medium-small landmarks.
```

### 07 — Mesa Highlands

```text
A chain of numerous layered sandstone mesas separated by eroded valleys, desert terraces, scattered dunes and minor crater basins. Keep relief smaller and distributed.
```

### 08 — Broken Badlands

```text
Dense intricately eroded badlands in grey-sand and beige, narrow branching gullies, low broken ridges, weathered plateaus and flat dry basins, readable small-scale detail.
```

### 09 — Wreck Expanse

```text
Desert with a few scattered tiny weathered industrial ruins and buried dark machinery wrecks integrated among sandstone ridges, dry channels, sandy plains and craters. Relics stay sparse and very small, no close-ups.
```

### 10 — Eroded Plateaus

```text
Wide dry terraced plateaus, curved escarpments, isolated worn mountains, scattered impact depressions and meandering wind-carved ravines, varied balanced terrain everywhere.
```

## Validation

10000 seeded selections reached all ten choices; IDs remained stable for repeated seeds. Actual controller/store code with managed vector/JSON adapters verified save/reload, turn advance, backup recovery, legacy missing-field loading and invalid-ID rejection. Comparison with the immediately preceding generator at seven seeds preserved exact geometry, adjacency, ownership, turn order and gameplay sequence. Added background EditMode regression methods ran using managed adapters; native JsonUtility and Resources imports are pending Unity 6000.5.4f1.

Native acceptance: run CampaignVisualTests and CampaignSystemTests; start several new campaigns, verify different backgrounds with matching sidebar previews; resume, finish a tactical battle, advance turns and reload; load an old save and verify the original background remains.
