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

# Feather only the transferred artwork on left/right/top edges.
SIDE_FEATHER_PX = 52
TOP_FEATHER_PX = 36

# Stats-card only: keep the artwork visible lower than before and let it
# softly overlap the stat area before fading to zero.
STATS_BOTTOM_FADE_START = 0.58
STATS_BOTTOM_FADE_END = 0.82


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


def make_edge_mask(
    size: tuple[int, int],
    bottom_fade_start: float | None = None,
    bottom_fade_end: float | None = None,
) -> Image.Image:
    width, height = size

    # Side/top feathering is shared by both card variants.
    # The interior is extended below the canvas so the lower edge is not
    # accidentally feathered here.
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

    # Full cards intentionally have no bottom fade: artwork reaches the bottom
    # of the card and is clipped only by the base alpha.
    if bottom_fade_start is None and bottom_fade_end is None:
        return edge_mask

    if bottom_fade_start is None or bottom_fade_end is None:
        raise RuntimeError(
            "Both bottom_fade_start and bottom_fade_end must be set together."
        )

    fade_start = int(height * bottom_fade_start)
    fade_end = int(height * bottom_fade_end)
    if fade_end <= fade_start:
        raise RuntimeError("bottom_fade_end must be greater than bottom_fade_start.")

    vertical = Image.new("L", (1, height), 255)
    values: list[int] = []

    for y in range(height):
        if y <= fade_start:
            value = 255
        elif y >= fade_end:
            value = 0
        else:
            t = (y - fade_start) / (fade_end - fade_start)
            # Smoothstep gives a gradual alpha falloff without a visible band.
            smooth = t * t * (3.0 - 2.0 * t)
            value = round(255 * (1.0 - smooth))
        values.append(value)

    vertical.putdata(values)
    vertical = vertical.resize(size, Image.Resampling.BILINEAR)

    return ImageChops.multiply(edge_mask, vertical)


def fit_art(art_path: Path, card_size: tuple[int, int]) -> Image.Image:
    art = Image.open(art_path).convert("RGBA")

    return ImageOps.fit(
        art,
        card_size,
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
    """
    Keep the base transparency as the final card silhouette.

    Transparent/rounded corners from the base remain transparent even when
    the source artwork itself is fully opaque.
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
    # Fit/crop only once so both variants have mathematically identical
    # scale, position and brightness.
    fitted_art = fit_art(art_path, stats_base.size)

    # Stats variant: long, smooth fade that continues into the stat area.
    stats_mask = make_edge_mask(
        stats_base.size,
        STATS_BOTTOM_FADE_START,
        STATS_BOTTOM_FADE_END,
    )
    stats_art = apply_mask(fitted_art, stats_mask)

    # Full variant: no lower fade. Artwork continues to the bottom of the
    # clear base; only side/top feathering and base alpha are applied.
    full_mask = make_edge_mask(clear_base.size)
    full_art = apply_mask(fitted_art, full_mask)

    stats_result = compose_on_base(stats_art, stats_base)
    full_result = compose_on_base(full_art, clear_base)

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
