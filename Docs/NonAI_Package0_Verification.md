# Non-AI refactoring — package 0

Base: `a174e9b4d3cff15f741d9afad2055cfe4b30b363` (29 October 2026).
Scope: local fixes 0.1 and 0.2 of the implementation specification, plus an adjacent human-memory consumer correction discovered during the requested bottom-up recheck. Packages 1–8 remain open.

## Changes

- Aviation fuel damage completes the entire wing's HP/roster changes, calls existing `StealthSystem.OnUnitRemoved` for the removed aircraft, then publishes content change. Partial death, all surviving, and all dead branches refresh final composition. Damage amounts and endurance rules remain unchanged.
- `GameAssetCatalog.ReplaceEntries` prepares and validates a complete replacement before swapping state and invalidating lookup. Empty/whitespace and duplicate keys produce explicit exceptions without changing the previous state. Key matching remains ordinal/case-sensitive. Null asset arrays/elements remain supported; typed resolution order is unchanged.
- `Entries` returns a read-only snapshot with copied nested arrays. Accepted input arrays/lists and returned lookup arrays cannot mutate the internal catalog. Unity object references retain their identity.
- Both editor writers use the controlled API. Effect registration keeps its no-op path and only replaces/saves when references change.
- The bottom-up recheck found that `RememberCurrentlyVisibleContent` used the ground-contact predicate `BattleInitiator.IsEngageable`, which excludes aircraft. A content notification could therefore remove even a surviving visible wing from human memory. The memory query now reuses `NonEmptyArmiesAt` (the existing map presentation policy) and observer-specific stealth filtering. Own/neutral armies, empty shells, prisons, airfields, and carrier containers do not become enemy map-memory markers. No battle targeting rules were changed.
- No authored catalog, scenes, prefabs, existing .meta/GUIDs, save formats, or AI logic were changed.

## Verification performed outside Unity

- New aviation regression against the original lifecycle: 3 expected failures, 1 passing endurance-boundary case. After the fix: **4/4 passed**. The runner used actual game model/rules/lifecycle/stealth source and Mono with legacy Unity API references and managed Mathf. Scene-independent fixture setup uses existing restore APIs.
- Original catalog stale-cache regression: failed because load returned the old asset after replacement. Final catalog regression fixture: **10/10 passed**, using actual catalog/test source with external Unity object adapters.
- Two additional isolated checks ran actual editor writer/catalog source against an in-memory AssetDatabase adapter: repeated rebuild retains keys and refreshes loaded references; effect registration refreshes cached references and the second registration does not save. **2/2 passed**. These adapters do not reproduce native import/serialization.
- Managed compilation of all runtime sources succeeded with verification-only legacy API/framework adapters. The raw all-source reference compile retained the same 22 normalized pre-existing diagnostics; no new diagnostics. Its missing UnityEditor/TMPro APIs prevent treating this as a full project build.
- The authored catalog at the base revision contained 87 keys with no duplicates. `git diff --check` passed.
- Initial independent read-only review found no critical/important defects. The subsequent user-requested bottom-up recheck identified and corrected the human-memory consumer issue described above.

All verification adapters and environment workarounds stayed outside the repository's production code.

## Remaining verification and deferred review notes

Use **Unity 6000.5.4f1**, import the complete project with real local assets, and run the complete EditMode suite, including `AviationFuelDeathNotificationTests` and `GameAssetCatalogMutationTests`. Verify end-turn fuel damage, real catalog rebuild/skill registration twice, and new/load/exit paths in the editor. No native Unity build or EditMode/PlayMode execution was available in the implementation environment.

The initial `StealthChanged` coverage gap is closed: callbacks now assert the final roster and HP, including the first aircraft dying before a later survivor. Native integration coverage for real rebuild/import/no-op registration remains pending; the external writer checks cover command behavior with adapters only.

Empty-army/controller/container deletion still uses the existing optional selection path. Its UI-independent lifecycle is explicitly assigned to package 1; package 0 does not claim to complete it.

## Bottom-up recheck

- Traced end-turn → fuel damage → removal/stealth cleanup → visibility/content callbacks → human memory/marker reconciliation, including movement-created historical sightings. Traced both editor writers → controlled replacement → cached runtime loaders and OnValidate; verified that content fingerprinting excludes the catalog and that no save schema changed.
- The actual map-controller memory query reproduced the surviving-wing loss (one expected failure); a second query regression reproduced the aircraft/container classification error. After the correction, the combined aviation/memory checks passed.
- Final managed results: **20/20** aviation and human-memory tests (11 new/expanded cases and 9 existing `HumanVisualMemoryTests`), plus **13/13** catalog/editor-writer checks (11 catalog cases and 2 external writer checks). Total **33/33** outside Unity.
- Additional cases cover detection without a hidden flag, repeated resolution of a dead wing, another owner's untouched aircraft, partial human refuelling with overflow death or survival, old snapshot independence, container exclusion, detected/undetected aircraft, and authoring cache invalidation.
- Mutation checks deliberately broke only verification copies: moving cleanup back inside the damage loop failed two strengthened callback cases; disabling OnValidate invalidation failed the authoring test. Both copies were restored before the final passing runs.
- Two new `UnityNative` cases, `RealMapContentCallbackRefreshesAircraftMemoryWithDisabledPresentation(4)` and `(1)`, compile in the managed runner but require Unity to execute. They drive the real instance callback on an inactive selection object, with no map/visual setup. These and the complete native suite remain unrun here.
- The raw reference compile still has the same 22 normalized baseline diagnostics and zero new diagnostics. It remains a limited compatibility check, not a Unity build.
