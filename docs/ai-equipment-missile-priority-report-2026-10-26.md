# Equipment and tactical missile priority — 2026-10-26

Implemented the approved situational calibration in the existing ARCH-02 pipeline. Optional reserve equipment keeps its original utility when no useful executable missile-production alternative is proved. When such an alternative exists, only its positive equipment-gain components are valued at 25%. This is a soft preference, not a ban or a compulsory missile purchase; costs, signed losses, success probability and the normal allocator still decide what is bought. The coefficient needs Unity match evidence for further tuning.

## Bottom-up implementation review

1. **Recipients:** current mission participants, facility operators and required home defenders retain their equipment utility. An active Disruption scout or an attached carrier remains an assigned participant; this change does not alter Disruption mechanics or expose hidden units.
2. **Physical copies:** preparation and peak rosters are multisets. Actual primary/support members count first, then eligible strongest available bodies/cards with stable ties. A same-key spare does not inherit a purpose just by sharing a card definition. The peak includes already assigned ground copies instead of acquiring a second full quota.
3. **All bases:** a last/required garrison member and a field defender on a threatened own secondary base remain required. Existing garrison release rules and honest positioned threats own the decision; no base is intentionally conceded.
4. **Production:** the witness reuses staffed ForecastSources, canonical actor/source eligibility, the investment window, quota/retry guards and owner-aware spendability. It neither creates a manager nor reserves or spends resources.
5. **Missile usefulness:** existing aviation candidates own uncovered target demand, physical and hand stock matching, blast value/collateral, airfield capacity, route/rebase feasibility, placement and launch-energy checks. Stock that already covers a target removes additional production pressure.
6. **Equipment paths:** live recipients, hand recipients, held attachments, generated upgrades, attach/deploy chains and operator output forecasts use the shared EquipmentDeltaParts gain. Positive components alone are reduced once; losses and capability vetoes remain signed.
7. **Requested units:** a concrete Phase-A capability demand supplies the required-recipient hint even for a future generated body/new army. Equipment is priced once into RoleFit or Synergy as appropriate. Optional CardUpgrade continues to use its actual recipient.
8. **Reevaluation:** memo scope is per immutable valuation pass. Admission/verdict keys include physical roster/role/duty inputs, investment-window observation, live spendable AP/resources, hand capacity, missile supply/routes and exact attempt/retry context. Without the retry set after a generation attempt, a forecast conservatively keeps reserve value; actual card selection still recognizes an untried alternative through its carried reservation.
9. **Execution and lifetime:** the calibration changes candidate utility only. Existing portfolio, payment, materialization, air-support launch/result and intent-completion owners execute selected work. Read-only missile projections issue no real card/unit identity. An existing test typo, UnityEngine.UnityEngine.Object, was corrected during the audit.

## Verification evidence and limits

- `git diff --check`: clean.
- Differential tree-sitter: 10 changed/new C# files, zero parser errors; no new errors relative to the local published-work checkpoint.
- Two new C# `.meta` pairs are present with unique GUIDs. No serialized assets changed in this task; both YAML validators also passed for the three assets from the previous implementation.
- Addressed callers inspected: all shared equipment consumers, Phase A/B materialization, Development warm/settled verdicts and canonical aviation stock/production paths. Prior assembly predicates remain present in all 8 PlanGather and 6 PlanPreparationAssembly production calls.
- One independent whole-change review identified the four relationship defects above; each received a targeted code fix and regression case.
- **Unity, dotnet and mono are unavailable. Compilation, NUnit/EditMode tests and gameplay runs were not performed.** Parser checks are not compilation. Added tests are unexecuted; no red/green result is claimed. The missing-dotnet compile script's misleading zero-error output is not evidence.

## Unity verification scenarios

Use the project's Unity version and run the full EditMode suite, especially AiEquipmentPriorityTests, AiMissileStockTests, AiEquipmentUtilityTests, AiEquipmentEfficiencyTests, AiProductionScoreAlignmentTests, AiDevelopmentEquipmentMatchupTests and AiIndependentDevelopmentTests.

- Compare reserve equipment scores with a useful uncovered reachable missile target, then with that target supplied by an existing missile. Expect reserve gain x0.25 only in the first state.
- Repeat with missing AP, launch energy, resources, operator, airfield capacity, route, hand slot or investment window. No proven alternative means original reserve gain.
- Fail the sole missile source while attempts remain; then offer a distinct untried source. Actual selection must reject the used source and recognize the alternative. Check warm/settled reevaluation across opening the investment window and changing resource holds.
- Use three identical bodies for a two-copy preparation plus a mobilized peak. Keep the weak assigned host and strongest usable accompaniment required; the third copy remains reserve. Repeat for hand copies and held supports.
- Fulfill a concrete Scout/Attack/Defence demand with a new generated/equipped army. Its required equipment value must not be discounted before deployment. Check negative mobility/capability deltas remain vetoes.
- Threaten a secondary base while a Disruption scout travels and an assault assembles. Equipment competition must not weaken required defence or strip those assignments. Compare multi-turn AP/resource spending and missile saturation in real matches before changing 0.25.

Remote publication must use real parent fa1a55a647d14939e95312af04ceda628c94aabf, never synthetic local checkpoint2590a16. Only task files are overlaid on the complete remote tree; this task is published separately for review.
