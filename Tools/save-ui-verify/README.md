# Save/Load menu verification

`python Tools/save-ui-verify/verify_assets.py` resolves the authored prefab object graph,
scene controller bindings, inherited CanvasScaler, and Continue/Multiplayer wiring.
It does not launch Unity.

Set `DOTNET` and `DOTNET_BIN` to a .NET 8 SDK executable and `NUGET_PACKAGES` to the
existing package cache (see `Tools/ai-verify/README.md`). Then:

```sh
python Tools/match-save-verify/run-files.py
export SAVE_VERIFY_WORK=/tmp/save-ui-binding
python Tools/save-preparation-verify/run.py --binding --base 6c2643b3185cf50f26620efe6e57a95df50f0185
python Tools/save-ui-verify/run_continue.py
python Tools/save-ui-verify/verify_assets.py
node Tools/unity-yaml-verify/verify_full.js Assets/Prefabs/UI/Panels/MatchSaveSlots.prefab Assets/Scenes/MainMenu.unity Assets/Scenes/Game.unity
node Tools/unity-yaml-verify/verify_types.js Assets/Prefabs/UI/Panels/MatchSaveSlots.prefab Assets/Scenes/MainMenu.unity Assets/Scenes/Game.unity
```

The file runner executes actual storage/fingerprint code. The Continue runner invokes the
actual compiled routing policy for all four combinations; it does not simulate scene input.
The binding gate uses older reference DLLs and adapters, so it proves C# binding against
that set, not native Unity 6000.5.4f1 compilation or execution.

In Unity, run the complete EditMode suite, including `MatchSaveSlotsUiTests`. Check MainMenu
and Game at 1280x720, 1920x1080, 4K, narrow and wide Free Aspect; arrow/Enter navigation,
Delete confirmation/Escape, repeated reopening, deleting the last save, backup-only and
corrupt saves, save/overwrite eligibility, actual load, and normal campaign continuation.

`python Tools/save-ui-verify/run_dialog.py` executes production ConfirmDelete/ReloadSlots
and storage against controlled files, with only native drawing, cancellation presentation
and focus calls adapted in a disposable copy. It covers retaining unrelated unsaved positions,
base-position reuse, load-mode removal, failed deletion and inaccessible-directory refresh.
It does not replace the native prefab-based EditMode fixture.
