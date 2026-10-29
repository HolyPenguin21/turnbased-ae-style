# Tactical match Save/Load

Implementation targets Unity **6000.5.4f1** and remote `master` **06c63685d3a54c9e29fc8dafd31f41bd2cf42447**. No balance or package changes. The earlier local in-memory integration additions are retained.

## Player flow and eligibility

Game menu: Save and Load. Main menu: Continue opens saved matches when any exist, otherwise the readable campaign, otherwise is disabled. The authored slot window shows six initial positions in save mode, lists occupied positions in load mode, pages through files and offers New slot in save mode; the format has no six-slot limit. Existing saves require an explicit Overwrite action. Cancel/Escape closes the dialog without preparing a new match. Failed saves show an error instead of a success message. A recovered backup is identified in the dialog and after a successful scene restoration.

Capture rechecks `GameTurnController.CanSaveMatch` immediately before capturing and again before returning the bytes for writing. Only a living local human's confirmed, settled turn qualifies: no combat/contact resolution, movement, production transaction, popup/decision/report, attachment, card drag, active tactical challenge or hand-flip transition. A queued future delayed battle is legal and persisted. The save dialog removes only its own overlay block during synchronous capture; all other checks still apply.

## On-disk format and compatibility

Files: `Application.persistentDataPath/Matches/slot-N.json`, where N is a nonnegative integer. Storage delegates atomic replacement, temporary-file cleanup, previous-file backup and corruption recovery to the existing `AtomicJsonFile`. An unsupported schema/content revision or unavailable durable campaign context does not fall back to an older backup. Both main and backup go through the same complete decoder before loading.

Envelope schema **1**, format `turnbased-ae-match`:

| Field | Meaning |
| --- | --- |
| `version`, `format` | Exact schema discriminator |
| `contentFingerprint` | Strict SHA-256 fingerprint of authored/imported content and editor version |
| `matchId` | Existing tactical identity; preserved for permanent reward receipts |
| `savedUtc`, `summary` | Display metadata |
| `payloadSha256` | SHA-256 of the UTF-8 payload string |
| `payload` | JSON string containing the complete detached `MatchSaveData` |

The payload contains the confirmed settled-turn marker, match/reward/campaign context, complete logical/visual map layout and decorative border, exact turn position and clocks, identity highwater, world, AI and historical presentation DTOs. Collection ownership, selected profile deck and campaign progress are not embedded or rolled back.

The transport is `MatchSaveJson`, a strict field-only JSON reader/writer over known DTO types. It does not use runtime mission/model reflection, file-supplied type names or Unity object references. Runtime entity/objective conversion is explicit in the concrete codecs. DTO metadata inspection only handles primitive fields, enums, arrays, lists and known nested DTO fields. `link.xml` preserves the required metadata/default constructors for stripped builds. Native player-build/AOT validation remains required.

This transport replaces the initially considered `JsonUtility` wire path: the complete AI tree exceeds 15 nested levels, and v1 must preserve explicit nulls and depth without depending on Unity inline serializer behavior. No JSON package dependency was added. Inputs reject duplicate/unknown/missing fields, invalid UTF-8/JSON, nonfinite or out-of-range numbers, unknown enum values, excessive nesting/complexity, unsupported versions, invalid checksums and files over **64 MiB**. Owner codecs then validate authored keys, unique IDs, references, historical identity manifests and highwater limits, human turn position, map membership and memory coverage before staging. They never substitute a similar/random definition for a missing key.

`MatchSaveAssetManifestBuilder` regenerates `Assets/Resources/MatchSaveAssetManifest.asset` automatically after content imports, before play and before build. It records GUID/local-file keys and direct references for sprites/materials/marker prefabs/events. Its fingerprint incorporates direct gameplay source/importer hashes and Unity version, while excluding declared UI presentation assets and generated registries; changed gameplay content is deliberately incompatible in schema 1. There is no migration promise across content edits. The committed manifest is a source-cache seed; import the complete project to generate the authoritative manifest before play/build.

## Capture and restoration

`MatchWorldCapture` follows canonical references into armies, units and cards, including departed paid units, carrier containers, service AA gunners, delayed participants already removed from the map and private AI evidence. Player identity follows historical references too. Runtime unit stats and both attachment-accounting snapshots are stored directly; equipment is not applied again. Hand order, overflow, remaining deck multiset/order, tactical/produced cards, age, frozen loadout and quotas survive profile deck edits. Historical army shells remain unregistered.

`MatchAiCodec` explicitly maps all **six** objective types and all **22** persistent owners. Dictionaries, tuple/nullable fields, snapshots, pinned actors, reservations, phases, expiry clocks and registered strategic predicates have concrete tagged representations. Unknown predicates/types reject capture. Generated typed DTOs/mappers come from the checked-in schema and generator; new runtime fields require schema/constructor/test updates.

`MatchPresentationCodec` persists vision tiers, old display rosters and frozen renderer trees, including the observed flag frame, sprite/material asset keys, tint, sorting, transform hierarchy/pose/scale, renderer visibility membership, exact primary/ordered auxiliary roles and child activation. Old captured/destroyed buildings are restored from these records, without querying their current hidden entities. The map descriptor includes the decorative extent used to size fog.

The load order is:

1. Read bounded bytes; verify envelope, schema/content and payload; construct and validate detached canonical objects. This does not issue IDs, register entities, alter the current session or claim rewards.
2. Create the encapsulated `MatchFileRestoreSource`; call `GameSession.TryPrepareRestore`. Recheck the pending campaign operation against durable campaign state.
3. Load Game. Existing early setup reset clears outgoing world holders; map and hand startup consume prepared data without generation/deal.
4. Coordinator reserves ID highwater, restores world and explicit relationships inside `MatchRestoreScope`, then AA/stealth, vision/human history and AI owners.
5. Import frozen remembered visuals; require complete coverage; quietly refresh derived resource/event markers.
6. `ResumeGame` binds the exact saved turn. No BeginGame/new-turn/income/AP/movement/AA refill, confirmation, random opening deal, reaction or event reward is replayed.

Scene-stage failure uses the existing partial-world cleanup and returns to Main Menu with an error. Persistent Library rewards still use the existing exact `(MatchId, hex, authored card key)` receipts, including an exhausted award. Tactical rollback does not increase permanent ownership again. A replaced/completed campaign operation is rejected before scene replacement.

**RNG decision:** schema 1 does not persist Unity/global RNG state. Loading itself performs no new map generation, random deal or selection. Future random outcomes after continuation may differ; deterministic replay is not promised.

## Verification

All commands accept the existing external .NET/NUnit cache; no Unity editor was installed in this environment.

```sh
export DOTNET=/path/to/dotnet
export DOTNET_BIN="$DOTNET"
export NUGET_PACKAGES=/path/to/nuget/cache
python Tools/match-save-verify/run-files.py
python Tools/match-save-verify/world/run.py
python Tools/match-save-verify/audit-world/run.py
python Tools/match-save-verify/audit-world/native-run.py
python Tools/match-save-verify/visual-role-audit/run.py
python Tools/match-save-verify/integration-run.py
python Tools/save-preparation-verify/ai/run.py
python Tools/save-preparation-verify/presentation-codec/run.py
python Tools/save-preparation-verify/integration/run.py
python Tools/save-preparation-verify/hand/run.py
python Tools/save-preparation-verify/world/run.py
python Tools/save-preparation-verify/presentation/run.py
python Tools/collection-verify/run.py
python -m unittest discover -s Tools/save-preparation-verify -p 'test_*.py'
python Tools/save-preparation-verify/run.py --binding --base <immutable-baseline-commit> --packages "$NUGET_PACKAGES"
node Tools/unity-yaml-verify/verify_full.js Assets/Resources/MatchSaveAssetManifest.asset
node Tools/unity-yaml-verify/verify_types.js Assets/Resources/MatchSaveAssetManifest.asset
```

The managed full-file runner keeps the production transport, capture/decode/validation, restore source/coordinator, logical owners and ResumeGame. It adapts native construction, object-null semantics, map/asset wiring and UI/rendering. It does not execute scene activation, renderer reconstruction or native popup input. Dedicated native EditMode fixtures cover those paths but were not executed here.

Final evidence and remaining native checks are recorded in the implementation plan execution ledger. Required final editor checks: complete EditMode suite, actual Game/MainMenu activation and slot dialog availability/cancel/errors, captured/destroyed Last Seen renderers and map-border fog extent, normal new game, Library receipt replay including no eligible card/profile backup failures, campaign operation replacement, and a stripped standalone build. Native/UI completion must not be inferred from managed adapter results.

The subsequent bottom-up audit, confirmed defects, fixes and fresh results are recorded in `docs/match-save-load-audit.md`. That ledger supersedes the initial verification counts.

## Authored Save/Load UI (2026-10-29)

Edit `Assets/Prefabs/UI/Panels/MatchSaveSlots.prefab`. Its instances are under the MainMenu
scene Canvas and GameMenuBlockingRoot in Game; both inherit the scene's 1280x720 scale.
`Window` contains six reusable SlotRow buttons, status and pagination/action controls;
`DeleteConfirmation` is an authored sibling with a confirmation panel. Code sets labels,
visibility and availability, and preserves the authored styling/layout. Closing hides the
instance instead of destroying it.

MainMenu Continue has strict priority: any match files (including backup-only or corrupt)
open the load list, otherwise a readable campaign opens Campaign, otherwise Continue is
disabled. Multiplayer keeps its existing scene configuration. There is no campaign entry
inside the load list. Game uses the same prefab for both modes and returns to its menu on
cancel. MainMenu hides its underlying menu while the window is open to isolate keyboard input.

Delete requires confirmation and removes the selected slot's `.tmp`, `.bak`, then primary
file. An error is displayed and disk contents are re-read; if that read also fails, the current positions remain visible; other slot IDs are not renumbered.
The six initial save positions become empty on deletion; a deleted extra position disappears. Other new unsaved positions remain available until the window is closed and reopened. The load
list contains occupied positions only. Rendering metadata may read a usable backup without
quarantining the primary; actual loading retains normal corruption quarantine behavior.

Compatibility fingerprints now hash direct gameplay source files and importer metadata,
excluding scenes, declared presentation directories, generated asset registries and known
HUD-only shaders. World-marker dependencies stay protected even within UI directories.
Direct hashing prevents GameConfig's references to UI prefabs from indirectly hashing their
visual edits. Existing strict schema/content checks remain in force. This changes the
fingerprint algorithm once: earlier saves are retained on disk but are not automatically
compatible with this version. Later visual edits within the designated UI directories no
longer invalidate newly created saves. No blanket legacy bypass/migration was added.

Verification commands and native checks: `Tools/save-ui-verify/README.md`.
