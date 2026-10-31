#!/usr/bin/env python3
"""Generate 16-frame destruction sprite sheets from the ACTUAL in-game 128px army icons.

Source layers remain separate so their runtime faction tint survives on frame zero.
All fragmentation, ash, cracks and fade are baked into the PNG sheets: no procedural
destruction particles or per-pixel texture modifications during gameplay.
Run: python -m pip install pillow numpy
     python Tools/army-destruction/generate.py
     python Tools/army-destruction/generate.py --check
"""
from __future__ import annotations
import argparse
import hashlib
import math
import re
from pathlib import Path
import numpy as np
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "Assets/Textures/Game/General/Armies"
OUTPUT = ROOT / "Assets/Resources/ArmyDestruction"
PREVIEW = ROOT / "Docs/Previews"
CELL = 160
COUNT = 16
COLUMNS = 8
ROWS = 2
MARGIN = (CELL - 128) // 2
# The same fracture boundaries are used for the base and accent of each marker.
SEEDS = np.array([(32, 24), (64, 19), (102, 31), (22, 64),
                  (53, 58), (87, 60), (114, 71), (37, 104),
                  (76, 110), (105, 102), (67, 83)], dtype=np.float32)


def smooth(start: float, end: float, x: float) -> float:
    u = min(1.0, max(0.0, (x - start) / (end - start)))
    return u * u * (3.0 - 2.0 * u)


def regions() -> np.ndarray:
    y, x = np.mgrid[:128, :128]
    # Imperfect Voronoi regions form organic, stable fracture edges, not radial pie slices.
    xx = x + 2.2 * np.sin(y * 0.21) + 1.3 * np.sin(x * 0.39 + y * 0.16)
    yy = y + 2.0 * np.sin(x * 0.23) + 1.1 * np.cos(y * 0.37 + x * 0.18)
    dx = xx[None] - SEEDS[:, 0, None, None]
    dy = yy[None] - SEEDS[:, 1, None, None]
    return (dx * dx + dy * dy).argmin(axis=0)


REGIONS = regions()


def erode(mask: np.ndarray) -> np.ndarray:
    interior = mask.copy()
    interior[1:] &= mask[:-1]
    interior[:-1] &= mask[1:]
    interior[:, 1:] &= mask[:, :-1]
    interior[:, :-1] &= mask[:, 1:]
    return interior


def recolor(source: Image.Image, t: float) -> Image.Image:
    px = np.array(source, dtype=np.uint8).copy()
    amount = smooth(0.02, 0.23, t)
    rgb = px[:, :, :3].astype(np.float32)
    luminance = rgb[:, :, 0] * .2126 + rgb[:, :, 1] * .7152 + rgb[:, :, 2] * .0722
    grey = np.clip(luminance * .82 + 18, 0, 255)
    px[:, :, :3] = np.clip(rgb * (1 - amount) + grey[:, :, None] * amount, 0, 255).astype(np.uint8)
    return Image.fromarray(px, "RGBA")


def draw_ash(image: Image.Image, frame: int, air: bool) -> None:
    t = frame / (COUNT - 1)
    strength = smooth(.29, .45, t) * (1 - smooth(.76, 1, t))
    if strength <= 0:
        return
    rng = np.random.default_rng(741 if air else 319)
    draw = ImageDraw.Draw(image, "RGBA")
    for k in range(56):
        angle = float(rng.uniform(0, math.tau))
        distance = float(rng.uniform(20, 55)) * (.45 + 1.0 * t)
        x = CELL / 2 + math.cos(angle) * distance
        y = CELL / 2 + math.sin(angle) * distance + 13 * (t - .4)
        radius = float(rng.uniform(.5, 2.2)) * (1 - .45 * t)
        opacity = int((50 + k % 5 * 12) * strength)
        color = (87 + k % 3 * 18, 82 + k % 3 * 15, 74 + k % 3 * 12, opacity)
        draw.ellipse((x-radius, y-radius, x+radius, y+radius), fill=color)


def generate_frame(source: Image.Image, frame: int, accent: bool, air: bool) -> Image.Image:
    assert source.size == (128, 128), f"Unexpected source size: {source.size}"
    t = frame / (COUNT - 1)
    canvas = Image.new("RGBA", (CELL, CELL))
    # Frame 0 is a lossless copy of the actual in-game sprite, with no redraw or tint.
    if frame == 0:
        canvas.paste(source, (MARGIN, MARGIN))
        return canvas

    colored = recolor(source, t)
    if frame < 3:
        canvas.paste(colored, (MARGIN, MARGIN))
        return canvas

    pixels = np.array(colored)
    original_alpha = pixels[:, :, 3].copy()
    fracturing = smooth(.23, .84, t)

    for idx, (seed_x, seed_y) in enumerate(SEEDS):
        raw_mask = REGIONS == idx
        # Thin transparent seams reveal actual cracks through the metal.
        seam = erode(raw_mask) if frame >= 4 else raw_mask
        chunk = pixels.copy()
        chunk[:, :, 3] = np.where(seam, original_alpha, 0).astype(np.uint8)
        # Different metal fragments crumble at slightly different moments.
        opacity = 1 - smooth(.58 + .018 * (idx % 4), .92 + .014 * (idx % 3), t)
        if frame == COUNT - 1:
            opacity = 0
        chunk[:, :, 3] = (chunk[:, :, 3].astype(np.float32) * opacity).astype(np.uint8)
        if not np.any(chunk[:, :, 3]):
            continue
        part = Image.new("RGBA", (CELL, CELL))
        part.paste(Image.fromarray(chunk, "RGBA"), (MARGIN, MARGIN))
        dx_seed = float(seed_x - 64)
        dy_seed = float(seed_y - 64)
        angle = math.atan2(dy_seed, dx_seed)
        dx = int(round((math.cos(angle) * (11 + 16 * (idx % 3) / 2) +
                         math.sin(idx * 3) * 7) * fracturing))
        dy = int(round((math.sin(angle) * 12 + 14 * fracturing +
                         ((idx % 3) - 1) * 8) * fracturing))
        rotation = (idx % 2 * 2 - 1) * (idx % 4 + 1) * 2.0 * fracturing
        part = part.rotate(rotation, resample=Image.Resampling.BICUBIC,
                           center=(seed_x + MARGIN, seed_y + MARGIN))
        shifted = Image.new("RGBA", (CELL, CELL))
        shifted.paste(part, (dx, dy))
        canvas.alpha_composite(shifted)

    if not accent:
        draw_ash(canvas, frame, air)
    return canvas


def make_sheet(frames: list[Image.Image]) -> Image.Image:
    sheet = Image.new("RGBA", (COLUMNS * CELL, ROWS * CELL))
    for i, image in enumerate(frames):
        sheet.paste(image, ((i % COLUMNS) * CELL, (i // COLUMNS) * CELL))
    return sheet


def write_or_check(path: Path, payload: bytes, check: bool) -> None:
    if check:
        assert path.is_file(), f"Missing generated asset: {path}"
        assert path.read_bytes() == payload, f"Stale generated asset: {path}"
    else:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(payload)


def image_bytes(image: Image.Image) -> bytes:
    from io import BytesIO
    stream = BytesIO()
    image.save(stream, format="PNG", optimize=True)
    return stream.getvalue()


def meta_for(source_name: str, output_name: str) -> bytes:
    text = (SOURCE / (source_name + ".png.meta")).read_text()
    guid = hashlib.md5(("army-destruction-" + output_name).encode()).hexdigest()
    text = re.sub(r"(?m)^guid: [0-9a-f]{32}$", "guid: " + guid, text)
    # Sheets are 1280x320: never downscale them to the marker's 256px import cap.
    text = re.sub(r"(?m)^    maxTextureSize: \d+$", "    maxTextureSize: 2048", text)
    text = re.sub(r"(?m)^    textureCompression: \d+$", "    textureCompression: 0", text)
    return text.encode()


def run(check: bool) -> None:
    manifest = [
        ("GroundBase", "MapObject_Army_Base", False, False),
        ("GroundAccent", "MapObject_Army_FactionAccent", True, False),
        ("AirBase", "MapObject_Army_Air_Base", False, True),
        ("AirAccent", "MapObject_Army_Air_FactionAccent", True, True),
    ]
    results = {}
    for name, filename, accent, air in manifest:
        with Image.open(SOURCE / (filename + ".png")) as source_image:
            src = source_image.convert("RGBA")
        frames = [generate_frame(src, i, accent, air) for i in range(COUNT)]
        assert list(frames[0].crop((MARGIN, MARGIN, MARGIN + 128, MARGIN + 128)).getdata()) == list(src.getdata())
        sheet = make_sheet(frames)
        assert sheet.size == (1280, 320)
        png_path = OUTPUT / (name + ".png")
        write_or_check(png_path, image_bytes(sheet), check)
        write_or_check(Path(str(png_path) + ".meta"), meta_for(filename, name), check)
        results[name] = frames

    folder_meta = ("fileFormatVersion: 2\n"
                   "guid: 82e95306ae3440d3a6ea00121434664d\n"
                   "folderAsset: yes\n"
                   "DefaultImporter:\n"
                   "  externalObjects: {}\n"
                   "  userData:\n"
                   "  assetBundleName:\n"
                   "  assetBundleVariant:\n").encode()
    write_or_check(OUTPUT.parent / "ArmyDestruction.meta", folder_meta, check)

    # Human-review GIFs are not loaded by the game.
    for kind in ("Ground", "Air"):
        frames = []
        for i in range(COUNT):
            composite = Image.new("RGBA", (CELL, CELL), (62, 57, 48, 255))
            composite.alpha_composite(results[kind + "Base"][i])
            composite.alpha_composite(results[kind + "Accent"][i])
            frames.append(composite.convert("RGB").resize((320, 320), Image.Resampling.NEAREST))
        from io import BytesIO
        stream = BytesIO()
        frames[0].save(stream, "GIF", save_all=True, append_images=frames[1:],
                       duration=125, loop=0, optimize=True)
        write_or_check(PREVIEW / ("ArmyDestruction_" + kind + ".gif"),
                       stream.getvalue(), check)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true")
    run(parser.parse_args().check)
