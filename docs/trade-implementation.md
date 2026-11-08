# Trade / Trading Post

Base: df8e67e577ac29bb3ff86066a240f04925f6a2bd. Behavior change; no new AI axis or mission.

## Gameplay

`Trade` is a capability, independent of card names and carrier type. A living owned unit, base or installed facility with Trade unlocks resource exchange. Trading Post is a shared neutral Facility with Trade, built through InfrastructureActions and existing facility slots. Construction costs 2 AP and H1/E1/T1; trading itself costs zero AP. Trading Post currently uses the existing Factory illustration; no new painted artwork is included in this code/UI change.

Every elementary trade buys one unit at the player's current global rate (2, 3, 4, ...), regardless of resource pair. A synchronous batch is fully simulated before any mutation. At the end of an own turn without trades the rate falls by one, minimum two. Ending the same turn twice does not decay twice. Losing or capturing a carrier does not reset/transfer a player's rate.

The rate, current-turn activity and turn-open status are part of PlayerEconomyState; capture, detached copy, validation and direct restore all include them. The existing automatically computed schema hash detects this payload change; old incompatible saves follow the existing refusal policy. Envelope version remains 2.

## Scene

`Button_Trade` is authored next to `Button_Data` (Intel). `TradeUI/Modal_Trade` is authored under Canvas_UI. The four resource rows are copied from the initiative purchase presentation, with an independent TradeResourceRowUI: minus selects the resource to give; plus buys one of that row's resource. The help/rate text explains both actions. No scene elements are constructed at runtime.

TradeModalUI controls visibility, validates the active confirmed human turn, subscribes to economy changes, and closes when the turn, generation or access changes. GameTurnController includes it in map, card and End Turn blocking. Close/Escape and disable release subscriptions.

## AI

TradeFunding is a read-only funding preview and an explicit execution adapter over TradeActions. Donors are restricted by ordinary spendability, full remaining main-hand/deck costs, and operational forecasts (including fuel). Conversion never inherits permission to consume another operation's deferred hold. The original action retains its own authority after conversion.

Materialization feasibility retains fundable resource-blocked chains after non-resource checks. Card scoring and joint consumption use external donor cost, rather than charging the acquired resource a second time. A portfolio admits at most one trade-dependent chain; the next round rebuilds against the new rate and balance. Execution revalidates placement/route/AP and funds the complete resource deficit before returning to the ordinary action path. Generation, facility placement, infrastructure and maintenance reuse the same adapter. Operational Economy completion keeps its existing AP envelope/owner. Proactive conversions compete in Phase B for real deck/operational demand, including at zero AP. Bounded search never sells a resource it is buying in the same quote.

Trade access is a PlayerGlobal strategic effect with snapshot-based saturation/value, not recurring income. Trade actions report Resources invalidation and one mutation receipt; the tempo loop does not stamp the same trade twice. No new static match registry or allocator is introduced.

See trade-deck-changes.md for all 27 starter/AI deck changes and resource totals. Heroes, existing Facilities and scout cards are not removed; at least one of every reduced ordinary unit remains. All decks remain within 100 points. The Vessels' intentionally low Human demand remains; this feature does not rebalance that faction's resource identity.

## Verification

- Both existing Unity YAML validators: scene and all three changed catalogs, zero missing references, duplicates, overflow and type mismatch.
- Roslyn C# 9 differential compilation against the repository's stub reference versions: full source baseline/current have the same 17 missing editor/TMP reference errors. These declaration errors are not treated as compilation success.
- Additional runtime-only differential compile, with temporary verification adapters for the known old-reference API gaps: baseline and changed runtime compile with zero errors. Adapters stay outside Assets/repository and do not change game code.
- The runtime + AI/Card test sources (except two unrelated editor-only Card fixtures) compile in the same temporary managed harness.
- Four TradeTests run under Mono/NUnitLite: 4 passed (global progressive rate, arithmetic/batch equivalence, no partial debit, invalid/overflow rejection).
- Focused pre-existing AI regression sample: 39 pass on both baseline and changed source; one economy-fingerprint dispatcher test fails identically on UnityEngine.Object initialization without native Unity. No new regression in that sample.
- New AiTradeFundingTests cover actual shared payment after funding at zero AP, pure quotes, insufficient full funding, hand protection, reservation protection, lifecycle/restore and capability access. They compile but require native Unity objects and are not claimed as executed here.

Not run: Unity 6000.5.4f1 import, full EditMode, visual interaction in the scene, or long AI games. The scene's native layout and the full funding-to-mission continuation chain still need that acceptance. Existing ai-verify scripts can be used in the normal Unity environment; local SDK CLI cannot read process metadata here, so this run invoked Roslyn directly rather than treating an aborted build/empty error list as a successful check.

## Bottom-up review (2026-11-08)

Scope: behavior corrections within the existing capability/payment, executor, result and invalidation owners; no new AI axis or architecture.

| Failure found | Correction and owner |
| --- | --- |
| Chronological largest-donor selection rejected feasible funding (3+2 donors for rates 2+3; 4+5 for 2+3+4). | TradeFunding assigns descending prices with bounded backtracking (8192 nodes, existing 128-unit limit), then returns chronological steps. Donor protection and pure preview remain unchanged. |
| A trade could disappear from an action result when a later step refused, or be counted twice as a world mutation. | MaterializationExecutor, NonCombatPlayExecutor, InfrastructureFulfillment, BuildingPlayExecutor, TaskExecutor and DevelopmentUpgradeFulfillment preserve committed conversions and distinguish them from the requested action's own mutation. Pure refusals remain no-ops. Phase A refreshes after funding-only changes; Phase B does not stamp those twice. |
| CapacityUnlock admitted exchange affordability but called the upgrade without funding it. | InfrastructureBuildExecutor validates owner, expected level, AP and tier through CanUpgradeBase, funds the tier, then calls the normal upgrade transaction. |
| Maintenance could buy resources before detecting insufficient AP or a stale tier; a non-combat card/source could disappear before funding. | StrategicMaintenancePolicy validates the live action first; NonCombatPlayExecutor checks held-card identity. ResearchProductionSystem exposes a quote that ignores only insufficient resources while retaining canonical source, output, blueprint, quota, cost and AP checks. Generation consumers use that quote before funding. |
| A conversion looked like an infrastructure rollback leak. | PhaseAInfrastructureLane reports it explicitly. AiV2Trace allows resource-vector changes for a known conversion while still checking AP, building/slot counts, levels, defenses and army movement. |
| Match completion left a survivor's backend trade window open. | GameTurnController closes every trade turn and the popup on match completion; PlayerRoot.CloseTradeTurn grants no idle-turn discount. TradeModalUI also tolerates repeated Show calls while visible. |

Review verification:

- Both Unity YAML validators repeated on Game.unity and the three changed catalogs: zero errors.
- Differential managed compilation: pre-review source and corrected runtime plus AI/Economy/Card test sources compile with zero errors using temporary old-reference adapters outside the repository. This does not establish Unity import/build success.
- Focused NUnitLite run: pre-review 43 passed / 1 native-engine error; corrected 47 passed / the same native-engine error. The eight passing trade/allocation cases include the exhaustive 2916-case donor-allocation oracle; the existing 39 passing AI checks remain passing.
- AiTradeFundingTests now also cover AP-blocked maintenance, a missing held equipment card and terminal close without rate decay. All 11 cases compile; their native Unity execution remains outstanding.
- The exact native-engine error in both runs is AiEconomyAdmissionFingerprintTests.TheDispatcherRoutesEachAxisToItsOwnerAndSharesNoKey (UnityEngine.Object initialization in the reference harness).

Remaining acceptance in Unity: full EditMode suite, popup layout/input and new-game/load transitions, loss/capture of the Trade carrier, and long games with AI traces showing funding followed by the originally selected action. No native or long-game run is claimed by this review.
