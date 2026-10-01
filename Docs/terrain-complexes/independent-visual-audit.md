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
| AcidLake | 14 | 1.368 | 1.492 | AcidLake_Part2_00.png | 0.1817..0.1958 (med 0.1860) | 0.1241..0.1668 (med 0.1431) | 0.4133..0.4178 (med 0.4155) | 0.0123..0.0149 (med 0.0134) |

**AcidLake animation continuity (edge/background):**

- AcidLake_Part1: max adjacent-frame delta edge luminance=0.0112, delta edge chroma=0.0007, delta saturation=0.0023
- AcidLake_Part2: max adjacent-frame delta edge luminance=0.0083, delta edge chroma=0.0005, delta saturation=0.0018
- Part1-to-Part2 same-frame agreement: max delta edge luminance=0.0126, max delta edge chroma=0.0026
| Canyon | 3 | 0.755 | 0.834 | Canyon_Part2.png | 0.2153..0.2326 (med 0.2191) | 0.2771..0.2931 (med 0.2905) | 0.4219..0.4294 (med 0.4236) | 0.0540..0.0560 (med 0.0550) |
| BoilingMud | 14 | 1.180 | 1.241 | BoilingMud_Part2_00.png | 0.2034..0.2174 (med 0.2087) | 0.2142..0.2601 (med 0.2346) | 0.4069..0.4341 (med 0.4187) | 0.0464..0.0607 (med 0.0498) |

**BoilingMud animation continuity (edge/background):**

- BoilingMud_Part1: max adjacent-frame delta edge luminance=0.0026, delta edge chroma=0.0022, delta saturation=0.0119
- BoilingMud_Part2: max adjacent-frame delta edge luminance=0.0207, delta edge chroma=0.0029, delta saturation=0.0074
- Part1-to-Part2 same-frame agreement: max delta edge luminance=0.0143, max delta edge chroma=0.0051
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
| AcidLake | 14 | 2.400 | 2.593 | AcidLake_Part2_01.png | 0.2622..0.2819 (med 0.2729) | 0.2137..0.2546 (med 0.2262) | 0.5000..0.5056 (med 0.5030) | 0.0120..0.0140 (med 0.0127) |

**AcidLake animation continuity (edge/background):**

- AcidLake_Part1: max adjacent-frame delta edge luminance=0.0177, delta edge chroma=0.0022, delta saturation=0.0047
- AcidLake_Part2: max adjacent-frame delta edge luminance=0.0166, delta edge chroma=0.0015, delta saturation=0.0055
- Part1-to-Part2 same-frame agreement: max delta edge luminance=0.0300, max delta edge chroma=0.0032
| Canyon | 3 | 0.621 | 0.707 | Canyon_Part1.png | 0.4056..0.4150 (med 0.4125) | 0.2946..0.3151 (med 0.3081) | 0.4764..0.4782 (med 0.4769) | 0.0352..0.0386 (med 0.0385) |
| BoilingMud | 14 | 1.811 | 2.071 | BoilingMud_Part2_04.png | 0.3250..0.3499 (med 0.3411) | 0.3083..0.3584 (med 0.3322) | 0.4321..0.4476 (med 0.4419) | 0.0426..0.0545 (med 0.0471) |

**BoilingMud animation continuity (edge/background):**

- BoilingMud_Part1: max adjacent-frame delta edge luminance=0.0080, delta edge chroma=0.0015, delta saturation=0.0025
- BoilingMud_Part2: max adjacent-frame delta edge luminance=0.0093, delta edge chroma=0.0041, delta saturation=0.0068
- Part1-to-Part2 same-frame agreement: max delta edge luminance=0.0385, max delta edge chroma=0.0096
| GiantMachineWreck | 3 | 0.631 | 0.692 | GiantMachineWreck_Part2.png | 0.2835..0.3914 (med 0.3910) | 0.4344..0.4415 (med 0.4386) | 0.4725..0.4776 (med 0.4751) | 0.0429..0.0521 (med 0.0445) |

Visual sheets: desert-reference-terrain-sheet.png and desert-current-clusters-audit.png.

