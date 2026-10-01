"""Normalize authored terrain-complex colors against each biome's existing terrain set.

The runtime hex mesh owns clipping/alpha; this tool changes RGB only. It derives one shared
OKLab transform per complex family and biome, so animation frames cannot acquire per-frame
color flicker. The target is built from every ordinary terrain texture in that biome, while the seam fit
strongly prioritizes Desert / Sand dunes / Rock desert because complexes can only be placed there.

Requires: Pillow, NumPy, PyYAML.
"""
from __future__ import annotations

import argparse
import math
import re
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable

import numpy as np
import yaml
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[2]
CONFIG = ROOT / "Assets/Config/GameConfig.asset"
DOCS = ROOT / "Docs/terrain-complexes"
TERRAIN_ROOT = ROOT / "Assets/Textures/Terrain"

GROUPS = {
    "AcidLake": ("AcidLake_*.png", 14),
    "Canyon": ("Canyon_*.png", 3),
    "BoilingMud": ("BoilingMud_*.png", 14),
    "GiantMachineWreck": ("GiantMachineWreck_*.png", 3),
}
PLACEMENT_TERRAINS = {"Desert", "Sand dunes", "Rock desert"}

M1 = np.array([
    [0.4122214708, 0.5363325363, 0.0514459929],
    [0.2119034982, 0.6806995451, 0.1073969566],
    [0.0883024619, 0.2817188376, 0.6299787005],
], dtype=np.float64)
M2 = np.array([
    [0.2104542553, 0.7936177850, -0.0040720468],
    [1.9779984951, -2.4285922050, 0.4505937099],
    [0.0259040371, 0.7827717662, -0.8086757660],
], dtype=np.float64)
M2_INV = np.array([
    [1.0, 0.3963377774, 0.2158037573],
    [1.0, -0.1055613458, -0.0638541728],
    [1.0, -0.0894841775, -1.2914855480],
], dtype=np.float64)
M1_INV = np.array([
    [4.0767416621, -3.3077115913, 0.2309699292],
    [-1.2684380046, 2.6097574011, -0.3413193965],
    [-0.0041960863, -0.7034186147, 1.7076147010],
], dtype=np.float64)

CHANNEL_FLOOR = np.array([0.025, 0.012, 0.012], dtype=np.float64)
# Background soil on the authored complexes starts substantially darker than the biome
# references (especially Desert AcidLake). The original 0.11 L cap was reached before
# the terrain edge entered the real reference envelope. The stronger limits are safe
# because transform_rgb now applies them primarily to source-like ground pixels.
SHIFT_LIMIT = np.array([0.18, 0.070, 0.070], dtype=np.float64)
SCALE_MIN = np.array([0.80, 0.78, 0.78], dtype=np.float64)
SCALE_MAX = np.array([1.20, 1.22, 1.22], dtype=np.float64)


@dataclass
class StyleStats:
    center: np.ndarray
    spread: np.ndarray


@dataclass
class FileMetric:
    biome: str
    group: str
    file: str
    before: float
    after: float


def load_config() -> dict:
    raw = CONFIG.read_text(encoding="utf-8")
    clean = "\n".join(line for line in raw.splitlines() if not line.startswith(("%", "---")))
    return yaml.safe_load(clean)["MonoBehaviour"]["mapGeneration"]


def guid_map() -> dict[str, Path]:
    result: dict[str, Path] = {}
    for meta in TERRAIN_ROOT.rglob("*.meta"):
        match = re.search(r"^guid: ([0-9a-f]+)$", meta.read_text(encoding="utf-8"), re.M)
        if match:
            result[match.group(1)] = Path(str(meta)[:-5])
    return result


def texture_refs(entry: dict) -> list[dict]:
    result = [entry["texture"]]
    result.extend(entry.get("alternativeTextures") or [])
    return result


def palette_for(config: dict, biome: str) -> dict:
    return config if biome == "AridSteppe" else config["desertOverride"]


def resolve_reference_paths(config: dict, biome: str, guids: dict[str, Path]) -> tuple[list[Path], list[Path]]:
    palette = palette_for(config, biome)
    ordinary = [
        entry for entry in palette["terrainTypes"]
        if entry.get("baselineWeight", 0) > 0 and not entry.get("blocksGroundMovement", 0)
    ]
    all_paths: list[Path] = []
    placement_paths: list[Path] = []
    for entry in ordinary:
        for ref in texture_refs(entry):
            path = guids.get(ref["guid"])
            if path is None:
                raise RuntimeError(f"{biome}: unresolved terrain GUID {ref['guid']}")
            if "Complexes" in path.parts:
                raise RuntimeError(f"{biome}: reference set unexpectedly contains complex asset: {path}")
            all_paths.append(path)
            if entry["terrainName"] in PLACEMENT_TERRAINS:
                placement_paths.append(path)
    if not all_paths or not placement_paths:
        raise RuntimeError(f"{biome}: empty reference texture set")
    return all_paths, placement_paths


def srgb_to_linear(rgb: np.ndarray) -> np.ndarray:
    return np.where(rgb <= 0.04045, rgb / 12.92, ((rgb + 0.055) / 1.055) ** 2.4)


def linear_to_srgb(rgb: np.ndarray) -> np.ndarray:
    rgb = np.clip(rgb, 0.0, 1.0)
    return np.where(rgb <= 0.0031308, 12.92 * rgb, 1.055 * (rgb ** (1.0 / 2.4)) - 0.055)


def rgb_to_oklab(rgb: np.ndarray) -> np.ndarray:
    linear = srgb_to_linear(np.clip(rgb, 0.0, 1.0))
    lms = linear @ M1.T
    return np.cbrt(np.maximum(lms, 0.0)) @ M2.T


def oklab_to_rgb(lab: np.ndarray) -> np.ndarray:
    lms_root = lab @ M2_INV.T
    lms = lms_root ** 3
    linear = lms @ M1_INV.T
    return linear_to_srgb(linear)


def robust_stats(values: np.ndarray) -> StyleStats:
    center = np.median(values, axis=0)
    q25 = np.percentile(values, 25, axis=0)
    q75 = np.percentile(values, 75, axis=0)
    spread = np.maximum((q75 - q25) / 1.349, CHANNEL_FLOOR)
    return StyleStats(center=center, spread=spread)


def sample_image(path: Path, side: int = 96) -> np.ndarray:
    with Image.open(path) as src:
        rgba = src.convert("RGBA").resize((side, side), Image.Resampling.LANCZOS)
    arr = np.asarray(rgba, dtype=np.float64) / 255.0
    mask = arr[..., 3] > 0.10
    rgb = arr[..., :3][mask]
    if rgb.size == 0:
        raise RuntimeError(f"No visible pixels: {path}")
    return rgb_to_oklab(rgb)


def texture_style(paths: Iterable[Path]) -> StyleStats:
    centers = []
    spreads = []
    for path in paths:
        stats = robust_stats(sample_image(path))
        centers.append(stats.center)
        spreads.append(stats.spread)
    return StyleStats(
        center=np.median(np.stack(centers), axis=0),
        spread=np.maximum(np.median(np.stack(spreads), axis=0), CHANNEL_FLOOR),
    )


def blended_target(all_style: StyleStats, placement_style: StyleStats) -> StyleStats:
    # Complexes can only be generated on Desert / Sand dunes / Rock desert, so those
    # textures define the seam-matching target. Keep a small contribution from every
    # ordinary texture to remain inside the biome's overall palette.
    return StyleStats(
        center=0.15 * all_style.center + 0.85 * placement_style.center,
        spread=np.maximum(0.15 * all_style.spread + 0.85 * placement_style.spread, CHANNEL_FLOOR),
    )


def hex_outer_ring(side: int, inner: float = 0.70) -> np.ndarray:
    """Approximate the visible outer band of the pointy-top runtime hex mesh."""
    yy, xx = np.mgrid[0:side, 0:side]
    x = (xx + 0.5) / side
    y = (yy + 0.5) / side

    def inside(scale: float) -> np.ndarray:
        cx = cy = 0.5
        xs = (x - cx) / scale + cx
        ys = (y - cy) / scale + cy
        dx = np.abs(xs - 0.5)
        return (
            (dx <= 0.5)
            & (ys >= np.maximum(0.0, 2.0 * dx - 0.5))
            & (ys <= np.minimum(1.0, 1.5 - 2.0 * dx))
        )

    return inside(1.0) & ~inside(inner)


def sample_edge_image(path: Path, side: int = 96) -> np.ndarray:
    with Image.open(path) as src:
        rgba = src.convert("RGBA").resize((side, side), Image.Resampling.LANCZOS)
    arr = np.asarray(rgba, dtype=np.float64) / 255.0
    mask = (arr[..., 3] > 0.10) & hex_outer_ring(side)
    rgb = arr[..., :3][mask]
    if rgb.size == 0:
        raise RuntimeError(f"No visible edge pixels: {path}")
    return rgb_to_oklab(rgb)


def group_source_style(paths: list[Path], target: StyleStats) -> StyleStats:
    # The visual seam is decided at the hex perimeter. Identify the source ground from
    # that region and use the terrain-like 60% only, rather than letting acid/mud/metal
    # dominate the fit.
    pixels = np.concatenate([sample_edge_image(path, 96) for path in paths], axis=0)
    norm = (pixels - target.center) / np.maximum(target.spread, CHANNEL_FLOOR)
    dist = np.sqrt(np.sum(norm * norm, axis=1))
    cutoff = np.quantile(dist, 0.60)
    return robust_stats(pixels[dist <= cutoff])


def correction(source: StyleStats, target: StyleStats, group: str) -> tuple[np.ndarray, np.ndarray]:
    shift = np.clip(target.center - source.center, -SHIFT_LIMIT, SHIFT_LIMIT)
    scale = np.clip(target.spread / np.maximum(source.spread, CHANNEL_FLOOR), SCALE_MIN, SCALE_MAX)

    # Once the edge-ground center is close, preserve feature-heavy families unless they
    # are BoilingMud. Mud can still have visibly excessive ground contrast/spread after
    # its center matches, so allow its feature-aware spread correction to converge.
    min_meaningful = np.array([0.015, 0.006, 0.006], dtype=np.float64)
    centered = np.all(np.abs(shift) < min_meaningful)
    if centered and group != "BoilingMud":
        return source.center.copy(), np.ones(3, dtype=np.float64)

    # For mud with a matched center, do not invent a center shift; only tighten/expand
    # the terrain-like spread around the existing center.
    desired_center = source.center.copy() if centered else source.center + shift
    return desired_center, scale


def transform_rgb(rgb: np.ndarray, source: StyleStats, desired_center: np.ndarray,
                  scale: np.ndarray, target: StyleStats) -> np.ndarray:
    shape = rgb.shape
    lab = rgb_to_oklab(rgb.reshape(-1, 3))
    corrected = desired_center + (lab - source.center) * scale

    # Ground/background pixels cluster around source.center and receive nearly the full
    # correction. Distinctive feature pixels (acid, boiling mud, canyon shadow, wreck)
    # are progressively protected instead of receiving the old unconditional 36% shift.
    norm = (lab - source.center) / np.maximum(source.spread, CHANNEL_FLOOR)
    distance = np.sqrt(np.sum(norm * norm, axis=1))
    weight = 0.05 + 0.95 * np.exp(-0.5 * (distance / 2.15) ** 2)
    out_lab = lab + weight[:, None] * (corrected - lab)
    return np.clip(oklab_to_rgb(out_lab).reshape(shape), 0.0, 1.0)


def metric_for_rgb(rgb: np.ndarray, target: StyleStats) -> float:
    image = Image.fromarray(np.rint(np.clip(rgb, 0, 1) * 255).astype(np.uint8), "RGB")
    image = image.resize((80, 80), Image.Resampling.LANCZOS)
    lab = rgb_to_oklab(np.asarray(image, dtype=np.float64).reshape(-1, 3) / 255.0)
    norm = (lab - target.center) / np.maximum(target.spread, CHANNEL_FLOOR)
    dist = np.sqrt(np.sum(norm * norm, axis=1))
    return float(np.mean(np.sort(dist)[: max(1, len(dist) // 2)]))


def open_rgb_alpha(path: Path) -> tuple[np.ndarray, np.ndarray | None]:
    with Image.open(path) as src:
        mode = src.mode
        rgba = np.asarray(src.convert("RGBA"), dtype=np.uint8)
    rgb = rgba[..., :3].astype(np.float64) / 255.0
    alpha = rgba[..., 3].copy() if ("A" in mode or mode in ("P", "LA")) else None
    return rgb, alpha


def save_rgb_alpha(path: Path, rgb: np.ndarray, alpha: np.ndarray | None) -> None:
    out = np.rint(np.clip(rgb, 0.0, 1.0) * 255.0).astype(np.uint8)
    if alpha is None:
        Image.fromarray(out, "RGB").save(path)
    else:
        Image.fromarray(np.dstack([out, alpha]), "RGBA").save(path)


def thumb(rgb: np.ndarray, size: int = 88) -> Image.Image:
    image = Image.fromarray(np.rint(np.clip(rgb, 0, 1) * 255).astype(np.uint8), "RGB")
    return image.resize((size, size), Image.Resampling.LANCZOS)


def build_contact_sheet(biome: str, pairs: list[tuple[str, str, np.ndarray, np.ndarray]]) -> Image.Image:
    pair_w = 196
    pair_h = 122
    cols = 6
    rows = 1
    for group in GROUPS:
        count = sum(1 for g, _, _, _ in pairs if g == group)
        rows += 1 + math.ceil(count / cols)
    canvas = Image.new("RGB", (cols * pair_w + 24, rows * pair_h + 36), (34, 34, 34))
    draw = ImageDraw.Draw(canvas)
    font = ImageFont.load_default()
    y = 12
    draw.text((12, y), f"{biome} palette normalization - left before, right after",
              fill=(235, 235, 235), font=font)
    y += 26
    for group in GROUPS:
        items = [item for item in pairs if item[0] == group]
        draw.text((12, y), group, fill=(245, 245, 245), font=font)
        y += 18
        for idx, (_, name, before, after) in enumerate(items):
            row, col = divmod(idx, cols)
            x = 12 + col * pair_w
            yy = y + row * pair_h
            canvas.paste(thumb(before), (x, yy))
            canvas.paste(thumb(after), (x + 92, yy))
            draw.text((x, yy + 91), name.replace(".png", "")[:30], fill=(220, 220, 220), font=font)
        y += math.ceil(len(items) / cols) * pair_h + 8
    return canvas.crop((0, 0, canvas.width, min(canvas.height, y + 8)))


def normalize_biome(config: dict, guids: dict[str, Path], biome: str, write: bool,
                    report: bool) -> tuple[list[FileMetric], list[str]]:
    all_refs, placement_refs = resolve_reference_paths(config, biome, guids)
    all_style = texture_style(all_refs)
    placement_style = texture_style(placement_refs)
    target = blended_target(all_style, placement_style)
    complex_dir = TERRAIN_ROOT / biome / "Complexes"
    metrics: list[FileMetric] = []
    summary: list[str] = []
    contact_pairs: list[tuple[str, str, np.ndarray, np.ndarray]] = []

    for group, (pattern, expected) in GROUPS.items():
        paths = sorted(complex_dir.glob(pattern))
        if len(paths) != expected:
            raise RuntimeError(f"{biome}/{group}: expected {expected} files, found {len(paths)}")
        source = group_source_style(paths, target)
        desired_center, scale = correction(source, target, group)
        before_scores = []
        after_scores = []

        for path in paths:
            before_rgb, alpha = open_rgb_alpha(path)
            before_alpha = None if alpha is None else alpha.copy()
            after_rgb = transform_rgb(before_rgb, source, desired_center, scale, target)
            before = metric_for_rgb(before_rgb, target)
            after = metric_for_rgb(after_rgb, target)
            before_scores.append(before)
            after_scores.append(after)
            metrics.append(FileMetric(biome, group, path.name, before, after))
            contact_pairs.append((group, path.name, before_rgb, after_rgb))

            if write:
                save_rgb_alpha(path, after_rgb, alpha)
                if before_alpha is not None:
                    with Image.open(path) as check:
                        after_alpha = np.asarray(check.convert("RGBA"), dtype=np.uint8)[..., 3]
                    if not np.array_equal(before_alpha, after_alpha):
                        raise RuntimeError(f"{path}: alpha changed during normalization")

        summary.append(
            f"{biome}/{group}: {len(paths)} files, terrain-style distance "
            f"{np.mean(before_scores):.3f} -> {np.mean(after_scores):.3f}, "
            f"center shift {np.round(desired_center - source.center, 4).tolist()}, "
            f"spread scale {np.round(scale, 3).tolist()}"
        )

    if report:
        DOCS.mkdir(parents=True, exist_ok=True)
        build_contact_sheet(biome, contact_pairs).save(
            DOCS / f"{biome.lower()}-palette-normalization-before-after.png"
        )
    return metrics, summary


def write_report(metrics: list[FileMetric], summaries: list[str]) -> None:
    lines = [
        "# Terrain complex palette normalization",
        "",
        "Generated by Tools/terrain-assets/normalize_clusters.py.",
        "",
        "Each biome is evaluated independently against all ordinary terrain textures configured in "
        "GameConfig (main + alternatives). The fitted target still weights Desert, Sand dunes and Rock "
        "desert more strongly because authored complexes can actually be placed only on those surfaces.",
        "",
        "One shared OKLab transform is fitted per complex family and biome. Animated frames are never "
        "normalized independently, preventing color/brightness flicker. RGB is adjusted; dimensions "
        "and alpha are preserved.",
        "",
        "## Group transforms",
        "",
    ]
    lines.extend(f"- {line}" for line in summaries)
    lines.extend([
        "",
        "## Per-file comparison",
        "",
        "Lower terrain-style distance is closer to the biome reference envelope. The score uses the "
        "terrain-like half of each image, so distinctive acid, mud, canyon and wreck features are not "
        "mistaken for background soil.",
        "",
        "| Biome | Group | File | Before | After | Delta |",
        "|---|---|---|---:|---:|---:|",
    ])
    for item in metrics:
        lines.append(
            f"| {item.biome} | {item.group} | {item.file} | {item.before:.3f} | "
            f"{item.after:.3f} | {item.after - item.before:+.3f} |"
        )
    lines.extend([
        "",
        "## Review images",
        "",
        "- aridsteppe-palette-normalization-before-after.png",
        "- desert-palette-normalization-before-after.png",
        "",
        "These are offline color-review sheets, not Unity screenshots. Final acceptance still requires "
        "an in-engine map review because URP/material lighting can shift perceived tone.",
        "",
    ])
    (DOCS / "palette-normalization-report.md").write_text("\n".join(lines), encoding="utf-8")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--write", action="store_true", help="overwrite runtime complex PNGs")
    parser.add_argument("--report", action="store_true", help="write per-file report/contact sheets")
    args = parser.parse_args()

    config = load_config()
    guids = guid_map()
    all_metrics: list[FileMetric] = []
    summaries: list[str] = []
    for biome in ("AridSteppe", "Desert"):
        metrics, summary = normalize_biome(config, guids, biome, args.write, args.report)
        all_metrics.extend(metrics)
        summaries.extend(summary)

    if args.report:
        write_report(all_metrics, summaries)

    print("\n".join(summaries))
    improved = sum(1 for item in all_metrics if item.after < item.before - 1e-4)
    regressed = [item for item in all_metrics if item.after > item.before + 0.02]
    unchanged = len(all_metrics) - improved - sum(
        1 for item in all_metrics if item.after > item.before + 1e-4
    )
    print(
        f"Per-file terrain-style score: improved={improved}, unchanged~={unchanged}, "
        f"material regressions={len(regressed)} / {len(all_metrics)}"
    )
    if regressed:
        names = ", ".join(f"{x.biome}/{x.file}" for x in regressed[:8])
        raise SystemExit(f"Normalization materially regressed files: {names}")

    # The transform is intentionally convergent/idempotent: after the first pass only one
    # family may still need work while already-aligned families remain unchanged. A fixed
    # percentage-of-files improvement gate incorrectly fails that healthy convergence.
    if improved == 0:
        print("Normalization is stable: no remaining per-file score improvement was required.")


if __name__ == "__main__":
    main()
