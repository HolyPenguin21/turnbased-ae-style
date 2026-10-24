# Rustmarch — four AI fixes and behaviour calibration

Base: `e47aca20ca3842641416920cdce340b03cac307f`, Unity `6000.5.4f1`.
Delivery parent: `73eb495d17337a22dd1d0cc0a5f29b80c899c6a5`. Two upstream
battle-effect commits were incorporated without overlapping AI changes. The final
combined managed source was rebuilt and rerun: the same 219 passes, zero regressions
against the original task baseline. Additional TMP properties needed by that upstream
UI exist only in the temporary verification adapters.
Scope: behaviour corrections within existing ARCH-02 owners. No new runtime classes,
layers, desire axes, registries, managers or payment mechanisms.

The previously prepared local commit was removed by workspace maintenance before
publication. This change reconstructs the agreed implementation against the same
master revision. Verification below belongs to this reconstructed source revision.

## F1 — global income in Economy actionability

**Root:** `Analysis/WorldAnalysis.BuildEconomy` derived actionable Economy opportunities
from extraction, bases and collectors, omitting an own held global income carrier.
The effect evaluator knew the source's value but the Economy desire gate did not
know the opportunity existed. Raising an effect score would not correct that gate.

**Log example:** Ysolde T1 had Dorian with ProduceHuman and an infantry alternative.
The carrier's availability should contribute to Economy actionability before these
cards compete. This does not prescribe Dorian regardless of the other scores.

Logic:

```mermaid
flowchart TD
  A["Own hand and attached effects"] --> B["BuildEconomy: structural opportunity"]
  B --> C["Economy desire gate"]
  C --> D["Existing demand and card arbitration"]
  D --> E["Canonical deployment and payment"]
```

Dependencies:

```mermaid
flowchart TD
  A["BuildEconomy"] --> B["MaterializationChainMatching.EffectiveAbilities"]
  B --> C["EquipmentSystem.Project"]
  A --> D["StrategicEffectRegistry.HasGlobalRecurringEffect"]
  D --> E["PlayerGlobal recurring-resource semantics"]
```

The existing analysis now includes a non-aviation Unit/Hero/Facility in hand whose
effective abilities carry a global recurring effect. Standalone Equipment and Base
cards retain their existing paths. Analysis selects no card and promises no legal
placement. Materialization still proves placement, ownership, capacity, resources
and AP. The radar is not recalculated within a turn. If physical resource deficits
are absent, actionability alone creates no resource deficit or independent AP desire.

## F2 — legal hero extraction and honest failure reporting

**Root:** shared sparable-hero selection protected defender power and facility work,
but omitted the capacity left after removing the commander. Shell/Host paths had a
canonical transfer probe; Create assumed the first hero always fit. Gameplay rejects
both an overcrowded source and a first hero whose Command cannot hold himself.
An uncreated synthetic Economy mover could then be reported as a lost real mover.

**Log example:** Brynn T12, Miller Command 8 + Rusty Command 3 + six bodies.
Removing Miller leaves seven members under Command 3. Miller must not be offered;
the existing tree must choose a legal alternative or defer the operation.

Logic:

```mermaid
flowchart TD
  A["Economy or Development hero candidates"] --> B["Shared source eligibility"]
  B --> C["Resolve Shell / Host / Create"]
  C --> D["Fund and pin exact candidate"]
  D --> E["TaskExecutor applies canonical transfer"]
  E --> F["Continuity receives actual result"]
```

Dependencies:

```mermaid
flowchart TD
  A["AiArmyRoles.SparableHeroes"] --> B["ArmyData.CanLeaveWithoutOvercrowding"]
  A --> C["Defender floor and facility operators"]
  D["ProvisioningManager extraction solver"] --> E["ArmyActions.CanTransferMembers"]
  E --> F["Both final rosters and ownership"]
  D --> G["Read-only CreateVisualSnapshot"]
```

`AiArmyRoles` is the shared source selector. `Provisioning/` proves Create against
the same final-roster domain predicate used by `CreateArmyWithMember`, through a
snapshot that allocates no real army ID, registers nothing and spends nothing.
`Execution/` preserves the actual gameplay rejection string. Failed synthetic
materialization means TargetInvalidated; an absent real direct mover still means
MoverLost. Existing stale-actor preparation also requests replanning.

Economy and Development consume the same rule. Mission ownership, funded envelope,
one-mutation-per-step extraction, donor preparation and real payment stay with their
existing owners. No fallback forcibly removes the commander or creates a fake army.

## F3 — patrol equipment preserves mobility

**Root:** the shared equipment delta protected Recce radius, spotting and required
stealth on Scout assignments, but allowed combat gains to compensate for losing
patrol movement or increasing activation AP.

**Log example:** Brynn T26, Portable Mortar on Hooded, Move 2 → 1. An Explore/Refresh
carrier must reject that item even when its combat delta is positive. A local Hunt,
CaptureStructure or AirSweep keeps its existing evaluation rather than inheriting
a patrol restriction.

Logic:

```mermaid
flowchart TD
  A["Hand attachment or generated equipment"] --> B["Existing recipient alternatives"]
  B --> C["ScoreEquipmentDelta: assignment guard"]
  C --> D["Efficiency and strategic card score"]
  D --> E["Existing arbitration and installation"]
```

Dependencies:

```mermaid
flowchart TD
  A["Evaluation/Cards/EquipmentUpgradeValuation"] --> B["ReconScoutKinds Explore / Refresh"]
  A --> C["EquipmentSystem.Predict"]
  C --> D["Before and after movement / activation AP"]
  A --> E["Mission purpose and existing Recce guards"]
```

The change extends the existing assignment-capability veto in `Evaluation/Cards/`.
Every current user of the common equipment valuation inherits it, including hand
installation and Production recipient evaluation. No separate item blacklist,
production preference or equipment score formula is introduced. Economy/Development
capability guards and equipment persistence remain in their existing owners.

## F4 — AP history measures useful legally available work

**Root:** `State/ApTurnPressure.Measure` summed resource-affordable cards remaining
in hand without proving purpose, placement, reservations or joint feasibility. It
also treated LowValuePerAp and physical-resource refusals as AP scarcity. End-turn
measurement occurred after owner reservations were cleared.

**Log example:** Ysolde T31, start 18 AP → end 14 AP, with a 4-AP Base awaiting its
Economy delivery. The old card sum added CardsUnmet 4. The corrected witness adds
zero for that reserved/unavailable Base: demand is the 4 AP actually spent.
Conversely, 6 AP of useful remaining work with only 4 AP left is real unfinished
work, so the full 6 belongs in demand, not merely the 2-AP shortfall.

Logic:

```mermaid
flowchart TD
  A["Final world after Housekeeping"] --> B["Refresh strategic knowledge and commitments"]
  B --> C["Existing Phase-B candidate owners"]
  C --> D["Jointly feasible useful hand workload"]
  D --> E["Measure with live reservations and caps"]
  E --> F["Record once for next-turn history"]
```

Dependencies:

```mermaid
flowchart TD
  A["MaterializationCandidateBuilder workload"] --> B["RankedSurplus and FilterSurplus"]
  A --> C["NonCombatCardPlayer.EnumeratePlays"]
  B --> D["Canonical preflight and spendable H/E/M/T"]
  C --> E["Equipment / aviation / facility preview owners"]
  A --> F["MaterializationPortfolioSolver.EstimatePhaseBWorkload"]
  F --> G["Shared cards, resources and placement consumption"]
```

`Materialization/` extends its existing workload method with a turn-end measurement
mode; `Orchestration/` only orders the calls and transports the result. Both existing
candidate lanes retain their scoring and must exceed `tempoMinSpendUtility`. Only
the current AP ceiling is relaxed. The canonical joint solver preserves physical
cards, resources, recipient constraints and competing alternatives. Economy build
card claims and Development operator claims remain protected.

The witness deliberately measures existing hand plays, without forecasting generated
outputs. It is conservative and does not claim to estimate every possible future
Production action. Ordinary Phase-B workload evaluation retains its original mode.

Equipment measurement uses existing attachment previews and does not populate the
executable recipient-verdict cache. Aviation reuses investment-preview placement.
Facility preview extends the existing domain check with `requireCurrentAp`; all
real placement/payment callers keep its default `true`. Negative AP, ownership,
resource costs, card presence and facility slots remain checked.

Draw workload is bounded by actual deck, hand slots and per-turn draw/tempo caps.
It contributes unmet demand only when raw remaining AP cannot cover it. Deferred
missions contribute only positive InsufficientBudget proposals with an actual AP
shortfall, no physical-resource shortfall and a requirement beyond raw end AP.
Their maximum requirement is used instead of summing competing proposals. The
history lifecycle and separate initiative-purchase analytics are unchanged.

## Calibration

| Existing parameter / owner | Before | After | Expected behaviour |
|---|---:|---:|---|
| Foundation/taskScoreEconomicDeficitBonusMax | 3 | 4 | Moderately favour useful income for a real deficit; physical income value unchanged |
| Foundation/raidParkValuePerAp | 2 | 3 | Park thin fresh optional raids for the existing AP remainder pass |

The raid calibration does not change active operations or demand-driven raids.
No combat thresholds, Attack continuation, secondary Attack rules, equipment
persistence or future-base forecasting were changed. These are starting values
for match calibration, not a claim that every turn must select a particular card.

## Verification

The changed owners were traced from candidate generation to gameplay transaction
and backward to their consumers. A project scan found one BuildEconomy, one shared
ScoreEquipmentDelta, one extraction solver and one ApTurnPressure.Measure. Both
ApplyGarrisonExtraction call sites were updated. No new runtime method or class was
added; existing methods/signatures were extended.

Compile comparison against the same base revision uses identical old reference DLLs
and a temporary TMPro enum: 12 pre-existing diagnostics in each revision, no new
diagnostics. This is not a successful native Unity compile. A separate managed
NUnit harness uses identical temporary API adapters for both revisions, exclusively
outside the repository. Eight affected/adjacent fixtures: base 331 cases, 212 passed;
current 348 cases, 219 passed. All 212 baseline passes are preserved: zero regressions,
seven newly passing cases (six equipment cases and the extraction regression).
The remaining current failures are 128 native-engine errors and the same pre-existing
`ActiveEconomyBuildIntent_ProtectsResourcesWithoutRepeatedDemand` assertion that fails
on the base revision. Three additional staged-preparation cases compile but require
Unity. No claim is made that the entire managed run or the Unity suite passes.

Regression coverage includes held global-income carriers; source and destination
capacity on extraction; six patrol/local-action equipment cases; useful AP work
versus idle AP; real AP versus resource/priority deferrals; a reserved waiting Base;
and a useful zero-AP card versus the same card lacking Tech.

Full EditMode and match reruns in Unity remain with the project owner, as requested.
Check the four log situations above and their legal alternatives, the next-turn AP
history, bank reservation release, and optional raids using only leftover AP. No
manual Unity run or completed match is claimed by this delivery.
