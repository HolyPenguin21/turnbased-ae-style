# Concrete tactical file verification

The production transport is `MatchSaveJson`. It serializes known detached public-field DTOs;
it has no dependency on JsonUtility, System.Text.Json or a JSON package. Explicit world,
historical and all-six-objective/22-owner AI codecs convert runtime models to these DTOs.

Set external environment inputs (not project dependencies):

```sh
export DOTNET=/path/to/dotnet
export DOTNET_BIN="$DOTNET"
export NUGET_PACKAGES=/path/to/nuget/cache
export NUNIT_DLL="$NUGET_PACKAGES/nunit/3.14.0/lib/netstandard2.0/nunit.framework.dll"
python Tools/match-save-verify/run-files.py
python Tools/match-save-verify/world/run.py
python Tools/match-save-verify/integration-run.py
python Tools/save-preparation-verify/ai/run.py
python Tools/save-preparation-verify/presentation-codec/run.py
```

`run-files.py` executes production JSON/storage and card-operation availability code. Its
UI adapter declares only the native hand's fields/cards; the predicate is the Assets source.
It checks malformed/duplicate/missing/unknown fields, numeric bounds, a 25-deep null DTO chain,
exact Int64/escaping, graph detachment, cycles/nonfinite output, disk roundtrip, backup,
future-version preservation, failed-write preservation and slot numbers beyond the UI page.

`world/run.py` executes real world capture/decode/import, canonical hand/unit/army state,
transport/activation/attachments/AA/stealth/delayed references and historical-only neutral
owners. Native construction/assets are explicit adapters outside Assets. Tactical transport
is the production serializer; unrelated profile serialization adapters remain separate.

`integration-run.py` compiles all runtime code and runs actual capture -> serialize -> atomic
file write/read -> decode/validate -> TryPrepareRestore -> scene-owner reset/transaction ->
RestoreWorld/Knowledge/Presentation -> ResumeGame. It repeats the same file after mutations,
checks canonical replacement/no refill/no extra turn, keeps an eliminated slot in a three-player
round and rejects malformed/incomplete files before session/ID changes.

Its temporary native-facing adaptations are explicit in the runner: cached Unity-reference API
compatibility, CLR null/identity instead of destroyed native-object checks, native object/field
construction, Time/frame/Debug calls, renderer-only palette/color defaults, native asset-manifest
lookup wiring, scene map-component lookup, text-input focus and final UI presentation/blocking.
The scenario uses a legitimate unrendered airfield container; it does not replace world/codecs,
content/reference validation, turn restoration or the scene restore transaction with test logic.
It does not execute a Unity scene, input, coroutines or renderer reconstruction. The native
EditMode fixtures remain required for those behaviors.

AI's runner executes all concrete objective and owner mappings through `MatchSaveJson`,
including the deep ActiveDefence snapshot and both valid/invalid preflight identity checks.
The historical runner executes logical old observations and DTOs, and explicitly excludes
native frozen renderer reconstruction.

The source generator and `ai-types.json` preserve explicit typed AI DTO/mapping field coverage.
Regenerate with `python Tools/match-save-verify/generate_ai_codec.py`; new model fields require
updating the schema and exact reconstruction constructors and extending populated tests. No
runtime reflection converts a MissionIntent or another live model to a save payload.

Full Unity 6000.5.4f1 EditMode, real Game/MainMenu startup and UI, asset-manifest rebuild, frozen
sprites/materials/flag frames, fog border, normal new game and stripped-player builds have not
been executed by these managed tools. See `docs/match-save-load.md` for the remaining editor checks.

Bottom-up audit runners:

```sh
python Tools/match-save-verify/audit-world/run.py
python Tools/match-save-verify/audit-world/native-run.py
python Tools/match-save-verify/visual-role-audit/run.py
```

The first checks real capture/import and the controller required by a ready carrier deck,
plus preflight relationship rejection. The second uses full runtime sources and actual
AntiAirRules to check ability ordering for units/buildings/facilities; its name denotes the
runtime-reference runner, not execution in Unity. The third runs actual frozen renderer
role capture/import and the subsequent real CopyAppearanceFrom, with engine-only component
surfaces. See `docs/match-save-load-audit.md` for findings and remaining native checks.
