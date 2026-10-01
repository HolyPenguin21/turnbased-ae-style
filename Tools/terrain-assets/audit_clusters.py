"""Independent visual/pixel audit for terrain-complex assets.

Read-only for runtime PNGs. Compares every cluster asset against every ordinary configured
terrain texture in its biome, including alternativeTextures. The report focuses on the
terrain-like background in the outer regular-hex band, while the generated per-group sheets
make the full authored feature and every animation frame available for visual review.
"""
from __future__ import annotations

import csv
import math
from dataclasses import asdict, dataclass
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

from normalize_clusters import (
    CHANNEL_FLOOR,
    DOCS,
    GROUPS,
    PLACEMENT_TERRAINS,
    TERRAIN_ROOT,
    guid_map,
    hex_norm_map,
    load_config,
    palette_for,
    rgb_to_oklab,
    texture_refs,
    texture_style,
)

SIDE = 128
REF_THUMB = 82
GROUP_THUMB = 92


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
    edge_contrast: float
    edge_saturation: float
    edge_hue_deg: float
    edge_chroma: float
    edge_warmth: float
    edge_detail: float


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


def terrain_like_subset(lab: np.ndarray, target_center: np.ndarray, target_spread: np.ndarray,
                        keep_fraction: float = 0.30):
    norm = (lab - target_center) / np.maximum(target_spread, CHANNEL_FLOOR)
    dist = np.sqrt(np.sum(norm * norm, axis=1))
    n = max(16, min(len(dist), int(round(len(dist) * keep_fraction))))
    idx = np.argpartition(dist, n - 1)[:n]
    return idx, dist[idx]


def local_detail_map(rgb: np.ndarray) -> np.ndarray:
    gray = np.rint(np.clip(luma(rgb), 0.0, 1.0) * 255.0).astype(np.uint8)
    image = Image.fromarray(gray, "L")
    blur = np.asarray(image.filter(ImageFilter.GaussianBlur(radius=1.15)), dtype=np.float64) / 255.0
    return np.abs(gray.astype(np.float64) / 255.0 - blur)


def image_stats(path: Path, placement_style) -> PixelStats:
    rgb, alpha = read_rgba(path)
    norm = hex_norm_map(rgb.shape[0])
    visible = (alpha > 0.10) & (norm <= 1.0)
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
    detail_map = local_detail_map(rgb)
    detail = float(np.median(detail_map[visible]))

    ring = (norm >= 0.66) & (norm <= 1.0) & (alpha > 0.10)
    ring_rgb = rgb[ring]
    ring_detail = detail_map[ring]
    if len(ring_rgb) < 32:
        ring_rgb = vals
        ring_detail = detail_map[visible]
    ring_lab = rgb_to_oklab(ring_rgb)
    idx, d = terrain_like_subset(
        ring_lab, placement_style.center, placement_style.spread, keep_fraction=0.30
    )
    terrain_ring_rgb = ring_rgb[idx]
    terrain_ring_lab = ring_lab[idx]
    terrain_ring_detail = ring_detail[idx]
    ring_y = luma(terrain_ring_rgb.reshape(-1, 1, 3)).reshape(-1)
    ring_sat = sat_values(terrain_ring_rgb)
    ring_center = np.median(terrain_ring_lab, axis=0)
    rp10, rp90 = np.percentile(ring_y, [10, 90])

    return PixelStats(
        luma=float(np.median(yv)),
        contrast=contrast,
        saturation=float(np.median(sat)),
        hue_deg=hue,
        chroma=chroma,
        detail=detail,
        edge_score=float(np.mean(d)),
        edge_luma=float(np.median(ring_y)),
        edge_contrast=float(rp90 - rp10),
        edge_saturation=float(np.median(ring_sat)),
        edge_hue_deg=float(math.degrees(math.atan2(ring_center[2], ring_center[1]))),
        edge_chroma=float(math.hypot(ring_center[1], ring_center[2])),
        edge_warmth=float(ring_center[2]),
        edge_detail=float(np.median(terrain_ring_detail)),
    )


def fmt_range(vals):
    vals = np.asarray(vals, dtype=float)
    return f"{vals.min():.4f}..{vals.max():.4f} (med {np.median(vals):.4f})"


def draw_thumb(canvas, draw, path: Path, x: int, y: int, size: int, label: str, font):
    with Image.open(path) as src:
        thumb = src.convert("RGB").resize((size, size), Image.Resampling.LANCZOS)
    canvas.paste(thumb, (x, y))
    draw.rectangle((x, y, x + size - 1, y + size - 1), outline=(85, 85, 85))
    draw.text((x, y + size + 3), label[:20], fill=(235, 235, 235), font=font)


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
            draw_thumb(canvas, draw, path, x, yy, 132, label, font)
        y += math.ceil(len(items) / cols) * cell_h + 8
    return canvas.crop((0, 0, canvas.width, y + 4))


def group_sheet(biome: str, group: str, placement_refs, paths: list[Path], stats: list[PixelStats]):
    width = 786
    canvas = Image.new("RGB", (width, 1000), (30, 30, 30))
    draw = ImageDraw.Draw(canvas)
    font = ImageFont.load_default()
    y = 10
    draw.text((12, y), f"{biome} / {group} - placement references + every runtime PNG",
              fill=(245,245,245), font=font)
    y += 20
    edge_scores = [s.edge_score for s in stats]
    draw.text((12, y), f"edge score mean {np.mean(edge_scores):.3f}, max {max(edge_scores):.3f}",
              fill=(210,230,255), font=font)
    y += 22

    draw.text((12, y), "PLACEMENT REFERENCES (all main + alternatives)", fill=(255,220,150), font=font)
    y += 16
    ref_cols = 8
    ref_cell_w, ref_cell_h = 96, 106
    for i, (_, path, _) in enumerate(placement_refs):
        row, col = divmod(i, ref_cols)
        draw_thumb(canvas, draw, path, 10 + col*ref_cell_w, y + row*ref_cell_h,
                   REF_THUMB, path.stem[:14], font)
    y += math.ceil(len(placement_refs)/ref_cols)*ref_cell_h + 10

    draw.text((12, y), "CLUSTER PNGS", fill=(180,230,255), font=font)
    y += 16
    cols = 7
    cell_w, cell_h = 108, 118
    for i, path in enumerate(paths):
        row, col = divmod(i, cols)
        draw_thumb(canvas, draw, path, 10 + col*cell_w, y + row*cell_h,
                   GROUP_THUMB, path.stem.replace(group + "_", "")[:16], font)
    y += math.ceil(len(paths)/cols)*cell_h + 8

    draw.text((12, y), "Edge/background medians: L / contrast / saturation / hue / warmth / detail",
              fill=(215,215,215), font=font)
    y += 15
    for i, (path, s) in enumerate(zip(paths, stats)):
        draw.text(
            (12, y),
            f"{path.stem[-12:]:>12}  {s.edge_luma:.3f} / {s.edge_contrast:.3f} / "
            f"{s.edge_saturation:.3f} / {s.edge_hue_deg:.1f} / {s.edge_warmth:.3f} / {s.edge_detail:.3f}",
            fill=(200,200,200), font=font
        )
        y += 13

    return canvas.crop((0, 0, width, min(canvas.height, y + 8)))


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
        # Include the 06 -> 00 transition: runtime animation loops.
        pairs = list(zip(stats, stats[1:] + stats[:1]))
        dl = [abs(b.edge_luma - a.edge_luma) for a, b in pairs]
        dc = [abs(b.edge_chroma - a.edge_chroma) for a, b in pairs]
        ds = [abs(b.edge_saturation - a.edge_saturation) for a, b in pairs]
        dw = [abs(b.edge_warmth - a.edge_warmth) for a, b in pairs]
        out.append((part, max(dl, default=0), max(dc, default=0),
                    max(ds, default=0), max(dw, default=0)))
    return out


def main():
    config = load_config()
    guids = guid_map()
    DOCS.mkdir(parents=True, exist_ok=True)
    for stale in DOCS.glob("*-current-clusters-audit.png"):
        stale.unlink()

    csv_rows = []
    lines = [
        "# Independent terrain cluster visual audit",
        "",
        "Generated from the actual runtime PNGs after normalization. This audit does not read or trust "
        "palette-normalization-report.md.",
        "",
        "Every configured ordinary terrain texture (main + alternatives) is used. Background metrics "
        "sample the terrain-like portion of the outer regular-hex band, because that is the area that "
        "must continue naturally into a neighbouring ordinary hex. Full-image metrics are retained only "
        "as a secondary check for global brightness/contrast/shadows.",
        "",
        "The closest 30% of ring pixels is a heuristic, not a semantic ground mask. "
        "Where water, mud, canyon walls or wreckage cross the ring, feature/shore transitions "
        "can remain in that subset. A higher score or contrast therefore requires visual "
        "inspection; it is not a requirement to flatten the obstacle to ordinary sand. "
        "See runtime-pixel-audit.csv for all per-file values, including full-image metrics. "
        "Animation deltas include the last-to-first transition.",
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

        reference_sheet(biome, refs).save(
            DOCS / f"{biome.lower()}-reference-terrain-sheet.png", optimize=True
        )

        lines += [
            f"## {biome}",
            "",
            f"- Ordinary configured references: **{len(refs)}**.",
            f"- Placement-compatible references: **{len(placement)}**.",
            f"- Placement-reference edge-score envelope: p95 **{self_p95:.3f}**, max **{self_max:.3f}**.",
            f"- Edge luminance: {fmt_range([s.edge_luma for _,_,s in placement_stats])}.",
            f"- Edge contrast: {fmt_range([s.edge_contrast for _,_,s in placement_stats])}.",
            f"- Edge saturation: {fmt_range([s.edge_saturation for _,_,s in placement_stats])}.",
            f"- Edge hue: {fmt_range([s.edge_hue_deg for _,_,s in placement_stats])}.",
            f"- Edge warmth (OKLab b): {fmt_range([s.edge_warmth for _,_,s in placement_stats])}.",
            f"- Edge detail density: {fmt_range([s.edge_detail for _,_,s in placement_stats])}.",
            "",
        ]

        cdir = TERRAIN_ROOT / biome / "Complexes"
        for group, (pattern, expected) in GROUPS.items():
            paths = sorted(cdir.glob(pattern))
            if len(paths) != expected:
                raise RuntimeError(f"{biome}/{group}: expected {expected}, got {len(paths)}")
            vals = [(p, image_stats(p, placement_style)) for p in paths]
            stats = [s for _, s in vals]
            scores = [s.edge_score for s in stats]
            worst_p, _ = max(vals, key=lambda x: x[1].edge_score)
            lines += [
                f"### {group}", "",
                "| Group | Files | edge mean | edge max | worst file | edge L | edge contrast | edge sat | edge hue | edge warmth | edge detail |",
                "|---|---:|---:|---:|---|---|---|---|---|---|---|",
            ]
            for p, s in vals:
                csv_rows.append({"biome": biome, "group": group, "file": p.name, **asdict(s)})
            lines.append(
                f"| {group} | {len(paths)} | {np.mean(scores):.3f} | {max(scores):.3f} | "
                f"{worst_p.name} | {fmt_range([s.edge_luma for s in stats])} | "
                f"{fmt_range([s.edge_contrast for s in stats])} | "
                f"{fmt_range([s.edge_saturation for s in stats])} | "
                f"{fmt_range([s.edge_hue_deg for s in stats])} | "
                f"{fmt_range([s.edge_warmth for s in stats])} | "
                f"{fmt_range([s.edge_detail for s in stats])} |"
            )

            sheet_name = f"{biome.lower()}-{group.lower()}-audit.png"
            group_sheet(biome, group, placement, paths, stats).save(
                DOCS / sheet_name, optimize=True, compress_level=9
            )

            anim = animation_metrics(paths, placement_style)
            if anim:
                lines += ["", f"**{group} animation continuity (edge/background):**", ""]
                for part, dl, dc, ds, dw in anim:
                    lines.append(
                        f"- {part}: max adjacent-frame delta edge luminance={dl:.4f}, "
                        f"chroma={dc:.4f}, saturation={ds:.4f}, warmth={dw:.4f}"
                    )

            p1 = sorted(cdir.glob(f"{group}_Part1_*.png"))
            p2 = sorted(cdir.glob(f"{group}_Part2_*.png"))
            if p1 and len(p1) == len(p2):
                diffs_l, diffs_c, diffs_w = [], [], []
                for a, b in zip(p1, p2):
                    sa, sb = image_stats(a, placement_style), image_stats(b, placement_style)
                    diffs_l.append(abs(sa.edge_luma - sb.edge_luma))
                    diffs_c.append(abs(sa.edge_chroma - sb.edge_chroma))
                    diffs_w.append(abs(sa.edge_warmth - sb.edge_warmth))
                lines.append(
                    f"- Part1-to-Part2 same-frame agreement: max delta edge luminance={max(diffs_l):.4f}, "
                    f"chroma={max(diffs_c):.4f}, warmth={max(diffs_w):.4f}"
                )

            lines.append(f"- Visual sheet: {sheet_name}")
            lines.append("")

        lines += [""]

    (DOCS / "independent-visual-audit.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    with (DOCS / "runtime-pixel-audit.csv").open("w", newline="", encoding="utf-8") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(csv_rows[0]))
        writer.writeheader()
        writer.writerows(csv_rows)
    print("\n".join(lines))


if __name__ == "__main__":
    main()
