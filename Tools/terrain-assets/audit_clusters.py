"""Independent visual/pixel audit for terrain-complex assets.

Read-only for runtime PNGs. Compares every cluster asset against every ordinary configured
terrain texture in its biome, including alternativeTextures.
"""
from __future__ import annotations

import math
from dataclasses import dataclass
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont

from normalize_clusters import (
    CHANNEL_FLOOR,
    DOCS,
    GROUPS,
    PLACEMENT_TERRAINS,
    TERRAIN_ROOT,
    guid_map,
    load_config,
    palette_for,
    rgb_to_oklab,
    texture_refs,
    texture_style,
)

SIDE = 128
THUMB = 132


@dataclass
class PixelStats:
    luma: float
    contrast: float
    saturation: float
    hue_deg: float
    chroma: float
    detail: float
    edge_score: float
    edge_luma: float
    edge_chroma: float


def configured_refs(config: dict, biome: str, guids: dict[str, Path]):
    palette = palette_for(config, biome)
    refs = []
    placement = []
    for entry in palette["terrainTypes"]:
        if entry.get("baselineWeight", 0) <= 0 or entry.get("blocksGroundMovement", 0):
            continue
        for i, ref in enumerate(texture_refs(entry)):
            path = guids.get(ref["guid"])
            if path is None:
                raise RuntimeError(f"{biome}: unresolved {ref['guid']}")
            item = (entry["terrainName"], path, i == 0)
            refs.append(item)
            if entry["terrainName"] in PLACEMENT_TERRAINS:
                placement.append(item)
    return refs, placement


def read_rgba(path: Path, side: int = SIDE):
    with Image.open(path) as src:
        img = src.convert("RGBA").resize((side, side), Image.Resampling.LANCZOS)
    arr = np.asarray(img, dtype=np.float64) / 255.0
    return arr[..., :3], arr[..., 3]


def srgb_to_linear(rgb: np.ndarray) -> np.ndarray:
    return np.where(rgb <= 0.04045, rgb / 12.92, ((rgb + 0.055) / 1.055) ** 2.4)


def luma(rgb: np.ndarray) -> np.ndarray:
    lin = srgb_to_linear(np.clip(rgb, 0.0, 1.0))
    return lin[..., 0] * 0.2126 + lin[..., 1] * 0.7152 + lin[..., 2] * 0.0722


def sat_values(rgb: np.ndarray) -> np.ndarray:
    hi = np.max(rgb, axis=-1)
    lo = np.min(rgb, axis=-1)
    return np.where(hi > 1e-6, (hi - lo) / hi, 0.0)


def hex_outer_ring(side: int, inner: float = 0.72) -> np.ndarray:
    yy, xx = np.mgrid[0:side, 0:side]
    x = (xx + 0.5) / side
    y = (yy + 0.5) / side

    def inside(scale: float):
        cx, cy = 0.5, 0.5
        xs = (x - cx) / scale + cx
        ys = (y - cy) / scale + cy
        dx = np.abs(xs - 0.5)
        return (
            (dx <= 0.5)
            & (ys >= np.maximum(0.0, 2.0 * dx - 0.5))
            & (ys <= np.minimum(1.0, 1.5 - 2.0 * dx))
        )

    return inside(1.0) & ~inside(inner)


def terrain_like_subset(lab: np.ndarray, target_center: np.ndarray, target_spread: np.ndarray,
                        keep_fraction: float = 0.50):
    norm = (lab - target_center) / np.maximum(target_spread, CHANNEL_FLOOR)
    dist = np.sqrt(np.sum(norm * norm, axis=1))
    n = max(16, int(len(dist) * keep_fraction))
    n = min(n, len(dist))
    idx = np.argpartition(dist, n - 1)[:n]
    return idx, dist[idx]


def image_stats(path: Path, placement_style) -> PixelStats:
    rgb, alpha = read_rgba(path)
    visible = alpha > 0.10
    vals = rgb[visible]
    ys = luma(rgb)
    yv = ys[visible]
    sat = sat_values(rgb)[visible]
    lab = rgb_to_oklab(vals)
    center = np.median(lab, axis=0)
    hue = math.degrees(math.atan2(center[2], center[1]))
    chroma = float(math.hypot(center[1], center[2]))

    p10, p90 = np.percentile(yv, [10, 90])
    contrast = float(p90 - p10)

    dx = np.abs(np.diff(ys, axis=1))
    dy = np.abs(np.diff(ys, axis=0))
    mx = visible[:, 1:] & visible[:, :-1]
    my = visible[1:, :] & visible[:-1, :]
    detail = float(0.5 * (dx[mx].mean() + dy[my].mean()))

    ring = hex_outer_ring(rgb.shape[0]) & visible
    ring_rgb = rgb[ring]
    if len(ring_rgb) < 32:
        ring_rgb = vals
    ring_lab = rgb_to_oklab(ring_rgb)
    idx, d = terrain_like_subset(
        ring_lab, placement_style.center, placement_style.spread, keep_fraction=0.50
    )
    terrain_ring_rgb = ring_rgb[idx]
    terrain_ring_lab = ring_lab[idx]
    ring_y = luma(terrain_ring_rgb.reshape(-1, 1, 3)).reshape(-1)
    ring_center = np.median(terrain_ring_lab, axis=0)

    return PixelStats(
        luma=float(np.median(yv)),
        contrast=contrast,
        saturation=float(np.median(sat)),
        hue_deg=hue,
        chroma=chroma,
        detail=detail,
        edge_score=float(np.mean(d)),
        edge_luma=float(np.median(ring_y)),
        edge_chroma=float(math.hypot(ring_center[1], ring_center[2])),
    )


def fmt_range(vals):
    vals = np.asarray(vals, dtype=float)
    return f"{vals.min():.4f}..{vals.max():.4f} (med {np.median(vals):.4f})"


def draw_thumb(canvas, draw, path: Path, x: int, y: int, label: str, font):
    with Image.open(path) as src:
        thumb = src.convert("RGB")
        thumb.thumbnail((THUMB, THUMB), Image.Resampling.LANCZOS)
    canvas.paste(thumb, (x, y))
    draw.rectangle((x, y, x + THUMB - 1, y + THUMB - 1), outline=(85, 85, 85))
    draw.text((x, y + THUMB + 3), label[:25], fill=(235, 235, 235), font=font)


def reference_sheet(biome: str, refs):
    cols = 6
    grouped = {}
    for terrain, path, primary in refs:
        grouped.setdefault(terrain, []).append((path, primary))
    rows = 1 + sum(1 + math.ceil(len(items) / cols) for items in grouped.values())
    cell_w, cell_h = 154, 158
    canvas = Image.new("RGB", (cols * cell_w + 24, rows * cell_h + 32), (30, 30, 30))
    draw = ImageDraw.Draw(canvas)
    font = ImageFont.load_default()
    y = 10
    draw.text((12, y), f"{biome} - ALL configured ordinary terrain textures", fill=(245,245,245), font=font)
    y += 24
    for terrain, items in grouped.items():
        draw.text((12, y), f"{terrain} ({len(items)})", fill=(255, 220, 150), font=font)
        y += 18
        for i, (path, primary) in enumerate(items):
            row, col = divmod(i, cols)
            x = 12 + col * cell_w
            yy = y + row * cell_h
            label = path.name + (" [main]" if primary else "")
            draw_thumb(canvas, draw, path, x, yy, label, font)
        y += math.ceil(len(items) / cols) * cell_h + 8
    return canvas.crop((0, 0, canvas.width, y + 4))


def cluster_sheet(biome: str, placement_refs):
    cols = 7
    cell_w, cell_h = 154, 158
    placement_rows = math.ceil(len(placement_refs) / cols)
    group_rows = 0
    cdir = TERRAIN_ROOT / biome / "Complexes"
    for group, (pattern, _) in GROUPS.items():
        n = len(list(cdir.glob(pattern)))
        group_rows += 1 + math.ceil(n / cols)
    rows = 2 + placement_rows + group_rows
    canvas = Image.new("RGB", (cols * cell_w + 24, rows * cell_h + 36), (30,30,30))
    draw = ImageDraw.Draw(canvas)
    font = ImageFont.load_default()
    y = 10
    draw.text((12, y), f"{biome} - placement refs then CURRENT cluster PNGs", fill=(245,245,245), font=font)
    y += 24
    draw.text((12, y), "PLACEMENT REFERENCES: Desert / Sand dunes / Rock desert", fill=(255,220,150), font=font)
    y += 18
    for i, (terrain, path, primary) in enumerate(placement_refs):
        row, col = divmod(i, cols)
        draw_thumb(canvas, draw, path, 12 + col*cell_w, y + row*cell_h, path.name, font)
    y += placement_rows * cell_h + 8

    for group, (pattern, _) in GROUPS.items():
        paths = sorted(cdir.glob(pattern))
        draw.text((12, y), group, fill=(180,230,255), font=font)
        y += 18
        for i, path in enumerate(paths):
            row, col = divmod(i, cols)
            draw_thumb(canvas, draw, path, 12 + col*cell_w, y + row*cell_h, path.name, font)
        y += math.ceil(len(paths) / cols) * cell_h + 8
    return canvas.crop((0, 0, canvas.width, y + 4))


def animation_metrics(paths: list[Path], placement_style):
    by_part = {}
    for p in paths:
        tokens = p.stem.split("_")
        if len(tokens) < 3 or not tokens[-1].isdigit():
            continue
        part = "_".join(tokens[:-1])
        by_part.setdefault(part, []).append(p)

    out = []
    for part, frames in sorted(by_part.items()):
        frames = sorted(frames)
        stats = [image_stats(p, placement_style) for p in frames]
        dl = [abs(stats[i+1].edge_luma - stats[i].edge_luma) for i in range(len(stats)-1)]
        dc = [abs(stats[i+1].edge_chroma - stats[i].edge_chroma) for i in range(len(stats)-1)]
        ds = [abs(stats[i+1].saturation - stats[i].saturation) for i in range(len(stats)-1)]
        out.append((part, max(dl, default=0), max(dc, default=0), max(ds, default=0)))
    return out


def main():
    config = load_config()
    guids = guid_map()
    DOCS.mkdir(parents=True, exist_ok=True)
    lines = [
        "# Independent terrain cluster visual audit",
        "",
        "Generated from the actual PNGs on the feature branch. This audit does not read or trust palette-normalization-report.md.",
        "",
        "The comparison set contains every configured ordinary terrain texture (main + alternatives). "
        "Edge score uses only the terrain-like half of pixels in the outer regular-hex band, so the "
        "intentional acid/mud/canyon/wreck feature itself does not dominate the background-continuity check.",
        "",
    ]

    for biome in ("AridSteppe", "Desert"):
        refs, placement = configured_refs(config, biome, guids)
        placement_paths = [p for _, p, _ in placement]
        placement_style = texture_style(placement_paths)

        placement_stats = [(t, p, image_stats(p, placement_style)) for t, p, _ in placement]
        self_scores = np.array([s.edge_score for _, _, s in placement_stats])
        self_p95 = float(np.percentile(self_scores, 95))
        self_max = float(np.max(self_scores))

        reference_sheet(biome, refs).save(DOCS / f"{biome.lower()}-reference-terrain-sheet.png")
        cluster_sheet(biome, placement).save(DOCS / f"{biome.lower()}-current-clusters-audit.png")

        lines += [
            f"## {biome}",
            "",
            f"- Ordinary configured references: **{len(refs)}**.",
            f"- Placement-compatible references: **{len(placement)}**.",
            f"- Placement-reference edge-score envelope: p95 **{self_p95:.3f}**, max **{self_max:.3f}**.",
            f"- Placement luminance range: {fmt_range([s.luma for _,_,s in placement_stats])}.",
            f"- Placement contrast range: {fmt_range([s.contrast for _,_,s in placement_stats])}.",
            f"- Placement saturation range: {fmt_range([s.saturation for _,_,s in placement_stats])}.",
            f"- Placement detail-density range: {fmt_range([s.detail for _,_,s in placement_stats])}.",
            "",
            "| Group | Files | edge mean | edge max | worst file | L range | contrast range | saturation range | detail range |",
            "|---|---:|---:|---:|---|---|---|---|---|",
        ]

        cdir = TERRAIN_ROOT / biome / "Complexes"
        for group, (pattern, expected) in GROUPS.items():
            paths = sorted(cdir.glob(pattern))
            if len(paths) != expected:
                raise RuntimeError(f"{biome}/{group}: expected {expected}, got {len(paths)}")
            vals = [(p, image_stats(p, placement_style)) for p in paths]
            scores = [s.edge_score for _, s in vals]
            worst_p, worst_s = max(vals, key=lambda x: x[1].edge_score)
            lines.append(
                f"| {group} | {len(paths)} | {np.mean(scores):.3f} | {max(scores):.3f} | "
                f"{worst_p.name} | {fmt_range([s.luma for _,s in vals])} | "
                f"{fmt_range([s.contrast for _,s in vals])} | "
                f"{fmt_range([s.saturation for _,s in vals])} | "
                f"{fmt_range([s.detail for _,s in vals])} |"
            )

            anim = animation_metrics(paths, placement_style)
            if anim:
                lines += ["", f"**{group} animation continuity (edge/background):**", ""]
                for part, dl, dc, ds in anim:
                    lines.append(
                        f"- {part}: max adjacent-frame delta edge luminance={dl:.4f}, "
                        f"delta edge chroma={dc:.4f}, delta saturation={ds:.4f}"
                    )

            p1 = sorted(cdir.glob(f"{group}_Part1_*.png"))
            p2 = sorted(cdir.glob(f"{group}_Part2_*.png"))
            if p1 and len(p1) == len(p2):
                diffs_l, diffs_c = [], []
                for a, b in zip(p1, p2):
                    sa, sb = image_stats(a, placement_style), image_stats(b, placement_style)
                    diffs_l.append(abs(sa.edge_luma - sb.edge_luma))
                    diffs_c.append(abs(sa.edge_chroma - sb.edge_chroma))
                lines.append(
                    f"- Part1-to-Part2 same-frame agreement: max delta edge luminance={max(diffs_l):.4f}, "
                    f"max delta edge chroma={max(diffs_c):.4f}"
                )

        lines += [
            "",
            f"Visual sheets: {biome.lower()}-reference-terrain-sheet.png and "
            f"{biome.lower()}-current-clusters-audit.png.",
            "",
        ]

    (DOCS / "independent-visual-audit.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print("\n".join(lines))


if __name__ == "__main__":
    main()
