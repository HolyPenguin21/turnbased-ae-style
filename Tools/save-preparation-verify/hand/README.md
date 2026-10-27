# Portable hand model checks

From the repository root, with a .NET 8 SDK and NUnit 3.14.0 available:

```bash
DOTNET=/path/to/dotnet python3 Tools/save-preparation-verify/hand/run.py
```

`DOTNET` is optional when `dotnet` is on `PATH`. NUnit is read from
`${NUGET_PACKAGES:-$HOME/.nuget/packages}/nunit/3.14.0/lib/netstandard2.0/nunit.framework.dll`;
set `NUNIT_DLL` to use an existing copy elsewhere. The runner invokes the SDK's Roslyn
`csc.dll` directly, without MSBuild or package restore. Build output goes to a fresh
scratch directory, or to `SAVE_HAND_VERIFY_WORK` when set.

The runner compiles the current production `CardData`, `PlayerHandData`, `CardState`,
`DeckDraw`, `MatchLoadout`/`BlueprintQuota`, and `AiHandData`/registry sources, plus the
existing `PlayerHandStateTests` and `AiHandCapacityParityTests` fixtures. It runs their
10 plain NUnit tests. Nonzero compiler or test results fail the command.

`EngineCatalogAdapters.cs` exists only under `Tools`. It supplies the minimal engine
and dependency DTO surfaces this slice needs. Untested catalog, production, deployment,
opening hand selection and random operations throw explicitly. No production sources
are rewritten or copied into the adapter.

These checks cover hand capacity/overflow, ordering and notifications, snapshot identity,
attachments, acquired turn, exact remaining deck copies, blueprint reservation state and
restored AI registry ownership. They do **not** verify authored catalog/deck validation,
opening deals, Unity UI binding, or Unity RNG. Run the full EditMode suite and the two
`MatchLoadoutStateTests` in the project's Unity version for those engine-bound checks.
