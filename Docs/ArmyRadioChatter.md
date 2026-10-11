# Army radio chatter

Implementation base: `ee5128dab82a52dc4fecde9e7de3af800782e930` (master).
Scope: presentation behavior; no AI decision or economy changes.

## Integration and appearance

`GameTurnController.BeginGame` configures one `ArmyRadioChatterController` automatically.
Existing scenes need no new references or prefab installation. `GameConfig.armyRadioChatter`
contains the enable switch, language, timing and size settings.

The third approved concept is a rounded, translucent sand-colored speech bubble with a
thin muted outline and a soft shadow. Text uses the existing GameMenuFont and collection
ink color. No typing animation: the entire phrase is readable from the start.

The first living, non-prisoner unit in the actual roster supplies its authored `detailArt`
(or the existing runtime `DetailArt`). The radio does not substitute `Art`, render stat
fields, crop the image or generate a new portrait. Its width is calculated from
`Sprite.rect`; `Image.preserveAspect` is enabled. Inspected infantry, tank and Vessels
detail PNGs are 772×1124 including their existing frame. All 29 records in each of the
three faction catalogs have a populated detail-art reference.

`MapObjectVisual.TryGetScreenAnchor` shares the existing marker hit-test geometry: the
actual rendered center and projected radius, including camera zoom. `ArmyRadioLayout`
places the complete card/message group in the safe area intersected with the camera
viewport, reserving space above the visible bottom hand panel. The card swaps ends to
stay on the side nearer the army; text is never mirrored. The pointer selects any of
the four sides of the message rectangle, avoids its rounded corners and stops at the
marker rim. Projection/layout update in LateUpdate. An off-screen, hidden, moving,
removed or re-ordered speaker cancels the bubble immediately.

## Radio behavior

Only the current viewer's own living armies speak. AI observer matches use the current
AI perspective; hot-seat perspective changes discard previous observations. Prisons,
neutrals and eliminated players never speak.

There are 40 phrases in English and 40 in Russian, across contact, waiting, completed
movement, garrison, air wing, stealth, damage and general radio checks. English matches
the current game UI by default. Contact has priority, followed by existing damage,
stealth and container flags. Waiting phrases require elapsed time in the army owner's
turn and neither recorded relocation nor activation. These are flavor lines, not
tactical recommendations or claims about a mission.

Visibility/content, relocation, stealth and turn events invalidate observations. Contact
checks use the speaker's own shared VisionSystem radius, explicit owner visibility and
existing stealth targetability. Other scouts' knowledge, last-seen contacts and the
debug reveal override do not authorize contact chatter. Moving enemy markers are skipped
until their registry position is settled. Undiscovered events are never queried.

One bubble is visible at a time. Defaults: 4.5-second display, 0.22-second fade-in,
0.35-second fade-out, at least 8 seconds between transmissions, 18-second ambient
interval, 45-second per-army cooldown. Pending contact observations are revalidated,
not queued as an old transcript. Ambient speakers rotate through armies; per-category
phrase counters avoid consecutive identical variants. UnityEngine.Random is untouched.

Menus, battle/contact/event/production modals, human turn confirmation, observer pause
and game over suppress chatter. The radio has no raycaster, interactive controls or
input-blocking registration. Enable/disable balances event subscriptions; dead army
records are pruned. No AP/resource/bank reservations, caches, mission ownership or game
model writes are added. Shared vision-radius and marker-anchor helpers preserve their
existing callers' behavior.

## Validation

- Runtime C# sources plus the new NUnit tests compile against the repository's older
  Unity reference assemblies, with zero errors. Baseline runtime also compiles after the
  same temporary compatibility adjustments: TMP dropdown members and the newer
  FindObjectsByType overload. These changes exist only in verification copies.
- 14 geometry/phrase tests passed in a portable .NET/NUnit harness with test-only math
  primitives. Coverage: all four pointer sides and diagonals, safe placement at nine
  positions for four resolutions, contained-marker guard and every phrase category in
  both languages. This is not a Unity rendering test.
- The checked-in EditMode suite additionally covers real sprite aspect, four camera
  directions and zoom, own-army sight vs distant scouting, stealth and debug reveal,
  roster order, existing flags, gameplay RNG preservation, duplicate configuration,
  non-interactive UI and unsubscribe-on-disable.
- `git diff --check` passes. No Unity scene/prefab YAML was changed.

Unity 6000.5.4f1 is unavailable in the execution environment. Still required: run the
complete EditMode suite in that editor, then inspect a live match at 1920×1080 and another
16:9 resolution. Check all screen corners, camera pan/zoom, HUD clearance, readable full
art and Russian glyphs, contact/idle phrases, army removal/roster changes, modal hiding,
observer pause, hot-seat handoff and scene restart.
