# Generation switches and smaller footprints

GameConfig > Map Generation > Complexes contains `Use In Generation` for every Arid
template. Desert has the same switches under `Desert Override > Complexes`.
The switch is independent per biome. Disabled templates are removed before the total
is allocated, so they consume neither placements nor attempts. The baseline terrain
pool still excludes complex terrain types, including disabled ones.

`Complex Count` remains the total for the map; zero disables all complexes. The authored
value remains **0**, preserving the current project setting. Each template's `Count`
remains its relative share of that total. All eight template switches start enabled.

| Terrain | Parts | Frames per part | Footprint |
| --- | ---: | ---: | --- |
| Acid lake | 1 | 7 | (0,0) |
| Boiling mud field | 1 | 7 | (0,0) |
| Giant machine wreck | 2 | 1 | (0,0), (1,0) |
| Deep canyon | 3 | 1 | (0,0), (1,0), (1,1) |

The footprints remain fixed: generation translates them without rotation or mirroring.
Single-cell templates pass through the existing placement/connectivity checks, mesh
materials and terrain animation groups. Movement, resource yields and AI terrain data
continue to use the existing per-cell HexMap store.

AcidLake Part1 contains the new closed-shore bubble-only animation for each biome.
All 14 existing texture GUIDs and importer settings are preserved. The delivered
flat-top hex artwork is fitted into the mesh's square UV space (512 wide, 444 high,
centred at y=34); the opaque square PNGs leave hex clipping and fade to the mesh.
The rate is 6.25 FPS (160 ms/frame). Water, soil and shores remain stationary outside
the small bubble areas.

BoilingMud retains the old Part1 artwork provisionally; the second cell is no longer
placed. Wreck retains the old first two parts provisionally; the third cell is no longer
placed. Their new artwork is a separate follow-up. Unused legacy PNGs/metas remain
in the repository, but are not referenced by these templates. No new art was drawn
for either family here.

Offline previews now read parts, offsets, frame references and rate from GameConfig.
The legacy atlas extractor refuses a changed family footprint before writing files,
preventing it from overwriting the imported acid animation with the old two-part lake.

Validation:

```sh
python Tools/terrain-assets/validate.py
python Tools/terrain-assets/validate_lake_shores.py
dotnet run --project Tools/terrain-verify/TerrainVerify.csproj
```

The managed tests include single-cell acceptance, protected cells and narrow bridges,
disabled template rejection, and invalid empty/oversized shapes. Unity-only tests
exercise generator allocation with exactly one enabled template and with all templates
disabled in both biomes; disabled types must also stay out of ordinary random fill.
The shader/mesh display and those generator tests require Unity EditMode/PlayMode.
