# Independent terrain cluster visual audit

Generated from the actual PNGs on the feature branch. This audit does not read or trust palette-normalization-report.md.

The comparison set contains every configured ordinary terrain texture (main + alternatives). Edge score uses only the terrain-like half of pixels in the outer regular-hex band, so the intentional acid/mud/canyon/wreck feature itself does not dominate the background-continuity check.

## AridSteppe

- Ordinary configured references: **24**.
- Placement-compatible references: **16**.
- Placement-reference edge-score envelope: p95 **0.561**, max **0.563**.
- Placement luminance range: 0.2468..0.2629 (med 0.2535).
- Placement contrast range: 0.1311..0.2077 (med 0.1665).
- Placement saturation range: 0.4158..0.4278 (med 0.4207).
- Placement detail-density range: 0.0256..0.0431 (med 0.0330).

| Group | Files | edge mean | edge max | worst file | L range | contrast range | saturation range | detail range |
|---|---:|---:|---:|---|---|---|---|---|
| AcidLake | 14 | 0.499 | 0.558 | AcidLake_Part2_00.png | 0.2369..0.2564 (med 0.2429) | 0.0971..0.1361 (med 0.1127) | 0.4114..0.4156 (med 0.4134) | 0.0129..0.0152 (med 0.0140) |

**AcidLake animation continuity (edge/background):**

- AcidLake_Part1: max adjacent-frame delta edge luminance=0.0078, delta edge chroma=0.0009, delta saturation=0.0020
- AcidLake_Part2: max adjacent-frame delta edge luminance=0.0107, delta edge chroma=0.0009, delta saturation=0.0011
- Part1-to-Part2 same-frame agreement: max delta edge luminance=0.0115, max delta edge chroma=0.0016
| Canyon | 3 | 0.755 | 0.834 | Canyon_Part2.png | 0.2153..0.2326 (med 0.2191) | 0.2771..0.2931 (med 0.2905) | 0.4219..0.4294 (med 0.4236) | 0.0540..0.0560 (med 0.0550) |
| BoilingMud | 14 | 0.967 | 1.018 | BoilingMud_Part1_01.png | 0.2243..0.2407 (med 0.2305) | 0.2365..0.2840 (med 0.2573) | 0.4310..0.4568 (med 0.4425) | 0.0506..0.0662 (med 0.0544) |

**BoilingMud animation continuity (edge/background):**

- BoilingMud_Part1: max adjacent-frame delta edge luminance=0.0038, delta edge chroma=0.0024, delta saturation=0.0099
- BoilingMud_Part2: max adjacent-frame delta edge luminance=0.0234, delta edge chroma=0.0029, delta saturation=0.0071
- Part1-to-Part2 same-frame agreement: max delta edge luminance=0.0131, max delta edge chroma=0.0036
| GiantMachineWreck | 3 | 0.902 | 0.942 | GiantMachineWreck_Part2.png | 0.1683..0.2067 (med 0.2001) | 0.2945..0.2969 (med 0.2967) | 0.4133..0.4181 (med 0.4162) | 0.0502..0.0518 (med 0.0515) |

Visual sheets: aridsteppe-reference-terrain-sheet.png and aridsteppe-current-clusters-audit.png.

## Desert

- Ordinary configured references: **24**.
- Placement-compatible references: **16**.
- Placement-reference edge-score envelope: p95 **0.528**, max **0.557**.
- Placement luminance range: 0.4323..0.4525 (med 0.4396).
- Placement contrast range: 0.1273..0.1976 (med 0.1641).
- Placement saturation range: 0.4653..0.4769 (med 0.4739).
- Placement detail-density range: 0.0198..0.0432 (med 0.0264).

| Group | Files | edge mean | edge max | worst file | L range | contrast range | saturation range | detail range |
|---|---:|---:|---:|---|---|---|---|---|
| AcidLake | 14 | 0.702 | 0.940 | AcidLake_Part2_04.png | 0.3960..0.4309 (med 0.4146) | 0.1984..0.2546 (med 0.2292) | 0.4651..0.4684 (med 0.4677) | 0.0131..0.0160 (med 0.0147) |

**AcidLake animation continuity (edge/background):**

- AcidLake_Part1: max adjacent-frame delta edge luminance=0.0098, delta edge chroma=0.0010, delta saturation=0.0032
- AcidLake_Part2: max adjacent-frame delta edge luminance=0.0220, delta edge chroma=0.0014, delta saturation=0.0025
- Part1-to-Part2 same-frame agreement: max delta edge luminance=0.0345, max delta edge chroma=0.0021
| Canyon | 3 | 0.621 | 0.707 | Canyon_Part1.png | 0.4056..0.4150 (med 0.4125) | 0.2946..0.3151 (med 0.3081) | 0.4764..0.4782 (med 0.4769) | 0.0352..0.0386 (med 0.0385) |
| BoilingMud | 14 | 1.274 | 1.422 | BoilingMud_Part2_04.png | 0.3718..0.3908 (med 0.3848) | 0.2524..0.3112 (med 0.2795) | 0.4426..0.4522 (med 0.4491) | 0.0384..0.0490 (med 0.0425) |

**BoilingMud animation continuity (edge/background):**

- BoilingMud_Part1: max adjacent-frame delta edge luminance=0.0045, delta edge chroma=0.0007, delta saturation=0.0011
- BoilingMud_Part2: max adjacent-frame delta edge luminance=0.0104, delta edge chroma=0.0036, delta saturation=0.0046
- Part1-to-Part2 same-frame agreement: max delta edge luminance=0.0333, max delta edge chroma=0.0100
| GiantMachineWreck | 3 | 0.631 | 0.692 | GiantMachineWreck_Part2.png | 0.2835..0.3914 (med 0.3910) | 0.4344..0.4415 (med 0.4386) | 0.4725..0.4776 (med 0.4751) | 0.0429..0.0521 (med 0.0445) |

Visual sheets: desert-reference-terrain-sheet.png and desert-current-clusters-audit.png.

