# CardGenerator

Deterministic compositor for unit cards.

## Folder layout

```text
CardGenerator/
  Bases/
    Card_Base.png
    Card_Base_Clear.png
  Input/
  Output/
```

- `Card_Base.png` — card base with stat slots.
- `Card_Base_Clear.png` — clear card base without stat slots.
- `Input/` — generated unit PNG files.
- `Output/` — final card PNG files.

For an input file:

```text
LightInfantry.png
```

the script generates:

```text
LightInfantry.png
LightInfantry_Full.png
```

- `LightInfantry.png` uses `Card_Base.png`.
- `LightInfantry_Full.png` uses `Card_Base_Clear.png`.

The exact same prepared art layer is used for both outputs, so scale, position,
brightness and fade stay identical.

The alpha channel of each base is applied to the final result. Transparent and
rounded card corners therefore remain transparent automatically.

## First-time setup

From PowerShell:

```powershell
cd Assets\Textures\Units\CardGenerator
py -m pip install -r .\requirements.txt
```

## Run

```powershell
py .\compose_cards.py
```

## Current behavior

- fixed base filenames;
- normal 100% alpha compositing, no Multiply/Overlay blending;
- identical unit placement and brightness on both card variants;
- soft feathering on left/right/top edges;
- deterministic vertical fade before the stats/description area;
- final card silhouette is clipped by the alpha channel of the selected base;
- stat-slot positions always come directly from `Card_Base.png`, so they cannot drift.

The shared composition constants are at the top of `compose_cards.py` and can
be tuned once for the whole card set.
