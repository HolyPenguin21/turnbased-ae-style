# Army composition follow-up

Baseline: `c16919764b70a87383544a609e75fa164655db66`.
Integration base: `81437eff9b47539345edd5f8772ae2a2b9436186`. Its intervening changes do not overlap the fix files; the updated managed runtime was rebuilt and all 1,747 selected portable cases passed again before publication.
Scope: behavior fixes within the existing GroundCombat assembly/reinforcement, Attack, Housekeeping and retreat-witness owners.

## Resulting behavior

1. A command upgrade first wins the existing contest against the current commander. Its complete atomic exchange returns ordinary old heroes to the support when legal, retains army support carriers, and has a retained-roster alternative when the return cannot fit or be paid. Body-fill counting, handoff AP and gather projections consume the same incoming/displaced lists. Existing Continuity return legs bring the support home.
2. Fresh Attack departures use complete roster selection independently of their zero probability floor. The strict Attack power bar and configured coverage remain in force. Missions, admission and provisioning pass the same `PreserveDepartureRoster` fact for started Attacks. Same-hex assembly can return an excess hero before recruiting a body even at the zero gate.
3. Preparation Housekeeping excludes army support carriers from ordinary hero slot waste and release candidates. Live release preflight enforces the same protection; a required facility operator may still relocate into the local garrison.
4. Departure selection searches legal partial return packages rather than requiring every desirable home hero to fit. Facility operators remain required returns; the canonical maximum possible receiving capacity bounds subset search. The departure-room probe reuses the same solver. Movement, combat and the complete final roster remain checked.
5. Both sides of a retreat witness include secondary support profiles in the existing fingerprints. Abilities, tags, wounds, maximum HP and other combat inputs are included. Aggression re-admission consumes the updated own fingerprint. Changed facts still require a new estimator verdict rather than automatically clearing the withdrawal.

No new bank, resource owner, composition manager, persistent roster cache or mission axis was added. Gameplay `ArmyData` and `ArmyActions` still allow a player to use multiple heroes. Catalog tags, scene data and balance coefficients are unchanged.

## Regression coverage

22 new parameterized cases were added to existing fixtures:

- `Assets/Editor/Tests/AI/AiGarrisonHeroTests.cs`: ordinary command replacement, passive retention, illegal return fallback, an actually executed atomic exchange, refusal to replace a better commander, fresh versus committed Attack departure, strict power equality rejection, zero-gate passive retention, and partial garrison returns with/without assembly at Raid and Attack gates.
- `Assets/Editor/Tests/AI/AiHousekeepingMissionContractTests.cs`: preparation retains secondary Heal, Drill and Transport carriers.
- `Assets/Editor/Tests/AI/AiAttackFieldContactTests.cs`: own/enemy/admission fingerprints notice secondary Heal/Drill, wounds and tags; support roster ordering remains immaterial.

The new tests were also compiled against the unchanged baseline runtime. Only the new request-field assignment was omitted in that reproduction copy so the old API could compile. They reproduced 13 assertion failures; the other nine are protective/control cases. All 22 pass on the fixed runtime.

All 1,725 previously passing portable baseline cases retain their result. Together with the 22 new cases, 1,747 portable cases pass. Twelve custom NUnit TestName cases required explicit name selection because NUnitLite's test-list selector omitted them; their names were reconciled against the baseline XML so no baseline pass was silently dropped.

## Verification limits

The unadapted reference-DLL compile has the same pre-existing UnityEditor/stub diagnostics as the baseline. Those declaration errors can hide method-body errors, so an additional managed test build was used: both baseline and fixed runtime compile without errors in that environment.

Portable checks use old Unity reference DLLs, NUnitLite/Mono and verification-only adapters for newer API signatures, managed Mathf and TMP fields. Without a configured native map, visibility recomputation is omitted in that copy; empty summon-catalog checks use reference equality. No adapter is part of the gameplay changes. The full unfiltered baseline suite contains engine-dependent failures; only its actually passing cases are used for the differential gate.

Unity `6000.5.4f1`, the full native EditMode suite and gameplay acceptance were not run. Native acceptance should include a travelling command handoff with activation charges, a full/partly full garrison, a fresh versus started Attack, preparation with passive heroes and facility operators, and re-evaluation after observed support-hero changes on either side.
