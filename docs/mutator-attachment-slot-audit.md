# Independent Mutator slot — implementation audit

## Source and scope

Repository: HolyPenguin21/turnbased-ae-style. Analysis and fixed behavioral baseline: `4837e23b80821a914b65f80b7e443c3af0ab83b6`. The feature branch incorporates the later `941d17ebf51a14feae418395d86db420a499a172` master update (Unity lookup API replacements in unrelated menu/audio files). Master is not merged into or modified by this feature.

Scope: a behavior extension at the permanent attachment layer, with corresponding state propagation. No second research/production or mutation system. Both definitions remain `CardType.Equipment`; `CardDefinition.attachmentSlot` is the sole destination discriminator. Its zero/default value is Equipment, requiring no existing asset migration. CardData and UnitData each keep explicit independent Equipment and Mutator references.

## Architecture and runtime resources

EquipmentSystem owns compatibility, destination-slot occupancy, the existing payment transaction, effect arithmetic, canonical projection, installation and live observation publication. Mutator adds a mandatory Bio restriction to the existing hostKinds/hostTypeTags restrictions; Hero identity alone never grants Bio.

The original single-grant Apply/Predict implementation remains the grant interpreter. Existing one-Equipment installation still runs the exact old Apply, including floors, RapidReaction and current/max behavior. Two permanent grants compose from a captured pre-attachment stat/ability snapshot in Equipment → Mutator order. Replacement of the permanent contribution preserves deltas already present in the live fields, temporary ability changes, Berserk counters, stealth flag, movement/aviation state and other runtime fields.

Current HP/Move/Fate are not rebuilt from a fresh unit. Consumption is retained across intermediate maximum reductions/overrides, with later real damage/spending or repair/refill reconciled from the live current fields. This prevents installation-order-dependent replenishment for conflicting maximum grants. A living unit retains the existing minimum 1 HP; no zero-HP body is revived by a two-slot rebuild. New permanent maximum increases can grant corresponding current capacity while retaining the amount already consumed. Resource state is runtime-only, alongside the existing UnitData runtime state; future save/load remains a separate task.

CardUI and AI card/materialization projections consume EquipmentSystem.Project. AI formulas retain the same weights, clamps, matchup thresholds and WorthIt simulations; their inputs include both slots and canonical insertion of a proposed attachment. Current Fate remains the evaluator's input contract, so already spent Fate is not counted as an upgrade.

## Existing pipeline confirmation

| Area | Result |
| --- | --- |
| ResearchProductionSystem | Unmodified, including MintCard and ResearchProductionCreated |
| ResearchProductionCatalog | Unmodified mechanism and content |
| Research/Production Challenge | Unmodified |
| Payment fields/AP/resources | CardData.EffectivePlayApCost / EffectivePlayResourceCost and PayCost unchanged |
| Resource Bank | No changed bank/reservation classes, no separate Mutator reservation or transaction |
| Materialization execution | Existing attach/mint/consume/deploy route; no extra chain, capability or generation system |
| Equipment evaluation | Existing valuation formulas preserved; technical slot/projection inputs extended |
| WorthIt / battle kernel | Unmodified |
| Development / Production scoring | Existing formulas and balance retained; readiness recognizes a free compatible Mutator slot only if catalog content offers one |
| CardType | Unmodified |
| Real card assets / catalog content | Unmodified; all new cards exist only as synthetic test definitions |

An ordinary attachment still spends its normal AP and ResourceCost once. A produced attachment still spends activation AP and no ResourceCost at attach, because creation has already paid the resource stake. Failed slot/compatibility checks occur before payment. Existing MaterializationReservation/Bank cost extraction reads the same CardData instance costs. Both slots pass through the same transaction. EquipmentReserve is an AI estimate of held upgrades, not a Resource Bank: its existing matching formula now matches independent host slots so one slot does not consume the other.

## State and cache audit

- Live installation writes the effective fields before publishing the existing VisionSystem.RecomputeFor and NotifyContentChanged notifications. Both overloads share that publication path.
- Army observation snapshots read the resulting live stats/abilities; no separate Mutator observer/cache is introduced.
- Frozen SelfSnapshot.PoolCards now captures both attachment references, so combat-opportunity analysis does not read a later live hand to reconstruct the second slot.
- Capability/trait projection, force/readiness power, operator Fate/roles, economy ability checks, refit projection, and materialization diagnostics read both attachments.
- Successful execution retains the existing hand-consumption and mutation-version boundary. The Aggression admission hand fingerprint includes the Mutator name as well as Equipment.
- Project/PredictAttachment allocate read-only views of fresh projected data and do not attach to real cards or change their live fields.
- Aircraft return creates the same ordinary CardData as before, preserving both attachments and intentionally leaving ResearchProductionCreated false.

## UI surfaces

| Surface | Work |
| --- | --- |
| Hand, including restored/debug hands | CardUI has serialized mutatorArtToggle and both indicators bind to CardData; shared effective stats/abilities |
| Army Viewer cards, garrisons and read-only viewers | ArmyUnitCardUI binds both indicators; Card_Army placeholder wired |
| Army detail panel | Adds Mutator name alongside Equipment; stats/abilities already read effective UnitData |
| Battle grid | BattleGridCellUI binds both indicators; BattleGridCell placeholder wired; no battle attachment action |
| Battle detail and attack/challenge combatant rows | Already display effective live stats/abilities, no separate attachment renderer or copy to change |
| Research/Production catalog cards and detail preview | Reuse ArmyUnitCardUI.SetupPreview / EquipmentCardText; both host indicators cleared in preview; Mutator face states mandatory Bio restriction |
| Battle result popup | Text/OK result summary only; no attachment-bearing card surface |
| Battle turn-order / tactical grid clones | Reuse live unit identity/effective data; no UnitData attachment clone to extend |
| Building/Base/Facility/Tactic surfaces | Not valid Unit/Hero attachment hosts; no extra placeholders |
| Scenes | No independent Equipment toggle instances or serialized owner fields found outside the three prefabs; scene references inherit prefab changes |

The existing EquipmentArtToggle class and .meta GUID are retained. Two instances of the same renderer preview either attachment; no copied Mutator renderer/formatter. Peers revert the previous preview before switching, and pointer exit/disable restores the host. Rebinding restores previews before writing new host values. Placeholders use the current Equipment sprite, a temporary 25-pixel vertical offset, and start hidden. Geometry, anchors, art and polish remain for the owner.

## Files and ownership

| File | Change and responsible layer |
| --- | --- |
| `Assets/Scripts/Cards/EquipmentGrant.cs` | Defines AttachmentSlot enum alongside the existing grant types. |
| `Assets/Scripts/Cards/CardDefinition.cs` | Owns the serialized slot discriminator with Equipment default. |
| `Assets/Scripts/Cards/CardData.cs` | Carries the in-hand Mutator reference. |
| `Assets/Scripts/Units/UnitData.cs` | Carries the live Mutator reference and private runtime attachment snapshots/resource usage. |
| `Assets/Scripts/Cards/EquipmentSystem.cs` | Owns slot validation, canonical grant composition, runtime preservation, projection and the shared transaction. |
| `Assets/Scripts/Map/ArmyActions.cs` | Transfers both slots from sourceCard at the authoritative spawn boundary; legacy Equipment argument remains fallback. |
| `Assets/Scripts/Aviation/AviationActions.cs` | Transfers both slots back to CardData at the return boundary. |
| `Assets/Scripts/UI/CardHandUI.cs` | Uses one pending attachment mode and existing cancellation/ownership/payment routing. |
| `Assets/Scripts/UI/CardUI.cs` | Binds both indicators and delegates effective stats/abilities to gameplay projection. |
| `Assets/Scripts/UI/ArmyUnitCardUI.cs` | Binds both indicators; clears them in definition preview; safely restores old preview before rebind. |
| `Assets/Scripts/UI/BattleGridCellUI.cs` | Binds both preview-only indicators and restores them on exit/rebind. |
| `Assets/Scripts/UI/EquipmentArtToggle.cs` | Shared peer-aware preview renderer; retains class/GUID and serialized compatibility. |
| `Assets/Scripts/UI/EquipmentCardText.cs` | Shared attachment formatter states mandatory Bio for future Mutator cards. |
| `Assets/Scripts/UI/ArmyViewerModalUI.cs` | Owns the added Mutator detail label. |
| `Assets/Prefabs/UI/Card_Hand.prefab` | Wires a second independent renderer placeholder to CardUI. |
| `Assets/Prefabs/UI/Card_Army.prefab` | Wires a second independent renderer placeholder to ArmyUnitCardUI. |
| `Assets/Prefabs/UI/BattleScreen/BattleGridCell.prefab` | Wires a second independent renderer placeholder to BattleGridCellUI. |
| `Assets/Scripts/Ai/V2/Materialization/MaterializationChainMatching.cs` | Shared capability ability read includes both slots. |
| `Assets/Scripts/Ai/V2/Materialization/MaterializationChainEnumerator.cs` | Slot-aware legality and canonical final abilities for existing/generated attach chains. |
| `Assets/Scripts/Ai/V2/Evaluation/Power/AiPower.cs` | Owns power mapping from the gameplay-owned combined card/materialization projection. |
| `Assets/Scripts/Ai/V2/Evaluation/Cards/StrategicCardEvaluator.cs` | Feeds canonical before/after state into unchanged valuation/matchup formulas and preserves current-Fate semantics. |
| `Assets/Scripts/Ai/V2/Evaluation/Cards/NonCombatCardPlayer.cs` | Recipient prefilter checks the proposed attachment slot, retaining CanAttach and scoring. |
| `Assets/Scripts/Ai/V2/Analysis/WorldSnapshot.cs` | Frozen pool tuple gains the Mutator reference. |
| `Assets/Scripts/Ai/V2/Analysis/WorldAnalysis.Self.cs` | Freezes both references and matches reserve items to independent host slots. |
| `Assets/Scripts/Ai/V2/Analysis/CombatOpportunityAnalyzer.cs` | Reads both frozen slots into existing defender-profile projection. |
| `Assets/Scripts/Ai/V2/Analysis/WorldAnalysis.Development.cs` | Reads combined operator abilities; compatible authored Mutators can keep an equipped host eligible. |
| `Assets/Scripts/Ai/V2/Strategy/Objectives/DevelopmentOpportunityEvaluator.cs` | Projects combined operator Fate/abilities without changing success-chance or scoring formulas. |
| `Assets/Scripts/Ai/V2/Strategy/Objectives/CapabilityQualityEvaluator.cs` | Uses canonical combined projection for Mutator cases, preserving legacy Equipment calculations. |
| `Assets/Scripts/Ai/V2/Missions/AttackBaseRefitPolicy.cs` | Uses combined candidate state and candidate-slot occupancy in the existing refit path. |
| `Assets/Scripts/Ai/V2/Strategy/Demand/DemandLayer.Economy.cs` | Reads effective collection/capability abilities from both slots. |
| `Assets/Scripts/Ai/V2/Strategy/Demand/InfrastructureFulfillment.cs` | Reads effective host abilities from both slots. |
| `Assets/Scripts/Ai/V2/Strategy/Demand/AggressionDemandEvaluator.Attack.cs` | Reads both slots when projecting held reinforcement strength. |
| `Assets/Scripts/Ai/V2/Orchestration/AiStrategyV2Pipeline.AggressionAdmission.cs` | Includes Mutator in the existing hand fingerprint. |
| `Assets/Scripts/Ai/V2/Diagnostics/MaterializationDiagnostics.cs` | Reports combined effective ability state. |
| `Assets/Editor/AiRaidKnownPoolReachabilityTests.cs` | Updates the existing frozen-pool test to the extended tuple shape. |
| `Assets/Editor/AttachmentSlotTests.cs` | Pure slot/order/runtime/prediction regressions using synthetic definitions. |
| `Assets/Editor/AttachmentLifecycleTests.cs` | Unity transaction, deployment, aircraft return and observation witnesses. |
| `Assets/Editor/AttachmentSlotTests.cs.meta` | Unity GUID for the new test source. |
| `Assets/Editor/AttachmentLifecycleTests.cs.meta` | Unity GUID for the new test source. |

## Verification and remaining editor checks

Differential baseline is pinned to the original task revision, never to the modified code. The original baseline has 26 distinct existing Editor-stub errors. After incorporating master 941d17eb, both that exact upstream baseline and the feature have 27: the same 26 plus the upstream Canvas lookup overload unsupported by the old reference DLLs. There are zero feature-added compiler errors. Test compilation succeeds with the project verification reference DLLs and temporary net472 compatibility copies. Normal SDK/MSBuild entry points fail in this sandbox's process reporting; the same Roslyn compiler, package references and project test sources were invoked directly. The temporary managed Mathf test stub receives Log10 on both baseline/current sides for the existing Audio code; this is not a gameplay change and is not included in the repository. After the upstream rebase, test-only copies map the new Canvas lookup overload to the old explicit-sort overload supported by the reference DLLs. Neither workaround modifies gameplay sources or their baselines.

- Existing baseline tests passing outside Unity: 774.
- Current tests passing outside Unity: 812; no previously passing test fails.
- New AttachmentSlotTests: 30/30 passed, including both install orders, add/override and ability-family interactions, legacy Equipment parity, runtime preservation, conflicting maximum changes and spent-Fate valuation.
- New AttachmentLifecycleTests: 26 cases require Unity native engine. They cannot execute here (PlayerColorPalette/UnityEngine native initialization); they are explicitly pending, not reported as passed.
- Existing runnable Equipment matchup / Hero matchup / Production Scout chain tests retain their results. Native-bound failures remain native-bound.
- verify_full.js: all three edited prefabs have zero missing references, duplicate IDs or Int64 overflow.
- verify_types.js: all three edited prefabs have no checked reference type mismatches.
- git diff --check: clean.

Run the complete EditMode suite in Unity `6000.5.4f1`, especially the 26 lifecycle cases. Manually exercise RMB attach → own hand/live Bio target → cancellation/turn change; both indicator previews and restoration; Unit/Hero deploy into garrison/existing army; and produced activation AP with no second resource debit. Full Unity compilation, scene loading, native payment/vision and interactive UI are not claimed verified by the external test harness.

## Deferred

Real Mutator content, syringe art, final prefab positioning/anchors/sizes, Research catalog content, balance and evaluator review against real cards. No new gameplay content was authored.


## Second review (2026-10-05)

The follow-up review found and fixed four omissions. These are local behavior corrections within the attachment feature.

| Finding | Correction and affected files |
| --- | --- |
| A maximum clamp retained hidden Move/Fate consumption after an explicit refill; a later attachment could resurrect that old consumption. Full repair had the same HP problem. | EquipmentSystem.ReconcileResourceRefill resets only the restored resource and reconciles the others. Called from UnitData turn/battle refills and UnitRepair after payment/healing. Two failing witnesses reproduced Move/Fate before the fix; both now pass. Repair has a Unity lifecycle witness. |
| CardUI.Setup wrote a new host name/art before restoring an active old attachment preview. Configure then restored stale host values. | Revert both toggles before rebinding CardUI. Native UI witnesses cover both slots, including name/art restoration and indicator hiding. |
| HumanVisualMemory's copied UnitData omitted attachment references. | Snapshot both Equipment and Mutator. A passing witness verifies the observed slots remain stable after live references change. |
| Known-pool reachability hypothetically applied Mutators to non-Bio or already mutated hosts. | Keep CardDefinition in the existing grant loop, reuse FitsHostCore for Mutator compatibility, and freeze own non-hero Mutator occupancy alongside ArmySnapshot.Members. Hand occupancy is read from frozen PoolCards. Changes are in CombatOpportunityAnalyzer, WorldSnapshot and WorldAnalysis.Self. Four new passing witnesses cover Bio, free/occupied live slots and occupied hand slots; all nine reachability tests pass. Existing Equipment heuristic and valuation formulas are unchanged. |

The resource bank, payment amounts, Research/Production creation paths, reservation ownership and mission executors remain unchanged. The new factual occupancy array is rebuilt by the existing snapshot cycle; no cache or bank ownership layer was added. Live attachment still uses the existing vision/content notifications.

Final external verification: 802 passing tests; all 774 baseline passes retained; 23/23 pure attachment tests; 9/9 known-pool reachability tests; 5/5 HumanVisualMemory tests. The 18 native attachment lifecycle cases remain pending in Unity 6000.5.4f1. Raw reference-compilation errors are compared against the immutable baseline rather than reported as a Unity build. Both prefab YAML validators pass.


## Cache read/write and resource-bank audit (base 4c53dac6)

The review followed the attachment state from gameplay mutation through AI hand removal/versioning, fresh WorldAnalysis snapshots, observation invalidation, admission keys, per-snapshot memos and the WorthIt estimate cache. It then followed canonical cost construction, feasibility, joint consumption, owner-aware spendability and actual execution. This is a local correction of feature behavior; no new cache, resource ledger, axis wallet or evaluator formula was introduced.

| Boundary | Findings and result |
| --- | --- |
| Attachment write / hand version | EquipmentSystem owns payment and slot installation. AI executors remove a consumed attachment once on success; AiHandData.RemoveCard increments MutationVersion. Failed attachment retains the card. Partial generation/deployment failures keep real mutations and stamp V2StateVersion through the existing result path. |
| Snapshot write / refresh | BuildSelf freezes both slot references in PoolCards and Mutator occupancy for own non-hero Members. RefreshOperationalState creates a new WorldSnapshot and rebuilds Self, Development and TrueWorld; RefreshStrategicKnowledge additionally rebuilds honest knowledge when its player-scoped revision changes. Per-snapshot ConditionalWeakTable memos therefore get a new identity after a completed action. |
| Snapshot read (fixed) | CombatOpportunityAnalyzer.AssemblableBodies used the live hand and raw CardDefinition, ignoring attachments and allowing the old snapshot's inputs to drift after hand mutation. Both known-pool and assemblable reads now use the same PoolCards reader. AiPower.ToDefenderProfile's attachment overload uses EquipmentSystem.Project, preserving Base -> Equipment -> Mutator without a second effect-folding algorithm. |
| Invalidation / admission (fixed) | An occupancy-only Mutator change did not publish Capability invalidation and was absent from Development's factual admission key. Both now compare the frozen NonHeroMutatorOccupied array. Existing hand-version and effective-combat inputs remain in place. Four pure witnesses reproduced these cache-input/invalidation omissions before the fix and pass after it. |
| WorthIt cache read / write | The cache keys converted BattleUnit Attack, Defense, Initiative, current/max HP, hero Fate, abilities and type tags, plus commanders, magnitudes, seed and trial parameters. These are the final attachment effects, so slot names/identities are not an additional simulation input. Scope begin clears entries; scope end clears/deactivates them. Three added native witnesses require a miss after changed Mutator Defense/HP/Initiative and a hit on repeat; they remain pending in Unity. |
| Bank write / read | StrategicResourceReservationLedger owns detached, idempotent owner/reason/resource rows. TurnResourceBook reads current rows per spend query and excludes only the caller's authorized holds. JointFeasibility's budget/own-hold copies live only within one synchronous portfolio search; execution rechecks current spendability. Existing bank tests verify request/read detachment, replacement, owner exclusion and release. |
| Cost / joint reservation | PlanFactory counts host play plus the new attachment's instance cost. Attached cards already paid do not add another cost. Ordinary Equipment/Mutator uses normal AP/resources; produced attachments use activation AP and no second resource charge. GenerateAttachDeploy counts one Challenge stake plus activation and host play. Six new pure witnesses cover both slots, prepaid/ordinary costs, one physical-card claim, one generation-source claim and exact push/pop release. |
| Chained execution | MaterializationExecutor checks the complete plan against StrategicSpendability before the first irreversible step. DevelopmentUpgradeFulfillment does the same for GenerateAttachUpgrade, validates the live slot before minting, and retains won cards when attach fails. No bank owner or extra persistent hold is created for an installed attachment. |
| Standalone execution (fixed) | NonCombatCardPlayer.Execute previously trusted candidate admission for the bank and checked only whether EquipHost was null. The Equipment branch now resolves the live own non-prisoner recipient and re-reads spendable AP and the real instance resource cost immediately before TryAttach. A stale recipient or another owner's new hold cannot cause an attachment debit through this boundary. Eight added Unity transaction witnesses cover holds, release/retry, prepaid cards and removed recipients for both slots. |
| Reservation lifecycle | Shared reaction envelopes replace/release through their existing owner. Portfolio claims are unwound by the existing token pop and rebuilt between searches. End-of-turn expiry and AssertClearAtTurnEnd remain the final ledger boundary. No reservation survives merely because a Mutator occupies a permanent slot. |

Verification against both immutable baselines: **812 external tests pass**, with **zero regressions from the 802 task-start passes and the 774 original feature-base passes**. New pure cache/bank witnesses: **10/10 pass**. AttachmentSlotTests: **30/30**; known-pool reachability: **11/11**; TurnResourceBook: **20/20**; reservation invariants: **7/7**; generation-source identity: **2/2**. Reference compilation adds no error messages relative to the preserved baseline; the runnable test build succeeds with the documented temporary harness compatibility substitutions.

Unity validation remains pending: all **26 AttachmentLifecycleTests** plus the **3 new WorthIt cache witnesses**; existing engine-bound cache/vision/economy tests also require the editor. Run the complete EditMode suite in Unity **6000.5.4f1**, then exercise a same-turn attach -> snapshot/admission refresh -> second action with protected AP/Tech, and generation win/loss/attach failure. No successful full Unity build, native payment execution or Monte Carlo cache hit/miss run is claimed here. No Unity YAML was modified in this audit.
