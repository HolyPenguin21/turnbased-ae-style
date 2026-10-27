# Match save preparation audit fixes

**Goal:** Close the three confirmed preparation defects and the verification baseline defect from the independent audit of PR #244.

**Architecture:** Preserve existing authoritative owners. Expose delayed participant references; rebuild derived map presentation after world/knowledge import. Do not introduce disk saves, entity codecs, gameplay notifications, income or turn-start effects.

**Constraints:** Unity 6000.5.4f1; preserve asset GUIDs and scenes; no dependency or balance changes. Work on `codex/save-preparation-audit-fixes`, based on `7e087557dd25a5821521d627430f7467e4385cf0`. Implementation initially stopped before publication; the user subsequently explicitly authorized commit, merge into master and push.

**Review focus:** Destroyed participants must remain unregistered; event markers must obey personal discovery and consumption; repeated rebuilds must not duplicate markers; resource rebuild must include bonus-only yields; committed test selection must use an explicit immutable baseline.

- [x] Delayed battles: add a regression retaining an unregistered participant via `CaptureState(Action<ArmyData> includeReferencedArmy = null)`; run RED, add callback, run world suite GREEN.
- [x] Map presentation: add regression coverage for skipped/consumed/undiscovered events, repeated rebuild and bonus-only resources. Add silent `MapEventDisplay.RefreshAll()`, reuse normal marker creation, and invoke both map displays from the restore coordinator after world/knowledge. Run focused managed checks and bind native EditMode tests; native execution remains unavailable.
- [x] Verification: require `--base` for binding; archive that revision and select committed plus working/untracked changed tests relative to it. Regression-test clean committed changes, dirty/untracked additions and invalid/missing baselines. Update documented command.
- [x] Integration: review all diffs/callers, run appropriate managed suites, syntax and baseline/current/fixture binding, check metadata and diff whitespace. Record native Unity limits.

Execution ledger:
- Initial baseline: clean checkout; prior independent audit reproduced 130 passing managed checks, source binding and all three defects. Implementation authorization: user requested all reported fixes.

- Delayed participant regression: missing callback API RED, then 22 world checks GREEN. Removed participant shells retain identity and queue order without becoming registered armies.
- Verification regressions: 7 Python tests GREEN (real temporary Git histories, immutable archive, committed/working/untracked fixtures, required baseline and NUnit).
- Map presentation: three original-failure checks RED, then 3/3 GREEN with production registries, displays and coordinator. Native fixture supplies three corresponding EditMode cases; binding passed, execution deferred to Unity.
- Integration: 134 managed checks plus 7 Python verifier tests GREEN; policy/turn checks and 923-file syntax GREEN; explicit 7e087557 baseline/current and two fixture bindings GREEN with old-reference adapters. New fixture metadata validates and has a unique GUID; no existing scene/asset/meta changes; diff whitespace clean.
- Fresh independent whole-change review: no actionable findings. Unity 6000.5.4f1 native compilation, full EditMode and real scene/renderer execution remain unavailable. The user subsequently requested publication after these checks; publishing the verified tree is authorized.
