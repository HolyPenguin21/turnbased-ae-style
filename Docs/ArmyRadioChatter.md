# Army radio chatter

Scope: presentation behavior; no AI decisions, AP, economy or gameplay RNG changes.
`Game.unity` contains a pre-placed `ArmyRadioOverlay` prefab instance under `Canvas_UI`; the turn controller
has an explicit `armyRadioChatter` reference and `BeginGame` only configures dependencies.
No radio GameObject, component, Canvas or slot is created/instantiated at runtime.
Behavior settings live in `GameConfig.armyRadioChatter`.

## Appearance and placement

The third approved concept retains its translucent sand-colored rounded panel, muted
outline, soft shadow, GameMenuFont and collection ink. The whole phrase is readable while
the complete card/message group fades in. The first living non-prisoner roster unit supplies
its full `detailArt`/runtime `DetailArt`; compact slot art is never substituted. Sprite
proportions are retained without cropping (inspected full images: 772×1124 including frame).

`Assets/Prefabs/UI/ArmyRadioOverlay.prefab` contains one child Canvas and the manager
and three inactive nested instances of `Assets/Prefabs/UI/ArmyRadioMessage.prefab`.
Each message contains its CanvasGroup, panel, shadow, TMP label and full-art Image. This
is the complete reusable pool; there is no runtime construction or fallback UI builder.
It has no raycaster. The manager bounds concurrent messages and prevents duplicate speakers. Each
LateUpdate projects actual marker centers/radii through the camera, intersects the viewport
with the safe area, reserves the bottom hand panel and places whole card/message groups.
The card swaps ends toward its army and the pointer can leave any of the panel's four sides.

Placement retains a valid camera-relative offset to avoid unnecessary jumps. If blocked,
it searches nearby diagonal, horizontal and vertical positions with an eight-pixel body clearance. Existing thin
connectors also reserve space; new connectors cannot cross earlier messages/connectors.
Fading messages retain their reserved space until removed. When there is no valid position,
a new transmission is deferred. If camera movement leaves an existing message with no valid
position, it is suppressed immediately so no overlap is drawn. Off-screen, hidden, moving,
removed or re-ordered speakers also cancel immediately.

## Editing in Unity

- Open `ArmyRadioMessage.prefab` to change all three messages together. Its
  `ArmyRadioBubbleView` exposes art height, text-panel width/minimum height, card/marker
  gaps and shadow offset. Runtime derives only dynamic placement and actual art proportions.
- `Message` / `Shadow` graphics expose fill color, border color/width and corner radius.
- `Speech` exposes the TMP font, font size and color; edit its stretched RectTransform
  offsets for text padding. These choices are preserved by Configure/Show. Text measurement
  refreshes on width, padding, font/size or corner/minimum-height changes.
- `FirstUnitDetailArt` retains Image settings/tint; its sprite is supplied by the speaker.
  Assign a temporary detail sprite in Prefab Mode if needed to preview the portrait.
- `ArmyRadioOverlay.prefab` controls child-Canvas sorting and the three slot references.
  It stretches inside the existing `Canvas_UI`, inherits its CanvasScaler, and isolates
  radio geometry/batching without adding a second root Canvas. Marker radius uses the
  root Canvas scale factor, including at 1920×1080 and other scaled resolutions.
  Leave it in Screen Space Overlay and keep references assigned. The scene's overlay is
  already wired to `GameTurnController`; there is no manual installation step for Game.unity.
- Interval, cooldown, lifetime, fade, humor/dialogue chance and message limit remain in
  `GameConfig.armyRadioChatter`. Visual dimensions/font settings moved out of GameConfig
  into the message prefab, so they cannot silently overwrite Inspector styling.

## Scheduling and content

Defaults (unscaled seconds):

| Setting | Default | Behavior |
| --- | ---: | --- |
| `ambientInterval` | 18 | Interval between ordinary transmissions |
| `minimumGap` | 3 | Global minimum gap; one scheduled dialogue reply is exempt |
| `armyCooldown` | 45 | Per-army cooldown; both dialogue participants are reserved |
| `displaySeconds` | 5 | Total lifetime including entry/exit fades |
| `fadeInSeconds` / `fadeOutSeconds` | 0.18 / 0.28 | Quick, gradual appearance/disappearance |
| `maximumMessages` | 3 | Concurrent messages; clamped to 1–3 |
| `waitingSeconds` | 12 | Minimum stationary observation time for idle chatter |
| `jokeIdleSeconds` / `jokeChance` | 60 / 0.30 | Long-idle threshold and chance on an eligible ambient transmission |
| `dialogueChance` / `replyDelay` | 0.25 / 2 | Chance of a nearby exchange and delay before its reply |

English only: 100 distinct situational lines (14 contact, 14 waiting, 12 each for movement,
garrison, air, stealth, damage and readiness), plus 25 wasteland jokes and 12 question/answer
pairs. Russian tables and the language selector were removed. Jokes/dialogues require no
visible enemy and exclude damaged, concealed and airborne armies. Actual movement/relocation
resets stationary time; an army that moved earlier can eventually become idle again.
Ordinary waiting classification retains activation/turn-movement guards.

Ambient speakers rotate through eligible armies. Fresh contact has scheduling priority; ongoing visible contact also selects contact lines. It is
revalidated against the speaker's own vision radius, explicit visibility and existing
stealth targetability, never fog memory or debug reveal. Existing events invalidate
observations; a one-second periodic sample also discovers idle/new army state. Cooldowns
are not reset by ordinary turn transitions. Presentation-owned randomness picks idle
content without consuming UnityEngine.Random; consecutive joke/dialogue repeats are avoided.

Exchanges use two stationary, on-screen armies of the same viewer on adjacent hexes (distance
one). There is at most one pending reply. Both armies are reserved at the opening line. The
reply must still have the same participants, first units, positions and quiet contact state,
and must find a collision-free slot within its short deadline. Movement, interaction, turn
actor/viewer changes, a modal or a disappearing speaker cancels the pending reply. There is
no old transcript queue and no tactical state mutation.

`HexSelectionController.ArmyInteracted` publishes the existing marker/modal and army-row
click paths. The manager dismisses that army's message, cancels its pending exchange and
renews its cooldown. Dismissal during fade-in starts from the current alpha, never brightens,
and repeated dismissal cannot restart/extend it. Map clicks pass through radio graphics.

Menus/modals, human turn confirmation, observer pause and game over fade existing messages
and suppress new ones. Viewer changes immediately discard prior-viewer UI/observations.
Enable/disable balances subscriptions; retired army observations/cooldowns are pruned.

## Validation

- Baseline and current runtime sources, plus radio and marker NUnit tests for current,
  compile with zero errors against the repository's older Unity reference DLLs. Identical
  temporary compatibility fixes affect only verification copies (TMP dropdown members and
  newer FindObjectsByType overload calls); no stubs are shipped in the game.
- 27 checked-in geometry/content/cadence/fade test cases pass in a portable NUnit harness
  using the real radio policy/layout/timing sources and test-only managed primitives. They
  cover all pointer sides/diagonals, resolution/corner placement, three-army reflow during
  camera movement, adjacent dialogues at all four corners, connector clearance, occupied viewport rejection, cooldown/reservation,
  normal/click exit timing, configurable rounded-corner clearance and exact English content counts. This does not test Unity rendering.
- Native EditMode tests additionally cover real sprite/camera projection, fog/stealth,
  roster flags, gameplay RNG, pool/configuration/subscriptions and the actual selection
  event path dismissing a message/cancelling a pending reply. They instantiate the actual
  authored prefab, check that Configure creates no additional transforms, preserves Inspector
  styling and subscribes/unsubscribes correctly, and prevent replies without the opening message. These were compiled, not run here.
- Both repository YAML validators pass for `Game.unity` and the two radio prefabs. A
  semantic check also verifies scene → overlay → three nested message references, component
  owners/types, font/material links and input pass-through. Source scan confirms no runtime
  radio GameObject/AddComponent/Instantiate calls remain. `git diff --check` passes.

Unity 6000.5.4f1 is unavailable here. Before merging, run the complete EditMode suite and a
live match: camera pan/zoom/all screen edges with three nearby messages, click dismissal
(including during entry), cooldown pacing, long-idle humor, paired exchanges/cancellation,
HUD clearance/full art, modal/observer pause, hot-seat handoff and scene restart. Also
verify the authored nested prefabs import in that editor and editing the message prefab updates
all three slots without runtime style overrides.
