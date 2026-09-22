#!/usr/bin/env python3
from __future__ import annotations

import math
import sys
from pathlib import Path

try:
    from PIL import Image, ImageChops, ImageOps
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

# Keep the original base border above the artwork so the art can never
# visually cover/eat the frame. Use the same overlay width on every side.
BORDER_OVERLAY_TOP_PX = 48
BORDER_OVERLAY_SIDE_PX = 48
BORDER_OVERLAY_BOTTOM_PX = 48

# Immediately inside the ragged 48 px base overlay, fade the artwork from
# alpha 0 to full opacity over 48 px on all four sides.
IMAGE_EDGE_FEATHER_PX = 48

# Deterministic irregularity of the inner overlay boundary. The same ragged
# profile is reused by the overlay and artwork mask so there are no gaps or
# mismatched seams between the two layers.
BORDER_RAGGEDNESS_PX = 10

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


def _edge_jitter(index: int, phase_a: float, phase_b: float) -> int:
    """
    Deterministic low-frequency edge variation.

    Combining several sine waves avoids random output between runs while
    keeping the inner frame edge visibly irregular instead of mechanically
    straight.
    """
    value = (
        math.sin(index * 0.043 + phase_a) * 0.55
        + math.sin(index * 0.117 + phase_b) * 0.30
        + math.sin(index * 0.251 + phase_a + phase_b) * 0.15
    )
    return round(value * BORDER_RAGGEDNESS_PX)


def make_ragged_edge_profiles(
    size: tuple[int, int],
) -> tuple[list[int], list[int], list[int], list[int]]:
    """
    Return local overlay thickness for top, bottom, left and right edges.

    Top/bottom are indexed by X. Left/right are indexed by Y.
    """
    width, height = size

    top = [
        max(1, BORDER_OVERLAY_TOP_PX + _edge_jitter(x, 0.0, 1.3))
        for x in range(width)
    ]
    bottom = [
        max(1, BORDER_OVERLAY_BOTTOM_PX + _edge_jitter(x, 2.1, 0.7))
        for x in range(width)
    ]
    left = [
        max(1, BORDER_OVERLAY_SIDE_PX + _edge_jitter(y, 0.9, 2.7))
        for y in range(height)
    ]
    right = [
        max(1, BORDER_OVERLAY_SIDE_PX + _edge_jitter(y, 1.8, 0.4))
        for y in range(height)
    ]

    return top, bottom, left, right


def make_edge_mask(
    size: tuple[int, int],
    bottom_fade_start_y: int | None = None,
    bottom_fade_end_y: int | None = None,
) -> Image.Image:
    """
    Build the artwork alpha mask.

    On every side, the image stays at alpha 0 through the local ragged overlay
    thickness, then fades from 0 to 255 over IMAGE_EDGE_FEATHER_PX.

    The artwork mask and the border overlay use the exact same ragged edge
    profiles. This keeps the layer order consistent everywhere:

        base edge -> ragged overlay -> alpha buffer -> full artwork

    The four side fades are multiplied together, so corners remain soft and
    organic instead of forming hard 90-degree joins.

    An optional additional bottom fade is multiplied on top for Stats/Full
    card-specific composition.
    """
    width, height = size
    top_edge, bottom_edge, left_edge, right_edge = make_ragged_edge_profiles(size)

    def side_alpha(distance: int, overlay_px: int) -> int:
        if distance < overlay_px:
            return 0

        fade_end = overlay_px + IMAGE_EDGE_FEATHER_PX
        if distance >= fade_end:
            return 255

        t = (distance - overlay_px) / IMAGE_EDGE_FEATHER_PX
        smooth = t * t * (3.0 - 2.0 * t)
        return round(255 * smooth)

    edge_mask = Image.new("L", size, 0)
    pixels = edge_mask.load()

    for y in range(height):
        left_overlay = left_edge[y]
        right_overlay = right_edge[y]

        for x in range(width):
            left_alpha = side_alpha(x, left_overlay)
            right_alpha = side_alpha(width - 1 - x, right_overlay)
            top_alpha = side_alpha(y, top_edge[x])
            bottom_alpha = side_alpha(height - 1 - y, bottom_edge[x])

            value = left_alpha
            value = round(value * right_alpha / 255)
            value = round(value * top_alpha / 255)
            value = round(value * bottom_alpha / 255)
            pixels[x, y] = value

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

    bottom_values: list[int] = []
    for y in range(height):
        if y <= fade_start:
            value = 255
        elif y >= fade_end:
            value = 0
        else:
            t = (y - fade_start) / (fade_end - fade_start)
            smooth = t * t * (3.0 - 2.0 * t)
            value = round(255 * (1.0 - smooth))
        bottom_values.append(value)

    bottom_fade = Image.new("L", (1, height), 255)
    bottom_fade.putdata(bottom_values)
    bottom_fade = bottom_fade.resize(size, Image.Resampling.NEAREST)

    return ImageChops.multiply(edge_mask, bottom_fade)


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
    Extract the protective decorative frame overlay from the base.

    The inner boundary is intentionally ragged. It uses the same deterministic
    edge profiles as make_edge_mask(), so the overlay ends exactly where the
    artwork's 48 px alpha buffer begins.
    """
    width, height = base.size
    top_edge, bottom_edge, left_edge, right_edge = make_ragged_edge_profiles(base.size)

    mask = Image.new("L", base.size, 0)
    pixels = mask.load()

    for y in range(height):
        left_overlay = left_edge[y]
        right_overlay = right_edge[y]

        for x in range(width):
            visible = (
                y < top_edge[x]
                or y >= height - bottom_edge[x]
                or x < left_overlay
                or x >= width - right_overlay
            )
            pixels[x, y] = 255 if visible else 0

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
    print(f"Image edge : alpha 0 -> 255 over {IMAGE_EDGE_FEATHER_PX}px")
    print(f"Raggedness : +/-{BORDER_RAGGEDNESS_PX}px")
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
