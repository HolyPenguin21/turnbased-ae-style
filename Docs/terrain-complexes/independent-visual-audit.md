# Independent terrain cluster visual audit

Generated from the actual runtime PNGs after normalization. This audit does not read or trust palette-normalization-report.md.

Every configured ordinary terrain texture (main + alternatives) is used. Background metrics sample the terrain-like portion of the outer regular-hex band, because that is the area that must continue naturally into a neighbouring ordinary hex. Full-image metrics are retained only as a secondary check for global brightness/contrast/shadows.

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

| Group | Files | edge mean | edge max | worst file | edge L | edge contrast | edge sat | edge hue | edge warmth | edge detail |
|---|---:|---:|---:|---|---|---|---|---|---|---|
| AcidLake | 14 | 1.609 | 1.793 | AcidLake_Part1_06.png | 0.2291..0.2585 (med 0.2417) | 0.1650..0.2005 (med 0.1821) | 0.4207..0.4294 (med 0.4246) | 66.9487..68.5946 (med 67.9235) | 0.0589..0.0609 (med 0.0600) | 0.0118..0.0157 (med 0.0137) |

**AcidLake animation continuity (edge/background):**

- AcidLake_Part1: max adjacent-frame delta edge luminance=0.0074, chroma=0.0016, saturation=0.0044, warmth=0.0013
- AcidLake_Part2: max adjacent-frame delta edge luminance=0.0073, chroma=0.0011, saturation=0.0043, warmth=0.0009
- Part1-to-Part2 same-frame agreement: max delta edge luminance=0.0227, chroma=0.0025, warmth=0.0019
- Visual sheet: aridsteppe-acidlake-audit.png
| Canyon | 3 | 0.598 | 0.662 | Canyon_Part2.png | 0.2446..0.2478 (med 0.2449) | 0.0703..0.0876 (med 0.0746) | 0.4294..0.4327 (med 0.4321) | 67.0498..67.1174 (med 67.0588) | 0.0606..0.0613 (med 0.0612) | 0.0235..0.0235 (med 0.0235) |
- Visual sheet: aridsteppe-canyon-audit.png
| BoilingMud | 14 | 1.317 | 1.466 | BoilingMud_Part2_05.png | 0.2388..0.2526 (med 0.2463) | 0.1425..0.1710 (med 0.1519) | 0.4155..0.4371 (med 0.4264) | 67.2815..67.9296 (med 67.6644) | 0.0580..0.0606 (med 0.0595) | 0.0314..0.0314 (med 0.0314) |

**BoilingMud animation continuity (edge/background):**

- BoilingMud_Part1: max adjacent-frame delta edge luminance=0.0100, chroma=0.0012, saturation=0.0107, warmth=0.0011
- BoilingMud_Part2: max adjacent-frame delta edge luminance=0.0041, chroma=0.0009, saturation=0.0109, warmth=0.0010
- Part1-to-Part2 same-frame agreement: max delta edge luminance=0.0095, chroma=0.0023, warmth=0.0021
- Visual sheet: aridsteppe-boilingmud-audit.png
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

| Group | Files | edge mean | edge max | worst file | edge L | edge contrast | edge sat | edge hue | edge warmth | edge detail |
|---|---:|---:|---:|---|---|---|---|---|---|---|
| AcidLake | 14 | 1.500 | 1.753 | AcidLake_Part1_02.png | 0.4211..0.4386 (med 0.4319) | 0.1588..0.2091 (med 0.1840) | 0.4761..0.4810 (med 0.4775) | 70.3803..70.9439 (med 70.5097) | 0.0839..0.0864 (med 0.0854) | 0.0078..0.0118 (med 0.0098) |

**AcidLake animation continuity (edge/background):**

- AcidLake_Part1: max adjacent-frame delta edge luminance=0.0057, chroma=0.0014, saturation=0.0031, warmth=0.0013
- AcidLake_Part2: max adjacent-frame delta edge luminance=0.0033, chroma=0.0007, saturation=0.0037, warmth=0.0006
- Part1-to-Part2 same-frame agreement: max delta edge luminance=0.0173, chroma=0.0025, warmth=0.0022
- Visual sheet: desert-acidlake-audit.png
| Canyon | 3 | 0.468 | 0.534 | Canyon_Part1.png | 0.4310..0.4388 (med 0.4362) | 0.0581..0.0753 (med 0.0587) | 0.4791..0.4798 (med 0.4793) | 70.2769..70.5141 (med 70.4326) | 0.0858..0.0863 (med 0.0860) | 0.0118..0.0157 (med 0.0157) |
- Visual sheet: desert-canyon-audit.png
| BoilingMud | 14 | 1.215 | 1.454 | BoilingMud_Part2_04.png | 0.4231..0.4454 (med 0.4302) | 0.1238..0.1858 (med 0.1568) | 0.4686..0.4828 (med 0.4759) | 69.7781..70.6338 (med 70.3135) | 0.0825..0.0862 (med 0.0848) | 0.0235..0.0314 (med 0.0275) |

**BoilingMud animation continuity (edge/background):**

- BoilingMud_Part1: max adjacent-frame delta edge luminance=0.0049, chroma=0.0018, saturation=0.0070, warmth=0.0018
- BoilingMud_Part2: max adjacent-frame delta edge luminance=0.0048, chroma=0.0009, saturation=0.0041, warmth=0.0009
- Part1-to-Part2 same-frame agreement: max delta edge luminance=0.0180, chroma=0.0036, warmth=0.0037
- Visual sheet: desert-boilingmud-audit.png
| GiantMachineWreck | 3 | 0.610 | 0.704 | GiantMachineWreck_Part2.png | 0.4410..0.4540 (med 0.4480) | 0.0673..0.0831 (med 0.0763) | 0.4796..0.4823 (med 0.4819) | 70.4018..70.6341 (med 70.6032) | 0.0868..0.0883 (med 0.0871) | 0.0118..0.0157 (med 0.0118) |
- Visual sheet: desert-giantmachinewreck-audit.png

