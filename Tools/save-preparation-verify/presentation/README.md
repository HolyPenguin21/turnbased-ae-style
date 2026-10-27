# Focused map presentation restore checks

Run from the repository root with Python 3 and a .NET 8+ SDK:

```sh
DOTNET=/path/to/dotnet python Tools/save-preparation-verify/presentation/run.py
```

The runner also accepts `dotnet` on `PATH` or `DOTNET_ROOT`. It invokes the SDK compiler directly, does not restore packages, and writes removable compiler outputs into a temporary directory. There are no machine-specific paths or prebuilt assemblies in this folder. Any compilation error or failed assertion returns a nonzero exit code.

Three checks execute the production `MapEventDisplay`, `MapResourceDisplay`, event/bonus registries, `MatchRestoreCoordinator` and `MatchRestoreScope`:

- Silent event rebuild restores markers, keeps repeated calls idempotent and respects the current viewer's personal discovery, including AI-only discovery hidden from a human. It emits no discovery/skip/consume events.
- Consumed, unskipped and missing entries remove old markers and hide them before deferred destruction.
- The real coordinator rebuilds both display owners inside its quiet scope before resuming: a terrain yield of 1 with restored bonus 3 displays 4, a bonus-only hex gets its previously absent icon, and the skipped event has a marker.

`PortableAdapters.cs` substitutes only native Unity rendering/object APIs and unrelated scene/entity/turn owners. Visibility is fixed to an explored map so the event test isolates personal discovery. Native icon writes retain observable amounts, while reconciliation and yield calculation remain production code. The source used by the coordinator is an in-memory test source, not a concrete file codec.

These are managed ownership/ordering regressions, **not a Unity build or renderer/scene execution**. `Assets/Editor/Tests/Map/MapPresentationRestoreTests.cs` supplies corresponding tests with real Unity objects, cleanup and private-cache observation. Run that fixture and the full EditMode suite in the project's Unity version to validate native lifecycle/rendering.
