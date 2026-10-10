# Planetary Campaign — wasteland atlas presentation

## Scope

The campaign screen now uses the accepted second visual direction: a softly lit,
wide wasteland surface with continuous terrain, translucent ownership colors,
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

Markers and selected labels use an interior point for concave regions. A bounded
projection using tanh (horizontal 1.65, vertical 1.45) broadens the disk into a
rounded rectangle while retaining all regions inside the viewport. Regions are
triangulated after projection; screen triangles subdivide linearly alongside
map-space UV triangles. Fill, contour and raycast therefore share exactly the
same displayed polygon. The 20-unit viewport inset and 48-unit edge fade remain;
lighting darkens the lower rim and a 12-unit shadow adds depth. Surface bounds
follow RectTransform size changes. Borders and markers carry shared map UVs so
they fade with the terrain rather than leaving a hard outline.

`VisibleCenter` chooses a point inside both the saved region and its displayed
polygon, away from the frame where possible. Marker, selected name, terrain
preview and operation arrows use that same point. Projection does not modify
saved vertices or adjacency. Decorative frames/images/text
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

Faction turns, legal attacks, battle resolution,
deck validation, collection rewards, saving and recovery logic are unchanged.
No AI resource/bank reservation or tactical map code is touched.

## Validation performed outside Unity

- Targeted compilation of the four changed campaign UI classes, the actual shared
  UI helper, campaign model/geometry/rules/generator, and the new visual EditMode
  tests against Unity reference DLLs and explicit dependency/TMPro stubs: baseline
  and changed sources both compiled with zero errors.
- Managed geometry check using current generator/geometry/rules and extracted
  presentation math: 35 generated maps, 1260 regions; interior/visible markers, UV bounds,
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

### Reference palette, contours and density

New campaigns now default to 36 regions, balanced at 12 per faction. The three
ownership tints are muted blue-gray, terracotta and olive. Terrain retains 55%
of its original color rather than being dominated by a 70% faction wash. Normal
borders are thin dark lines; selection uses a thin warm amber contour and a
stronger warm fill. Markers use darkened versions of the same ownership palette.

New region borders use one cached sampled curve per shared Voronoi edge. Both
neighbors reuse exactly identical points in reverse order. Samples are spaced
according to edge length, up to six segments; very short edges stay unsplit to
avoid near-collinear triangles. The warp has smooth low-frequency displacement
with a smaller secondary wave. Existing minimum area/width, shared-vertex,
intersection, adjacency and connected ownership checks remain active.

Saved polygons are loaded directly and are not regenerated: previous 24-region
campaigns retain their region count and boundaries. Only a newly created campaign
uses the 36-region default and revised contours.

The composite uses actual seed-7 generated geometry, production `VisibleCenter`
output, committed textures and original game sprites/font. The square source
frame is assembled as nine slices: its 128-pixel corner patches display at 32
canvas units; only the rails stretch. The resulting 1400×780 map frame is
rectangular. The CPU rendering approximates nonlinear UV interpolation and
native shader/TMP antialiasing; it is not a Unity screenshot.

Validation for the palette/contour/density change: targeted reference-DLL UI and
visual-test compilation passed. A managed check generated 1000 default planets
(36000 regions) and validated shared curved boundaries, balanced connected
ownership, projected triangulation area, visible interior markers and mesh
vertex limits. Custom counts 3/7/24/31/36 and seed reproducibility passed. A map
created by the previous generator retained its original 24 regions and exact
geometry through a managed JSON roundtrip and current geometry validation.
These checks do not execute native Unity JsonUtility or the full EditMode suite.
