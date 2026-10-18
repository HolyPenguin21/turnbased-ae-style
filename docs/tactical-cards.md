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
- An added success is guaranteed and may exceed the original dice pool. Suppression removes
  existing effective successes and never creates a missed die eligible for a Fate reroll.
- The original dice remain unchanged. Signed modifiers are carried in `BattleChallengeRollResult`
  into the same rules the engine uses for damage, abilities and Capture/Kill.
- AddAndSuppressSuccess applies both parts atomically: add own successes and remove opposing
  successes, using `tacticAmount` for each part. With no opposing successes it still adds own
  successes; pure SuppressSuccess remains unavailable against zero.
- In Research/Production, AddSuccess increases the operator's successes; SuppressSuccess reduces
  the fixed resistance of this attempt. The combined card does both. The product definition remains
  unchanged. Payment, blueprint quota handling, output minting and temporary Fate restoration keep
  their existing owners.

## Presentation

The battle hand reads the player's inventory. On the map, the ordinary hand flips to a horizontal
view of that inventory and flips back when the challenge closes. Its ordering/scroll position is
preserved. The normal deploy/draw input remains blocked.

Drag an available card from the prepared battle column or the flipped map hand into the challenge
slot to apply it. Both sources use the ordinary hand's existing `CardUI` pickup, `UIDragUtility`
movement, sorting and return gesture. `BattleTacticCardUI` supplies the face and interaction callbacks;
it has no separate drag handlers or ghost. The challenge owns drop validation and application.
Returning to the hand cancels without spending. An invalid drop returns the card and displays the
standard `GameTurnController.ShowSpawnHint` / `PopupPanelUI` information window. Closing a decision,
rebuilding the hand, disabling the view or showing a blocking hint cancels an active drag. The info
window covers the hand and blocks Accept, Fate and OK; dismissing it cannot also accept a challenge
in the same frame.

Click selection and Apply tactic remain available. Clicking the selected hand card again clears the
selection; choosing another replaces it. Only the current participant can apply a card, including a
defender during the other player's strategic turn. Stale decisions, already-used or foreign cards,
unfinished rolls and effects with no applicable target return shared session validation reasons.

UI is wired from the existing scene hand/card/button prefabs at runtime, so no scene GUID changes
or manual scene assignments are required. Cards currently use text and the existing card frame;
their new illustration fields are unassigned.

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

AI follows the existing Fate policy first, then evaluates one tactic against the resulting dice.
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
uses EventSystem dispatch through the actual `CardUI` in both hand orientations and checks valid
application exactly once, invalid-drop information, return to the slot, modal blocking and decision
closure during a drag. Its C# signatures compile against verification references; execution requires
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
