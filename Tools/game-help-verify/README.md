# Game help verification

Read-only integration checks (Python 3 + PyYAML):

```sh
python3 Tools/game-help-verify/verify.py
python3 Tools/game-help-verify/export_content.py --check
node Tools/unity-yaml-verify/verify_full.js Assets/Scenes/Game.unity Assets/Config/GameHelpCatalog.asset
node Tools/unity-yaml-verify/verify_types.js Assets/Scenes/Game.unity Assets/Config/GameHelpCatalog.asset
```

These validate the authored references, separate sibling roots, both masked scroll columns,
flexible topic rows, templates, stable translation keys, review-document synchronization and
coverage of every `UnitAbilities.All` tag. Content checks also require English instructions in
every article, unique block identities/translation keys and an exact review export. They do not compile C# or run Unity layout/input.

Grouped abilities add a Heading block kind rendered with the existing article-title style; no scene layout changes.
The help window's behaviour in the engine (blocking, open/close, layout) is checked by hand below;
its Play Mode tests were removed on 2026-11-25 (no automated UI tests).

Manual acceptance:

1. Start a human match; open help with the existing `?` button. Check the help root is beside
   GameMenuBlockingRoot and the latter stays inactive. Check the map, hand and hotkeys are blocked.
2. Scroll both columns independently with wheel, drag and scrollbar. Navigate the long topic
   list by keyboard; a focused topic must be scrolled into view. Select different topics.
3. Close by button/Escape and reopen. The topic must remain selected; changing topics starts
   the article at the top. Open the normal game menu and check options/save/load still work.
4. Check wide and narrow landscape resolutions and a long translated title/body. Text must
   wrap and stay inside the masked column, with the close button always outside the scroll area.
5. Choose Abilities once. All 46 abilities must be present on one scrollable page with distinct
   subheadings, effect, instructions and restrictions; there must be no per-ability navigation buttons.
6. Repeat in an AI-only spectator match, while paused and running. Help holds AI at the same
   action boundary as the existing menu; closing preserves the spectator's previous pause state.

Content review: `Docs/GameHelp_English_Content.md` (86 navigation articles, 11 sections,
440 localized text/heading blocks including all 46 abilities on a single page). Headings: `Docs/GameHelp_Topics_Validation.md`.
Source audit: `Docs/GameHelp_Content_Sources.md`.

Edit the ordered blocks in `Assets/Config/GameHelpCatalog.asset`, then run
`python3 Tools/game-help-verify/export_content.py` to refresh the review document.
Use `GameHelpPanelUI.SetTextResolver` for future translations. Missing translations retain the
English fallback text. Image blocks can be added later without replacing article identities.

## Verification in this workspace (2026-11-17)

- The 11 structural/content checks pass; both Unity YAML validators pass; YAML parsing and diff checks pass.
- Unity and dotnet/Mono are not installed. C# compilation, the engine fixture, full EditMode suite
  and manual visual/input acceptance were not run.
