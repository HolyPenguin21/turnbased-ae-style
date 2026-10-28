# In-memory match restoration verification

Checked against upstream master source `06c63685d3a54c9e29fc8dafd31f41bd2cf42447`. No tactical files, slots, Save/Load UI or production entity/objective codecs are added.

## Executed checks

| Check | Result | Actual code exercised |
| --- | --- | --- |
| Logical match transaction | 11 passed | GameSession staging, coordinator, scene restore transaction/failure handler, inventory reset, ResumeGame, hand/world/knowledge owners, remembered appearance import/coverage and display reconciliation |
| AI aggregate | 1 passed with all 22 owners populated | Capture, owner clear, canonical player/army/card/unit replacement, restore and equivalent next continuation gates/history updates |
| Existing AI and policy/campaign cases | 17 passed | 8 AI and 9 policy/campaign cases |
| Existing world and presentation cases | 25 passed | 22 world and 3 presentation cases |
| Verifier regressions | 7 passed | Baseline selection, committed/untracked fixtures, dependency guard and archive |

The logical transaction preserves identity, ordered hands/draw pools, overflow, card age/attachments, economy/AP/initiative contributions, departed-unit activation references, spent movement, transport/carrier links, AA/stealth windows, event facts and historical knowledge. Forbidden opening/new-turn/refill entrypoints in the portable layer throw.

Errors after partial world, knowledge, presentation and actual ResumeGame validation invoke the real failure handler and MatchStateInventory.EndMatch. Assertions precede test teardown. Invalid input before reset, missing frozen visuals, replaced staged requests and existing setup errors are checked too. Successful restoration repeats from the same saved values using fresh prepared sources after live state mutations.

Library replay executes real CollectionService, DeckRules, CollectionProfileStore and AtomicJsonFile with a temporary permanent-profile directory. Two tactical restore/reward cycles remove the old hand award, return the original card once per timeline and keep permanent ownership/receipt at one. A newly constructed service rereads the same receipt from disk. Profile state never comes from the tactical snapshot.

The in-memory source is test-only. The AI objective test codec covers Scout/Economy/Development and rejects unsupported tags; it is not a production six-objective file codec. The AI comparison covers continuation gates/history, not an entire AI turn simulation.

## Runtime adjustment

CitadelSetupController.TryRestorePreparedMatch contains the existing configure/restore/error-cleanup transaction, allowing the same error path to run without loading MainMenu. Start retains input activation, temporary UI/controller removal, navigation and the outer catch for presentation-finalization errors. State ownership, gameplay balance and scene assets are unchanged.

## Native limits

Six cases in Assets/Editor/Tests/Core/MatchRestoreIntegrationTests.cs use actual Unity objects, explicit production Awake, BuildFromLayout, the restore transaction, frozen renderer clones and profile serialization. They include repeated rollback and failures. **Those six native cases were not executed here.** They require Unity 6000.5.4f1.

The managed runner adapts native object/rendering behavior, map authoring/configuration, UI, non-persistence turn-controller methods and unrelated AI/diagnostic owners. Its inventory table is real; native destruction/coroutines and full scene activation/Start ordering are unverified. Frozen appearances are test values there, not native sprites/materials/poses. Profile JSON uses existing PortableJson; native JsonUtility and Library popup/event UI flow are unexecuted.

All 926 Assets C# files parse. Baseline/current runtime and both new fixture files bind with cached Unity 2021/package adapters. NativeEditorAdapters adds compile-only Editor/Test Framework declarations whose bodies throw; this is not native Unity compilation/execution. Fresh review found no production defect; its repeated-success coverage gap was closed by the repeated-payload and Library cases.

## Commands

```sh
export DOTNET=/path/to/dotnet
export DOTNET_BIN="$DOTNET"
export NUGET_PACKAGES=/path/to/nuget/cache
python Tools/save-preparation-verify/integration/run.py
python Tools/save-preparation-verify/ai/run.py
python Tools/save-preparation-verify/world/run.py
python Tools/save-preparation-verify/presentation/run.py
python -m unittest discover -s Tools/save-preparation-verify -p 'test_*.py'
python Tools/save-preparation-verify/run.py --binding --base 06c63685d3a54c9e29fc8dafd31f41bd2cf42447 --packages "$NUGET_PACKAGES"
```

The upstream baseline command targets a normal checkout. Here the shell clone was unavailable, so a source cache was verified against GitHub blob hashes. Local source-snapshot baseline 06d1ae500605bbad0db6456fc9acf113becf0e20 was used for differential binding and maps to that upstream source; it is not a public commit.
