# unity-scene-index

Compact, queryable index of a hand-authored Unity YAML file (`.unity`/`.prefab`) — answers "what's
this object's hierarchy/wiring" without re-grepping the raw YAML from scratch each time.

```sh
node Tools/unity-scene-index/scene_index.js Assets/Scenes/Game.unity --tree
node Tools/unity-scene-index/scene_index.js Assets/Scenes/Game.unity --find BattleScreen
node Tools/unity-scene-index/scene_index.js Assets/Scenes/Game.unity --id 8880000000000000035
node Tools/unity-scene-index/scene_index.js Assets/Scenes/Game.unity --json scene.json
```

- `--tree` — the whole GameObject hierarchy, indented, each line showing `Name [&fileID]` plus its
  components (`ScriptClass [&fileID]` for MonoBehaviours, the built-in type name otherwise).
- `--find <substring>` — case-insensitive name search; prints each match's parent, full component
  list, and children.
- `--id <fileID>` — look up one object or component directly. For a GameObject: parent/components/
  children. For a MonoBehaviour: every serialized field and its raw value (`{fileID: ...}`
  references included) — this is the fast path for "what is this component actually wired to."
- `--json <path>` — dumps the full parsed index (every object, every field) as JSON, for anything
  more involved than the built-in query modes.

Read-only, never touches the source file. Not a general YAML parser — relies on Unity's own
consistent 2-space-indent serializer formatting, same approach as `Tools/unity-yaml-verify`'s
scripts. If Unity ever changes its serialization format this would need updating, but that hasn't
happened across this project's whole history so far.
