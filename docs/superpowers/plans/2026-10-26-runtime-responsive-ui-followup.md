# Runtime UI responsiveness — follow-up, 2026-10-26

## Scope and baseline

User correction: «колода = коллекция». Collection is the user-confirmed working result;
its code, prefabs and scene layout were not changed by this follow-up.

Baseline read from GitHub: master `cb63fd73bc2e36b75d025e91f142e975c90b079a`.
Compared with PR #241 merge `73b5ec3bce77f8dfc2347ff3a7de4d4bb0b90fc8`:
subsequent changes include both scenes and AI compile fixes. All existing local source
files were hashed against the remote tree; divergent target files were refreshed,
including Card_Hand and Card_Army. Native Unity is unavailable in the verification environment. Publication is based on
the actual remote commit and changed files only, preserving the repository history.

## Evidence, cause and changes

| Area | Evidence and cause | Change |
| --- | --- | --- |
| In-game hand | User screenshot in narrow/tall Free Aspect shows cards floating above the lower trim. Source geometry reproduces it: in a 730×203 hand, the old bottom clearance is 34.34069 instead of 9.25065. Cards shrink uniformly while their centres followed half the increasing container height. | CardRowGeometry now supplies a bottom-referenced centre offset. Ordinary, tactic and debug cards, empty slots, pins, arrows and reorder band use that reference. Hover, flip reset and new-card paths retain it. |
| Game setup | Source hierarchy: PlayerRow/Controls uses fixed widths totalling 824; the original stretched window offers roughly 731 at 4:3 and less in the screenshot aspect. Vertical scrolling alone does not solve horizontal overflow. | The setup window retains its authored 901.68×532.64 internal coordinate space and fits uniformly into its anchored region. Existing row layout, controls, deck line and ScrollRect continue to own their internal geometry. Applied on opening and resize. |
| Army modal | Source hierarchy: Grid/ArmyGridViewport/Modal_Army. Four fixed 106-wide cells plus gaps require 448, while the stretched viewport falls to about 390 at 4:3. DetailText has overflow enabled without a scroll viewport. | Modal_Army retains its authored 828.32×478.32 coordinate space and uniformly fits its anchor region. The existing grid's slots, padding, hit tests and scroll extent remain in the same local coordinates. Detail text receives one bounded vertical ScrollRect with RectMask2D and preferred-height content. |
| Army lifecycle | Grid cards ignore GridLayoutGroup; the modal owns slots and content extent. Deferred destruction could leave outgoing cards active during refresh. | Deactivate outgoing cards, clear reorder preview, cancel carried cards, settle nonanimated slots, reset grid scroll when switching/opening, and reset detail scroll on selection. Resize cancels a drag without mutating the roster. |

UIUtility.FitPanelToAnchors is the sole new size/scale owner for the two windows.
It assigns design rect size and uniform scale rather than multiplying prior values.
Their actual roots have centred pivots, zero anchored offset, stretched anchors, direct
Canvas parents and no competing LayoutGroup/ContentSizeFitter.

Hand screen-scale changes are tracked even when logical rect size is unchanged.
Army window resize also tracks world scale. Existing ApplyScreenDelta already resolves
pointer movement through the actual parent, including the fitted modal scale.

## Checks actually performed

- The new C# geometry regression failed on the old implementation with
  `Narrow Free Aspect lifts the cards/pins away from the bottom edge`, then passed after
  the fix. The geometry executable compiles and runs the real CardRowGeometry source.
- Geometry checks cover 1280×720, 1920×1080, 2560×1440, 1920×1200, 2560×1080,
  1024×768, approximately 457×656 from the screenshot, and 360×800; verify lower clearance,
  pins, hover bounds and reorder inversion.
- C# syntax parsing: 863 files, zero syntax errors. This does not bind Unity APIs or
  prove project compilation.
- Both unity-yaml-verify tools pass for current MainMenu, Game, PlayerRow and
  CollectionCard. No YAML was edited in this follow-up.
- git diff --check passes.
- Independent bottom-up source review found no Critical/Important defects. Its minor
  coverage recommendation was addressed: a single window instance is resized
  1280→portrait→wide→4:3→1280, asserting the anchor-region bounds and unchanged centre.
- The full source layout suite remains RED on the same baseline failures:
  test_collection_columns_do_not_overlap_at_supported_aspects at 1920×1200 and
  1024×768. These were observed before edits and remain reported. This is an authored
  scene check, not a Unity runtime verdict. Collection changes were deliberately avoided.
  The runner now continues independent YAML/C# checks after these failures and still
  returns a nonzero exit status. Nothing is silently waived.
- The prior ResourceBar top overflow is outside this change and was not modified.

Repeat source verification:

```sh
python Tools/responsive-ui-verify/run_runtime_checks.py --dotnet /path/to/dotnet
```

## Unity acceptance still required

Unity 6000.5.4f1 is not installed here. No Unity compilation, EditMode tests, Play Mode
or Game View interaction checks were run. The screenshot is the user's pre-fix view;
no post-fix visual result is claimed.

Added native regressions in Assets/Editor/Tests/UI/RuntimeResponsiveUiTests.cs cover
bottom geometry, same-aspect drag cancellation, repeated window fit/reopen and detail
scroll construction. They remain unexecuted.

In the full project, run the complete EditMode suite and then check:

1. Free Aspect near the supplied screenshot: first open, ordinary/tactic/debug hand,
   hover, paging, pins/arrows, flip and resize during drag; pointer-up must not play.
2. Setup: first/repeated open, 2–6 players, add/remove, last-row scrolling, dropdowns
   over masks, deck editor return, keyboard input and Start/Back.
3. Army: player/enemy/locked/garrison paths, 12+ slots, summary/long detail scrolling,
   all eight army buttons plus paging, rename/create/repair/transfer/reorder; resize
   during drag and reopen after scrolling. Verify raycasts and clipping visually.
4. Recheck the already-working collection as an acceptance smoke check, without
   treating it as the in-game hand.

Scale of change: local layout/lifecycle fixes using existing controllers and utility;
no gameplay rules, asset GUIDs, or UI architecture were replaced.


## Horizontal follow-up after the wide Game View screenshot

Baseline: master `adf48913cd1b85ac2b5045f8a202552f36754705`. Its three commits after
#242 change the two scenes; those changes were read and preserved. In this baseline
all five source-layout tests pass, including the collection checks that failed on the
earlier baseline described above. No scene or collection content is changed here.

The user's new screenshot shows very large horizontal gaps. Confirmed cause:
CardRowGeometry used the minimum width/height ratio for card scale but width ratio
alone for the step. In a wide/short view, card width shrinks while centre spacing grows.
The old standalone C# calculation fails the new regression with:
`Wide Free Aspect stretches card spacing independently from card size`.

The row step now uses the same uniform scale as the card. Empty slots and separators
use explicit positions from the same geometry rather than independent width anchors.
Pins use half-slot coordinates. Paging arrows retain their authored offsets from the
hand centre with the same scale. Ordinary, tactic, debug and reorder paths already use
that shared geometry; the bottom reference from #242 is retained.

Checks: full source runner passes (five layout tests, both YAML validators, actual C#
geometry, syntax of 863 C# files), plus diff checks and independent source review.
Geometry covers ten screen sizes including 1240×360 and 3440×1440, constant proportional
gaps, pin midpoints, bottom clearance and reorder inversion. Added native tests cover
first-open at reference/wide/portrait sizes and repeated resizing of one instance,
including arrow and slot alignment. Unity compilation, native tests and Game View
interaction remain unexecuted because Unity is not installed.
