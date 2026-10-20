# Army UI, challenge skills and token terminology — verification

Base: `408dd276934f5dd34ec63b205be624b6726d0a4a`.
Unity project version: `6000.5.4f1`.
Scope: behavior fixes and a terminology rename; no combat balance changes.

## Changes

- Hex selection and the map army row both exclude every empty mobile army.
- The shared army row uses a stable Prison → Airfield → Garrison → Mobile priority. Mobile input/arrival order remains unchanged.
- Challenge results append actual effects (ShockAttack, Splash, Scorcher, Berserk) after resolving the real victim, including Bodyguard interception. The engine selects collateral once before presentation and applies that same plan after acknowledgement, without another RNG draw. Shock is reported only for a pending turn or suppressed counterattack. Misses and fully absorbed hits report no hit effects; collateral requires positive rounded half-damage and an actually selected target. Field attacks only report Berserk from this list. Each collateral victim retains its separate result.
- Code, tests, authored scenes/prefabs, asset paths and documentation use Token/Tokens. Unity GUIDs and token image bytes are preserved. Words such as `Indices` retain their original meaning.

## Checks completed

- `git diff --check`: passed.
- Changed Game scene and TokenRow/TokenSlot prefabs: both `verify_full.js` and `verify_types.js` passed.
- All 39 scenes/prefabs passed the responsive verifier's YAML/reference/component/hierarchy stage.
- GUID metadata and binary contents for every renamed .meta/.png pair were compared against the base: unchanged (15 files).
- Terminology audit: no remaining Dice/Dices identifiers, words or asset paths. Unrelated words containing those letters, such as indices, were deliberately preserved.
- Differential compile with the repository's old verification references: full source has the same 9 unique editor API errors as base; runtime source has the same 5 unique API errors as base; no new errors. This does not establish a Unity 6000.5 compilation.
- A disposable .NET 8 direct runner executed 11 logic cases successfully: 4 new hit-summary cases, 3 new effect-preview cases, Ionic modifiers, Drill round effects, target-selector counterattack avoidance and side-commander profiles. Preview tests cover absent/non-Bio/consumed collateral candidates, pending-turn shock, unchanged HP/turn order before acknowledgement and unchanged RNG draw count across repeat presentation and application. Old-reference API and logging adapters are confined to the disposable copy, outside the project.

## Checks blocked or failing in this environment

Unity is not installed. Full EditMode/PlayMode tests and the interactive map/modal/result scenarios were not run.

NUnitLite discovery and the following 5 existing tests could not complete in the latest run under CoreCLR because the old Unity reference DLLs produce `TypeLoadException: Internal call method with non_NULL RVA`:

- `Bodyguard_TakesTheHitOncePerRound_WithItsOwnArmor`
- `Bodyguard_IgnoresMissesDiagonalsAndCounterattacks`
- `Counterattack_AnswersAnAdjacentAttackerOncePerRound`
- `Counterattack_CancelledByDeathAndByShockHit`
- `WorthIt_CountsDrillCounterattackAndBodyguard`

An earlier run also hit the same native logging limitation in `Drill_BuffsTheStrongestFightersForOneRoundAndRevertsAtBattleEnd` and `TargetSelector_PrefersTheTargetThatCannotCounterattack`; both pass with the disposable logging adapter. The full suite was not run.

`Tools/responsive-ui-verify/verify.py` fails its layout stage at line 152 on both base and current revisions: the script assumes every prefab instance is the audio settings prefab. No attempt was made to change unrelated UI or this pre-existing assertion.

## Editor follow-up

Run the full EditMode suite in Unity 6000.5.4f1, including updated `CombatReactionSkillsTests` and `ArmyButtonRowPresentationPlayModeTests`. Exercise empty/non-empty mobile rosters, modal storage ordering and arrival order, ordinary/absorbed/intercepted hits, ground effects, aviation/AA results, initiative purchase and token rerolls. Verify imported scripts and serialized references after the asset path rename.
