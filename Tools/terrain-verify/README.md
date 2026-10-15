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

It also runs `MatchResourcePlacementTests` against the actual resource planner and bonus
registry: counts and spacing across map sizes/player counts/seeds, central Tech attachment,
unreachable/existing-bonus exclusions, cramped-map handling and positional diversity. The
resource-pass integration test needs the real Unity `CitadelSetupController` and runs only in
the full EditMode suite, not this managed harness.

The substitutes in `UnityStubs.cs` retain mesh arrays and provide deterministic random draws
and empty content registries for these checks. They are only data/geometry APIs and collaborators. They do not
prove URP rendering, Unity lifecycle/coroutine scheduling, fog, scene wiring, aviation endurance,
AI execution, resource bank transactions, or BattleEngine behavior. The retreat and scout estimator
tests are compiled/run only by the real Unity EditMode suite. Run that suite and the PlayMode cases
in `Docs/terrain-complexes/implementation.md` before accepting this feature.


Regional resource and neutral coverage now also compiles the pure
`CitadelSetupController.NeutralPlacement.cs` partial of the existing content owner.
The tests exercise type-aware access scoring, actual-count quotas, joint near/far
feasibility and compare neutral coverage/spacing/start imbalance to the previous
random sequential placement at equal counts.

To sample the **authored GameConfig terrain parameters**, additionally run:

```sh
python Tools/terrain-verify/sample_authored.py --seeds 4
```

Requires Python/PyYAML and .NET 8 (`DOTNET_BIN` may point to its executable).
This reads the actual YAML, generates a temporary value-only fixture and calls the
real `AssignTerrainTypes`, `PlaceComplexes` and resource planner for both biomes,
radii 5–8, 2–4 players, every resource profile/richness, edge and inward starts.
Texture references are placeholders retaining validity/frame counts; pixels, mesh
rendering, native Unity randomness and scene lifecycle are not validated. No
fixture, generated C# or alternate generation algorithm is imported into Unity.

The audit also exercises actual start selection without copying its rules into a
second authored algorithm. `--actual-starts` extracts the current setup method
bodies into a disposable probe, replaces only the highlight callback with a no-op,
and samples one valid neighboring human choice. The production campaign path is:

```sh
python Tools/terrain-verify/sample_authored.py --actual-starts --players 2 --seeds 8
python Tools/terrain-verify/sample_authored.py --actual-starts --ordinary --players 3 --seeds 8
```

`--ordinary` uses the shared ordinary budget without regional inputs. Sampling
ordinary Small four-player starts can FAIL the full-budget assertion for a proven
geometric reason: in the recorded Arid seed-2 witness only three nonadjacent far
hexes exist for a target of five, even before near placement. See
`Docs/CampaignRegionConditions_Audit.md`; the planner's legal partial fallback is
covered separately. A full target for every allowed start cannot be asserted under
the unchanged start and far-distance rules. The sampler never labels that shortfall
a complete plan. Its generated binary runs from a private temporary directory.

The bonus-registry managed check verifies publication after a committed write and
silence during sampling/reset. Fog filtering and actual AI-memory refresh have a
separate Unity-only regression; the collaborator stub does not prove those effects.
