# Campaign verification

Native acceptance: Unity 6000.5.4f1, EditMode suite (`CampaignSystemTests`,
`CampaignCollectionIntegrationTests`, existing collection/combat/AI tests), then the
manual campaign lifecycle and UI checks in `Docs/PlanetaryCampaign_Implementation.md`.

`Tools > Campaign > Compare standard decks (1000 fast results)` in Unity prints
scores and sampled calculator frequencies. It does not simulate full AI V2 matches.

Cloud backend verification (requires .NET 8, Python/PyYAML and NuGet access):

```sh
python Tools/campaign-verify/run.py
# Optional custom executable/work directory:
DOTNET_BIN=/path/to/dotnet CAMPAIGN_VERIFY_WORK=/tmp/campaign-verify python Tools/campaign-verify/run.py
```

Runs the same campaign backend C# and `CampaignSystemTests` in a disposable source
copy. Unity reference DLLs have non-executable vector/native JSON bodies; ONLY that
copy substitutes a managed value vector and fields-only JSON. The JSON adapter
models Unity's empty inline `CampaignOperation` and null-string representation by
default; `CAMPAIGN_JSON_INLINE_NULLS=0` also checks a null-preserving representation.
This is a targeted regression model, not native serialization coverage. Old reference API
and Mathf stubs are also confined to this tool. No adapter is imported into Unity.
This verifies rules, reproducibility, file transitions and logical transactions;
it does **not** prove native JsonUtility, scene rendering, input, tactical matches,
reward UI or Unity asset-backed integration tests. File.Replace/flush are real disk
operations. The runtime card experiment reads authored YAML without losing Unity's
packed enum arrays. No player profile is read or modified.

Use `Tools/ai-verify/compile_check.sh --baseline <base>` before changes and the
working-tree compile gate afterward when the standard verification environment is
available. For hand-edited scene/settings YAML run both `unity-yaml-verify` scripts.
