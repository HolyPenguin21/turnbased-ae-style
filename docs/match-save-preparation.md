# Match save preparation

Preparation for settled local-human-turn saves. Save/Load buttons and file slots remain disabled. No external packages or gameplay balance are changed.

## Existing owners and new boundaries

| Owner | Prepared boundary |
| --- | --- |
| GameTurnController | `CanSaveMatch`, `CaptureTurnState`, `ResumeGame` |
| GameSession | `TryPrepareRestore`; a validated `IMatchRestoreSource` is staged before Game scene activation |
| HexMap / HexMapGenerator | retained copied `HexMapLayout`; `ValidateLayout` / `BuildFromLayout`; common rendering path |
| PlayerHandData / AiHandData | shared one-use deck/hand model; authored-key `HandState` / `CardState`; no random deal on restore |
| MatchLoadout / BlueprintQuota | frozen match composition and blueprint facts independent of current profile deck |
| ArmyData / UnitData | activation paid-unit ledger; raw spent movement and transport/carrier links |
| PlayerRoot | economy and ordered initiative contributions restored without purchasing or notifying |
| AntiAirState / StealthSystem | per-owner spent reaction facts, facility gunner identities, detection expiries and notices |
| VisionSystem / HumanVisualMemory | current/visited/ever-seen tiers, frozen historical army observations |
| HexSelectionController | frozen remembered building/army appearance capture/import, both army indices, exact memory coverage validation |
| DelayedBattleRegistry | queued future fights with participant order; these are permitted at a save point |
| HexResourceBonusRegistry / HexEventRegistry | detached typed export/import, explicit event codec callback, no reward/discovery side effects |
| AI persistent owners | explicit typed capture/restore, canonical PlayerId rebinding, historical snapshots and continuation clocks, mandatory tagged mission objective codec |
| CollectionService | permanent Library reward receipt `(MatchId, Q:R, exact authored card key)` in the same profile transaction as its grant |
| CampaignMatchBridge | reject a tactical save whose manual campaign operation is missing, different or already resolved |
| MatchIdentityState | issued Army/Unit/Card ID highwater, including remembered/destroyed entities |

`MatchRestoreScope` suppresses gameplay registration effects while the graph is incomplete. It is synchronous, nestable and does not learn visibility when disposed. Normal new-game setup retains its existing lifecycle.

## Required order for the file feature

1. Decode an entire detached graph; do not register entities while parsing. Validate schema/content compatibility, full references, unique canonical player/entity IDs, finite numeric values, current human-turn position and all settled-operation invariants before replacing a match. Prepare the human `Hand` before the Game scene opens.
2. Construct an immutable-after-staging `IMatchRestoreSource` and call `GameSession.TryPrepareRestore(source, config, out error)`. This validates map content keys and the pending campaign operation without running a new match or changing its identity.
3. Enter the Game scene. The existing early setup reset clears outgoing static holders; generation reads `PendingRestore.Map`, while human hand Awake uses the already-restored model. No new citadel placement, post-generation contents or opening deal.
4. `RestoreWorld(map)` runs in a quiet scope. Reserve `MatchIdentityState` first. Build entity shells/membership, use restored identities and restore owner facts. Supply all units retained only by activation/AA/stealth ledgers to reference resolvers. Pass the AI capture `includeReferencedCard` callback to retain cards held only by private Economy/Development evidence, even after they left the live hand. Restore carrier-container links and call `RebindRestoredTransportRecipients` after unit transport links; do not invoke normal automatic carrier creation during reconstruction.
5. `RestoreKnowledge()` restores fog, stealth, human observations and AI history after references exist. Do not replace historical observations with scans of the current world. Do not reroll detection or use new-turn refill APIs.
6. `RestorePresentation(selection)` imports complete frozen historical appearances. Building appearances must come from their original remembered clone, even if the real building was captured/destroyed while hidden. The coordinator rejects missing remembered visual coverage. Do not look up hidden live entities to rebuild old appearances.
7. `ResumeGame(Turn)` restores exact round/order/index/completed-owner-turn counters and binds UI. It does not call `BeginGame`, `BeginNewTurn`, `BeginPlayerTurn`, income/AP allocation, move/AA refill or greeting/reaction confirmation. Setup re-enables map input only after successful reconstruction.

After any scene-stage failure the incomplete world is discarded and the main menu receives an error. Profile/campaign persistence is never rolled back together with the tactical world.

## What belongs to the actual save feature

The prepared source is a contract, not a partially implemented disk serializer. Next work implements the concrete entity/event/frozen-appearance codecs and tagged codecs for all six mission objective types, a versioned file envelope/content compatibility policy, slot persistence with the existing `AtomicJsonFile`, and UI integration. Include identity highwater and runtime attachment snapshots; do not reconstruct mutated unit stats by blindly reapplying equipment.

The entire generated logical/visual map layout is retained (terrain/texture/template keys, complex part order/rotation, scale, border, colors, animation starting phase). Elapsed cosmetic animation time is not tracked. The future format must decide RNG continuation explicitly; this preparation guarantees that restoring hand/map itself does not perform a new random deal/generation.

New authored terrain/template/texture keys are migrated in `GameConfig.asset`; event definition keys are migrated in `EventCatalog.asset`. Names/list indices are not the persistent identity. Future balance/content incompatibility must be detected before restoring; the current stage does not promise old-save compatibility across arbitrary changes.

Collection profile schema 1 migrates to 2 while retaining owned cards/decks; schema 2 requires the durable event receipt ledger. The existing profile filename is retained. An exhausted event also receives a receipt; retries never select a new card. Existing final-match reward receipts remain independent. A recovered profile backup rolls ownership and its matching receipts back together.

## Verification

Use an installed .NET 8 SDK and NUnit 3.14.0. Paths below are environment inputs, not project dependencies:

```sh
export DOTNET_BIN=/path/to/dotnet
export DOTNET="$DOTNET_BIN"
export DOTNET_HOST_PATH="$DOTNET_BIN"
export NUGET_PACKAGES=/path/to/nuget/cache
export NUNIT_DLL="$NUGET_PACKAGES/nunit/3.14.0/lib/netstandard2.0/nunit.framework.dll"
python Tools/save-preparation-verify/run.py --binding --packages "$NUGET_PACKAGES"
python Tools/save-preparation-verify/hand/run.py
python Tools/save-preparation-verify/world/run.py
python Tools/save-preparation-verify/ai/run.py
python Tools/collection-verify/run.py
python Tools/terrain-verify/run_managed.py --dotnet "$DOTNET_BIN" --nunit "$NUNIT_DLL"
node Tools/unity-yaml-verify/verify_full.js Assets/Config/GameConfig.asset Assets/Cards/EventCatalog.asset
node Tools/unity-yaml-verify/verify_types.js Assets/Config/GameConfig.asset Assets/Cards/EventCatalog.asset
```

Managed harnesses use explicit testing adapters outside Assets. Binding compares the baseline and runtime sources against cached old Unity references; it is not a Unity 6000.5.4f1 build. `DeckCollectionTests` needs native Editor builders and is excluded from the managed changed-fixture binding pass. Full EditMode, scene/input/renderer startup, native JsonUtility migration, and real restore-without-refill/last-seen marker scenarios still require the project's Unity editor.

Latest managed result: **130 checks passed** (61 terrain, 10 hand, 21 world, 21 collection, 8 AI and 9 core/campaign). Separate policy/turn checks passed. All 922 Assets C# files parse; runtime baseline/current and 10 changed/new EditMode fixture files bind with the documented adapters. Native Unity tests are not included in these claims.
