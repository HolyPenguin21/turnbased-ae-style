# Full file Save/Load integration

```sh
DOTNET=/path/to/dotnet NUGET_PACKAGES=/path/to/nuget \
  python Tools/match-save-verify/integration-run.py
```

Requires .NET 8, the cached UnityEngine.Modules 2021.3.33 / Unity UI references used by `Tools/ai-verify`, and the actual runtime source. `MATCH_INTEGRATION_WORK` optionally retains the disposable compile tree at a chosen path.

The runner compiles the entire current runtime, including the concrete world, AI, historical presentation and strict JSON codecs. It executes production `MatchSaveCodec.Capture`, `MatchSaveJson`, `MatchSaveStore`, `AtomicJsonFile`, `Decode`, `GameSession.TryPrepareRestore`, the scene setup's actual early `Awake` reset, `TryRestorePreparedMatch`, `MatchRestoreCoordinator` and `ResumeGame`. No test restore source, entity/objective codec, JSON adapter, graph-validation substitute or direct registration-based substitute for restoration is used.

The fixture has a full logical one-cell map, three real players (one eliminated during the existing round), both hands/deck pool, frozen human loadout/quota, all participant economies, a registered airfield container with a spent aircraft, activation/reaction spending, visibility and an AI mobilization hold. Two fresh file loads follow live mutations. They verify replacement of canonical objects, issued-ID stability during decoding, exact turn order/clocks, preserved spending, no new-turn/deal notifications, restored AI continuation expiry, unchanged bytes and repeated rollback. Malformed files pass through the actual slot store and decoder, with session/registry/ID assertions before cleanup.

The runner's temporary native adapters are explicit:

- `IntegrationNative.New` allocates Unity component/asset holders without native constructors and initializes collection fields; `PlayerRoot`'s constructor-equivalent zero resource values use its actual economy import boundary. The `PlayerRoot.Create` temporary copy uses this allocator and omits renderer color assignment.
- Native Unity object null checks use CLR identity, including the source's same-config reference check. There are no native destroyed handles in the fixture. Cached reference DLL `InternalCall` flags are normalized by the existing `ReferenceAdapter`; their throw bodies are not implementations of Unity.
- The manifest `Resources.Load` lookup returns a fixture manifest with a fixed content fingerprint. The real key resolvers and fingerprint checks still execute.
- Native frame count is fixed. Text-field focus, final turn HUD binding and UI blocked-state recomputation are inactive. Gameplay save eligibility, turn capture/resumption and all state owners still execute.
- Renderer-only palette/color initializers use default colors while retaining the actual palette length. `HexMap.GetComponent` returns no optional native resource/event display. The airfield uses the real no-marker path; marker/content validation and marker creation are not replaced.
- The native `Vector3` constructor used by axial geometry is supplied by its exact three field assignments; geometry arithmetic and validation remain production code. The campaign store uses an empty temporary profile directory; real stale-context validation still runs.
- Existing package API declaration adapters and managed `Mathf` match the other portable verification runners.

Map `SetData`/`SetLayout` and real layout validation run, while mesh generation, native scene activation/Start ordering, input, UI dialogs, assets/renderers and coroutine scheduling do not. Historical appearance codec code is compiled but this portable fixture contains no remembered renderer trees. Native file roundtrips in `Assets/Editor/Tests/Core/MatchSaveIntegrationTests.cs` additionally cover captured/destroyed hidden buildings with old frozen flags, real meshes/markers, permanent profile deck changes and on-disk Library receipts; **those require Unity 6000.5.4f1 and are not executed by this runner**.

The final 24 scenarios also reject missing/wrong human perspective and overflowing derived geometry, and prove that stale campaign context preserves an intact primary and its valid backup instead of loading another match.
