# Tactical challenge cards

Implemented against upstream `9acade34c0ddaad50c796b4261fd38235c8acf21`.

## Rules

- Existing `CardType.Tactic` is used. These cards do not enter the ordinary deck, collection
  loadout, Research/Production catalog, or main hand.
- `PlayerSetupData.Tactics` owns a match-only inventory shared by map and battle views.
- A card costs no AP/resources and is consumed on confirmed application. Selecting, replacing,
  or deselecting a card does not consume it. Each side can apply one card per challenge.
- The defender decides first using the existing Fate turn order. Tactics are an action inside
  that decision, alongside Fate. A tactic counts as a changed result and reopens the opponent's
  response. There is no separate post-Fate phase.
- A human decision waits for input when either Fate or a legal tactic remains. No hero and zero
  Fate do not prevent tactic use. A pure suppression card is unavailable against zero effective successes.
- Existing result-screen auto-close still applies after both decisions are complete.
- An added success is guaranteed and may exceed the original tokens pool. Suppression removes
  existing effective successes and never creates a missed die eligible for a Fate reroll.
- The original tokens remain unchanged. Signed modifiers are carried in `BattleChallengeRollResult`
  into the same rules the engine uses for damage, abilities and Capture/Kill.
- AddAndSuppressSuccess applies both parts atomically: add own successes and remove opposing
  successes, using `tacticAmount` for each part. With no opposing successes it still adds own
  successes; pure SuppressSuccess remains unavailable against zero.
- In Research/Production, AddSuccess increases the operator's successes; SuppressSuccess reduces
  the fixed resistance of this attempt. The combined card does both. The product definition remains
  unchanged. Payment, blueprint quota handling, output minting and temporary Fate restoration keep
  their existing owners.

## Presentation

The battle screen uses its existing vertical tactical column. On the map, `CardHandUI` uses the
ordinary `CardUI` prefab, seven fixed slots, separators, hover and scroll controls for both ordinary
and tactical cards. There is no cloned battle hand. The ordinary and tactical scroll offsets are
independent. Tactical faces show artwork without names, descriptions, costs or combat stats.

Switching replaces each visible slot individually: close the outgoing card to its edge, then open
the incoming card, or leave the slot empty. Cards start from left to right with an overlapping wave
(default duration 0.24 seconds, start interval 0.05 seconds). Only card transforms animate; the hand
container and surrounding controls stay fixed. A new switch normalizes any unfinished wave before
starting the next one. Draw/deploy input and card interaction are blocked during the transition.

Dragging a legal card into the challenge slot **stages** it and immediately previews its effect.
The exact card instance stays in `PlayerSetupData.Tactics` until Accept, but both hand views omit
that staged instance. Dragging another card into the occupied slot replaces it and restores the
previous card to its original inventory order. Dragging the staged card back into the hand clears
the slot and its preview. The slot background stays fixed; its card uses the same `CardUI` drag
handler and `UIDragUtility` as hand cards. Invalid drops and stale callbacks preserve the selection.

Preview modifiers are rebuilt from the current tokens and committed session modifiers through
`TacticRules.Apply`; replacement never reverses an already-clamped suppression by arithmetic.
Fate rerolls remain real and are not refunded when the staged card is returned. A pending card keeps
the decision open even after the last useful Fate spend. Accept validates and commits only the final
card through the existing battle/production session, consumes it exactly once and completes the
participant's decision. A committed battle tactic counts as an action for the opponent's response.
Closing the presentation before Accept drops the reservation without changing the inventory.
The last committed card image is retained separately from the staged card: it cannot show through
the staged artwork, and returning the preview restores that committed image without undoing its effect.
A returned card is revealed in the hand window even if staging the last visible card had shortened
that window. Normal-card additions/removals settle an in-progress flip before mutating its lists.
The human response temporarily takes precedence over an AI hand being watched on the map; closing
the challenge restores the watched AI hand and its counters. Repeated Fate callbacks cannot spend
again after a spend or Accept until the next decision window opens.

Click selection followed by a click on the slot also stages a card. An invalid interaction displays
the standard `GameTurnController.ShowSpawnHint` / `PopupPanelUI` information window. The info window
covers the hand and blocks Accept, Fate and OK; dismissing it cannot also accept the challenge in
the same frame. Closing a decision, rebuilding the hand, disabling a view or opening an info window
cancels an active drag. The staged card survives the temporary closure around a Fate animation.

UI uses existing scene prefabs and creates the draggable slot image at runtime. No new scene
assignments or GUID changes are required.

## Initial content

| Key | Name | Effect |
| --- | --- | --- |
| `neutral.tactic.precise-execution` | Precise Execution | +1 own success |
| `neutral.tactic.disruption` | Disruption | -1 opposing success |
| `neutral.tactic.flawless-execution` | Flawless Execution | +2 own successes |
| `neutral.tactic.total-disruption` | Total Disruption | -2 opposing successes |
| `neutral.tactic.tactical-advantage` | Tactical Advantage | -1 opposing / +1 own success |

One tactic reward is added to each variant of Oasis outpost, Crashed cargo blimp, Orbital satellite,
Resort settlement and Nuclear silo. Existing guards, resource counts and prior card rewards remain
unchanged. The last reward variants of Orbital satellite, Nuclear silo and Crashed cargo blimp
supply +2, -2 and the combined card respectively; the other 12 tactic grants remain +1/-1. Event
grants go directly to the recipient's tactic inventory, even with a full main hand.
Preparing another match clears this inventory for human and AI profiles.

## AI and estimates

The critical-encounter flag is captured when the encounter opens and survives individual attacks,
counterattacks and casualties. Critical means a player citadel fight, or a fight between substantial
hero-led field forces, using the existing hostile-power significance bar. Neutral fights are excluded.

AI follows the existing Fate policy first, then evaluates one tactic against the resulting tokens.
It preserves cards outside critical encounters and when no meaningful result changes. It can prevent
a lethal hit, fully block damage, secure a kill, enable ShockAttack, or improve Capture/Kill to escape
or capture. Bodyguard/armor and damage abilities are included in this comparison. The same policy is
used for fast battles; aviation's headless strike path also applies the session result.

Ordinary AI Research/Production conserves this stock: the requested AI spending restriction is
military. Human Research/Production supports all five cards.

Strategic WorthIt estimates remain conservative and do not assume consumable tactics will be spent.
Their simulation/cache inputs remain unchanged; no live inventory is read from a cached estimator.
Tactic spending does not debit AP or resources and cannot consume the bank's reserved funds.

## Verification and remaining editor checks

Focused EditMode tests cover zero-Fate response order, playing after the last Fate, wrong-side and
foreign-card rejection, exactly-once consumption, success-pool overflow, suppression clamping,
real engine damage, Capture/Kill, all five authored effects in production, combined effects for both
sides including zero opposing successes, authored content and stronger cards securing critical AI kills.
Additional Unity-only tests cover the human coroutine wait and match-setup clearing. The drag fixture
uses EventSystem dispatch through the actual `CardUI` in the ordinary map hand and battle column,
checking staging, replacement, return, consumption on Accept, invalid-drop information, modal
blocking and decision closure during a drag. Presentation tests also cover the overlapping flip
wave, empty slots, interrupted transitions, Fate persistence and production previews. Its C# signatures compile against verification references; execution requires
Unity.

Verification outside Unity uses the repository's reference-assembly/stub approach. It does not
replace an import/compile and EditMode run in the actual Unity version of the project.

Final results: the gameplay/UI source compiles against the available verification references;
all 1,734 tests that passed in the baseline full run still pass, with 28 additional passing tactic
tests. The standalone tactic run passes 28/28. Native-engine tests fail in this environment in both
revisions and are not counted as verified. Both Unity YAML validators and `git diff --check` pass.

In Unity, check: horizontal/vertical hand paging, the flip and return animation, the slot at the
right of the challenge popup, drag/drop from both sources, preview/cancel/apply input, standard info
window layering and input blocking, defense on an AI turn, both auto toggles, and forced-close/reopen
of an active challenge. Check the existing card prefab's long effect text
and supply the five illustrations separately if desired.
