# Detached human presentation codec verification

```sh
DOTNET=/path/to/dotnet python Tools/save-preparation-verify/presentation-codec/run.py
```

Runs 17 focused assertions against production `MatchPresentationData`, `MatchPresentationCodec`, `HumanVisualMemory`, `VisionSystem`, Army/Unit owners and identity validation. Covers a historical roster after its live army was captured and emptied behind fog, mutable observed stats, display-only identities, aviation facts, frozen movement, absent costs/optional strings across a JSON roundtrip, legitimate visibility outside playable cells, exact missing/duplicate remembered-appearance coverage, validation without knowledge mutation unissued historical IDs, and capture callbacks retaining a neutral player referenced only by an old display snapshot (plus its viewer and captured-from owner).

Reuses the existing integration runner's source list and explicit portable adapters. Unity object/asset APIs and frozen renderer encoding are outside this runner; it supplies an explicitly marked partial appearance bridge. JSON uses the existing System.Text.Json test adapter with Unity-style empty optional strings. This does **not** execute native JsonUtility or renderers.

`Assets/Editor/Tests/Map/MatchPresentationCodecTests.cs` additionally tests native JsonUtility and frozen hierarchy/flag-frame sprite, tint, sorting and pose. Those cases require Unity 6000.5.4f1 and the complete authored binary assets; the source cache alone cannot execute them.
