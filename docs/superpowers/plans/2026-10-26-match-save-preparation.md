# Match save preparation implementation plan

> Execute inline with isolated owners for map, hand, rewards and world/AI state; review the integrated result.

**Goal:** Prepare existing gameplay owners for exact restoration at a settled human turn without enabling disk save/load yet.

**Architecture:** Preserve authoritative state in its current owner. Extract the human hand into a shared model; retain generated map layout; expose typed restoration boundaries. Separate new-game effects from resumed presentation. A future file codec validates and constructs a prepared restore source before scene activation.

**Spec:** User-approved analysis in the current conversation: own confirmed turn only, no battle or pending operations; no architecture rewrite or duplicate authorities.

**Constraints:** Unity 6000.5.4f1; preserve GUIDs, no dependency updates; profile/campaign are independent; no save UI/disk slots in this stage. Runtime adapters belong only in Tools/tests. No commits/push requested.

**Review focus:** startup ordering; no repeat income/movement/AP; hidden knowledge; permanent reward replay; invalid input must be rejected before destructive reset.

- [x] Task 1: common capture eligibility, nested silent-restore scope, stable turn position DTO and owner resume API. Tests: every blocking condition, pending delayed battles allowed, nesting and exception cleanup, duplicate/unknown player order rejection.
- [x] Task 2: shared hand/deck model, detached authored-key card state and loadout export. Tests: random-free restore, order, acquired-turn/prepayment flags, overflow, AI compatibility.
- [x] Task 3: value-only logical/visual map layout and shared rendering pipeline. Tests: copy ownership, exact complex orientation/texture placement, invalid layout rejection; YAML validators on key migration.
- [x] Task 4: typed world and knowledge owner capture/restore access, activation ledger, movement, AA, stealth and identity highwater. Tests: spent state survives, no registration side effects, ID reuse avoided.
- [x] Task 5: durable event reward receipts and campaign restore identity validation. Tests: replay across process/store reload, exhausted receipt, profile migration, cross-campaign/result rejection.
- [x] Task 6: stage a prevalidated restore source in GameSession; skip new setup/deal/generation; restore world then memory then presentation without calling new-turn lifecycle. Preserve normal new game path. Integrate AI persistent boundaries and document codec obligations.
- [x] Task 7: syntax/binding checks available locally, focused runtime regressions, bottom-up review, diff/meta/YAML checks. Explicitly report Unity checks unavailable.

## Execution ledger

- Base: remote master e769664eb8398eea80e94300345bcbbde87f5b0d; verified local C# sources from analysis match remote git blob IDs.
- Environment: isolated local source checkout, no remote mutation. .NET SDK available; Unity reference assemblies/editor and Mono absent. Standard ai-verify cannot establish a real baseline here (its shell pipeline can misleadingly report zero when dotnet is missing). Use direct Roslyn, focused runtime harnesses and native EditMode tests for later editor run.
- Scope decision: concrete save file serialization and full entity/AI DTO codecs are the next stage. Preparation exposes owner boundaries and retains facts; it does not create an incomplete disk format or activate Save/Load.

- Completed: Tasks 1–6 implemented in current owners; save/load buttons remain disabled. Concrete disk/entity/appearance/objective codecs remain the explicitly separate file-feature stage.
- Review: independent bottom-up review identified missing last-seen presentation import. Added typed frozen appearance capture/import, complete coverage validation, and four native EditMode cases (binding passed; execution requires Unity). Root also closed retained private AI-card discovery through `includeReferencedCard` after an expected missing-API RED, then GREEN.
- Shared helper review: reused the existing CollectionProfile.CopyDeck boundary for loadouts instead of retaining a second deck-copy implementation.
- Verification: 61 terrain + 10 hand + 21 world + 21 collection + 8 AI + 9 core/campaign = 130 managed checks passed. Policy/turn command checks passed separately. All 922 Assets C# files parse; baseline/current runtime sources bind successfully with cached Unity 2021/package adapters; ten changed/new EditMode fixture files bind. DeckCollectionTests depends on native Editor builders and is deferred to the actual editor. Both asset YAML validators and semantic old/new identity-only comparisons passed, new C# metadata complete, git diff --check clean.
- Native validation remaining: Unity 6000.5.4f1 full EditMode and loaded-scene restore/UI/JsonUtility scenarios. Managed adapters do not establish native Unity correctness.
- Implementation finished uncommitted. The user subsequently requested commit, merge into master and push; publication is handled from the validated upstream tree, preserving existing Unity metadata.
