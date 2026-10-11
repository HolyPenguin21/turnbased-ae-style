# Collection visual style

The collection now uses the approved armory reference: dark worn metal, bronze edges,
parchment headings and input fields. MainMenu already contains the authored changes;
`Tools > UI > Build Collection Window` uses the same assets and layout.

- Canvas reference: **1280×720**. Existing player output defaults: **1920×1080** (scale 1.5).
- Card host: **106×155** Canvas units. Grid cell: 140×194, four columns, spacing 8.
- Ownership/copy limit and per-copy deck cost are centered below each card.
- Save Deck is at the bottom of the deck panel. Existing create/copy/delete/starter,
  add/remove controls, filtering and deck validation remain available.
- Collection and acting battle-unit highlights share a rounded amber contour and soft glow.
  Existing component/shader identities are preserved; the old ragged appearance is replaced.
- All four faction emblems retain their symbols and GUIDs, with bronze/steel materials.
- Thumbnail captions use autosizing/ellipsis and a local mask; complete descriptions remain
  in the details column. This change is local to collection preview instances.

## Assets

Six transparent PNG sprites live in `Assets/Resources/UI/Collection`:
`Panel_Outer`, `Panel_Inset`, `Plaque`, `Button`, `Input`, `Meter`.
Their importers use Full Rect meshes, no mipmaps, clamp sampling and authored sprite borders.
UGUI Images use Sliced rendering and a pixels-per-unit multiplier of 4.
Text remains editable TMP content, rather than being baked into button images.

Artwork was produced using the built-in image generation tool from the approved collection
reference and Panel_PreBattle. The asset prompt requested six empty front-facing metal /
parchment UI sprites with transparent gutters. The emblem prompt requested four faithful
redraws preserving the original tower, skull, energy-crystal and compass motifs and their
orange, blue, ivory and turquoise accents. The resulting sheets were separated into PNGs.

## Validation

Both repository YAML validators passed for MainMenu, GameConfig, both collection prefabs
and BattleGridCell. Layout calculations confirm that four columns and three rows fit inside
the card viewport, the counters are centered, all six PNGs are valid RGBA sprites with valid
slice borders, and all four emblem GUIDs are unchanged. Objects outside CollectionWindow
in MainMenu were compared with the base revision and are unchanged.

Unity Editor 6000.5.4f1 is unavailable in the execution environment. Compilation, shader
compilation, EditMode tests and a Game View screenshot have **not** been run. Final visual
matching, text metrics and input behavior require checking in that Editor at 1920×1080:

1. Open Collection from the main menu and from player setup. Switch every faction.
2. Select a card, scroll both lists, edit/save a deck, and exercise both confirmation dialogs.
3. Inspect long building/equipment captions and their complete details.
4. Open a human battle and check the active-unit amber frame, including its first appearance.
