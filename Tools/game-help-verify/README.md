# Game help verification

Read-only integration checks (Python 3 + PyYAML):

```sh
python3 Tools/game-help-verify/verify.py
node Tools/unity-yaml-verify/verify_full.js Assets/Scenes/Game.unity Assets/Config/GameHelpCatalog.asset
node Tools/unity-yaml-verify/verify_types.js Assets/Scenes/Game.unity Assets/Config/GameHelpCatalog.asset
```

These validate the authored references, separate sibling roots, both masked scroll columns,
flexible topic rows, templates, stable translation keys, review-document synchronization and
coverage of every `UnitAbilities.All` tag. They do not compile C# or run Unity layout/input.

Engine checks in Unity **6000.5.4f1**:

```sh
Unity -batchmode -projectPath <checkout> -runTests -testPlatform EditMode \
  -testFilter Game.EditorTests.GameHelpPlayModeTests \
  -testResults <results.xml> -logFile <editor.log>
```

The two tests enter Play Mode and clones only the authored menu subtree into an isolated
canvas. It checks blocking/restoration, repeated open/close, external disable, mutual exclusion,
AI observer hold, translation reflow and ordered text/image/caption rendering. It does not
start a match or change the authored catalog. Full EditMode tests are still required before merge.

Manual acceptance:

1. Start a human match; open help with the existing `?` button. Check the help root is beside
   GameMenuBlockingRoot and the latter stays inactive. Check the map, hand and hotkeys are blocked.
2. Scroll both columns independently with wheel, drag and scrollbar. Navigate the long topic
   list by keyboard; a focused topic must be scrolled into view. Select different topics.
3. Close by button/Escape and reopen. The topic must remain selected; changing topics starts
   the article at the top. Open the normal game menu and check options/save/load still work.
4. Check wide and narrow landscape resolutions and a long translated title/body. Text must
   wrap and stay inside the masked column, with the close button always outside the scroll area.
5. Repeat in an AI-only spectator match, while paused and running. Help holds AI at the same
   action boundary as the existing menu; closing preserves the spectator's previous pause state.

Content review: `Docs/GameHelp_Topics_Validation.md` (131 topics, 11 sections).
Articles intentionally remain pending until their headings are approved. Add ordered blocks in
`Assets/Config/GameHelpCatalog.asset`; use `GameHelpPanelUI.SetTextResolver` for future translations.

## Verification in this workspace (2026-11-17)

- The 9 structural checks pass; both Unity YAML validators pass; YAML parsing and diff checks pass.
- Unity and dotnet/Mono are not installed. C# compilation, the engine fixture, full EditMode suite
  and manual visual/input acceptance were not run.
