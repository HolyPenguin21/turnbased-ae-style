# Army destruction: local-only flipbook atlases

Rustmarch plays **16 frames in 2 seconds**, using separate **base** and **faction-accent** layers for **ground** and **air** army markers. The C# player and the Unity importer metadata are stored in Git, but **the PNG atlases themselves are intentionally excluded**.

## Install four PNG files locally

Copy these files into the Unity project folder `Assets/Resources/ArmyDestruction/`:

| Filename | Purpose |
| --- | --- |
| `GroundBase.png` | Ground army: helmet/medallion destruction |
| `GroundAccent.png` | Ground army: runtime-tinted faction overlay |
| `AirBase.png` | Air army: plane/medallion destruction |
| `AirAccent.png` | Air army: runtime-tinted faction overlay |

Each file: **1280×320 RGBA PNG**, transparent background, **8 columns × 2 rows**, **160×160 px per frame**. Keep exactly these names; `Resources.Load<Texture2D>("ArmyDestruction/<name>")` relies on them. **Do not overwrite or delete the versioned `.png.meta` files** (they preserve native-resolution sprite import settings); the first frame in both layers matches the live army marker's art.

**Before the first `git pull` after these assets become untracked:** back up all four PNGs outside the repo. Git removes old tracked files as it applies the deletion; `.gitignore` only prevents future re-adding. Copy the four PNGs back *after* updating the repository. They will then remain local and ignored.

If you need the originals to restore locally, retrieve the earlier archive from commit `712dbcfce7888153f31ff1bf1aef94f3dfba28a1`:
https://github.com/HolyPenguin21/turnbased-ae-style/raw/712dbcfce7888153f31ff1bf1aef94f3dfba28a1/Docs/Exports/ArmyDestruction_Images.zip

## Development and validation

- The game code does not regenerate textures; it only plays the local PNG flipbooks. There is no procedural ash effect.
- `Tools/army-destruction/generate.py` can recreate the four sheets from the **versioned** original in-game 128px ground/air army sprites. It is an optional authoring tool, **not a runtime dependency**.
- GitHub CI generates temporary art, runs `--check`, and confirms that no PNG sheet is tracked.
- Unity tests that require the four locally installed sheets skip those integration checks when art is absent. Tests for viewer safety and visual capture remain independent of the PNGs.
- Preview GIFs in `Docs/Previews/` are for inspection only, not runtime assets.

**Release note:** Since the PNGs are ignored, clean clones and builds must receive all four locally; without them, the destruction flipbook cannot display correctly.
