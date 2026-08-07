# Card art pipeline (Iron Concord)

How the existing card art in `Units/IronConcord/GameCards/` was made, and how to make more.
Two separate steps — generate the raw illustration, then composite it onto the reusable frame.
Do not skip straight to a "finished-looking" generation; the frame/vignette are added after.

## Step 1 — generate the raw illustration

Local ComfyUI instance, HTTP API, no UI needed:

- Endpoint: `http://127.0.0.1:8188/prompt`
- Checkpoint: `sd_xl_base_1.0.safetensors`
- Output lands in ComfyUI's own `output/` folder — copy it into
  `Assets/Textures/Units/IronConcord/` (flat, no frame yet) before doing anything else. ComfyUI's
  output folder has been observed to get cleared/overwritten between runs — don't leave anything
  there you haven't already copied out.

Workflow (API format, POST as JSON body):

```json
{
  "prompt": {
    "1": { "class_type": "CheckpointLoaderSimple", "inputs": { "ckpt_name": "sd_xl_base_1.0.safetensors" } },
    "2": { "class_type": "CLIPTextEncode", "inputs": { "text": "<POSITIVE PROMPT>", "clip": ["1", 1] } },
    "3": { "class_type": "CLIPTextEncode", "inputs": { "text": "<NEGATIVE PROMPT>", "clip": ["1", 1] } },
    "4": { "class_type": "EmptyLatentImage", "inputs": { "width": 832, "height": 1216, "batch_size": 1 } },
    "5": { "class_type": "KSampler", "inputs": { "model": ["1", 0], "positive": ["2", 0], "negative": ["3", 0], "latent_image": ["4", 0], "seed": <RANDOM>, "steps": 30, "cfg": 6.5, "sampler_name": "euler", "scheduler": "karras", "denoise": 1.0 } },
    "6": { "class_type": "VAEDecode", "inputs": { "samples": ["5", 0], "vae": ["1", 2] } },
    "7": { "class_type": "SaveImage", "inputs": { "images": ["6", 0], "filename_prefix": "<NAME>" } }
  },
  "client_id": "claude-code-session"
}
```

**Negative prompt** (reused as-is for every card so far):
```
text, watermark, blurry, low quality, cartoon, anime, 3d render, photograph, medieval castle, fantasy, moat, multiple separate buildings, city skyline, aerial view, top down view, map, person, human figure, crowd
```

**Positive prompt style** — every card ends with this same suffix, only the subject
description at the front changes:
```
..., hand-drawn pencil sketch outline, clean contour lines, light hatching shading, realistic muted warm color, architecture card illustration, dramatic lighting, detailed
```
For a single building/structure (not a unit/hero), lead with `a single <structure>, sci-fi
military-industrial structure, ground level view, <2-4 concrete details>, <background matching
the hex terrain it belongs to>`. Ground-level view, not aerial/top-down — the one time a citadel
prompt asked for a broader shot it came back as a city map instead of one building (see negative
prompt's `aerial view, top down view, multiple separate buildings, city skyline`).

### Pending: 4 resource-extraction facility cards

Not generated yet — prompts ready, `filename_prefix` values below, one per resource type,
background matched to the terrain that resource comes from:

**IC_Facility_HumanExtractor** (City ruins):
```
a single squat fortified labor extraction tower, sci-fi military-industrial structure, ground level view, cage-lift derrick, chained scaffolding platforms, watchtower with searchlight, reinforced metal plating, salvaged rebar and concrete rubble around the base, wasteland ruins background, hand-drawn pencil sketch outline, clean contour lines, light hatching shading, realistic muted warm color, architecture card illustration, dramatic lighting, detailed
```

**IC_Facility_EnergyExtractor** (Sand dunes):
```
a single tall solar energy collector tower, sci-fi military-industrial structure, ground level view, angled heliostat mirror array, thick armored cable conduits running into the sand, glowing capacitor housing at the base, reinforced metal frame, sand dunes wasteland background, hand-drawn pencil sketch outline, clean contour lines, light hatching shading, realistic muted warm color, architecture card illustration, dramatic lighting, detailed
```

**IC_Facility_MaterialsExtractor** (Mountains):
```
a single ore mining derrick built into a rocky slope, sci-fi military-industrial structure, ground level view, conveyor belt descending into a quarry pit, drilling rig with exposed gears, stacked mineral crates, scaffolding anchored to the cliff face, mountain wasteland background, hand-drawn pencil sketch outline, clean contour lines, light hatching shading, realistic muted warm color, architecture card illustration, dramatic lighting, detailed
```

**IC_Facility_TechExtractor** (Rock desert):
```
a single salvage scavenger tower with a satellite dish array, sci-fi military-industrial structure, ground level view, scrapper crane arm pulling circuit boards from buried wreckage, tangled cable bundles, stacked server rack husks, rock desert ruins background, hand-drawn pencil sketch outline, clean contour lines, light hatching shading, realistic muted warm color, architecture card illustration, dramatic lighting, detailed
```

## Step 2 — composite onto the card frame

Frame template: `Assets/Textures/General/Card_Base.png` (832×1216, torn/dripping black ink
border baked in, square corners — corner rounding is applied by the script below, not the
frame file itself). Never generate the border/frame as part of Step 1 — always this same
reusable template.

PowerShell (`Add-Type -AssemblyName System.Drawing`), per image:

1. **Round the frame's corners** — 46px radius alpha-cutout on all 4 corners of `Card_Base.png`,
   fresh copy each run (don't mutate the template file itself).
2. **Feather the raw illustration's edges** — alpha ramp on the source PNG:
   - Left/right: outer 15% of width fades from 0 to full alpha
   - Top: outer 15% of height fades from 0 to full alpha
   - Bottom: from 50% height, alpha ramps from full to 0, reaching 0 by **72%** height (not the
     bottom edge) — stays fully transparent for the remaining ~28%. A ramp that only hits 0 at the
     very last pixel row leaves faint art visible almost to the border, crowding out room for the
     card's description text; cutting it off by 72% leaves a clean blank lower section instead.
3. **Composite** — new 832×1216 ARGB canvas, draw the rounded frame full-size at (0,0), then
   draw the feathered art scaled to fit width into the window `left=70, right=760, top=80`
   (690px wide, height scaled proportionally, `HighQualityBicubic` interpolation).
4. Save as PNG into `Assets/Textures/Units/IronConcord/GameCards/`, named `IC_Card_<Type>_<Name>_01.png`.

## Step 3 — fix Unity import settings

New PNGs land with default (non-sprite) import settings — fix via the `.meta` file:
```
spriteMode: 0 → 1        (Single)
textureType: 0 → 8       (Sprite)
alphaIsTransparency: 0 → 1
nPOTScale: 1 → 0          (None)
```
If Unity's Editor isn't live-watching the project (no auto-generated `.meta` appears), guid must
be hand-written into a fresh `.meta` file — never invent one; only use a guid Unity itself
assigned (check the file after Unity has had a chance to import it) or generate a fresh random
32-hex-char one yourself and use it *consistently* everywhere that file is referenced.

## Step 4 — wire into the game

- Cards that live in the deck/hand (units, heroes, Base cards): guid goes into
  `Assets/Cards/IronConcord/CardCatalog_IronConcord.asset`, the matching card's `art:` field.
- The 4 extraction-facility cards do **not** go in the catalog (`CardDefinition` is a plain
  embedded record, not its own asset — nothing to reference by guid within a list). Their guid
  goes directly into `Assets/Config/GameConfig.asset` → `extractionFacilityCards[]` → matching
  entry's `art:` field (currently `{fileID: 0}` placeholders, indexed Human/Energy/Materials/Tech
  to match `Game.Economy.ResourceType`'s declaration order).
