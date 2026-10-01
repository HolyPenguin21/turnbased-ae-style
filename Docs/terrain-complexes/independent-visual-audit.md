# Independent terrain cluster visual audit

Generated from the actual runtime PNGs after normalization. This audit does not read or trust palette-normalization-report.md.

Every configured ordinary terrain texture (main + alternatives) is used. Background metrics sample the terrain-like portion of the outer regular-hex band, because that is the area that must continue naturally into a neighbouring ordinary hex. Full-image metrics are retained only as a secondary check for global brightness/contrast/shadows.

The closest 30% of ring pixels is a heuristic, not a semantic ground mask. Where water, mud, canyon walls or wreckage cross the ring, feature/shore transitions can remain in that subset. A higher score or contrast therefore requires visual inspection; it is not a requirement to flatten the obstacle to ordinary sand. See runtime-pixel-audit.csv for all per-file values, including full-image metrics. Animation deltas include the last-to-first transition.

## AridSteppe

- Ordinary configured references: **24**.
- Placement-compatible references: **16**.
- Placement-reference edge-score envelope: p95 **0.433**, max **0.438**.
- Edge luminance: 0.2488..0.2535 (med 0.2519).
- Edge contrast: 0.0395..0.0565 (med 0.0500).
- Edge saturation: 0.4177..0.4251 (med 0.4207).
- Edge hue: 67.4774..68.6633 (med 67.9353).
- Edge warmth (OKLab b): 0.0595..0.0610 (med 0.0602).
- Edge detail density: 0.0078..0.0157 (med 0.0118).

### AcidLake

| Group | Files | edge mean | edge max | worst file | edge L | edge contrast | edge sat | edge hue | edge warmth | edge detail |
|---|---:|---:|---:|---|---|---|---|---|---|---|
| AcidLake | 14 | 1.428 | 1.581 | AcidLake_Part1_04.png | 0.2446..0.2560 (med 0.2512) | 0.1084..0.1332 (med 0.1180) | 0.4102..0.4294 (med 0.4184) | 66.9700..68.6347 (med 67.9622) | 0.0588..0.0610 (med 0.0600) | 0.0118..0.0157 (med 0.0157) |

**AcidLake animation continuity (edge/background):**

- AcidLake_Part1: max adjacent-frame delta edge luminance=0.0067, chroma=0.0017, saturation=0.0050, warmth=0.0014
- AcidLake_Part2: max adjacent-frame delta edge luminance=0.0036, chroma=0.0011, saturation=0.0097, warmth=0.0010
- Part1-to-Part2 same-frame agreement: max delta edge luminance=0.0091, chroma=0.0025, warmth=0.0019
- Visual sheet: aridsteppe-acidlake-audit.png

### Canyon

| Group | Files | edge mean | edge max | worst file | edge L | edge contrast | edge sat | edge hue | edge warmth | edge detail |
|---|---:|---:|---:|---|---|---|---|---|---|---|
| Canyon | 3 | 0.598 | 0.662 | Canyon_Part2.png | 0.2446..0.2478 (med 0.2449) | 0.0703..0.0876 (med 0.0746) | 0.4294..0.4327 (med 0.4321) | 67.0498..67.1174 (med 67.0588) | 0.0606..0.0613 (med 0.0612) | 0.0235..0.0235 (med 0.0235) |
- Visual sheet: aridsteppe-canyon-audit.png

### BoilingMud

| Group | Files | edge mean | edge max | worst file | edge L | edge contrast | edge sat | edge hue | edge warmth | edge detail |
|---|---:|---:|---:|---|---|---|---|---|---|---|
| BoilingMud | 14 | 1.006 | 1.142 | BoilingMud_Part2_05.png | 0.2467..0.2515 (med 0.2506) | 0.0782..0.0898 (med 0.0833) | 0.4167..0.4257 (med 0.4204) | 67.2608..67.8724 (med 67.6214) | 0.0582..0.0601 (med 0.0592) | 0.0196..0.0196 (med 0.0196) |

**BoilingMud animation continuity (edge/background):**

- BoilingMud_Part1: max adjacent-frame delta edge luminance=0.0045, chroma=0.0013, saturation=0.0062, warmth=0.0011
- BoilingMud_Part2: max adjacent-frame delta edge luminance=0.0041, chroma=0.0009, saturation=0.0068, warmth=0.0007
- Part1-to-Part2 same-frame agreement: max delta edge luminance=0.0040, chroma=0.0021, warmth=0.0019
- Visual sheet: aridsteppe-boilingmud-audit.png

### GiantMachineWreck

| Group | Files | edge mean | edge max | worst file | edge L | edge contrast | edge sat | edge hue | edge warmth | edge detail |
|---|---:|---:|---:|---|---|---|---|---|---|---|
| GiantMachineWreck | 3 | 0.711 | 0.791 | GiantMachineWreck_Part2.png | 0.2486..0.2605 (med 0.2570) | 0.0884..0.1042 (med 0.0927) | 0.4260..0.4321 (med 0.4294) | 67.6491..68.1872 (med 67.9699) | 0.0607..0.0622 (med 0.0620) | 0.0235..0.0275 (med 0.0235) |
- Visual sheet: aridsteppe-giantmachinewreck-audit.png


## Desert

- Ordinary configured references: **24**.
- Placement-compatible references: **16**.
- Placement-reference edge-score envelope: p95 **0.384**, max **0.389**.
- Edge luminance: 0.4362..0.4419 (med 0.4380).
- Edge contrast: 0.0313..0.0518 (med 0.0412).
- Edge saturation: 0.4698..0.4771 (med 0.4749).
- Edge hue: 69.7132..71.0149 (med 70.2658).
- Edge warmth (OKLab b): 0.0846..0.0860 (med 0.0854).
- Edge detail density: 0.0078..0.0118 (med 0.0118).

### AcidLake

| Group | Files | edge mean | edge max | worst file | edge L | edge contrast | edge sat | edge hue | edge warmth | edge detail |
|---|---:|---:|---:|---|---|---|---|---|---|---|
| AcidLake | 14 | 1.274 | 1.492 | AcidLake_Part1_02.png | 0.4337..0.4386 (med 0.4369) | 0.0888..0.1261 (med 0.1055) | 0.4694..0.4817 (med 0.4758) | 70.3420..70.9332 (med 70.5360) | 0.0839..0.0863 (med 0.0854) | 0.0078..0.0118 (med 0.0118) |

**AcidLake animation continuity (edge/background):**

- AcidLake_Part1: max adjacent-frame delta edge luminance=0.0024, chroma=0.0013, saturation=0.0050, warmth=0.0013
- AcidLake_Part2: max adjacent-frame delta edge luminance=0.0014, chroma=0.0006, saturation=0.0026, warmth=0.0005
- Part1-to-Part2 same-frame agreement: max delta edge luminance=0.0047, chroma=0.0025, warmth=0.0021
- Visual sheet: desert-acidlake-audit.png

### Canyon

| Group | Files | edge mean | edge max | worst file | edge L | edge contrast | edge sat | edge hue | edge warmth | edge detail |
|---|---:|---:|---:|---|---|---|---|---|---|---|
| Canyon | 3 | 0.468 | 0.534 | Canyon_Part1.png | 0.4310..0.4388 (med 0.4362) | 0.0581..0.0753 (med 0.0587) | 0.4791..0.4798 (med 0.4793) | 70.2769..70.5141 (med 70.4326) | 0.0858..0.0863 (med 0.0860) | 0.0118..0.0157 (med 0.0157) |
- Visual sheet: desert-canyon-audit.png

### BoilingMud

| Group | Files | edge mean | edge max | worst file | edge L | edge contrast | edge sat | edge hue | edge warmth | edge detail |
|---|---:|---:|---:|---|---|---|---|---|---|---|
| BoilingMud | 14 | 0.904 | 1.084 | BoilingMud_Part2_04.png | 0.4333..0.4396 (med 0.4360) | 0.0559..0.0734 (med 0.0634) | 0.4621..0.4816 (med 0.4742) | 69.9190..70.6525 (med 70.2485) | 0.0823..0.0862 (med 0.0849) | 0.0118..0.0157 (med 0.0137) |

**BoilingMud animation continuity (edge/background):**

- BoilingMud_Part1: max adjacent-frame delta edge luminance=0.0028, chroma=0.0020, saturation=0.0099, warmth=0.0022
- BoilingMud_Part2: max adjacent-frame delta edge luminance=0.0037, chroma=0.0006, saturation=0.0030, warmth=0.0006
- Part1-to-Part2 same-frame agreement: max delta edge luminance=0.0051, chroma=0.0038, warmth=0.0039
- Visual sheet: desert-boilingmud-audit.png

### GiantMachineWreck

| Group | Files | edge mean | edge max | worst file | edge L | edge contrast | edge sat | edge hue | edge warmth | edge detail |
|---|---:|---:|---:|---|---|---|---|---|---|---|
| GiantMachineWreck | 3 | 0.610 | 0.704 | GiantMachineWreck_Part2.png | 0.4410..0.4540 (med 0.4480) | 0.0673..0.0831 (med 0.0763) | 0.4796..0.4823 (med 0.4819) | 70.4018..70.6341 (med 70.6032) | 0.0868..0.0883 (med 0.0871) | 0.0118..0.0157 (med 0.0118) |
- Visual sheet: desert-giantmachinewreck-audit.png
