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
| AcidLake | 14 | 1.428 | 1.581 | AcidLake_Part1_04.png | 0.2446..0.2560 (med 0.2512) | 0.1084..0.1332 (med 0.1180) | 0.4102..0.4294 (med 0.4184) | 66.9700..68.6347 (med 67.9622) | 0.0588..0.0610 (med 0.0600) | 0.0118..0.0157 (med 0.0157) |

**AcidLake animation continuity (edge/background):**

- AcidLake_Part1: max adjacent-frame delta edge luminance=0.0067, chroma=0.0017, saturation=0.0050, warmth=0.0014
- AcidLake_Part2: max adjacent-frame delta edge luminance=0.0036, chroma=0.0011, saturation=0.0097, warmth=0.0010
- Part1-to-Part2 same-frame agreement: max delta edge luminance=0.0091, chroma=0.0025, warmth=0.0019
- Visual sheet: aridsteppe-acidlake-audit.png
| Canyon | 3 | 0.598 | 0.662 | Canyon_Part2.png | 0.2446..0.2478 (med 0.2449) | 0.0703..0.0876 (med 0.0746) | 0.4294..0.4327 (med 0.4321) | 67.0498..67.1174 (med 67.0588) | 0.0606..0.0613 (med 0.0612) | 0.0235..0.0235 (med 0.0235) |
- Visual sheet: aridsteppe-canyon-audit.png
| BoilingMud | 14 | 1.198 | 1.316 | BoilingMud_Part2_05.png | 0.2509..0.2581 (med 0.2538) | 0.1218..0.1329 (med 0.1269) | 0.4032..0.4260 (med 0.4171) | 67.4364..68.0780 (med 67.6811) | 0.0570..0.0604 (med 0.0587) | 0.0275..0.0314 (med 0.0314) |

**BoilingMud animation continuity (edge/background):**

- BoilingMud_Part1: max adjacent-frame delta edge luminance=0.0042, chroma=0.0014, saturation=0.0086, warmth=0.0012
- BoilingMud_Part2: max adjacent-frame delta edge luminance=0.0068, chroma=0.0010, saturation=0.0104, warmth=0.0008
- Part1-to-Part2 same-frame agreement: max delta edge luminance=0.0049, chroma=0.0028, warmth=0.0027
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
| AcidLake | 14 | 1.274 | 1.492 | AcidLake_Part1_02.png | 0.4337..0.4386 (med 0.4369) | 0.0888..0.1261 (med 0.1055) | 0.4694..0.4817 (med 0.4758) | 70.3420..70.9332 (med 70.5360) | 0.0839..0.0863 (med 0.0854) | 0.0078..0.0118 (med 0.0118) |

**AcidLake animation continuity (edge/background):**

- AcidLake_Part1: max adjacent-frame delta edge luminance=0.0024, chroma=0.0013, saturation=0.0050, warmth=0.0013
- AcidLake_Part2: max adjacent-frame delta edge luminance=0.0013, chroma=0.0006, saturation=0.0026, warmth=0.0005
- Part1-to-Part2 same-frame agreement: max delta edge luminance=0.0047, chroma=0.0025, warmth=0.0021
- Visual sheet: desert-acidlake-audit.png
| Canyon | 3 | 0.468 | 0.534 | Canyon_Part1.png | 0.4310..0.4388 (med 0.4362) | 0.0581..0.0753 (med 0.0587) | 0.4791..0.4798 (med 0.4793) | 70.2769..70.5141 (med 70.4326) | 0.0858..0.0863 (med 0.0860) | 0.0118..0.0157 (med 0.0157) |
- Visual sheet: desert-canyon-audit.png
| BoilingMud | 14 | 1.087 | 1.241 | BoilingMud_Part2_04.png | 0.4395..0.4471 (med 0.4427) | 0.1007..0.1161 (med 0.1102) | 0.4593..0.4786 (med 0.4697) | 69.9931..70.7128 (med 70.3840) | 0.0819..0.0862 (med 0.0847) | 0.0235..0.0275 (med 0.0275) |

**BoilingMud animation continuity (edge/background):**

- BoilingMud_Part1: max adjacent-frame delta edge luminance=0.0025, chroma=0.0020, saturation=0.0073, warmth=0.0022
- BoilingMud_Part2: max adjacent-frame delta edge luminance=0.0028, chroma=0.0008, saturation=0.0038, warmth=0.0008
- Part1-to-Part2 same-frame agreement: max delta edge luminance=0.0059, chroma=0.0042, warmth=0.0043
- Visual sheet: desert-boilingmud-audit.png
| GiantMachineWreck | 3 | 0.610 | 0.704 | GiantMachineWreck_Part2.png | 0.4410..0.4540 (med 0.4480) | 0.0673..0.0831 (med 0.0763) | 0.4796..0.4823 (med 0.4819) | 70.4018..70.6341 (med 70.6032) | 0.0868..0.0883 (med 0.0871) | 0.0118..0.0157 (med 0.0118) |
- Visual sheet: desert-giantmachinewreck-audit.png

