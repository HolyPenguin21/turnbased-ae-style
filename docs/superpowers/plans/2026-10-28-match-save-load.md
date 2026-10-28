# Match Save/Load Implementation Plan

> Execute the independent codec domains concurrently, integrate and verify in this session. User requested implementation through completion and delegated slot/UI choices; no further design approval required.

**Goal:** Persist and restore an exact settled, confirmed local human turn through a versioned file and existing scene restoration chain.

**Architecture:** Explicit detached public-field DTOs and concrete codecs for canonical world entities, historical presentation and all 22 AI owners/six objectives. Fully validate an independent graph before staging; stage an encapsulated source, reconstruct quietly in the existing coordinator. Slots reuse AtomicJsonFile. Profile/campaign persistence stays outside tactical snapshots.

**Tech Stack:** Unity 6000.5.4f1, current C#, strict known-DTO JSON transport, existing uGUI/TMP and managed verification tools. No package changes.

**Spec:** `2026-10-28-match-save-next-chat-task.md` and user attachment (identical requirements).

## Global Constraints

- Remote master is 06c63685d3a54c9e29fc8dafd31f41bd2cf42447 (GitHub compare confirms identical).
- Source cache baseline 06d1ae5 is not remote history. Preserve all incoming local integration changes. No commits/push/merge authorized.
- Preserve GUIDs, serialized references, game rules and normal new-game lifecycle.
- No Unity editor installed; managed adapters never count as native execution.
- Loading cannot roll back permanent collection or campaign progress. Keep MatchId and exact durable receipts.

## Review Focus

- Decode must not issue identities, register objects, overwrite current session, mutate RNG or claim rewards.
- Frozen last-seen appearance must never consult a hidden current entity, even after capture/destruction.
- Every private/persistent AI owner, nullable clock, canonical reference and all objective fields must survive serialization.
- Invalid/newer content, missing references, duplicates and incomplete operation facts fail before scene reset.
- Atomic write and backup failures must report failure while preserving a prior usable file; cancelled UI must do nothing to game state.

## Tasks and Interfaces

- [x] 1. Read handoff/code/rules, confirm remote base, preserve incoming changes in separate source copy; run baseline syntax/binding and policy checks.
- [x] 2. World/entity/event DTO graph. `MatchWorldCapture(config,players)` + IncludeArmy/Unit/Card callbacks + Finish. `MatchWorldCodec.Decode(data,config,identity)` -> detached canonical `MatchWorldGraph` with Players/Player/Army/Unit/Card, RestoreWorld(map), RestoreKnowledge(). Tests: hand/deck/loadout/tactics/quota, altered unit stats/attachments, transport/AA/stealth/delayed/history identity, repeated decode.
- [x] 3. AI DTOs/objective codecs. `MatchAiCodec.Capture(playerId,includeArmy,includeUnit,includeCard)`, Validate and Restore with canonical resolvers. Tests: six populated objectives, 22 persistent owners, nullable clocks, follow-on continuation without replanning.
- [x] 4. Historical knowledge/appearance DTOs. `MatchPresentationCodec.Capture(selection,config,callbacks)`, Validate, RestoreKnowledge, RestorePresentation. Stable content asset registry. Tests: frozen old building flags/sprites/tints and display rosters, complete memory coverage, no hidden-world query.
- [x] 5. Versioned envelope, strict syntax/size/checksum/content validation and staged source. `MatchSaveCodec.Capture(config,map,turns,selection)` -> bytes; `Decode(bytes,config)` -> IMatchRestoreSource. Tests reject malformed/unknown/duplicate/dangling/incomplete before GameSession change; roundtrip through real serialize/read/write pipeline.
- [x] 6. Atomic file slots and menu integration. `MatchSaveStore(directory)` with numeric safe slot IDs, List/Save/Load. UI offers six initial slots and unlimited additional slots; overwrites require in-dialog confirmation, cancel leaves match untouched. Recheck CanSaveMatch at capture. Load scene only after source staging. Save failures show errors. Tests atomic backup recovery/future version preservation/write failures/cancel.
- [x] 7. Integrated verification/review/documentation. Run portable owner suites + new codec/store checks, baseline/current binding and all C# syntax. Review changed production/test/adapter code. Native JsonUtility/EditMode/scenes/UI remain explicitly unexecuted if Unity unavailable.

## Decisions

- Explicit format schema version 1 and strict content fingerprint, no speculative migration or approximate key fallback.
- RNG continuation is not persisted in v1: loading does no map generation/deal/reroll; subsequent future random outcomes may differ. Document this limitation, do not promise deterministic replay.
- Slots are files, not fixed positions in the format. UI starts with six slots and can create another.

## Execution ledger

- Baseline: 926 C# files parse; baseline/current runtime and two incoming native fixture files bind against cached references; policy/turn checks pass. These are not Unity execution.

- Final source: production codecs for all6 objectives/all22 AI owners; canonical world closure, historical-only owners, frozen visuals, version1 envelope, strict JSON transport, atomic slots and menus connected.
- Review fixes: detached AI validation no longer reserves global IDs (valid validation previously raised523->553; valid+late-invalid regressions passed). Current-round eliminated slots remain legal; full file Capture reproduced rejection before fix. Required player economies/human loadout/quota and completed production quotas reject malformed input before staging.
- Transport decision: use real MatchSaveJson for the DTO wire path. Field metadata/default constructors are preserved in link.xml; model/objective codecs remain explicit. Deep25/null/Int64 transport tests pass, removing the Unity inline-depth dependency. No new package.
- Executed:20 full production file/transaction checks (capture/write/read/decode/stage/actual scene helper/ResumeGame; twice after live mutations;17malformed payload cases),14 file/JSON tests plus real card-operation predicate,6 production world codec tests,23 AI/core cases (all6 objectives and22 populated owner aggregate through production transport),17 logical historical codec checks,11 incoming in-memory integration checks,22 world,10 hand,3 derived-presentation and21 collection/receipt checks,7Python verifier regressions. No failing final checks.
- Final syntax/binding:950 C#files parse without error; immutable baseline06d1ae5 and current runtime bind;8 changed/new EditMode fixture files bind with cached Unity2021/package/compile-only editor adapters. This is NOT a Unity6000.5.4f1 editor build. Baseline was not updated after changes.
- Both YAML validators pass on the new asset manifest; link.xml parses; git diff --check clean. Independent review performed; identified production issues fixed with regressions. No deferred minor findings reported.
- Native coverage:42 full-file EditMode scenarios added, not executed. Complete EditMode, real scene activation/UI/cancel/errors, live and historical rendering/material/flag reconstruction, manifest rebuild, normal new game and stripped/AOT build remain unexecuted without Unity. RNG continuation is deliberately not persisted; future random outcomes may differ.
- No commits, push or merge. Source-cache history is not published. Deliver a point patch including preserved incoming integration changes against the verified remote source base; exclude missing cached binary assets from the patch.

## Subsequent bottom-up audit (supersedes initial counts)

- User requested broader bottom-up verification. Three independent reviews plus primary inspection traced private owners through downstream actions. Nine confirmed defect groups are fixed and documented in `docs/match-save-load-audit.md`: carrier controller, remembered renderer roles, finite float extrema, missing recon trail, carrier/transport relationship validation, ability-order semantics, human perspective, stale campaign backup recovery, derived geometry overflow.
- Fresh checks:24 full-file chain,18 file/JSON,25 AI/core,3 carrier/relationship,7 renderer-role, real AA behavior for all3 source kinds,6 world codec,17 historical codec,11 in-memory integration,22 world owners,10 hand,3 derived presentation,21 collection/receipt and7 Python checks all pass.
- C#951 syntax files pass; original immutable baseline and current runtime bind using cached Unity2021 reference/package adapters;9 changed/new native fixture files bind, not executed. Both YAML validators, AI generator idempotence, link.xml/meta/GUID integrity and diff whitespace checks pass.
- Additional full-file/native fixtures cover missing/wrong current viewer, overflow geometry and stale campaign fallback; focused native carrier and renderer tests are prepared. Native Unity6000.5.4f1 scenes/UI/input/rendering/manifest rebuild/AOT remain unexecuted. No deterministic future RNG promise. No commits/publication.
