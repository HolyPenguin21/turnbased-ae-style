# Focused world-state checks

From the repository root:

```bash
python3 Tools/save-preparation-verify/world/run.py
# Just the original 14 world-state checks + 3 runtime identity checks:
python3 Tools/save-preparation-verify/world/run.py --original-17
```

Requirements: Python 3, a .NET 8+ SDK and NUnit 3.14.0. The runner finds `dotnet` on `PATH`, or accepts `DOTNET` (the executable) / `DOTNET_ROOT` (the SDK directory). It finds NUnit at `$NUGET_PACKAGES/nunit/3.14.0/lib/netstandard2.0/nunit.framework.dll`, defaulting to the user's `.nuget/packages` cache. `NUNIT_FRAMEWORK_DLL` may point directly to an existing framework DLL. If absent, restore NUnit 3.14.0 with the usual NuGet tooling before running; this runner does not download dependencies.

`run.py` resolves the repository relative to itself, invokes the SDK's Roslyn compiler directly, compiles the actual listed production sources and EditMode test fixtures, and runs their NUnit assertions. It writes its assembly, response file and runtime configuration into a temporary directory and removes them after the run. There are no absolute workspace paths or prebuilt assemblies in this folder.

The default suite adds four registry boundary checks: resource bonus copies on capture/restore, silent replacement, detached event collections/reward values, and atomic rejection of duplicate hexes or codec failure. The generic event payload used in these tests is deliberately not a full event serialization format. A future codec must encode authored event/card keys, player IDs and all selected runtime outcomes; restore must never reroll placement/rewards.

`PortableAdapters.cs` supplies only dependencies outside this focused compile: native visuals/GameObjects, authored card catalogs, equipment effects, AI statistics, combat challenges, carrier actions and initiative rules. The registry owners, fog and stealth state, movement/activation data, identity allocation and restore methods are the production implementations. The carrier creation adapter exposes a side effect so a scope-registration regression can be detected. Reflection in `Program.cs` only discovers/invokes test methods; it never snapshots private production state.

This suite is a managed contract check, **not a Unity build or full gameplay test**. Native scene behavior, actual carrier actions, visuals, editor serialization and the full EditMode suite still need the project's Unity version. Keep adapters under `Tools/`; do not add them to `Assets/`.

Package 1 adds the real synchronous notification-batch partial to this compile. Its existing
preparation cases do not execute gameplay roster commands; the corresponding explicit
`NotSupportedException` adapters fail on unexpected use. Actual command contracts are tested
by `WorldMutationContractTests` and native integration by `WorldMutationUnityTests`. This
runner also reports unexpected notification exceptions instead of hiding them.
