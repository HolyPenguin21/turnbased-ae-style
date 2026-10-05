# Aviation mission fixes — 2026-10-05

AI aircraft now distinguish a flight step from authorization to strike. The original game log is unchanged, as are all diagnostic calls and message formats.

| Case | Root cause | Result |
| --- | --- | --- |
| Incidental attacks during approach or return | Each adjacent AI move looked like a completed gameplay route and inherited Standard strike authorization | AI orders default to Transit. The support executor authorizes only entry into its actual target, preserving exact army selection and Raid survivor floors. Manual endpoint strikes and entry AA retain their gameplay rules. |
| Landed wings reported as lost | An empty army shell did not distinguish landing from destruction | Full physical landing leaves a completion fact on the shell. Boarding clears that fact; partial landing and casualties do not publish it. ActiveDefence continuity distinguishes completed and lost wings. |
| Strike series interrupted or miscounted | A transit strike could consume the current-turn attack before arrival | Transit cannot consume a strike. The existing AviationRange calendar, stationary repeat strikes and safe-return proof remain the single flight-cycle rules. No separate calendar defect was established in the original log. |
| Flight continued toward an observed empty target | Empty and unknown defender collections were treated alike | Common support admission, projection, provisioning and execution require fresh observer knowledge before treating a target as empty. Attack releases only its air support and keeps the main capture operation. A wing with a live sortie turns home through existing recovery. |
| Repeated AirSweep formation and underfunded launch | Formation happened before mission admission; initial envelopes did not price an executable aircraft | Shared Recon Assignment nominates a mission-specific live wing or exact stored aircraft before funding. ScoutCostModel publishes its mandatory AP/Energy. Storage preparation runs only during execution of the provisioned task. Task keys survive the source-to-wing transition. |

AirSweep preparation uses AviationWingPreparation and the existing generic resource allocator; no additional manager, bank ledger or cache was added. Nomination, final assignment and capacity measurement share the same route projection. Paid combat/recovery wings cannot serve as free planning witnesses for a fresh Recon launch. Execution revalidates the funded envelope and spendable bank before formation. Reaction batches pass their reserved actors to preparation so another task's army cannot be repurposed.

Formation records ActorMaterialized, bumps V2StateVersion and refreshes operational/strategic knowledge before flight planning. Stored launch facts are detached snapshot values. Empty-target decisions use the existing observer/turn/knowledge-revision intel cache; stale or unknown data does not cancel a blind sortie. Combat-operation formation and generic relocation retain their existing admission paths.

Validation against master `22532001d9201dc7c9fb3e533829fcf509190b4c`:

- Managed verification assembly compiles with zero errors. Full-source comparison retains the same 47 verification-stub/reference diagnostics as the baseline, with no new diagnostics.
- Expanded reflection test run: baseline 809 passed / 322 failed; current 830 passed / 325 failed. All 1,131 existing cases retain their baseline pass/fail outcome.
- Added 24 regression cases: 21 pass; three physical landing/casualty cases are blocked by the unavailable Unity native runtime. They are included for Unity Editor execution, not reported as passing here.
- Resource-book tests: 20/20; reservation invariants: 7/7; estimate-cache tests: 11/11; route-cache isolation: 4/4. Existing engine-dependent economy/combat-cache cases remain blocked in this environment.
- New bank cases cover mandatory launch Energy on the first allocation, rejection of a zero-Energy envelope and prevention of reusing a locked physical launch claim. Snapshot cases cover knowledge-revision/observer isolation and detached stored-aircraft prices.
- Final diff review confirms no diagnostic-call changes and no modifications to the supplied game log. A full Unity Editor/PlayMode flight run remains necessary to validate native movement, AA presentation and landing in the live scene.
