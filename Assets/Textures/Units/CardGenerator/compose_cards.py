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

# Shared composition settings.
# Keep them fixed for the whole card set to guarantee consistent placement,
# brightness and fade behavior across all unit cards.
ART_CENTER_X = 0.50
ART_CENTER_Y = 0.48

# Feather only the transferred unit artwork. The card bases are never resized,
# repainted or regenerated.
SIDE_FEATHER_PX = 52
TOP_FEATHER_PX = 36

# Vertical fade of the unit art, as fractions of card height.
# The art is fully visible above START, then smoothly reaches zero at END.
# With the current bases this clears the stat row and leaves room for text.
BOTTOM_FADE_START = 0.50
BOTTOM_FADE_END = 0.62


def ensure_directories() -> None:
    for directory in (BASES_DIR, INPUT_DIR, OUTPUT_DIR):
        directory.mkdir(parents=True, exist_ok=True)


def list_pngs(directory: Path) -> list[Path]:
    return sorted(
        path
        for path in directory.iterdir()
        if path.is_file() and path.suffix.lower() == ".png"
    )


def load_bases() -> tuple[Path, Path, Image.Image, Image.Image]:
    base_paths = list_pngs(BASES_DIR)
    if len(base_paths) != 2:
        raise RuntimeError(
            f"Expected exactly 2 PNG files in '{BASES_DIR}'. "
            f"Found {len(base_paths)}."
        )

    clear_candidates = [
        path for path in base_paths if "clear" in path.stem.lower()
    ]
    if len(clear_candidates) != 1:
        raise RuntimeError(
            "One of the two base PNG files must contain 'Clear' in its filename "
            "(for example: Card_Base_01_Clear.png)."
        )

    clear_path = clear_candidates[0]
    stats_path = next(path for path in base_paths if path != clear_path)

    clear_base = Image.open(clear_path).convert("RGBA")
    stats_base = Image.open(stats_path).convert("RGBA")

    if clear_base.size != stats_base.size:
        raise RuntimeError(
            "The two card bases must have exactly the same pixel dimensions. "
            f"Clear={clear_base.size}, Stats={stats_base.size}"
        )

    return clear_path, stats_path, clear_base, stats_base


def make_edge_mask(size: tuple[int, int]) -> Image.Image:
    width, height = size

    # White interior on black background, extended below the canvas so this
    # mask only feathers left/right/top. Bottom fading is handled separately.
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

    fade_start = int(height * BOTTOM_FADE_START)
    fade_end = int(height * BOTTOM_FADE_END)
    if fade_end <= fade_start:
        raise RuntimeError("BOTTOM_FADE_END must be greater than BOTTOM_FADE_START.")

    # 1-pixel-wide vertical gradient, then expanded across the card.
    vertical = Image.new("L", (1, height), 255)
    values: list[int] = []
    for y in range(height):
        if y <= fade_start:
            value = 255
        elif y >= fade_end:
            value = 0
        else:
            t = (y - fade_start) / (fade_end - fade_start)
            # Smoothstep avoids a visible linear band.
            smooth = t * t * (3.0 - 2.0 * t)
            value = round(255 * (1.0 - smooth))
        values.append(value)

    vertical.putdata(values)
    vertical = vertical.resize(size, Image.Resampling.BILINEAR)

    return ImageChops.multiply(edge_mask, vertical)


def prepare_art(art_path: Path, card_size: tuple[int, int]) -> Image.Image:
    art = Image.open(art_path).convert("RGBA")

    # Crop-to-cover puts every generated unit into the exact same coordinate
    # system even when source dimensions differ slightly.
    fitted = ImageOps.fit(
        art,
        card_size,
        method=Image.Resampling.LANCZOS,
        centering=(ART_CENTER_X, ART_CENTER_Y),
    )

    feather = make_edge_mask(card_size)
    fitted.putalpha(
        ImageChops.multiply(fitted.getchannel("A"), feather)
    )
    return fitted


def compose_one(
    art_path: Path,
    clear_base: Image.Image,
    stats_base: Image.Image,
) -> tuple[Path, Path]:
    art = prepare_art(art_path, clear_base.size)

    # The exact same prepared art layer is composited normally (100% color,
    # no Multiply/Overlay blending) over both immutable bases. This guarantees
    # identical unit scale, position, brightness and fade in both variants.
    clear_result = Image.alpha_composite(clear_base.copy(), art)
    stats_result = Image.alpha_composite(stats_base.copy(), art)

    clear_out = OUTPUT_DIR / f"{art_path.stem}_Clear.png"
    stats_out = OUTPUT_DIR / f"{art_path.stem}_Stats.png"

    clear_result.save(clear_out, "PNG")
    stats_result.save(stats_out, "PNG")
    return clear_out, stats_out


def main() -> int:
    ensure_directories()

    try:
        clear_path, stats_path, clear_base, stats_base = load_bases()
    except RuntimeError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        print(
            f"\nPut exactly two card bases into:\n  {BASES_DIR}\n"
            "One filename must contain 'Clear'.",
            file=sys.stderr,
        )
        return 1

    art_paths = list_pngs(INPUT_DIR)
    if not art_paths:
        print(f"No PNG files found in: {INPUT_DIR}")
        print("Add generated unit images there and run the script again.")
        return 0

    print(f"Clear base : {clear_path.name}")
    print(f"Stats base : {stats_path.name}")
    print(f"Units      : {len(art_paths)}")
    print()

    for art_path in art_paths:
        clear_out, stats_out = compose_one(
            art_path,
            clear_base,
            stats_base,
        )
        print(f"[OK] {art_path.name}")
        print(f"     -> {clear_out.name}")
        print(f"     -> {stats_out.name}")

    print(f"\nDone. Results are in:\n  {OUTPUT_DIR}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
