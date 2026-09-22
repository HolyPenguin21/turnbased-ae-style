#!/usr/bin/env python3
from __future__ import annotations

import sys
from pathlib import Path

try:
    from PIL import Image, ImageChops, ImageFilter, ImageOps
except ImportError:
    print(
        "Pillow is required.\n"
        "Install it from PowerShell with:\n"
        "  py -m pip install -r requirements.txt",
        file=sys.stderr,
    )
    raise SystemExit(1)


ROOT = Path(__file__).resolve().parent
BASES_DIR = ROOT / "Bases"
INPUT_DIR = ROOT / "Input"
OUTPUT_DIR = ROOT / "Output"

STATS_BASE_NAME = "Card_Base.png"
CLEAR_BASE_NAME = "Card_Base_Clear.png"
OUTPUT_SIZE = (768, 1120)

# Shared composition settings.
ART_CENTER_X = 0.50
ART_CENTER_Y = 0.48

# Feathering is calculated directly in final 768x1120 coordinates.
SIDE_FEATHER_PX = 38
TOP_FEATHER_PX = 27

# Stats card:
# the art must already be fully transparent when the top edge of the stat
# slots begins. The slot top is detected automatically by comparing the two
# bases, and the fade starts this many pixels above it.
STATS_FADE_HEIGHT_PX = 190

# Ignore tiny compression/color differences while detecting the stat UI.
STATS_DIFF_THRESHOLD = 8
STATS_MIN_CHANGED_ROW_RATIO = 0.03


def ensure_directories() -> None:
    for directory in (BASES_DIR, INPUT_DIR, OUTPUT_DIR):
        directory.mkdir(parents=True, exist_ok=True)


def list_pngs(directory: Path) -> list[Path]:
    return sorted(
        path
        for path in directory.iterdir()
        if path.is_file() and path.suffix.lower() == ".png"
    )


def load_base(path: Path) -> Image.Image:
    if not path.is_file():
        raise RuntimeError(f"Missing required base: {path.name}")
    return Image.open(path).convert("RGBA")


def resize_base_to_output(base: Image.Image) -> Image.Image:
    # Resize the base itself before any compositing. This keeps the whole
    # pipeline in one coordinate system and prevents the finished frame from
    # being resampled/cropped after composition.
    if base.size == OUTPUT_SIZE:
        return base.copy()
    return base.resize(OUTPUT_SIZE, Image.Resampling.LANCZOS)


def load_bases() -> tuple[Image.Image, Image.Image]:
    stats_base = load_base(BASES_DIR / STATS_BASE_NAME)
    clear_base = load_base(BASES_DIR / CLEAR_BASE_NAME)

    if stats_base.size != clear_base.size:
        raise RuntimeError(
            "The two card bases must have exactly the same source dimensions. "
            f"Stats={stats_base.size}, Clear={clear_base.size}"
        )

    return (
        resize_base_to_output(stats_base),
        resize_base_to_output(clear_base),
    )


def detect_stats_top_y(
    stats_base: Image.Image,
    clear_base: Image.Image,
) -> int:
    """
    Detect the top of the stat-slot row by comparing Card_Base.png with
    Card_Base_Clear.png.

    We require a meaningful number of changed pixels in the same row, so tiny
    paper/noise differences do not move the fade boundary.
    """
    stats_rgb = stats_base.convert("RGB")
    clear_rgb = clear_base.convert("RGB")
    diff = ImageChops.difference(stats_rgb, clear_rgb)

    r, g, b = diff.split()
    strongest = ImageChops.lighter(ImageChops.lighter(r, g), b)
    changed = strongest.point(
        lambda value: 255 if value >= STATS_DIFF_THRESHOLD else 0
    )

    width, height = changed.size
    min_changed = max(8, int(width * STATS_MIN_CHANGED_ROW_RATIO))
    pixels = changed.load()

    # UI slots are expected in the middle/lower part of the card. Ignoring the
    # extreme top/bottom also protects against unrelated base-edge differences.
    search_start = int(height * 0.25)
    search_end = int(height * 0.85)

    for y in range(search_start, search_end):
        count = 0
        for x in range(width):
            if pixels[x, y]:
                count += 1
                if count >= min_changed:
                    return y

    raise RuntimeError(
        "Could not detect the stat-slot row by comparing Card_Base.png and "
        "Card_Base_Clear.png. Make sure the two bases are identical except "
        "for the stat UI."
    )


def make_edge_mask(
    size: tuple[int, int],
    bottom_fade_start_y: int | None = None,
    bottom_fade_end_y: int | None = None,
) -> Image.Image:
    width, height = size

    # Feather only left/right/top. The interior intentionally extends below
    # the canvas so the Full version remains fully opaque at the bottom.
    radius = max(SIDE_FEATHER_PX, TOP_FEATHER_PX)
    edge_mask = Image.new("L", size, 0)
    interior = Image.new(
        "L",
        (
            max(1, width - SIDE_FEATHER_PX * 2),
            height + radius * 2,
        ),
        255,
    )
    edge_mask.paste(interior, (SIDE_FEATHER_PX, TOP_FEATHER_PX))
    edge_mask = edge_mask.filter(ImageFilter.GaussianBlur(radius=radius / 2))

    # Full cards have no bottom fade.
    if bottom_fade_start_y is None and bottom_fade_end_y is None:
        return edge_mask

    if bottom_fade_start_y is None or bottom_fade_end_y is None:
        raise RuntimeError(
            "Both bottom_fade_start_y and bottom_fade_end_y must be set together."
        )

    fade_start = max(0, min(height, bottom_fade_start_y))
    fade_end = max(0, min(height, bottom_fade_end_y))
    if fade_end <= fade_start:
        raise RuntimeError("bottom fade end must be greater than start.")

    vertical = Image.new("L", (1, height), 255)
    values: list[int] = []

    for y in range(height):
        if y <= fade_start:
            value = 255
        elif y >= fade_end:
            value = 0
        else:
            t = (y - fade_start) / (fade_end - fade_start)
            # Smoothstep: continuous, soft alpha falloff.
            smooth = t * t * (3.0 - 2.0 * t)
            value = round(255 * (1.0 - smooth))
        values.append(value)

    vertical.putdata(values)
    vertical = vertical.resize(size, Image.Resampling.BILINEAR)

    return ImageChops.multiply(edge_mask, vertical)


def fit_art(art_path: Path) -> Image.Image:
    art = Image.open(art_path).convert("RGBA")

    # Fit directly into the final 768x1120 canvas. No resizing is performed
    # after the card has been composed.
    return ImageOps.fit(
        art,
        OUTPUT_SIZE,
        method=Image.Resampling.LANCZOS,
        centering=(ART_CENTER_X, ART_CENTER_Y),
    )


def apply_mask(art: Image.Image, mask: Image.Image) -> Image.Image:
    prepared = art.copy()
    prepared.putalpha(
        ImageChops.multiply(prepared.getchannel("A"), mask)
    )
    return prepared


def apply_base_alpha(result: Image.Image, base: Image.Image) -> Image.Image:
    result = result.copy()
    result.putalpha(
        ImageChops.multiply(
            result.getchannel("A"),
            base.getchannel("A"),
        )
    )
    return result


def compose_on_base(art: Image.Image, base: Image.Image) -> Image.Image:
    result = Image.alpha_composite(base.copy(), art)
    return apply_base_alpha(result, base)


def compose_one(
    art_path: Path,
    stats_base: Image.Image,
    clear_base: Image.Image,
    stats_top_y: int,
) -> tuple[Path, Path]:
    fitted_art = fit_art(art_path)

    # Stats version: fade reaches alpha=0 exactly where the slot row begins.
    stats_fade_start_y = max(0, stats_top_y - STATS_FADE_HEIGHT_PX)
    stats_mask = make_edge_mask(
        OUTPUT_SIZE,
        stats_fade_start_y,
        stats_top_y,
    )
    stats_art = apply_mask(fitted_art, stats_mask)

    # Full version: no bottom fade at all. Art continues to the bottom edge;
    # the final silhouette is controlled only by Card_Base_Clear.png alpha.
    full_mask = make_edge_mask(OUTPUT_SIZE)
    full_art = apply_mask(fitted_art, full_mask)

    stats_result = compose_on_base(stats_art, stats_base)
    full_result = compose_on_base(full_art, clear_base)

    stats_out = OUTPUT_DIR / f"{art_path.stem}.png"
    full_out = OUTPUT_DIR / f"{art_path.stem}_Full.png"

    stats_result.save(stats_out, "PNG")
    full_result.save(full_out, "PNG")

    return stats_out, full_out


def main() -> int:
    ensure_directories()

    try:
        stats_base, clear_base = load_bases()
        stats_top_y = detect_stats_top_y(stats_base, clear_base)
    except RuntimeError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        print(
            f"\nRequired files in {BASES_DIR}:\n"
            f"  {STATS_BASE_NAME}\n"
            f"  {CLEAR_BASE_NAME}",
            file=sys.stderr,
        )
        return 1

    art_paths = list_pngs(INPUT_DIR)
    if not art_paths:
        print(f"No PNG files found in: {INPUT_DIR}")
        print("Add generated unit images there and run the script again.")
        return 0

    print(f"Stats base : {STATS_BASE_NAME}")
    print(f"Clear base : {CLEAR_BASE_NAME}")
    print(f"Output size: {OUTPUT_SIZE[0]}x{OUTPUT_SIZE[1]}")
    print(f"Stats top  : y={stats_top_y}px")
    print(f"Stats fade : y={max(0, stats_top_y - STATS_FADE_HEIGHT_PX)}..{stats_top_y}px")
    print(f"Units      : {len(art_paths)}")
    print()

    for art_path in art_paths:
        stats_out, full_out = compose_one(
            art_path,
            stats_base,
            clear_base,
            stats_top_y,
        )
        print(f"[OK] {art_path.name}")
        print(f"     -> {stats_out.name}")
        print(f"     -> {full_out.name}")

    print(f"\nDone. Results are in:\n  {OUTPUT_DIR}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
