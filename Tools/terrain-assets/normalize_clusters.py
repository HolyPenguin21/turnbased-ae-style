"""Normalize authored terrain-complex colours against each biome's terrain set.

The tool changes RGB only. Dimensions and alpha stay byte-for-byte stable.
A shared OKLab transform is fitted per biome/complex family from the closest terrain-like
pixels in the outer hex band. The subset is deliberately conservative: feature-heavy assets
such as AcidLake must never be forced to contribute acid pixels merely to fill a fixed
majority quota. The transform is then applied adaptively: ordinary ground is corrected
strongly (especially near the hex edge), while distinctive acid, mud, canyon interiors and
wreck metal stay effectively untouched.

A second, tightly capped residual pass equalises terrain-like background between parts.
For animated families the residual is shared by every frame of a given Part, so the
normalizer cannot introduce frame-to-frame colour/brightness flicker.

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
from PIL import Image, ImageDraw, ImageFilter, ImageFont

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

CHANNEL_FLOOR = np.array([0.022, 0.010, 0.010], dtype=np.float64)
SHIFT_LIMIT = np.array([0.16, 0.070, 0.070], dtype=np.float64)
SCALE_MIN = np.array([0.38, 0.55, 0.55], dtype=np.float64)
SCALE_MAX = np.array([1.20, 1.18, 1.18], dtype=np.float64)
RESIDUAL_LIMIT = np.array([0.012, 0.0045, 0.0045], dtype=np.float64)
GROUND_KEEP_FRACTION = 0.30

# Animated lakes/mud need a second, explicitly edge-biased ground pass. Their authored
# features occupy most of the hex, so a whole-image palette match leaves the small exposed
# terrain rim too contrasty even when its average hue is already correct. The pass below
# keys mostly on terrain chroma (not luminance), allowing dark/light grains of the same soil
# to be compressed without pulling green acid or neutral mud toward sand colours.
EDGE_REFINEMENT_STRENGTH = {
    "AcidLake": 0.96,
    "BoilingMud": 0.94,
}
EDGE_LOW_FREQUENCY_SCALE = {
    "AcidLake": 0.14,
    "BoilingMud": 0.18,
}
EDGE_DETAIL_SCALE = {
    "AcidLake": 1.00,
    "BoilingMud": 0.35,
}
EDGE_CHROMA_SPREAD_MULT = 2.25
EDGE_BLUR_RADIUS = 10.0


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


def hex_norm_map(side: int) -> np.ndarray:
    """0 in the centre, about 1 on a flat-top regular-hex boundary."""
    yy, xx = np.mgrid[0:side, 0:side]
    x = ((xx + 0.5) / side - 0.5) * 2.0
    y = ((yy + 0.5) / side - 0.5) * 2.0
    p0 = np.abs(x)
    p1 = np.abs(0.5 * x + (math.sqrt(3) / 2.0) * y)
    p2 = np.abs(0.5 * x - (math.sqrt(3) / 2.0) * y)
    return np.maximum.reduce([p0, p1, p2])


def regular_hex_mask(side: int, scale: float = 1.0) -> np.ndarray:
    return hex_norm_map(side) <= scale


def smoothstep(a: float, b: float, x: np.ndarray) -> np.ndarray:
    t = np.clip((x - a) / max(b - a, 1e-9), 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


def sample_image(path: Path, side: int = 96) -> np.ndarray:
    with Image.open(path) as src:
        rgba = src.convert("RGBA").resize((side, side), Image.Resampling.LANCZOS)
    arr = np.asarray(rgba, dtype=np.float64) / 255.0
    mask = (arr[..., 3] > 0.10) & regular_hex_mask(side, 1.0)
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
    return StyleStats(
        center=0.20 * all_style.center + 0.80 * placement_style.center,
        spread=np.maximum(0.20 * all_style.spread + 0.80 * placement_style.spread, CHANNEL_FLOOR),
    )


def terrain_like_subset(lab: np.ndarray, target: StyleStats, keep_fraction: float) -> np.ndarray:
    norm = (lab - target.center) / np.maximum(target.spread, CHANNEL_FLOOR)
    dist = np.sqrt(np.sum(norm * norm, axis=1))
    n = max(32, min(len(dist), int(round(len(dist) * keep_fraction))))
    idx = np.argpartition(dist, n - 1)[:n]
    return idx


def ground_ring_samples(path: Path, target: StyleStats, side: int = 112,
                        keep_fraction: float = GROUND_KEEP_FRACTION) -> np.ndarray:
    with Image.open(path) as src:
        rgba = src.convert("RGBA").resize((side, side), Image.Resampling.LANCZOS)
    arr = np.asarray(rgba, dtype=np.float64) / 255.0
    norm = hex_norm_map(side)
    ring = (norm >= 0.66) & (norm <= 1.0) & (arr[..., 3] > 0.10)
    rgb = arr[..., :3][ring]
    if len(rgb) < 64:
        rgb = arr[..., :3][(norm <= 1.0) & (arr[..., 3] > 0.10)]
    lab = rgb_to_oklab(rgb)
    idx = terrain_like_subset(lab, target, keep_fraction)
    return lab[idx]


def group_source_style(paths: list[Path], target: StyleStats) -> StyleStats:
    pixels = np.concatenate([ground_ring_samples(path, target) for path in paths], axis=0)
    return robust_stats(pixels)


def correction(source: StyleStats, target: StyleStats) -> tuple[np.ndarray, np.ndarray]:
    shift = np.clip(target.center - source.center, -SHIFT_LIMIT, SHIFT_LIMIT)
    desired_center = source.center + shift
    scale = np.clip(target.spread / np.maximum(source.spread, CHANNEL_FLOOR), SCALE_MIN, SCALE_MAX)
    return desired_center, scale


def affinity_and_edge(lab: np.ndarray, source: StyleStats, h: int, w: int) -> tuple[np.ndarray, np.ndarray]:
    norm = (lab - source.center) / np.maximum(source.spread * 1.95, CHANNEL_FLOOR)
    distance = np.sqrt(np.sum(norm * norm, axis=1))
    affinity = np.exp(-0.5 * (distance / 1.35) ** 2)

    if h == w:
        edge = smoothstep(0.56, 0.95, hex_norm_map(h)).reshape(-1)
    else:
        edge = np.zeros(h * w, dtype=np.float64)
    return affinity, edge


def transform_rgb(rgb: np.ndarray, source: StyleStats, desired_center: np.ndarray,
                  scale: np.ndarray, target: StyleStats) -> np.ndarray:
    shape = rgb.shape
    h, w = shape[:2]
    lab = rgb_to_oklab(rgb.reshape(-1, 3))
    corrected = desired_center + (lab - source.center) * scale

    affinity, edge = affinity_and_edge(lab, source, h, w)
    # No feature floor: pixels that do not resemble the authored ground keep their
    # original colour. This is essential for acid/mud/metal identity.
    ground_weight = np.power(affinity, 1.35) * (0.82 + 0.18 * edge)
    weight = np.clip(ground_weight, 0.0, 0.99)

    out_lab = lab + weight[:, None] * (corrected - lab)
    return np.clip(oklab_to_rgb(out_lab).reshape(shape), 0.0, 1.0)


def refine_edge_background(rgb: np.ndarray, target: StyleStats,
                           strength: float, low_frequency_scale: float,
                           detail_scale: float) -> np.ndarray:
    """Reduce broad edge-ground tonal mismatch without erasing authored texture.

    The first normalization pass already aligns the ground colour centre. This pass therefore
    must *not* apply another centre shift. It separates OKLab L into a blurred low-frequency
    component plus local detail, compresses only the broad shading around the biome target,
    and keeps the high-frequency detail term unchanged. A target-chroma gate protects green
    acid and neutral/brown mud; the spatial weight rises toward the regular-hex boundary.
    """
    shape = rgb.shape
    h, w = shape[:2]
    if h != w:
        return rgb

    lab = rgb_to_oklab(rgb.reshape(-1, 3))
    chroma_scale = np.maximum(
        target.spread[1:] * EDGE_CHROMA_SPREAD_MULT,
        CHANNEL_FLOOR[1:] * 1.35,
    )
    chroma_delta = (lab[:, 1:] - target.center[1:]) / chroma_scale
    chroma_distance = np.sqrt(np.sum(chroma_delta * chroma_delta, axis=1))
    terrain_affinity = np.exp(-0.5 * (chroma_distance / 1.25) ** 2)
    edge = smoothstep(0.30, 0.90, hex_norm_map(h)).reshape(-1)

    luma = lab[:, 0].reshape(h, w)
    luma_u8 = np.rint(np.clip(luma, 0.0, 1.0) * 255.0).astype(np.uint8)
    low = np.asarray(
        Image.fromarray(luma_u8, "L").filter(
            ImageFilter.GaussianBlur(radius=EDGE_BLUR_RADIUS)
        ),
        dtype=np.float64,
    ) / 255.0
    detail = luma - low
    corrected_luma = (
        target.center[0]
        + (low - target.center[0]) * low_frequency_scale
        + detail * detail_scale
    ).reshape(-1)

    weight = np.clip(
        strength * edge * np.power(terrain_affinity, 1.12),
        0.0,
        0.985,
    )
    out_lab = lab.copy()
    out_lab[:, 0] = lab[:, 0] + weight * (corrected_luma - lab[:, 0])
    return np.clip(oklab_to_rgb(out_lab).reshape(shape), 0.0, 1.0)

def edge_ground_center(rgb: np.ndarray, target: StyleStats) -> np.ndarray:
    h, w = rgb.shape[:2]
    if h != w:
        return robust_stats(rgb_to_oklab(rgb.reshape(-1, 3))).center
    norm = hex_norm_map(h)
    ring = (norm >= 0.66) & (norm <= 1.0)
    lab = rgb_to_oklab(rgb[ring])
    idx = terrain_like_subset(lab, target, GROUND_KEEP_FRACTION)
    return np.median(lab[idx], axis=0)


def residual_equalize(rgb: np.ndarray, source: StyleStats, target: StyleStats,
                      residual: np.ndarray) -> np.ndarray:
    shape = rgb.shape
    h, w = shape[:2]
    lab = rgb_to_oklab(rgb.reshape(-1, 3))
    affinity, edge = affinity_and_edge(lab, source, h, w)
    weight = np.power(affinity, 1.35) * (0.58 + 0.42 * edge)
    weight = np.where(affinity >= 0.12, weight, 0.0)
    out = lab + weight[:, None] * residual[None, :]
    return np.clip(oklab_to_rgb(out).reshape(shape), 0.0, 1.0)


def metric_for_rgb(rgb: np.ndarray, target: StyleStats) -> float:
    image = Image.fromarray(np.rint(np.clip(rgb, 0, 1) * 255).astype(np.uint8), "RGB")
    image = image.resize((96, 96), Image.Resampling.LANCZOS)
    arr = np.asarray(image, dtype=np.float64) / 255.0
    norm_map = hex_norm_map(96)
    ring = (norm_map >= 0.64) & (norm_map <= 1.0)
    lab = rgb_to_oklab(arr[ring])
    norm = (lab - target.center) / np.maximum(target.spread, CHANNEL_FLOOR)
    dist = np.sqrt(np.sum(norm * norm, axis=1))
    n = max(1, int(len(dist) * GROUND_KEEP_FRACTION))
    return float(np.mean(np.partition(dist, n - 1)[:n]))


def open_rgb_alpha(path: Path) -> tuple[np.ndarray, np.ndarray | None, str]:
    with Image.open(path) as src:
        mode = src.mode
        rgba = np.asarray(src.convert("RGBA"), dtype=np.uint8)
    rgb = rgba[..., :3].astype(np.float64) / 255.0
    alpha = rgba[..., 3].copy() if ("A" in mode or mode in ("P", "LA")) else None
    return rgb, alpha, mode


def save_rgb_alpha(path: Path, rgb: np.ndarray, alpha: np.ndarray | None) -> None:
    out = np.rint(np.clip(rgb, 0.0, 1.0) * 255.0).astype(np.uint8)
    if alpha is None:
        Image.fromarray(out, "RGB").save(path, optimize=True)
    else:
        Image.fromarray(np.dstack([out, alpha]), "RGBA").save(path, optimize=True)


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
    draw.text((12, y), f"{biome} adaptive palette normalization - left source, right result",
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
        desired_center, scale = correction(source, target)

        original: dict[Path, tuple[np.ndarray, np.ndarray | None, str]] = {
            path: open_rgb_alpha(path) for path in paths
        }
        transformed: dict[Path, np.ndarray] = {}
        before_scores: list[float] = []

        for path in paths:
            before_rgb, _, _ = original[path]
            before_scores.append(metric_for_rgb(before_rgb, target))
            after_rgb = transform_rgb(before_rgb, source, desired_center, scale, target)
            edge_strength = EDGE_REFINEMENT_STRENGTH.get(group)
            if edge_strength is not None:
                after_rgb = refine_edge_background(
                    after_rgb,
                    target,
                    edge_strength,
                    EDGE_LOW_FREQUENCY_SCALE[group],
                    EDGE_DETAIL_SCALE[group],
                )
            transformed[path] = after_rgb

        centers = {path: edge_ground_center(rgb, target) for path, rgb in transformed.items()}

        # Animated assets keep one residual per Part across all seven frames. Static
        # assets may use one residual per file because there is no temporal continuity
        # to protect.
        residuals: dict[Path, np.ndarray] = {}
        if group in ("AcidLake", "BoilingMud"):
            by_part: dict[str, list[Path]] = {}
            for path in paths:
                match = re.match(r"(.+_Part\d+)_\d+$", path.stem)
                key = match.group(1) if match else path.stem
                by_part.setdefault(key, []).append(path)
            for part_paths in by_part.values():
                part_center = np.median(np.stack([centers[p] for p in part_paths]), axis=0)
                residual = np.clip(target.center - part_center, -RESIDUAL_LIMIT, RESIDUAL_LIMIT)
                for path in part_paths:
                    residuals[path] = residual
        else:
            for path in paths:
                residuals[path] = np.clip(
                    target.center - centers[path], -RESIDUAL_LIMIT, RESIDUAL_LIMIT
                )

        for path in paths:
            transformed[path] = residual_equalize(
                transformed[path], source, target, residuals[path]
            )

        after_scores: list[float] = []
        residual_magnitudes: list[float] = []
        for path in paths:
            before_rgb, alpha, _ = original[path]
            before_alpha = None if alpha is None else alpha.copy()
            after_rgb = transformed[path]
            before = metric_for_rgb(before_rgb, target)
            after = metric_for_rgb(after_rgb, target)
            after_scores.append(after)
            residual_magnitudes.append(float(np.linalg.norm(residuals[path])))
            metrics.append(FileMetric(biome, group, path.name, before, after))
            contact_pairs.append((group, path.name, before_rgb, after_rgb))

            if write:
                original_size = before_rgb.shape[:2]
                save_rgb_alpha(path, after_rgb, alpha)
                with Image.open(path) as check:
                    if check.size != (original_size[1], original_size[0]):
                        raise RuntimeError(f"{path}: dimensions changed during normalization")
                    if before_alpha is not None:
                        after_alpha = np.asarray(check.convert("RGBA"), dtype=np.uint8)[..., 3]
                        if not np.array_equal(before_alpha, after_alpha):
                            raise RuntimeError(f"{path}: alpha changed during normalization")

        summary.append(
            f"{biome}/{group}: {len(paths)} files, edge terrain distance "
            f"{np.mean(before_scores):.3f} -> {np.mean(after_scores):.3f}, "
            f"ground center shift {np.round(desired_center - source.center, 4).tolist()}, "
            f"ground spread scale {np.round(scale, 3).tolist()}, "
            f"max residual {max(residual_magnitudes):.4f}"
        )

    if report:
        DOCS.mkdir(parents=True, exist_ok=True)
        build_contact_sheet(biome, contact_pairs).save(
            DOCS / f"{biome.lower()}-palette-normalization-before-after.png",
            optimize=True,
        )
    return metrics, summary


def write_report(metrics: list[FileMetric], summaries: list[str]) -> None:
    lines = [
        "# Terrain complex palette normalization",
        "",
        "Generated by Tools/terrain-assets/normalize_clusters.py.",
        "",
        "The source runtime PNGs are restored from the feature branch merge-base before this tool runs, "
        "so repeated workflow runs are deterministic rather than cumulative.",
        "",
        "Each biome is evaluated independently against every ordinary configured terrain texture "
        "(main + alternatives). The target is weighted toward Desert / Sand dunes / Rock desert because "
        "complexes can be placed only on those surfaces.",
        "",
        "The main correction is fitted from the closest terrain-like pixels in the outer hex band and "
        "shared by the whole complex family. Ground receives a strong correction, especially near the "
        "hex boundary; distinctive acid/mud/canyon/wreck pixels are not given a forced feature correction. "
        "AcidLake and BoilingMud additionally use a chroma-gated edge pass that compresses "
        "low-frequency exposed-soil luminance; BoilingMud also softens excess crack contrast only in "
        "terrain-like edge pixels, while the central feature remains protected. "
        "Animated families use one background-only residual per Part across all frames, preventing the "
        "normalizer from introducing temporal flicker. RGB only is modified; dimensions and alpha are preserved.",
        "",
        "## Group transforms",
        "",
    ]
    lines.extend(f"- {line}" for line in summaries)
    lines.extend([
        "",
        "## Per-file edge/background comparison",
        "",
        "Lower edge terrain distance is closer to the biome's placement-compatible terrain envelope. "
        "The metric uses the terrain-like portion of the outer regular-hex band so the authored obstacle "
        "itself does not dominate the score.",
        "",
        "| Biome | Group | File | Source | Result | Delta |",
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
        "Independent visual audit sheets are produced by audit_clusters.py after normalization.",
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
    improved = sum(1 for item in all_metrics if item.after < item.before)
    print(f"Per-file edge/background score improved: {improved}/{len(all_metrics)}")
    if improved < math.ceil(len(all_metrics) * 0.90):
        raise SystemExit("Normalization did not improve enough individual files; review the transform.")


if __name__ == "__main__":
    main()
