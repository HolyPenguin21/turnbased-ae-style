# Planetary Campaign — wasteland atlas presentation

## Scope

The campaign screen now uses the accepted second visual direction: a softly lit,
tilted wasteland surface with continuous terrain, translucent ownership colors,
fine borders, faction markers and an amber selected region. Worn steel frames,
parchment information panels and the existing game font/buttons/logos complete
the console. This is a presentation change, not a new campaign architecture.

The authored terrain is artistic background only. Mountains, craters and channels
do not imply gameplay bonuses, resources or extra campaign mechanics.

## Assets and integration

- `Assets/Resources/Campaign/WastelandSurface.jpg`: neutral uninterrupted desert
  art. No region borders, faction colors, labels or baked selection.
- `MetalFrame.png`: actual alpha opening and corners; single sprite, full-rect,
  128-pixel nine-slice borders. The UI uses a pixels-per-unit multiplier of 4.
- `Parchment.jpg`, `ConsoleMetal.jpg`: readable light paper and dark console material.
- `CampaignSurface.shader`: UI stencil/clip-compatible surface rendering.
  TEXCOORD1.x distinguishes textured terrain from solid borders and markers.
- The campaign scene references the existing End Turn/action button sprites and
  Iron Concord/Ashen/Vessels logos by their original GUIDs; they are not duplicated.
- All new assets have committed Unity import metadata and load from Resources in
  player builds. No runtime generation API, additional package or editor asset
  setup is required.

The four bitmap materials were generated from the approved concept, then exported
as JPEG for opaque materials and PNG for the transparent frame. They retain their
1254×1254 native dimensions. Terrain is intentionally a reusable fixed artistic
surface; procedural region geometry and ownership remain campaign data.

## Rendering and input

`CampaignMapView` owns the shared material and one map-to-pixel conversion.
`CampaignRegionGraphic` samples the same map-space texture coordinates across
all cells. Its cached triangle subdivision gives the edge lighting enough interior
vertices instead of shading only polygon corners. Region boundaries remain actual
generated vertices. Raycasting uses the same projected polygons and pixel
conversion as rendering, including the inherited CanvasGroup/mask filters.

Markers and selected labels use an interior point for concave regions. Operation
arrows use the same interior points and pixel space. Decorative frames/images/text
do not intercept region clicks. Owner/selection/hover changes update the existing
meshes; no map regeneration or save mutation occurs. The shared runtime material
is released when the view is destroyed.

`CampaignUI` applies the skin locally without changing the collection UI helpers.
The screen uses a 1920×1080 reference Canvas and retains proportional scaling for
16:9 resolutions. The right column contains owner counts, selected region,
surface thumbnail, owner, scrollable neighbors and the existing attack action.
The shorter footer shows status/history and the existing End Turn action.
Up to four recent battles use two rows and two columns, with ellipsis for long
entries. The baked End Turn text is not duplicated; action buttons use dark ink
on their existing light artwork. Selected map names have a parchment nameplate
for contrast against terrain.
Attack confirmation, deck selection and result/error dialogs use the same skin.
The neighbor list scrolls for regions with many neighbors.

Campaign generation, adjacency, faction turns, legal attacks, battle resolution,
deck validation, collection rewards, saving and recovery logic are unchanged.
No AI resource/bank reservation or tactical map code is touched.

## Validation performed outside Unity

- Targeted compilation of the four changed campaign UI classes, the actual shared
  UI helper, campaign model/geometry/rules/generator, and the new visual EditMode
  tests against Unity reference DLLs and explicit dependency/TMPro stubs: baseline
  and changed sources both compiled with zero errors.
- Managed geometry check using current generator/geometry/rules and extracted
  presentation math: 35 generated maps, 840 regions; interior markers, UV bounds,
  pixel conversion at 1280×720 / 1920×1080 / 2560×1440, unchanged region data, and
  the concave-marker regression passed. This uses the existing verification
  approach of managed vector copies, not the native Unity renderer.
- Both `Tools/unity-yaml-verify` scripts passed for `Assets/Scenes/Campaign.unity`.
- New texture dimensions, frame alpha, clamp wrapping, import sizes, resource
  filenames, unique GUIDs and scene sprite GUID bindings checked.

Unity 6000.5.4f1 is unavailable in the implementation environment. Native shader
compilation, scene screenshots, click/hover rendering and the EditMode test suite
have not been run here. Reference-DLL compilation is not a Unity player build.
The AI/backend suite was not rerun because its production files are unchanged.

## Native acceptance

1. Open the project in Unity 6000.5.4f1; allow the committed textures/shader to
   import. Check the Console for shader/import/script errors.
2. Run `CampaignVisualTests` and the existing EditMode suite.
3. Start campaigns with different seeds; confirm different region shapes over the
   continuous terrain, matching borders/owner markers, and no seams at captures.
4. Test hover and clicks at the center, edges and narrow/concave regions; verify
   gold selection, available enemy neighbors, selected name, and attack button.
5. Check 1280×720, 1920×1080 and 2560×1440. Check long generated names, a region
   with many neighbors, and the four-entry history.
6. Check attack confirmation/cancel, offensive and defensive deck selection,
   interrupted battle resume, AI result/Continue, reward return and finished
   campaigns. Transparent frame centers must remain visible and decorative art
   must not block input.
7. Compare the running scene against the approved reference. Native Unity visual
   parity can only be confirmed with that rendered scene, not the concept image.

## Asset overlay review

`Campaign_Asset_Composite.jpg` is a deterministic CPU composition of the committed
terrain/paper/metal/frame assets, original game sprites and Liberation Sans source
font used by GameMenuFont. Panel coordinates and 128-pixel nine-slice borders
(multiplier 4) match the UI code. It uses actual seed-7 generated polygons and
ownership; round 4, the human turn and four history entries are review fixtures.
It is not a Unity screenshot: shader interpolation, TMP bold/line positioning,
scrollbars and native antialiasing still need verification in the scene.

The overlay review found and fixed duplicated End Turn lettering, insufficient
contrast on the light button artwork and terrain labels, and the four-line
history extending below its intended area. Reference-DLL compilation passed
again after those UI fixes. The scene and all procedural/backend data remain
unchanged by this follow-up.
