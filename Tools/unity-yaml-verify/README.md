# unity-yaml-verify

Two Node.js scripts for validating hand-edited Unity YAML (`.unity` scenes, `.prefab`, `.asset`)
after a manual/scripted edit — this project's scenes and prefabs get hand-authored directly in
YAML fairly often (rather than only ever going through the Unity Editor), and Unity gives no
warning at all if a `fileID` reference is wrong until something breaks at load/edit time in the
Editor.

Run both after any manual YAML edit, before trusting it:

```sh
node Tools/unity-yaml-verify/verify_full.js Assets/Scenes/Game.unity
node Tools/unity-yaml-verify/verify_types.js Assets/Scenes/Game.unity
```

Both accept multiple paths in one call (e.g. every `.prefab` touched in a batch).

- **verify_full.js** — referential integrity: every `{fileID: N}` in the file actually has a
  matching `--- !u!TYPE &N` definition somewhere in the same file, no fileID is defined twice,
  and no fileID exceeds Int64 range (an oversized hand-picked ID silently corrupts on save).
  Does NOT check that a reference points at the *right kind* of object — see verify_types.js.
- **verify_types.js** — catches the specific class of bug where a reference exists but points at
  the wrong object type (e.g. `m_Father` pointing at a GameObject's own fileID instead of its
  Transform/RectTransform's — a real bug this project hit once, which verify_full.js alone
  couldn't catch since the target ID did technically exist, just as the wrong type). Currently
  only checks `m_Father` (must resolve to `!u!4` Transform or `!u!224` RectTransform) and
  `m_GameObject` (must resolve to `!u!1` GameObject) — extend `verify_types.js` if another
  commonly-hand-edited field turns out to need the same check.

Neither script touches the file — read-only, safe to run as often as you like. Exit code 0 means
clean; non-zero means something needs fixing before the edit is trusted.
