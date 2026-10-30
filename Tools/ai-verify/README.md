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
Tools/ai-verify/test_regress.sh [base-rev]      # after every change: exit 1 on a regression (default HEAD)

# Linux container without Unity:
Tools/ai-verify/setup.sh                        # once (apt: dotnet-sdk-8.0, mono)
Tools/ai-verify/compile_check.sh --baseline && Tools/ai-verify/compile_check.sh
Tools/ai-verify/test_regress.sh
```

Work files go to `$AI_VERIFY_WORK` (default `$TMPDIR/ai-verify`); baselines are kept per mode.
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
