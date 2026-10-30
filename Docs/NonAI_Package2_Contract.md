# Non-AI package 2 — card, payment and production command contract

Implemented on 30 October 2026 from master `bc014c913ddc3d70a889eda79871a511a0c18213`, branch `refactor/non-ai-package-2`. This is an architectural change with the explicit consistency fixes listed below. No commit, push or merge was authorized for this implementation request.

## Package checklist

- [x] **2.1** Move paid draw and hand play orchestration from `CardHandUI` into application commands. Validate canonical owner/account/hand, exact RuntimeId **and object membership**, target, capacity and effective cost before mutation. Hex and modal adapters use the same command; UI no longer separately pays or removes a played/attached card.
- [x] **2.2** Introduce one AP/resource quote/debit primitive with explicit failure and an operation-owned, one-use compensation receipt. Migrate shared deployment, attachments, infrastructure, research/production and repair payments off void `PayFrom` orchestration.
- [x] **2.3** Extract the existing unit factory to `UnitData`, reuse `FacilityData.FromDefinition`, add common building materialization, and preserve authored/permanent/current state boundaries. Record attachment baselines before temporary combat effects; preserve Equipment → Mutator composition, additive → override, resource clamp/refill/spending history and consumed equipment semantics.
- [x] **2.4** Keep `ProductionAttempt` as reservation/release/mint owner; add completion with original-hand delivery and stale-root/hand rejection. Share research and extraction quote/validation with apply. Move extraction rules from selection to `InfrastructureActions`; retain `IncomeProjection` as the allocator.
- [x] Bottom-up caller/lifecycle review and one independent whole-branch review; confirmed findings corrected with the evidence below.
- [ ] Full Unity 6000.5.4f1 EditMode run, native import and interactive new/load/exit checks. Unity is unavailable in this environment.

## Application entrypoints for subsequent packages

| Operation | Contract |
|---|---|
| `CardHandCommands.TryDraw(owner, hand, apCost)` | Requires `owner.Hand` to be that exact hand and its registered root to belong to the owner. Rejects empty/full/invalid deck and unaffordable costs before drawing. Returns `CardCommandResult` with `Ok`, `FailReason`, `Card`, `Cost`. |
| `CardHandCommands.TryQuotePlay(owner, hand, card, target, map, out quote, out reason)` | Read-only validation of the exact held instance and live destination. Quotes are advisory and do not reserve funds. |
| `CardHandCommands.TryPlay(owner, hand, card, target, map, presentation)` | Revalidates and calls existing actions, then consumes the card inside one synchronous completed-state notification scope. Repeated/stale/foreign calls reject without payment, card consumption or world mutation. Presentation is optional. |
| `CardPlayTarget.AtHex / InArmy / InBuilding / OnCard / OnUnit` | Screen routing belongs to UI; target legality belongs to the command. Modal references must still be live canonical objects. |
| `PaymentQuote(ap, resources).CanDebit / TryDebit` | Copies resource amounts, rejects negative costs, checks AP and all resources together, then debits as one observable account mutation. `PaymentFailure` distinguishes invalid cost, missing account, AP and resources. |
| `PaymentReceipt.TryCompensate` (internal) | Available only to operation implementations. Refunds the actual copied debit once as a delta. A receipt is not a public refund entitlement. World compensation remains operation-specific. |
| `ResearchProductionSystem.TryQuoteAttempt / TryStartAttempt` | Same catalog, canonical account, actor, facility, blueprint and budget validation. Only start reserves quota and pays. A successfully started attempt remains paid on loss or cancellation. |
| `ProductionAttempt.CompleteToHand(success)` | Single terminal mint/release/delivery to the captured original hand. Rejects disposed/repeated completion and replacement account/hand. Produced output keeps permitted overflow. |
| `InfrastructureActions.CanBuildExtractionSite / TryBuildExtractionSiteOnMap` | Shared legality, marginal-income allocator, quote and final apply. Existing owned slots or a new resource site, exact builder movement/stealth consequences, completed notifications. |
| `UnitData.FromDefinition / Materialize`, `BuildingData.FromDefinition`, `FacilityData.FromDefinition` | Materialization is distinct from playing a held card. It neither authorizes a hand instance nor consumes one. |

A play command never calls an action and then pays it again. Payment remains inside the existing action owner; the command adds source authorization, common routing, consumption and the outer publication boundary. There are no async/yield points inside that boundary. Observers see the completed account/hand/world, and cannot cause a second commit of the same card.

Legacy public definition-level action signatures and `ResourceCost.PayFrom` remain compatible. They support scripted/older callers and are **not** hand-authorization APIs. New UI/application callers must use the table above and must not add their own pay/remove. `ProductionAttempt.Complete` retains its return-a-card contract for legacy callers; it shares the same terminal flag with `CompleteToHand`. This preserves excluded consumers without changing their policy or codec code.

## Preserved rules and intentional fixes

Ordinary, produced and returned-card provenance retains its original costs. Produced cards pay authored attempt cost once and activation AP on play; deployment RapidReaction can still make that AP zero. Granting RapidReaction to a host does not make the attachment card itself free. The effective-cost wrappers now agree for both ordinary and projected calls.

Ordinary hand capacity, explicit Library overflow and produced overflow retain their distinction. Paid production loss/cancel releases pending blueprint ownership without refund; consuming SingleUse gear neither pays nor refunds account resources. Infrastructure rejection compensates only its own operation, including the existing fuller founding checkpoint for a declined composed completion.

Confirmed consistency fixes:

- A hand rejects a different object with an already held RuntimeId.
- Repeated play, foreign owner, stale modal/card and changed budget/target/capacity reject before mutation.
- Incoming commander capacity uses the existing attachment projection, including both increases and reductions.
- First attachment after Drill/Berserk cannot capture temporary Attack/Defense as permanent baseline.
- Rebased Berserk Defense stays at its floor; the ledger retains only actual remaining loss, preventing phantom Defense at battle-end restoration. An unchanged authored zero-Defense stat is not newly floored by unrelated equipment.
- Selector disable/cancel disposes the attempt, tears down its popup and unlocks its picker. A captured old callback cannot mint or complete/unlock a replacement attempt.
- Extraction UI continues to display rejection outcomes and refresh the selected hex through its existing adapter. Post-commit visual restacking uses isolated completed notifications.

No existing save DTO/version, identity/highwater mechanism, authored key, scene, prefab, dependency or existing `.meta` GUID was changed. New script metadata is supplied. `Docs/` and `docs/` retain their separate identities. Excluded AI implementations, policies and special codecs were not investigated or modified; their runtime source was compatibility-compiled with the rest of the project.

## Verification and its limits

The managed checks compile the **complete actual runtime source** and selected real fixtures against legacy Unity reference assemblies. Native object creation/equality, framework API compatibility and deterministic integer RNG are adapted only in an external verification copy. These passes do not establish a build/import in Unity 6000.5.4f1, native coroutine/popup rendering or scene correctness.

- Initial duplicate-identity and temporary-baseline regressions: **2/2 failed before fixes**.
- Attachment-adjusted commander capacity: **2/2 failed before the shared projection fix**.
- Review regressions (attachment and consumption Defense floors; disabled production picker): **3/3 failed before fixes**.
- Ordinary/projected attachment-cost parity: **failed before the shared cost-wrapper fix**.
- Final combined runnable subset: **306/306**, including all **37 new cases** and the ten adapted existing R/P/loadout cases. The three named unchanged baseline failures are explicitly excluded from this passing subset, and remain visible in the differential broad run.
- Existing attachment/single-use plus command contract run after review fixes: **94/94** (before the final extra cost-parity case).
- Existing research-production/loadout subset with external native-construction adaptations: **10/10**.
- World/identity preparation runner: **22/22**. Its narrow adapter explicitly rejects attachment simulation; attachment/deployment tests use the actual implementation in the broader runner.
- Differential broad run: base **262 cases / 259 passed**, working **267 / 264 passed**, **zero regressions or missing previously passing cases**. The same three baseline failures are listed below.

The unchanged broad-run failures are:

1. `AttachmentSlotTests.AuthoredTwinSmgOverridesBeforeMutatorAttackAndPreservesWounds`: authored card asset absent from this checkout.
2. `AttachmentSlotTests.EveryPublishedAttachmentProjectsTheLiveResultWithWoundsAndEitherOtherSlot`: same missing authored content.
3. `ExplosionAttackTests.BlueprintChallengePriceAndAircraftDeploymentRemainSeparate`: expected `0.25390625`, actual `0.14453125` at both base and working tree.

The independent review found two Important defects and an extraction UI outcome regression; all were corrected in one implementation pass. The UI outcome finding was treated as functional, rather than deferred as polish. No review finding is knowingly left unfixed. The review did not judge native Unity behavior, excluded AI policies or the unchanged baseline failures; those limits remain explicit.

Run the full EditMode suite in Unity 6000.5.4f1, including both new `CardCommand*` fixtures and the required existing `PlayerHandStateTests`, `MatchLoadoutStateTests`, `ResearchProductionAttemptTransactionTests`, `AttachmentSlotTests`, `AttachmentLifecycleTests` and `SingleUseEquipmentTests`. Exercise hex and Army/Base-modal drops; production success/loss/disable/cancel; extraction success/failure and selected-hex refresh; new → exit → new; load → draw/play/produce → save/load; and exit with a pending production callback. Confirm original visuals and popup Fate restoration. These native checks remain unrun.

## Requested bottom-up recheck across packages 0, 1 and 2

The second pass reviewed the current affected owners and their non-AI callers, with independent read-only audits of duplication and world lifecycle. It found additional gaps; the earlier completion report did not cover them.

| Finding | Correction |
|---|---|
| Full carrier-sortie landing retires an original army during a founding completion that later declines | Restore its original registry entry as well as roster/hex/wing/sortie state. Restore its controller reference and pending flight-policy value. Defer original marker destruction through the existing completed-notification boundary, so declined scopes do not destroy it. |
| A stale positive-ID army could still be used by world commands | Mutation entrypoints now require the canonical registered objects for both transfer endpoints, reorder, activation, create-with-members, launch and all unload/board containers. Preview/query APIs remain usable for detached projections. |
| Starting-citadel setup repeated all authored Base field mapping | Setup now calls `BuildingData.FromDefinition`, then adds only its own name, marker and starting-citadel identity. Its missing-definition fallback, registration and hex bonus remain unchanged. |
| Extraction UI had a second eligibility calculation that ignored own unit collectors | The row calls `CanBuildExtractionSite`, which uses the same `IncomeProjection` marginal allocator as execution. The query can omit only the current-budget gate, preserving the existing visible-action/click-for-cost behavior. UI turn/contact interaction guards remain UI-owned. |
| Migration left unused internal forwarding layers | Remove `SpawnBuildingForFounding` and redundant `SpawnBuildingCore`; retain compatible public `SpawnBuilding`, `SpawnUnit`, `AddProducedCardToHand` and void payment adapters. |

### Duplication audit

No duplicate class or second authoritative implementation was found in the checked affected chains after the corrections. Compatible wrappers/overloads are distinguished from a second writer or formula:

| Package | Checked ownership chains |
|---|---|
| 0 | Catalog entries and nested-array copies → one `CopyEntry`; both editor writers → controlled `ReplaceEntries`; visible-memory consumer → existing `NonEmptyArmiesAt`; fuel casualties → shared removal/cleanup and final notification. |
| 1 | One army index; read-only/legacy query result shapes refer to it. Single/batch/exchange/swap → common transfer validator. Capacity → `RosterFits`/existing capacity functions. Selection lifetime adapters → `ArmyActions`; one mutation-notification owner. |
| 2 | Unit creation → `FromDefinition`/`Materialize`; selector spawning forwards. Starting and played bases → one Base materializer. Facility placement → `FacilityData.FromDefinition`. Resource adapters → `PaymentQuote`; effective costs → existing cost rules. Production completion/disposal → one terminal flag and quota release. Extraction preview/apply → one validator and allocator. |

Class names and public compatibility surfaces were also checked by compiling all runtime sources. This is not a claim of an exhaustive new audit of unrelated code or excluded AI implementations. The original package-0 parent commit object is unavailable in this local checkout; its current implementation, consumers and existing regressions were checked, but its full original diff was not reconstructed.

### Recheck regression evidence

- Initial recheck: **4 expected failures / 6 cases**, covering stale transfer endpoints, lost registry entry after rejected full landing, and the collector-sensitive extraction preview; the two valid/budget-preview characterizations passed. After fixes: **6/6**.
- Activation/reorder/launch and stale unload/board containers: **6/6 failed before their commit guards**. A stale create-with-members source was independently reproduced by removing only its guard in the external verification copy.
- Enhanced full-landing rollback test reproduced the cleared pending flight-policy field before it was added to the existing checkpoint.
- Catalog regression suite from package 0: **11/11** with external native-object adapters. World/identity preparation: **22/22**.
- Final expanded passing subset: **319/319** (pre-recheck **306/306**, **13 additional cases**, no missing/regressed previously passing case). Broad run: **269 cases / 266 passed**, retaining exactly the same three named baseline failures above. The original master baseline’s **259 passing cases** also remain passing. All runtime sources and selected native fixtures compatibility-compile against the legacy references; full Unity compilation is still unverified.

Native tests were added for starting-citadel authoring and retired-wing controller rollback. They are compiled with the real runtime but remain unrun without Unity, together with the existing native suite. In particular, actual delayed marker destruction and new/load/exit rendering still require editor verification. No existing scenes, prefabs, assets, `.meta` GUIDs, dependencies or save schemas were edited by this recheck.
