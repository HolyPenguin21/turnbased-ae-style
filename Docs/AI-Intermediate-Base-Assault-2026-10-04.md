# Intermediate base assaults during Attack

Attack operations can evaluate an observed hostile Base while marching toward their primary target. The primary target and durable operation identity remain unchanged; only the funded Assault endpoint changes. This includes a Base owned by a different enemy player.

## Admission and knowledge

- Only an incumbent Assault's pinned structural army can divert; preparation, reinforcement and return legs retain their behavior.
- The building must have been observed in the current turn. Every remembered enemy army on that hex must have current observation and a defender roster. Stale empty sites and incomplete defender intel are rejected.
- The shared sequential WorthIt estimator evaluates the actual base opposition, commanders, current roster/HP and terrain/structural defence. The optional fight uses the existing strict fresh-start gate (0.8), independently of the primary Attack policy.
- No comparison with citadel defenders is made. Location-only citadel knowledge stays unknown and is not treated as an empty or weak garrison.
- Contact must be reachable with current movement, and an onward route must exist. The detour must advance toward the main location and add at most one maximum-movement turn. A base blocking the only direct route can also be considered.
- Optional base fights use the assigned army alone, with no same-hex assembly, donor recruitment or additional operation.
- One spent optional fight per operation per turn uses the existing opportunistic-strike marker. Failed local legs are dropped without retiring the main operation.

## Lifecycle and execution double-check

Planning selects the local objective before funding. Admission and provisioning use its actual opposition and strict gate; execution uses its actual hex and canonical movement authority. Primary goal satisfaction still refers only to the primary target. Capturing the local base records infrastructure progress, clears the local pin and resumes the main operation. A local ownership change invalidates the local step rather than satisfying or retiring the primary objective.

## Cache audit

No new cache is introduced. Main and local provisioning keys are distinct; local keys include expected owner, hex and pinned actor. Durable MissionIntentKey remains the primary target. The strategic-admission fingerprint includes the local pin, once-per-turn marker and exact current/max movement, since a positive-MP boolean cannot establish contact reachability. Sequential combat estimates retain the existing complete simulation key and per-turn scope. Re-selection after defender strength changes is covered by a new regression.

## Bank and reservations audit

The local step goes through ResourceAllocator and GroundCombatAssaultTransaction. It requests actual activation AP (zero for an already activated actor), and no physical resources. The transaction retains pre-mutation envelope/remaining-AP checks, actor contention and post-assembly reconciliation. The allocator continues to honor other owners' bank holds; no alternate spending authority or reservation write is introduced. Existing operation continuation uses the same durable intent; the base does not create a second intent or donor/card demand.

A new integration test verifies that a 2-AP local Assault cannot spend a building owner's 2-AP hold when only 3 AP exist. Releasing the hold permits normal funding. Planner integration tests cover unknown citadel intel and both activation states.

## Validation

Baseline: master 763b6a9ce2c34930c349a1a001e49b7ed440e2be.

| Verification | Result |
| --- | --- |
| New intermediate-base regression scenarios | 26/26 pass |
| TurnResourceBook | 20/20 pass |
| Reservation invariants | 7/7 pass |
| WorthIt estimate cache | 11/11 pass |
| Route cache isolation | 4/4 pass |
| TaskScore allocator regressions | 3/3 pass |
| Expanded managed suite, current | 228/286 pass |
| Same suite, baseline | 202/260 pass |
| Previously passing scenarios that regressed | 0 |
| Full-source compiler diagnostic differential | No new errors; same 47 baseline stub/reference errors |
| Diff whitespace check | Pass |

Verification used a managed .NET 8 runner with Unity reference assemblies and engine stubs. Two identical verification-only adaptations in both source mirrors bypass Unity native null operators for null map/catalog references; they are not repository changes. The 58 remaining failing scenarios also fail on baseline. Most depend on Unity native object behavior; two contain baseline assertion failures under the managed stubs. These results do not certify Unity Editor/PlayMode execution. Final game verification still requires running the Unity tests and replaying a game with observed intermediate bases; the original log is historical and cannot demonstrate behavior of the new code.
