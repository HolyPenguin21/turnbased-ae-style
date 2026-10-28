# In-memory match restoration integration verification

**Goal:** Exercise preparation as a connected restore transaction without implementing tactical files, slots or Save/Load UI.

**Source:** upstream master `06c63685d3a54c9e29fc8dafd31f41bd2cf42447`; available sources verified against GitHub blob hashes. Local `06d1ae500605bbad0db6456fc9acf113becf0e20` is a source-snapshot baseline, not an upstream commit.

**Approach:** Test-only detached `IMatchRestoreSource` objects drive real staging, owner restoration, coordinator and turn-resume APIs. Add a full AI aggregate roundtrip. Keep native Unity fixtures and portable execution claims separate. Engine adapters stay under Tools and must not implement the state/ordering assertions they are meant to check.

**Constraints:** Unity 6000.5.4f1; preserve existing GUIDs and gameplay rules. No tactical serializer or concrete production objective codec. Profile persistence remains independent. No publication requested for these new checks.

**Review focus:** Canonical identity after object replacement; repeated restoration must not refill/deal/learn hidden facts; destroyed historical entities remain unregistered; failure cleanup must run production reset boundaries; permanent Library receipts must survive tactical reset.

- [x] Managed logical transaction: real GameSession/restore coordinator/ResumeGame and world/hand owners; invalid input before reset; exact resources/AP/move/AA/activation/stealth and hand; failures at restoration phases.
- [x] Full AI aggregate: populate and roundtrip every persistent owner through canonical replacement objects, detached test objective payloads and exact clocks/history; test continuation rather than new planning.
- [x] Native integration fixture: actual map construction and scene restoration transaction, remembered appearances, repeated restore and Library replay. Bind it here; execution explicitly requires the project editor.
- [x] Runtime scene transaction helper: extract existing configure/restore/failure cleanup from Start so normal navigation stays in Start while the actual transaction can be tested without loading MainMenu.
- [x] Run relevant existing/new checks, syntax and differential binding; fresh review of tests/adapters and any fixes; document executed coverage and remaining native-only behavior.

Execution notes:
- Shell clone was unavailable; restored local source cache and fetched the upstream deltas with GitHub tools. All available source contents were verified against upstream hashes before changes. No remote changes made.

- Final execution: 11 logical restore cases, 1 full AI aggregate (22 populated owners), existing 8 AI + 9 policy/campaign, 22 world + 3 presentation, and 7 Python verifier tests passed. Policy/turn and 926-file syntax plus baseline/current/two-fixture binding passed with documented adapters.
- Review found no production bug. Repeated-success coverage gap was closed with fresh prepared-source factory and a genuinely executed CollectionService/store/atomic-write receipt scenario through two tactical rollback/replay cycles.
- Extracted the existing scene transaction and failure handler for direct invocation; no new save format, state owner, balance or UI feature. Native fixture has 6 cases and remains unexecuted without Unity 6000.5.4f1.
- New changes remain local and uncommitted; no remote mutation in this task.
