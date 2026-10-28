# Bottom-up Save/Load audit (2026-10-28)

The audit follows actual state owners and their consumers, beyond the original diff and green tests. It covers world/hand/economy/activation/transport/carrier/AA/stealth/events, six AI objectives and 22 persistent owners, scene startup, human knowledge, remembered rendering, menus, strict transport, atomic recovery and durable profile/campaign boundaries. Three independent reviews were followed by inspection and reruns in the primary workspace.

Baseline: previously delivered patch SHA256 `ff123ec65b3952360a6e81bd1bc8c81d2b5fc403978e4c93a344cc1088dabe61`; immutable local source snapshot `06d1ae5`, matching remote source `06c63685`. Original 20 full-file managed checks passed before this audit, despite the defects below. No publication was requested.

## Confirmed defects and corrections

| Defect | Downstream effect | Correction and regression |
| --- | --- | --- |
| Ready carrier deck skipped marker/controller reconstruction | `SelectArmyForOrders` rejected a populated, ready wing after loading | Restore and prefab validation now include populated decks, while hangars/empty containers stay unrendered. Production capture/JSON/decode/import verifies controller, canonical links, spent movement and launch payment. |
| Frozen Last Seen import discarded primary/ordered auxiliary renderer roles | Subsequent `CopyAppearanceFrom` kept an old icon/tint or copied into the wrong renderer | Persist exact primary indices and ordered auxiliary indices, including null slots; validate visibility membership against roles. Real capture/import/copy code passes seven focused checks. Native counterpart added. |
| Finite float extrema rejected by a double-precision bounds comparison | Valid AI facts using `float.MaxValue` could not be saved | Validate by parsing at destination single precision; MaxValue/MinValue/Epsilon roundtrip and overflow still rejects. The original two failures were reproduced. |
| Recon sortie with absent Trail passed preflight | AI restore could call `AddRange(null)` after owners had begun changing | Require each sortie and trail in preflight. Regression checks owner/baseline and all three identity highwaters remain unchanged. |
| Carrier/transport links resolved existing IDs without validating relationships | A host could refer to an unrelated enemy army; moving it could relocate that army, or restore an invalid passenger assignment | Validate ownership, container kind, reciprocal carrier links, live host/hex and transport membership/uniqueness. Removed historical shells remain allowed. Two rejection regressions precede registry/identity changes. |
| Capture sorted abilities | First-match `AA2`/`AA` behavior changed radius 2 to 1 after loading | Preserve the captured ability enumeration order for units, buildings and facilities. Actual production `AntiAirRules` regression reproduced 2→1, then passed for all three. No combat rule/balance changed. |
| Full-match validation accepted a missing/wrong human perspective | Resume could bind the turn without the saved human fog/history | Require current viewer to be the sole living human and require that human's vision entry. Both damaged full files reject before session/ID changes. Other players' lazily created vision is not required. |
| Stale campaign context classified as file corruption | Atomic recovery could quarantine an intact primary and silently load a different prior match from `.bak` | Treat unavailable durable campaign context as unsupported, preserving both files and current match. Full actual store/decoder regression reproduced silent fallback; now rejects without staging. Native counterpart added. Shared profile storage policy is unchanged. |
| Finite layout fields could overflow derived geometry | Accepted radius could yield NaN/Infinity during mesh/fog/ground construction after scene replacement | Preflight checks actual axial positions and single-precision tile/bounds/ground extents, including decorative cells. No arbitrary gameplay size limit added. A finite `float.MaxValue` radius is rejected before session/ID changes. |

## Cross-owner checks

- All six objective mappings and 22 populated persistent owners pass real `MatchSaveJson` transport. An independent field-coverage probe inspected 61 model types with no omitted instance fields, accounting explicitly for backing properties/packed fields; it is diagnostic evidence, not a substitute for behavior tests.
- A deeper ActiveDefence regression mutates the live nested snapshot after capture, then verifies detached movement/routes, effects, AP/battle slots, heal predicate and equipment-consumption behavior on a fresh canonical owner.
- Turn-scoped disposal and continuation clocks were traced through `AiTurnSession.Dispose` and the pipeline. Full future AI-turn simulation was not executed.
- The real scene restore helper/coordinator and `ResumeGame` run twice against the same file after live mutations. Eliminated current-round slots, spent AP/movement/AA, hand/deck ordering, canonical references and no extra turn/deal remain checked.
- Temporary native adapters were inspected: they provide engine construction, field-only Vector3 construction, object-null semantics, assets/UI wiring and a temporary profile directory. No world/AI codec, graph validation, JSON transport, campaign-context validation or turn-restoration behavior is replaced.

## Executed verification after fixes

| Check | Result |
| --- | --- |
| Full production file/store/decode/stage/restore-helper/ResumeGame chain | 24 passed, including stale campaign plus 20 malformed-file cases |
| File/strict JSON and actual pending-card-operation predicate | 18 passed |
| AI/objective/owner/core-policy regressions | 25 passed |
| Carrier controller and relationship regressions | 3 passed |
| Actual AA consumer after world capture/JSON/decode | Passed for unit, building and facility |
| Actual frozen visual import and subsequent re-observation copy | 7 passed |
| Production world codec | 6 passed |
| Logical historical presentation codec | 17 passed |
| Existing in-memory restore integration/receipt chain | 11 passed |
| Python verifier regressions | 7 passed |
| All C# syntax | 951 files, zero errors |
| Baseline/current runtime and changed native fixture binding | Passed against cached Unity 2021 references/package adapters; 9 fixture files bind |

Existing owner/hand/derived presentation/collection suites also pass: 22 / 10 / 3 / 21 checks respectively. Both manifest YAML validators, generator idempotence, link.xml parsing, complete Asset metas/unique GUIDs and git diff --check pass. The patch is checked for applicability against the original source snapshot.

## Unexecuted checks and limits

Unity 6000.5.4f1 is unavailable. Full EditMode, real Game/MainMenu activation and input/cancel/errors, native clone callbacks/rendering/flag animation/fog, authoritative editor manifest rebuild and stripped/AOT player builds remain required. Managed construction adapters do not prove those paths. The added native fixtures bind but were not run in Unity. Future random outcomes remain nondeterministic because schema 1 intentionally does not persist global RNG state.

Changes are local behavioral corrections and validation, without a gameplay rebalance, package update or unrelated refactor. No commit, push or merge was performed. Diagnostic failing probes were kept outside the project deliverable; product regressions are in `Tools`/EditMode fixtures.

## Publication preparation

The user subsequently authorized commit, merge to master and push. Current upstream master is `c69082d6fb648af6cd33f8ff673e90ddb270e031` (four commits beyond the original source base). Its serialization fixes and mutagen display-name changes are preserved. HexCoord keeps upstream's serializable mutable fields plus the exact detached reconstruction constructor. World capture explicitly converts upstream SerializableNullableInt snapshots to nullable wire values; focused runner source lists include that existing upstream type. Integrated checks are rerun before publication; the original immutable audit baseline is retained.

Integrated publication checks passed:24 full-file,25 AI/core,6 world-codec,3 carrier/relationship,22 world-owner and11 in-memory integration scenarios. The integrated runtime and9 native fixture files bind;952 C# files parse. Full Unity execution remains unavailable.
