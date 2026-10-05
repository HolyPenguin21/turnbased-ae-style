# UI audio verification

Run the structural integration gate from the repository root:

```sh
python3 Tools/ui-audio-verify/verify.py
node Tools/unity-yaml-verify/verify_full.js Assets/Scenes/MainMenu.unity Assets/Resources/Audio/GameAudio.prefab Assets/Audio/GameAudio.mixer
node Tools/unity-yaml-verify/verify_types.js Assets/Scenes/MainMenu.unity Assets/Resources/Audio/GameAudio.prefab Assets/Audio/GameAudio.mixer
```

These checks do not compile C# or prove Unity imports or plays the assets correctly.

In Unity 6000.5.4f1, run all EditMode tests, including `UIAudioBindingTests` and
`UIAudioLifecyclePlayModeTests` (the latter enter Play Mode using UnityTest).
Verify MainMenu → Game → MainMenu, direct Game launch, inactive and dynamically
created buttons, human turn PopupPanel sound and the three other popup openings, mouse and keyboard settings navigation,
master zero, music pause/resume, persistence after restarting, and repeated Play
Mode entry with Domain Reload and Scene Reload disabled. The bootstrap test
covers service reinitialization; it does not replace actual Editor reload testing.

## Runtime design

`Resources/Audio/GameAudio` is bootstrapped before scene loading and survives scene
changes. Its two 2D AudioSources route into UI and Music mixer groups, with Master
above both. Scene cameras retain their AudioListeners; the binder selects the
active scene's MainCamera listener (or its sole eligible camera listener).

Loaded scene roots are scanned including inactive objects. `UIButtonSound` binds
once to Button.onClick; `UIButtonEventUtility.ResetRuntimeListeners` preserves
that binding when business listeners are rebuilt. Runtime UI factories call
`SceneUIAudioBinder.BindCreatedRoot`. Future factories must do the same.
The three choice/contact popup subscriptions play on a hidden-to-visible transition.
PopupPanelUI uses HumanTurnShown instead: AI/Neutral handoffs and hints are silent. Settings are stored in PlayerPrefs.

## Game menu

`Assets/Prefabs/UI/AudioOptionsPanel.prefab` is the shared owner-adjusted panel.
Both MainMenu and Game reference it. In Game, GameMenuPanelUI owns Options/Continue,
Escape and selection. It blocks existing gameplay canvas groups temporarily and
restores their original flags. GameTurnController folds menu visibility into its
InputBlocked/CardDraggingBlocked flags; direct input readers also guard against
menu input and the same frame that closes it. This blocks human interaction,
without stopping AI or changing Time.timeScale.

```sh
python3 Tools/ui-audio-verify/game_menu_verify.py
node Tools/unity-yaml-verify/verify_full.js Assets/Scenes/MainMenu.unity Assets/Scenes/Game.unity Assets/Prefabs/UI/AudioOptionsPanel.prefab
node Tools/unity-yaml-verify/verify_types.js Assets/Scenes/MainMenu.unity Assets/Scenes/Game.unity Assets/Prefabs/UI/AudioOptionsPanel.prefab
```

Check in Unity: gear positioning at different aspect ratios; options appearance
in both scenes; mouse/keyboard navigation; Escape options → menu → game; map,
camera, placement, card dragging and popup shortcuts while the menu is open;
closing during an existing popup; repeated open/close; unload while open.
Disabled Save/Load must not trigger a click or action.
Also open the menu with Escape during an Army Viewer or battle-arrangement drag:
the captured drag must stop moving, releasing must cancel the drop, and starting
another drag must remain blocked until returning to gameplay. The regression
fixture is `MenuBlocksArmyAndBattleDragsAlreadyCapturedByEventSystem`.
