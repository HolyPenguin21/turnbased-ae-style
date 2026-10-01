"""Check the authored olive-water footprint against the existing flat-top hex edges.

This palette-specific check samples an inner shore band on the five exterior edges of
each part, and an open-water window in the shared edge. It is not Unity render QA.
"""
from pathlib import Path
import math
import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
for biome in ("AridSteppe", "Desert"):
    worst_outer = 0.0
    least_connection = 1.0
    for part in (1, 2):
        shared = 5 if part == 1 else 2
        paths = sorted((ROOT / "Assets/Textures/Terrain" / biome / "Complexes").glob(
            f"AcidLake_Part{part}_*.png"))
        assert len(paths) == 7
        for path in paths:
            rgb = np.asarray(Image.open(path).convert("RGB"), dtype=float)
            # Acid is olive-green; soil has a distinctly lower green/red ratio.
            water = (rgb[..., 1] > .92 * rgb[..., 0]) & (rgb[..., 2] < .95 * rgb[..., 1])
            for edge in range(6):
                angle = math.radians(edge * 60 + 30)
                normal = np.array([math.cos(angle), math.sin(angle)])
                tangent = np.array([-normal[1], normal[0]])
                offsets = np.linspace(-.22, .22, 89)
                points = .5 + np.array([
                    normal * math.sqrt(3) / 4 * depth + tangent * offset
                    for depth in (.94, .97, .99) for offset in offsets])
                xy = np.clip(np.rint(points * 511).astype(int), 0, 511)
                fraction = float(water[xy[:, 1], xy[:, 0]].mean())
                if edge != shared:
                    worst_outer = max(worst_outer, fraction)
                    assert fraction <= .04, f"Water in exterior shore band: {path.name}, edge {edge}, {fraction:.3f}"
                else:
                    points = .5 + normal * math.sqrt(3) / 4 + np.linspace(-.04, .04, 25)[:, None] * tangent
                    xy = np.clip(np.rint(points * 511).astype(int), 0, 511)
                    fraction = float(water[xy[:, 1], xy[:, 0]].mean())
                    least_connection = min(least_connection, fraction)
                    assert fraction >= .90, f"Blocked shared water connection: {path.name}, {fraction:.3f}"
    print(f"{biome}: 14 frames; five exterior shore edges per part; outer water max={worst_outer:.3f}, shared water min={least_connection:.3f}")
