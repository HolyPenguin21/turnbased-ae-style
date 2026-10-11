# Campaign fit, clouds, compact corners and region markers

Local presentation correction against master `c659d1d335de3f601c3b7444baf0f756668c3d1b`. Generation, stored polygons, neighboring regions, campaign saves and battle rules remain unchanged.

## Final behavior

- The UI projects the existing disk-shaped map into a rounded rectangle, occupying approximately 94% of its viewport. The map viewport remains 1352 × 744 inside a 1376 × 768 panel. Insets separate the map rail from the console, header and footer.
- Clouds render above regions and below the map frame. A modest texture crop moves visible mist toward the corners. The existing shader derives the map footprint from the same exponent-8 projection and the actual 12-unit map inset. Within radius 0.99, cloud alpha is multiplied by 0.10 and RGB is 70% desaturated: maximum opacity is 0.078 before texture alpha/edge feather. Across radius 0.99–1.075 it smoothly increases to the full 0.78 layer opacity, covering the corner background. The layer does not receive input; cropped UVs are normalized before masking.
- The edited frame has compact curves, worn graphite/bronze rails and a transparent center. The 192px nine-slice borders contain the full curved corners. Console, map, region preview and dialog masks use a consistent 32-unit radius. The rounded frame GUID is preserved; the original angular MetalFrame remains intact.
- The depth silhouette receives the same soft horizon fade as terrain. Packed surface mode -1 means solid depth color with horizon fading, 0 means opaque contours/markers, and 1 means textured terrain.
- Markers use the area centroid of the displayed polygon, mapped back into saved coordinates through the inverse UI projection. The vertex-average bias and .90/.80 viewport gates are removed. Concave regions retain an interior fallback inside both polygons. Labels, arrows and sidebar sampling use the same calculation; selected labels remain inside the viewport.
- CampaignUI owns and disposes its cloud/panel materials. Preview and dialog materials are reused when content changes.

## Preview

![CPU composition using the screenshot seed 0x907DFFC](Campaign_MapFitAndRoundedFrames.jpg)

This is a CPU composition of production assets and newly generated seed 0x907DFFC polygons, with a selected-region and history fixture. It is not the user's live save or a Unity screenshot. GPU shading, TMP metrics, stencil clipping and input require native verification.

## Validation

- Baseline and changed presentation source/test declarations compile with zero errors against available Unity reference DLLs and explicit verifier stubs.
- The old centering code fails both targeted regressions: an unevenly subdivided centered rectangle yields y=0.085714 instead of 0; an outer rectangle yields displayed y=0.833 rather than 0.880–0.890. The changed code passes both.
- Managed production-source checks pass for 36 maps / 1728 regions, including seed 0x907DFFC: interior markers, three-resolution input projection, terrain aspect, UV bounds, unchanged saved polygons, concave fallback and selection glow.
- Stress checks pass for 1000 planets / 48000 regions: projected triangulation/area, markers in both polygons, mesh budget, custom counts, reproducibility and managed restoration of previous 24-region geometry.
- GPU regression declarations cover Canvas translation, opaque outer markers, soft depth silhouettes, cropped overlay masks and stronger cloud opacity outside the map. GPU cases are not executed here.
- Asset alpha, preserved GUID, corner slice bounds, overlay sibling order, non-intercepting decoration, material cleanup, numerical cloud alpha limits and diff whitespace checks pass.
- Unity 6000.5.4f1 is unavailable. Shader import, native EditMode/GPU tests, nested masks and in-scene acceptance remain pending. Check outer regions/points, preview crops, cloud corners, scrolling, menu/end turn and dialogs at 1920×1080 and other 16:9 resolutions.

## Final frame edit

Mode: built-in image_gen with a transparent background. The returned PNG is copied unchanged to `Assets/Resources/UI/Panels/Campaign/MetalFrameRounded.png`, replacing oversized-corner artwork and preserving its metadata GUID.

Final prompt:

```text
Use case: precise-object-edit. Asset type: transparent production Unity nine-sliced panel frame PNG. Image 1 is the edit target: preserve the worn graphite steel, fine antique bronze edge highlights, narrow uniform rail thickness, matte surface texture and square overall proportions. Change ONLY the four oversized rounded corners into compact softly rounded corners: each outer quarter circle radius should be about 64 pixels on a 1024x1024 canvas, never more than 80px. The straight top/bottom edges must begin by x=96px from each corner; straight left/right edges begin by y=96px. Inner and outer curves concentric and uniform thickness, no fat corner pads. Rectangular panel frame with small gentle bends at corners, NOT a capsule and NOT large sweeping arcs. Rails around 32 pixels thick. The outer edges of the straight rails align nearly to the canvas edge with only a 2 pixel transparent margin, equal all sides. Empty center completely alpha transparent, exterior corners completely alpha transparent, same genuine transparent background as reference. Keep all four corners symmetrical, keep the bronze restrained and dark steel understated. No screws, rivets, diagonal chamfers, plates, ornaments, extra rings, paper fill, background rectangle, text, logos or objects. Output one frame only with actual alpha transparency.
```
