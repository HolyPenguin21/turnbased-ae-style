# Package 1: world, roster and container operations

Base: `df2341b08cad6053c4fc266b09fb9c232e01ca2c` (package 0 merged), 29 October 2026.
Branch: `refactor/non-ai-package1`. The user authorized commit, merge into master and push on 30 October 2026.
Scope: package 1, requirements 1.1–1.4. AI decision logic and special AI codecs were not investigated or modified.

## Implementation checklist

- [x] **1.1** Protected army-by-hex query, isolated empty results, controlled live position/ownership, canonical registration checks, and silent restore compatibility.
- [x] **1.2** Single/batch/exchange/swap share projected-roster validation. Reorder preview and commit use the same rule. Split/launch/preparation/deployment/activation reject display snapshots at commit.
- [x] **1.3** Building capture/destruction and aircraft/carrier/empty-army cleanup complete without selection. The match hand owns returned cards; presentation no longer grants them.
- [x] **1.4** Synchronous notification batches cover registry/roster, construction, deployment, activation, battle finalization and initial player placement. Compensation discards success notifications. Prior capture witnesses retain their observation window.
- [x] Bottom-up caller review, managed runtime compilation, differential regression check, preparation regressions, and independent whole-change review plus the user-requested second bottom-up pass with fixes.
- [ ] Full Unity 6000.5.4f1 EditMode suite and native new/load/exit/presentation scenarios. No Unity editor was available in this environment.

## Query and command contract

| Concern | Boundary |
|---|---|
| Read armies by hex | `ArmyRegistry.At`: detached read-only list of canonical army references |
| Existing List callers | `AllAt`: detached List; edits cannot change registry storage or another empty query |
| Read roster | `ArmyData.Roster`: read-only view, with canonical unit references |
| Position | `ArmyRegistry.MoveArmy`; live `ArmyData.Hex` assignments forward to it |
| Ownership | `ArmyActions.ChangeOwner`; live `ArmyData.Owner` assignments forward to it |
| Membership transfer | `ArmyActions.TransferMember`, `TransferMembersAtomic`, `SwapMembers`, `CreateArmyWithMembers` |
| Order | `CanReorderMembers` / `ReorderMembers`; `TryReorderCommander` forwards for live armies |
| Casualties/prison | `RemoveMember(s)`, `ClearMembers`, and the engine's exceptional prisoner operation |
| Empty lifetime | `ArmyActions.DeleteArmyIfEmptied`; selection's previous entrypoint is a forwarding adapter |
| Capture/destruction | `BuildingRegistry.CaptureOrDestroy` / `Unregister`, including stale aviation and empty-container cleanup |
| Carrier lifetime | Existing `AviationActions` owns hangar/deck creation, relocation and aircraft return |

`Members` retains its List representation for compatibility with excluded consumers and existing codecs. Covered gameplay callers no longer reorder/clear/remove directly from UI or casualty presentation. Model/action implementations and detached construction/restore still use that representation. Subsequent packages must call the command boundary rather than introduce another direct gameplay writer. This is a compatibility bridge, not an assembly-level write prohibition; final assembly boundaries belong to package 8.

Registration rejects display armies, projection units, duplicate army IDs, duplicate member references and duplicate live unit IDs before changing the index. Read-only projections remain usable by `Can*` queries and detached model calculations; commands that materialize live state reject them.

The common final-roster check includes ownership, duplicate/prisoner restrictions, capacity, hero order and container composition. Existing activation coverage and aircraft sortie payment semantics are preserved. Transfers additionally check both storage-capacity deltas and pay the combined activation cost before any observer runs.

## Completed operation notifications

A short `VisionSystem.BeginMutation` scope batches existing recompute/content helpers and operation receipts. It is synchronous, does not span animations or yield points, and does not implement state rollback.

The outer scope publishes in this order:

1. Final capture/destruction observation while legitimate prior viewers still have their old vision.
2. Owner vision recomputation.
3. One content notification per affected hex, to viewers who can currently see it.
4. Completed hand/resource/stealth/landing/deployment/relocation/removal/encounter receipts.

World ownership, roster, transport, carrier links and hand membership are already final before this publication. The pre-vision transition is not moved behind recomputation: doing so would deprive the former owner of a witnessed capture. Hidden strangers are not added as witnesses. Human building-presence knowledge is updated on the shared side; optional scene subscribers continue to own frozen visual appearance.

Each subscriber is isolated. A failing subscriber is logged, cannot prevent later subscribers receiving the result, and cannot turn a committed command into a thrown failure that leaves the source card unconsumed. A world reset changes the notification epoch and cancels remaining old-world callbacks. `ArmyRemoved` lets selection clear a deleted army; re-enabling selection also checks its retained reference against the registry.

A pending multi-target explosion retains its existing deferred carrier cleanup. It does not publish the unfinished casualty/container state; completion returns cargo and publishes the finished state. Repeated completion is a no-op. Landing and subsequent fuel damage remain separate completed operations and retain their two notification phases.

## Compensation and lifecycle

Founding uses an internal construction primitive that postpones new-building detection until completion accepts. Public `SpawnBuilding` retains its existing immediate completed behavior. A declined completion restores canonical source/recipient rosters, activation coverage, transport references, account/resources, existing container positions, wing/sortie state and detection knowledge/notices, removes newly created containers, restores the previous structure, and discards queued success facts. Same-owner roster snapshots include sources on other indexed hexes; compensation does not reroll detection or serialize through authored/player IDs.

The source-card removal boundary stays with the existing card caller after the successful operation result. Independent production/hand/quota workflows belong to package 2; arbitrary unrelated gameplay inside the deterministic founding callback is not a general world transaction.

Permanent prisons and carrier containers retain their special lifetime. Fixed airfields/garrisons and ordinary empty shells on an owned Barracks keep the existing retention rule; an emptied travelling army or returned carrier sortie follows its existing removal rule. Gameplay deletion no longer requires selection to be enabled or present. The removal receipt and marker cancellation concern presentation only.

Returning aircraft retain their originating definition, Equipment and Mutator. The consumed first-play production concession is not restored. **The existing ordinary-card hand capacity is preserved:** a full hand still rejects a returned ordinary card. A successful-return event is now emitted only when the hand actually accepts the card. Repeat return/removal cannot grant a second card.

## Independent review and decisions

One independent read-only review found Important issues; the implementation fixes them:

- Committed notification failures could leave a spawned object plus its source card: isolated/logged handlers, no post-commit observer exception outcome.
- Declined founding could retain stealth detection/notices: checks postponed and a narrow stealth-owned compensation checkpoint added.
- Creation payment, activation and encounter finalization could publish intermediate state: outer scopes added.
- Split/launch/wing preparation could materialize display snapshots: commit-only identity guards added.
- Domain deletion bypassed old selection cleanup: removal receipt and re-enable reconciliation added.

Additional regressions cover duplicate identities/projection units, pending explosion cleanup, completed encounter cleanup, and false successful-return facts for a full hand.

Decisions retained from the specification: excluded AI policies/codecs are not refactored; compatibility Members representation and the capped hand policy remain; remaining Library/hex-event reward orchestration is not claimed as a UI-independent rewrite by this package. Their guard registration/cleanup changes are included. Existing authored assets, scenes, prefabs, `.meta` files, save DTOs/versions, public forwarding entrypoints and identity highwater policy are preserved.

## Verification

The final managed checks compile the actual complete runtime source and the selected real fixtures against legacy Unity reference assemblies, with framework/native API adaptations only in an external verification copy. They do not establish a Unity build, native import, serialization or rendering correctness.

- `WorldMutationContractTests`: **34 cases** covering query protection, common preview restrictions, capture/destruction without selection, attachments, carrier death, witness visibility, canonical identities, notification failure isolation/reset, snapshot rejection, detection compensation, explosion/finalization boundaries, and capped return receipts.
- Differential broad run against the base: **227 cases at the working tree, 224 passing; zero regressions** from the base's 193 cases / 190 passing. All 34 new cases pass.
- Explicit runnable subset excluding the three unchanged baseline failures: **224/224**.
- `python3 Tools/save-preparation-verify/world/run.py`: **22 checks, zero failures**. Its adapters explicitly reject unsupported gameplay commands; the command contracts above use the actual implementations in the broader runner.
- Independent review and `git diff --check` completed. Existing tracked metadata, scenes, prefab and save schema files have no changes.

Three unchanged baseline failures were not hidden or repaired as unrelated work:

1. `AttachmentSlotTests.AuthoredTwinSmgOverridesBeforeMutatorAttackAndPreservesWounds`: required authored card asset is absent from this checkout.
2. `AttachmentSlotTests.EveryPublishedAttachmentProjectsTheLiveResultWithWoundsAndEitherOtherSlot`: same missing authored content.
3. `ExplosionAttackTests.BlueprintChallengePriceAndAircraftDeploymentRemainSeparate`: existing expected `0.25390625`, actual `0.14453125` at both base and working tree.

Native-bound cases in existing fixtures were not treated as managed passes. `WorldMutationUnityTests` adds **21 native cases** for real construction/compensation (including new carriers and a source on another hex), final payment/garrison observation, detection rollback and successful detection, source-card consumption after an observer failure, split rejection, enabled/disabled selection capture, and selection cleanup/re-enable. These cases require Unity and remain unrun.

Before publication in the editor: run the full EditMode suite, including `VisibleHexMutationNotificationTests`, `VisionContainerTests`, `CarrierAirfieldTests`, `HexRegistryStatePreparationTests`, `RuntimeIdentityRestoreTests`, both new fixtures and existing movement lifecycle tests. Exercise new → exit → new, load → capture/carrier loss → save/load, moving-army death/cancel, and enabling/disabling selection. Verify the existing hand-cap behavior and unchanged authored appearance. No native success is claimed here.

## User-requested bottom-up recheck, 30 October 2026

The earlier completion statement was too broad. The second pass traced registry/model → commands → gameplay callers → observers and lifecycle, with a separate read-only reviewer checking aviation/combat/selection. It found and corrected these remaining gaps:

| Gap | Correction and regression evidence |
|---|---|
| Duplicate RuntimeIds inside one new army passed registration | Reject before index mutation; managed rejection test |
| A batch received the source's own Members list and invalidated its enumeration mid-transfer | Snapshot command input before writes for transfer, split and unload/board; two managed cases, native split case |
| Prison release still wrote membership from the turn controller | ArmyActions owns the exceptional release and final roster/transport/vision publication; managed final-state and capacity-preservation cases |
| Summon insertion bypassed the command boundary and published no roster change | Narrow capacity-exempt summon command; engine batches roster and tactical-grid placement; managed observer test |
| End-turn empty-shell sweep required selection | Turn controller calls ArmyActions directly; old public selection entrypoint forwards; managed lifetime case and native end-turn integration case |
| Reusing a registered carrier deck published no change | Successful formation notifies after both rosters are final; managed observation case |
| A distant aircraft casualty was announced at its still-indexed origin | Moving casualty content/stealth publication waits for the final relocation; native case |
| Aviation callbacks could publish a spent launcher or empty target before both sides finished cleanup | Synchronous scope includes attack, casualty removal and stationary empty-army cleanup; native single-use strike case |
| Deployment and activation could commit against display snapshots | Commit-only guards preserve preview paths; managed activation case and native deployment case |
| Declined founding restored rosters but could leave carrier containers relocated or a landing's sortie reset | Restore original indexed placement, wing flags and aircraft sortie/fuel flags silently; two native integration cases |
| Assertions inside caught callbacks could give weak test evidence | New contract observers capture state and assert after the operation; founding compensation tests assert callback success outside its exception boundary |

Verification: 227 broad managed cases / 224 passing / the same three named baseline failures above; exact base df2341b: 193 / 190 / the same three failures. No previously passing baseline case became failing or missing. Explicit runnable subset: 224/224, exit 0; all 34 new managed contract cases pass. World/identity preparation: 22/22, exit 0. git diff --check passes.

Six newly discovered defects were observed failing before fixes; the activation snapshot case also failed separately before its guard. Native cases are compatibility-compiled only. The full Unity 6000.5.4f1 EditMode suite and all 21 native cases remain unrun, especially the moving-casualty, turn-without-selection, attack callback and extended founding compensation scenarios. Broad human visual-memory redesign and decision/popup cancellation remain later packages. Existing tracked metadata, scenes/prefabs, save schemas, dependencies and excluded AI files are unchanged.
