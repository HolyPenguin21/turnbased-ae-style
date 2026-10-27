# AI persistence preparation checks

Run with a .NET 8 SDK and the package cache used by `Tools/ai-verify`:

```bash
DOTNET=/path/to/dotnet NUGET_PACKAGES=/path/to/packages \
  python Tools/save-preparation-verify/ai/run.py
```

The runner compiles **all current runtime sources** and executes nine engine-independent core policy/campaign cases and the eight named NUnit tests in
`Assets/Editor/Tests/AI/AiPersistencePreparationTests.cs`. They exercise actual owner APIs, including
stale knowledge and owner rebinding, historical identity reservation, private continuity evidence,
public initiative history, cooldowns, and cross-turn clocks. Missing/mismatched player resolvers and
noncanonical capture IDs must fail before the tested owner is replaced.

This is managed preparation verification, not native Unity execution. Disposable copies use:

- the same cached Unity 2021 API/package compatibility adapters as the broader binding gate;
- managed `Mathf` from `Tools/ai-verify/TestRunStubs.cs`, with `Log10` added to the temporary copy;
- `ReferenceAdapter.cs` to remove `InternalCall` only where cached references already contain a
  nonzero managed method body (CoreCLR otherwise refuses to load those types);
- CLR null identity for the fixture's literal-null map argument in the copied `EnsureSubscribed`.

The original package cache and game sources are not rewritten. The adapters provide no native
engine implementation. Run the project-version Unity EditMode suite and loaded-scene scenarios
before integrating a future disk save/load pipeline. The mandatory six-kind mission objective codec
is a typed preparation boundary; the test codec covers Scout only and is not a disk serializer.
