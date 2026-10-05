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
created buttons, all four popup openings, mouse and keyboard settings navigation,
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
Popup subscriptions use the four existing controllers' visibility events and
play only on a hidden-to-visible transition. Settings are stored in PlayerPrefs.
