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

# Feather only the transferred artwork on left/right/top edges.
SIDE_FEATHER_PX = 38
TOP_FEATHER_PX = 27

# Keep the original base border above the artwork so the art can never
# visually cover/eat the frame. The lower decorative edge is substantially
# taller than the top/sides, so it needs a thicker protective overlay.
BORDER_OVERLAY_TOP_PX = 12
BORDER_OVERLAY_SIDE_PX = 12
BORDER_OVERLAY_BOTTOM_PX = 42

# Fixed stats fade in final 768x1120 coordinates.
# Artwork is fully transparent from the top edge of the stat slots downward.
STATS_FADE_START_Y = 510
STATS_FADE_END_Y = 690

# Full-card fade: keep most of the illustration intact, then let it dissolve
# into the paper before reaching the bottom frame. This avoids a hard cutoff
# and prevents the character/background from visually running too far down.
FULL_FADE_START_Y = 900
FULL_FADE_END_Y = 1040


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


def fit_base_to_output(base: Image.Image) -> Image.Image:
    """
    Preserve the whole card base when fitting it to 768x1120.

    ImageOps.contain() prevents any edge cropping. The resized base is centered
    on a transparent 768x1120 canvas so the decorative border, including the
    bottom edge, is never cut off.
    """
    if base.size == OUTPUT_SIZE:
        return base.copy()

    contained = ImageOps.contain(
        base,
        OUTPUT_SIZE,
        method=Image.Resampling.LANCZOS,
    )

    canvas = Image.new("RGBA", OUTPUT_SIZE, (0, 0, 0, 0))
    x = (OUTPUT_SIZE[0] - contained.width) // 2
    y = (OUTPUT_SIZE[1] - contained.height) // 2
    canvas.paste(contained, (x, y), contained)
    return canvas


def load_bases() -> tuple[Image.Image, Image.Image]:
    stats_base = load_base(BASES_DIR / STATS_BASE_NAME)
    clear_base = load_base(BASES_DIR / CLEAR_BASE_NAME)

    if stats_base.size != clear_base.size:
        raise RuntimeError(
            "The two card bases must have exactly the same source dimensions. "
            f"Stats={stats_base.size}, Clear={clear_base.size}"
        )

    return (
        fit_base_to_output(stats_base),
        fit_base_to_output(clear_base),
    )


def make_edge_mask(
    size: tuple[int, int],
    bottom_fade_start_y: int | None = None,
    bottom_fade_end_y: int | None = None,
) -> Image.Image:
    width, height = size

    # Feather only left/right/top. Extend the white interior below the canvas
    # so Full output remains opaque all the way to the bottom.
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
            # Smoothstep gives a gradual alpha falloff without a visible band.
            smooth = t * t * (3.0 - 2.0 * t)
            value = round(255 * (1.0 - smooth))
        values.append(value)

    vertical.putdata(values)
    vertical = vertical.resize(size, Image.Resampling.BILINEAR)

    return ImageChops.multiply(edge_mask, vertical)


def fit_art(art_path: Path) -> Image.Image:
    art = Image.open(art_path).convert("RGBA")

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


def make_border_overlay(base: Image.Image) -> Image.Image:
    """
    Extract a protective decorative frame overlay from the base.

    The bottom overlay is intentionally thicker than the top/sides because
    the lower base decoration extends farther into the card. Re-applying that
    full decorative band above the artwork prevents the bottom edge from
    looking visually eaten.
    """
    width, height = base.size
    mask = Image.new("L", (width, height), 0)

    top = max(1, min(BORDER_OVERLAY_TOP_PX, height // 2))
    side = max(1, min(BORDER_OVERLAY_SIDE_PX, width // 2))
    bottom = max(1, min(BORDER_OVERLAY_BOTTOM_PX, height // 2))

    mask.paste(255, (0, 0, width, top))
    mask.paste(255, (0, height - bottom, width, height))
    mask.paste(255, (0, 0, side, height))
    mask.paste(255, (width - side, 0, width, height))

    overlay = base.copy()
    overlay.putalpha(
        ImageChops.multiply(
            overlay.getchannel("A"),
            mask,
        )
    )
    return overlay


def apply_base_alpha(result: Image.Image, base: Image.Image) -> Image.Image:
    result = result.copy()
    result.putalpha(
        ImageChops.multiply(
            result.getchannel("A"),
            base.getchannel("A"),
        )
    )
    return result


def compose_on_base(
    art: Image.Image,
    base: Image.Image,
    border_overlay: Image.Image,
) -> Image.Image:
    result = Image.alpha_composite(base.copy(), art)
    result = Image.alpha_composite(result, border_overlay)
    return apply_base_alpha(result, base)


def compose_one(
    art_path: Path,
    stats_base: Image.Image,
    clear_base: Image.Image,
) -> tuple[Path, Path]:
    fitted_art = fit_art(art_path)

    # Stats version: fade is deterministic. Artwork reaches alpha=0 exactly
    # at y=STATS_FADE_END_Y and stays fully transparent below that point.
    stats_mask = make_edge_mask(
        OUTPUT_SIZE,
        STATS_FADE_START_Y,
        STATS_FADE_END_Y,
    )
    stats_art = apply_mask(fitted_art, stats_mask)

    # Full version: use a late, gentle bottom fade so the illustration blends
    # into the clean paper area before the decorative lower frame.
    full_mask = make_edge_mask(
        OUTPUT_SIZE,
        FULL_FADE_START_Y,
        FULL_FADE_END_Y,
    )
    full_art = apply_mask(fitted_art, full_mask)

    stats_border = make_border_overlay(stats_base)
    full_border = make_border_overlay(clear_base)

    stats_result = compose_on_base(stats_art, stats_base, stats_border)
    full_result = compose_on_base(full_art, clear_base, full_border)

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
    print(f"Output size: {OUTPUT_SIZE[0]}x{OUTPUT_SIZE[1]}")
    print(f"Stats fade : y={STATS_FADE_START_Y}..{STATS_FADE_END_Y}px")
    print(f"Full fade  : y={FULL_FADE_START_Y}..{FULL_FADE_END_Y}px")
    print(
        f"Border ovl : top={BORDER_OVERLAY_TOP_PX}px, "
        f"side={BORDER_OVERLAY_SIDE_PX}px, "
        f"bottom={BORDER_OVERLAY_BOTTOM_PX}px"
    )
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
