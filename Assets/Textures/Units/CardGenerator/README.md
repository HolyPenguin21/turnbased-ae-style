# CardGenerator

Small deterministic compositor for unit cards.

## Folder layout

```text
CardGenerator/
  Bases/   # exactly 2 card bases
  Input/   # generated unit PNG files
  Output/  # generated card variants
```

The script expects exactly two PNG files in `Bases/`.
One of them must contain `Clear` in the filename, for example:

```text
Card_Base_01.png
Card_Base_01_Clear.png
```

For every PNG placed in `Input/`, the script generates two files in `Output/`:

```text
<UnitName>_Stats.png
<UnitName>_Clear.png
```

The same prepared art layer is used for both variants, so unit scale, position,
brightness and fade remain consistent. The card bases themselves are never
resized or regenerated.

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

- normal 100% alpha compositing, no Multiply/Overlay blending;
- identical unit placement on both bases;
- soft feathering on left/right/top edges;
- deterministic vertical fade before the stat/description zone;
- stat-slot positions come only from the original base PNG, so they cannot drift.

The shared composition constants are at the top of `compose_cards.py` and can
be tuned once for the whole card set.
