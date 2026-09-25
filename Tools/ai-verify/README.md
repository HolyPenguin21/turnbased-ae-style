# ai-verify — compile + test gate for the AI code without Unity

Cloud/CI containers have no Unity. These scripts give two differential gates:

1. **Compile** — all of `Assets/` against the real UnityEngine reference DLLs (NuGet
   `UnityEngine.Modules` 2021.3.33, `Unity3D.UnityEngine.UI` 2020.3.21) plus `EngineStubs.cs`
   (TMPro, InputSystem, UnityEditor). ~30 errors remain in UI/editor/camera code (missing stub
   members) and none in `Scripts/Ai`; the gate fails only on errors that are *not* in the baseline.
2. **Tests** — the EditMode tests (`Assets/Editor`) built as a net472 NUnitLite exe and run under
   Mono (CoreCLR refuses the Unity DLLs' InternalCall methods). `TestRunStubs.cs` shadows
   `UnityEngine.Mathf` with a managed copy. About 350 of ~520 tests run (most AI lanes); the rest need
   the native engine. The gate: nothing that passes at the base revision may stop passing.

```bash
Tools/ai-verify/setup.sh                    # once per container (apt: dotnet-sdk-8.0, mono)
Tools/ai-verify/compile_check.sh --baseline # once, records HEAD's error set
Tools/ai-verify/compile_check.sh            # after every change: exit 1 on a new error
Tools/ai-verify/test_regress.sh             # after every change: exit 1 on a regression vs HEAD
Tools/ai-verify/test_regress.sh <rev>       # ...or vs another revision
```

Work files go to `$AI_VERIFY_WORK` (default `$TMPDIR/ai-verify`). Nothing here is imported by Unity
(`Tools/` is outside `Assets/`), and the .NET 4.7.2 API patches are applied only to a copy.
Limits, know them before trusting a green run:
- A declaration-level error hides method-body errors of the same build (Roslyn stops binding bodies),
  so fix the first reported error and re-run.
- The test gate only guards behaviour the runnable tests cover. Example: changing
  `ContinuationWinChanceFloor` 0.40 -> 0.99 passes it; flipping `IsNeutralRaidTarget` fails 13 tests.
  For logic without coverage, add a focused EditMode test (it runs here too) before refactoring it.
- Still run the full EditMode suite in Unity before merging: the ~170 engine-bound tests only run there.
