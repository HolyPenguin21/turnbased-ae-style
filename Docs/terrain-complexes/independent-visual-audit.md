# Independent terrain cluster visual audit

Generated from the actual runtime PNGs after normalization. This audit does not read or trust palette-normalization-report.md.

Every configured ordinary terrain texture (main + alternatives) is used. Background metrics sample the terrain-like portion of the outer regular-hex band, because that is the area that must continue naturally into a neighbouring ordinary hex. Full-image metrics are retained only as a secondary check for global brightness/contrast/shadows.

## AridSteppe

- Ordinary configured references: **24**.
- Placement-compatible references: **16**.
- Placement-reference edge-score envelope: p95 **0.659**, max **0.673**.
- Edge luminance: 0.2472..0.2598 (med 0.2526).
- Edge contrast: 0.0604..0.0924 (med 0.0808).
- Edge saturation: 0.4177..0.4277 (med 0.4216).
- Edge hue: 67.5570..68.7557 (med 67.9994).
- Edge warmth (OKLab b): 0.0594..0.0621 (med 0.0603).
- Edge detail density: 0.0118..0.0157 (med 0.0118).

| Group | Files | edge mean | edge max | worst file | edge L | edge contrast | edge sat | edge hue | edge warmth | edge detail |
|---|---:|---:|---:|---|---|---|---|---|---|---|
| AcidLake | 14 | 1.091 | 1.189 | AcidLake_Part2_00.png | 0.2159..0.2249 (med 0.2191) | 0.0479..0.0622 (med 0.0562) | 0.4074..0.4155 (med 0.4106) | 73.3905..75.7100 (med 74.2970) | 0.0578..0.0585 (med 0.0580) | 0.0000..0.0000 (med 0.0000) |

**AcidLake animation continuity (edge/background):**

- AcidLake_Part1: max adjacent-frame delta edge luminance=0.0087, chroma=0.0007, saturation=0.0049, warmth=0.0006
- AcidLake_Part2: max adjacent-frame delta edge luminance=0.0069, chroma=0.0006, saturation=0.0030, warmth=0.0005
- Part1-to-Part2 same-frame agreement: max delta edge luminance=0.0076, chroma=0.0008, warmth=0.0003
- Visual sheet: aridsteppe-acidlake-audit.png
| Canyon | 3 | 0.905 | 1.045 | Canyon_Part2.png | 0.2475..0.2531 (med 0.2501) | 0.1224..0.1619 (med 0.1307) | 0.4228..0.4251 (med 0.4235) | 67.6342..67.7711 (med 67.7142) | 0.0597..0.0604 (med 0.0604) | 0.0275..0.0314 (med 0.0275) |
- Visual sheet: aridsteppe-canyon-audit.png
| BoilingMud | 14 | 1.179 | 1.244 | BoilingMud_Part1_06.png | 0.2218..0.2405 (med 0.2281) | 0.1067..0.1319 (med 0.1178) | 0.4024..0.4201 (med 0.4092) | 67.3475..67.7183 (med 67.5215) | 0.0538..0.0567 (med 0.0550) | 0.0118..0.0235 (med 0.0157) |

**BoilingMud animation continuity (edge/background):**

- BoilingMud_Part1: max adjacent-frame delta edge luminance=0.0095, chroma=0.0019, saturation=0.0091, warmth=0.0019
- BoilingMud_Part2: max adjacent-frame delta edge luminance=0.0167, chroma=0.0015, saturation=0.0048, warmth=0.0015
- Part1-to-Part2 same-frame agreement: max delta edge luminance=0.0101, chroma=0.0021, warmth=0.0020
- Visual sheet: aridsteppe-boilingmud-audit.png
| GiantMachineWreck | 3 | 0.983 | 1.100 | GiantMachineWreck_Part2.png | 0.2500..0.2681 (med 0.2604) | 0.1228..0.1608 (med 0.1453) | 0.4207..0.4236 (med 0.4220) | 67.8499..68.3342 (med 68.0978) | 0.0601..0.0615 (med 0.0613) | 0.0235..0.0275 (med 0.0235) |
- Visual sheet: aridsteppe-giantmachinewreck-audit.png

## Desert

- Ordinary configured references: **24**.
- Placement-compatible references: **16**.
- Placement-reference edge-score envelope: p95 **0.596**, max **0.617**.
- Edge luminance: 0.4364..0.4514 (med 0.4404).
- Edge contrast: 0.0480..0.0884 (med 0.0693).
- Edge saturation: 0.4670..0.4783 (med 0.4751).
- Edge hue: 69.8147..71.2444 (med 70.4303).
- Edge warmth (OKLab b): 0.0841..0.0865 (med 0.0856).
- Edge detail density: 0.0078..0.0118 (med 0.0118).

| Group | Files | edge mean | edge max | worst file | edge L | edge contrast | edge sat | edge hue | edge warmth | edge detail |
|---|---:|---:|---:|---|---|---|---|---|---|---|
| AcidLake | 14 | 1.500 | 1.667 | AcidLake_Part2_05.png | 0.3621..0.3763 (med 0.3685) | 0.0869..0.1253 (med 0.1046) | 0.4794..0.4850 (med 0.4822) | 72.3634..73.7784 (med 73.1519) | 0.0816..0.0827 (med 0.0825) | 0.0039..0.0039 (med 0.0039) |

**AcidLake animation continuity (edge/background):**

- AcidLake_Part1: max adjacent-frame delta edge luminance=0.0109, chroma=0.0004, saturation=0.0032, warmth=0.0002
- AcidLake_Part2: max adjacent-frame delta edge luminance=0.0079, chroma=0.0010, saturation=0.0034, warmth=0.0009
- Part1-to-Part2 same-frame agreement: max delta edge luminance=0.0138, chroma=0.0015, warmth=0.0009
- Visual sheet: desert-acidlake-audit.png
| Canyon | 3 | 0.707 | 0.818 | Canyon_Part1.png | 0.4313..0.4421 (med 0.4376) | 0.0983..0.1299 (med 0.1044) | 0.4766..0.4776 (med 0.4771) | 70.3795..70.6368 (med 70.5141) | 0.0854..0.0862 (med 0.0857) | 0.0157..0.0157 (med 0.0157) |
- Visual sheet: desert-canyon-audit.png
| BoilingMud | 14 | 1.659 | 1.942 | BoilingMud_Part2_04.png | 0.4176..0.4330 (med 0.4283) | 0.1668..0.2177 (med 0.1915) | 0.4663..0.4771 (med 0.4751) | 70.3247..70.6044 (med 70.3985) | 0.0819..0.0851 (med 0.0843) | 0.0196..0.0235 (med 0.0216) |

**BoilingMud animation continuity (edge/background):**

- BoilingMud_Part1: max adjacent-frame delta edge luminance=0.0035, chroma=0.0004, saturation=0.0017, warmth=0.0004
- BoilingMud_Part2: max adjacent-frame delta edge luminance=0.0070, chroma=0.0022, saturation=0.0074, warmth=0.0020
- Part1-to-Part2 same-frame agreement: max delta edge luminance=0.0114, chroma=0.0031, warmth=0.0028
- Visual sheet: desert-boilingmud-audit.png
| GiantMachineWreck | 3 | 0.715 | 0.845 | GiantMachineWreck_Part2.png | 0.4428..0.4458 (med 0.4456) | 0.0816..0.1263 (med 0.0955) | 0.4775..0.4796 (med 0.4780) | 70.4452..70.6802 (med 70.5756) | 0.0864..0.0870 (med 0.0867) | 0.0118..0.0157 (med 0.0118) |
- Visual sheet: desert-giantmachinewreck-audit.png

