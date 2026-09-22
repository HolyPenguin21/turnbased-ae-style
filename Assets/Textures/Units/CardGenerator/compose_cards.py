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

# Shared composition settings.
ART_CENTER_X = 0.50
ART_CENTER_Y = 0.48

# Feather only the transferred artwork.
SIDE_FEATHER_PX = 52
TOP_FEATHER_PX = 36

# Vertical fade of the artwork, as fractions of card height.
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


def load_base(path: Path) -> Image.Image:
    if not path.is_file():
        raise RuntimeError(f"Missing required base: {path.name}")
    return Image.open(path).convert("RGBA")


def load_bases() -> tuple[Image.Image, Image.Image]:
    stats_base = load_base(BASES_DIR / STATS_BASE_NAME)
    clear_base = load_base(BASES_DIR / CLEAR_BASE_NAME)

    if stats_base.size != clear_base.size:
        raise RuntimeError(
            "The two card bases must have exactly the same pixel dimensions. "
            f"Stats={stats_base.size}, Clear={clear_base.size}"
        )

    return stats_base, clear_base


def make_edge_mask(size: tuple[int, int]) -> Image.Image:
    width, height = size

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

    vertical = Image.new("L", (1, height), 255)
    values: list[int] = []
    for y in range(height):
        if y <= fade_start:
            value = 255
        elif y >= fade_end:
            value = 0
        else:
            t = (y - fade_start) / (fade_end - fade_start)
            smooth = t * t * (3.0 - 2.0 * t)
            value = round(255 * (1.0 - smooth))
        values.append(value)

    vertical.putdata(values)
    vertical = vertical.resize(size, Image.Resampling.BILINEAR)

    return ImageChops.multiply(edge_mask, vertical)


def prepare_art(art_path: Path, card_size: tuple[int, int]) -> Image.Image:
    art = Image.open(art_path).convert("RGBA")

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


def apply_base_alpha(result: Image.Image, base: Image.Image) -> Image.Image:
    """
    Keep the base transparency as the final card silhouette.

    This means transparent/rounded corners from the base remain transparent
    even when the source artwork is fully opaque.
    """
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
) -> tuple[Path, Path]:
    art = prepare_art(art_path, stats_base.size)

    # The exact same prepared art layer is used for both variants.
    stats_result = compose_on_base(art, stats_base)
    full_result = compose_on_base(art, clear_base)

    # Input Foo.png -> Output Foo.png + Foo_Full.png
    stats_out = OUTPUT_DIR / f"{art_path.stem}.png"
    full_out = OUTPUT_DIR / f"{art_path.stem}_Full.png"

    stats_result.save(stats_out, "PNG")
    full_result.save(full_out, "PNG")

    return stats_out, full_out


def main() -> int:
    ensure_directories()

    try:
        stats_base, clear_base = load_bases()
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
    print(f"Units      : {len(art_paths)}")
    print()

    for art_path in art_paths:
        stats_out, full_out = compose_one(
            art_path,
            stats_base,
            clear_base,
        )
        print(f"[OK] {art_path.name}")
        print(f"     -> {stats_out.name}")
        print(f"     -> {full_out.name}")

    print(f"\nDone. Results are in:\n  {OUTPUT_DIR}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
