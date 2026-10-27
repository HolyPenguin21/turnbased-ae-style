# Collection reward verification

Run the real `CollectionService`, `CollectionProfileStore`, `CollectionProfile`, `RewardService`,
`AtomicJsonFile` and `DeckRules` against temporary disk profiles with .NET 8:

```bash
DOTNET_BIN=/path/to/dotnet python3 Tools/collection-verify/run.py
```

NUnit 3.14.0 must be cached at `~/.nuget/packages/nunit/3.14.0/lib/netstandard2.0/nunit.framework.dll`,
or supplied through `NUNIT_DLL`. The compiler output stays outside the repository in
`$COLLECTION_VERIFY_WORK` (default `/tmp/collection-verify`). No package restore is performed.

The harness runs `Assets/Editor/Tests/Cards/LibraryRewardReceiptTests.cs` with real atomic
profile writes, reads, backup recovery, migration, reward selection and receipt replay.
`Adapters.cs` supplies only Unity objects/logging and minimal card/catalog/player dependency
surfaces for these transaction tests; it does not replace receipt or collection logic.
The JSON adapter is shared with `Tools/campaign-verify/PortableJson.cs`.

This is not a full project compile. Native Unity serialization, actual catalog resolution,
scene objects, the hand/UI and the `UnityScene` replay case in `LibraryEventTests.cs`
still require the project's Unity editor and full EditMode tests.
