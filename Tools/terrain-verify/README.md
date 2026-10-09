Run the same focused terrain tests used by Unity's `TerrainComplexTests`, with a small managed
Unity API surface for the pure algorithms:

```sh
dotnet run --project Tools/terrain-verify/TerrainVerify.csproj
```

Requires .NET 8 and restores NUnit 3.14. The runner compiles the **actual project sources** for
pathfinding, map terrain data, template validation, animation timing, movement's first-step gate
and the ground route cache. It also compiles the actual HexMapGenerator and hex mesh builder:
the rotation test generates sixty maps and checks terrain cells, per-part texture references,
and mesh UVs together. It does not copy these implementations into the harness.

The substitutes in `UnityStubs.cs` retain mesh arrays and provide deterministic random draws
and empty content registries for these checks. They are only data/geometry APIs and collaborators. They do not
prove URP rendering, Unity lifecycle/coroutine scheduling, fog, scene wiring, aviation endurance,
AI execution, resource bank transactions, or BattleEngine behavior. The retreat and scout estimator
tests are compiled/run only by the real Unity EditMode suite. Run that suite and the PlayMode cases
in `Docs/terrain-complexes/implementation.md` before accepting this feature.

