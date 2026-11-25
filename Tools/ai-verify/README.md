# ai-verify — compile + test gate for the AI code without Unity

Two differential gates, in one of two reference modes.

1. **Compile** — all of `Assets/` against the Unity API.
2. **Tests** — the EditMode tests (`Assets/Editor`) built as a net472 NUnitLite exe and run under
   Mono (CoreCLR refuses the Unity assemblies' InternalCall methods). The gate: nothing that passes
   at the base revision may stop passing.

## Modes

| | `unity` (preferred) | `stub` (no Unity installed) |
|---|---|---|
| Select | `AI_VERIFY_UNITY=<Editor/Data of the project's version>` | leave `AI_VERIFY_UNITY` unset |
| UnityEngine/UnityEditor | the editor's real `Managed/UnityEngine/*.dll` | NuGet `UnityEngine.Modules` 2021.3.33 — publicized reference DLLs whose bodies throw |
| uGUI, TextMeshPro, InputSystem | the project's `Library/ScriptAssemblies` (Unity must have imported the project once) | `EngineStubs.cs` |
| Mathf | real | managed copy in `TestRunStubs.cs` |
| Mono | the editor's `MonoBleedingEdge` | `mono` on PATH (`setup.sh`) |
| Compile errors at HEAD | 0 — the real API, every file | ~30 permanent ones in UI/editor code |
| Tests runnable (2026-10-15) | ~2280 of 2811, the whole suite runs | ~1680; several UI/visual fixtures crash Mono and must be excluded |

In unity mode a passing compile is a real compile against the project's Unity version (the
project's own package versions included); only Burst/IL post-processing and asmdef boundaries are
not reproduced. What still fails there needs the native engine (`GameObject`, `ScriptableObject.CreateInstance`,
`JsonUtility`, asset loading) and only runs inside Unity.

```bash
# Windows (Git Bash), the project's editor installed:
export AI_VERIFY_UNITY=D:/Unity/Editor/6000.5.4f1/Editor/Data
export AI_VERIFY_WORK=D:/aiv-work/tool          # ASCII path: Mono fails on non-ASCII paths
Tools/ai-verify/compile_check.sh --baseline     # once per base revision
Tools/ai-verify/compile_check.sh                # after every change: exit 1 on a new error
Tools/ai-verify/test_regress.sh [base-rev]      # after every change: exit 1 on a regression (default HEAD; see Test scopes)

# Linux container without Unity:
Tools/ai-verify/setup.sh                        # once (apt: dotnet-sdk-8.0, mono)
Tools/ai-verify/compile_check.sh --baseline && Tools/ai-verify/compile_check.sh
Tools/ai-verify/test_regress.sh
```

Work files go to `$AI_VERIFY_WORK` (default `$TMPDIR/ai-verify`); baselines are kept per mode.

## Test scopes

| Scope | Command | Runs | Time (unity mode, eaee2e98) |
|---|---|---|---|
| fast (default) | `test_regress.sh [base-rev]`, `run_tests.sh <out.xml> [rev]` | everything except `[Category("Extended")]` and `engine_bound.txt` | ~100 s of tests; `test_regress.sh` ~2 min (base + working tree in parallel), ~1.5 min with a cached base |
| all | `test_regress.sh --all [base-rev]`, `run_tests.sh <out.xml> [rev] --all` | every test | ~19 min (1130 s of tests, 944 s of them `GridVsForecastCalibration.Probe`) |
| scoped | `run_tests.sh <out.xml> "" --where "class =~ 'Carrier\|Air'"` | the fast set narrowed by the `--where` | seconds |

- **fast** skips what the routine Unity run skips (`Extended`: calibration probes and generators —
  `GridVsForecastCalibration.Probe` alone is ~15 min here) and the tests that cannot pass outside Unity.
  It is the gate after every change.
- **all** is for rebuilding the list and for a final check of the tool itself; it reports the
  ~950 engine-bound failures again.
- `test_regress.sh` runs the base and the working tree in the same scope, in parallel when the base
  is not cached (`tests_base_<mode>_<fast-<list hash>|all>_<sha>.xml`). Regression = passed at the
  base and not now. It also lists tests failing on both sides whose message changed (`~` lines):
  not a verdict, but read them — a failure that moved is often a new bug behind an old one.
- Each revision builds in its own folder (`tests-<mode>-rev-<sha>`, `tests-<mode>-wt`), so a base
  run, a working-tree run and a manual run of another revision do not overwrite each other. Two
  simultaneous runs of the *same* revision (or two of the working tree) still share a folder.

### engine_bound.txt

The full names of tests that fail outside Unity because they need the native engine
(`MissingMethodException` / `TypeInitializationException` in the message). Generated, not edited:

```bash
Tools/ai-verify/run_tests.sh "$AI_VERIFY_WORK/full.xml" <base-rev> --all
python Tools/ai-verify/gen_engine_bound.py "$AI_VERIFY_WORK/full.xml"
```

The generator also prints the failures it did NOT list (assertion failures, NRE, ratchets): those
are candidates for real errors and stay in the fast run. Rebuild the list from the base revision
when an engine-bound test starts passing (it shows up only in `--all`) or a new engine-bound test
appears (it shows up as a failure on both sides in the fast run). A changed list changes the hash in
the cached base name, so the base re-runs on its own.

ai-verify is a fast filter. The final check before merging stays the full EditMode suite in Unity
(see "Unity runs").
Nothing here is imported by Unity (`Tools/` is outside `Assets/`), and the .NET 4.7.2 API patches
are applied only to a copy. Verification adapters never go into game code.

Limits, know them before trusting a green run:
- A declaration-level error hides method-body errors of the same build (Roslyn stops binding bodies),
  so fix the first reported error and re-run.
- The test gate only guards behaviour the runnable tests cover. Example: changing
  `ContinuationWinChanceFloor` 0.40 -> 0.99 passes it; flipping `IsNeutralRaidTarget` fails 13 tests.
  For logic without coverage, add a focused EditMode test (it runs here too) before refactoring it.
- Still run the full EditMode suite in Unity before merging: the engine-bound tests only run there.

## Unity runs

Routine check after a task — the full EditMode suite without the `Extended` category
(fixtures that enter Play Mode per test, thousand-seed generators, whole-campaign runs; on
2026-10-30 they took 50 of ~53 minutes, most of it UI Play Mode fixtures since removed):

```bash
Unity.exe -batchmode -projectPath <checkout> -runTests -testPlatform EditMode \
  -testCategory "!Extended" -testResults <out.xml> -logFile <out.log>
```

Run the full suite (no `-testCategory`) before merging into master or after engine/presentation
changes. A batch run needs a checkout the open editor does not hold, e.g. a `git worktree`.
